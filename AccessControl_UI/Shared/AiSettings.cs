// 파일 역할: Python 실행 파일·AI 폴더·모델 캐시·카메라 설정을 앱 역할별로 읽고 저장합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Text.Json;

namespace AccessControl.Ai;

public sealed class AiSettings
{
    public string Python { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "anaconda3",
            "envs",
            "access_face_ai",
            "python.exe"
        );
    public string AiDirectory { get; set; } = FindAiDirectory();
    public string ModelHome { get; set; } = FindModelHome();
    public string CameraDisplayName { get; set; } = "CAM-01 · 출입구";
    public int Camera { get; set; }
    public string Backend { get; set; } = "dshow";
    public bool ReverseYaw { get; set; }
    public bool ReversePitch { get; set; }

    private static string FindAiDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "FaceAI");
            if (File.Exists(Path.Combine(candidate, "wpf_bridge.py")))
                return candidate;
            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "FaceAI");
    }

    private static string FindModelHome()
    {
        var oldCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "AccessControl_FaceAI_Python",
            "face_ai_python",
            "model_cache"
        );
        return Directory.Exists(oldCache)
            ? oldCache
            : Path.Combine(FindAiDirectory(), "model_cache");
    }

    private static string SettingsPath(string role) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessControlSystem",
            $"ai-{role}.json"
        );

    public static AiSettings Load(string role)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AiSettings>(
                File.ReadAllText(SettingsPath(role))
            );
            if (value is not null && File.Exists(Path.Combine(value.AiDirectory, "wpf_bridge.py")))
                return value;
        }
        catch { }

        return new AiSettings { Camera = 0 };
    }

    public void Save(string role)
    {
        var path = SettingsPath(role);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
        );
    }
}
