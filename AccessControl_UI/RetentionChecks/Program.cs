// 파일 역할: 녹화 보관 정리와 파일 사용권의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Text.Json;
using AccessControl.Faces;

static void Check(bool value, string message)
{
    if (!value)
        throw new Exception(message);
}

string testRoot = Path.Combine(
    Path.GetTempPath(),
    "AccessControlRetentionChecks_" + Guid.NewGuid().ToString("N")
);
string root = Path.Combine(testRoot, "recordings", "CAM-01");
Directory.CreateDirectory(root);
try
{
    var now = DateTimeOffset.UtcNow;
    var boundary = new RecordingEntry { Status = "READY", EndTime = now - TimeSpan.FromHours(72) };
    Check(RecordingRetention.IsExpired(boundary, now), "72h boundary was retained");
    Check(
        !RecordingRetention.IsExpired(
            boundary with
            {
                EndTime = boundary.EndTime!.Value.AddTicks(1)
            },
            now
        ),
        "Recent boundary was removed"
    );
    Check(
        !RecordingRetention.IsExpired(boundary with { Status = "RECORDING" }, now),
        "Active recording expired"
    );
    Console.WriteLine("72-hour end-time boundary / recent / active recording: PASS");
    RecordingEntry Create(double hours, string status = "READY")
    {
        var start = now.AddHours(-hours).AddSeconds(-10);
        var id = Guid.NewGuid();
        string path = Path.Combine(
            root,
            start.UtcDateTime.ToString("yyyyMMdd"),
            id.ToString("N") + ".mp4"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1000]);
        var e = new RecordingEntry
        {
            SegmentId = id,
            StartTime = start,
            EndTime = status == "RECORDING" ? null : start.AddSeconds(10),
            Status = status,
            FilePath = path,
            FileSizeBytes = status == "RECORDING" ? null : 1000,
            FrameCount = status == "RECORDING" ? 0 : 100,
            Fps = 10,
            Format = "mp4v",
            Width = 640,
            Height = 480
        };
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(e));
        return e;
    }

    async Task<RecordingEntry> Read(RecordingEntry e) =>
        JsonSerializer.Deserialize<RecordingEntry>(
            await RecordingFiles.ReadManifestAsync(
                Path.ChangeExtension(e.FilePath, ".json"),
                default
            )
        )!;
    var old = Create(73);
    var recent = Create(71);
    var active = Create(80, "RECORDING");
    var failed = Create(80, "FAILED");
    string clip = Path.Combine(testRoot, "clips", "keep.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(clip)!);
    File.WriteAllText(clip, "CLIP");
    string unknown = Path.Combine(Path.GetDirectoryName(old.FilePath)!, "untracked.mp4");
    File.WriteAllText(unknown, "UNTRACKED");
    var runner = new RecordingRetention(root);
    var result = await runner.RunAsync();
    Check(result.Deleted == 1 && !File.Exists(old.FilePath), "Expired original was not deleted");
    var tombstone = await Read(old);
    Check(
        tombstone.Status == "DELETED"
            && tombstone.DeletedAt is not null
            && tombstone.DeletionReason == RecordingRetention.Reason,
        "Deleted metadata missing"
    );
    RecordingFiles.ValidateAtRoot(tombstone, root);
    Check(
        new[] { recent, active, failed }.All(e => File.Exists(e.FilePath))
            && File.Exists(clip)
            && File.Exists(unknown),
        "Protected/recent/untracked file removed"
    );
    Check((await runner.RunAsync()).Deleted == 0, "Tombstone repeatedly deleted");
    Console.WriteLine(
        "Expired media removal / durable tombstone / recent and RECORDING/FAILED preserved / clips excluded / untracked preserved / idempotency: PASS"
    );
    var pinned = Create(75);
    using (var use = RecordingMediaUse.TryUse([pinned.FilePath]))
    {
        Check(use is not null, "Cannot pin playback");
        result = await runner.RunAsync();
        Check(
            result.Deferred >= 1
                && File.Exists(pinned.FilePath)
                && (await Read(pinned)).Status == "READY",
            "Playback file deleted"
        );
        Check(RecordingMediaUse.TryDelete(pinned.FilePath) is null, "Delete lock ignored pin");
    }

    Check((await runner.RunAsync()).Deleted == 1, "Released playback file not cleaned");
    Console.WriteLine("Playback/whole clip-job pin / cleanup contention / release and retry: PASS");
    var unfinished = Create(76);
    var intent = unfinished with
    {
        Status = "DELETING",
        DeletionReason = RecordingRetention.Reason
    };
    await RecordingRetention.WriteManifestAsync(
        Path.ChangeExtension(intent.FilePath, ".json"),
        intent,
        default
    );
    File.Delete(intent.FilePath); // Simulate shutdown after unlink and before final metadata.
    Check(
        (await runner.RunAsync()).Deleted == 1 && (await Read(intent)).Status == "DELETED",
        "Interrupted deletion not recovered"
    );
    var pending = Create(77);
    await RecordingRetention.WriteManifestAsync(
        Path.ChangeExtension(pending.FilePath, ".json"),
        pending with
        {
            Status = "DELETING",
            DeletionReason = RecordingRetention.Reason
        },
        default
    );
    Check(
        (await runner.RunAsync()).Deleted == 1 && !File.Exists(pending.FilePath),
        "Pending delete not resumed"
    );
    Console.WriteLine("Crash after media deletion / intent before deletion / restart resume: PASS");
    var forged = Create(78);
    string original = forged.FilePath;
    string outside = Path.Combine(testRoot, "outside.mp4");
    File.WriteAllBytes(outside, new byte[1000]);
    File.WriteAllText(
        Path.ChangeExtension(original, ".json"),
        JsonSerializer.Serialize(forged with { FilePath = outside })
    );
    result = await runner.RunAsync();
    Check(
        result.Invalid >= 1 && File.Exists(outside) && File.Exists(original),
        "Outside path removed"
    );
    var changed = Create(79);
    File.WriteAllBytes(changed.FilePath, new byte[999]);
    result = await runner.RunAsync();
    Check(result.Invalid >= 2 && File.Exists(changed.FilePath), "Changed media removed");
    Console.WriteLine("Out-of-root manifest / modified file-size rejection: PASS");
    string links = Path.Combine(testRoot, "links");
    Directory.CreateDirectory(links);
    var linkTarget = Create(81);
    string targetFolder = Path.GetDirectoryName(linkTarget.FilePath)!;
    string linkedRoot = Path.Combine(links, "CAM-01");
    try
    {
        Directory.CreateSymbolicLink(linkedRoot, root);
        bool rejected = false;
        try
        {
            await new RecordingRetention(linkedRoot).RunAsync();
        }
        catch (IOException)
        {
            rejected = true;
        }

        Check(rejected && File.Exists(linkTarget.FilePath), "Link root was traversed");
        Directory.Delete(linkedRoot);
        Console.WriteLine("Symlink/junction root rejection: PASS");
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine(
            "Symbolic link creation requires OS privilege; Windows link check pending."
        );
    }

    using (var stream = new MemoryStream())
    {
        await FaceApi.WriteAsync(
            stream,
            new FaceRequest { Operation = "recording", Recording = tombstone },
            128000,
            default
        );
        stream.Position = 0;
        var decoded = await FaceApi.ReadAsync<FaceRequest>(stream, 128000, default);
        Check(decoded.Recording == tombstone, "Deletion fields lost in DB sync request");
    }

    Console.WriteLine("DELETED metadata transport roundtrip: PASS");
    Console.WriteLine(
        "Only this test's temporary files were used. Production recordings and DB were not changed."
    );
}
finally
{
    Directory.Delete(testRoot, true);
}
