// 파일 역할: 클립 결과와 프레임 단위 추출 계획을 정의합니다. 서버 사본과 클라이언트 임시 출력 위치를 분리합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;

namespace AccessControl.Faces;

public sealed record ClipInfo
{
    public Guid ClipId { get; init; }
    public Guid? EventId { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public string FilePath { get; init; } = "";
    public string Status { get; init; } = "EXPORTING";
    public long? FileSizeBytes { get; init; }
    public string? FileHash { get; init; }
    public int FrameCount { get; init; }
    public double DurationSeconds { get; init; }
}

public sealed record ClipPart(string FilePath, int StartFrame, int FrameCount);

public sealed record ClipJob(ClipInfo Clip, int Width, int Height, int Fps, List<ClipPart> Parts);

public static class ClipFiles
{
    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessControlSystem",
            "clips"
        );
    public static string AgentRoot => Path.Combine(Root, "client_temp");

    public static string AgentPathFor(Guid id) =>
        Path.Combine(AgentRoot, id.ToString("N") + ".mp4");

    public static string PathFor(Guid id) => Path.Combine(Root, id.ToString("N") + ".mp4");

    public static void Validate(ClipInfo c)
    {
        if (
            c.ClipId == Guid.Empty
            || c.EventId == Guid.Empty
            || c.StartTime == default
            || c.StartTime.Offset != TimeSpan.Zero
            || c.EndTime.Offset != TimeSpan.Zero
            || c.EndTime <= c.StartTime
            || (c.EndTime - c.StartTime).TotalSeconds > 300
            || c.EndTime > DateTimeOffset.UtcNow.AddSeconds(5)
            || c.FileSizeBytes is < 0
            || !double.IsFinite(c.DurationSeconds)
            || c.Status is not ("EXPORTING" or "READY" or "FAILED")
            || (
                !string.Equals(
                    c.FilePath,
                    PathFor(c.ClipId),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                )
                && !string.Equals(
                    c.FilePath,
                    AgentPathFor(c.ClipId),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                )
            )
        )
            throw new InvalidDataException(
                "클립 시각·길이·파일 위치를 확인해 주세요. 최대 5분입니다."
            );
        if (
            c.Status == "READY"
            && (
                c.FileSizeBytes is null or <= 0 or > 500_000_000
                || c.FrameCount is < 1 or > 3001
                || c.FileHash is not { Length: 64 }
                || !c.FileHash.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')
                || !double.IsFinite(c.DurationSeconds)
                || Math.Abs(c.DurationSeconds - c.FrameCount / 10.0) > .02
                || Math.Abs(c.DurationSeconds - (c.EndTime - c.StartTime).TotalSeconds) > 1
            )
        )
            throw new InvalidDataException("완료된 클립의 길이·크기·해시가 올바르지 않습니다.");
    }

    // 선택 시간을 실제 녹화 프레임 범위로 바꿉니다. 녹화 공백이나 해상도 변경이 크면 추출을 거부합니다.
    public static ClipJob Plan(
        IEnumerable<RecordingEntry> entries,
        DateTimeOffset start,
        DateTimeOffset end,
        Guid? eventId = null
    )
    {
        var clip = new ClipInfo
        {
            ClipId = Guid.NewGuid(),
            EventId = eventId,
            StartTime = start.ToUniversalTime(),
            EndTime = end.ToUniversalTime()
        };
        clip = clip with { FilePath = PathFor(clip.ClipId) };
        Validate(clip);
        var parts = new List<ClipPart>();
        var cursor = start;
        int width = 0,
            height = 0;
        bool began = false;
        foreach (
            var e in entries
                .Where(e => e.Status == "READY" && e.EndTime is not null)
                .OrderBy(e => e.StartTime)
        )
        {
            if (e.EndTime <= cursor || e.StartTime >= end)
                continue;
            RecordingFiles.Validate(e);
            if (e.StartTime > cursor && (!began || (e.StartTime - cursor).TotalSeconds > .25))
                throw new InvalidDataException("선택 범위에 녹화가 없는 구간이 있습니다.");
            if (width == 0)
            {
                width = e.Width;
                height = e.Height;
            }

            if (e.Width != width || e.Height != height)
                throw new InvalidDataException("해상도가 다른 녹화는 나누어 추출해 주세요.");
            var from = cursor > e.StartTime ? cursor : e.StartTime;
            var to = end < e.EndTime!.Value ? end : e.EndTime.Value;
            int first = Math.Clamp(
                (int)Math.Ceiling((from - e.StartTime).TotalSeconds * 10 - 1e-6),
                0,
                e.FrameCount
            );
            int last = Math.Clamp(
                (int)Math.Ceiling((to - e.StartTime).TotalSeconds * 10 - 1e-6),
                0,
                e.FrameCount
            );
            if (last > first)
                parts.Add(new(e.FilePath, first, last - first));
            cursor = to;
            began = true;
            if (cursor >= end)
                break;
        }

        if (cursor < end || parts.Count == 0 || parts.Count > 64)
            throw new InvalidDataException(
                "선택 범위를 채우는 완료 녹화가 없습니다. 범위를 줄여 주세요."
            );
        int frames = parts.Sum(p => p.FrameCount);
        if (Math.Abs(frames / 10.0 - (end - start).TotalSeconds) > 1)
            throw new InvalidDataException(
                "녹화 간격이 많아 시간 정합성을 유지할 수 없습니다. 범위를 줄여 주세요."
            );
        return new(clip, width, height, 10, parts);
    }
}
