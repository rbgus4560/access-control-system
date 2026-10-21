// 파일 역할: TLS·직원 동기화·영상/클립 복구의 개발용 검증 코드입니다. 실행 조건과 대역 사용 범위는 해당 검사 폴더의 안내를 참고합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Text.Json;
using AccessControl.Faces;
using AccessControl.Ai;
using AccessControl.Network;
using FaceRecognition;

internal static class GalleryRegression
{
    private static void Check(bool ok, string reason)
    {
        if (!ok)
            throw new Exception(reason);
    }

    private static async Task Reject(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Exception("Invalid gallery accepted");
    }

    public static async Task Run()
    {
        var previous = FaceApi.RemoteCaller;
        var epoch = Guid.NewGuid();
        int loads = 0,
            published = 0;
        bool fail = false,
            mutate = false;
        var people = Enumerable
            .Range(0, 3)
            .Select(i => new FaceEmployee(
                Guid.NewGuid(),
                "EMP" + i,
                "직원" + i,
                i != 1,
                i != 2,
                Guid.NewGuid()
            ))
            .ToList();
        var vector = new double[512];
        vector[0] = 1;
        var reference = JsonSerializer.SerializeToElement(
            new
            {
                pipeline = FaceApi.Pipeline,
                features = FaceApi.Poses.ToDictionary(p => p, _ => vector)
            }
        );
        FaceGallerySnapshot? current = null;
        await using var sync = new GallerySynchronizer(() => true);
        sync.Synchronized += value =>
        {
            current = value;
            published++;
        };
        FaceApi.RemoteCaller = (r, token) =>
        {
            if (r.Operation == "catalog")
                return Task.FromResult(
                    new FaceReply
                    {
                        Success = true,
                        RequestId = r.RequestId,
                        Employees = people.ToList(),
                        DatabaseSession = epoch
                    }
                );
            if (r.Operation != "gallery-load")
                throw new Exception("Unexpected request");
            loads++;
            var p = people.Single(p => p.PersonId == r.PersonId);
            if (fail)
                throw new InvalidDataException("simulated corrupt template");
            if (mutate)
            {
                mutate = false;
                people.Add(new(Guid.NewGuid(), "ADDED", "새 직원", true, true, Guid.NewGuid()));
            }

            return Task.FromResult(
                new FaceReply
                {
                    Success = true,
                    RequestId = r.RequestId,
                    Face = new(p.PersonId, p.EnrollmentId!.Value, p.EmployeeNo, p.Name, reference)
                    {
                        Lease = Guid.NewGuid().ToString("N")
                    }
                }
            );
        };
        try
        {
            await sync.RefreshAsync(default);
            Check(
                loads == 3 && current?.People.Count == 3,
                "All employees, including inactive and access-disabled, must reach server policy"
            );
            string first = current!.Id;
            await sync.RefreshAsync(default);
            Check(
                loads == 3 && published == 1 && current.Id == first,
                "Unchanged catalog reloaded templates"
            );
            people[0] = people[0] with { EnrollmentId = Guid.NewGuid() };
            await sync.RefreshAsync(default);
            Check(loads == 4 && current!.Id != first, "Changed enrollment not refreshed");
            people.RemoveAt(1);
            await sync.RefreshAsync(default);
            Check(current!.People.Count == 2 && loads == 4, "Removal not applied atomically");
            epoch = Guid.NewGuid();
            await sync.RefreshAsync(default);
            Check(loads == 6, "Server restart did not reload leases");
            int before = published;
            people[0] = people[0] with { EnrollmentId = Guid.NewGuid() };
            fail = true;
            await Reject(() => sync.RefreshAsync(default));
            Check(published == before, "Partial failed snapshot published");
            fail = false;
            mutate = true;
            await Reject(() => sync.RefreshAsync(default));
            Check(published == before, "Catalog race published mixed snapshot");
            await sync.RefreshAsync(default);
            Check(current!.People.Count == 3, "Retry did not recover complete roster");
            before = published;
            sync.Reset();
            await sync.RefreshAsync(default);
            Check(published == before + 1, "Fast reconnect did not revalidate unchanged snapshot");
            people.Clear();
            await sync.RefreshAsync(default);
            Check(current!.People.Count == 0, "Empty roster kept stale faces");
            Console.WriteLine(
                "Gallery sync: automatic all-person roster, cache reuse, registration update/removal, restart leases, corrupt/racing batch rejection, reconnect and empty roster: PASS"
            );
        }
        finally
        {
            FaceApi.RemoteCaller = previous;
        }

        var observation = new ObservationSource();
        var identity = new ReferenceIdentity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N")
        );
        var state = new AiState
        {
            Reason = "OK",
            Pose = "FRONT",
            Matching = true,
            Calibrated = true,
            ReferenceCount = 5,
            Comparison = JsonSerializer.SerializeToElement(new object[] { .10, "FRONT", true }),
            Identification = new IdentificationState
            {
                Result = "MATCH",
                PersonId = identity.PersonId.ToString(),
                RunnerUpDistance = .12
            }
        };
        observation.Update(state, true, 10, identity);
        var ambiguous = observation.Get();
        Check(
            ambiguous.Status == "PAUSED"
                && ambiguous.Reason == "AMBIGUOUS_MATCH"
                && ambiguous.Distance is null
                && ambiguous.AttemptId is null,
            "Ambiguous candidate reached authentication"
        );
        MonitorProtocol.Validate(
            new MonitorPacket
            {
                Type = "snapshot",
                Session = Guid.NewGuid().ToString(),
                Sequence = 1,
                Snapshot = ambiguous
            },
            "snapshot"
        );
        state.Identification.RunnerUpDistance = .3;
        observation.Update(state, true, 10, identity);
        var match = observation.Get();
        Check(
            match.Status == "MATCH_CANDIDATE"
                && match.Identity == identity
                && match.RunnerUpDistance == .3,
            "Winner identity or runner-up lost"
        );
        MonitorProtocol.Validate(
            new MonitorPacket
            {
                Type = "snapshot",
                Session = Guid.NewGuid().ToString(),
                Sequence = 1,
                Snapshot = match
            },
            "snapshot"
        );
        Check(
            AccessPolicy.Evaluate(true, true, true, false, true).Reason == "ACCESS_DISABLED",
            "Disabled permission granted"
        );
        Check(
            AccessPolicy.Evaluate(true, false, true, true, true).Reason == "PERSON_INACTIVE",
            "Inactive person granted"
        );
        state.Comparison = JsonSerializer.SerializeToElement(new object[] { .8, "FRONT", false });
        state.Identification.Result = "NO_MATCH";
        state.Identification.RunnerUpDistance = .9;
        observation.Update(state, true, 10, identity);
        var unknown = observation.Get();
        observation.Update(
            state,
            true,
            10,
            new ReferenceIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"))
        );
        Check(
            observation.Get().AttemptId == unknown.AttemptId,
            "Unknown closest-reference fluctuations created duplicate attempts"
        );
        Console.WriteLine(
            "Gallery observation: ambiguous scores held, winner identity/runner-up preserved, protocol valid, inactive/no-permission deny policy: PASS"
        );
    }
}
