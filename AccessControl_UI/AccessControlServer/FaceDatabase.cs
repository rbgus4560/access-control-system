// 파일 역할: 직원 얼굴 등록·조회와 API 요청을 처리합니다. 얼굴 특징은 서버의 Windows 계정으로 암호화해 DB에 보관합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessControl.Faces;
using Npgsql;
using NpgsqlTypes;

namespace AccessControlServer;

public sealed record FaceAdmin(Guid Id, string Login, string Name)
{
    public override string ToString() => $"{Login} · {Name}";
}

public sealed partial class FaceDatabase
{
    private readonly string connectionString;
    private readonly Func<byte[], byte[]> protect,
        unprotect;
    private readonly object actorLock = new();
    private Guid actor;
    public Guid Actor
    {
        get
        {
            lock (actorLock)
                return actor;
        }
        set
        {
            lock (actorLock)
                actor = value;
        }
    }

    public FaceDatabase(
        DatabaseEndpoint endpoint,
        string password,
        Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null
    )
    {
        connectionString = new NpgsqlConnectionStringBuilder
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
            ApplicationName = "AccessControl.FaceDatabase"
        }.ConnectionString;
        // Windows DPAPI CurrentUser binds encrypted templates to this server's Windows account.
        byte[] purpose = Encoding.UTF8.GetBytes("AccessControl.FaceTemplates.v1");
        this.protect =
            protect
            ?? (bytes => ProtectedData.Protect(bytes, purpose, DataProtectionScope.CurrentUser));
        this.unprotect =
            unprotect
            ?? (bytes => ProtectedData.Unprotect(bytes, purpose, DataProtectionScope.CurrentUser));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<List<FaceAdmin>> AdminsAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            "SELECT admin_id,login_id,display_name FROM access_control.admin_accounts WHERE is_active ORDER BY login_id LIMIT 100",
            connection
        );
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<FaceAdmin>();
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    public async Task<FaceAdmin> CreateAdminAsync(
        string login,
        string name,
        string password,
        CancellationToken token
    )
    {
        if (
            string.IsNullOrWhiteSpace(login)
            || login.Length > 50
            || string.IsNullOrWhiteSpace(name)
            || name.Length > 100
            || password.Length < 10
        )
            throw new InvalidOperationException(
                "관리자 ID·이름과 10자 이상의 관리자 비밀번호를 입력해 주세요."
            );
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            210_000,
            HashAlgorithmName.SHA256,
            32
        );
        string encoded =
            $"pbkdf2-sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO access_control.admin_accounts(login_id,display_name,password_hash) VALUES (@login,@name,@hash) RETURNING admin_id",
            connection
        );
        cmd.Parameters.AddWithValue("login", login.Trim());
        cmd.Parameters.AddWithValue("name", name.Trim());
        cmd.Parameters.AddWithValue("hash", encoded);
        return new((Guid)(await cmd.ExecuteScalarAsync(token))!, login.Trim(), name.Trim());
    }

    public async Task<List<FaceEmployee>> EmployeesAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            "SELECT p.person_id,p.employee_no,p.name,p.is_active,p.access_enabled,e.enrollment_id FROM access_control.persons p LEFT JOIN access_control.face_enrollments e ON e.person_id=p.person_id AND e.is_active WHERE (to_jsonb(p)->>'deleted_at') IS NULL ORDER BY p.employee_no LIMIT 500",
            connection
        );
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<FaceEmployee>();
        while (await reader.ReadAsync(token))
            rows.Add(
                new(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5)
                )
            );
        return rows;
    }

    // 이전 등록 ID를 확인하고 새 특징 전체를 트랜잭션으로 저장합니다. 중간 실패 시 이전 등록을 유지합니다.
    public async Task<StoredFace> SaveAsync(FaceRequest request, CancellationToken token)
    {
        if (request.PersonId == Guid.Empty || request.Reference is not { } reference)
            throw new InvalidDataException("직원과 얼굴 특징을 선택해 주세요.");
        var poses = FaceApi.ReferencePoses(reference);
        Guid actorId = Actor;
        if (actorId == Guid.Empty)
            throw new InvalidOperationException(
                "서버에서 얼굴 등록 관리자를 선택하거나 관리자 추가를 해 주세요."
            );
        // Encrypt before opening the transaction; never write clear vectors to disk or DB.
        var encrypted = poses.ToDictionary(
            pose => pose,
            pose =>
                protect(
                    JsonSerializer.SerializeToUtf8Bytes(
                        reference.GetProperty("features").GetProperty(pose)
                    )
                )
        );
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        string employeeNo,
            name;
        bool active;
        await using (
            var cmd = new NpgsqlCommand(
                "SELECT employee_no,name,is_active FROM access_control.persons WHERE person_id=@id AND (to_jsonb(persons)->>'deleted_at') IS NULL FOR UPDATE",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("id", request.PersonId);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                throw new InvalidOperationException(
                    "직원을 찾을 수 없습니다. 목록을 다시 조회해 주세요."
                );
            employeeNo = reader.GetString(0);
            name = reader.GetString(1);
            active = reader.GetBoolean(2);
        }

        if (!active)
            throw new InvalidOperationException("비활성 직원에게 얼굴을 등록할 수 없습니다.");
        await using (
            var cmd = new NpgsqlCommand(
                "SELECT admin_id FROM access_control.admin_accounts WHERE admin_id=@id AND is_active FOR SHARE",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("id", actorId);
            if (await cmd.ExecuteScalarAsync(token) is null)
                throw new InvalidOperationException("활성 관리자를 다시 선택해 주세요.");
        }

        Guid? previous;
        await using (
            var cmd = new NpgsqlCommand(
                "SELECT enrollment_id FROM access_control.face_enrollments WHERE person_id=@person AND is_active",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("person", request.PersonId);
            previous = await cmd.ExecuteScalarAsync(token) as Guid?;
        }

        // A lost acknowledgement can be retried with the same request ID without a second registration.
        if (previous == request.RequestId)
        {
            await transaction.RollbackAsync(token);
            return await LoadAsync(request.PersonId, token);
        }

        if (previous != request.ExpectedEnrollmentId)
            throw new InvalidOperationException(
                "다른 작업에서 얼굴 등록이 변경됐습니다. 직원 목록을 다시 조회해 주세요."
            );
        await using (
            var cmd = new NpgsqlCommand(
                "UPDATE access_control.face_enrollments SET is_active=false,deactivated_at=now() WHERE person_id=@person AND is_active",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("person", request.PersonId);
            await cmd.ExecuteNonQueryAsync(token);
        }

        await using (
            var cmd = new NpgsqlCommand(
                "INSERT INTO access_control.face_enrollments(enrollment_id,person_id,model_version,embedding_dimension,enrolled_by) VALUES (@id,@person,@model,512,@actor)",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("id", request.RequestId);
            cmd.Parameters.AddWithValue("person", request.PersonId);
            cmd.Parameters.AddWithValue("model", FaceApi.Pipeline);
            cmd.Parameters.AddWithValue("actor", actorId);
            await cmd.ExecuteNonQueryAsync(token);
        }

        foreach (var pose in poses)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO access_control.face_templates(enrollment_id,pose,sample_no,embedding_encrypted,captured_at) VALUES (@id,@pose,@sample,@bytes,now())",
                connection,
                transaction
            );
            var sampleKey = FaceReferenceRules.SampleKey(pose);
            cmd.Parameters.AddWithValue("id", request.RequestId);
            cmd.Parameters.AddWithValue("pose", sampleKey.Pose);
            cmd.Parameters.AddWithValue("sample", sampleKey.Sample);
            cmd.Parameters.AddWithValue("bytes", NpgsqlDbType.Bytea, encrypted[pose]);
            try
            {
                await cmd.ExecuteNonQueryAsync(token);
            }
            catch (PostgresException ex)
                when (poses.Length == 9 && ex.SqlState == PostgresErrorCodes.CheckViolation)
            {
                throw new InvalidOperationException(
                    "DB 등록 제약 확장이 필요합니다. Database/14_enable_guided_samples.sql을 실행해 주세요. 기존 등록은 롤백해 유지합니다."
                );
            }
        }

        await using (
            var cmd = new NpgsqlCommand(
                "UPDATE access_control.face_enrollments SET is_active=true WHERE enrollment_id=@id",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("id", request.RequestId);
            await cmd.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        return new(request.PersonId, request.RequestId, employeeNo, name, reference.Clone());
    }

    // 동일한 DB 스냅샷에서 등록 정보와 암호화된 특징을 읽습니다. 서버가 복호화한 특징에 식별 토큰을 붙입니다.
    public async Task<StoredFace> LoadAsync(
        Guid personId,
        CancellationToken token,
        bool includeInactive = false
    )
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            token
        );
        Guid enrollment;
        string employeeNo,
            name;
        await using (
            var cmd = new NpgsqlCommand(
                "SELECT e.enrollment_id,p.employee_no,p.name FROM access_control.persons p JOIN access_control.face_enrollments e ON e.person_id=p.person_id AND e.is_active WHERE p.person_id=@person AND (p.is_active OR @include_inactive) AND e.model_version=@model AND e.embedding_dimension=512",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("person", personId);
            cmd.Parameters.AddWithValue("model", FaceApi.Pipeline);
            cmd.Parameters.AddWithValue("include_inactive", includeInactive);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                throw new InvalidOperationException(
                    "이 직원에게 호환되는 활성 얼굴 등록이 없습니다."
                );
            enrollment = reader.GetGuid(0);
            employeeNo = reader.GetString(1);
            name = reader.GetString(2);
        }

        var features = new Dictionary<string, JsonElement>();
        await using (
            var cmd = new NpgsqlCommand(
                "SELECT pose,embedding_encrypted,sample_no FROM access_control.face_templates WHERE enrollment_id=@id",
                connection,
                transaction
            )
        )
        {
            cmd.Parameters.AddWithValue("id", enrollment);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                byte[] bytes = unprotect(reader.GetFieldValue<byte[]>(1));
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    int sample = reader.GetInt32(2);
                    string key = reader.GetString(0) + (sample == 1 ? "" : "#" + sample);
                    features.Add(key, doc.RootElement.Clone());
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        var reference = JsonSerializer.SerializeToElement(
            new { pipeline = FaceApi.Pipeline, features }
        );
        FaceApi.ValidateReference(reference);
        await transaction.CommitAsync(token);
        return new StoredFace(personId, enrollment, employeeNo, name, reference)
        {
            Lease = IssueLease(personId, enrollment)
        };
    }

    // 출입 불가·비활성 직원도 식별 후 서버에서 거부할 수 있도록 호환되는 활성 얼굴 등록 목록에 포함합니다.
    public async Task<List<FaceEmployee>> CatalogAsync(CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var cmd = new NpgsqlCommand(
            "SELECT p.person_id,p.employee_no,p.name,p.is_active,p.access_enabled,e.enrollment_id FROM access_control.persons p JOIN access_control.face_enrollments e ON e.person_id=p.person_id AND e.is_active WHERE e.model_version=@model AND e.embedding_dimension=512 ORDER BY p.person_id LIMIT 1001",
            connection
        );
        cmd.Parameters.AddWithValue("model", FaceApi.Pipeline);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<FaceEmployee>();
        while (await reader.ReadAsync(token))
            rows.Add(
                new(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.GetGuid(5)
                )
            );
        if (rows.Count > GalleryRules.MaxPeople)
            throw new InvalidOperationException(
                "현재 데모는 최대 1000명까지 지원합니다. 기준 일부를 생략하지 않고 비교를 보류합니다."
            );
        return rows;
    }

    public async Task<FaceReply> HandleAsync(FaceRequest request, CancellationToken token)
    {
        return request.Operation switch
        {
            "employees" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                Employees = await EmployeesAsync(token)
            },
            "catalog" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                Employees = await CatalogAsync(token),
                DatabaseSession = DatabaseSession
            },
            "gallery-load" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                Face = await LoadAsync(request.PersonId, token, true)
            },
            "load" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                Face = await LoadAsync(request.PersonId, token),
                Message = "DB 얼굴 기준 불러오기 완료"
            },
            "save" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                Face = await SaveAsync(request, token),
                Message = "얼굴 특징 DB 저장 완료"
            },
            "recording-state" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                DatabaseSession = DatabaseSession
            },
            "recording" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                RecordedSegmentId = await SaveRecordingAsync(
                    request.Recording ?? throw new InvalidDataException("녹화 정보가 없습니다."),
                    token,
                    request.Remote
                )
            },
            "clip-begin" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                ClipId = await BeginClipAsync(
                    request.Clip ?? throw new InvalidDataException("클립 정보가 없습니다."),
                    token
                )
            },
            "clip-complete" => new FaceReply
            {
                RequestId = request.RequestId,
                Success = true,
                ClipId = await CompleteClipAsync(
                    request.Clip ?? throw new InvalidDataException("클립 정보가 없습니다."),
                    token
                )
            },
            _ => throw new InvalidDataException("지원하지 않는 요청입니다.")
        };
    }
}
