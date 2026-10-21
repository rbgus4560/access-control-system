// 파일 역할: 인증서 고정과 연결 토큰으로 TLS 채널을 확인합니다. 인증·영상·API 채널을 구분합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AccessControl.Network;

public sealed record ClientLink(string Host, int Port, string Token, string Fingerprint)
{
    public void Validate()
    {
        if (
            !IPAddress.TryParse(Host, out var address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || Port is < 1024 or > 65533
            || Token is not { Length: 64 }
            || Fingerprint is not { Length: 64 }
            || !Token.All(Uri.IsHexDigit)
            || !Fingerprint.All(Uri.IsHexDigit)
        )
            throw new InvalidDataException("서버 연결 코드를 확인하세요.");
    }
}

public sealed record ServerLink(string Token, X509Certificate2 Certificate);

public static class JsonWire
{
    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        int limit,
        CancellationToken token
    )
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > limit)
            throw new InvalidDataException("통신 크기 초과");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, int limit, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 2 || length > limit)
            throw new InvalidDataException("통신 크기 초과");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { MaxDepth = 16 })
            ?? throw new InvalidDataException("빈 통신 데이터");
    }
}

public static class LinkSecurity
{
    public static ClientLink? Client { get; set; }
    public static ServerLink? Server { get; set; }
    public static IPAddress BindAddress => Server is null ? IPAddress.Loopback : IPAddress.Any;

    private sealed record Hello(string Channel, string Token);

    private sealed record HelloReply(bool Accepted);

    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static async Task<Stream> ConnectAsync(
        TcpClient tcp,
        string channel,
        CancellationToken token
    )
    {
        var profile = Client;
        if (profile is null)
            return tcp.GetStream();
        profile.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(8000);
        var ssl = new SslStream(
            tcp.GetStream(),
            false,
            (_, certificate, _, _) =>
            {
                if (certificate is null)
                    return false;
                using var cert = X509CertificateLoader.LoadCertificate(
                    certificate.GetRawCertData()
                );
                return cert.NotBefore.ToUniversalTime() <= DateTime.UtcNow
                    && cert.NotAfter.ToUniversalTime() > DateTime.UtcNow
                    && CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(cert.RawData),
                        Convert.FromHexString(profile.Fingerprint)
                    );
            }
        );
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "AccessControlServer",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowRenegotiation = false
                },
                timeout.Token
            );
            await JsonWire.WriteAsync(ssl, new Hello(channel, profile.Token), 1024, timeout.Token);
            if (!(await JsonWire.ReadAsync<HelloReply>(ssl, 128, timeout.Token)).Accepted)
                throw new AuthenticationException("연결 코드 불일치");
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }

    public static async Task<Stream> AcceptAsync(
        TcpClient tcp,
        string channel,
        CancellationToken token
    )
    {
        var profile = Server;
        if (profile is null)
            return tcp.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(8000);
        var ssl = new SslStream(tcp.GetStream(), false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = profile.Certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    AllowRenegotiation = false
                },
                timeout.Token
            );
            var hello = await JsonWire.ReadAsync<Hello>(ssl, 1024, timeout.Token);
            if (
                hello.Channel != channel
                || hello.Token is null
                || hello.Token.Length != 64
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(hello.Token),
                    Encoding.UTF8.GetBytes(profile.Token)
                )
            )
                throw new AuthenticationException("장치 인증 실패");
            await JsonWire.WriteAsync(ssl, new HelloReply(true), 128, timeout.Token);
            return ssl;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }
}
