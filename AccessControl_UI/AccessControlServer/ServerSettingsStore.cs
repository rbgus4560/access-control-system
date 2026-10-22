using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("StartupChecks")]

namespace AccessControlServer;

// 자동 DB 연결은 명시적으로 선택한 경우에만 저장합니다. 평문 파일·저장소에 비밀번호를 남기지 않습니다.
internal sealed record ServerStartupSettings(DatabaseEndpoint Endpoint, string Password, int TcpPort);

internal sealed class ServerSettingsStore
{
    private string PathName { get; }
    public ServerSettingsStore(string? directory = null) => PathName = Path.Combine(
        directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccessControlSystem"),
        "server-startup.bin");
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("AccessControl.ServerStartup.v1");

    public ServerStartupSettings? Load()
    {
        if (!File.Exists(PathName)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(PathName), Purpose, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<ServerStartupSettings>(bytes)
            ?? throw new InvalidDataException("서버 시작 설정을 읽을 수 없습니다.");
    }

    public void Save(ServerStartupSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(settings), Purpose, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathName + ".tmp", encrypted);
        File.Move(PathName + ".tmp", PathName, true);
    }

    public void Forget()
    {
        if (File.Exists(PathName)) File.Delete(PathName);
    }
}
