// 파일 역할: 서버 인증서와 클라이언트 연결 정보를 Windows 계정에 묶어 보관합니다. 연결 코드 생성·파싱도 담당합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AccessControl.Network;

public static class PairingStore
{
    private sealed record ServerSecret(string Token, string Pfx);

    private static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccessControlSystem",
            "network"
        );
    private static byte[] Purpose => Encoding.UTF8.GetBytes("AccessControl.LAN.v1");

    private static void Save(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(
            path + ".tmp",
            ProtectedData.Protect(bytes, Purpose, DataProtectionScope.CurrentUser)
        );
        File.Move(path + ".tmp", path, true);
    }

    private static byte[] Read(string name) =>
        ProtectedData.Unprotect(
            File.ReadAllBytes(Path.Combine(Root, name)),
            Purpose,
            DataProtectionScope.CurrentUser
        );

    public static ServerLink Server()
    {
        if (!File.Exists(Path.Combine(Root, "server.bin")))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest(
                "CN=AccessControlServer",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true)
            );
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    true
                )
            );
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(2)
            );
            Save(
                "server.bin",
                JsonSerializer.SerializeToUtf8Bytes(
                    new ServerSecret(
                        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                        Convert.ToBase64String(cert.Export(X509ContentType.Pfx))
                    )
                )
            );
        }

        byte[] bytes = Read("server.bin");
        try
        {
            var data =
                JsonSerializer.Deserialize<ServerSecret>(bytes) ?? throw new InvalidDataException();
            // Windows Schannel needs a named user key container for TLS credentials.
            // Keep the existing certificate and pairing token; do not regenerate identity.
            var flags = OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.UserKeySet
                : X509KeyStorageFlags.EphemeralKeySet;
            var certificate = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(data.Pfx),
                null,
                flags
            );
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new CryptographicException(
                    "서버 TLS 인증서의 개인 키를 불러오지 못했습니다."
                );
            }

            return new(data.Token, certificate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Code(ServerLink profile, string host, int port)
    {
        var client = new ClientLink(
            host,
            port,
            profile.Token,
            LinkSecurity.Fingerprint(profile.Certificate)
        );
        client.Validate();
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(client));
    }

    public static ClientLink Parse(string code)
    {
        if (code.Length > 4096)
            throw new InvalidDataException("연결 코드 크기 초과");
        var value =
            JsonSerializer.Deserialize<ClientLink>(Convert.FromBase64String(code.Trim()))
            ?? throw new InvalidDataException("연결 코드 오류");
        value.Validate();
        return value;
    }

    public static void SaveClient(ClientLink profile)
    {
        profile.Validate();
        Save("client.bin", JsonSerializer.SerializeToUtf8Bytes(profile));
    }

    public static ClientLink? LoadClient()
    {
        if (!File.Exists(Path.Combine(Root, "client.bin")))
            return null;
        byte[] b = Read("client.bin");
        try
        {
            var p = JsonSerializer.Deserialize<ClientLink>(b);
            p?.Validate();
            return p;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(b);
        }
    }
}
