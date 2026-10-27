using Npgsql;
using NpgsqlTypes;

namespace AccessControlServer;

// 삭제 대기·복원과 명시적으로 확인한 직원의 DB 개인정보/얼굴 특징 영구 삭제를 처리합니다.
public static class EmployeeDeletionService
{
    public const int GraceDays = 7;

    public static async Task ChangeAsync(DatabaseEndpoint endpoint, string password, EmployeeRow original, string operation, CancellationToken token)
    {
        await using var connection = EmployeeService.Connection(endpoint, password);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var check = new NpgsqlCommand("SELECT count(*)=2 FROM information_schema.columns WHERE table_schema='access_control' AND table_name='persons' AND column_name IN ('deleted_at','delete_after')", connection, transaction))
            if (await check.ExecuteScalarAsync(token) is not true)
                throw new InvalidOperationException("Database/17_employee_delete_lifecycle.sql을 대상 DB에 먼저 적용해 주세요.");
        string sql = operation switch
        {
            "schedule" => "UPDATE access_control.persons SET deleted_at=now(),delete_after=now()+interval '7 days',is_active=false,access_enabled=false,updated_at=now() WHERE person_id=@id AND updated_at=@version AND deleted_at IS NULL",
            "restore" => "UPDATE access_control.persons SET deleted_at=NULL,delete_after=NULL,is_active=false,access_enabled=false,updated_at=now() WHERE person_id=@id AND updated_at=@version AND deleted_at IS NOT NULL AND (to_jsonb(persons)->>'purged_at') IS NULL",
            _ => throw new ArgumentException("지원하지 않는 작업입니다.")
        };
        await using (var update = new NpgsqlCommand(sql, connection, transaction))
        {
            update.Parameters.AddWithValue("id", original.Id);
            update.Parameters.AddWithValue("version", NpgsqlDbType.TimestampTz, original.UpdatedAt);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("직원 정보나 삭제 상태가 변경됐습니다. 새로고침 후 다시 시도해 주세요.");
        }
        if (operation == "schedule")
        {
            await using var faces = new NpgsqlCommand("UPDATE access_control.face_enrollments SET is_active=false,deactivated_at=now() WHERE person_id=@id AND is_active", connection, transaction);
            faces.Parameters.AddWithValue("id", original.Id);
            await faces.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
    public static async Task PurgeAsync(DatabaseEndpoint endpoint, string password, EmployeeRow original, CancellationToken token)
    {
        if (original.DeletedAt is null) throw new InvalidOperationException("먼저 삭제 대기로 전환해 주세요.");
        await using var connection = EmployeeService.Connection(endpoint, password);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        // 기록의 외래키를 보존하기 위해 직원 식별 행은 익명 표식으로 남깁니다.
        await using (var update = new NpgsqlCommand(@"
UPDATE access_control.persons SET employee_no='DELETED-' || person_id::text,
 name='삭제된 직원',department=NULL,position=NULL,is_active=false,access_enabled=false,
 purged_at=now(),delete_after=NULL,updated_at=now()
WHERE person_id=@id AND updated_at=@version AND deleted_at IS NOT NULL AND purged_at IS NULL", connection, transaction))
        {
            update.Parameters.AddWithValue("id", original.Id);
            update.Parameters.AddWithValue("version", NpgsqlDbType.TimestampTz, original.UpdatedAt);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("직원 상태가 변경됐습니다. 새로고침 후 확인해 주세요.");
        }
        await using (var faces = new NpgsqlCommand(@"
DELETE FROM access_control.face_templates WHERE enrollment_id IN
 (SELECT enrollment_id FROM access_control.face_enrollments WHERE person_id=@id);
UPDATE access_control.face_enrollments SET is_active=false,deactivated_at=COALESCE(deactivated_at,now()) WHERE person_id=@id;", connection, transaction))
        {
            faces.Parameters.AddWithValue("id", original.Id);
            await faces.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
}
