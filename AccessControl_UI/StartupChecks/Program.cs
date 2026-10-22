using System.IO;
using System.Security.Cryptography;
using System.Text;
using AccessControlServer;

// 실제 사용자 설정 대신 새 임시 디렉터리에서 Windows DPAPI 저장·복원·삭제를 검증합니다.
var root = Path.Combine(Path.GetTempPath(), "AccessControl.StartupChecks." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new ServerSettingsStore(root);
    void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine(label + ": PASS");
    }
    Check(store.Load() is null, "Missing settings do not enable auto connection");
    var settings = new ServerStartupSettings(new("127.0.0.1", 5432, "test-only", "test-user"), "synthetic-secret-20261022", 5050);
    store.Save(settings);
    var file = Path.Combine(root, "server-startup.bin");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(settings.Password), "Password is not stored as plaintext");
    Check(store.Load() == settings, "Windows user DPAPI round trip");
    var replacement = settings with { TcpPort = 6060, Password = "replacement-test-secret" };
    store.Save(replacement);
    Check(store.Load() == replacement && !File.Exists(file + ".tmp"), "Atomic replacement keeps latest profile");
    File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
    bool rejected = false;
    try { store.Load(); }
    catch (CryptographicException) { rejected = true; }
    Check(rejected, "Corrupt encrypted settings are rejected");
    store.Forget();
    store.Forget();
    Check(!File.Exists(file) && store.Load() is null, "Forget is repeatable and removes persisted credentials");
}
finally
{
    // 이번 실행에서 만든 고유한 임시 폴더만 정리합니다.
    if (Path.GetFileName(root).StartsWith("AccessControl.StartupChecks.") && Path.GetDirectoryName(root) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
        Directory.Delete(root, true);
}
