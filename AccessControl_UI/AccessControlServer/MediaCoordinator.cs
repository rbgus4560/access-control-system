// 파일 역할: 서버의 영상·클립 요청을 직렬화합니다. 파일 검증과 영속 작업 기록으로 재시작 후 DB 반영을 재시도합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using AccessControl.Faces;
using AccessControl.Network;

namespace AccessControlServer;

// The zone opens an outbound connection. Destinations are chosen locally, never from client paths.
public sealed class MediaCoordinator : IAsyncDisposable
{
    private sealed record Work(
        MediaRequest Request,
        RecordingEntry? Expected,
        TaskCompletionSource<MediaResponse> Done
    );

    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(
        new BoundedChannelOptions(16)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        }
    );
    private readonly ConcurrentDictionary<Guid, byte> activeClips = new();
    private readonly SemaphoreSlim agentSlot = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Func<FaceDatabase?> database;
    private readonly Task recovery;
    private int connected;
    public bool Connected => Volatile.Read(ref connected) == 1;

    public MediaCoordinator(Func<FaceDatabase?> database)
    {
        this.database = database;
        recovery = Task.Run(Recover);
    }

    private static string Pending(Guid id) =>
        Path.Combine(ClipFiles.Root, id.ToString("N") + ".remotejob.json");

    private Task<MediaResponse> Submit(
        MediaRequest request,
        RecordingEntry? expected,
        CancellationToken token
    )
    {
        var done = new TaskCompletionSource<MediaResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        if (!queue.Writer.TryWrite(new(request, expected, done)))
            throw new IOException("영상 요청이 많습니다. 잠시 후 다시 시도하세요.");
        return Wait();
        async Task<MediaResponse> Wait()
        {
            try
            {
                return await done.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled(token);
                throw;
            }
        }
    }

    public async Task<RecordingEntry> PrepareAsync(RecordingEntry entry, CancellationToken token)
    {
        if (
            !entry.FilePath.StartsWith(
                RecordingFiles.CacheRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return entry;
        RecordingFiles.Validate(entry);
        if (RecordingRetention.IsExpired(entry, DateTimeOffset.UtcNow))
            throw new IOException("72시간 원본 보관 기간이 지났습니다.");
        string manifest = Path.ChangeExtension(entry.FilePath, ".json");
        if (File.Exists(manifest) && File.Exists(entry.FilePath))
        {
            var cached = JsonSerializer.Deserialize<RecordingEntry>(
                await RecordingFiles.ReadManifestAsync(manifest, token)
            );
            string hashPath = entry.FilePath + ".sha256";
            if (
                cached == entry
                && new FileInfo(entry.FilePath).Length == entry.FileSizeBytes
                && File.Exists(hashPath)
                && await File.ReadAllTextAsync(hashPath, token)
                    == await MediaTransfer.HashAsync(entry.FilePath, token)
            )
                return entry;
        }

        if (!Connected)
            throw new IOException("카메라 클라이언트의 영상 전송 연결을 확인하세요.");
        await Submit(
            new MediaRequest
            {
                Kind = "segment",
                SegmentId = entry.SegmentId,
                StartTime = entry.StartTime
            },
            entry,
            token
        );
        return entry;
    }

    public async Task<ClipInfo> ExportAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        Guid? eventId,
        CancellationToken token
    )
    {
        var db = database() ?? throw new InvalidOperationException("서버 DB에 연결하세요.");
        var clip = new ClipInfo
        {
            ClipId = Guid.NewGuid(),
            StartTime = start.ToUniversalTime(),
            EndTime = end.ToUniversalTime(),
            EventId = eventId
        };
        clip = clip with { FilePath = ClipFiles.PathFor(clip.ClipId) };
        ClipFiles.Validate(clip);
        if (!Connected)
            throw new IOException("클라이언트의 영상·클립 연결을 먼저 확인하세요.");
        await db.BeginClipAsync(clip, token);
        return await ExecuteClip(clip, stop.Token).WaitAsync(token);
    }

    // 작업 JSON을 먼저 남겨 서버가 종료되어도 요청을 잃지 않게 합니다. 결과 파일과 DB 완료 처리는 재시도할 수 있습니다.
    private async Task<ClipInfo> ExecuteClip(ClipInfo requested, CancellationToken token)
    {
        if (!activeClips.TryAdd(requested.ClipId, 0))
            throw new IOException("클립 요청 처리 중");
        try
        {
            // Claim before publishing the job, so recovery cannot race the first UI submission.
            if (!File.Exists(Pending(requested.ClipId)))
                await MediaTransfer.WriteJsonAsync(
                    Pending(requested.ClipId),
                    requested,
                    CancellationToken.None
                );
            string manifest = Path.ChangeExtension(requested.FilePath, ".json");
            if (File.Exists(manifest))
            {
                var saved = JsonSerializer.Deserialize<ClipInfo>(
                    await RecordingFiles.ReadManifestAsync(manifest, token)
                );
                if (saved is { Status: "READY" or "FAILED" } && SameClip(saved, requested))
                {
                    await Complete(saved, token);
                    return saved;
                }
            }

            var reply = await Submit(
                new MediaRequest
                {
                    JobId = requested.ClipId,
                    Kind = "clip",
                    Clip = requested
                },
                null,
                token
            );
            var clip = reply.Success
                ? reply.Clip! with
                {
                    FilePath = requested.FilePath
                }
                : requested with
                {
                    Status = "FAILED"
                };
            try
            {
                await Complete(clip, token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            { /* Durable file+job stay for DB retry. */
            }

            return clip;
        }
        finally
        {
            activeClips.TryRemove(requested.ClipId, out _);
        }
    }

    private async Task Complete(ClipInfo clip, CancellationToken token)
    {
        await (database() ?? throw new InvalidOperationException("DB 연결 대기")).CompleteClipAsync(
            clip,
            token
        );
        File.Delete(Pending(clip.ClipId));
    }

    private static bool SameClip(ClipInfo a, ClipInfo b) =>
        a.ClipId == b.ClipId
        && a.EventId == b.EventId
        && a.StartTime == b.StartTime
        && a.EndTime == b.EndTime;

    // 연결 하나에서 파일 요청을 순서대로 처리합니다. 검증 후 수신 ACK를 보내고 대기 중인 요청을 완료합니다.
    public async Task ServeAgentAsync(Stream stream, CancellationToken token)
    {
        if (!agentSlot.Wait(0))
            throw new IOException("이미 CAM-01 영상 연결이 있습니다.");
        Volatile.Write(ref connected, 1);
        try
        {
            while (!token.IsCancellationRequested)
            {
                Work? work = null;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(2000);
                    try
                    {
                        while (await queue.Reader.WaitToReadAsync(idle.Token))
                        {
                            if (queue.Reader.TryRead(out work))
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                }

                if (work is not null && work.Done.Task.IsCompleted)
                    continue;
                var request = work?.Request ?? new MediaRequest();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    await JsonWire.WriteAsync(stream, request, 16384, timeout.Token);
                    var response = await JsonWire.ReadAsync<MediaResponse>(
                        stream,
                        16384,
                        timeout.Token
                    );
                    if (response.JobId != request.JobId)
                        throw new InvalidDataException("영상 요청 ID 불일치");
                    if (!response.Success)
                    {
                        if (request.Kind == "clip" && response.TerminalFailure)
                        {
                            var failed = request.Clip! with
                            {
                                Status = "FAILED",
                                FilePath = ClipFiles.PathFor(request.JobId)
                            };
                            ClipFiles.Validate(failed);
                            await MediaTransfer.WriteJsonAsync(
                                Path.ChangeExtension(failed.FilePath, ".json"),
                                failed,
                                timeout.Token
                            );
                            await File.WriteAllTextAsync(
                                failed.FilePath + ".error.txt",
                                response.Error,
                                timeout.Token
                            );
                            await JsonWire.WriteAsync(
                                stream,
                                new MediaReceipt(request.JobId, false),
                                1024,
                                timeout.Token
                            );
                            work?.Done.TrySetResult(response);
                        }
                        else
                        {
                            await JsonWire.WriteAsync(
                                stream,
                                new MediaReceipt(request.JobId, false),
                                1024,
                                timeout.Token
                            );
                            work?.Done.TrySetException(new IOException(response.Error));
                        }

                        continue;
                    }

                    if (request.Kind == "ping")
                    {
                        if (response.Length != 0)
                            throw new InvalidDataException();
                    }
                    else
                    {
                        MediaTransfer.ValidateHeader(response);
                        if (request.Kind == "segment")
                        {
                            var expected = work!.Expected!;
                            var entry = (
                                response.Recording
                                ?? throw new InvalidDataException("녹화 정보 없음")
                            ) with
                            {
                                FilePath = expected.FilePath
                            };
                            RecordingFiles.Validate(entry);
                            if (entry != expected || response.Length != expected.FileSizeBytes)
                                throw new InvalidDataException("DB 녹화 정보와 원본 불일치");
                            using var cacheUse =
                                RecordingMediaUse.TryUse([entry.FilePath])
                                ?? throw new IOException("재생 캐시 정리 중");
                            await MediaTransfer.ReceiveAsync(
                                stream,
                                entry.FilePath,
                                response,
                                timeout.Token
                            );
                            await File.WriteAllTextAsync(
                                entry.FilePath + ".sha256",
                                response.Hash,
                                timeout.Token
                            );
                            await RecordingRetention.WriteManifestAsync(
                                Path.ChangeExtension(entry.FilePath, ".json"),
                                entry,
                                timeout.Token
                            );
                        }
                        else if (request.Kind == "clip")
                        {
                            var clip = (response.Clip ?? throw new InvalidDataException()) with
                            {
                                FilePath = ClipFiles.PathFor(request.JobId)
                            };
                            ClipFiles.Validate(clip);
                            if (
                                clip.Status != "READY"
                                || !SameClip(clip, request.Clip!)
                                || clip.FileSizeBytes != response.Length
                                || clip.FileHash != response.Hash
                            )
                                throw new InvalidDataException("클립 응답 불일치");
                            await MediaTransfer.ReceiveAsync(
                                stream,
                                clip.FilePath,
                                response,
                                timeout.Token
                            );
                            await MediaTransfer.WriteJsonAsync(
                                Path.ChangeExtension(clip.FilePath, ".json"),
                                clip,
                                timeout.Token
                            );
                        }
                        else
                            throw new InvalidDataException();
                    }

                    await JsonWire.WriteAsync(
                        stream,
                        new MediaReceipt(request.JobId, true),
                        1024,
                        timeout.Token
                    );
                    work?.Done.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    work?.Done.TrySetException(
                        new IOException("영상 연결 중단 · 재연결 후 다시 시도합니다.", ex)
                    );
                    throw;
                }
            }
        }
        finally
        {
            Volatile.Write(ref connected, 0);
            agentSlot.Release();
        }
    }

    // 남아 있는 작업 파일을 읽어 완료 결과의 DB 반영 또는 클라이언트 재요청을 진행합니다.
    private async Task Recover()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await new RecordingRetention(RecordingFiles.CacheRoot).RunAsync(stop.Token);
                if (database() is not null && Directory.Exists(ClipFiles.Root))
                    foreach (
                        var path in Directory.EnumerateFiles(ClipFiles.Root, "*.remotejob.json")
                    )
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        try
                        {
                            RecordingRetention.NoLinks(path);
                            var clip =
                                JsonSerializer.Deserialize<ClipInfo>(
                                    await RecordingFiles.ReadManifestAsync(path, stop.Token)
                                ) ?? throw new InvalidDataException();
                            ClipFiles.Validate(clip);
                            if (
                                path != Pending(clip.ClipId)
                                || clip.Status != "EXPORTING"
                                || activeClips.ContainsKey(clip.ClipId)
                            )
                                continue;
                            if (
                                Connected
                                || File.Exists(Path.ChangeExtension(clip.FilePath, ".json"))
                            )
                                await ExecuteClip(clip, stop.Token);
                        }
                        catch (Exception) when (!stop.IsCancellationRequested) { }
                    }
            }
            catch (Exception) when (!stop.IsCancellationRequested) { }

            try
            {
                await Task.Delay(10000, stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        queue.Writer.TryComplete();
        while (queue.Reader.TryRead(out var work))
            work.Done.TrySetCanceled(stop.Token);
        try
        {
            await recovery;
        }
        catch (OperationCanceledException) { }

        stop.Dispose();
        agentSlot.Dispose();
    }
}
