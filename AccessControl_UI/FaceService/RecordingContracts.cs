// 파일 역할: 녹화 구간 메타데이터·고정 저장 위치·상태별 유효 조건을 정의합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;

namespace AccessControl.Faces;

public sealed record RecordingEntry
{
    public Guid SegmentId { get; init; }
    public string CameraCode { get; init; } = "CAM-01";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public string FilePath { get; init; } = "";
    public string Status { get; init; } = "";
    public long? FileSizeBytes { get; init; }
    public int FrameCount { get; init; }
    public int Fps { get; init; }
    public string Format { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public string? DeletionReason { get; init; }
}

public static class RecordingFiles
{
    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessControlSystem",
            "recordings",
            "CAM-01"
        );
    public static string CacheRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessControlSystem",
            "remote_cache",
            "CAM-01"
        );

    public static string PathFor(RecordingEntry entry, string? root = null) =>
        Path.GetFullPath(
            Path.Combine(
                root ?? Root,
                entry.StartTime.UtcDateTime.ToString("yyyyMMdd"),
                entry.SegmentId.ToString("N") + ".mp4"
            )
        );

    public static async Task<string> ReadManifestAsync(string path, CancellationToken token)
    {
        // Allow Python's atomic os.replace while a Windows reader holds the old JSON open.
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous
        );
        if (stream.Length > 8192)
            throw new InvalidDataException("녹화 메타데이터 크기 초과");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }

    public static string Validate(RecordingEntry entry) =>
        ValidateAtRoot(
            entry,
            entry.FilePath.StartsWith(
                CacheRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
                ? CacheRoot
                : Root
        );

    internal static string ValidateAtRoot(RecordingEntry entry, string root)
    {
        if (
            entry.SegmentId == Guid.Empty
            || entry.CameraCode != "CAM-01"
            || entry.StartTime == default
            || entry.StartTime.Offset != TimeSpan.Zero
            || entry.StartTime > DateTimeOffset.UtcNow.AddSeconds(5)
            || entry.Status is not ("RECORDING" or "READY" or "FAILED" or "DELETING" or "DELETED")
            || entry.Fps != 10
            || entry.Format != "mp4v"
            || entry.Width < 2
            || entry.Height < 2
            || entry.Width > 1280
            || entry.Height > 1280
            || entry.FrameCount < 0
            || entry.FrameCount > 600
            || entry.FileSizeBytes is < 0
            || entry.FileSizeBytes is > 500_000_000
        )
            throw new InvalidDataException("유효하지 않은 녹화 구간 정보입니다.");
        string expected = Path.GetFullPath(
            Path.Combine(
                root,
                entry.StartTime.UtcDateTime.ToString("yyyyMMdd"),
                entry.SegmentId.ToString("N") + ".mp4"
            )
        );
        if (
            !string.Equals(
                expected,
                entry.FilePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
        )
            throw new InvalidDataException("녹화 파일 위치가 지정 폴더와 일치하지 않습니다.");
        if (
            entry.EndTime is { } end
            && (
                end.Offset != TimeSpan.Zero
                || end <= entry.StartTime
                || end - entry.StartTime > TimeSpan.FromSeconds(60.1)
            )
        )
            throw new InvalidDataException("녹화 구간 시각이 올바르지 않습니다.");
        if (
            entry.Status == "RECORDING"
            && (
                entry.EndTime is not null
                || entry.FileSizeBytes is not null
                || entry.FrameCount != 0
            )
        )
            throw new InvalidDataException("녹화 중 상태가 올바르지 않습니다.");
        if (
            entry.Status is "READY" or "DELETING" or "DELETED"
            && (
                entry.EndTime is null
                || entry.FileSizeBytes is null or <= 0
                || entry.FrameCount < 1
                || Math.Abs(
                    (entry.EndTime.Value - entry.StartTime).TotalSeconds
                        - entry.FrameCount / (double)entry.Fps
                ) > .02
            )
        )
            throw new InvalidDataException("완료된 녹화 구간의 크기·길이가 올바르지 않습니다.");
        if (entry.Status is "DELETING" or "DELETED")
        {
            if (
                entry.DeletionReason != RecordingRetention.Reason
                || entry.EndTime > DateTimeOffset.UtcNow - RecordingRetention.KeepFor
                || (entry.Status == "DELETING" && entry.DeletedAt is not null)
                || (
                    entry.Status == "DELETED"
                    && (
                        entry.DeletedAt is not { } deleted
                        || deleted.Offset != TimeSpan.Zero
                        || deleted > DateTimeOffset.UtcNow.AddSeconds(5)
                        || deleted < entry.EndTime + RecordingRetention.KeepFor
                    )
                )
            )
                throw new InvalidDataException("원본 보관 정리 정보가 올바르지 않습니다.");
        }
        else if (entry.DeletedAt is not null || entry.DeletionReason is not null)
            throw new InvalidDataException("삭제되지 않은 녹화에 삭제 정보가 있습니다.");
        return expected;
    }
}
