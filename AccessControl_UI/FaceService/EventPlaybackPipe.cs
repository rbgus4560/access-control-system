// 파일 역할: 동일 PC의 출입 기록 상세에서 해당 시각의 서버 재생 화면을 여는 요청을 전달합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.IO.Pipes;

namespace AccessControl.Faces;

public sealed record EventPlaybackRequest
{
    public int Version { get; init; } = 1;
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public Guid EventId { get; init; }
    public string CameraCode { get; init; } = "CAM-01";
    public DateTimeOffset EventTime { get; init; }
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
    public string Summary { get; init; } = "";
}

public sealed record EventPlaybackReply
{
    public Guid RequestId { get; init; }
    public Guid EventId { get; init; }
    public bool Success { get; init; }
    public bool HasRecording { get; init; }
    public string Message { get; init; } = "";
}

public static class EventPlaybackPipe
{
    public const string Name = "AccessControlSystem.EventPlayback.stage10";

    public static void Validate(EventPlaybackRequest r)
    {
        double age = (DateTimeOffset.UtcNow - r.SentAt).TotalMilliseconds;
        if (
            r.Version != 1
            || r.RequestId == Guid.Empty
            || r.EventId == Guid.Empty
            || r.CameraCode != "CAM-01"
            || r.EventTime == default
            || r.EventTime.Offset != TimeSpan.Zero
            || r.EventTime > DateTimeOffset.UtcNow.AddSeconds(5)
            || r.SentAt == default
            || age < -500
            || age > 10_000
            || r.Summary is null
            || r.Summary.Length > 200
        )
            throw new InvalidDataException("출입 기록 재생 요청이 올바르지 않습니다.");
    }

    public static async Task<EventPlaybackReply> SendAsync(
        EventPlaybackRequest request,
        CancellationToken stop
    )
    {
        Validate(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(6000);
        await using var pipe = new NamedPipeClientStream(
            ".",
            Name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await pipe.ConnectAsync(3000, timeout.Token);
        await FaceApi.WriteAsync(pipe, request, 4096, timeout.Token);
        var reply = await FaceApi.ReadAsync<EventPlaybackReply>(pipe, 4096, timeout.Token);
        if (
            reply.RequestId != request.RequestId
            || reply.EventId != request.EventId
            || reply.Message is null
            || reply.Message.Length > 500
            || (!reply.Success && reply.HasRecording)
        )
            throw new InvalidDataException("출입 기록 재생 응답이 일치하지 않습니다.");
        return reply;
    }

    public static async Task ListenAsync(
        Func<EventPlaybackRequest, CancellationToken, Task<EventPlaybackReply>> receive,
        CancellationToken stop
    )
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    Name,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );
                await pipe.WaitForConnectionAsync(stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(5000);
                var request = await FaceApi.ReadAsync<EventPlaybackRequest>(
                    pipe,
                    4096,
                    timeout.Token
                );
                Validate(request);
                var reply = await receive(request, timeout.Token);
                await FaceApi.WriteAsync(pipe, reply, 4096, timeout.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or InvalidDataException
                            or OperationCanceledException
                            or System.Text.Json.JsonException
                            or TimeoutException
                )
            {
                if (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(100, stop);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
    }
}
