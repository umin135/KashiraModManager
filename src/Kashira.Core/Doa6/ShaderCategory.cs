namespace Kashira.Core.Doa6;

/// <summary>
/// DOA6 셰이더 텍스처 카테고리(KTS primary). **현재는 인덱스(카테고리 ID)로만 표시**한다.
/// 이름 매핑(albedo/normal…)은 추정치이므로, 모더 기반으로 제대로 분석·검증된 뒤에 부여한다(아래 KnownNames 는 휴면 참조).
/// 정렬도 이름 우선순위 대신 카테고리 ID 오름차순.
/// </summary>
public static class ShaderCategory
{
    /// <summary>
    /// (휴면) 추정 역할명 — 제대로 분석되기 전까지 표시에 사용하지 않는다. 확정 후 <see cref="Label"/> 에 연결.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> KnownNames = new Dictionary<int, string>
    {
        [1] = "albedo", [2] = "roughness", [3] = "normal", [5] = "occlusion",
        [37] = "shell", [41] = "air", [47] = "wetmask", [55] = "occ2", [62] = "s4m",
    };

    /// <summary>표시 라벨 = 카테고리 인덱스(예: "cat 1"). 이름 확정 전까지 인덱스만.</summary>
    public static string Label(int category) => $"cat {category}";

    /// <summary>(휴면) 확정 전 참조용 추정 이름. 표시엔 사용하지 않음 — 분석 후 <see cref="Label"/> 에 연결 예정.</summary>
    public static string? KnownName(int category) => KnownNames.TryGetValue(category, out var n) ? n : null;

    /// <summary>구버전 호환 별칭. 현재는 인덱스 라벨을 반환.</summary>
    public static string RoleName(int category) => Label(category);

    /// <summary>카테고리 집합을 ID 오름차순으로 정렬(이름 우선순위 미사용).</summary>
    public static IEnumerable<int> Sort(IEnumerable<int> categories)
        => new SortedSet<int>(categories);
}
