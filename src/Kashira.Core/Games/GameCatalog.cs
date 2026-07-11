namespace Kashira.Core.Games;

/// <summary>New Project / 툴바 게임 드롭다운의 한 항목. Key 가 project.TargetGame 에 저장된다.</summary>
public sealed record GameOption(string Key, string DisplayName, bool Installed, bool IsAuthoring);

/// <summary>
/// 에디터↔매니저 연동의 게임 선택/게이팅 헬퍼.
/// - 드롭다운 옵션은 매니저가 저장한 <see cref="GameLibrary"/>(설치 게임) 우선, 그 뒤 미설치 프로파일.
/// - 저장 Key 는 안정 매칭을 위해 ProfileId 우선(없으면 exe명, 그다음 Id).
///   ProjectWorkspace.ResolveWorkspace 가 이 Key 를 exe명/ProfileId 로 매칭한다.
/// - 저작(재질/변형 편집)은 DOA6LR 만. 그 외 타겟은 Content_Legacy raw redirect 전용.
/// </summary>
public static class GameCatalog
{
    public const string Doa6lrId = "doa6lr";

    /// <summary>project.TargetGame 에 저장할 안정 키.</summary>
    public static string KeyFor(GameInstall g) =>
        !string.IsNullOrEmpty(g.ProfileId) ? g.ProfileId!
        : g.ExePath is not null ? Path.GetFileNameWithoutExtension(g.ExePath)
        : g.Id;

    /// <summary>드롭다운 옵션: 설치 게임(매니저 저장) 먼저, 그 뒤 미설치 알려진 프로파일.</summary>
    public static List<GameOption> Options()
    {
        var installed = GameLibrary.Load();
        var opts = new List<GameOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in installed)
        {
            var key = KeyFor(g);
            if (!seen.Add(key)) continue;
            opts.Add(new GameOption(key, g.DisplayName, Installed: true, IsAuthoring: g.ProfileId == Doa6lrId));
        }
        foreach (var p in GameProfile.All)
        {
            if (seen.Contains(p.Id)) continue;
            if (installed.Any(g => g.ProfileId == p.Id)) continue;
            opts.Add(new GameOption(p.Id, $"{p.DisplayName} (미설치)", Installed: false, IsAuthoring: p.Id == Doa6lrId));
        }
        return opts;
    }

    /// <summary>이 타겟이 DOA6LR 저작 게임인가(재질/변형 편집 활성 여부).</summary>
    public static bool IsAuthoringTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (string.Equals(target, Doa6lrId, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(target, "DOA6LR", StringComparison.OrdinalIgnoreCase)) return true;

        var g = GameLibrary.Load().FirstOrDefault(x => Matches(x, target));
        return g?.ProfileId == Doa6lrId;
    }

    /// <summary>ResolveWorkspace 와 동일한 매칭 규칙(Key/exe명/ProfileId).</summary>
    public static bool Matches(GameInstall g, string target) =>
        string.Equals(KeyFor(g), target, StringComparison.OrdinalIgnoreCase)
        || (g.ExePath is not null && string.Equals(Path.GetFileNameWithoutExtension(g.ExePath), target, StringComparison.OrdinalIgnoreCase))
        || string.Equals(g.ProfileId, target, StringComparison.OrdinalIgnoreCase);
}
