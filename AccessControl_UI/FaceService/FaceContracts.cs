// 파일 역할: 직원·얼굴 기준·DB API의 공통 요청·응답 형식과 로컬 파이프 통신을 정의합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace AccessControl.Faces;

public sealed record FaceEmployee(
    Guid PersonId,
    string EmployeeNo,
    string Name,
    bool Active,
    bool AccessEnabled,
    Guid? EnrollmentId
)
{
    public override string ToString() =>
        $"{EmployeeNo} · {Name} · {(EnrollmentId is null ? "얼굴 미등록" : "얼굴 등록됨")}{(Active ? "" : " · 비활성")}";
}

public sealed record StoredFace(
    Guid PersonId,
    Guid EnrollmentId,
    string EmployeeNo,
    string Name,
    JsonElement Reference
)
{
    public string Lease { get; init; } = "";
}

public sealed record FaceRequest
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Remote { get; init; }
    public int Version { get; init; } = 1;
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public string Operation { get; init; } = "employees";
    public Guid PersonId { get; init; }
    public Guid? ExpectedEnrollmentId { get; init; }
    public JsonElement? Reference { get; init; }
    public RecordingEntry? Recording { get; init; }
    public ClipInfo? Clip { get; init; }
}

public sealed record FaceReply
{
    public int Version { get; init; } = 1;
    public Guid RequestId { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public List<FaceEmployee>? Employees { get; init; }
    public StoredFace? Face { get; init; }
    public Guid? RecordedSegmentId { get; init; }
    public Guid? DatabaseSession { get; init; }
    public Guid? ClipId { get; init; }
}

public static class FaceApi
{
    public const string PipeName = "AccessControlSystem.FaceDatabase.stage8";
    public const string Pipeline = "arcface512_df093_dlibfast480_eyealign_v1";
    public static readonly string[] Poses = ["FRONT", "UP", "DOWN", "LEFT", "RIGHT"];

    public static void ValidateReference(JsonElement reference) =>
        FaceReferenceRules.Validate(reference);

    public static string[] ReferencePoses(JsonElement reference) =>
        FaceReferenceRules.Validate(reference);

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        int maxBytes,
        CancellationToken token
    )
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > maxBytes)
            throw new InvalidDataException("요청 또는 응답 크기 초과");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, int maxBytes, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is < 1 || size > maxBytes)
            throw new InvalidDataException("요청 또는 응답 크기 초과");
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { MaxDepth = 12 })
            ?? throw new InvalidDataException("응답 데이터 없음");
    }

    public static Func<FaceRequest, CancellationToken, Task<FaceReply>>? RemoteCaller { get; set; }

    public static async Task<FaceReply> CallAsync(
        FaceRequest request,
        CancellationToken token = default
    )
    {
        if (RemoteCaller is { } remote)
            return await remote(request, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        try
        {
            await pipe.ConnectAsync(5000, timeout.Token);
            await WriteAsync(pipe, request, 128_000, timeout.Token);
            var reply = await ReadAsync<FaceReply>(pipe, 512_000, timeout.Token);
            if (reply.Version != 1 || reply.RequestId != request.RequestId)
                throw new InvalidDataException("서버 응답 불일치");
            if (!reply.Success)
                throw new InvalidOperationException(reply.Message);
            return reply;
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("서버 앱을 실행하고 DB 연결을 먼저 해 주세요.");
        }
    }
}

public static class FaceApiServer
{
    public static async Task ListenAsync(
        Func<FaceRequest, CancellationToken, Task<FaceReply>> handle,
        CancellationToken stop
    )
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    FaceApi.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );
                await pipe.WaitForConnectionAsync(stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(18));
                FaceRequest? request = null;
                FaceReply reply;
                try
                {
                    request = await FaceApi.ReadAsync<FaceRequest>(pipe, 128_000, timeout.Token);
                    if (
                        request.Version != 1
                        || request.RequestId == Guid.Empty
                        || request.Operation
                            is not (
                                "employees"
                                or "load"
                                or "catalog"
                                or "gallery-load"
                                or "save"
                                or "recording"
                                or "recording-state"
                                or "clip-begin"
                                or "clip-complete"
                                or "open-employees"
                            )
                    )
                        throw new InvalidDataException("지원하지 않는 DB 요청입니다.");
                    reply = await handle(request, timeout.Token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
                {
                    reply = new FaceReply
                    {
                        RequestId = request?.RequestId ?? Guid.Empty,
                        Message = ex.Message
                    };
                }
                catch (Exception) when (!stop.IsCancellationRequested)
                {
                    reply = new FaceReply
                    {
                        RequestId = request?.RequestId ?? Guid.Empty,
                        Message = "DB 처리 실패 · 서버 DB 연결과 테이블 상태를 확인해 주세요."
                    };
                }

                await FaceApi.WriteAsync(pipe, reply, 512_000, timeout.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
                when (ex is IOException or OperationCanceledException or JsonException)
            {
                if (!stop.IsCancellationRequested)
                    await Task.Delay(200, stop);
            }
        }
    }
}
