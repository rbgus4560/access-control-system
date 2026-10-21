// 파일 역할: 서버로 연결하는 영상 전송 에이전트입니다. 원본·클립을 요청받아 전송하고 수신 확인 후 임시 클립만 정리합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AccessControl.Faces;
using AccessControl.Network;

namespace FaceRecognition;

public sealed class MediaAgent : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private readonly object gate = new();
    private CancellationTokenSource? connection;

    public void Reconnect()
    {
        lock (gate)
        {
            try
            {
                connection?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }

    public event Action<string>? StatusChanged;

    public void Start() => loop = Task.Run(Run);

    private async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var p = LinkSecurity.Client;
                if (p is null)
                {
                    await Task.Delay(1000, stop.Token);
                    continue;
                }

                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                lock (gate)
                {
                    connection = session;
                }

                using var tcp = new TcpClient { NoDelay = true };
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                connect.CancelAfter(8000);
                await tcp.ConnectAsync(p.Host, p.Port + 2, connect.Token);
                await using var stream = await LinkSecurity.ConnectAsync(tcp, "api", connect.Token);
                await JsonWire.WriteAsync(
                    stream,
                    new RemoteEnvelope { Kind = "agent" },
                    140000,
                    connect.Token
                );
                StatusChanged?.Invoke("영상·클립 요청 수신 대기");
                while (!stop.IsCancellationRequested)
                {
                    using var read = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    read.CancelAfter(10000);
                    var job = await JsonWire.ReadAsync<MediaRequest>(stream, 16384, read.Token);
                    using var work = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    work.CancelAfter(TimeSpan.FromMinutes(3));
                    await Process(stream, job, work.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                StatusChanged?.Invoke("영상·클립 연결 대기 · 자동 재접속");
            }
            finally
            {
                lock (gate)
                {
                    connection = null;
                }
            }

            try
            {
                await Task.Delay(2000, stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // 서버가 요청한 구간·클립만 처리합니다. 수신 ACK 이전에는 임시 파일을 지우지 않습니다.
    private async Task Process(Stream stream, MediaRequest job, CancellationToken token)
    {
        MediaResponse response = new() { JobId = job.JobId };
        string? path = null;
        bool clip = false,
            permanent = false;
        IDisposable? use = null;
        try
        {
            if (job.JobId == Guid.Empty)
                throw new InvalidDataException("요청 ID 오류");
            if (job.Kind == "ping")
                response = response with { Success = true };
            else if (job.Kind == "segment")
            {
                var probe = new RecordingEntry
                {
                    SegmentId = job.SegmentId,
                    StartTime = job.StartTime
                };
                path = RecordingFiles.PathFor(probe);
                RecordingRetention.NoLinks(path);
                var entry =
                    JsonSerializer.Deserialize<RecordingEntry>(
                        await RecordingFiles.ReadManifestAsync(
                            Path.ChangeExtension(path, ".json"),
                            token
                        )
                    ) ?? throw new InvalidDataException();
                RecordingFiles.Validate(entry);
                if (
                    entry.SegmentId != job.SegmentId
                    || entry.StartTime != job.StartTime
                    || entry.Status != "READY"
                    || new FileInfo(path).Length != entry.FileSizeBytes
                )
                    throw new InvalidDataException("완료 원본을 찾을 수 없습니다.");
                use =
                    RecordingMediaUse.TryUse([path]) ?? throw new IOException("원본 보관 정리 중");
                response = response with { Success = true, Recording = entry };
            }
            else if (job.Kind == "clip" && job.Clip is { } requested)
            {
                requested = requested with { FilePath = ClipFiles.AgentPathFor(requested.ClipId) };
                ClipFiles.Validate(requested);
                if (requested.ClipId != job.JobId || requested.Status != "EXPORTING")
                    throw new InvalidDataException("클립 요청 오류");
                path = requested.FilePath;
                clip = true;
                Directory.CreateDirectory(ClipFiles.AgentRoot);
                if (
                    new DriveInfo(Path.GetPathRoot(ClipFiles.Root)!).AvailableFreeSpace
                    < 20L * 1024 * 1024 * 1024
                )
                    throw new IOException("클라이언트 여유 공간 20GB가 필요합니다.");
                ClipInfo? completed = null;
                if (File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".json")))
                {
                    var cached = JsonSerializer.Deserialize<ClipInfo>(
                        await RecordingFiles.ReadManifestAsync(
                            Path.ChangeExtension(path, ".json"),
                            token
                        )
                    );
                    if (
                        cached is { Status: "READY" }
                        && cached.ClipId == requested.ClipId
                        && cached.StartTime == requested.StartTime
                        && cached.EndTime == requested.EndTime
                        && cached.EventId == requested.EventId
                        && new FileInfo(path).Length == cached.FileSizeBytes
                    )
                    {
                        ClipFiles.Validate(cached);
                        if (await MediaTransfer.HashAsync(path, token) == cached.FileHash)
                            completed = cached;
                    }
                }

                if (completed is null)
                {
                    StatusChanged?.Invoke("서버 요청 클립 추출 중");
                    ClipJob plan;
                    try
                    {
                        var day = await RecordingTimeline.ReadAsync(
                            requested.StartTime.LocalDateTime.Date,
                            token
                        );
                        plan = ClipFiles.Plan(
                            day.Segments,
                            requested.StartTime,
                            requested.EndTime,
                            requested.EventId
                        ) with
                        {
                            Clip = requested
                        };
                    }
                    catch (InvalidDataException)
                    {
                        permanent = true;
                        throw;
                    }
                    catch (FileNotFoundException)
                    {
                        permanent = true;
                        throw;
                    }

                    using var originals =
                        RecordingMediaUse.TryUse(plan.Parts.Select(p => p.FilePath))
                        ?? throw new IOException("원본 정리 중");
                    // A failed encoder can leave a partial output. Only this client's temporary
                    // file is replaceable; server copies and recording originals are untouched.
                    using (
                        var stale =
                            RecordingMediaUse.TryDelete(path)
                            ?? throw new IOException("임시 클립 사용 중")
                    )
                    {
                        foreach (
                            string temporary in new[] { path, Path.ChangeExtension(path, ".json") }
                        )
                        {
                            RecordingRetention.NoLinks(temporary);
                            if (File.Exists(temporary))
                                File.Delete(temporary);
                        }
                    }

                    completed = await ClipExporter.RunPython(plan, token);
                    ClipFiles.Validate(completed);
                    if (
                        completed.ClipId != requested.ClipId
                        || completed.StartTime != requested.StartTime
                        || completed.EndTime != requested.EndTime
                        || completed.EventId != requested.EventId
                    )
                        throw new InvalidDataException("클립 결과 불일치");
                    await MediaTransfer.WriteJsonAsync(
                        Path.ChangeExtension(path, ".json"),
                        completed,
                        token
                    );
                }

                use =
                    RecordingMediaUse.TryUse([path])
                    ?? throw new IOException("클립 파일 사용 불가");
                response = response with { Success = true, Clip = completed };
            }
            else
                throw new InvalidDataException("지원되지 않는 미디어 요청");
            if (path is not null)
            {
                RecordingRetention.NoLinks(path);
                await using var file = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    65536,
                    FileOptions.Asynchronous
                );
                string hash = Convert
                    .ToHexString(await SHA256.HashDataAsync(file, token))
                    .ToLowerInvariant();
                file.Position = 0;
                response = response with { Length = file.Length, Hash = hash };
                MediaTransfer.ValidateHeader(response);
                await JsonWire.WriteAsync(stream, response, 16384, token);
                await file.CopyToAsync(stream, 65536, token);
                await stream.FlushAsync(token);
            }
            else
                await JsonWire.WriteAsync(stream, response, 16384, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !response.Success)
        {
            // A completed clip range with missing coverage cannot improve by blindly retrying.
            // Connection, disk space and Python failures stay retryable.
            bool terminal = job.Kind == "clip" && permanent;
            string reason = ex.GetBaseException().Message;
            response = response with
            {
                Success = false,
                TerminalFailure = terminal,
                Error = reason.Length > 700 ? reason[..700] : reason
            };
            await JsonWire.WriteAsync(stream, response, 16384, token);
        }
        finally
        {
            use?.Dispose();
        }

        var receipt = await JsonWire.ReadAsync<MediaReceipt>(stream, 1024, token);
        if (receipt.JobId != job.JobId)
            throw new InvalidDataException("전송 확인 불일치");
        if (clip && receipt.Accepted && path is not null)
        {
            // Delete only this temporary generated clip after the server durably verified its copy.
            foreach (
                string candidate in new[]
                {
                    path,
                    Path.ChangeExtension(path, ".json"),
                    Path.ChangeExtension(path, ".job.json")
                }
            )
                try
                {
                    RecordingRetention.NoLinks(candidate);
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
                catch (IOException) { }
        }

        if (job.Kind != "ping")
            StatusChanged?.Invoke(
                receipt.Accepted ? "서버 영상 파일 수신 확인"
                : response.TerminalFailure ? "클립 요청 실패 확정 · " + response.Error
                : "영상 요청 재시도 대기 · " + response.Error
            );
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null)
            try
            {
                await loop;
            }
            catch (OperationCanceledException) { }

        stop.Dispose();
    }
}
