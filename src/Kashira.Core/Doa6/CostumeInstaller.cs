using Kashira.Core.Formats;
using Kashira.Core.Games;
using Kashira.Core.Patching;

namespace Kashira.Core.Doa6;

/// <summary>
/// 코스튬 오버라이드 최상위 오케스트레이터: 싱글톤 세트를 한 번 로드해 모든 코스튬 교체를
/// 누적 적용(같은 DOK 편집 병합) → 편집된 DOK + 신규 에셋을 PatchEngine.Replacement 로 변환.
/// PatchEngine 은 pristine 재계산이므로 활성 코스튬 모드 전체를 한 목록으로 넘겨야 한다.
/// </summary>
public static class CostumeInstaller
{
    /// <summary>한 코스튬 교체 = 타겟을 소스로 오버라이드. (소스 = 같은 게임의 기존 코스튬)</summary>
    public sealed record Swap(string TargetCostume, string SourceCostume);

    /// <summary>스왑 + 저작 코스튬을 하나의 싱글톤 세트에 누적 적용해 Replacement 목록을 만든다(DOK 병합).</summary>
    public static List<PatchEngine.Replacement> BuildReplacements(
        GameWorkspace ws,
        IEnumerable<Swap> swaps,
        IEnumerable<CostumeAuthorInstaller.AuthoredCostume>? authored = null)
    {
        using var ex = AssetExtractor.Open(ws);
        var set = Doa6SingletonSet.Load(ex);

        var reps = new List<PatchEngine.Replacement>();
        void AddNew(IEnumerable<CostumeOverride.NewAsset> assets)
        {
            foreach (var a in assets)
                reps.Add(new PatchEngine.Replacement(a.FileKtid, a.Bytes, a.Ext)); // 신규등록
        }

        foreach (var s in swaps)
            AddNew(CostumeOverride.Apply(set, s.TargetCostume, s.SourceCostume));
        foreach (var a in authored ?? Enumerable.Empty<CostumeAuthorInstaller.AuthoredCostume>())
            AddNew(CostumeAuthorInstaller.Apply(set, a));

        // 누적 편집된 싱글톤 DB(CE/CE1Common/ME) = 리다이렉트 (한 번씩만, 병합된 최종 바이트)
        foreach (var (fk, bytes) in set.DirtyBytes())
            reps.Add(new PatchEngine.Replacement(fk, bytes, Ext: "kidssingletondb"));

        return reps;
    }

    /// <summary>단일 스왑 편의 오버로드.</summary>
    public static List<PatchEngine.Replacement> BuildReplacements(GameWorkspace ws, string targetCostume, string sourceCostume)
        => BuildReplacements(ws, new[] { new Swap(targetCostume, sourceCostume) });
}
