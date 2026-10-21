// 파일 역할: 인증 TCP 연결을 수신하고 메시지를 검증한 뒤 서버 판정 콜백의 결과를 응답합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Net;
using System.Net.Sockets;

namespace AccessControl.Network;

public sealed class MonitorServer : IAsyncDisposable
{
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;
    private Task? run;
    public event Action<string>? StatusChanged;
    public event Action<MonitorPacket>? Received;
    public Func<MonitorPacket, CancellationToken, Task<AccessDecision?>>? DecideAsync { get; set; }
    public bool Running => cancellation is not null;
    public int BoundPort { get; private set; }

    public void Start(int port)
    {
        if (Running)
            throw new InvalidOperationException("이미 서버 대기 중입니다.");
        if (!MonitorProtocol.ValidPort(port))
            throw new ArgumentException("TCP 포트를 확인해 주세요.");
        var tcp = new TcpListener(LinkSecurity.BindAddress, port);
        // Bind synchronously so conflicts are visible at the Start button.
        tcp.Start(1);
        listener = tcp;
        BoundPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        StatusChanged?.Invoke($"TCP 수신 대기 · :{BoundPort}");
        run = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // One authenticated recognition device (CAM-01) in this two-PC version.
                        using var client = await tcp.AcceptTcpClientAsync(token);
                        client.NoDelay = true;
                        using var stream = await LinkSecurity.AcceptAsync(client, "monitor", token);
                        string? session = null;
                        long sequence = 0;
                        while (!token.IsCancellationRequested)
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                                token
                            );
                            timeout.CancelAfter(MonitorProtocol.Timeout);
                            var packet = await MonitorProtocol.ReadAsync(stream, timeout.Token);
                            MonitorProtocol.Validate(packet, "snapshot");
                            session ??= packet.Session;
                            if (packet.Session != session || packet.Sequence != sequence + 1)
                                throw new InvalidDataException("세션·순번 오류");
                            sequence = packet.Sequence;
                            var decision = DecideAsync is null
                                ? null
                                : await DecideAsync(packet, timeout.Token);
                            Received?.Invoke(packet with { Decision = decision });
                            await MonitorProtocol.WriteAsync(
                                stream,
                                packet with
                                {
                                    Type = "ack",
                                    Snapshot = null,
                                    Decision = decision
                                },
                                timeout.Token
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
                                    or InvalidDataException
                                    or System.Text.Json.JsonException
                        )
                    {
                        if (!token.IsCancellationRequested)
                            StatusChanged?.Invoke(
                                $"클라이언트 연결 종료 · TCP 재접속 대기 :{BoundPort}"
                            );
                    }
                }
            },
            token
        );
    }

    public async Task StopAsync()
    {
        var source = cancellation;
        if (source is null)
            return;
        source.Cancel();
        listener?.Stop();
        try
        {
            if (run is not null)
                await run;
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener = null;
            run = null;
            source.Dispose();
            cancellation = null;
            StatusChanged?.Invoke("TCP 서버 중지");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
