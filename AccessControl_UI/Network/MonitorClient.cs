// 파일 역할: 인증 관측을 서버로 전송하고 판정 ACK를 받습니다. 연결이 끊어지면 다시 연결합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Net.Sockets;

namespace AccessControl.Network;

public sealed class MonitorClient : IAsyncDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? run;
    public event Action<string>? StatusChanged;
    public event Action<MonitorPacket>? Acknowledged;
    public bool Running => cancellation is not null;

    public void Start(string host, int port, Func<MonitorSnapshot> latest)
    {
        if (Running)
            throw new InvalidOperationException("TCP 연결을 먼저 종료해 주세요.");
        var address = MonitorProtocol.LocalAddress(host);
        if (!MonitorProtocol.ValidPort(port))
            throw new ArgumentException("TCP 포트를 확인해 주세요.");
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        run = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        StatusChanged?.Invoke("서버 연결 시도 중");
                        using var tcp = new TcpClient { NoDelay = true };
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(MonitorProtocol.Timeout);
                            await tcp.ConnectAsync(address, port, timeout.Token);
                        }

                        using var stream = await LinkSecurity.ConnectAsync(tcp, "monitor", token);
                        string session = Guid.NewGuid().ToString("D");
                        long sequence = 0;
                        while (!token.IsCancellationRequested)
                        {
                            var packet = new MonitorPacket
                            {
                                Session = session,
                                Sequence = ++sequence,
                                Snapshot = latest()
                            };
                            MonitorProtocol.Validate(packet, "snapshot");
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                                token
                            );
                            timeout.CancelAfter(MonitorProtocol.Timeout);
                            await MonitorProtocol.WriteAsync(stream, packet, timeout.Token);
                            var ack = await MonitorProtocol.ReadAsync(stream, timeout.Token);
                            MonitorProtocol.Validate(ack, "ack");
                            if (ack.Session != session || ack.Sequence != sequence)
                                throw new InvalidDataException("수신 확인 순번 불일치");
                            StatusChanged?.Invoke(
                                $"서버 연결 · 수신 확인 #{sequence} · {DateTime.Now:HH:mm:ss}"
                            );
                            Acknowledged?.Invoke(ack);
                            await Task.Delay(1000, token);
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
                                    or System.Text.Json.JsonException
                        )
                    {
                        StatusChanged?.Invoke("서버 미연결 · 2초 뒤 재접속");
                    }

                    try
                    {
                        await Task.Delay(2000, token);
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
        var source = cancellation;
        if (source is null)
            return;
        source.Cancel();
        try
        {
            if (run is not null)
                await run;
        }
        catch (OperationCanceledException) { }
        finally
        {
            source.Dispose();
            cancellation = null;
            run = null;
            StatusChanged?.Invoke("서버 연결 종료");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
