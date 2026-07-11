using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kashira.Core.Doa6;
using Kashira.Core.Formats;
using Kashira.Core.Games;

namespace Kashira.Core.Mods;

/// <summary>
/// KissakiViewer v0.2.0 코스튬 번들(복구된 이름 + HashByName.json) → 편집 가능한 ktmod 프로젝트로 포팅.
///
/// 번들은 g1m과 연결된 전 에셋(g1m/mtl/grp/ktid/g1t/kts…)을 복구된 이름으로 모아둔 폴더이며,
/// HashByName.json 이 FileKtid 해시 → 복구이름 테이블을 제공한다. 단, DOA6 ktid 값은 TexContext
/// objID(= 싱글톤 DB 내부 키)라 objID→g1t 링크는 번들에 없다. 따라서 슬롯→g1t 를 정확히 얻으려면
/// 게임 싱글톤 DB(CE/ME)로 소스 코스튬 체인을 해석해야 한다(메모리 kissaki-bundle-import-method).
///
/// 흐름: 번들 g1m 파일명 = 소스 코스튬 → ResolveMaterial → 변형 v × 재질 m 마다
///   MI→MBE→TBC→MPR ktid→objID→TexContext→g1t FK → HashByName 으로 파일명 조회 → 번들에서 복사.
/// 변형별(cvn) 텍스처를 전부 캡처해 완전히 채워진 매니페스트를 생성한다(변형에 따라 constant/variation).
/// 설치는 CostumeAuthorInstaller 경로(g1m=in-place, 나머지 새 FK) — 온라인 안전.
/// </summary>
public static class BundleImporter
{
    /// <summary>포팅 결과 요약(UI 상태 표시용).</summary>
    public sealed record ImportResult(string SourceCostume, int Materials, int Variations, int Textures, int MissingG1t);

    /// <summary>
    /// 번들 폴더를 project 의 Content/&lt;setName&gt;/ 로 포팅하고 매니페스트를 생성.
    /// 소스 코스튬은 번들의 .g1m 파일명에서 추론(예: RAC_COS_005.g1m → RAC_COS_005).
    /// targetCostume = 이 모드가 오버라이드할 대상 코스튬(기본은 소스와 동일하게 두면 자기 리스킨).
    /// </summary>
    public static ImportResult Import(GameWorkspace ws, KtmodProject project, string setName,
                                      string bundleDir, string targetCostume)
    {
        // 1) 번들 핵심 파일 + HashByName
        string g1mPath = One(bundleDir, "g1m");
        string sourceCostume = Path.GetFileNameWithoutExtension(g1mPath);
        // target 미지정 시 소스와 동일(자기 리스킨)
        if (string.IsNullOrWhiteSpace(targetCostume)) targetCostume = sourceCostume;
        byte[] g1m = File.ReadAllBytes(g1mPath);
        string? grpPath = OneOrNull(bundleDir, "grp");   // grp 없으면 g1m 에서 생성
        byte[]? grp = grpPath is null ? null : File.ReadAllBytes(grpPath);
        byte[] mtl = File.ReadAllBytes(One(bundleDir, "mtl"));
        var fkToName = LoadFkNames(bundleDir);

        // 2) 게임 싱글톤 DB로 소스 코스튬 재질 구조 해석
        using var ex = AssetExtractor.Open(ws);
        var set = Doa6SingletonSet.Load(ex);
        uint srcOid = set.CostumeOid(sourceCostume);
        var mm = set.ResolveMaterial(srcOid);
        int sc = mm.SlotCount, cvn = mm.Cvn;   // MPR 변형 없는 base-only 바디면 0
        var me = set.MatEditor;

        var neededNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<uint>();

        // g1m 재질 텍스처 구조(카테고리·base 슬롯) — 캡처/설치의 앵커. tex_id → 카테고리 역맵.
        var g1mMats = G1mFile.Materials(g1m);
        var texIdToCat = new Dictionary<int, int>();
        foreach (var mat in g1mMats)
            foreach (var s in mat)
                texIdToCat[s.BaseKtidSlot] = s.Primary;

        // 2b) base ktid(기본 형태, DM.TBC.ktid) → 카테고리→g1t (base ktid 슬롯 = g1m tex_id → 카테고리).
        uint baseKtidFk = set.ResolveAssets(srcOid).BaseKtid;
        var baseSlots = ResolveKtidByCategory(set, ex, me, baseKtidFk, texIdToCat, fkToName, neededNames, missing);

        // 3) 재질 m × 변형 v → (카테고리 → g1t 파일명). null = base 폴백(MI=0). MPR 없으면 빈 배열.
        //    카테고리는 MBE 의 네이티브 KTS(slot→primary)로 해석 → 슬롯 순서 차이에 불변.
        var perMat = new Dictionary<int, string>?[Math.Max(0, sc)][];
        for (int m = 0; m < sc; m++)
        {
            perMat[m] = new Dictionary<int, string>?[cvn];
            for (int v = 0; v < cvn; v++)
                perMat[m][v] = ResolveMbeByCategory(set, ex, me, mm.Mi[v * sc + m], fkToName, neededNames, missing);
        }

        // 4) 세트 폴더에 에셋 기록 (g1m=raw, grp/mtl=JSON, g1t=복사)
        string baseName = SanitizeName(setName);
        string setDir = Path.Combine(project.ContentDir, baseName);
        Directory.CreateDirectory(setDir);
        File.WriteAllBytes(Path.Combine(setDir, baseName + ".g1m"), g1m);
        File.WriteAllText(Path.Combine(setDir, baseName + ".grp.json"), GrpToJson(grp, g1m));
        File.WriteAllText(Path.Combine(setDir, baseName + ".mtl.json"), MtlDoc.FromBinary(mtl).ToJson());
        foreach (var name in neededNames)
        {
            string src = Path.Combine(bundleDir, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(setDir, name), true);
        }

        // 4b) KTS(슬롯 스키마)를 g1m 에서 생성해 프로젝트에 저장(편집 가능 에셋). 재질 m ↔ g1m 재질 m.
        for (int m = 0; m < g1mMats.Count; m++)
            File.WriteAllText(Path.Combine(setDir, $"{baseName}.mat{m}.kts.json"),
                KtsFile.ToJson(KtsFile.SlotsFromG1mMaterial(g1mMats[m])));

        // 5) 매니페스트(base ktid + 변형별 MPR + KTS 참조)
        File.WriteAllText(Path.Combine(project.ContentDir, baseName + ".json"),
            BuildManifest(targetCostume, sourceCostume, baseName, Math.Max(1, cvn), baseSlots, perMat, g1mMats.Count));

        return new ImportResult(sourceCostume, sc, cvn, neededNames.Count, missing.Count);
    }

    /// <summary>MBE → (KTS 로 슬롯→카테고리) + MPR ktid(슬롯→g1t) → **카테고리 → g1t 파일명**. mbe==0 이면 null(base 폴백).</summary>
    private static Dictionary<int, string>? ResolveMbeByCategory(Doa6SingletonSet set, AssetExtractor ex, SingletonDb me,
        uint mbe, IReadOnlyDictionary<uint, string> fkToName, HashSet<string> needed, HashSet<uint> missing)
    {
        if (mbe == 0) return null; // MI=0 = base 폴백
        var map = new Dictionary<int, string>();
        if (me.Find(mbe) is not { } mbeRec) return map;
        // 네이티브 KTS: slot → primary(카테고리)
        var kts = ex.Extract(mbeRec.ReadU32(Doa6SingletonSet.P_Mbe_Kts)) is { } ktsBytes
            ? KtsFile.Parse(ktsBytes) : (IReadOnlyList<KtsFile.Slot>)Array.Empty<KtsFile.Slot>();
        uint tbc = mbeRec.ReadU32(Doa6SingletonSet.P_Dm_TbcObj);
        if (me.Find(tbc) is not { } tbcRec) return map;
        var mpr = ex.Extract(tbcRec.ReadU32(Doa6SingletonSet.P_Tbc_Ktid));
        if (mpr is null) return map;
        for (int j = 0; j < mpr.Length / 8; j++)
        {
            if (ResolveTexName(set, me, BinaryPrimitives.ReadUInt32LittleEndian(mpr.AsSpan(j * 8 + 4)),
                    fkToName, missing) is not { } name) continue;
            int category = j < kts.Count ? kts[j].Primary : j; // KTS 없으면 슬롯 인덱스 폴백
            map[category] = name; needed.Add(name);
        }
        return map;
    }

    /// <summary>base/MPR ktid 슬롯 → 카테고리 → g1t 파일명. slotToCat: 슬롯(=g1m tex_id 또는 KTS slot)→카테고리.</summary>
    private static Dictionary<int, string> ResolveKtidByCategory(Doa6SingletonSet set, AssetExtractor ex, SingletonDb me,
        uint ktidFk, IReadOnlyDictionary<int, int> slotToCat,
        IReadOnlyDictionary<uint, string> fkToName, HashSet<string> needed, HashSet<uint> missing)
    {
        var map = new Dictionary<int, string>();
        var data = ktidFk != 0 ? ex.Extract(ktidFk) : null;
        if (data is null) return map;
        for (int s = 0; s < data.Length / 8; s++)
        {
            if (ResolveTexName(set, me, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(s * 8 + 4)),
                    fkToName, missing) is not { } name) continue;
            int category = slotToCat.TryGetValue(s, out int c) ? c : s;
            map[category] = name; needed.Add(name);
        }
        return map;
    }

    /// <summary>TexContext objID → g1t FK → HashByName 파일명. 미매칭 g1t 는 missing 에 기록.</summary>
    private static string? ResolveTexName(Doa6SingletonSet set, SingletonDb me, uint objId,
        IReadOnlyDictionary<uint, string> fkToName, HashSet<uint> missing)
    {
        var tc = me.Find(objId) ?? set.Ce.Find(objId);
        if (tc is null || tc.Type != Doa6SingletonSet.T_TexCtx) return null;
        uint g1t = tc.ReadU32(Doa6SingletonSet.P_Tex_G1t);
        if (fkToName.TryGetValue(g1t, out var name)) return name;
        missing.Add(g1t); return null;
    }

    private static string BuildManifest(string target, string source, string baseName, int cvn,
        Dictionary<int, string> baseSlots, Dictionary<int, string>?[][] perMat, int g1mMatCount)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"ModType\": \"Costume\",\n");
        sb.Append($"  \"TargetCostume\": \"{target}\",\n");
        sb.Append($"  \"MaterialTemplate\": \"{source}\",\n");
        sb.Append($"  \"Mesh\": {{ \"g1m\": \"@{baseName}.g1m\", \"grp\": \"@{baseName}.grp.json\", \"mtl\": \"@{baseName}.mtl.json\" }},\n");
        sb.Append($"  \"VariationCount\": {cvn},\n");
        // base ktid = 기본 형태(초기값) 텍스처. MPR 변형 없는 base-only 바디는 이것 + BaseKts 로 정의된다.
        if (baseSlots.Count > 0)
        {
            sb.Append("  \"BaseKtid\": " + Slots(baseSlots) + ",\n");
            if (g1mMatCount > 0) sb.Append($"  \"BaseKts\": \"@{baseName}.mat0.kts.json\",\n");
        }
        sb.Append("  \"Materials\": [\n");
        for (int m = 0; m < perMat.Length; m++)
        {
            string kts = m < g1mMatCount ? $", \"Kts\": \"@{baseName}.mat{m}.kts.json\"" : "";
            // 변형 전부 비-null 이고 동일 → constant. 하나라도 null(base 폴백)이거나 다르면 variation.
            if (ConstantAcrossVariations(perMat[m]))
                sb.Append("    { \"Mode\": \"constant\", \"Textures\": " + Slots(perMat[m][0]!) + kts + " }");
            else
                sb.Append("    { \"Mode\": \"variation\", \"Variations\": ["
                          + string.Join(", ", perMat[m].Select(s => s is null ? "null" : Slots(s))) + "]" + kts + " }");
            sb.Append(m < perMat.Length - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string Slots(Dictionary<int, string> slots)
        => "{" + string.Join(", ", slots.OrderBy(kv => kv.Key).Select(kv => $"\"{kv.Key}\": \"@{kv.Value}\"")) + "}";

    private static bool ConstantAcrossVariations(Dictionary<int, string>?[] perVar)
    {
        if (perVar[0] is null) return false;
        for (int v = 1; v < perVar.Length; v++)
            if (perVar[v] is null || !SameMap(perVar[0]!, perVar[v]!)) return false;
        return true;
    }

    private static bool SameMap(Dictionary<int, string> a, Dictionary<int, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, val) in a)
            if (!b.TryGetValue(k, out var bv) || !string.Equals(bv, val, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// FileKtid → 번들 파일명 맵. HashByName.json(복구된 이름) + 해시명 파일(`0x{fk}.{ext}`, 이름복구 실패분)을 합친다.
    /// 이름복구가 안 된 에셋은 파일명 자체가 FK 해시이므로 그걸로 해석한다.
    /// </summary>
    private static Dictionary<uint, string> LoadFkNames(string bundleDir)
    {
        var map = new Dictionary<uint, string>();
        string path = Path.Combine(bundleDir, "HashByName.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                string key = p.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p.Name[2..] : p.Name;
                if (uint.TryParse(key, System.Globalization.NumberStyles.HexNumber, null, out uint fk)
                    && p.Value.ValueKind == JsonValueKind.String)
                    map[fk] = p.Value.GetString()!;
            }
        }
        // 해시명 파일(0x{8hex}.{ext}) — 이름복구 실패분. 파일명 = FK.
        foreach (var file in Directory.EnumerateFiles(bundleDir))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(stem.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint fk)
                && !map.ContainsKey(fk))
                map[fk] = Path.GetFileName(file);
        }
        return map;
    }

    /// <summary>번들 grp → JSON. grp 없음/파싱 실패(비표준·손상)하면 g1m 슬라이싱 기반 단일 파츠(0x3057221F)로 생성.</summary>
    private static string GrpToJson(byte[]? grp, byte[] g1m)
    {
        try { if (grp is not null) return GrpDoc.FromBinary(grp).ToJson(); }
        catch { /* 비표준 grp → 아래 g1m 생성으로 폴백 */ }
        return GrpDoc.SinglePart(G1mFile.MeshGroupSlicing(g1m), Doa6.Doa6GrpDefaults.MainBody).ToJson();
    }

    private static string One(string dir, string ext)
        => OneOrNull(dir, ext) ?? throw new FileNotFoundException($"번들에 .{ext} 없음: {dir}");

    private static string? OneOrNull(string dir, string ext)
        => Directory.EnumerateFiles(dir, "*." + ext).FirstOrDefault();

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "bundle" : cleaned;
    }
}
