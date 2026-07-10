using System.Text;
using Kashira.Core.Formats;

namespace Kashira.Core.Mods;

/// <summary>
/// g1m(+grp) 임포트 → 나머지 저작 포맷(mtl/manifest)을 기초 동작하도록 자동 생성.
/// g1m 재질개수로 1:1 mtl(합성 name_hash) 생성, 각 재질은 빈 슬롯(=설치 시 타겟 텍스처 상속).
/// 모더는 이후 매니페스트 슬롯에 @g1t 를 채워 실제 텍스처를 지정한다.
/// </summary>
public static class CostumeScaffold
{
    /// <summary>
    /// 프로젝트 Content/&lt;set&gt;/ 에 g1m/grp/mtl 을 쓰고 Content/&lt;set&gt;.json 매니페스트를 생성.
    /// grp 는 모더가 함께 준비(현 단계). 반환: 생성된 재질 개수.
    /// </summary>
    public static int GenerateFromG1m(KtmodProject project, string setName, string targetCostume,
                                      byte[] g1m, byte[]? grp = null)
    {
        int numMat = G1mFile.MaterialCount(g1m);

        // grp 미제공 시 g1m 슬라이싱으로 단일 파츠(0x3057221F) 자동 생성. 제공 시 디코드해 보존.
        var grpDoc = grp is null
            ? GrpDoc.SinglePart(G1mFile.MeshGroupSlicing(g1m), Doa6.Doa6GrpDefaults.MainBody)
            : GrpDoc.FromBinary(grp);

        // 파일 이름은 세트 이름 기준(@참조 전역 스코프에서 세트 간 충돌 회피)
        string baseName = SanitizeName(setName);
        string setDir = Path.Combine(project.ContentDir, baseName);
        Directory.CreateDirectory(setDir);
        // g1m = raw(별도 편집 경로). grp/mtl = JSON-first(사람이 읽고 편집; 설치 시 Manager 가 raw 변환)
        File.WriteAllBytes(Path.Combine(setDir, baseName + ".g1m"), g1m);
        File.WriteAllText(Path.Combine(setDir, baseName + ".grp.json"), grpDoc.ToJson());
        File.WriteAllText(Path.Combine(setDir, baseName + ".mtl.json"), MtlDoc.Baseline(numMat).ToJson());

        File.WriteAllText(Path.Combine(project.ContentDir, baseName + ".json"),
            BuildManifestJson(targetCostume, numMat, baseName));
        return numMat;
    }

    /// <summary>빈 슬롯 재질 numMat 개짜리 authored 매니페스트 JSON. @참조는 baseName 기준.</summary>
    private static string BuildManifestJson(string targetCostume, int numMat, string baseName)
    {
        var mats = new StringBuilder();
        for (int i = 0; i < numMat; i++)
        {
            if (i > 0) mats.Append(",\n    ");
            mats.Append("{ \"Mode\": \"constant\", \"Textures\": {} }");
        }
        return "{\n" +
               "  \"ModType\": \"Costume\",\n" +
               $"  \"TargetCostume\": \"{targetCostume}\",\n" +
               $"  \"Mesh\": {{ \"g1m\": \"@{baseName}.g1m\", \"grp\": \"@{baseName}.grp.json\", \"mtl\": \"@{baseName}.mtl.json\" }},\n" +
               "  \"VariationCount\": 1,\n" +
               $"  \"Materials\": [\n    {mats}\n  ]\n" +
               "}\n";
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "body" : cleaned;
    }
}
