using Kashira.Core.Formats;

namespace Kashira.Core.Doa6;

/// <summary>
/// 텍스처 신규 저작 코스튬 설치(SourceCostume 없이 Content/ 저작 에셋 사용).
/// mod 의 g1m/grp/mtl 을 신규등록해 타겟 DM 에 배선하고, material.json 의 슬롯→g1t 지정으로
/// 재질별 새 MBE 체인(MaterialChainFactory)을 만들어 CE1MotorChar/CharacterSetting 에 배선한다.
/// 하이브리드 변형: 타겟 변형수 유지, 타겟 변형 v → mod 변형 min(v, M-1) 클램프.
/// 검증된 메커니즘 조합(메모리 ktmod-content-symbolic-design): resize_record + MaterialChainFactory + DM repoint.
/// </summary>
public static class CostumeAuthorInstaller
{
    /// <summary>한 재질의 저작 정의. 변형별 (텍스처 슬롯 인덱스 → @텍스처 이름).</summary>
    public sealed record AuthoredMaterial(bool VariationAffecting, IReadOnlyList<IReadOnlyDictionary<int, string>> Slots);

    /// <summary>
    /// 저작 코스튬 입력. 텍스처는 @이름 → g1t 바이트로 전달(모드 파일에서 로드).
    /// RequireAllSlots=true 면 재질마다 템플릿 MBE 의 전 텍스처 슬롯을 지정해야 함(누락 시 예외).
    /// false(기본)면 미지정 슬롯은 타겟 원본 텍스처를 상속(g1m 임포트 베이스라인 등).
    /// </summary>
    public sealed record AuthoredCostume(
        string TargetCostume,
        byte[] G1m, byte[] Grp, byte[] Mtl,
        int VariationCount,
        IReadOnlyList<AuthoredMaterial> Materials,
        IReadOnlyDictionary<string, byte[]> TextureFiles,
        bool RequireAllSlots = false);

    /// <summary>공유 세트에 저작 코스튬을 적용하고 신규 에셋 목록을 반환(누적 가능).</summary>
    public static IReadOnlyList<CostumeOverride.NewAsset> Apply(Doa6SingletonSet set, AuthoredCostume mod)
    {
        uint tgt = set.CostumeOid(mod.TargetCostume);
        var tgtMat = set.ResolveMaterial(tgt);

        var mtl = MtlFile.Parse(mod.Mtl);
        if (mod.Materials.Count != mtl.NumNames)
            throw new InvalidOperationException($"material 개수({mod.Materials.Count}) ≠ mtl num_names({mtl.NumNames})");
        var nameHashes = mtl.NameHashes();
        int numMat = mod.Materials.Count;
        int M = Math.Max(1, mod.VariationCount);

        var newAssets = new List<CostumeOverride.NewAsset>();

        // 1) mod g1m/grp/mtl 신규등록 + 타겟 DM repoint
        uint fkG1m = set.AllocFk(), fkGrp = set.AllocFk(), fkMtl = set.AllocFk();
        newAssets.Add(new(fkG1m, mod.G1m, "g1m"));
        newAssets.Add(new(fkGrp, mod.Grp, "grp"));
        newAssets.Add(new(fkMtl, mod.Mtl, "mtl"));
        var dm = set.DisplaysetModel(tgt);
        dm.SetU32(Doa6SingletonSet.P_Dm_G1m, fkG1m);
        dm.SetU32(Doa6SingletonSet.P_Dm_Grp, fkGrp);
        dm.SetU32(Doa6SingletonSet.P_Dm_Mtl, fkMtl);
        set.MarkDirty(Doa6SingletonSet.CeFk);

        // 2) 텍스처 파일 신규등록(@이름 → g1t FK, 한 번씩만)
        var texFk = new Dictionary<string, uint>();
        foreach (var (name, bytes) in mod.TextureFiles)
        {
            uint fk = set.AllocFk();
            texFk[name] = fk;
            newAssets.Add(new(fk, bytes, "g1t"));
        }

        // 3) 재질별 MBE 체인 생성(변형별). 템플릿 = 타겟의 var0 MBE(구조 상속).
        var tgtVar0 = TargetVar0Mbes(tgtMat);
        var matMbe = new uint[numMat][]; // matMbe[m][variation] = MBE oid
        for (int m = 0; m < numMat; m++)
        {
            uint template = tgtVar0[Math.Min(m, tgtVar0.Length - 1)];
            var mat = mod.Materials[m];
            int variations = mat.VariationAffecting ? M : 1;
            matMbe[m] = new uint[variations];
            for (int v = 0; v < variations; v++)
            {
                var slotMap = ResolveSlots(mat.Slots[v], texFk);
                var chain = MaterialChainFactory.Create(set, template, slotMap, requireAllSlots: mod.RequireAllSlots);
                matMbe[m][v] = chain.MbeOid;
                newAssets.AddRange(chain.NewAssets);
            }
        }

        // 4) MI 행렬(numMat × 타겟 변형수, 클램프) + MRNH + nameArr 배선
        int tgtCvn = Math.Max(1, tgtMat.Cvn);
        var mi = new uint[numMat * tgtCvn];
        for (int v = 0; v < tgtCvn; v++)
            for (int m = 0; m < numMat; m++)
            {
                var perVar = matMbe[m];
                mi[v * numMat + m] = perVar[Math.Min(v, perVar.Length - 1)];
            }
        var mrnh = new uint[numMat * 2];
        for (int m = 0; m < numMat; m++) { mrnh[m * 2] = nameHashes[m]; mrnh[m * 2 + 1] = matMbe[m][0]; }

        var motor = set.MotorChar(tgt);
        motor.SetU32Array(Doa6SingletonSet.P_Mc_NameArr, nameHashes);
        motor.SetU32Array(Doa6SingletonSet.P_Mc_Mrnh, mrnh);
        set.CharSetting(tgt).SetU32Array(Doa6SingletonSet.P_Cs_Mi, mi);
        set.MarkDirty(Doa6SingletonSet.Ce1CommonFk);

        return newAssets;
    }

    /// <summary>타겟의 var0(베이스 변형) MBE 열 = MI 앞 slotCount 개.</summary>
    private static uint[] TargetVar0Mbes(Doa6SingletonSet.MaterialInfo m)
    {
        int sc = Math.Max(1, m.SlotCount);
        var arr = new uint[sc];
        for (int s = 0; s < sc && s < m.Mi.Length; s++) arr[s] = m.Mi[s];
        return arr;
    }

    private static Dictionary<int, uint> ResolveSlots(IReadOnlyDictionary<int, string> slots, Dictionary<string, uint> texFk)
    {
        var map = new Dictionary<int, uint>();
        foreach (var (slot, name) in slots)
        {
            if (!texFk.TryGetValue(name, out uint fk))
                throw new KeyNotFoundException($"텍스처 '{name}' 파일 없음");
            map[slot] = fk;
        }
        return map;
    }
}
