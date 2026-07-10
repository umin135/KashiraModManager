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
        byte[] grp = File.ReadAllBytes(One(bundleDir, "grp"));
        byte[] mtl = File.ReadAllBytes(One(bundleDir, "mtl"));
        var fkToName = LoadHashByName(bundleDir);

        // 2) 게임 싱글톤 DB로 소스 코스튬 재질 구조 해석
        using var ex = AssetExtractor.Open(ws);
        var set = Doa6SingletonSet.Load(ex);
        var mm = set.ResolveMaterial(set.CostumeOid(sourceCostume));
        int sc = Math.Max(1, mm.SlotCount), cvn = Math.Max(1, mm.Cvn);
        var me = set.MatEditor;

        // 3) 재질 m × 변형 v → (텍스처 슬롯 인덱스 → g1t 파일명). null = base 폴백(MI=0).
        var perMat = new Dictionary<int, string>?[sc][];
        var neededNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<uint>();
        for (int m = 0; m < sc; m++)
        {
            perMat[m] = new Dictionary<int, string>?[cvn];
            for (int v = 0; v < cvn; v++)
                perMat[m][v] = ResolveMbe(set, ex, me, mm.Mi[v * sc + m], fkToName, neededNames, missing);
        }

        // 4) 세트 폴더에 에셋 기록 (g1m=raw, grp/mtl=JSON, g1t=복사)
        string baseName = SanitizeName(setName);
        string setDir = Path.Combine(project.ContentDir, baseName);
        Directory.CreateDirectory(setDir);
        File.WriteAllBytes(Path.Combine(setDir, baseName + ".g1m"), g1m);
        File.WriteAllText(Path.Combine(setDir, baseName + ".grp.json"), GrpDoc.FromBinary(grp).ToJson());
        File.WriteAllText(Path.Combine(setDir, baseName + ".mtl.json"), MtlDoc.FromBinary(mtl).ToJson());
        foreach (var name in neededNames)
        {
            string src = Path.Combine(bundleDir, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(setDir, name), true);
        }

        // 5) 변형별 매니페스트
        File.WriteAllText(Path.Combine(project.ContentDir, baseName + ".json"),
            BuildManifest(targetCostume, sourceCostume, baseName, cvn, perMat));

        return new ImportResult(sourceCostume, sc, cvn, neededNames.Count, missing.Count);
    }

    /// <summary>MBE → MPR ktid → 각 텍스처 슬롯의 objID → TexContext → g1t FK → HashByName 파일명.
    /// mbe==0 이면 null(base 폴백)을 반환한다.</summary>
    private static Dictionary<int, string>? ResolveMbe(Doa6SingletonSet set, AssetExtractor ex, SingletonDb me,
        uint mbe, IReadOnlyDictionary<uint, string> fkToName, HashSet<string> needed, HashSet<uint> missing)
    {
        if (mbe == 0) return null; // MI=0 = base 폴백
        var map = new Dictionary<int, string>();
        if (me.Find(mbe) is not { } mbeRec) return map;
        uint tbc = mbeRec.ReadU32(Doa6SingletonSet.P_Dm_TbcObj);
        if (me.Find(tbc) is not { } tbcRec) return map;
        uint mprFk = tbcRec.ReadU32(Doa6SingletonSet.P_Tbc_Ktid);
        var mpr = ex.Extract(mprFk);
        if (mpr is null) return map;
        for (int s = 0; s < mpr.Length / 8; s++)
        {
            uint objid = BinaryPrimitives.ReadUInt32LittleEndian(mpr.AsSpan(s * 8 + 4));
            var tc = me.Find(objid) ?? set.Ce.Find(objid);
            if (tc is null || tc.Type != Doa6SingletonSet.T_TexCtx) continue;
            uint g1t = tc.ReadU32(Doa6SingletonSet.P_Tex_G1t);
            if (fkToName.TryGetValue(g1t, out var name)) { map[s] = name; needed.Add(name); }
            else missing.Add(g1t);
        }
        return map;
    }

    private static string BuildManifest(string target, string source, string baseName, int cvn,
        Dictionary<int, string>?[][] perMat)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"ModType\": \"Costume\",\n");
        sb.Append($"  \"TargetCostume\": \"{target}\",\n");
        sb.Append($"  \"MaterialTemplate\": \"{source}\",\n");
        sb.Append($"  \"Mesh\": {{ \"g1m\": \"@{baseName}.g1m\", \"grp\": \"@{baseName}.grp.json\", \"mtl\": \"@{baseName}.mtl.json\" }},\n");
        sb.Append($"  \"VariationCount\": {cvn},\n");
        sb.Append("  \"Materials\": [\n");
        for (int m = 0; m < perMat.Length; m++)
        {
            // 변형 전부 비-null 이고 동일 → constant. 하나라도 null(base 폴백)이거나 다르면 variation.
            if (ConstantAcrossVariations(perMat[m]))
                sb.Append("    { \"Mode\": \"constant\", \"Textures\": " + Slots(perMat[m][0]!) + " }");
            else
                sb.Append("    { \"Mode\": \"variation\", \"Variations\": ["
                          + string.Join(", ", perMat[m].Select(s => s is null ? "null" : Slots(s))) + "] }");
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

    /// <summary>HashByName.json → (FileKtid → 복구된 파일명). 키는 "0x????????" 16진.</summary>
    private static Dictionary<uint, string> LoadHashByName(string bundleDir)
    {
        string path = Path.Combine(bundleDir, "HashByName.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"HashByName.json 없음: {bundleDir}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<uint, string>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            string key = p.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p.Name[2..] : p.Name;
            if (uint.TryParse(key, System.Globalization.NumberStyles.HexNumber, null, out uint fk)
                && p.Value.ValueKind == JsonValueKind.String)
                map[fk] = p.Value.GetString()!;
        }
        return map;
    }

    private static string One(string dir, string ext)
        => Directory.EnumerateFiles(dir, "*." + ext).FirstOrDefault()
           ?? throw new FileNotFoundException($"번들에 .{ext} 없음: {dir}");

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "bundle" : cleaned;
    }
}
