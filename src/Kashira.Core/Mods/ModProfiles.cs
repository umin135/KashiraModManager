using System.Text.Json;
using Kashira.Core.Games;

namespace Kashira.Core.Mods;

/// <summary>프로필 내 모드 한 개의 슬롯. 리스트에서의 순서(위=인덱스 0)가 우선순위 — 상위가 파일 충돌에서 승리.</summary>
public sealed class ModSlot
{
    /// <summary>.ktmod 파일명(확장자 제외) = KtmodPackage.Name.</summary>
    public string Mod { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>사용자 정의 프로필: 순서 있는 모드 슬롯 목록(상단=최우선).</summary>
public sealed class ModProfile
{
    public string Name { get; set; } = "";
    public List<ModSlot> Mods { get; set; } = new();
}

/// <summary>활성 프로필 이름 + 해석된 활성 여부(우선순위는 목록 순서로 표현).</summary>
public sealed record ResolvedMod(string Mod, bool Enabled);

/// <summary>
/// 게임별 모드 프로필 저장소(_Kashira/profiles.json).
/// Default 프로필은 가상·불변 — 파일에 저장하지 않으며 "모든 모드 Enable · ABC 정렬"을 의미한다.
/// 사용자 프로필만 저장되고, Active 가 Default(또는 미존재)면 기본 동작.
/// </summary>
public sealed class ModProfiles
{
    public const string DefaultName = "Default";

    public string Active { get; set; } = DefaultName;
    public List<ModProfile> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static ModProfiles Load(GameWorkspace ws)
    {
        try
        {
            if (File.Exists(ws.ProfilesPath))
                return JsonSerializer.Deserialize<ModProfiles>(File.ReadAllText(ws.ProfilesPath)) ?? new ModProfiles();
        }
        catch { /* 손상 → 기본 */ }
        return new ModProfiles();
    }

    public void Save(GameWorkspace ws)
    {
        Directory.CreateDirectory(ws.KashiraDir);
        File.WriteAllText(ws.ProfilesPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public ModProfile? Find(string name) =>
        Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool IsDefault(string name) =>
        string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase);

    /// <summary>설치된 모드 이름을 ABC 정렬한 뒤 새 프로필로 만든다(전부 Enable). 이름 중복은 호출측 검사.</summary>
    public ModProfile CreateProfile(string name, IEnumerable<string> installed)
    {
        var p = new ModProfile
        {
            Name = name,
            Mods = Abc(installed).Select(n => new ModSlot { Mod = n, Enabled = true }).ToList(),
        };
        Profiles.Add(p);
        return p;
    }

    public void Remove(string name) =>
        Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>프로필의 Mods 를 현재 설치 목록에 맞춰 정규화: 삭제된 모드 제거 + 새 모드는 하단 Enable 로 추가.</summary>
    public void Normalize(string name, IEnumerable<string> installed)
    {
        var p = Find(name);
        if (p is null) return;
        p.Mods = ResolveProfile(p, Abc(installed))
            .Select(r => new ModSlot { Mod = r.Mod, Enabled = r.Enabled }).ToList();
    }

    /// <summary>활성 프로필 기준으로 설치 모드를 우선순위·활성 여부로 해석(상단=최우선).</summary>
    public List<ResolvedMod> Resolve(IEnumerable<string> installed) => Resolve(Active, installed);

    /// <summary>지정 프로필 기준 해석. Default(또는 미존재)면 모든 모드 ABC · Enable.</summary>
    public List<ResolvedMod> Resolve(string profileName, IEnumerable<string> installed)
    {
        var abc = Abc(installed);
        var p = Find(profileName);
        if (p is null) // Default 또는 알 수 없는 프로필 → 전부 ABC · Enable
            return abc.Select(n => new ResolvedMod(n, true)).ToList();
        return ResolveProfile(p, abc);
    }

    private static List<ResolvedMod> ResolveProfile(ModProfile p, List<string> installedAbc)
    {
        var set = new HashSet<string>(installedAbc, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ResolvedMod>();
        foreach (var slot in p.Mods)
        {
            if (!set.Contains(slot.Mod) || !seen.Add(slot.Mod)) continue; // 삭제된 모드/중복 슬롯 무시
            result.Add(new ResolvedMod(slot.Mod, slot.Enabled));
        }
        foreach (var n in installedAbc) // 프로필에 없던 새 모드 → 하단에 Enable 로
            if (seen.Add(n)) result.Add(new ResolvedMod(n, true));
        return result;
    }

    private static List<string> Abc(IEnumerable<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
}
