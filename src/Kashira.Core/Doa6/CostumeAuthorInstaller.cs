using System.Buffers.Binary;
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
    /// <summary>한 재질의 저작 정의. 변형별 (카테고리 → @텍스처) + KTS(슬롯 스키마, 임포트 시 g1m 에서 생성해 프로젝트 저장). null 변형=base 폴백.</summary>
    public sealed record AuthoredMaterial(bool VariationAffecting, IReadOnlyList<IReadOnlyDictionary<int, string>?> Slots,
        IReadOnlyList<Formats.KtsFile.Slot>? Kts = null);

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
        string? MaterialTemplateCostume = null,
        IReadOnlyDictionary<int, string>? BaseKtid = null,
        IReadOnlyList<Formats.KtsFile.Slot>? BaseKts = null,
        IReadOnlyDictionary<uint, uint>? ShaderOverrides = null,     // 메시해시 → matB (레거시 사이드카)
        IReadOnlyDictionary<int, uint>? MaterialShaders = null);     // 재질 인덱스 → matB (매니페스트, 메시그룹으로 팬아웃)

    /// <summary>Apply 결과: 신규 에셋 + Character.sid 등록(셰이더 오버라이드).</summary>
    public sealed record ApplyResult(
        IReadOnlyList<CostumeOverride.NewAsset> Assets,
        IReadOnlyList<SidInstaller.Registration> SidRegs);

    /// <summary>공유 세트에 저작 코스튬을 적용하고 신규 에셋 + sid 등록을 반환(누적 가능).</summary>
    /// <param name="shaderAllocBase">셰이더 오버라이드 fresh 해시 할당 시작값 — 다중 코스튬 충돌 방지 위해 코스튬별로 구분된 범위를 넘긴다.</param>
    public static ApplyResult Apply(Doa6SingletonSet set, AuthoredCostume mod, uint shaderAllocBase = 0x0FA10000)
    {
        uint tgt = set.CostumeOid(mod.TargetCostume);
        var tgtMat = set.ResolveMaterial(tgt);
        var tgtAssets = set.ResolveAssets(tgt);

        var mtl = MtlFile.Parse(mod.Mtl);
        if (mod.Materials.Count > 0 && mod.Materials.Count != mtl.NumNames)
            throw new InvalidOperationException($"material 개수({mod.Materials.Count}) ≠ mtl num_names({mtl.NumNames})");
        var nameHashes = mtl.NameHashes();
        int numMat = mod.Materials.Count;
        int M = Math.Max(1, mod.VariationCount);

        // g1m 재질 텍스처 구조 = KTS/슬롯 생성의 앵커. 재질 m ↔ g1m 재질 m.
        var g1mMats = G1mFile.Materials(mod.G1m);

        var newAssets = new List<CostumeOverride.NewAsset>();

        // 0) 셰이더 오버라이드: fresh 해시 할당 → g1m 메시그룹 재작성 + Character.sid 등록(도너 셰이더).
        //    텍스처는 유지(메시 g1m 재질 불변), 셰이더만 변경·코스튬별 격리(전역 공유 해시 불변).
        //    입력 = 재질 셰이더(재질→메시그룹 팬아웃, 주 경로) ∪ 레거시 사이드카(메시해시 직접). 재질 우선.
        var sidRegs = new List<SidInstaller.Registration>();
        var g1mBytes = mod.G1m;
        var meshOv = new Dictionary<uint, uint>();
        if (mod.ShaderOverrides is { } legacy) foreach (var kv in legacy) meshOv[kv.Key] = kv.Value;
        if (mod.MaterialShaders is { Count: > 0 } matShaders)
        {
            var meshes = CostumeMeshModel.Build(G1mContainer.Parse(g1mBytes)); // sid 불요(재질/메시타입=g1m)
            foreach (var kv in MaterialShaderFanout.Expand(meshes, matShaders).MeshShaders) meshOv[kv.Key] = kv.Value;
        }
        if (meshOv.Count > 0 && set.Extractor.Extract(CharacterSid.FileKtid) is { } sidBytes)
        {
            var catalog = ShaderCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "res", "doa6lr", "shaders.json"));
            var plan = ShaderOverridePlan.Build(meshOv, catalog, CharacterSid.Parse(sidBytes), shaderAllocBase);
            if (plan.RenameMap.Count > 0)
            {
                var gc = G1mContainer.Parse(g1mBytes);
                G1mGeometry.RenameMeshGroups(gc, plan.RenameMap);
                g1mBytes = gc.Build();
            }
            sidRegs.AddRange(plan.SidRegs);
        }

        // 1) 에셋 배선:
        //    - g1m: 새 FK 금지(온라인 검증) → 타겟 canonical g1m FK 에 mod 메시 in-place 교체(raw redirect). DM.g1m 유지.
        //    - grp/mtl: 새 FK 신규등록 + DM repoint (온라인 미검증 → 안전).
        uint fkGrp = set.AllocFk(), fkMtl = set.AllocFk();
        newAssets.Add(new(tgtAssets.G1m, g1mBytes, "g1m")); // 재작성된 g1m (셰이더 오버라이드 반영)
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

        // 2b) base-only 바디(MPR 재질 없이 base ktid 만): 렌더는 MPR 체인(MBE+KTS+MPR)이 좌우하므로
        //     base ktid 텍스처로 **1재질 MPR 체인을 저작**한다(타겟 body 재질 MBE 를 템플릿으로 클론 → KTS 확보).
        //     base ktid 도 만들어 두되(사용자: 항상 명시 생성) 로딩엔 inert. 기존 MPR 번들 경로(§3~)는 그대로.
        if (mod.BaseKtid is { Count: > 0 } && mod.Materials.Count == 0)
        {
            // 템플릿 = 타겟 var0 재질 MBE(MatIx/blob 제공). KTS 는 base 바디 g1m 에서 생성. 카테고리맵 = BaseKtid.
            uint template = tgtMat.Mi.Length > 0 ? tgtMat.Mi[0] : 0;
            if (template == 0) throw new InvalidOperationException("타겟에 템플릿 재질(MBE)이 없어 base 바디를 저작할 수 없음");
            if (g1mMats.Count == 0) throw new InvalidOperationException("base 바디 g1m 에 재질이 없음");
            var catMap = ResolveSlots(mod.BaseKtid, texFk);
            // KTS = 프로젝트 저장분(임포트 시 생성) 우선, 없으면 g1m 에서 유도.
            var baseKts = mod.BaseKts ?? Formats.KtsFile.SlotsFromG1mMaterial(g1mMats[0]);
            var chain = MaterialChainFactory.CreateFromKts(set, template, baseKts, catMap);
            newAssets.AddRange(chain.NewAssets);

            // 1재질 구조 배선: MI = [재질0] × 타겟CVN(전 변형 동일 MBE), nameArr/MRNH = mtl 재질명 1개.
            int baseCvn = Math.Max(1, tgtMat.Cvn);
            var baseMi = new uint[baseCvn];
            for (int v = 0; v < baseCvn; v++) baseMi[v] = chain.MbeOid;
            uint name0 = nameHashes.Length > 0 ? nameHashes[0] : 0;
            var motor0 = set.MotorChar(tgt);
            motor0.SetU32Array(Doa6SingletonSet.P_Mc_NameArr, new[] { name0 });
            motor0.SetU32Array(Doa6SingletonSet.P_Mc_Mrnh, new[] { name0, chain.MbeOid });
            set.CharSetting(tgt).SetU32Array(Doa6SingletonSet.P_Cs_Mi, baseMi);
            set.MarkDirty(Doa6SingletonSet.Ce1CommonFk);

            BuildBaseKtidChain(set, mod, tgtAssets, texFk, newAssets); // base ktid 도 생성(inert)
            return new ApplyResult(newAssets, sidRegs);
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
                // 카테고리 기반: KTS(프로젝트 저장분, 임포트 시 g1m 에서 생성) + MPR 을 카테고리 순서로 빌드.
                //   템플릿(var0 MBE)은 MatIx/blob 만 제공. 변형별 KTS 순서 차이가 구조적으로 불가.
                uint template = tgtVar0[Math.Min(m, tgtVar0.Length - 1)];
                var kts = mat.Kts ?? Formats.KtsFile.SlotsFromG1mMaterial(g1mMats[Math.Min(m, g1mMats.Count - 1)]);
                var chain = MaterialChainFactory.CreateFromKts(set, template, kts, ResolveSlots(slots, texFk));
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

        // 5) base form(DM.TBC.ktid): 소스 코스튬의 base 스킨으로 repoint(canonical 참조 = 온라인 안전, 스왑 경로 동종).
        //    미처리 시 base form 이 타겟의 base 스킨으로 남아, 재로드/base-form 해석 경로에서 스킨 드리프트
        //    (소스↔타겟이 같은 캐릭터면 "스킨인데 미묘하게 다름"으로 나타남). MPR 체인(주 렌더)은 위에서 배선됨.
        if (mod.MaterialTemplateCostume is { } srcCostume)
        {
            uint srcBaseKtid = set.ResolveAssets(set.CostumeOid(srcCostume)).BaseKtid;
            if (srcBaseKtid != 0 && set.Ce.Find(tgtAssets.TbcObj) is { } tbcRec)
            {
                tbcRec.SetU32(Doa6SingletonSet.P_Tbc_Ktid, srcBaseKtid);
                set.MarkDirty(Doa6SingletonSet.CeFk);
            }
        }

        return new ApplyResult(newAssets, sidRegs);
    }

    /// <summary>타겟의 var0(베이스 변형) MBE 열 = MI 앞 slotCount 개.</summary>
    private static uint[] TargetVar0Mbes(Doa6SingletonSet.MaterialInfo m)
    {
        int sc = Math.Max(1, m.SlotCount);
        var arr = new uint[sc];
        for (int s = 0; s < sc && s < m.Mi.Length; s++) arr[s] = m.Mi[s];
        return arr;
    }

    /// <summary>
    /// base ktid 체인 생성: 소스 base ktid 를 복제해 각 슬롯의 TexContext 를 새로 만들고(→새 g1t FK) base ktid 를
    /// 새 FK 로 등록 후 타겟 DM.TBC.ktid 를 그 새 base ktid 로 repoint. 전부 온라인 안전(새 g1t/ktid FK + 싱글톤 레코드).
    /// </summary>
    private static void BuildBaseKtidChain(Doa6SingletonSet set, AuthoredCostume mod,
        Doa6SingletonSet.DmAssets tgtAssets, Dictionary<string, uint> texFk,
        List<CostumeOverride.NewAsset> newAssets)
    {
        uint srcBaseFk = mod.MaterialTemplateCostume is { } tc
            ? set.ResolveAssets(set.CostumeOid(tc)).BaseKtid : tgtAssets.BaseKtid;
        var bk = (set.Extractor.Extract(srcBaseFk) ?? throw new InvalidDataException($"소스 base ktid 0x{srcBaseFk:X8} 추출 실패")).ToArray();
        var me = set.MatEditor;
        foreach (var (slot, atRef) in mod.BaseKtid!)
        {
            int off = slot * 8 + 4;
            if (off + 4 > bk.Length || !texFk.TryGetValue(atRef, out uint g1tFk)) continue;
            uint objId = BinaryPrimitives.ReadUInt32LittleEndian(bk.AsSpan(off));
            var texTpl = me.Find(objId) ?? set.Ce.Find(objId);
            if (texTpl is null) continue;
            uint newTex = me.AllocOid();
            var texRec = texTpl.Clone(newTex);
            texRec.SetU32(Doa6SingletonSet.P_Tex_G1t, g1tFk);
            me.Insert(texRec);
            BinaryPrimitives.WriteUInt32LittleEndian(bk.AsSpan(off), newTex);
        }
        uint newBaseFk = set.AllocFk();
        newAssets.Add(new(newBaseFk, bk, "ktid"));
        if (set.Ce.Find(tgtAssets.TbcObj) is { } tbc)
            tbc.SetU32(Doa6SingletonSet.P_Tbc_Ktid, newBaseFk);
        set.MarkDirty(Doa6SingletonSet.CeFk);
        set.MarkDirty(Doa6SingletonSet.MatEditorFk);
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
