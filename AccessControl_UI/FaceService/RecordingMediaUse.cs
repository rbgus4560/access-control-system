// 파일 역할: 재생·클립 추출과 보관 정리가 같은 파일을 동시에 사용하지 않도록 사용권을 관리합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;

namespace AccessControl.Faces;

// In-process protection shared by playback, whole clip jobs and retention.
// Windows file sharing additionally protects files opened by other programs.
public static class RecordingMediaUse
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, int> readers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> writers = new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable? TryUse(IEnumerable<string> paths) => Acquire(paths, false);

    public static IDisposable? TryDelete(string path) => Acquire([path], true);

    private static IDisposable? Acquire(IEnumerable<string> values, bool exclusive)
    {
        string[] paths = values
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (gate)
        {
            if (paths.Any(p => writers.Contains(p) || (exclusive && readers.ContainsKey(p))))
                return null;
            foreach (string p in paths)
                if (exclusive)
                    writers.Add(p);
                else
                    readers[p] = readers.GetValueOrDefault(p) + 1;
            return new Lease(paths, exclusive);
        }
    }

    private sealed class Lease(string[] paths, bool exclusive) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                foreach (string p in paths)
                    if (exclusive)
                        writers.Remove(p);
                    else if (readers[p] == 1)
                        readers.Remove(p);
                    else
                        readers[p]--;
            }
        }
    }
}
