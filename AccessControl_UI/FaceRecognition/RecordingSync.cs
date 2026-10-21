// 파일 역할: 로컬 녹화 JSON을 서버 DB에 재전송합니다. 구간별 ACK와 공정한 재시도로 과거 오류가 신규 녹화를 막지 않게 합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Text.Json;
using AccessControl.Faces;

namespace FaceRecognition;

// Durable outbox: media + atomic JSON survive closing either app. ACKs are only a memory optimization.
public sealed class RecordingSync : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, string> acknowledged = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, string> acknowledgedClips = new(
        StringComparer.OrdinalIgnoreCase
    );
    private Guid? databaseSession;
    private readonly Dictionary<string, long> attempted = new(StringComparer.OrdinalIgnoreCase);
    private long attemptSequence;
    private Task? run;
    private readonly RecordingRetention retention = new();
    private long lastRetention;
    public event Action<string>? StatusChanged;
    public event Action<string>? RetentionStatusChanged;

    public void Start() => run = Task.Run(Loop);

    // 완료 JSON을 재시도 가능한 작업 목록으로 사용합니다. 성공한 원문만 ACK하여 변경된 상태는 다시 전송합니다.
    private async Task Loop()
    {
        var token = stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (
                    lastRetention == 0
                    || System.Diagnostics.Stopwatch.GetElapsedTime(lastRetention)
                        >= TimeSpan.FromMinutes(1)
                )
                {
                    lastRetention = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        var cleaned = await retention.RunAsync(token);
                        RetentionStatusChanged?.Invoke(
                            $"원본 보관 72시간 · 만료 {cleaned.Expired}건 / 정리 {cleaned.Deleted}건 / 사용 중·잔여 {cleaned.Deferred}건"
                                + (cleaned.Invalid > 0 ? $" / 확인 필요 {cleaned.Invalid}건" : "")
                        );
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        RetentionStatusChanged?.Invoke(
                            "원본 보관 정리 대기 · 파일 접근 상태 확인 필요"
                        );
                    }
                }

                if (!Directory.Exists(RecordingFiles.Root))
                {
                    StatusChanged?.Invoke("DB 녹화 동기화 · 저장 구간 없음");
                }
                else
                {
                    var pending = new List<(string Path, string Stamp, RecordingEntry Entry)>();
                    int invalid = 0,
                        total = 0;
                    foreach (
                        string file in Directory
                            .EnumerateFiles(
                                RecordingFiles.Root,
                                "*.json",
                                SearchOption.AllDirectories
                            )
                            .OrderBy(f => f)
                    )
                    {
                        token.ThrowIfCancellationRequested();
                        total++;
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.Length > 8192)
                                throw new InvalidDataException();
                            string text = await RecordingFiles.ReadManifestAsync(file, token);
                            var entry =
                                JsonSerializer.Deserialize<RecordingEntry>(text)
                                ?? throw new InvalidDataException();
                            RecordingFiles.Validate(entry);
                            if (
                                !string.Equals(
                                    Path.ChangeExtension(entry.FilePath, ".json"),
                                    file,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                                throw new InvalidDataException();
                            if (entry.Status == "DELETING")
                                continue;
                            pending.Add((file, text, entry));
                        }
                        catch (Exception ex)
                            when (ex
                                    is IOException
                                        or JsonException
                                        or InvalidDataException
                                        or ArgumentException
                            )
                        {
                            invalid++;
                        }
                    }

                    if (total == 0)
                    {
                        StatusChanged?.Invoke("DB 녹화 동기화 · 저장 구간 없음");
                    }
                    else
                    {
                        var state = await FaceApi.CallAsync(
                            new FaceRequest { Operation = "recording-state" },
                            token
                        );
                        if (state.DatabaseSession is not { } epoch || epoch == Guid.Empty)
                            throw new InvalidDataException("서버 녹화 버전을 확인하세요.");
                        if (state.DatabaseSession != databaseSession)
                        {
                            acknowledged.Clear();
                            acknowledgedClips.Clear();
                            databaseSession = state.DatabaseSession;
                        }

                        var unsent = pending
                            .Where(p =>
                                !acknowledged.TryGetValue(p.Path, out var value) || value != p.Stamp
                            )
                            .ToList();
                        int sent = 0,
                            failed = 0;
                        string error = "";
                        // A conflicting legacy segment must not block newer recordings.
                        // Least recently attempted first prevents a full batch of failures starving the queue.
                        foreach (
                            var item in unsent
                                .OrderBy(p => attempted.GetValueOrDefault(p.Path))
                                .Take(16)
                        )
                        {
                            attempted[item.Path] = ++attemptSequence;
                            try
                            {
                                var reply = await FaceApi.CallAsync(
                                    new FaceRequest
                                    {
                                        Operation = "recording",
                                        Recording = item.Entry
                                    },
                                    token
                                );
                                if (reply.RecordedSegmentId != item.Entry.SegmentId)
                                    throw new InvalidDataException("녹화 구간 응답 불일치");
                                acknowledged[item.Path] = item.Stamp;
                                sent++;
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                error =
                                    $"{item.Entry.SegmentId:N}: {ex.GetBaseException().Message}";
                            }
                        }

                        StatusChanged?.Invoke(
                            $"DB 녹화 동기화 · 대기 {unsent.Count - sent}건 / 전체 {total}건"
                                + (invalid > 0 ? $" · 메타데이터 확인 필요 {invalid}건" : "")
                                + (
                                    failed > 0
                                        ? $"\n이번 처리 실패 {failed}건 · 재시도 예정 · {error}"
                                        : ""
                                )
                        );
                    }
                }

                if (FaceApi.RemoteCaller is null)
                    await SyncClips(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("DB 녹화 동기화 재시도 · " + ex.GetBaseException().Message);
            }

            try
            {
                await Task.Delay(3000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncClips(CancellationToken token)
    {
        if (!Directory.Exists(ClipFiles.Root))
            return;
        var pending = new List<(string Path, string Text, ClipInfo Clip)>();
        foreach (string file in Directory.EnumerateFiles(ClipFiles.Root, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _))
                continue;
            try
            {
                string text = await RecordingFiles.ReadManifestAsync(file, token);
                var clip =
                    JsonSerializer.Deserialize<ClipInfo>(text) ?? throw new InvalidDataException();
                if (clip.Status is not ("READY" or "FAILED"))
                    continue;
                ClipFiles.Validate(clip);
                if (
                    !string.Equals(
                        file,
                        Path.ChangeExtension(clip.FilePath, ".json"),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;
                pending.Add((file, text, clip));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            { }
        }

        if (pending.Count == 0)
            return;
        var state = await FaceApi.CallAsync(
            new FaceRequest { Operation = "recording-state" },
            token
        );
        if (state.DatabaseSession is not { } session || session == Guid.Empty)
            throw new InvalidDataException();
        if (session != databaseSession)
        {
            databaseSession = session;
            acknowledged.Clear();
            acknowledgedClips.Clear();
        }

        int count = 0;
        foreach (var item in pending)
        {
            if (acknowledgedClips.TryGetValue(item.Path, out var stamp) && stamp == item.Text)
                continue;
            if (count++ >= 16)
                break;
            var reply = await FaceApi.CallAsync(
                new FaceRequest { Operation = "clip-complete", Clip = item.Clip },
                token
            );
            if (reply.ClipId != item.Clip.ClipId)
                throw new InvalidDataException();
            acknowledgedClips[item.Path] = item.Text;
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (run is not null)
        {
            try
            {
                await run;
            }
            catch (OperationCanceledException) { }
        }

        stop.Dispose();
    }
}
