// 파일 역할: 완료 원본의 72시간 보관 정책을 적용합니다. 사용 중인 파일은 다음 정리로 미루고 삭제 이력을 남깁니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AccessControl.Faces;

public sealed record RetentionResult(int Expired, int Deleted, int Deferred, int Invalid);

public sealed class RecordingRetention
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(72);
    public const string Reason = "RETENTION_72H";
    private readonly string root;

    public RecordingRetention()
        : this(RecordingFiles.Root) { }

    // Explicit root supports isolated temporary-file tests. No network request can choose it.
    internal RecordingRetention(string root) => this.root = Path.GetFullPath(root);

    public static bool IsExpired(RecordingEntry entry, DateTimeOffset now) =>
        entry.Status is "READY" or "DELETING" && entry.EndTime is { } end && end <= now - KeepFor;

    // 만료된 완료 원본만 대상으로 하며, 사용권 획득과 메타데이터 갱신 순서로 중간 종료에도 삭제 상태를 복구합니다.
    public async Task<RetentionResult> RunAsync(CancellationToken token = default)
    {
        int expired = 0,
            deleted = 0,
            deferred = 0,
            invalid = 0;
        if (!Directory.Exists(root))
            return new(0, 0, 0, 0);
        NoLinks(root);
        var now = DateTimeOffset.UtcNow;
        foreach (string folder in Directory.EnumerateDirectories(root).OrderBy(p => p))
        {
            token.ThrowIfCancellationRequested();
            if (
                !DateTime.TryParseExact(
                    Path.GetFileName(folder),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _
                )
            )
                continue;
            try
            {
                NoLinks(folder);
            }
            catch (IOException)
            {
                invalid++;
                continue;
            }

            foreach (string manifest in Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    NoLinks(manifest);
                    var entry =
                        JsonSerializer.Deserialize<RecordingEntry>(
                            await RecordingFiles.ReadManifestAsync(manifest, token)
                        ) ?? throw new InvalidDataException();
                    RecordingFiles.ValidateAtRoot(entry, root);
                    if (
                        !string.Equals(
                            Path.ChangeExtension(entry.FilePath, ".json"),
                            manifest,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new InvalidDataException();
                    if (!IsExpired(entry, now))
                        continue;
                    expired++;
                    // Bound work per pass, leaving remaining candidates for the next minute.
                    if (deleted >= 64)
                    {
                        deferred++;
                        continue;
                    }

                    using var lease = RecordingMediaUse.TryDelete(entry.FilePath);
                    if (lease is null)
                    {
                        deferred++;
                        continue;
                    }

                    NoLinks(entry.FilePath);
                    if (entry.Status == "READY" && !File.Exists(entry.FilePath))
                        throw new IOException("완료 원본 누락 · 삭제 이력 확인 필요");
                    if (
                        File.Exists(entry.FilePath)
                        && new FileInfo(entry.FilePath).Length != entry.FileSizeBytes
                    )
                        throw new InvalidDataException("원본 크기 변경");
                    if (entry.Status == "READY")
                    {
                        // Persist intent before removing media, so restart can finish the tombstone.
                        entry = entry with
                        {
                            Status = "DELETING",
                            DeletionReason = Reason
                        };
                        await WriteManifestAsync(manifest, entry, token);
                    }

                    File.Delete(entry.FilePath); // Missing media after a crash is an idempotent success.
                    if (File.Exists(entry.FilePath))
                        throw new IOException("원본 삭제 미완료");
                    // Once media is removed, finish persistent metadata even during app shutdown.
                    await WriteManifestAsync(
                        manifest,
                        entry with
                        {
                            Status = "DELETED",
                            DeletedAt = DateTimeOffset.UtcNow
                        },
                        CancellationToken.None
                    );
                    deleted++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                    when (ex
                            is IOException
                                or UnauthorizedAccessException
                                or JsonException
                                or InvalidDataException
                                or ArgumentException
                    )
                {
                    invalid++;
                }
            }
        }

        return new(expired, deleted, deferred, invalid);
    }

    internal static void NoLinks(string path)
    {
        for (
            string? current = Path.GetFullPath(path);
            current is not null;
            current = Path.GetDirectoryName(current)
        )
        {
            // Check files and every ancestor; never follow a junction/symlink out of the media root.
            if (
                (File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
            )
                throw new IOException("링크 경로는 보관 정리에서 제외합니다.");
        }
    }

    internal static async Task WriteManifestAsync(
        string path,
        RecordingEntry entry,
        CancellationToken token
    )
    {
        NoLinks(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (
                var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous
                )
            )
            {
                await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }

            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
