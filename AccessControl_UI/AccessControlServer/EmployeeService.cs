// 파일 역할: 직원 검색·추가·수정과 출입 권한 설정을 SQL 매개변수로 처리합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using Npgsql;
using NpgsqlTypes;

namespace AccessControlServer;

public sealed record DatabaseEndpoint(string Host, int Port, string Database, string Username);

public sealed record EmployeeRow(
    Guid Id,
    string EmployeeNo,
    string Name,
    string? Department,
    string? Position,
    bool IsActive,
    bool AccessEnabled,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt = null,
    DateTime? DeleteAfter = null,
    DateTime? PurgedAt = null
);

public static class EmployeeService
{
    private const string Columns =
        "person_id,employee_no,name,department,position,is_active,access_enabled,created_at,updated_at,(to_jsonb(p)->>'deleted_at')::timestamptz,(to_jsonb(p)->>'delete_after')::timestamptz,(to_jsonb(p)->>'purged_at')::timestamptz";

    internal static NpgsqlConnection Connection(DatabaseEndpoint endpoint, string password) =>
        new(
            new NpgsqlConnectionStringBuilder
            {
                Host = endpoint.Host,
                Port = endpoint.Port,
                Database = endpoint.Database,
                Username = endpoint.Username,
                Password = password,
                Pooling = false,
                Timeout = 5,
                CommandTimeout = 10,
                IncludeErrorDetail = false,
                ApplicationName = "AccessControlServer"
            }.ConnectionString
        );

    private static EmployeeRow Read(NpgsqlDataReader r) =>
        new(
            r.GetGuid(0),
            r.GetString(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.GetBoolean(5),
            r.GetBoolean(6),
            r.GetDateTime(7),
            r.GetDateTime(8),
            r.IsDBNull(9) ? null : r.GetDateTime(9),
            r.IsDBNull(10) ? null : r.GetDateTime(10),
            r.IsDBNull(11) ? null : r.GetDateTime(11)
        );

    public static async Task<List<EmployeeRow>> ListAsync(
        DatabaseEndpoint endpoint,
        string password,
        string search,
        CancellationToken token,
        bool includeDeleted = false
    )
    {
        await using var conn = Connection(endpoint, password);
        await conn.OpenAsync(token);
        // Literal substring matching: %, _ and quotes in search text are not interpreted as SQL.
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM access_control.persons p WHERE (to_jsonb(p)->>'purged_at') IS NULL AND (@include_deleted OR (to_jsonb(p)->>'deleted_at') IS NULL) AND (@search='' OR strpos(lower(employee_no),lower(@search))>0 OR strpos(lower(name),lower(@search))>0) ORDER BY employee_no LIMIT 500",
            conn
        );
        cmd.Parameters.AddWithValue("include_deleted", includeDeleted);
        cmd.Parameters.AddWithValue("search", NpgsqlDbType.Text, search.Trim());
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<EmployeeRow>();
        while (await reader.ReadAsync(token))
            rows.Add(Read(reader));
        return rows;
    }

    public static async Task<EmployeeRow> SaveAsync(
        DatabaseEndpoint endpoint,
        string password,
        EmployeeRow? original,
        string employeeNo,
        string name,
        string? department,
        string? position,
        bool active,
        bool access,
        CancellationToken token
    )
    {
        await using var conn = Connection(endpoint, password);
        await conn.OpenAsync(token);
        var sql =
            original == null
                ? $"INSERT INTO access_control.persons AS p(employee_no,name,department,position,is_active,access_enabled) VALUES (@no,@name,@dept,@position,@active,@access) RETURNING {Columns}"
                : $"UPDATE access_control.persons p SET employee_no=@no,name=@name,department=@dept,position=@position,is_active=@active,access_enabled=@access WHERE person_id=@id AND updated_at=@version AND (to_jsonb(p)->>'deleted_at') IS NULL RETURNING {Columns}";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("no", NpgsqlDbType.Text, employeeNo.Trim());
        cmd.Parameters.AddWithValue("name", NpgsqlDbType.Text, name.Trim());
        cmd.Parameters.AddWithValue(
            "dept",
            NpgsqlDbType.Text,
            (object?)NullIfBlank(department) ?? DBNull.Value
        );
        cmd.Parameters.AddWithValue(
            "position",
            NpgsqlDbType.Text,
            (object?)NullIfBlank(position) ?? DBNull.Value
        );
        cmd.Parameters.AddWithValue("active", active);
        cmd.Parameters.AddWithValue("access", access);
        if (original != null)
        {
            cmd.Parameters.AddWithValue("id", original.Id);
            cmd.Parameters.AddWithValue("version", NpgsqlDbType.TimestampTz, original.UpdatedAt);
        }

        await using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new InvalidOperationException(
                "다른 작업에서 직원 정보가 변경됐습니다. 다시 조회하고 수정해 주세요."
            );
        return Read(reader);
    }

    public static Task DeleteAsync(DatabaseEndpoint endpoint, string password, EmployeeRow original, CancellationToken token) =>
        EmployeeDeletionService.ChangeAsync(endpoint, password, original, "schedule", token);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
