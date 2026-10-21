// 파일 역할: TLS·직원 동기화·영상/클립 복구의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using AccessControl.Faces;

namespace AccessControlServer
{
    // Integration-test DB double only: PostgreSQL DDL/UPSERT is checked separately.
    public sealed class FaceDatabase
    {
        public int Begun,
            Completed,
            FailedCompleted;
        public bool FailCompletion;

        public Task<Guid> BeginClipAsync(ClipInfo clip, CancellationToken token)
        {
            Interlocked.Increment(ref Begun);
            return Task.FromResult(clip.ClipId);
        }

        public Task<Guid> CompleteClipAsync(ClipInfo clip, CancellationToken token)
        {
            if (FailCompletion)
                throw new IOException("DB offline for test");
            if (clip.Status == "READY" && !File.Exists(clip.FilePath))
                throw new IOException();
            if (clip.Status == "FAILED")
                Interlocked.Increment(ref FailedCompleted);
            Interlocked.Increment(ref Completed);
            return Task.FromResult(clip.ClipId);
        }
    }
}

namespace FaceRecognition
{
    public static class ClipExporter
    {
        // This double replaces Windows/Python execution only. The production agent, planner, file/hash transport are real.
        public static async Task<ClipInfo> RunPython(ClipJob job, CancellationToken token)
        {
            if (File.Exists(job.Clip.FilePath))
                throw new IOException("Invalid or existing output");
            byte[] data = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(job.Clip.FilePath, data, token);
            return job.Clip with
            {
                Status = "READY",
                FrameCount = job.Parts.Sum(p => p.FrameCount),
                DurationSeconds = job.Parts.Sum(p => p.FrameCount) / 10.0,
                FileSizeBytes = data.Length,
                FileHash = await MediaTransfer.HashAsync(job.Clip.FilePath, token)
            };
        }
    }
}
