// 파일 역할: 식별 토큰과 현재 DB 권한을 확인해 최종 판정합니다. 같은 인증 시도의 중복 기록을 방지합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Collections.Concurrent;
using AccessControl.Network;
using Npgsql;
using NpgsqlTypes;

namespace AccessControlServer;

public sealed record AccessEventRow(
    string 시각,
    string 카메라,
    string 사원번호,
    string 이름,
    string 결과,
    string 사유,
    string 거리,
    Guid 이벤트ID
);

public sealed partial class FaceDatabase
{
    private readonly ConcurrentDictionary<string, (Guid Person, Guid Enrollment)> leases = new();
    private readonly Dictionary<string, string> leaseKeys = new();

    private string IssueLease(Guid person, Guid enrollment)
    {
        // Reuse a lease for this DB session and registration. Polling the catalog must
        // not evict another employee's active lease or change an encounter ID.
        string identityKey = $"{person:N}:{enrollment:N}";
        lock (leases)
        {
            if (leaseKeys.TryGetValue(identityKey, out var existing))
                return existing;
            string key = Guid.NewGuid().ToString("N");
            leases[key] = (person, enrollment);
            leaseKeys[identityKey] = key;
            return key;
        }
    }

    // 점수·토큰·최신성·DB 권한을 확인하고 DB 기록 완료 후에만 허가를 반환합니다.
    public async Task<AccessDecision> DecideAsync(MonitorPacket packet, CancellationToken token)
    {
        var snapshot = packet.Snapshot!;
        var waiting = new AccessDecision { AttemptId = snapshot.AttemptId };
        if (
            snapshot.Status is not ("MATCH_CANDIDATE" or "NO_MATCH")
            || snapshot.AttemptId is not { } attempt
        )
            return waiting with
            {
                Reason = snapshot.Reason == "AMBIGUOUS_MATCH" ? "AMBIGUOUS_MATCH" : "NO_COMPARISON"
            };
        if (
            snapshot.Status == "MATCH_CANDIDATE"
            && snapshot.RunnerUpDistance is { } second
            && snapshot.Distance is { } first
            && second - first < .05
        )
            return waiting with { Reason = "AMBIGUOUS_MATCH" };
        if (!AccessPolicy.IsCurrent(snapshot, DateTimeOffset.UtcNow))
            return waiting with { Reason = "STALE_ANALYSIS" };
        if (
            snapshot.Identity is not { } identity
            || !leases.TryGetValue(identity.Lease, out var lease)
            || lease.Person != identity.PersonId
            || lease.Enrollment != identity.EnrollmentId
        )
            return waiting with { Result = "ERROR", Reason = "DB_REFERENCE_REQUIRED" };
        double remaining =
            MonitorProtocol.MaxAnalysisAgeMs
            - snapshot.AnalysisAgeMs
            - Math.Max(0, (DateTimeOffset.UtcNow - snapshot.SentAt).TotalMilliseconds);
        if (remaining < 100)
            return waiting with { Reason = "STALE_ANALYSIS" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        try
        {
            var stop = deadline.Token;
            await using var connection = await OpenAsync(stop);
            await using var transaction = await connection.BeginTransactionAsync(stop);
            Guid camera;
            bool deviceEnabled;
            string deviceVersion;
            await using (
                var cmd = new NpgsqlCommand(
                    "SELECT c.camera_id,(c.is_enabled AND i.is_enabled),c.updated_at,i.updated_at FROM access_control.cameras c JOIN access_control.client_instances i ON i.client_id=c.client_id WHERE c.camera_code=@camera AND i.client_code=@client AND c.purpose='AUTHENTICATION' AND i.app_type='RECOGNITION' FOR SHARE OF c,i",
                    connection,
                    transaction
                )
            )
            {
                cmd.Parameters.AddWithValue("camera", packet.CameraCode);
                cmd.Parameters.AddWithValue("client", packet.ClientCode);
                await using var reader = await cmd.ExecuteReaderAsync(stop);
                if (!await reader.ReadAsync(stop))
                    return waiting with { Result = "ERROR", Reason = "DEVICE_DISABLED" };
                camera = reader.GetGuid(0);
                deviceEnabled = reader.GetBoolean(1);
                deviceVersion = $"{reader.GetDateTime(2).Ticks}:{reader.GetDateTime(3).Ticks}";
            }

            string employeeNo,
                name,
                personVersion;
            bool personActive,
                enrollmentActive,
                allowed;
            await using (
                var cmd = new NpgsqlCommand(
                    "SELECT p.employee_no,p.name,p.is_active,p.access_enabled,(e.is_active AND e.model_version=@model AND e.embedding_dimension=512),p.updated_at FROM access_control.persons p JOIN access_control.face_enrollments e ON e.person_id=p.person_id WHERE p.person_id=@person AND e.enrollment_id=@enrollment FOR SHARE OF p,e",
                    connection,
                    transaction
                )
            )
            {
                cmd.Parameters.AddWithValue("person", identity.PersonId);
                cmd.Parameters.AddWithValue("enrollment", identity.EnrollmentId);
                cmd.Parameters.AddWithValue("model", AccessControl.Faces.FaceApi.Pipeline);
                await using var reader = await cmd.ExecuteReaderAsync(stop);
                if (!await reader.ReadAsync(stop))
                    return waiting with { Result = "ERROR", Reason = "DB_REFERENCE_REQUIRED" };
                employeeNo = reader.GetString(0);
                name = reader.GetString(1);
                personActive = reader.GetBoolean(2);
                allowed = reader.GetBoolean(3);
                enrollmentActive = reader.GetBoolean(4);
                personVersion = reader
                    .GetDateTime(5)
                    .Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            bool matched = snapshot.Status == "MATCH_CANDIDATE";
            var (result, reason) = AccessPolicy.Evaluate(
                matched,
                personActive,
                enrollmentActive,
                allowed,
                deviceEnabled
            );
            if (!AccessPolicy.IsCurrent(snapshot, DateTimeOffset.UtcNow))
                return waiting with { Reason = "STALE_ANALYSIS" };
            Guid? person = matched ? identity.PersonId : null,
                enrollment = matched ? identity.EnrollmentId : null;
            Guid eventId = AccessPolicy.EventId(
                attempt,
                packet.CameraCode,
                result,
                reason,
                person,
                enrollment,
                deviceVersion + (matched ? ":" + personVersion : "")
            );
            bool isNew;
            await using (
                var cmd = new NpgsqlCommand(
                    "INSERT INTO access_control.access_events(event_id,camera_id,person_id,enrollment_id,event_time,result,reason_code,similarity,threshold) VALUES (@id,@camera,@person,@enrollment,@time,@result,@reason,@similarity,@threshold) ON CONFLICT(event_id) DO NOTHING RETURNING event_id",
                    connection,
                    transaction
                )
            )
            {
                cmd.Parameters.AddWithValue("id", eventId);
                cmd.Parameters.AddWithValue("camera", camera);
                cmd.Parameters.AddWithValue(
                    "person",
                    NpgsqlDbType.Uuid,
                    (object?)person ?? DBNull.Value
                );
                cmd.Parameters.AddWithValue(
                    "enrollment",
                    NpgsqlDbType.Uuid,
                    (object?)enrollment ?? DBNull.Value
                );
                cmd.Parameters.AddWithValue("time", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("result", result);
                cmd.Parameters.AddWithValue("reason", reason);
                // Existing schema stores cosine similarity, not cosine distance.
                cmd.Parameters.AddWithValue("similarity", 1 - snapshot.Distance!.Value);
                cmd.Parameters.AddWithValue("threshold", 1 - snapshot.Threshold);
                isNew = await cmd.ExecuteScalarAsync(stop) is not null;
            }

            DateTimeOffset eventTime;
            double recordedDistance;
            await using (
                var cmd = new NpgsqlCommand(
                    "SELECT event_time,result,reason_code,camera_id,person_id,enrollment_id,similarity FROM access_control.access_events WHERE event_id=@id",
                    connection,
                    transaction
                )
            )
            {
                cmd.Parameters.AddWithValue("id", eventId);
                await using var reader = await cmd.ExecuteReaderAsync(stop);
                if (
                    !await reader.ReadAsync(stop)
                    || reader.GetString(1) != result
                    || reader.GetString(2) != reason
                    || reader.GetGuid(3) != camera
                    || (reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4)) != person
                    || (reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5)) != enrollment
                )
                    throw new InvalidOperationException("이벤트 ID 충돌");
                eventTime = new DateTimeOffset(reader.GetDateTime(0));
                recordedDistance = 1 - reader.GetDouble(6);
            }

            await transaction.CommitAsync(stop);
            return new AccessDecision
            {
                Result = result,
                Reason = reason,
                AttemptId = attempt,
                EventId = eventId,
                Recorded = true,
                IsNew = isNew,
                EmployeeNo = matched ? employeeNo : "",
                PersonName = matched ? name : "미등록/불일치",
                EventTime = eventTime,
                RecordedDistance = recordedDistance
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return waiting with { Reason = "STALE_ANALYSIS" };
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return waiting with { Result = "ERROR", Reason = "DB_ERROR" };
        }
    }

    public async Task<List<AccessEventRow>> EventsAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            "SELECT e.event_time,c.camera_code,p.employee_no,p.name,e.result,e.reason_code,e.similarity,e.event_id FROM access_control.access_events e JOIN access_control.cameras c ON c.camera_id=e.camera_id LEFT JOIN access_control.persons p ON p.person_id=e.person_id ORDER BY e.event_time DESC,e.event_id LIMIT 500",
            connection
        );
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<AccessEventRow>();
        while (await reader.ReadAsync(token))
            rows.Add(
                new(
                    reader.GetDateTime(0).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "—" : reader.GetString(2),
                    reader.IsDBNull(3) ? "미등록/불일치" : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? "—" : (1 - reader.GetDouble(6)).ToString("0.000"),
                    reader.GetGuid(7)
                )
            );
        return rows;
    }
}
