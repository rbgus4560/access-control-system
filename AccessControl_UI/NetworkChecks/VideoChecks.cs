// 파일 역할: 실제 루프백 통신과 메시지 검증의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AccessControl.Network;

public static class VideoChecks
{
    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new Exception(message);
    }

    private static int Port()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int p = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return p;
    }

    private static async Task Until(Func<bool> predicate, string message, int ms = 7000)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate() && clock.ElapsedMilliseconds < ms)
            await Task.Delay(20);
        Check(predicate(), message);
    }

    private static void Reject(VideoPacket packet)
    {
        try
        {
            VideoProtocol.Validate(packet);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new Exception("Malformed video accepted");
    }

    public static async Task Run()
    {
        // Marker bytes exercise transport only, not a JPEG decoder or WPF renderer.
        byte[] jpeg = { 255, 216, 1, 2, 255, 217 };
        var packet = new VideoPacket
        {
            Session = Guid.NewGuid(),
            Sequence = 1,
            SentAt = DateTimeOffset.UtcNow,
            Jpeg = jpeg,
            AgeMs = 10
        };
        using (var memory = new MemoryStream())
        {
            await VideoProtocol.WriteAsync(memory, packet, default);
            memory.Position = 0;
            var decoded = await VideoProtocol.ReadAsync(memory, default);
            Check(
                decoded.Jpeg!.SequenceEqual(jpeg) && decoded.Session == packet.Session,
                "Video roundtrip failed"
            );
        }

        Reject(packet with { Jpeg = new byte[VideoProtocol.MaxJpegBytes + 1] });
        Reject(packet with { Jpeg = new byte[] { 1, 2, 3, 4 } });
        Reject(packet with { Version = 9 });
        Reject(packet with { AgeMs = double.NaN });
        Reject(packet with { CameraCode = "CAM-02" });
        Reject(packet with { SentAt = default });
        Reject(packet with { Session = Guid.Empty });
        Check(
            !VideoProtocol.IsFresh(packet with { SentAt = DateTimeOffset.UtcNow.AddSeconds(-2) }),
            "Stale video accepted"
        );
        Check(
            !VideoProtocol.IsFresh(packet with { SentAt = DateTimeOffset.UtcNow.AddSeconds(2) }),
            "Future video accepted"
        );
        Check(!VideoProtocol.IsFresh(packet, 1600), "Held video did not expire");
        Check(!VideoProtocol.IsFresh(packet with { Jpeg = null }), "Empty video accepted");
        Check(VideoProtocol.VideoPort(5050) == 5051, "Video port mapping");
        bool badPort = false;
        try
        {
            VideoProtocol.VideoPort(65535);
        }
        catch (ArgumentException)
        {
            badPort = true;
        }

        Check(badPort, "Port overflow");
        foreach (int length in new[] { 0, -1, VideoProtocol.MaxPacketBytes + 1 })
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, length);
            using var memory = new MemoryStream(bytes);
            bool rejected = false;
            try
            {
                await VideoProtocol.ReadAsync(memory, default);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Check(rejected, "Unbounded allocation accepted");
        }

        var source = new VideoSource();
        Guid session = Guid.NewGuid();
        source.Publish(Convert.ToBase64String(jpeg), Stopwatch.GetTimestamp());
        Check(source.Get(session, 1).Jpeg is not null, "Source missing");
        source.Publish(
            Convert.ToBase64String(jpeg),
            Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2
        );
        Check(source.Get(session, 1).Jpeg is null, "Source replayed stale JPEG");
        source.Publish("invalid base64", Stopwatch.GetTimestamp());
        Check(source.Get(session, 1).Jpeg is null, "Invalid source");
        Console.WriteLine(
            "Video bounds / metadata / freshness / port mapping / source expiry: PASS"
        );
        await using var server = new VideoServer();
        await using var client = new VideoClient();
        int port = Port();
        server.Start(port);
        client.Start("127.0.0.1", port, source);
        source.Publish(Convert.ToBase64String(jpeg), Stopwatch.GetTimestamp());
        await Until(() => server.Latest is not null, "Video was not received");
        var oldSession = server.Latest!.Packet.Session;
        source.Clear();
        await Until(() => server.Latest is null, "Camera stop retained previous image");
        await server.StopAsync();
        server.Start(port);
        // Keep a live source fresh during automatic reconnect.
        using (var refresh = new CancellationTokenSource())
        {
            var publisher = Task.Run(async () =>
            {
                while (!refresh.IsCancellationRequested)
                {
                    source.Publish(Convert.ToBase64String(jpeg), Stopwatch.GetTimestamp());
                    try
                    {
                        await Task.Delay(100, refresh.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
            await Until(
                () => server.Latest is not null && server.Latest.Packet.Session != oldSession,
                "Video restart did not reconnect"
            );
            refresh.Cancel();
            await publisher;
        }

        await client.StopAsync();
        await Until(() => server.Latest is null, "Disconnected video retained image");
        await server.StopAsync();
        Console.WriteLine(
            "Video real TCP / camera stop clears / server restart reconnect / disconnect clears: PASS"
        );
        server.Start(port);
        using (var malformed = new TcpClient())
        {
            await malformed.ConnectAsync(IPAddress.Loopback, port);
            byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, VideoProtocol.MaxPacketBytes + 1);
            await malformed.GetStream().WriteAsync(length);
        }

        source.Publish(Convert.ToBase64String(jpeg), Stopwatch.GetTimestamp());
        client.Start("localhost", port, source);
        await Until(() => server.Latest is not null, "Malformed video killed accept loop");
        await client.StopAsync();
        using (var partial = new TcpClient())
        {
            await partial.ConnectAsync(IPAddress.Loopback, port);
            await partial.GetStream().WriteAsync(new byte[] { 0 });
            var clock = Stopwatch.StartNew();
            await server.StopAsync();
            Check(clock.ElapsedMilliseconds < 2000, "Partial frame blocked shutdown");
        }

        Console.WriteLine("Malformed video recovery / partial frame cancellation: PASS");
        // A failed video connection must not block authorization ACKs.
        await using var controlServer = new MonitorServer();
        await using var controlClient = new MonitorClient();
        int controlPort = Port();
        controlServer.Start(controlPort);
        int acks = 0;
        controlClient.Acknowledged += _ => Interlocked.Increment(ref acks);
        client.Start("localhost", port, source); // Video server deliberately offline.
        controlClient.Start("localhost", controlPort, () => new MonitorSnapshot());
        await Until(() => Volatile.Read(ref acks) >= 2, "Video failure blocked control channel");
        await controlClient.StopAsync();
        await controlServer.StopAsync();
        await client.StopAsync();
        Console.WriteLine("Video offline / independent control ACKs: PASS");
    }
}
