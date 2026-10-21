// 파일 역할: 클립 추출 시작·완료 상태를 DB에 반영합니다. 성공 클립은 실제 파일 크기와 SHA-256 해시를 확인합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AccessControl.Faces;
using Npgsql;
using NpgsqlTypes;

namespace AccessControlServer;

public sealed partial class FaceDatabase
{
    public async Task<Guid> BeginClipAsync(ClipInfo clip, CancellationToken token)
    {
        ClipFiles.Validate(clip);
        if (clip.Status != "EXPORTING")
            throw new InvalidDataException("새 추출 요청이 아닙니다.");
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO access_control.exported_clips(clip_id,camera_id,event_id,exported_by,start_time,end_time,file_path,status)
            SELECT @id,c.camera_id,@event,a.admin_id,@start,@end,@path,'EXPORTING'
            FROM access_control.cameras c CROSS JOIN access_control.admin_accounts a
            WHERE c.camera_code='CAM-01' AND c.purpose='AUTHENTICATION' AND a.admin_id=@actor AND a.is_active
              AND (@event IS NULL OR EXISTS(SELECT 1 FROM access_control.access_events e
                WHERE e.event_id=@event AND e.camera_id=c.camera_id AND e.event_time>=@start AND e.event_time<@end))
            ON CONFLICT(clip_id) DO NOTHING RETURNING clip_id
            """,
            connection
        );
        ClipParameters(cmd, clip);
        cmd.Parameters.AddWithValue("actor", Actor);
        var id = await cmd.ExecuteScalarAsync(token);
        if (id is Guid saved)
            return saved;
        await using var existing = new NpgsqlCommand(
            "SELECT clip_id FROM access_control.exported_clips WHERE clip_id=@id AND start_time=@start AND end_time=@end AND file_path=@path AND event_id IS NOT DISTINCT FROM @event",
            connection
        );
        ClipParameters(existing, clip);
        id = await existing.ExecuteScalarAsync(token);
        return id is Guid same
            ? same
            : throw new InvalidOperationException(
                "서버의 활성 관리자를 선택하세요. 기록 연결 시 추출 범위에 해당 출입 시각이 포함돼야 합니다."
            );
    }

    // READY는 파일과 해시를 검증하고, FAILED는 실패 메타데이터를 확인해 종료 상태를 반영합니다.
    public async Task<Guid> CompleteClipAsync(ClipInfo clip, CancellationToken token)
    {
        ClipFiles.Validate(clip);
        if (clip.Status is not ("READY" or "FAILED"))
            throw new InvalidDataException("완료 상태가 아닙니다.");
        string manifest = Path.ChangeExtension(clip.FilePath, ".json");
        var stored = JsonSerializer.Deserialize<ClipInfo>(
            await RecordingFiles.ReadManifestAsync(manifest, token)
        );
        if (stored != clip)
            throw new InvalidDataException("클립 결과 정보가 일치하지 않습니다.");
        if (clip.Status == "READY")
        {
            if (
                !File.Exists(clip.FilePath)
                || new FileInfo(clip.FilePath).Length != clip.FileSizeBytes
            )
                throw new InvalidDataException("클립 파일 크기가 일치하지 않습니다.");
            await using var stream = File.OpenRead(clip.FilePath);
            string hash = Convert
                .ToHexString(await SHA256.HashDataAsync(stream, token))
                .ToLowerInvariant();
            if (hash != clip.FileHash)
                throw new InvalidDataException("클립 해시가 일치하지 않습니다.");
        }

        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE access_control.exported_clips SET status=@status,file_size_bytes=@size,file_hash=@hash,
                completed_at=COALESCE(completed_at,now())
            WHERE clip_id=@id AND start_time=@start AND end_time=@end AND file_path=@path
                AND event_id IS NOT DISTINCT FROM @event
                AND (status='EXPORTING' OR (status=@status AND file_size_bytes IS NOT DISTINCT FROM @size AND file_hash IS NOT DISTINCT FROM @hash))
            RETURNING clip_id
            """,
            connection
        );
        ClipParameters(cmd, clip);
        cmd.Parameters.AddWithValue("status", clip.Status);
        cmd.Parameters.Add("size", NpgsqlDbType.Bigint).Value =
            (object?)clip.FileSizeBytes ?? DBNull.Value;
        cmd.Parameters.Add("hash", NpgsqlDbType.Text).Value =
            (object?)clip.FileHash ?? DBNull.Value;
        return await cmd.ExecuteScalarAsync(token) is Guid id
            ? id
            : throw new InvalidOperationException(
                "이 DB에서 원래 클립 추출 요청을 찾을 수 없거나 상태가 다릅니다."
            );
    }

    private static void ClipParameters(NpgsqlCommand cmd, ClipInfo clip)
    {
        cmd.Parameters.AddWithValue("id", clip.ClipId);
        cmd.Parameters.AddWithValue("start", clip.StartTime.UtcDateTime);
        cmd.Parameters.AddWithValue("end", clip.EndTime.UtcDateTime);
        cmd.Parameters.AddWithValue("path", clip.FilePath);
        cmd.Parameters.Add("event", NpgsqlDbType.Uuid).Value =
            (object?)clip.EventId ?? DBNull.Value;
    }
}
