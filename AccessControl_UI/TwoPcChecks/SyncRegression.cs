// 파일 역할: TLS·직원 동기화·영상/클립 복구의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Collections.Concurrent;
using System.Text.Json;
using AccessControl.Faces;
using FaceRecognition;

internal static class SyncRegression
{
    public static async Task Run()
    {
        if (Environment.GetEnvironmentVariable("ACCESS_SYNC_TEST_ISOLATED") != "1")
            throw new Exception("Run only with isolated XDG_DATA_HOME.");
        if (Directory.Exists(RecordingFiles.Root))
            throw new Exception("Test root must be empty.");
        var original = FaceApi.RemoteCaller;
        var session = Guid.NewGuid();
        var accepted = new ConcurrentDictionary<Guid, int>();
        var failures = new ConcurrentBag<string>();
        bool recover = false;
        var good = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        try
        {
            for (int i = 1; i <= 21; i++)
            {
                var start = DateTimeOffset.UtcNow.AddMinutes(-4).AddSeconds(i * 2);
                var entry = new RecordingEntry
                {
                    SegmentId = i == 21 ? good : Guid.Parse(i.ToString("x32")),
                    StartTime = start,
                    EndTime = start.AddSeconds(1),
                    Status = "READY",
                    FrameCount = 10,
                    Fps = 10,
                    Format = "mp4v",
                    Width = 640,
                    Height = 480,
                    FileSizeBytes = 1
                };
                entry = entry with { FilePath = RecordingFiles.PathFor(entry) };
                Directory.CreateDirectory(Path.GetDirectoryName(entry.FilePath)!);
                await File.WriteAllBytesAsync(entry.FilePath, new byte[] { 1 });
                await File.WriteAllTextAsync(
                    Path.ChangeExtension(entry.FilePath, ".json"),
                    JsonSerializer.Serialize(entry)
                );
            }

            FaceApi.RemoteCaller = (request, token) =>
            {
                if (request.Operation == "recording-state")
                    return Task.FromResult(
                        new FaceReply
                        {
                            Success = true,
                            RequestId = request.RequestId,
                            DatabaseSession = session
                        }
                    );
                var entry = request.Recording ?? throw new Exception("Unexpected operation");
                if (entry.SegmentId != good && !Volatile.Read(ref recover))
                    throw new InvalidOperationException("simulated legacy conflict");
                accepted.AddOrUpdate(entry.SegmentId, 1, (_, n) => n + 1);
                return Task.FromResult(
                    new FaceReply
                    {
                        Success = true,
                        RequestId = request.RequestId,
                        RecordedSegmentId = entry.SegmentId
                    }
                );
            };
            await using var sync = new RecordingSync();
            sync.StatusChanged += failures.Add;
            sync.Start();
            await Wait(() => accepted.ContainsKey(good));
            if (accepted.Count != 1 || !failures.Any(x => x.Contains("simulated legacy conflict")))
                throw new Exception("Failure isolation or error visibility failed.");
            Console.WriteLine(
                "20 failing legacy segments do not starve a new segment; failure cause visible: PASS"
            );
            Volatile.Write(ref recover, true);
            await Wait(() => accepted.Count == 21);
            await Task.Delay(3500);
            if (accepted.Values.Any(n => n != 1))
                throw new Exception("Successful ACK was not retained.");
            Console.WriteLine(
                "Failed segments remain pending and recover; acknowledged segments not resent: PASS"
            );
        }
        finally
        {
            FaceApi.RemoteCaller = original;
            if (Directory.Exists(RecordingFiles.Root))
                Directory.Delete(RecordingFiles.Root, true);
        }
    }

    private static async Task Wait(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!ready())
            await Task.Delay(30, timeout.Token);
    }
}
