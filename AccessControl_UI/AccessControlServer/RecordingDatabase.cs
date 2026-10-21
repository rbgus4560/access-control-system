// 파일 역할: 녹화 메타데이터 저장·목록 조회를 담당합니다. 원본 파일 대신 클라이언트 위치와 구간 상태를 DB에 기록합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Text.Json;
using AccessControl.Faces;
using Npgsql;
using NpgsqlTypes;

namespace AccessControlServer;

public sealed record RecordingRow(
    Guid 구간ID,
    string 카메라,
    string 시작시각,
    string? 종료시각,
    string 상태,
    long? 바이트,
    string 파일위치,
    string? 삭제시각,
    string? 삭제사유
);

public sealed partial class FaceDatabase
{
    public Guid DatabaseSession { get; } = Guid.NewGuid();

    // 같은 구간의 경로와 허용된 상태 전이를 검증합니다. 검증된 과거 로컬 경로만 원격 위치로 전환합니다.
    public async Task<Guid> SaveRecordingAsync(
        RecordingEntry entry,
        CancellationToken token,
        bool remote = false
    )
    {
        string path;
        if (remote)
        {
            RecordingFiles.Validate(entry with { FilePath = RecordingFiles.PathFor(entry) });
            if (entry.Status == "DELETING")
                throw new InvalidDataException("삭제 진행 중");
            path = "client://CAM-01/" + entry.SegmentId.ToString("N");
        }
        else
        {
            path = RecordingFiles.Validate(entry);
            if (entry.Status == "DELETING")
                throw new InvalidDataException(
                    "로컬 삭제 진행 중입니다. 완료 후 다시 동기화합니다."
                );
            RecordingRetention.NoLinks(path);
            var manifest = new FileInfo(Path.ChangeExtension(path, ".json"));
            if (!manifest.Exists || manifest.Length > 8192)
                throw new InvalidDataException("녹화 구간 메타데이터 파일이 없습니다.");
            var stored = JsonSerializer.Deserialize<RecordingEntry>(
                await RecordingFiles.ReadManifestAsync(manifest.FullName, token)
            );
            if (stored != entry)
                throw new InvalidOperationException(
                    "녹화 구간 상태가 변경됐습니다. 다음 동기화에서 다시 확인합니다."
                );
            if (
                entry.Status == "READY"
                && (!File.Exists(path) || new FileInfo(path).Length != entry.FileSizeBytes)
            )
                throw new InvalidDataException("완료된 녹화 파일의 실제 크기가 일치하지 않습니다.");
            if (entry.Status == "DELETED" && File.Exists(path))
                throw new InvalidDataException(
                    "원본 파일이 남아 있어 삭제 완료로 기록하지 않습니다."
                );
        }

        string metadata = JsonSerializer.Serialize(entry with { FilePath = path });
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO access_control.recording_segments AS r
                (segment_id,camera_id,start_time,end_time,file_path,status,file_size_bytes,deleted_at,deletion_reason,client_metadata)
            SELECT @id,c.camera_id,@start,@end,@path,@status,@size,@deleted,@reason,@meta
            FROM access_control.cameras c JOIN access_control.client_instances i ON i.client_id=c.client_id
            WHERE c.camera_code='CAM-01' AND c.purpose='AUTHENTICATION'
                AND i.client_code='RECOGNITION-01' AND i.app_type='RECOGNITION'
            ON CONFLICT (segment_id) DO UPDATE
                SET file_path=EXCLUDED.file_path,end_time=EXCLUDED.end_time,status=EXCLUDED.status,file_size_bytes=EXCLUDED.file_size_bytes,
                    deleted_at=EXCLUDED.deleted_at,deletion_reason=EXCLUDED.deletion_reason,client_metadata=EXCLUDED.client_metadata
            WHERE r.camera_id=EXCLUDED.camera_id AND r.start_time=EXCLUDED.start_time
                -- Upgrade only the canonical legacy path on this server. Never accept an arbitrary client path.
                AND (r.file_path=EXCLUDED.file_path OR (@remote AND r.file_path=@legacy_path))
                AND (r.status='RECORDING' OR
                    (r.status='READY' AND EXCLUDED.status='DELETED'
                     AND r.end_time IS NOT DISTINCT FROM EXCLUDED.end_time
                     AND r.file_size_bytes IS NOT DISTINCT FROM EXCLUDED.file_size_bytes) OR
                    (r.status=EXCLUDED.status AND r.end_time IS NOT DISTINCT FROM EXCLUDED.end_time
                     AND r.file_size_bytes IS NOT DISTINCT FROM EXCLUDED.file_size_bytes
                     AND r.deleted_at IS NOT DISTINCT FROM EXCLUDED.deleted_at
                     AND r.deletion_reason IS NOT DISTINCT FROM EXCLUDED.deletion_reason))
            RETURNING segment_id
            """,
            connection
        );
        cmd.Parameters.AddWithValue("id", entry.SegmentId);
        cmd.Parameters.AddWithValue("start", entry.StartTime.UtcDateTime);
        cmd.Parameters.Add("end", NpgsqlDbType.TimestampTz).Value = entry.EndTime is { } end
            ? end.UtcDateTime
            : DBNull.Value;
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("status", entry.Status);
        cmd.Parameters.Add("size", NpgsqlDbType.Bigint).Value = entry.FileSizeBytes is { } size
            ? size
            : DBNull.Value;
        cmd.Parameters.Add("deleted", NpgsqlDbType.TimestampTz).Value = entry.DeletedAt
            is { } deleted
            ? deleted.UtcDateTime
            : DBNull.Value;
        cmd.Parameters.Add("reason", NpgsqlDbType.Text).Value =
            (object?)entry.DeletionReason ?? DBNull.Value;
        cmd.Parameters.AddWithValue("meta", NpgsqlDbType.Jsonb, metadata);
        cmd.Parameters.AddWithValue("remote", remote);
        cmd.Parameters.AddWithValue("legacy_path", RecordingFiles.PathFor(entry));
        var id = await cmd.ExecuteScalarAsync(token);
        if (id is Guid saved)
            return saved;
        throw new InvalidOperationException(
            $"녹화 {entry.SegmentId:N}: 카메라 등록, 기존 시작 시각·경로 또는 상태가 일치하지 않습니다. 원본과 DB를 보존한 채 서버 오류 로그를 확인하세요."
        );
    }

    public async Task<List<RecordingRow>> RecordingsAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.segment_id,c.camera_code,r.start_time,r.end_time,r.status,r.file_size_bytes,r.file_path,r.deleted_at,r.deletion_reason
            FROM access_control.recording_segments r JOIN access_control.cameras c ON c.camera_id=r.camera_id
            ORDER BY r.start_time DESC LIMIT 500
            """,
            connection
        );
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<RecordingRow>();
        while (await reader.ReadAsync(token))
            rows.Add(
                new(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetDateTime(2).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    reader.IsDBNull(3)
                        ? null
                        : reader.GetDateTime(3).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetString(6),
                    reader.IsDBNull(7)
                        ? null
                        : reader.GetDateTime(7).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    reader.IsDBNull(8) ? null : reader.GetString(8)
                )
            );
        return rows;
    }

    // DB 메타데이터와 원본/캐시 정보를 대조하여 선택 날짜에서 재생할 완료 구간을 찾습니다.
    public async Task<RecordingDay> RecordingDayAsync(DateTime date, CancellationToken token)
    {
        var start = RecordingTimeline.LocalMidnight(date);
        var end = RecordingTimeline.LocalMidnight(date.AddDays(1));
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.segment_id,r.start_time,r.end_time,r.file_size_bytes,r.file_path,r.client_metadata::text
            FROM access_control.recording_segments r JOIN access_control.cameras c ON c.camera_id=r.camera_id
            WHERE c.camera_code='CAM-01' AND r.status='READY' AND r.start_time<@end AND r.end_time>@start
            ORDER BY r.start_time LIMIT 2000
            """,
            connection
        );
        cmd.Parameters.AddWithValue("start", start.UtcDateTime);
        cmd.Parameters.AddWithValue("end", end.UtcDateTime);
        var entries = new List<RecordingEntry>();
        int unavailable = 0;
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            try
            {
                var id = reader.GetGuid(0);
                var begin = new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)
                );
                var finish = new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)
                );
                var size = reader.GetInt64(3);
                var storedPath = reader.GetString(4);
                RecordingEntry? entry;
                bool remote = storedPath == "client://CAM-01/" + id.ToString("N");
                if (remote)
                {
                    entry = reader.IsDBNull(5)
                        ? null
                        : JsonSerializer.Deserialize<RecordingEntry>(reader.GetString(5));
                    if (entry is null || entry.FilePath != storedPath)
                        throw new InvalidDataException();
                    entry = entry with
                    {
                        FilePath = RecordingFiles.PathFor(entry, RecordingFiles.CacheRoot)
                    };
                    if (RecordingRetention.IsExpired(entry, DateTimeOffset.UtcNow))
                        continue;
                }
                else
                {
                    // Retain access to legacy originals on the existing school server PC.
                    var probe = new RecordingEntry { SegmentId = id, StartTime = begin };
                    if (
                        !string.Equals(
                            storedPath,
                            RecordingFiles.PathFor(probe),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        throw new InvalidDataException();
                    entry = JsonSerializer.Deserialize<RecordingEntry>(
                        await RecordingFiles.ReadManifestAsync(
                            Path.ChangeExtension(storedPath, ".json"),
                            token
                        )
                    );
                }

                if (
                    entry is null
                    || entry.SegmentId != id
                    || entry.StartTime != begin
                    || entry.EndTime != finish
                    || entry.FileSizeBytes != size
                    || entry.Status != "READY"
                )
                    throw new InvalidDataException();
                RecordingFiles.Validate(entry);
                entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
            {
                unavailable++;
            }
        }

        return new(start, end, entries, unavailable);
    }
}
