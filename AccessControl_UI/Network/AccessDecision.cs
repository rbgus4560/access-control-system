// 파일 역할: 서버 최종 판정·식별 토큰·정책 사유와 인증 시도 ID 규칙을 정의합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Security.Cryptography;
using System.Text;

namespace AccessControl.Network;

public sealed record ReferenceIdentity(Guid PersonId, Guid EnrollmentId, string Lease);

public sealed record AccessDecision
{
    public string Result { get; init; } = "WAITING";
    public string Reason { get; init; } = "NO_COMPARISON";
    public Guid? AttemptId { get; init; }
    public Guid? EventId { get; init; }
    public bool Recorded { get; init; }
    public bool IsNew { get; init; }
    public string EmployeeNo { get; init; } = "";
    public string PersonName { get; init; } = "";
    public DateTimeOffset? EventTime { get; init; }
    public double? RecordedDistance { get; init; }
}

public static class AccessPolicy
{
    public static string Explain(string reason) =>
        reason switch
        {
            "ACCESS_ALLOWED" => "얼굴 일치 · 직원 출입 허용",
            "ACCESS_DISABLED" => "직원 출입 권한 없음",
            "PERSON_INACTIVE" => "비활성 직원",
            "REFERENCE_INACTIVE" => "이전 얼굴 등록 · DB 기준을 다시 불러오세요",
            "AMBIGUOUS_MATCH" => "직원 식별 모호 · 정면과 조명을 조정해 주세요",
            "NO_MATCH" => "등록 얼굴 불일치",
            "DEVICE_DISABLED" => "비활성 인증 장치",
            "DB_REFERENCE_REQUIRED" => "DB 얼굴 기준을 다시 불러오세요",
            "DB_NOT_CONNECTED" => "서버 DB 연결 필요",
            "DB_ERROR" => "DB 처리 실패 · 출입 허가 보류",
            "STALE_ANALYSIS" => "새 얼굴 분석 결과 대기",
            _ => "얼굴 비교 대기/중지"
        };

    public static (string Result, string Reason) Evaluate(
        bool matched,
        bool personActive,
        bool enrollmentActive,
        bool accessEnabled,
        bool deviceEnabled
    )
    {
        if (!deviceEnabled)
            return ("DENIED", "DEVICE_DISABLED");
        if (!matched)
            return ("DENIED", "NO_MATCH");
        if (!personActive)
            return ("DENIED", "PERSON_INACTIVE");
        if (!enrollmentActive)
            return ("DENIED", "REFERENCE_INACTIVE");
        return accessEnabled ? ("GRANTED", "ACCESS_ALLOWED") : ("DENIED", "ACCESS_DISABLED");
    }

    public static bool IsCurrent(MonitorSnapshot snapshot, DateTimeOffset now)
    {
        double transit = (now - snapshot.SentAt).TotalMilliseconds;
        return snapshot.Fresh
            && transit >= -500
            && transit <= MonitorProtocol.MaxAnalysisAgeMs
            && snapshot.AnalysisAgeMs + Math.Max(0, transit) <= MonitorProtocol.MaxAnalysisAgeMs;
    }

    public static Guid EventId(
        Guid attempt,
        string camera,
        string result,
        string reason,
        Guid? person,
        Guid? enrollment,
        string policyVersion = ""
    )
    {
        // Stable across retransmission, TCP reconnect and server restart.
        byte[] bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{attempt:D}|{camera}|{result}|{reason}|{person:D}|{enrollment:D}|{policyVersion}"
            )
        );
        return new Guid(bytes.AsSpan(0, 16));
    }
}

// Retain one encounter through brief quality losses; re-arm after two seconds
// without a comparison. A changed comparison/identity starts a new attempt.
public sealed class AuthenticationAttempt
{
    private string key = "";
    private Guid id;
    private double? pausedAt;

    public Guid? Observe(string? next, double nowMilliseconds)
    {
        if (next is null)
        {
            pausedAt ??= nowMilliseconds;
            return null;
        }

        if (next != key || id == Guid.Empty || (pausedAt is { } at && nowMilliseconds - at >= 2000))
        {
            key = next;
            id = Guid.NewGuid();
        }

        pausedAt = null;
        return id;
    }
}
