using Kashira.Core.Doa6;
using Kashira.Core.Games;
using Kashira.Core.Patching;

namespace Kashira.Core.Mods;

/// <summary>
/// 한 게임에 적용할 전체 교체 목록을 모은다:
///   DebugMods/*        — 날것 파일. 신규 등록 허용(전략 D).
///   Mods/*.ktmod       — 패키지. Target 이 이 게임과 일치하는 것만, Content_Legacy 를 리다이렉트 전용으로.
/// PatchEngine.Apply 는 항상 pristine 에서 재계산(멱등)하므로, 활성 모드 전체를 한 번에 넘긴다.
/// </summary>
public static class ModApplier
{
    public sealed record GatherResult(
        List<PatchEngine.Replacement> Replacements,
        int DebugCount,
        int KtmodCount,
        List<string> Incompatible,
        int Conflicts);

    public static GatherResult Gather(GameWorkspace ws, GameInstall game)
    {
        var reps = new List<PatchEngine.Replacement>();

        // DebugMods 는 최상위 우선순위(수동 디버그 오버라이드) — 항상 먼저.
        foreach (var e in DebugMods.List(ws.DebugModsDir))
            reps.Add(new PatchEngine.Replacement(e.FileKtid, File.ReadAllBytes(e.FullPath), e.Ext));
        int debugCount = reps.Count;

        // DebugMods/sid.json — Character.sid 셰이더 등록 드라이버(최소 검증). pristine sid + 도너 복사 → Replacement.
        var sidRegs = SidInstaller.ReadJson(Path.Combine(ws.DebugModsDir, "sid.json"));
        if (sidRegs.Count > 0)
        {
            using var ex = Formats.AssetExtractor.Open(ws);
            if (SidInstaller.BuildReplacement(ex, sidRegs) is { } sidRep) reps.Add(sidRep);
        }

        int ktmodCount = 0;
        var incompatible = new List<string>();
        var swaps = new List<CostumeInstaller.Swap>();
        var authored = new List<CostumeAuthorInstaller.AuthoredCostume>();
        if (Directory.Exists(ws.ModsDir))
        {
            // 설치된 .ktmod 를 Name(확장자 제외) → 경로로 매핑.
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(ws.ModsDir, "*.ktmod"))
                files[Path.GetFileNameWithoutExtension(f)] = f;

            // 활성 프로필로 우선순위(상단=최우선)·활성 여부를 해석해 그 순서대로 처리.
            var profiles = ModProfiles.Load(ws);
            foreach (var rm in profiles.Resolve(files.Keys))
            {
                if (!rm.Enabled) continue;
                if (!files.TryGetValue(rm.Mod, out var f)) continue;
                var pkg = KtmodPackage.Load(f);
                if (pkg is null) continue;
                if (!pkg.MatchesGame(game)) { incompatible.Add(pkg.Name); continue; }
                reps.AddRange(pkg.BuildReplacements());
                // Content 코스튬 매니페스트: 스왑(SourceCostume) / 저작(Mesh+Materials)
                foreach (var cm in pkg.CostumeManifests)
                {
                    if (cm.IsSwap)
                        swaps.Add(new CostumeInstaller.Swap(cm.TargetCostume, cm.SourceCostume!));
                    else if (cm.IsAuthored && pkg.BuildAuthored(cm) is { } ac)
                        authored.Add(ac);
                }
                ktmodCount++;
            }
        }

        // 모든 코스튬 스왑/저작을 하나의 싱글톤 세트에 누적 적용(같은 DOK 편집 병합) 후 Replacement 로.
        if (swaps.Count > 0 || authored.Count > 0)
            reps.AddRange(CostumeInstaller.BuildReplacements(ws, swaps, authored));

        // 파일 충돌 해소: 같은 FileKtid 는 우선순위(먼저 등장=상위)를 유지하고 이후 중복은 제거.
        var seen = new HashSet<uint>();
        var deduped = new List<PatchEngine.Replacement>(reps.Count);
        int conflicts = 0;
        foreach (var r in reps)
        {
            if (seen.Add(r.FileKtid)) deduped.Add(r);
            else conflicts++;
        }

        return new GatherResult(deduped, debugCount, ktmodCount, incompatible, conflicts);
    }
}
