// 파일 역할: 이전 단일 기준 전달 경로의 메모리 보관함입니다. AI 준비·재연결 시 적용 대기 상태를 관리합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Text.Json;

namespace AccessControl.Ai;

public sealed record ReferenceSnapshot(JsonElement Reference, string Label);

// Accessed only on the UI thread. Keeps one validated handoff in memory until
// AI is ready; reconnecting the camera requeues it without writing features.
public sealed class ReferenceInbox
{
    private ReferenceSnapshot? current;
    private bool pending;
    public int Count => current?.Reference.GetProperty("features").EnumerateObject().Count() ?? 0;
    public bool HasReference => current is not null;
    public bool HasPending => current is not null && pending;
    public string Label => current?.Label ?? "";

    public void Receive(JsonElement reference, string label)
    {
        ReferencePipe.Validate(reference);
        if (label.Length > 180)
            throw new InvalidDataException("표시 정보 길이 초과");
        var accepted = new ReferenceSnapshot(reference.Clone(), label);
        current = accepted;
        pending = true;
    }

    public bool TryTakePending(out ReferenceSnapshot? value)
    {
        value = HasPending ? current : null;
        if (value is null)
            return false;
        pending = false;
        return true;
    }

    public void Requeue() => pending = current is not null;

    public void Clear()
    {
        current = null;
        pending = false;
    }
}
