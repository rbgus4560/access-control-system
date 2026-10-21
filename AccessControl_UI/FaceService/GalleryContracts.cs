// 파일 역할: 전체 직원 기준 스냅샷과 목록 변경 감지용 키를 정의합니다. 얼굴 특징은 스냅샷 메모리에 유지합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.Security.Cryptography;
using System.Text;

namespace AccessControl.Faces;

public sealed record FaceGallerySnapshot(string Id, IReadOnlyList<StoredFace> People);

public static class GalleryRules
{
    public const int MaxPeople = 1000;

    public static string CatalogKey(IEnumerable<FaceEmployee> people, Guid session) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    session.ToString("N")
                        + "|"
                        + string.Join(
                            "|",
                            people
                                .OrderBy(p => p.PersonId)
                                .Select(p =>
                                    $"{p.PersonId}:{p.EnrollmentId}:{p.EmployeeNo}:{p.Name}:{p.Active}:{p.AccessEnabled}"
                                )
                        )
                )
            )
        );
}
