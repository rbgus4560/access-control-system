// 파일 역할: TLS API 요청과 미디어 에이전트 연결을 처리합니다. 클라이언트의 관리자 쓰기 요청은 허용하지 않습니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using AccessControl.Network;

namespace AccessControl.Faces;

public sealed record RemoteEnvelope
{
    public string Kind { get; init; } = "face";
    public FaceRequest? Request { get; init; }
    public FaceReply? Reply { get; init; }
}

public sealed record MediaRequest
{
    public Guid JobId { get; init; } = Guid.NewGuid();
    public string Kind { get; init; } = "ping";
    public Guid SegmentId { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public ClipInfo? Clip { get; init; }
}

public sealed record MediaResponse
{
    public Guid JobId { get; init; }
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public bool TerminalFailure { get; init; }
    public RecordingEntry? Recording { get; init; }
    public ClipInfo? Clip { get; init; }
    public long Length { get; init; }
    public string Hash { get; init; } = "";
}

public sealed record MediaReceipt(Guid JobId, bool Accepted);

public static class RemoteFaceClient
{
    public static async Task<FaceReply> CallAsync(FaceRequest request, CancellationToken stop)
    {
        var p =
            LinkSecurity.Client
            ?? throw new InvalidOperationException("설정에서 서버 연결 코드를 적용하세요.");
        p.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(20000);
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(p.Host, p.Port + 2, timeout.Token);
        await using var stream = await LinkSecurity.ConnectAsync(tcp, "api", timeout.Token);
        await JsonWire.WriteAsync(
            stream,
            new RemoteEnvelope { Request = request },
            140000,
            timeout.Token
        );
        var response = await JsonWire.ReadAsync<RemoteEnvelope>(stream, 600000, timeout.Token);
        var reply = response.Reply ?? throw new InvalidDataException("서버 응답 없음");
        if (reply.Version != 1 || reply.RequestId != request.RequestId)
            throw new InvalidDataException("서버 응답 불일치");
        if (!reply.Success)
            throw new InvalidOperationException(reply.Message);
        return reply;
    }
}

public sealed class RemoteApiServer : IAsyncDisposable
{
    private readonly Func<FaceRequest, CancellationToken, Task<FaceReply>> handle;
    private readonly Func<Stream, CancellationToken, Task> agent;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(8);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> clients = new();
    private TcpListener? listener;
    private Task? loop;

    public RemoteApiServer(
        Func<FaceRequest, CancellationToken, Task<FaceReply>> handle,
        Func<Stream, CancellationToken, Task> agent
    )
    {
        this.handle = handle;
        this.agent = agent;
    }

    public void Start(int port)
    {
        if (LinkSecurity.Server is null)
            throw new InvalidOperationException("서버 연결 인증 정보가 없습니다.");
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start(16);
        loop = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                var task = Serve(client);
                clients.TryAdd(task, 0);
                _ = task.ContinueWith(
                    t => clients.TryRemove(t, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        using (tcp)
            try
            {
                using var hello = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                hello.CancelAfter(20000);
                await using var stream = await LinkSecurity.AcceptAsync(tcp, "api", hello.Token);
                var envelope = await JsonWire.ReadAsync<RemoteEnvelope>(
                    stream,
                    140000,
                    hello.Token
                );
                if (envelope.Kind == "agent")
                {
                    await agent(stream, stop.Token);
                    return;
                }

                var request = envelope.Request ?? throw new InvalidDataException();
                FaceReply reply;
                try
                {
                    // Enrollment writes and administrator UI commands remain on the server PC.
                    if (
                        envelope.Kind != "face"
                        || request.Version != 1
                        || request.RequestId == Guid.Empty
                        || request.Operation
                            is not (
                                "employees"
                                or "load"
                                or "catalog"
                                or "gallery-load"
                                or "recording"
                                or "recording-state"
                            )
                    )
                        throw new InvalidDataException(
                            "구역 클라이언트에서 허용되지 않는 요청입니다."
                        );
                    reply = await handle(request with { Remote = true }, hello.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogFailure(request, ex);
                    reply = new FaceReply
                    {
                        RequestId = request.RequestId,
                        Message =
                            $"서버 {request.Operation} 요청 실패 · server-api-errors.log 확인 (요청 {request.RequestId:N})"
                    };
                }

                await JsonWire.WriteAsync(
                    stream,
                    new RemoteEnvelope { Reply = reply },
                    600000,
                    hello.Token
                );
            }
            catch (Exception) when (!stop.IsCancellationRequested) { }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                slots.Release();
            }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener?.Stop();
        if (loop is not null)
            try
            {
                await loop;
            }
            catch (OperationCanceledException) { }

        await Task.WhenAll(
            clients.Keys.Select(async t =>
            {
                try
                {
                    await t;
                }
                catch (Exception) { }
            })
        );
        stop.Dispose();
        slots.Dispose();
    }

    private static readonly object logGate = new();

    private static void LogFailure(FaceRequest request, Exception error)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AccessControlSystem"
            );
            lock (logGate)
            {
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "server-api-errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".previous", true);
                // Do not log request bodies, pairing credentials, or face reference vectors.
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} Request={request.RequestId:N} Operation={request.Operation} Segment={request.Recording?.SegmentId}\n{error}\n\n"
                );
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
