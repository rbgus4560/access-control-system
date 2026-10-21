// 파일 역할: TLS·직원 동기화·영상/클립 복구의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AccessControl.Faces;
using AccessControl.Network;
using AccessControlServer;
using FaceRecognition;

static void Check(bool ok, string reason)
{
    if (!ok)
        throw new Exception(reason);
}

static async Task Until(Func<bool> ok, string why, int ms = 15000)
{
    var watch = Stopwatch.StartNew();
    while (!ok() && watch.ElapsedMilliseconds < ms)
        await Task.Delay(30);
    Check(ok(), why);
}

static async Task Reject(Func<Task> action, string why)
{
    try
    {
        await action();
    }
    catch (Exception e) when (e is not ApplicationException)
    {
        return;
    }

    throw new ApplicationException("Accepted " + why);
}

static int Port()
{
    for (int port = 25000; port < 50000; port += 3)
    {
        var sockets = new List<TcpListener>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var p = new TcpListener(IPAddress.Loopback, port + i);
                p.Start();
                sockets.Add(p);
            }

            return port;
        }
        catch (SocketException) { }
        finally
        {
            foreach (var p in sockets)
                p.Stop();
        }
    }

    throw new IOException();
}

if (args.Contains("--gallery-bridge"))
{
    await GalleryBridgeRegression.Run();
    return;
}

if (args.Contains("--gallery-only"))
{
    await GalleryRegression.Run();
    return;
}

if (args.Contains("--sync-only"))
{
    await SyncRegression.Run();
    return;
}

if (args.Contains("--agent"))
{
    LinkSecurity.Client = JsonSerializer.Deserialize<ClientLink>(
        Environment.GetEnvironmentVariable("PAIR_TEST")!
    )!;
    var childSeed = JsonSerializer.Deserialize<RecordingEntry>(
        Environment.GetEnvironmentVariable("SEED_TEST")!
    )!;
    childSeed = childSeed with { FilePath = RecordingFiles.PathFor(childSeed) };
    Directory.CreateDirectory(Path.GetDirectoryName(childSeed.FilePath)!);
    await File.WriteAllBytesAsync(
        childSeed.FilePath,
        Enumerable.Range(0, (int)childSeed.FileSizeBytes!).Select(i => (byte)(i % 251)).ToArray()
    );
    await MediaTransfer.WriteJsonAsync(
        Path.ChangeExtension(childSeed.FilePath, ".json"),
        childSeed,
        default
    );
    await using var agent = new MediaAgent();
    agent.Start();
    Console.WriteLine("agent-ready");
    await Console.In.ReadLineAsync();
    return;
}

using var testStop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = testStop.Token;
using var key = RSA.Create(2048);
var certRequest = new CertificateRequest(
    "CN=AccessControlServer",
    key,
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1
);
using var cert = certRequest.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddMinutes(-1),
    DateTimeOffset.UtcNow.AddDays(1)
);
var serverProfile = new ServerLink(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), cert);
LinkSecurity.Server = serverProfile;
int port = Port();
var clientProfile = new ClientLink(
    "127.0.0.1",
    port,
    serverProfile.Token,
    LinkSecurity.Fingerprint(cert)
);
LinkSecurity.Client = clientProfile;
await Reject(
    () =>
    {
        (clientProfile with { Token = null! }).Validate();
        return Task.CompletedTask;
    },
    "null pairing token"
);
await using (var monitor = new MonitorServer())
await using (var client = new MonitorClient())
await using (var videoServer = new VideoServer())
await using (var videoClient = new VideoClient())
{
    int acks = 0;
    client.Acknowledged += _ => Interlocked.Increment(ref acks);
    monitor.Start(port);
    videoServer.Start(port + 1);
    client.Start("127.0.0.1", port, () => new MonitorSnapshot());
    var source = new VideoSource();
    source.Publish(
        Convert.ToBase64String(new byte[] { 255, 216, 1, 2, 255, 217 }),
        Stopwatch.GetTimestamp()
    );
    videoClient.Start("127.0.0.1", port + 1, source);
    await Until(() => acks >= 2, "TLS monitor ACK");
    await Until(() => videoServer.Latest is not null, "TLS video");
    await monitor.StopAsync();
    int before = acks;
    monitor.Start(port);
    await Until(() => acks > before, "TLS monitor reconnect");
    await client.StopAsync();
    await videoClient.StopAsync();
}

Console.WriteLine("Pinned TLS monitor/video, ACK, reconnect: PASS");
var db = new FaceDatabase();
var coordinator = new MediaCoordinator(() => db);
int calls = 0;
Task<FaceReply> Handle(FaceRequest r, CancellationToken ct)
{
    Interlocked.Increment(ref calls);
    Check(r.Remote, "remote marker missing");
    if (r.Operation == "catalog")
        return Task.FromResult(
            new FaceReply
            {
                Success = true,
                RequestId = r.RequestId,
                Employees = new(),
                DatabaseSession = Guid.NewGuid()
            }
        );
    if (r.Operation is "load" or "gallery-load")
    {
        var vector = new double[512];
        vector[0] = 1;
        string[] poses =
        [
            "FRONT",
            "LEFT",
            "LEFT#2",
            "RIGHT",
            "RIGHT#2",
            "UP",
            "UP#2",
            "DOWN",
            "DOWN#2"
        ];
        var reference = JsonSerializer.SerializeToElement(
            new { pipeline = FaceApi.Pipeline, features = poses.ToDictionary(p => p, _ => vector) }
        );
        return Task.FromResult(
            new FaceReply
            {
                RequestId = r.RequestId,
                Success = true,
                Face = new StoredFace(r.PersonId, Guid.NewGuid(), "TEST", "테스트", reference)
                {
                    Lease = Guid.NewGuid().ToString("N")
                }
            }
        );
    }

    return Task.FromResult(
        new FaceReply
        {
            RequestId = r.RequestId,
            Success = true,
            DatabaseSession = Guid.NewGuid(),
            Employees = new()
        }
    );
}

var api = new RemoteApiServer(Handle, coordinator.ServeAgentAsync);
api.Start(port + 2);
await RemoteFaceClient.CallAsync(new FaceRequest(), token);
Check(calls == 1, "API callback");
await Reject(
    () => RemoteFaceClient.CallAsync(new FaceRequest { Operation = "save" }, token),
    "remote template write"
);
Check(calls == 1, "Denied API reached callback");
LinkSecurity.Client = clientProfile with { Token = new string('0', 64) };
await Reject(() => RemoteFaceClient.CallAsync(new FaceRequest(), token), "wrong token");
LinkSecurity.Client = clientProfile with { Fingerprint = new string('0', 64) };
await Reject(() => RemoteFaceClient.CallAsync(new FaceRequest(), token), "wrong certificate");
LinkSecurity.Client = clientProfile;
using (var wrong = new TcpClient())
{
    await wrong.ConnectAsync("127.0.0.1", port + 2, token);
    await Reject(
        async () =>
        {
            await using var s = await LinkSecurity.ConnectAsync(wrong, "video", token);
        },
        "wrong channel"
    );
}

await RemoteFaceClient.CallAsync(new FaceRequest(), token);
Check(calls == 2, "Bad peer killed API listener");
var loadId = Guid.NewGuid();
var loaded = await RemoteFaceClient.CallAsync(
    new FaceRequest { Operation = "load", PersonId = loadId },
    token
);
Check(
    loaded.Face?.PersonId == loadId
        && FaceApi.ReferencePoses(loaded.Face.Reference).Length == 9
        && loaded.Face.Lease.Length == 32,
    "Guided reference TLS roundtrip"
);
var catalogReply = await RemoteFaceClient.CallAsync(
    new FaceRequest { Operation = "catalog" },
    token
);
Check(
    catalogReply.Employees is not null && catalogReply.DatabaseSession is not null,
    "TLS catalog operation rejected"
);
var galleryLoad = await RemoteFaceClient.CallAsync(
    new FaceRequest { Operation = "gallery-load", PersonId = loadId },
    token
);
Check(galleryLoad.Face?.PersonId == loadId, "TLS gallery-load operation rejected");
Console.WriteLine(
    "Remote API correlation, guided nine-sample reference, token/pin/channel rejection, write allowlist: PASS"
);
byte[] bytes = Enumerable.Range(0, 120003).Select(i => (byte)(i % 251)).ToArray();
string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var response = new MediaResponse
{
    Success = true,
    Length = bytes.Length,
    Hash = hash
};
string testDir = Path.Combine(
    Path.GetTempPath(),
    "AccessTwoPcChecks-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(testDir);
try
{
    var path = Path.Combine(testDir, "copy.mp4");
    await MediaTransfer.ReceiveAsync(new MemoryStream(bytes), path, response, token, 0);
    Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Transfer changed bytes");
    await Reject(
        () =>
            MediaTransfer.ReceiveAsync(
                new MemoryStream(bytes),
                path,
                response with
                {
                    Hash = new string('0', 64)
                },
                token,
                0
            ),
        "hash mismatch"
    );
    Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Bad hash replaced good file");
    await Reject(
        () =>
            MediaTransfer.ReceiveAsync(
                new MemoryStream(bytes[..^1]),
                Path.Combine(testDir, "short.mp4"),
                response,
                token,
                0
            ),
        "truncation"
    );
    Check(!Directory.EnumerateFiles(testDir, "*.part").Any(), "Part files retained");
    await Reject(
        () =>
            MediaTransfer.ReceiveAsync(
                new MemoryStream(bytes),
                path,
                response with
                {
                    Length = MediaTransfer.MaxBytes + 1
                },
                token,
                0
            ),
        "oversized media"
    );
}
finally
{
    Directory.Delete(testDir, true);
}

Console.WriteLine(
    "Streaming hash/size validation, truncated transfer, atomic destination preservation: PASS"
);

// Production media agent runs in a second process with a different local data root: no shared original paths.
var begin = DateTimeOffset.UtcNow.AddMinutes(-2);
begin = new DateTimeOffset(
    begin.UtcDateTime.AddTicks(-(begin.Ticks % TimeSpan.TicksPerMillisecond)),
    TimeSpan.Zero
);
var seed = new RecordingEntry
{
    SegmentId = Guid.NewGuid(),
    StartTime = begin,
    EndTime = begin.AddSeconds(10),
    Status = "READY",
    FrameCount = 100,
    Fps = 10,
    Format = "mp4v",
    Width = 640,
    Height = 480,
    FileSizeBytes = 300001
};
seed = seed with { FilePath = RecordingFiles.PathFor(seed, RecordingFiles.CacheRoot) };
var childRoot = Path.Combine(
    Path.GetTempPath(),
    "AccessTwoPcAgent-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(childRoot);
var info = new ProcessStartInfo(Environment.ProcessPath!)
{
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
info.ArgumentList.Add(typeof(Program).Assembly.Location);
info.ArgumentList.Add("--agent");
info.Environment["PAIR_TEST"] = JsonSerializer.Serialize(clientProfile);
info.Environment["SEED_TEST"] = JsonSerializer.Serialize(seed);
if (OperatingSystem.IsWindows())
    throw new InvalidOperationException(
        "Isolated process test currently requires Linux XDG_DATA_HOME isolation."
    );
info.Environment["XDG_DATA_HOME"] = childRoot;
using var child = Process.Start(info)!;
try
{
    Check(
        await child.StandardOutput.ReadLineAsync(token) == "agent-ready",
        "Agent failed: " + (child.HasExited ? await child.StandardError.ReadToEndAsync() : "")
    );
    await Until(() => coordinator.Connected, "Outbound media agent never connected");
    await coordinator.PrepareAsync(seed, token);
    Check(new FileInfo(seed.FilePath).Length == seed.FileSizeBytes, "Remote segment copy");
    Check(
        !File.Exists(RecordingFiles.PathFor(seed)),
        "Test incorrectly used server local originals"
    );
    db.FailCompletion = true;
    var clip = await coordinator.ExportAsync(begin.AddSeconds(1), begin.AddSeconds(3), null, token);
    Check(
        clip.Status == "READY"
            && await MediaTransfer.HashAsync(clip.FilePath, token) == clip.FileHash,
        "Clip received hash"
    );
    string pending = Path.Combine(ClipFiles.Root, clip.ClipId.ToString("N") + ".remotejob.json");
    Check(File.Exists(pending), "DB failure lost durable clip job");
    await Until(
        () =>
            !File.Exists(
                Path.Combine(
                    childRoot,
                    "AccessControlSystem",
                    "clips",
                    "client_temp",
                    clip.ClipId.ToString("N") + ".mp4"
                )
            ),
        "Agent did not delete temporary clip after receipt"
    );
    Check(
        Directory
            .EnumerateFiles(
                Path.Combine(childRoot, "AccessControlSystem", "recordings"),
                "*.mp4",
                SearchOption.AllDirectories
            )
            .Any(),
        "Clip deleted original"
    );
    await api.DisposeAsync();
    await coordinator.DisposeAsync();
    db.FailCompletion = false;
    coordinator = new MediaCoordinator(() => db);
    api = new RemoteApiServer(Handle, coordinator.ServeAgentAsync);
    api.Start(port + 2);
    await Until(
        () => coordinator.Connected,
        "Media agent failed to reconnect after server restart"
    );
    await Until(
        () => db.Completed > 0 && !File.Exists(pending),
        "Restarted server lost durable DB completion retry",
        25000
    );
    Console.WriteLine(
        "Separate-process roots, outbound segment/clip transfer, receipt-only temp cleanup, server restart/reconnect, durable DB completion recovery: PASS"
    );
    // A stale durable request with no original coverage must settle FAILED, not loop forever.
    var stale = new ClipInfo
    {
        ClipId = Guid.NewGuid(),
        StartTime = begin.AddHours(-1),
        EndTime = begin.AddHours(-1).AddSeconds(6)
    };
    stale = stale with { FilePath = ClipFiles.PathFor(stale.ClipId) };
    string stalePending = Path.Combine(
        ClipFiles.Root,
        stale.ClipId.ToString("N") + ".remotejob.json"
    );
    await MediaTransfer.WriteJsonAsync(stalePending, stale, token);
    await Until(
        () => db.FailedCompleted > 0 && !File.Exists(stalePending),
        "Stale request stayed EXPORTING",
        25000
    );
    var failed = JsonSerializer.Deserialize<ClipInfo>(
        await File.ReadAllTextAsync(Path.ChangeExtension(stale.FilePath, ".json"), token)
    );
    Check(
        failed?.Status == "FAILED" && File.Exists(stale.FilePath + ".error.txt"),
        "Failed outcome lost reason"
    );
    Check(!File.Exists(stale.FilePath), "Failure incorrectly produced an MP4");
    Console.WriteLine(
        "Recovered stale request: terminal FAILED, durable reason, DB completion, no endless retry: PASS"
    );
    var interrupted = new ClipInfo
    {
        ClipId = Guid.NewGuid(),
        StartTime = begin.AddSeconds(2),
        EndTime = begin.AddSeconds(4)
    };
    interrupted = interrupted with { FilePath = ClipFiles.PathFor(interrupted.ClipId) };
    string partial = Path.Combine(
        childRoot,
        "AccessControlSystem",
        "clips",
        "client_temp",
        interrupted.ClipId.ToString("N") + ".mp4"
    );
    Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
    await File.WriteAllBytesAsync(partial, new byte[] { 1, 2, 3 }, token);
    string interruptedJob = Path.Combine(
        ClipFiles.Root,
        interrupted.ClipId.ToString("N") + ".remotejob.json"
    );
    await MediaTransfer.WriteJsonAsync(interruptedJob, interrupted, token);
    await Until(
        () => File.Exists(interrupted.FilePath) && !File.Exists(interruptedJob),
        "Partial encoder output prevented retry",
        25000
    );
    Check(new FileInfo(interrupted.FilePath).Length == 200000, "Partial bytes accepted as clip");
    Console.WriteLine(
        "Interrupted encoder output: safe client-temp replacement, retry succeeds, server copy retained: PASS"
    );
}
finally
{
    try
    {
        await child.StandardInput.WriteLineAsync();
        await child.WaitForExitAsync(token);
    }
    catch
    {
        child.Kill(true);
    }

    await api.DisposeAsync();
    await coordinator.DisposeAsync();
    if (Directory.Exists(childRoot))
        Directory.Delete(childRoot, true);
}

// Same-user, same-PC execution must leave the server clip intact after client receipt cleanup.
Directory.CreateDirectory(Path.GetDirectoryName(RecordingFiles.PathFor(seed))!);
var localSeed = seed with { FilePath = RecordingFiles.PathFor(seed) };
await File.WriteAllBytesAsync(localSeed.FilePath, new byte[(int)localSeed.FileSizeBytes!]);
await MediaTransfer.WriteJsonAsync(
    Path.ChangeExtension(localSeed.FilePath, ".json"),
    localSeed,
    token
);
coordinator = new MediaCoordinator(() => db);
api = new RemoteApiServer(Handle, coordinator.ServeAgentAsync);
api.Start(port + 2);
await using (var samePc = new MediaAgent())
{
    try
    {
        samePc.Start();
        await Until(() => coordinator.Connected, "Same-PC agent");
        var clip = await coordinator.ExportAsync(
            begin.AddSeconds(2),
            begin.AddSeconds(4),
            null,
            token
        );
        await Until(
            () => !File.Exists(ClipFiles.AgentPathFor(clip.ClipId)),
            "Temporary clip not cleaned"
        );
        Check(
            File.Exists(clip.FilePath)
                && await MediaTransfer.HashAsync(clip.FilePath, token) == clip.FileHash,
            "Cleanup removed server copy"
        );
        Check(File.Exists(localSeed.FilePath), "Cleanup removed original");
        Console.WriteLine(
            "Same-PC clip: distinct client temp path, receipt cleanup preserves server MP4 and original: PASS"
        );
    }
    finally
    {
        await api.DisposeAsync();
        await coordinator.DisposeAsync();
    }
}
