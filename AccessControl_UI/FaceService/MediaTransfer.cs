// 파일 역할: 파일을 분할 스트림으로 전송하고 크기·SHA-256을 확인합니다. 검증된 임시 파일만 최종 위치로 이동합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AccessControl.Faces;

public static class MediaTransfer
{
    public const long MaxBytes = 500_000_000;

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    public static void ValidateHeader(MediaResponse response)
    {
        if (
            !response.Success
            || response.Length is < 1 or > MaxBytes
            || response.Hash is not { Length: 64 }
            || !response.Hash.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')
        )
            throw new InvalidDataException("영상 전송 크기·해시 오류");
    }

    // 별도 임시 파일에 수신하고 해시가 일치한 뒤 교체합니다. 전송 실패가 정상 사본을 덮어쓰지 않게 합니다.
    public static async Task ReceiveAsync(
        Stream source,
        string destination,
        MediaResponse response,
        CancellationToken token,
        long reserve = 20L * 1024 * 1024 * 1024
    )
    {
        ValidateHeader(response);
        RecordingRetention.NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        RecordingRetention.NoLinks(destination);
        if (
            new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace
            < reserve + response.Length
        )
            throw new IOException("서버 저장 공간이 부족합니다.");
        string part = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (
                var file = new FileStream(
                    part,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.Asynchronous
                )
            )
            {
                byte[] buffer = new byte[65536];
                long left = response.Length;
                while (left > 0)
                {
                    int got = await source.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)),
                        token
                    );
                    if (got == 0)
                        throw new EndOfStreamException("영상 전송이 중단됐습니다.");
                    hash.AppendData(buffer, 0, got);
                    await file.WriteAsync(buffer.AsMemory(0, got), token);
                    left -= got;
                }

                await file.FlushAsync(token);
                file.Flush(true);
            }

            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != response.Hash)
                throw new InvalidDataException("영상 해시 불일치");
            File.Move(part, destination, true);
        }
        finally
        {
            if (File.Exists(part))
                File.Delete(part);
        }
    }

    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        RecordingRetention.NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RecordingRetention.NoLinks(path);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value), token);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
