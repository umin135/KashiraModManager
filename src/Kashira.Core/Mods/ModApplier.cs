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
        List<string> Incompatible);

    public static GatherResult Gather(GameWorkspace ws, GameInstall game)
    {
        var reps = new List<PatchEngine.Replacement>();

        foreach (var e in DebugMods.List(ws.DebugModsDir))
            reps.Add(new PatchEngine.Replacement(e.FileKtid, File.ReadAllBytes(e.FullPath), e.Ext));
        int debugCount = reps.Count;

        int ktmodCount = 0;
        var incompatible = new List<string>();
        if (Directory.Exists(ws.ModsDir))
        {
            foreach (var f in Directory.EnumerateFiles(ws.ModsDir, "*.ktmod").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var pkg = KtmodPackage.Load(f);
                if (pkg is null) continue;
                if (!pkg.MatchesGame(game)) { incompatible.Add(pkg.Name); continue; }
                reps.AddRange(pkg.BuildReplacements());
                ktmodCount++;
            }
        }

        return new GatherResult(reps, debugCount, ktmodCount, incompatible);
    }
}
