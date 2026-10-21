// 파일 역할: 인증 관측·서버 응답의 메시지 형식과 검증 규칙입니다. 길이 접두부로 TCP 메시지 경계를 구분합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text.Json;

namespace AccessControl.Network;

// Stage 9: clients report observations; only the server returns access decisions.
public sealed record MonitorSnapshot
{
    public string Status { get; init; } = "WAITING";
    public string Reason { get; init; } = "AI_NOT_CONNECTED";
    public string Pose { get; init; } = "UNKNOWN";
    public string ReferenceLabel { get; init; } = "";
    public int ReferenceCount { get; init; }
    public bool Fresh { get; init; }
    public double AnalysisAgeMs { get; init; }
    public double? Distance { get; init; }
    public double Threshold { get; init; } = .450;
    public double? RunnerUpDistance { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public Guid? AttemptId { get; init; }
    public ReferenceIdentity? Identity { get; init; }
}

public sealed record MonitorPacket
{
    public int Version { get; init; } = 2;
    public string Type { get; init; } = "snapshot";
    public string Session { get; init; } = "";
    public long Sequence { get; init; }
    public string ClientCode { get; init; } = "RECOGNITION-01";
    public string CameraCode { get; init; } = "CAM-01";
    public MonitorSnapshot? Snapshot { get; init; }
    public AccessDecision? Decision { get; init; }
}

public static class MonitorProtocol
{
    public const int DefaultPort = 5050;
    public const int MaxBytes = 16_384;
    public const double MaxAnalysisAgeMs = 2500;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static bool ValidPort(int port) => port is >= 1 and <= 65535;

    public static IPAddress LocalAddress(string host)
    {
        if (LinkSecurity.Client is not null && IPAddress.TryParse(host.Trim(), out var remote))
            return remote;
        // Plain transport remains loopback-only for local contract checks.
        if (
            host.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Trim() == "127.0.0.1"
        )
            return IPAddress.Loopback;
        throw new ArgumentException("서버 연결 코드를 적용하고 올바른 서버 IP를 사용하세요.");
    }

    public static void Validate(MonitorPacket packet, string type)
    {
        if (
            packet.Version != 2
            || packet.Type != type
            || !Guid.TryParse(packet.Session, out _)
            || packet.Sequence <= 0
            || packet.ClientCode != "RECOGNITION-01"
            || packet.CameraCode != "CAM-01"
        )
            throw new InvalidDataException("통신 버전·세션·장치·순번 오류");
        if (type == "ack")
        {
            if (
                packet.Snapshot is not null
                || packet.Decision is { } d
                    && (
                        d.Result is not ("GRANTED" or "DENIED" or "ERROR" or "WAITING")
                        || d.Reason is null
                        || d.Reason.Length > 80
                        || d.EmployeeNo is null
                        || d.EmployeeNo.Length > 50
                        || d.PersonName is null
                        || d.PersonName.Length > 100
                        || d.Result == "GRANTED"
                            && (
                                !d.Recorded
                                || d.EventId is null
                                || d.EventId == Guid.Empty
                                || d.AttemptId is null
                                || d.AttemptId == Guid.Empty
                                || d.EventTime is null
                                || d.Reason != "ACCESS_ALLOWED"
                            )
                    )
            )
                throw new InvalidDataException("잘못된 수신 확인");
            return;
        }

        if (packet.Decision is not null)
            throw new InvalidDataException("클라이언트가 권한 판정을 보낼 수 없습니다.");
        var value = packet.Snapshot ?? throw new InvalidDataException("상태 데이터 없음");
        if (
            value.ReferenceLabel is null
            || value.ReferenceLabel.Length > 180
            || value.ReferenceLabel.Any(char.IsControl)
            || value.Reason is null
            || value.Reason.Length > 80
            || value.Reason.Any(char.IsControl)
            || value.ReferenceCount is not (0 or 5 or 9)
            || !double.IsFinite(value.AnalysisAgeMs)
            || value.AnalysisAgeMs < 0
            || value.Threshold != .450
            || value.Pose
                is not (
                    "FRONT"
                    or "UP"
                    or "DOWN"
                    or "LEFT"
                    or "RIGHT"
                    or "UP_LEFT"
                    or "UP_RIGHT"
                    or "DOWN_LEFT"
                    or "DOWN_RIGHT"
                    or "UNKNOWN"
                )
            || value.Status is not ("WAITING" or "PAUSED" or "MATCH_CANDIDATE" or "NO_MATCH")
        )
            throw new InvalidDataException("잘못된 얼굴 분석 상태");
        if (value.Fresh && value.AnalysisAgeMs > MaxAnalysisAgeMs)
            throw new InvalidDataException("오래된 분석 결과");
        bool compared = value.Status is "MATCH_CANDIDATE" or "NO_MATCH";
        if (
            compared
            && (
                !value.Fresh
                || value.Reason != "OK"
                || value.ReferenceCount is not (5 or 9)
                || value.Distance is not { } distance
                || !double.IsFinite(distance)
                || distance is < 0 or > 2
                || (value.Status == "MATCH_CANDIDATE") != (distance <= value.Threshold)
            )
        )
            throw new InvalidDataException("비교 조건과 결과 불일치");
        if (
            value.RunnerUpDistance is { } second
            && (!compared || !double.IsFinite(second) || second < value.Distance || second > 2)
        )
            throw new InvalidDataException("유효하지 않은 차순위 직원 점수");
        if (!compared && value.Distance is not null)
            throw new InvalidDataException("대기 상태에 비교 결과가 있음");
        if (
            value.Identity is { } identity
            && (
                identity.PersonId == Guid.Empty
                || identity.EnrollmentId == Guid.Empty
                || identity.Lease is null
                || !Guid.TryParseExact(identity.Lease, "N", out _)
            )
        )
            throw new InvalidDataException("잘못된 DB 기준 정보");
        if (value.AttemptId == Guid.Empty)
            throw new InvalidDataException("잘못된 인증 시도 ID");
    }

    public static async Task WriteAsync(
        Stream stream,
        MonitorPacket packet,
        CancellationToken token
    )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("통신 크기 초과");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<MonitorPacket> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 1 or > MaxBytes)
            throw new InvalidDataException("통신 크기 초과");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<MonitorPacket>(
                bytes,
                new JsonSerializerOptions { MaxDepth = 8 }
            ) ?? throw new InvalidDataException("통신 응답 없음");
    }
}
