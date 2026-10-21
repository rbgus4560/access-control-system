// 파일 역할: DB 연결 정보와 등록된 클라이언트·카메라 상태를 조회합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using Npgsql;

namespace AccessControlServer;

public sealed record ClientRow(string 코드, string 종류, string PC이름, bool 사용허용);

public sealed record CameraRow(string 코드, string 이름, string 용도, string 연결프로그램);

public sealed record DeviceSnapshot(
    string Database,
    List<ClientRow> Clients,
    List<CameraRow> Cameras
);

public static class DatabaseService
{
    public static async Task<DeviceSnapshot> LoadAsync(
        string host,
        int port,
        string database,
        string username,
        string password,
        CancellationToken token
    )
    {
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            Timeout = 5,
            CommandTimeout = 10,
            Pooling = false,
            ApplicationName = "AccessControlServer",
            IncludeErrorDetail = false
        };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync(token);
        // All three reads use one repeatable-read snapshot; no application data is changed.
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            token
        );
        string current;
        await using (
            var cmd = new NpgsqlCommand("SELECT current_database()", connection, transaction)
        )
            current = (string)(await cmd.ExecuteScalarAsync(token))!;
        var clients = new List<ClientRow>();
        await using (
            var cmd = new NpgsqlCommand(
                "SELECT client_code, app_type, machine_name, is_enabled FROM access_control.client_instances ORDER BY client_code",
                connection,
                transaction
            )
        )
        await using (var reader = await cmd.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                clients.Add(
                    new(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetBoolean(3)
                    )
                );
        var cameras = new List<CameraRow>();
        await using (
            var cmd = new NpgsqlCommand(
                "SELECT c.camera_code,c.name,c.purpose,i.client_code FROM access_control.cameras c JOIN access_control.client_instances i ON i.client_id=c.client_id ORDER BY c.camera_code",
                connection,
                transaction
            )
        )
        await using (var reader = await cmd.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                cameras.Add(
                    new(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)
                    )
                );
        await transaction.CommitAsync(token);
        return new(current, clients, cameras);
    }
}
