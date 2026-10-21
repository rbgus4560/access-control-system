// 파일 역할: 기존 5방향·9방향·연속 등록 대표 샘플의 구조와 512차원 특징 유효성을 확인합니다.
// 기능 기준: 12.3 정상 동작 버전. 코드 설명과 서식을 정리한 소스입니다.

using System.IO;
using System.Text.Json;

namespace AccessControl.Faces;

public static class FaceReferenceRules
{
    public const string Pipeline = "arcface512_df093_dlibfast480_eyealign_v1";
    public static readonly string[] LegacyPoses = ["FRONT", "UP", "DOWN", "LEFT", "RIGHT"];
    public static readonly string[] NinePoses =
    [
        "FRONT",
        "UP",
        "UP_RIGHT",
        "RIGHT",
        "DOWN_RIGHT",
        "DOWN",
        "DOWN_LEFT",
        "LEFT",
        "UP_LEFT"
    ];
    public static readonly string[] GuidedSamples =
    [
        "FRONT",
        "LEFT",
        "LEFT#2",
        "RIGHT",
        "RIGHT#2",
        "UP",
        "UP#2",
        "DOWN",
        "DOWN#2"
    ];

    public static (string Pose, int Sample) SampleKey(string key)
    {
        var parts = key.Split('#');
        return (parts[0], parts.Length == 1 ? 1 : int.Parse(parts[1]));
    }

    public static bool CompleteCount(int count) => count is 5 or 9;

    public static string[] Validate(JsonElement reference)
    {
        if (
            reference.ValueKind != JsonValueKind.Object
            || !reference.TryGetProperty("pipeline", out var pipeline)
            || pipeline.ValueKind != JsonValueKind.String
            || pipeline.GetString() != Pipeline
            || !reference.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Object
        )
            throw new InvalidDataException("호환되는 얼굴 특징이 필요합니다.");
        string[] names = features.EnumerateObject().Select(p => p.Name).ToArray();
        var namesSet = new HashSet<string>(names, StringComparer.Ordinal);
        var poses =
            names.Length == 5 ? LegacyPoses
            : names.Length == 9 ? (namesSet.SetEquals(GuidedSamples) ? GuidedSamples : NinePoses)
            : [];
        if (
            poses.Length == 0
            || !new HashSet<string>(names, StringComparer.Ordinal).SetEquals(poses)
        )
            throw new InvalidDataException("기존 5·9방향 또는 안내형 대표 샘플 전체가 필요합니다.");
        foreach (string pose in poses)
        {
            var values = features.GetProperty(pose);
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 512)
                throw new InvalidDataException("방향마다 512차원 특징이 필요합니다.");
            double norm = 0;
            foreach (var v in values.EnumerateArray())
            {
                if (
                    v.ValueKind != JsonValueKind.Number
                    || !v.TryGetDouble(out double d)
                    || !double.IsFinite(d)
                )
                    throw new InvalidDataException("유효하지 않은 얼굴 특징입니다.");
                norm += d * d;
            }

            if (!double.IsFinite(norm) || norm <= 1e-12)
                throw new InvalidDataException("유효하지 않은 특징 크기입니다.");
        }

        return poses.ToArray();
    }
}
