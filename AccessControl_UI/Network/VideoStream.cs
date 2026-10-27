// 파일 역할: JPEG 관제 영상 전송과 최신 프레임 보관을 담당합니다. 영상 수신 자체는 출입 허가를 만들지 않습니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AccessControl.Network;

// Independent from authorization. Video never grants access or writes an access event.
public sealed record VideoPacket
{
    public int Version { get; init; } = 1;
    public string ClientCode { get; init; } = "RECOGNITION-01";
    public string CameraCode { get; init; } = "CAM-01";
    public string CameraDisplayName { get; init; } = "";
    public Guid Session { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public double AgeMs { get; init; }
    public byte[]? Jpeg { get; init; }
}

public static class VideoProtocol
{
    public const int MaxJpegBytes = 400_000,
        MaxPacketBytes = 540_000;
    public const double MaxAgeMs = 1500;

    public static int VideoPort(int controlPort)
    {
        if (controlPort < 1 || controlPort > 65534)
            throw new ArgumentException(
                "TCP 포트는 1~65534로 입력해 주세요. 영상은 다음 포트를 사용합니다."
            );
        return controlPort + 1;
    }

    public static bool IsFresh(VideoPacket packet, double elapsedMs = 0)
    {
        double transit = (DateTimeOffset.UtcNow - packet.SentAt).TotalMilliseconds;
        return packet.Jpeg is not null
            && transit >= -500
            && transit <= MaxAgeMs
            && packet.AgeMs + Math.Max(Math.Max(0, transit), elapsedMs) <= MaxAgeMs;
    }

    public static void Validate(VideoPacket p)
    {
        if (
            p.CameraDisplayName is null || p.CameraDisplayName.Length > 60 || p.CameraDisplayName.Any(char.IsControl)
            || p.Version != 1
            || p.ClientCode != "RECOGNITION-01"
            || p.CameraCode != "CAM-01"
            || p.Session == Guid.Empty
            || p.Sequence < 1
            || p.SentAt == default
            || !double.IsFinite(p.AgeMs)
            || p.AgeMs < 0
            || p.AgeMs > MaxAgeMs
        )
            throw new InvalidDataException("잘못된 영상 메타데이터");
        if (
            p.Jpeg is { } jpeg
            && (
                jpeg.Length < 4
                || jpeg.Length > MaxJpegBytes
                || jpeg[0] != 0xff
                || jpeg[1] != 0xd8
                || jpeg[^2] != 0xff
                || jpeg[^1] != 0xd9
            )
        )
            throw new InvalidDataException("잘못된 JPEG 프레임");
    }

    public static async Task WriteAsync(Stream stream, VideoPacket packet, CancellationToken token)
    {
        Validate(packet);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (bytes.Length > MaxPacketBytes)
            throw new InvalidDataException("영상 패킷 크기 초과");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
    }

    public static async Task<VideoPacket> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 2 || length > MaxPacketBytes)
            throw new InvalidDataException("영상 패킷 크기 초과");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        var packet =
            JsonSerializer.Deserialize<VideoPacket>(bytes)
            ?? throw new InvalidDataException("빈 영상 패킷");
        Validate(packet);
        return packet;
    }
}

// Only the latest received JPEG is retained; a slow connection cannot build a frame queue.
public sealed class VideoSource
{
    public Func<string>? DisplayName { get; set; }
    private sealed record Frame(string Base64, long At);

    private Frame? frame;

    public void Publish(string? base64, long at)
    {
        Volatile.Write(
            ref frame,
            base64 is { Length: > 0 } && base64.Length <= 534_000 ? new Frame(base64, at) : null
        );
    }

    public VideoPacket Get(Guid session, long sequence)
    {
        var value = Volatile.Read(ref frame);
        double age = value is null
            ? double.PositiveInfinity
            : Stopwatch.GetElapsedTime(value.At).TotalMilliseconds;
        byte[]? jpeg = null;
        if (age >= 0 && age <= VideoProtocol.MaxAgeMs)
        {
            try
            {
                jpeg = Convert.FromBase64String(value!.Base64);
            }
            catch (FormatException) { }
        }

        var packet = new VideoPacket
        {
            CameraDisplayName = new string((DisplayName?.Invoke() ?? "").Where(c => !char.IsControl(c)).Take(60).ToArray()),
            Session = session,
            Sequence = sequence,
            SentAt = DateTimeOffset.UtcNow,
            AgeMs = jpeg is null ? 0 : age,
            Jpeg = jpeg
        };
        try
        {
            VideoProtocol.Validate(packet);
            return packet;
        }
        catch (InvalidDataException)
        {
            return packet with { Jpeg = null, AgeMs = 0 };
        }
    }

    public void Clear() => Volatile.Write(ref frame, null);
}

public sealed class VideoClient : IAsyncDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? run;

    public void Start(string host, int port, VideoSource source)
    {
        if (cancellation is not null)
            throw new InvalidOperationException("영상 연결이 이미 실행 중입니다.");
        var address = MonitorProtocol.LocalAddress(host);
        if (!MonitorProtocol.ValidPort(port))
            throw new ArgumentException("영상 포트를 확인하세요.");
        cancellation = new();
        var token = cancellation.Token;
        run = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var tcp = new TcpClient { NoDelay = true };
                        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            connect.CancelAfter(2000);
                            await tcp.ConnectAsync(address, port, connect.Token);
                        }

                        using var stream = await LinkSecurity.ConnectAsync(tcp, "video", token);
                        Guid session = Guid.NewGuid();
                        long sequence = 0;
                        while (!token.IsCancellationRequested)
                        {
                            long frameStarted = Stopwatch.GetTimestamp();
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                                token
                            );
                            timeout.CancelAfter(1000);
                            await VideoProtocol.WriteAsync(
                                stream,
                                source.Get(session, ++sequence),
                                timeout.Token
                            );
                            // 전송 시간을 포함해 약 30FPS로 제한합니다. 느린 연결에서는 최신 프레임만 보냅니다.
                            var remaining = TimeSpan.FromSeconds(1.0 / 30) - Stopwatch.GetElapsedTime(frameStarted);
                            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                        when (ex
                                is System.Security.Authentication.AuthenticationException
                                    or IOException
                                    or SocketException
                                    or OperationCanceledException
                                    or InvalidDataException
                        ) { }

                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            token
        );
    }

    public async Task StopAsync()
    {
        var cts = cancellation;
        if (cts is null)
            return;
        cts.Cancel();
        try
        {
            if (run is not null)
                await run;
        }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Dispose();
            cancellation = null;
            run = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed record ReceivedVideo(VideoPacket Packet, long At);

public sealed class VideoServer : IAsyncDisposable
{
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;
    private Task? run;
    private ReceivedVideo? latest;
    public ReceivedVideo? Latest => Volatile.Read(ref latest);
    public int BoundPort { get; private set; }

    public void Start(int port)
    {
        if (cancellation is not null)
            throw new InvalidOperationException("영상 서버가 이미 실행 중입니다.");
        if (!MonitorProtocol.ValidPort(port))
            throw new ArgumentException("영상 포트를 확인하세요.");
        var tcp = new TcpListener(LinkSecurity.BindAddress, port);
        tcp.Start(1);
        listener = tcp;
        BoundPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        cancellation = new();
        var token = cancellation.Token;
        run = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await tcp.AcceptTcpClientAsync(token);
                        client.NoDelay = true;
                        using var stream = await LinkSecurity.AcceptAsync(client, "video", token);
                        Guid? session = null;
                        long sequence = 0;
                        while (!token.IsCancellationRequested)
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                                token
                            );
                            timeout.CancelAfter(3000);
                            var packet = await VideoProtocol.ReadAsync(stream, timeout.Token);
                            session ??= packet.Session;
                            if (session != packet.Session || packet.Sequence != sequence + 1)
                                throw new InvalidDataException("영상 순번 오류");
                            sequence = packet.Sequence;
                            Volatile.Write(
                                ref latest,
                                VideoProtocol.IsFresh(packet)
                                    ? new ReceivedVideo(packet, Stopwatch.GetTimestamp())
                                    : null
                            );
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                        when (ex
                                is System.Security.Authentication.AuthenticationException
                                    or IOException
                                    or SocketException
                                    or OperationCanceledException
                                    or JsonException
                                    or InvalidDataException
                        ) { }
                    finally
                    {
                        Volatile.Write(ref latest, null);
                    }
                }
            },
            token
        );
    }

    public async Task StopAsync()
    {
        var cts = cancellation;
        if (cts is null)
            return;
        cts.Cancel();
        listener?.Stop();
        try
        {
            if (run is not null)
                await run;
        }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Dispose();
            cancellation = null;
            run = null;
            listener = null;
            Volatile.Write(ref latest, null);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
