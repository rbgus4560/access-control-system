// 파일 역할: 동일 PC에서 단일 얼굴 기준을 전달하던 로컬 파이프 규약입니다. 현재 전체 직원 자동 동기화는 TCP API를 사용합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AccessControl.Ai;

// Local stage-6 handoff. No disk persistence, DB access or TCP server connection.
public static class ReferencePipe
{
    public const string Name = "AccessControlSystem.FaceReference.stage6";
    public const string Pipeline = "arcface512_df093_dlibfast480_eyealign_v1";

    public static void Validate(JsonElement value) =>
        AccessControl.Faces.FaceReferenceRules.Validate(value);

    public static async Task<string> SendAsync(
        JsonElement reference,
        string employee,
        string person
    )
    {
        Validate(reference);
        var json = JsonSerializer.Serialize(
            new
            {
                reference,
                employee,
                person
            }
        );
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 128_000)
            throw new InvalidDataException("전달 크기 초과");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(
            ".",
            Name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token);
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadLineAsync(timeout.Token)
            ?? throw new IOException("인증 앱 응답 없음");
    }

    public static async Task ListenAsync(
        Func<JsonElement, string, Task> receiver,
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
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var reply = "";
                try
                {
                    var header = new byte[4];
                    await pipe.ReadExactlyAsync(header, timeout.Token);
                    var size = BitConverter.ToInt32(header);
                    if (size is <= 0 or > 128_000)
                        throw new InvalidDataException("전달 크기 초과");
                    var bytes = new byte[size];
                    await pipe.ReadExactlyAsync(bytes, timeout.Token);
                    using var doc = JsonDocument.Parse(bytes);
                    var root = doc.RootElement;
                    var reference = root.GetProperty("reference").Clone();
                    Validate(reference);
                    var employee = root.GetProperty("employee").GetString() ?? "";
                    var person = root.GetProperty("person").GetString() ?? "";
                    if (employee.Length > 50 || person.Length > 100)
                        throw new InvalidDataException("표시 정보 길이 초과");
                    await receiver(reference, $"{employee} · {person}");
                    reply =
                        "전달 요청 접수 · 임시 기준 메모리 수신 완료. 등록 앱의 카메라를 종료한 뒤 인증 앱에서 카메라·AI를 연결하고 기준 얼굴의 샘플 수를 확인해 주세요. DB에는 저장하지 않았습니다.";
                }
                catch (Exception ex)
                {
                    reply = "전달 실패: " + ex.Message;
                }

                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true
                };
                await writer.WriteLineAsync(reply.AsMemory(), timeout.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(300, stop);
            }
            catch (OperationCanceledException) { }
        }
    }
}
