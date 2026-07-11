namespace Kashira.Core.Doa6;

/// <summary>
/// DOA6 셰이더 텍스처 카테고리(KTS primary) → 역할명. 게임 전역 레지스트리(재질간 일관, 실측).
/// 에디터 재질 그리드 행 라벨 등에 사용. 미확인 카테고리는 "cat{n}".
/// </summary>
public static class ShaderCategory
{
    private static readonly IReadOnlyDictionary<int, string> Roles = new Dictionary<int, string>
    {
        [1] = "albedo", [2] = "roughness", [3] = "normal", [5] = "occlusion",
        [37] = "shell", [41] = "air", [47] = "wetmask", [55] = "occ2", [62] = "s4m",
    };

    /// <summary>행 정렬용 표준 순서(중요도 순). 목록 밖 카테고리는 뒤에 오름차순.</summary>
    private static readonly int[] Order = { 1, 3, 2, 5, 55, 37, 41, 47, 62 };

    public static string RoleName(int category) =>
        Roles.TryGetValue(category, out var r) ? r : $"cat{category}";

    /// <summary>카테고리 집합을 표준 역할 순서로 정렬(미등록은 뒤, 오름차순).</summary>
    public static IEnumerable<int> Sort(IEnumerable<int> categories)
    {
        var set = new HashSet<int>(categories);
        foreach (var c in Order) if (set.Remove(c)) yield return c;
        foreach (var c in set.OrderBy(x => x)) yield return c;
    }
}
