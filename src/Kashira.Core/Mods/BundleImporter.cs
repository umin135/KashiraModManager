using System.Text;
using Kashira.Core.Doa6;
using Kashira.Core.Formats;
using Kashira.Core.Games;

namespace Kashira.Core.Mods;

/// <summary>
/// KissakiViewer Bundle Export(코스튬 전체 에셋) → 편집 가능한 ktmod 프로젝트로 포팅.
/// 소스 코스튬 구조를 게임에서 해석해 재질 슬롯→g1t 매핑을 만들고, g1m=raw / grp·mtl=JSON / g1t=복사로 세트 구성.
/// 매니페스트 MaterialTemplate=소스 코스튬으로 소스 KTS/슬롯 레이아웃 보존(슬롯 밀림 방지).
/// </summary>
public static class BundleImporter
{
    /// <summary>번들 폴더에서 확장자로 g1m/grp/mtl 파일 경로를 찾는다.</summary>
    private static string One(string dir, string ext)
        => Directory.EnumerateFiles(dir, "*." + ext).FirstOrDefault()
           ?? throw new FileNotFoundException($"번들에 .{ext} 없음: {dir}");

    /// <summary>
    /// 번들을 project 의 Content/&lt;setName&gt;/ 로 포팅하고 매니페스트를 생성. 반환: 생성된 재질 수.
    /// sourceCostume = 번들의 원본 코스튬(예 RYU_COS_008), targetCostume = 오버라이드 대상.
    /// </summary>
    public static int Import(GameWorkspace ws, KtmodProject project, string setName,
                             string bundleDir, string sourceCostume, string targetCostume)
    {
        byte[] g1m = File.ReadAllBytes(One(bundleDir, "g1m"));
        byte[] grp = File.ReadAllBytes(One(bundleDir, "grp"));
        byte[] mtl = File.ReadAllBytes(One(bundleDir, "mtl"));

        using var ex = AssetExtractor.Open(ws);
        var set = Doa6SingletonSet.Load(ex);
        var mm = set.ResolveMaterial(set.CostumeOid(sourceCostume));
        var me = set.MatEditor;

        // 재질별 슬롯 → g1t FK (소스 코스튬 MBE→MPR ktid→TexContext→g1t)
        var matSlots = new List<Dictionary<int, uint>>();
        var neededG1t = new HashSet<uint>();
        for (int m = 0; m < mm.SlotCount; m++)
        {
            uint mbe = mm.Mi[m];
            uint tbc = me.Find(mbe)!.ReadU32(Doa6SingletonSet.P_Dm_TbcObj);
            uint mprFk = me.Find(tbc)!.ReadU32(Doa6SingletonSet.P_Tbc_Ktid);
            var mpr = ex.Extract(mprFk)!;
            var slots = new Dictionary<int, uint>();
            for (int s = 0; s < mpr.Length / 8; s++)
            {
                uint objid = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mpr.AsSpan(s * 8 + 4));
                var tc = me.Find(objid) ?? set.Ce.Find(objid);
                if (tc != null && tc.Type == Doa6SingletonSet.T_TexCtx)
                {
                    uint g1tfk = tc.ReadU32(Doa6SingletonSet.P_Tex_G1t);
                    slots[s] = g1tfk; neededG1t.Add(g1tfk);
                }
            }
            matSlots.Add(slots);
        }

        // 세트 파일: g1m=raw, grp/mtl=JSON, g1t=복사
        string baseName = SanitizeName(setName);
        string setDir = Path.Combine(project.ContentDir, baseName);
        Directory.CreateDirectory(setDir);
        File.WriteAllBytes(Path.Combine(setDir, baseName + ".g1m"), g1m);
        File.WriteAllText(Path.Combine(setDir, baseName + ".grp.json"), GrpDoc.FromBinary(grp).ToJson());
        File.WriteAllText(Path.Combine(setDir, baseName + ".mtl.json"), MtlDoc.FromBinary(mtl).ToJson());
        foreach (var fk in neededG1t)
            File.Copy(Path.Combine(bundleDir, $"0x{fk:x8}.g1t"), Path.Combine(setDir, $"0x{fk:x8}.g1t"), true);

        File.WriteAllText(Path.Combine(project.ContentDir, baseName + ".json"),
            BuildManifest(targetCostume, sourceCostume, baseName, mm.Cvn, matSlots));
        return matSlots.Count;
    }

    private static string BuildManifest(string target, string source, string baseName, int variationCount,
                                        List<Dictionary<int, uint>> matSlots)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"ModType\": \"Costume\",\n");
        sb.Append($"  \"TargetCostume\": \"{target}\",\n");
        sb.Append($"  \"MaterialTemplate\": \"{source}\",\n");
        sb.Append($"  \"Mesh\": {{ \"g1m\": \"@{baseName}.g1m\", \"grp\": \"@{baseName}.grp.json\", \"mtl\": \"@{baseName}.mtl.json\" }},\n");
        sb.Append($"  \"VariationCount\": {Math.Max(1, variationCount)},\n");
        sb.Append("  \"Materials\": [\n");
        for (int m = 0; m < matSlots.Count; m++)
        {
            var tx = string.Join(", ", matSlots[m].Select(kv => $"\"{kv.Key}\": \"@0x{kv.Value:x8}.g1t\""));
            sb.Append("    { \"Mode\": \"constant\", \"Textures\": {" + tx + "} }");
            sb.Append(m < matSlots.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "bundle" : cleaned;
    }
}
