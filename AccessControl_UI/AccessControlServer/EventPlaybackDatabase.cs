// 파일 역할: 출입 이벤트의 시각과 카메라를 검증해 재생할 녹화 구간을 찾습니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using AccessControl.Faces;
using Npgsql;

namespace AccessControlServer;

public sealed partial class FaceDatabase
{
    public async Task<EventPlaybackRequest> PlaybackEventAsync(
        Guid eventId,
        CancellationToken token
    )
    {
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT a.event_id,c.camera_code,a.event_time,a.result,a.reason_code,p.employee_no,p.name
            FROM access_control.access_events a JOIN access_control.cameras c ON c.camera_id=a.camera_id
            LEFT JOIN access_control.persons p ON p.person_id=a.person_id
            WHERE a.event_id=@id
            """,
            connection
        );
        cmd.Parameters.AddWithValue("id", eventId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new InvalidOperationException("DB에서 선택한 출입 기록을 찾을 수 없습니다.");
        if (reader.GetString(1) != "CAM-01")
            throw new InvalidOperationException("현재 재생은 CAM-01 녹화만 지원합니다.");
        string person = reader.IsDBNull(5)
            ? "미등록/불일치"
            : reader.GetString(5) + " · " + reader.GetString(6);
        string summary = $"출입 기록 · {person} · {reader.GetString(3)} / {reader.GetString(4)}";
        return new EventPlaybackRequest
        {
            EventId = reader.GetGuid(0),
            CameraCode = reader.GetString(1),
            EventTime = new DateTimeOffset(
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)
            ),
            Summary = summary.Length > 200 ? summary[..200] : summary
        };
    }
}
