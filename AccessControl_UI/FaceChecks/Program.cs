// 파일 역할: 얼굴 기준과 인증 정책의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Buffers.Binary;
using System.Text.Json;
using AccessControl.Faces;

static void Check(bool value, string message)
{
    if (!value)
        throw new Exception(message);
}

static JsonElement Make(
    string pipeline = FaceApi.Pipeline,
    int dimensions = 512,
    string[]? poses = null
)
{
    var vector = new double[dimensions];
    vector[0] = 1;
    return JsonSerializer.SerializeToElement(
        new { pipeline, features = (poses ?? FaceApi.Poses).ToDictionary(p => p, _ => vector) }
    );
}

static void Reject(JsonElement value)
{
    try
    {
        FaceApi.ValidateReference(value);
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new Exception("Bad face registration accepted");
}

var reference = Make();
FaceApi.ValidateReference(reference);
Reject(Make("old-model"));
Reject(Make(dimensions: 128));
Reject(Make(poses: ["FRONT", "UP", "DOWN", "LEFT"]));
Reject(Make(poses: ["FRONT", "UP", "DOWN", "LEFT", "SIDEWAYS"]));
Reject(
    JsonSerializer.SerializeToElement(
        new
        {
            pipeline = FaceApi.Pipeline,
            features = FaceApi.Poses.ToDictionary(p => p, _ => new double[512])
        }
    )
);
Reject(
    JsonSerializer.SerializeToElement(
        new
        {
            pipeline = FaceApi.Pipeline,
            features = FaceApi.Poses.ToDictionary(
                p => p,
                _ => Enumerable.Repeat("bad", 512).ToArray()
            )
        }
    )
);
Console.WriteLine(
    "Five-direction / model / dimensions / missing direction / invalid values / zero vectors: PASS"
);
var request = new FaceRequest
{
    Operation = "save",
    PersonId = Guid.NewGuid(),
    Reference = reference
};
using (var stream = new MemoryStream())
{
    await FaceApi.WriteAsync(stream, request, 128000, default);
    stream.Position = 0;
    var restored = await FaceApi.ReadAsync<FaceRequest>(stream, 128000, default);
    Check(
        restored.RequestId == request.RequestId && restored.PersonId == request.PersonId,
        "Request identity lost"
    );
    FaceApi.ValidateReference(restored.Reference!.Value);
}

var face = new StoredFace(request.PersonId, request.RequestId, "TEST-01", "검증 직원", reference)
{
    Lease = Guid.NewGuid().ToString("N")
};
using (var stream = new MemoryStream())
{
    await FaceApi.WriteAsync(
        stream,
        new FaceReply
        {
            RequestId = request.RequestId,
            Success = true,
            Face = face
        },
        512000,
        default
    );
    stream.Position = 0;
    var restored = await FaceApi.ReadAsync<FaceReply>(stream, 512000, default);
    Check(
        restored.Face!.Name == "검증 직원"
            && restored.Face.EnrollmentId == request.RequestId
            && restored.Face.Lease == face.Lease,
        "Stored-face response changed"
    );
    FaceApi.ValidateReference(restored.Face.Reference);
}

byte[] header = new byte[4];
BinaryPrimitives.WriteInt32BigEndian(header, 128001);
try
{
    await FaceApi.ReadAsync<FaceRequest>(new MemoryStream(header), 128000, default);
    throw new Exception("Oversize accepted");
}
catch (InvalidDataException) { }

try
{
    await FaceApi.ReadAsync<FaceRequest>(new MemoryStream([0, 0]), 128000, default);
    throw new Exception("Truncation accepted");
}
catch (EndOfStreamException) { }

Console.WriteLine(
    "Request correlation / Korean response / feature JSON ownership / bounded and truncated frames: PASS"
);
var start = DateTimeOffset.UtcNow.AddMinutes(-1);
var sid = Guid.NewGuid();
var recording = new RecordingEntry
{
    SegmentId = sid,
    StartTime = start,
    EndTime = start.AddSeconds(2),
    FilePath = Path.GetFullPath(
        Path.Combine(
            RecordingFiles.Root,
            start.UtcDateTime.ToString("yyyyMMdd"),
            sid.ToString("N") + ".mp4"
        )
    ),
    Status = "READY",
    FileSizeBytes = 1000,
    FrameCount = 20,
    Fps = 10,
    Format = "mp4v",
    Width = 640,
    Height = 480
};
RecordingFiles.Validate(recording);
foreach (
    var bad in new[]
    {
        recording with
        {
            FilePath = Path.Combine(Path.GetTempPath(), "other.mp4")
        },
        recording with
        {
            FrameCount = 0
        },
        recording with
        {
            Status = "DELETED"
        },
        recording with
        {
            CameraCode = "CAM-02"
        },
        recording with
        {
            FileSizeBytes = 0
        },
        recording with
        {
            Fps = 30
        },
        recording with
        {
            EndTime = start.AddSeconds(5)
        }
    }
)
{
    bool rejected = false;
    try
    {
        RecordingFiles.Validate(bad);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    Check(rejected, "Invalid recording metadata accepted");
}

using (var stream = new MemoryStream())
{
    var sync = new FaceRequest { Operation = "recording", Recording = recording };
    await FaceApi.WriteAsync(stream, sync, 128000, default);
    stream.Position = 0;
    var decoded = await FaceApi.ReadAsync<FaceRequest>(stream, 128000, default);
    Check(decoded.Recording == recording, "Recording metadata changed during transport");
}

Console.WriteLine(
    "Recording contract / approved local paths / time-count consistency / sync request: PASS"
);
var first = recording with { StartTime = start, EndTime = start.AddSeconds(60), FrameCount = 600 };
var second = recording with
{
    SegmentId = Guid.NewGuid(),
    StartTime = start.AddSeconds(60.004),
    EndTime = start.AddSeconds(76.404),
    FrameCount = 164
};
second = second with
{
    FilePath = Path.GetFullPath(
        Path.Combine(
            RecordingFiles.Root,
            second.StartTime.UtcDateTime.ToString("yyyyMMdd"),
            second.SegmentId.ToString("N") + ".mp4"
        )
    )
};
var clips = new[] { first, second };
Check(RecordingTimeline.FindAt(clips, start) == first, "Segment start not selectable");
Check(RecordingTimeline.FindAt(clips, start.AddSeconds(59.9)) == first, "Inside segment missing");
Check(
    RecordingTimeline.FindAt(clips, start.AddSeconds(60)) is null,
    "Gap/end boundary returned old segment"
);
Check(RecordingTimeline.FindAt(clips, second.StartTime) == second, "Next segment start missing");
Check(RecordingTimeline.FindAt(clips, second.EndTime!.Value) is null, "End boundary not half-open");
Check(
    RecordingTimeline.Next(clips, first) == second && RecordingTimeline.Continuous(first, second),
    "Four millisecond rotation gap broke continuity"
);
Check(
    !RecordingTimeline.Continuous(first, second with { StartTime = start.AddSeconds(65) }),
    "Real recording gap was hidden"
);
Check(RecordingTimeline.Next(clips, second) is null, "Last segment looped");
Check(
    RecordingTimeline.TryTime(DateTime.Today, "15:53:41.007", out var instant)
        && instant.LocalDateTime.Hour == 15
        && instant.LocalDateTime.Millisecond == 7,
    "Local millisecond selection failed"
);
Check(!RecordingTimeline.TryTime(DateTime.Today, "24:15:00", out _), "Invalid local time accepted");
Check(!RecordingTimeline.TryTime(DateTime.Today, "abc", out _), "Invalid time accepted");
Console.WriteLine(
    "Timeline start/end boundaries / true gaps / rotation continuity / next segment / local millisecond time: PASS"
);
var eventRequest = new EventPlaybackRequest
{
    EventId = Guid.NewGuid(),
    EventTime = DateTimeOffset.UtcNow.AddHours(-1),
    Summary = "출입 기록 · 0511 · GRANTED"
};
EventPlaybackPipe.Validate(eventRequest);
foreach (
    var invalid in new[]
    {
        eventRequest with
        {
            EventId = Guid.Empty
        },
        eventRequest with
        {
            RequestId = Guid.Empty
        },
        eventRequest with
        {
            CameraCode = "CAM-02"
        },
        eventRequest with
        {
            EventTime = default
        },
        eventRequest with
        {
            SentAt = DateTimeOffset.UtcNow.AddSeconds(-20)
        },
        eventRequest with
        {
            SentAt = DateTimeOffset.UtcNow.AddSeconds(3)
        },
        eventRequest with
        {
            Summary = new string('x', 201)
        },
        eventRequest with
        {
            Version = 9
        }
    }
)
{
    bool rejected = false;
    try
    {
        EventPlaybackPipe.Validate(invalid);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    Check(rejected, "Invalid event playback request accepted");
}

using (var stream = new MemoryStream())
{
    await FaceApi.WriteAsync(stream, eventRequest, 4096, default);
    stream.Position = 0;
    var decoded = await FaceApi.ReadAsync<EventPlaybackRequest>(stream, 4096, default);
    Check(decoded == eventRequest, "Event identity / exact timestamp / Korean summary changed");
}

Console.WriteLine(
    "Event playback request / identity / camera / timestamp freshness / bounded Korean roundtrip: PASS"
);
var cut = ClipFiles.Plan(new[] { first }, start.AddSeconds(5), start.AddSeconds(10));
Check(
    cut.Parts.Count == 1 && cut.Parts[0].StartFrame == 50 && cut.Parts[0].FrameCount == 50,
    "Five second clip frame range wrong"
);
var joined = ClipFiles.Plan(clips, start.AddSeconds(55), start.AddSeconds(65));
Check(
    joined.Parts.Count == 2 && joined.Parts.Sum(p => p.FrameCount) == 100,
    "Cross-segment clip count wrong"
);
foreach (
    var range in new[]
    {
        (start.AddSeconds(-1), start.AddSeconds(2)),
        (start.AddSeconds(75), start.AddSeconds(80)),
        (start, start.AddSeconds(301)),
        (start, start)
    }
)
{
    bool rejected = false;
    try
    {
        ClipFiles.Plan(clips, range.Item1, range.Item2);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    Check(rejected, "Incomplete/invalid clip interval accepted");
}

bool gapRejected = false;
try
{
    ClipFiles.Plan(
        new[]
        {
            first,
            second with
            {
                StartTime = start.AddSeconds(65),
                EndTime = start.AddSeconds(81.4)
            }
        },
        start.AddSeconds(55),
        start.AddSeconds(70)
    );
}
catch (InvalidDataException)
{
    gapRejected = true;
}

Check(gapRejected, "Real recording gap exported");
var clipResult = cut.Clip with
{
    Status = "READY",
    FileSizeBytes = 1000,
    FileHash = new string('a', 64),
    FrameCount = 50,
    DurationSeconds = 5
};
ClipFiles.Validate(clipResult);
bool hashRejected = false;
try
{
    ClipFiles.Validate(clipResult with { FileHash = "bad" });
}
catch (InvalidDataException)
{
    hashRejected = true;
}

Check(hashRejected, "Invalid clip hash accepted");
Console.WriteLine(
    "Clip frame plan / cross-segment range / missing coverage / true gaps / duration limit / SHA256 metadata: PASS"
);
var nine = Make(poses: FaceReferenceRules.NinePoses);
FaceApi.ValidateReference(nine);
Check(
    FaceApi.ReferencePoses(nine).Length == 9 && FaceApi.ReferencePoses(reference).Length == 5,
    "Legacy/nine compatibility failed"
);
Reject(Make(poses: FaceReferenceRules.NinePoses[..8]));
Reject(Make(poses: ["FRONT", "UP", "DOWN", "LEFT", "RIGHT", "UP_LEFT"]));
Reject(
    Make(
        poses: ["FRONT", "UP", "DOWN", "LEFT", "RIGHT", "UP_LEFT", "UP_RIGHT", "DOWN_LEFT", "OTHER"]
    )
);
using (var nineStream = new MemoryStream())
{
    var nineRequest = new FaceRequest
    {
        Operation = "save",
        PersonId = Guid.NewGuid(),
        Reference = nine
    };
    await FaceApi.WriteAsync(nineStream, nineRequest, 128000, default);
    nineStream.Position = 0;
    var nineRead = await FaceApi.ReadAsync<FaceRequest>(nineStream, 128000, default);
    Check(
        FaceApi.ReferencePoses(nineRead.Reference!.Value).Length == 9,
        "Nine-direction save request changed"
    );
}

Console.WriteLine(
    "Legacy 5 / complete 9 / partial and unknown rejection / bounded nine save request: PASS"
);
var guided = Make(poses: FaceReferenceRules.GuidedSamples);
FaceApi.ValidateReference(guided);
Check(
    FaceApi.ReferencePoses(guided).SequenceEqual(FaceReferenceRules.GuidedSamples),
    "Guided sample keys truncated"
);
foreach (var key in FaceReferenceRules.GuidedSamples)
{
    var mapped = FaceReferenceRules.SampleKey(key);
    Check(
        mapped.Sample is 1 or 2 && FaceReferenceRules.LegacyPoses.Contains(mapped.Pose),
        "Invalid guided DB row mapping"
    );
    string restored = mapped.Pose + (mapped.Sample == 1 ? "" : "#" + mapped.Sample);
    Check(restored == key, "DB pose/sample key failed to restore");
}

Reject(Make(poses: FaceReferenceRules.GuidedSamples[..8]));
Reject(
    Make(poses: ["FRONT", "LEFT", "LEFT#3", "RIGHT", "RIGHT#2", "UP", "UP#2", "DOWN", "DOWN#2"])
);
using (var guidedStream = new MemoryStream())
{
    await FaceApi.WriteAsync(
        guidedStream,
        new FaceRequest
        {
            Operation = "save",
            PersonId = Guid.NewGuid(),
            Reference = guided
        },
        128000,
        default
    );
    guidedStream.Position = 0;
    var guidedRead = await FaceApi.ReadAsync<FaceRequest>(guidedStream, 128000, default);
    FaceApi.ValidateReference(guidedRead.Reference!.Value);
}

Console.WriteLine(
    "Guided representative samples / DB pose and sample mapping / partial rejection / bounded save roundtrip: PASS"
);
