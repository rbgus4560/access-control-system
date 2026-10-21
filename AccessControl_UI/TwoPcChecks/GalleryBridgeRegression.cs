// 파일 역할: TLS·직원 동기화·영상/클립 복구의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Text.Json;
using AccessControl.Ai;
using AccessControl.Faces;

internal static class GalleryBridgeRegression
{
    private static void Check(bool ok, string reason)
    {
        if (!ok)
            throw new Exception(reason);
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(15000);
        while (!ready())
            await Task.Delay(30, timeout.Token);
    }

    public static async Task Run()
    {
        string root =
            Environment.GetEnvironmentVariable("ACCESS_FACEAI_ROOT")
            ?? throw new Exception("Set ACCESS_FACEAI_ROOT");
        string folder = Path.Combine(
            Path.GetTempPath(),
            "AccessGalleryBridge-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(folder);
        File.Copy(
            Path.Combine(root, "tests", "gallery_bridge_fixture.py"),
            Path.Combine(folder, "wpf_bridge.py")
        );
        File.Copy(Path.Combine(root, "face_gallery.py"), Path.Combine(folder, "face_gallery.py"));
        var settings = new AiSettings
        {
            Python = Environment.GetEnvironmentVariable("ACCESS_TEST_PYTHON")!,
            AiDirectory = folder,
            ModelHome = folder
        };
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid(),
            duplicate = Guid.NewGuid();
        object Reference(int index)
        {
            var vector = new double[512];
            vector[index] = 1;
            return new
            {
                pipeline = FaceApi.Pipeline,
                features = FaceApi.Poses.ToDictionary(p => p, _ => vector)
            };
        }

        await using var client = new AiClient();
        async Task<string> Load((Guid Id, int Feature)[] people)
        {
            string id = Guid.NewGuid().ToString("D");
            await client.SendAppliedAsync(
                "gallery_begin",
                new { gallery_id = id, count = people.Length },
                default
            );
            foreach (var person in people)
                await client.SendAppliedAsync(
                    "gallery_add",
                    new
                    {
                        person_id = person.Id.ToString("D"),
                        reference = Reference(person.Feature)
                    },
                    default
                );
            await client.SendAppliedAsync("gallery_commit", new { gallery_id = id }, default);
            Check(client.State?.State.GalleryId == id, "Applied barrier returned before commit");
            return id;
        }

        try
        {
            client.Start(settings, "recognition");
            await Until(() => client.State is not null);
            await Load([(a, 0), (b, 1)]);
            await Until(() =>
                client.State?.State.Matching == true
                && client.State.State.Identification?.Result == "MATCH"
            );
            Check(
                client.State!.State.Identification!.PersonId == a.ToString("D"),
                "Employee A not identified"
            );
            await client.SendAppliedAsync("test_person", new { person = 1 }, default);
            await Until(() => client.State?.State.Identification?.PersonId == b.ToString("D"));
            Check(
                client.State!.State.Identification!.Result == "MATCH",
                "Employee B needed manual selection"
            );
            await client.SendAppliedAsync("t", new { }, default);
            await Load([(b, 1)]);
            Check(!client.State!.State.Matching, "Roster refresh resumed manual pause");
            await client.SendAppliedAsync("t", new { }, default);
            await client.SendAppliedAsync("test_person", new { person = 0 }, default);
            await Until(() => client.State?.State.Identification?.Result == "NO_MATCH");
            await Load([(a, 0), (duplicate, 0)]);
            await Until(() => client.State?.State.Identification?.Result == "AMBIGUOUS");
            await Load([]);
            Check(
                !client.State!.State.Matching && client.State.State.GalleryCount == 0,
                "Empty roster kept comparison running"
            );
            await client.StopAsync();
            client.Start(settings, "recognition");
            await Until(() => client.State is not null);
            await Load([(b, 0)]);
            await Until(() => client.State?.State.Identification?.Result == "MATCH");
            Check(
                client.State!.State.Identification!.PersonId == b.ToString("D"),
                "Camera restart did not apply gallery and auto start"
            );
            Console.WriteLine(
                "Production C# AI client + Python worker: staged ACKs, A/B automatic identity, removal/no-match, ambiguous hold, pause through refresh, empty roster, camera restart: PASS"
            );
        }
        finally
        {
            await client.StopAsync();
            Directory.Delete(folder, true);
        }
    }
}
