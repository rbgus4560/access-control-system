// 파일 역할: 날짜별 원본 메타데이터와 시간 입력을 해석해 재생 가능한 구간을 구성합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AccessControl.Faces;

public sealed record RecordingDay(
    DateTimeOffset Start,
    DateTimeOffset End,
    List<RecordingEntry> Segments,
    int Unavailable
);

public static class RecordingTimeline
{
    public static DateTimeOffset LocalMidnight(DateTime date) =>
        new(DateTime.SpecifyKind(date.Date, DateTimeKind.Local));

    public static bool TryTime(DateTime date, string time, out DateTimeOffset instant)
    {
        instant = default;
        if (
            !DateTime.TryParseExact(
                time.Trim(),
                new[] { "HH:mm:ss", "HH:mm:ss.fff" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var clock
            )
        )
            return false;
        var local = DateTime.SpecifyKind(date.Date + clock.TimeOfDay, DateTimeKind.Local);
        if (TimeZoneInfo.Local.IsInvalidTime(local))
            return false;
        instant = new DateTimeOffset(local);
        return true;
    }

    public static RecordingEntry? FindAt(IEnumerable<RecordingEntry> entries, DateTimeOffset at) =>
        entries
            .Where(e =>
                e.Status == "READY" && e.EndTime is { } end && e.StartTime <= at && at < end
            )
            .OrderByDescending(e => e.StartTime)
            .FirstOrDefault();

    public static RecordingEntry? Next(
        IEnumerable<RecordingEntry> entries,
        RecordingEntry current
    ) =>
        entries
            .Where(e =>
                e.Status == "READY"
                && e.SegmentId != current.SegmentId
                && current.EndTime is { } end
                && e.StartTime >= end
            )
            .OrderBy(e => e.StartTime)
            .FirstOrDefault();

    public static bool Continuous(RecordingEntry current, RecordingEntry next) =>
        current.EndTime is { } end
        && next.StartTime >= end
        && (next.StartTime - end).TotalSeconds <= .25;

    public static async Task<RecordingDay> ReadAsync(DateTime date, CancellationToken token)
    {
        var start = LocalMidnight(date);
        var end = LocalMidnight(date.AddDays(1));
        var entries = new List<RecordingEntry>();
        int unavailable = 0;
        // A local day can intersect two UTC date folders (three with time-zone transitions).
        for (var utc = start.UtcDateTime.Date; utc <= end.UtcDateTime.Date; utc = utc.AddDays(1))
        {
            string folder = Path.Combine(
                RecordingFiles.Root,
                utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            );
            if (!Directory.Exists(folder))
                continue;
            foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var entry =
                        JsonSerializer.Deserialize<RecordingEntry>(
                            await RecordingFiles.ReadManifestAsync(path, token)
                        ) ?? throw new InvalidDataException();
                    RecordingFiles.Validate(entry);
                    if (
                        entry.Status != "READY"
                        || entry.EndTime is not { } finished
                        || finished <= start
                        || entry.StartTime >= end
                    )
                        continue;
                    if (
                        !string.Equals(
                            path,
                            Path.ChangeExtension(entry.FilePath, ".json"),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new InvalidDataException();
                    var file = new FileInfo(entry.FilePath);
                    if (!file.Exists || file.Length != entry.FileSizeBytes)
                        throw new InvalidDataException();
                    entries.Add(entry);
                }
                catch (Exception ex)
                    when (ex
                            is IOException
                                or JsonException
                                or InvalidDataException
                                or ArgumentException
                    )
                {
                    unavailable++;
                }
            }
        }

        return new(start, end, entries.OrderBy(e => e.StartTime).ToList(), unavailable);
    }
}
