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
    /// <summary>한 재질의 저작 정의. 변형별 (텍스처 슬롯 인덱스 → @텍스처 이름). 변형 항목이 null 이면 그 변형은 base 폴백(MI=0).</summary>
    public sealed record AuthoredMaterial(bool VariationAffecting, IReadOnlyList<IReadOnlyDictionary<int, string>?> Slots);

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
        bool RequireAllSlots = false,
        string? MaterialTemplateCostume = null);

    /// <summary>공유 세트에 저작 코스튬을 적용하고 신규 에셋 목록을 반환(누적 가능).</summary>
    public static IReadOnlyList<CostumeOverride.NewAsset> Apply(Doa6SingletonSet set, AuthoredCostume mod)
    {
        uint tgt = set.CostumeOid(mod.TargetCostume);
        var tgtMat = set.ResolveMaterial(tgt);
        var tgtAssets = set.ResolveAssets(tgt);

        var mtl = MtlFile.Parse(mod.Mtl);
        if (mod.Materials.Count != mtl.NumNames)
            throw new InvalidOperationException($"material 개수({mod.Materials.Count}) ≠ mtl num_names({mtl.NumNames})");
        var nameHashes = mtl.NameHashes();
        int numMat = mod.Materials.Count;
        int M = Math.Max(1, mod.VariationCount);

        var newAssets = new List<CostumeOverride.NewAsset>();

        // 1) 에셋 배선:
        //    - g1m: 새 FK 금지(온라인 검증) → 타겟 canonical g1m FK 에 mod 메시 in-place 교체(raw redirect). DM.g1m 유지.
        //    - grp/mtl: 새 FK 신규등록 + DM repoint (온라인 미검증 → 안전).
        uint fkGrp = set.AllocFk(), fkMtl = set.AllocFk();
        newAssets.Add(new(tgtAssets.G1m, mod.G1m, "g1m")); // 기존 FK → redirect
        newAssets.Add(new(fkGrp, mod.Grp, "grp"));
        newAssets.Add(new(fkMtl, mod.Mtl, "mtl"));
        var dm = set.DisplaysetModel(tgt);
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

        // 3) 재질별 MBE 체인 생성(변형별). 템플릿 = MaterialTemplateCostume(있으면, 예: 번들 소스 코스튬)
        //    또는 타겟의 var0 MBE. 소스 템플릿을 쓰면 소스의 KTS/슬롯 레이아웃이 보존돼 슬롯 밀림이 없다.
        var templateMat = mod.MaterialTemplateCostume is { } tc
            ? set.ResolveMaterial(set.CostumeOid(tc)) : tgtMat;
        var tgtVar0 = TargetVar0Mbes(templateMat);
        int tsc = Math.Max(1, templateMat.SlotCount);
        int tcvn = Math.Max(1, templateMat.Cvn);
        var matMbe = new uint[numMat][]; // matMbe[m][variation] = MBE oid
        for (int m = 0; m < numMat; m++)
        {
            var mat = mod.Materials[m];
            int variations = Math.Max(1, mat.Slots.Count);
            matMbe[m] = new uint[variations];
            uint baseMbe = 0; // 첫 비-null(기본) 변형의 MBE — null 변형은 이걸 재사용(=base)
            for (int v = 0; v < variations; v++)
            {
                if (mat.Slots[v] is not { } slots) { matMbe[m][v] = 0; continue; } // null → 아래서 base 채움
                // 변형별 네이티브 MBE 를 템플릿으로 클론 → 올바른 KTS(슬롯→타입 스키마)/MatIx/슬롯구조 확보.
                //   (변형마다 KTS 가 다를 수 있어 var0 고정 클론은 var1~ 을 깨뜨림.) 없으면 var0 로 폴백.
                int srcV = Math.Min(v, tcvn - 1);
                uint template = (srcV * tsc + m < templateMat.Mi.Length && templateMat.Mi[srcV * tsc + m] != 0)
                    ? templateMat.Mi[srcV * tsc + m]
                    : tgtVar0[Math.Min(m, tgtVar0.Length - 1)];
                var chain = MaterialChainFactory.Create(set, template, ResolveSlots(slots, texFk),
                                                        requireAllSlots: mod.RequireAllSlots);
                matMbe[m][v] = chain.MbeOid;
                newAssets.AddRange(chain.NewAssets);
                if (baseMbe == 0) baseMbe = chain.MbeOid;
            }
            // null(base 폴백) 변형을 기본 변형 MBE 로 채운다 → MI 를 꽉 채워 base ktid 폴백 경로 의존 제거.
            for (int v = 0; v < variations; v++)
                if (matMbe[m][v] == 0) matMbe[m][v] = baseMbe;
        }

        // 4) MI 행렬(numMat × 타겟 변형수, 클램프; 0=base 폴백) + MRNH + nameArr 배선
        int tgtCvn = Math.Max(1, tgtMat.Cvn);
        var mi = new uint[numMat * tgtCvn];
        for (int v = 0; v < tgtCvn; v++)
            for (int m = 0; m < numMat; m++)
            {
                var perVar = matMbe[m];
                mi[v * numMat + m] = perVar[Math.Min(v, perVar.Length - 1)];
            }
        // MRNH = (name_hash, 베이스 MBE) 쌍. 베이스 = var0(없으면 첫 비-0 변형).
        var mrnh = new uint[numMat * 2];
        for (int m = 0; m < numMat; m++) { mrnh[m * 2] = nameHashes[m]; mrnh[m * 2 + 1] = FirstNonZero(matMbe[m]); }

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

    /// <summary>배열의 첫 비-0 값(=베이스 변형 MBE). 전부 0이면 0.</summary>
    private static uint FirstNonZero(uint[] arr)
    {
        foreach (var v in arr) if (v != 0) return v;
        return 0;
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
