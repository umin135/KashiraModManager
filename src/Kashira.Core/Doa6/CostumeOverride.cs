using Kashira.Core.Formats;

namespace Kashira.Core.Doa6;

/// <summary>
/// 코스튬 → 코스튬 전체 오버라이드(소스의 메시/재질 체인을 타겟 코스튬에 덮어씀).
/// 검증된 엔드투엔드 레시피(메모리 ktmod-content-symbolic-design, KAS_COS_002→004):
///   - 소스 g1m/mtl/grp 를 새 FileKtid 로 신규등록 후 타겟 DM repoint (기존 FK 재사용 금지)
///   - 타겟 DM 의 TBC(base ktid) → 소스 base ktid FK repoint (기존참조 OK)
///   - 타겟 CE1MotorChar 의 name 배열(0xCEE5DDAE)·MRNH(0x34CF9E5C) → 소스값(소스 MBE 참조)
///   - 타겟 CharacterSetting MI 배열 → 소스 MBE(변형 클램프 매핑), oidex/rigbin 은 타겟 유지
/// 소스 MBE 는 기존 ME DOK 에 있으므로 insert_record 불필요(resize_record 만).
/// </summary>
public static class CostumeOverride
{
    /// <summary>신규 등록할 에셋 한 건(파일 확장자로 TypeKtid 결정).</summary>
    public sealed record NewAsset(uint FileKtid, byte[] Bytes, string Ext);

    /// <summary>
    /// 타겟 코스튬을 소스 코스튬으로 오버라이드하도록 공유 싱글톤 세트를 편집하고, 신규 에셋 목록을 반환.
    /// 여러 코스튬 모드를 같은 set 에 누적 적용할 수 있다(같은 DOK 편집이 병합됨).
    /// 직렬화된 dirty DOK 는 모든 적용 후 set.DirtyBytes() 로 한 번에 얻는다.
    /// </summary>
    public static IReadOnlyList<NewAsset> Apply(Doa6SingletonSet set, string targetCostume, string sourceCostume)
    {
        uint tgt = set.CostumeOid(targetCostume);
        uint src = set.CostumeOid(sourceCostume);
        var tgtAssets = set.ResolveAssets(tgt);
        var srcAssets = set.ResolveAssets(src);
        var srcMat = set.ResolveMaterial(src);
        var tgtMat = set.ResolveMaterial(tgt);

        // 1) 소스 g1m/mtl/grp 를 새 FK 로 신규등록(설치 전역 공유 할당기)
        uint fkG1m = set.AllocFk(), fkMtl = set.AllocFk(), fkGrp = set.AllocFk();
        var newAssets = new List<NewAsset>
        {
            new(fkG1m, Require(set, srcAssets.G1m, "g1m"), "g1m"),
            new(fkMtl, Require(set, srcAssets.Mtl, "mtl"), "mtl"),
            new(fkGrp, Require(set, srcAssets.Grp, "grp"), "grp"),
        };

        // 2) 타겟 DM repoint (g1m/mtl/grp → 신규, base ktid → 소스)
        var dm = set.DisplaysetModel(tgt);
        dm.SetU32(Doa6SingletonSet.P_Dm_G1m, fkG1m);
        dm.SetU32(Doa6SingletonSet.P_Dm_Mtl, fkMtl);
        dm.SetU32(Doa6SingletonSet.P_Dm_Grp, fkGrp);
        var tbc = set.Ce.Find(tgtAssets.TbcObj)
                  ?? throw new InvalidDataException($"타겟 TBC 0x{tgtAssets.TbcObj:X8} 없음");
        tbc.SetU32(Doa6SingletonSet.P_Tbc_Ktid, srcAssets.BaseKtid);
        set.MarkDirty(Doa6SingletonSet.CeFk);

        // 3) 타겟 CE1MotorChar name/MRNH → 소스값
        var motor = set.MotorChar(tgt);
        motor.SetU32Array(Doa6SingletonSet.P_Mc_NameArr, srcMat.NameArr);
        motor.SetU32Array(Doa6SingletonSet.P_Mc_Mrnh, srcMat.Mrnh);

        // 4) 타겟 CharacterSetting MI → 소스(변형 클램프 매핑, 타겟 변형수 유지)
        var cs = set.CharSetting(tgt);
        cs.SetU32Array(Doa6SingletonSet.P_Cs_Mi, MapMi(srcMat, tgtMat.Cvn));
        set.MarkDirty(Doa6SingletonSet.Ce1CommonFk);

        return newAssets;
    }

    /// <summary>MI 하이브리드 클램프 매핑: 타겟 변형 v(0..tgtCvn-1) → 소스 변형 min(v, srcCvn-1).</summary>
    private static uint[] MapMi(Doa6SingletonSet.MaterialInfo srcMat, int tgtCvn)
    {
        int slot = srcMat.SlotCount, srcCvn = srcMat.Cvn;
        var mi = new uint[slot * tgtCvn];
        for (int v = 0; v < tgtCvn; v++)
        {
            int sv = Math.Min(v, srcCvn - 1);
            for (int s = 0; s < slot; s++)
                mi[v * slot + s] = srcMat.Mi[sv * slot + s];
        }
        return mi;
    }

    private static byte[] Require(Doa6SingletonSet set, uint fk, string what) =>
        set.Extractor.Extract(fk) ?? throw new InvalidDataException($"소스 {what} 0x{fk:X8} 추출 실패");
}
