// 파일 역할: AI 상태를 서버용 관측 메시지로 변환합니다. 오래된 결과·모호한 식별은 인증 시도로 만들지 않습니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Diagnostics;
using System.Text.Json;
using AccessControl.Ai;
using AccessControl.Network;

namespace FaceRecognition;

public sealed class ObservationSource
{
    private sealed record Timed(MonitorSnapshot Snapshot, long At);

    private Timed latest = new(new MonitorSnapshot(), Stopwatch.GetTimestamp());
    private readonly AuthenticationAttempt attempts = new();

    public static bool Ambiguous(AiState state) =>
        state.Identification?.Result == "AMBIGUOUS"
        || state.Identification?.RunnerUpDistance is { } second
            && state.Comparison is { ValueKind: JsonValueKind.Array } c
            && c.GetArrayLength() == 3
            && c[0].TryGetDouble(out var best)
            && best <= .45
            && second - best < .05;

    // 유효하고 최신인 비교만 인증 시도로 보냅니다. 미등록자의 최근접 직원 변동은 새 시도로 세지 않습니다.
    public void Update(AiState state, bool fresh, double ageMs, ReferenceIdentity? identity = null)
    {
        double age =
            double.IsFinite(ageMs) && ageMs >= 0 ? ageMs : MonitorProtocol.MaxAnalysisAgeMs + 1;
        bool valid = fresh && age <= MonitorProtocol.MaxAnalysisAgeMs;
        string status = valid ? "PAUSED" : "WAITING";
        double? distance = null;
        if (
            valid
            && state.Reason == "OK"
            && state.Calibrated
            && state.Matching
            && !state.Enrolling
            && state.ReferenceCount is 5 or 9
            && !Ambiguous(state)
            && state.Comparison is { ValueKind: JsonValueKind.Array } c
            && c.GetArrayLength() == 3
            && c[0].TryGetDouble(out var d)
            && double.IsFinite(d)
            && d is >= 0 and <= 2
            && c[2].ValueKind is JsonValueKind.True or JsonValueKind.False
            && c[2].GetBoolean() == (d <= .450)
        )
        {
            distance = d;
            status = d <= .450 ? "MATCH_CANDIDATE" : "NO_MATCH";
        }

        string label = new(
            (state.ReferenceLabel ?? "").Where(x => !char.IsControl(x)).Take(180).ToArray()
        );
        var snapshot = new MonitorSnapshot
        {
            Status = status,
            Reason = valid
                ? Ambiguous(state)
                    ? "AMBIGUOUS_MATCH"
                    : state.Reason
                : "ANALYSIS_WAITING",
            Pose = state.Pose,
            ReferenceLabel = label,
            ReferenceCount = state.ReferenceCount,
            Fresh = valid,
            AnalysisAgeMs = age,
            Distance = distance,
            Identity = identity,
            RunnerUpDistance = distance is null ? null : state.Identification?.RunnerUpDistance,
            AttemptId = attempts.Observe(
                distance is null ? null
                    : status == "NO_MATCH" ? $"NO_MATCH|{label}"
                    : $"{status}|{identity?.PersonId}|{identity?.EnrollmentId}|{identity?.Lease}|{label}",
                Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency
            )
        };
        Volatile.Write(ref latest, new Timed(snapshot, Stopwatch.GetTimestamp()));
    }

    public MonitorSnapshot Get()
    {
        var value = Volatile.Read(ref latest);
        double age =
            value.Snapshot.AnalysisAgeMs + Stopwatch.GetElapsedTime(value.At).TotalMilliseconds;
        if (age > MonitorProtocol.MaxAnalysisAgeMs)
            return value.Snapshot with
            {
                AnalysisAgeMs = age,
                Fresh = false,
                Distance = null,
                RunnerUpDistance = null,
                Status = "WAITING",
                Reason = "ANALYSIS_WAITING",
                AttemptId = null,
                SentAt = DateTimeOffset.UtcNow
            };
        return value.Snapshot with { AnalysisAgeMs = age, SentAt = DateTimeOffset.UtcNow };
    }
}
