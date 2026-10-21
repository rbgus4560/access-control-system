// 파일 역할: 로컬 얼굴 기준 전달의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Text.Json;
using AccessControl.Ai;

static void Ensure(bool condition, string reason)
{
    if (!condition)
        throw new Exception(reason);
}

static JsonElement MakeReference(string pipeline = ReferencePipe.Pipeline)
{
    var unit = new double[512];
    unit[0] = 1;
    var features = new[] { "FRONT", "UP", "DOWN", "LEFT", "RIGHT" }.ToDictionary(x => x, _ => unit);
    return JsonSerializer.SerializeToElement(new { pipeline, features });
}

var inbox = new ReferenceInbox();

// Receive before opening any camera or Python process, and own the JSON after
// the incoming document is disposed.
using (var incoming = JsonDocument.Parse(MakeReference().GetRawText()))
    inbox.Receive(incoming.RootElement, "TEST-1 · first");
Ensure(inbox.HasReference && inbox.HasPending, "Offline reference was lost");
Ensure(inbox.TryTakePending(out var accepted), "No pending import after receiving offline");
ReferencePipe.Validate(accepted!.Reference);
Ensure(!inbox.TryTakePending(out _), "Repeated import without reconnect");
inbox.Requeue();
Ensure(
    inbox.TryTakePending(out var reconnected) && reconnected!.Label == accepted.Label,
    "Reference did not survive reconnect"
);
try
{
    inbox.Receive(MakeReference("incompatible"), "invalid");
    throw new Exception("Invalid reference accepted");
}
catch (InvalidDataException) { }

Ensure(inbox.Label == accepted.Label, "Rejected reference replaced accepted reference");
inbox.Receive(MakeReference(), "TEST-2 · latest");
Ensure(
    inbox.TryTakePending(out var latest) && latest!.Label == "TEST-2 · latest",
    "Latest reference not used"
);
inbox.Clear();
inbox.Requeue();
Ensure(!inbox.HasReference && !inbox.TryTakePending(out _), "Clear allowed a reference to return");
Console.WriteLine(
    "Offline receive / JSON ownership / reconnect / invalid version / replacement / clear: PASS"
);
using var cancel = new CancellationTokenSource();
var listening = ReferencePipe.ListenAsync(
    (reference, label) =>
    {
        inbox.Receive(reference, label);
        return Task.CompletedTask;
    },
    cancel.Token
);
try
{
    if (listening.IsFaulted)
        await listening;
    var answer = await ReferencePipe.SendAsync(MakeReference(), "EMP-TEST", "test person");
    Ensure(answer.StartsWith("전달 요청 접수"), "Handoff was not acknowledged: " + answer);
    Ensure(
        inbox.HasPending && inbox.Label == "EMP-TEST · test person",
        "Offline pipe recipient lost the reference"
    );
    Ensure(inbox.TryTakePending(out var fromPipe), "No deferred reference after pipe receive");
    ReferencePipe.Validate(fromPipe!.Reference);
    Console.WriteLine("Named Pipe -> offline receiver -> deferred validated import: PASS");
}
catch (System.Net.Sockets.SocketException ex)
    when (!OperatingSystem.IsWindows()
        && ex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied
    )
{
    Console.WriteLine(
        "OS denies Unix sockets here; Windows Named Pipe transport verification pending."
    );
}
finally
{
    cancel.Cancel();
    try
    {
        await listening;
    }
    catch (OperationCanceledException) { }
    catch (System.Net.Sockets.SocketException) when (!OperatingSystem.IsWindows()) { }

    inbox.Clear();
}
