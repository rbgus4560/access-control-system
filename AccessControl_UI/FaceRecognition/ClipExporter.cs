// 파일 역할: 독립 Python 프로세스에 클립 계획을 전달하고 제한 시간 내 MP4 추출 결과를 받습니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AccessControl.Ai;
using AccessControl.Faces;

namespace FaceRecognition;

internal static class ClipExporter
{
    internal static async Task<ClipInfo> RunPython(ClipJob job, CancellationToken token)
    {
        var settings = AiSettings.Load("recognition");
        string script = Path.Combine(settings.AiDirectory, "export_clip.py");
        if (!File.Exists(script))
            throw new FileNotFoundException("이번 버전의 FaceAI 폴더를 선택해 주세요.");
        string plan = Path.ChangeExtension(job.Clip.FilePath, ".job.json");
        await File.WriteAllTextAsync(
            plan,
            JsonSerializer.Serialize(job),
            new UTF8Encoding(false),
            token
        );
        var info = new ProcessStartInfo(settings.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = settings.AiDirectory
        };
        foreach (string arg in new[] { "-u", script, "--job", plan })
            info.ArgumentList.Add(arg);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        string pythonRoot = Path.GetDirectoryName(settings.Python)!;
        info.Environment["PATH"] =
            string.Join(
                Path.PathSeparator,
                new[]
                {
                    pythonRoot,
                    Path.Combine(pythonRoot, "Library", "bin"),
                    Path.Combine(pythonRoot, "Scripts")
                }
            )
            + Path.PathSeparator
            + (info.Environment.TryGetValue("PATH", out var old) ? old : "");
        using var process = Process.Start(info) ?? throw new IOException("Python 실행 실패");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }

            throw;
        }

        string text = await output,
            diagnostic = await error;
        if (process.ExitCode != 0)
            throw new IOException(diagnostic.Length > 500 ? diagnostic[..500] : diagnostic);
        if (text.Length > 8192)
            throw new InvalidDataException("클립 결과 크기 초과");
        return JsonSerializer.Deserialize<ClipInfo>(text)
            ?? throw new InvalidDataException("클립 결과가 없습니다.");
    }
}
