// 파일 역할: 실제 루프백 통신과 메시지 검증의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AccessControl.Ai;
using AccessControl.Network;
using FaceRecognition;

if (args.Contains("--video-metadata-only"))
{
    await VideoChecks.Run();
    return;
}

static void Check(bool value, string description)
{
    if (!value)
        throw new Exception(description);
}

static void Reject(Action action)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new Exception("Invalid observation was accepted");
}

static async Task Until(Func<bool> condition, string description, int ms = 8000)
{
    var watch = Stopwatch.StartNew();
    while (!condition() && watch.ElapsedMilliseconds < ms)
        await Task.Delay(20);
    Check(condition(), description);
}

static int FreePort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    int port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

var source = new ObservationSource();
var face = new AiState
{
    Reason = "OK",
    Pose = "FRONT",
    Calibrated = true,
    Matching = true,
    ReferenceCount = 5,
    ReferenceLabel = "TEST-01 · 검증",
    Comparison = JsonSerializer.SerializeToElement(new object[] { .181, "FRONT", true })
};
source.Update(face, true, 400);
Check(source.Get().Status == "MATCH_CANDIDATE", "Candidate missing");
source.Update(face, false, 400);
Check(source.Get().Distance is null, "Pending command retained identity result");
face.Reason = "MULTIPLE_FACES";
source.Update(face, true, 400);
Check(source.Get().Status == "PAUSED" && source.Get().Distance is null, "Multiple faces compared");
face.Reason = "NO_MESH";
source.Update(face, true, 400);
Check(source.Get().Distance is null, "Absent face compared");
face.Reason = "OK";
face.Matching = false;
source.Update(face, true, 400);
Check(source.Get().Distance is null, "Disabled comparison transmitted");
face.Matching = true;
source.Update(face, true, 3000);
Check(!source.Get().Fresh && source.Get().Distance is null, "Old identity transmitted");
source.Update(face, true, 2450);
await Task.Delay(80);
Check(
    !source.Get().Fresh && source.Get().Status == "WAITING",
    "Snapshot failed to age without new AI events"
);
face.Comparison = JsonSerializer.SerializeToElement(new object[] { .8, "FRONT", false });
source.Update(face, true, 400);
Check(source.Get().Status == "NO_MATCH", "Mismatch missing");
Console.WriteLine(
    "Observation conditions / stale command / multiple faces / no face / comparison off / aging / mismatch: PASS"
);
var packet = new MonitorPacket
{
    Session = Guid.NewGuid().ToString(),
    Sequence = 1,
    Snapshot = source.Get()
};
MonitorProtocol.Validate(packet, "snapshot");
Reject(() => MonitorProtocol.Validate(packet with { Version = 9 }, "snapshot"));
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Snapshot = packet.Snapshot! with { Status = "MATCH_CANDIDATE" }
        },
        "snapshot"
    )
);
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Snapshot = packet.Snapshot! with { AnalysisAgeMs = 3000 }
        },
        "snapshot"
    )
);
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Snapshot = packet.Snapshot! with { ReferenceLabel = new string('x', 181) }
        },
        "snapshot"
    )
);
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Snapshot = packet.Snapshot! with { ReferenceCount = 0 }
        },
        "snapshot"
    )
);
using (var memory = new MemoryStream())
{
    await MonitorProtocol.WriteAsync(memory, packet, default);
    memory.Position = 0;
    Check(
        (await MonitorProtocol.ReadAsync(memory, default)).Snapshot!.ReferenceLabel
            == face.ReferenceLabel,
        "Korean text changed"
    );
}

byte[] oversized = new byte[4];
BinaryPrimitives.WriteInt32BigEndian(oversized, MonitorProtocol.MaxBytes + 1);
try
{
    await MonitorProtocol.ReadAsync(new MemoryStream(oversized), default);
    throw new Exception("Oversized frame accepted");
}
catch (InvalidDataException) { }

Console.WriteLine("Protocol validation / Korean JSON round trip / bounded packet: PASS");
int port = FreePort();
await using var server = new MonitorServer();
await using var client = new MonitorClient();
long received = 0,
    acknowledged = 0;
server.Received += p => Interlocked.Increment(ref received);
client.Acknowledged += p => Interlocked.Increment(ref acknowledged);
server.Start(port);
client.Start("127.0.0.1", port, () => new MonitorSnapshot());
await Until(() => Interlocked.Read(ref acknowledged) >= 2, "Actual TCP exchange failed");
await server.StopAsync();
long before = Interlocked.Read(ref acknowledged);
await Task.Delay(1200);
server.Start(port);
await Until(
    () => Interlocked.Read(ref acknowledged) > before,
    "Client failed to reconnect after server restart"
);
await client.StopAsync();
Check(received >= acknowledged, "ACK preceded reception");

// Invalid and partial frames must not poison the listener for the next client.
using (var invalid = new TcpClient())
{
    await invalid.ConnectAsync(IPAddress.Loopback, port);
    await invalid.GetStream().WriteAsync(oversized);
}

client.Start("localhost", port, () => new MonitorSnapshot());
before = Interlocked.Read(ref acknowledged);
await Until(() => Interlocked.Read(ref acknowledged) > before, "Bad connection stopped listener");
await client.StopAsync();
await server.StopAsync();
Console.WriteLine(
    "Real loopback TCP / ACK / heartbeat / server restart / reconnect / malformed peer / shutdown: PASS"
);

// A peer holding a partial frame cannot block the server indefinitely.
server.Start(port);
using (var stalled = new TcpClient())
{
    await stalled.ConnectAsync(IPAddress.Loopback, port);
    await stalled.GetStream().WriteAsync(new byte[] { 0, 0 });
    client.Start("localhost", port, () => new MonitorSnapshot());
    before = Interlocked.Read(ref acknowledged);
    await Until(
        () => Interlocked.Read(ref acknowledged) > before,
        "Partial frame stalled next connection",
        11000
    );
}

await client.StopAsync();
await server.StopAsync();
Console.WriteLine("Partial-frame timeout and recovery: PASS");

// Authorization is stricter than a face match: every required flag must be true.
int combinations = 0;
foreach (bool matched in new[] { false, true })
foreach (bool active in new[] { false, true })
foreach (bool enrollmentActive in new[] { false, true })
foreach (bool access in new[] { false, true })
foreach (bool device in new[] { false, true })
{
    var decision = AccessPolicy.Evaluate(matched, active, enrollmentActive, access, device);
    Check(
        (decision.Result == "GRANTED")
            == (matched && active && enrollmentActive && access && device),
        "Unsafe authorization flag combination"
    );
    combinations++;
}

Check(
    AccessPolicy.Evaluate(true, true, true, false, true).Reason == "ACCESS_DISABLED",
    "Missing permission not rejected"
);
Check(
    AccessPolicy.Evaluate(true, false, true, true, true).Reason == "PERSON_INACTIVE",
    "Inactive person not rejected"
);
Check(
    AccessPolicy.Evaluate(true, true, false, true, true).Reason == "REFERENCE_INACTIVE",
    "Inactive enrollment not rejected"
);
var encounters = new AuthenticationAttempt();
var first = encounters.Observe("person:match", 0);
Check(
    first == encounters.Observe("person:match", 200),
    "Continuous face created repeated attempts"
);
encounters.Observe(null, 300);
Check(
    first == encounters.Observe("person:match", 500),
    "Brief quality loss created another attempt"
);
encounters.Observe(null, 600);
Check(
    first != encounters.Observe("person:match", 3000),
    "No-face interval failed to re-arm authentication"
);
var now = DateTimeOffset.UtcNow;
Check(
    !AccessPolicy.IsCurrent(
        new MonitorSnapshot
        {
            Fresh = true,
            AnalysisAgeMs = 100,
            SentAt = now.AddSeconds(-3)
        },
        now
    ),
    "Delayed network observation authorized"
);
Check(
    !AccessPolicy.IsCurrent(
        new MonitorSnapshot
        {
            Fresh = true,
            AnalysisAgeMs = 100,
            SentAt = now.AddSeconds(2)
        },
        now
    ),
    "Future observation authorized"
);
Check(
    AccessPolicy.IsCurrent(
        new MonitorSnapshot
        {
            Fresh = true,
            AnalysisAgeMs = 400,
            SentAt = now.AddMilliseconds(-100)
        },
        now
    ),
    "Fresh observation rejected"
);
Guid eventAttempt = Guid.NewGuid(),
    pid = Guid.NewGuid(),
    eid = Guid.NewGuid();
Guid grantId = AccessPolicy.EventId(
    eventAttempt,
    "CAM-01",
    "GRANTED",
    "ACCESS_ALLOWED",
    pid,
    eid,
    "policy1"
);
Check(
    grantId
        == AccessPolicy.EventId(
            eventAttempt,
            "CAM-01",
            "GRANTED",
            "ACCESS_ALLOWED",
            pid,
            eid,
            "policy1"
        ),
    "Retransmission changed event ID"
);
Check(
    grantId
        != AccessPolicy.EventId(
            eventAttempt,
            "CAM-01",
            "DENIED",
            "ACCESS_DISABLED",
            pid,
            eid,
            "policy1"
        ),
    "Permission change reused grant ID"
);
Check(
    grantId
        != AccessPolicy.EventId(
            eventAttempt,
            "CAM-01",
            "GRANTED",
            "ACCESS_ALLOWED",
            pid,
            eid,
            "policy2"
        ),
    "Permission restoration failed to create new event"
);
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Decision = new AccessDecision { Result = "GRANTED" }
        },
        "snapshot"
    )
);
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Type = "ack",
            Snapshot = null,
            Decision = new AccessDecision { Result = "GRANTED", AttemptId = eventAttempt }
        },
        "ack"
    )
);
Console.WriteLine(
    $"Authorization {combinations} flag combinations / attempts / re-arm / transit age / event idempotency / permission restore / forged or unrecorded grant: PASS"
);

// Exercise server-to-client policy response transport with an explicit test handler.
server.DecideAsync = (p, t) =>
    Task.FromResult<AccessDecision?>(
        new AccessDecision
        {
            Result = "ERROR",
            Reason = "DB_NOT_CONNECTED",
            AttemptId = p.Snapshot?.AttemptId
        }
    );
long policyAcks = 0;
client.Acknowledged += ack =>
{
    if (ack.Decision?.Reason == "DB_NOT_CONNECTED")
        Interlocked.Increment(ref policyAcks);
};
server.Start(port);
client.Start("localhost", port, () => new MonitorSnapshot());
await Until(() => Interlocked.Read(ref policyAcks) > 0, "Policy response did not reach TCP client");
await client.StopAsync();
await server.StopAsync();
Console.WriteLine(
    "Server policy callback / TCP decision response / acknowledgement correlation: PASS"
);
Check(
    !AccessPolicy.IsCurrent(
        new MonitorSnapshot { Fresh = true, AnalysisAgeMs = 0 },
        DateTimeOffset.UtcNow
    ),
    "Missing send time authorized"
);
var validGrant = new AccessDecision
{
    Result = "GRANTED",
    Reason = "ACCESS_ALLOWED",
    Recorded = true,
    AttemptId = eventAttempt,
    EventId = grantId,
    EventTime = DateTimeOffset.UtcNow,
    RecordedDistance = .182,
    EmployeeNo = "0511",
    PersonName = "검증 직원"
};
var grantAck = packet with { Type = "ack", Snapshot = null, Decision = validGrant };
MonitorProtocol.Validate(grantAck, "ack");
using (var grantStream = new MemoryStream())
{
    await MonitorProtocol.WriteAsync(grantStream, grantAck, default);
    grantStream.Position = 0;
    var decoded = await MonitorProtocol.ReadAsync(grantStream, default);
    MonitorProtocol.Validate(decoded, "ack");
    Check(
        decoded.Decision!.EventId == grantId
            && decoded.Decision.RecordedDistance == .182
            && decoded.Decision.PersonName == "검증 직원",
        "Recorded grant response changed"
    );
}

Console.WriteLine(
    "Missing timestamp rejection / recorded grant ACK / stored distance and identity response: PASS"
);
await VideoChecks.Run();

// Both legacy and expanded references must reach policy evaluation without truncation.
face.Reason = "OK";
face.Matching = true;
face.Pose = "UP_LEFT";
face.ReferenceCount = 9;
face.Comparison = JsonSerializer.SerializeToElement(new object[] { .181, "UP_LEFT", true });
source.Update(face, true, 400);
var expanded = source.Get();
Check(
    expanded.Status == "MATCH_CANDIDATE" && expanded.ReferenceCount == 9,
    "Nine-direction candidate lost"
);
MonitorProtocol.Validate(packet with { Snapshot = expanded }, "snapshot");
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Snapshot = expanded with { ReferenceCount = 8 }
        },
        "snapshot"
    )
);
Reject(() =>
    MonitorProtocol.Validate(
        packet with
        {
            Snapshot = expanded with { ReferenceCount = 6 }
        },
        "snapshot"
    )
);
Console.WriteLine(
    "Legacy five / expanded nine directions / diagonal transport / partial reference rejection: PASS"
);
