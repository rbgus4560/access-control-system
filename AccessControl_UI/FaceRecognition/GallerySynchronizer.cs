// 파일 역할: 서버 직원 목록을 주기적으로 확인합니다. 변경된 얼굴 기준을 받아 전체 검증이 끝난 스냅샷만 공개합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using AccessControl.Faces;

namespace FaceRecognition;

// Faces stay in memory. Poll metadata, reuse unchanged templates, publish a complete snapshot only.
public sealed class GallerySynchronizer(Func<bool> enabled) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private FaceGallerySnapshot? current;
    private string key = "";
    private Guid? session;
    private volatile bool valid;
    private int generation;
    private const int PollIntervalMs = 10000;

    // 비동기 처리가 끝났을 때 이전 연결의 결과를 공개하지 않도록 확인합니다.
    private void EnsureCurrentGeneration(int started)
    {
        if (started != Volatile.Read(ref generation))
            throw new InvalidOperationException("연결 설정이 변경됐습니다.");
    }

    private void ReportCurrentStatus(int count) =>
        StatusChanged?.Invoke(
            $"직원 기준 자동 동기화 · {count}명 · 최신 확인 {DateTime.Now:HH:mm:ss}"
        );

    // 빠른 서버 재연결도 이전 검증 상태를 재사용하지 않도록 세대 번호를 바꿉니다.
    public void Reset()
    {
        Interlocked.Increment(ref generation);
        Invalidate("서버 연결 후 직원 기준을 다시 동기화합니다.");
    }

    public event Action<FaceGallerySnapshot>? Synchronized;
    public event Action<string>? Invalidated;
    public event Action<string>? StatusChanged;

    public void Start() => loop = Task.Run(Run);

    private void Invalidate(string reason)
    {
        valid = false;
        Invalidated?.Invoke(reason);
        StatusChanged?.Invoke(reason);
    }

    private async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!enabled())
                {
                    if (valid)
                        Invalidate("서버 연결 후 직원 기준을 자동 동기화합니다.");
                }
                else
                    await RefreshAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Invalidate(
                    "직원 기준 동기화 재시도 · 인증 보류 · " + ex.GetBaseException().Message
                );
            }

            try
            {
                await Task.Delay(PollIntervalMs, stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // 목록을 먼저 읽고 필요한 특징만 받은 뒤 목록을 다시 확인합니다. 두 조회 사이에 변경되면 일부 기준을 공개하지 않습니다.
    public async Task RefreshAsync(CancellationToken token)
    {
        int started = Volatile.Read(ref generation);
        var catalog = await FaceApi.CallAsync(new FaceRequest { Operation = "catalog" }, token);
        if (
            catalog.DatabaseSession is not { } epoch
            || catalog.Employees is not { } employees
            || employees.Count > GalleryRules.MaxPeople
            || employees.Any(p =>
                p.PersonId == Guid.Empty || p.EnrollmentId is null || p.EnrollmentId == Guid.Empty
            )
            || employees.Select(p => p.PersonId).Distinct().Count() != employees.Count
        )
            throw new InvalidDataException("서버 직원 기준 목록을 확인하세요.");
        string nextKey = GalleryRules.CatalogKey(employees, epoch);
        EnsureCurrentGeneration(started);
        if (nextKey == key && current is not null)
        {
            if (!valid)
            {
                valid = true;
                Synchronized?.Invoke(current);
            }

            ReportCurrentStatus(employees.Count);
            return;
        }

        Invalidate($"직원 기준 자동 동기화 중 · {employees.Count}명 · 인증 보류");
        var cache = session == epoch ? current?.People.ToDictionary(p => p.PersonId) : null;
        var accepted = new List<StoredFace>();
        foreach (var person in employees)
        {
            token.ThrowIfCancellationRequested();
            StoredFace face;
            if (
                cache is not null
                && cache.TryGetValue(person.PersonId, out var old)
                && old.EnrollmentId == person.EnrollmentId
            )
                face = old with { EmployeeNo = person.EmployeeNo, Name = person.Name };
            else
            {
                var reply = await FaceApi.CallAsync(
                    new FaceRequest { Operation = "gallery-load", PersonId = person.PersonId },
                    token
                );
                face = reply.Face ?? throw new InvalidDataException("직원 얼굴 기준이 없습니다.");
            }

            if (
                face.PersonId != person.PersonId
                || face.EnrollmentId != person.EnrollmentId
                || !Guid.TryParseExact(face.Lease, "N", out _)
            )
                throw new InvalidDataException("직원 등록이 변경됐습니다. 다시 동기화합니다.");
            FaceApi.ValidateReference(face.Reference);
            accepted.Add(face);
        }

        var confirm = await FaceApi.CallAsync(new FaceRequest { Operation = "catalog" }, token);
        if (
            confirm.DatabaseSession != epoch
            || confirm.Employees is null
            || GalleryRules.CatalogKey(confirm.Employees, epoch) != nextKey
        )
            throw new InvalidOperationException(
                "동기화 중 직원 등록이 변경됐습니다. 다시 조회합니다."
            );
        EnsureCurrentGeneration(started);
        current = new(Guid.NewGuid().ToString("D"), accepted);
        key = nextKey;
        session = epoch;
        valid = true;
        Synchronized?.Invoke(current);
        ReportCurrentStatus(accepted.Count);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null)
            try
            {
                await loop;
            }
            catch (OperationCanceledException) { }

        stop.Dispose();
    }
}
