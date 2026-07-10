using System.Text.Json;

namespace Kashira.Core.Mods;

/// <summary>
/// Content/&lt;set&gt;.json 코스튬 매니페스트 파싱 모델. 두 모드:
///   - 스왑: SourceCostume 지정(같은 게임의 기존 코스튬으로 오버라이드, 저작 에셋 없음)
///   - 저작: Mesh + Materials 지정(Content/ 의 g1t/g1m 저작 에셋으로 새 재질 체인 생성)
/// 심볼릭 참조 = "@파일이름.ext"(ktmod 전역 스코프). 슬롯 = 숫자 인덱스, 전 슬롯 필수(설계 결정).
/// </summary>
public sealed record CostumeManifest(
    string ModType,
    string TargetCostume,
    string? SourceCostume,
    MeshRefs? Mesh,
    int VariationCount,
    IReadOnlyList<MaterialSpec> Materials)
{
    public bool IsSwap => !string.IsNullOrWhiteSpace(SourceCostume);
    public bool IsAuthored => Mesh is not null && Materials.Count > 0;

    public static CostumeManifest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string modType = Str(root, "ModType") ?? "";
            string? target = Str(root, "TargetCostume");
            if (!modType.Equals("Costume", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(target))
                return null;

            MeshRefs? mesh = null;
            if (root.TryGetProperty("Mesh", out var m) && m.ValueKind == JsonValueKind.Object)
                mesh = new MeshRefs(Str(m, "g1m"), Str(m, "grp"), Str(m, "mtl"));

            int varCount = root.TryGetProperty("VariationCount", out var vc) && vc.ValueKind == JsonValueKind.Number
                ? vc.GetInt32() : 1;

            var materials = new List<MaterialSpec>();
            if (root.TryGetProperty("Materials", out var mats) && mats.ValueKind == JsonValueKind.Array)
                foreach (var mat in mats.EnumerateArray())
                    if (ParseMaterial(mat) is { } spec) materials.Add(spec);

            return new CostumeManifest(modType, target!, Str(root, "SourceCostume"), mesh, varCount, materials);
        }
        catch { return null; }
    }

    private static MaterialSpec? ParseMaterial(JsonElement mat)
    {
        if (mat.ValueKind != JsonValueKind.Object) return null;
        string mode = Str(mat, "Mode") ?? "constant";
        bool variation = mode.Equals("variation", StringComparison.OrdinalIgnoreCase);

        var perVar = new List<IReadOnlyDictionary<int, string>>();
        if (variation)
        {
            if (mat.TryGetProperty("Variations", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray()) perVar.Add(ParseSlots(v));
        }
        else if (mat.TryGetProperty("Textures", out var tx))
        {
            perVar.Add(ParseSlots(tx));
        }
        if (perVar.Count == 0) return null;
        return new MaterialSpec(variation, perVar);
    }

    /// <summary>{ "0": "@a.g1t", "3": "@b.g1t" } → {0:@a.g1t, 3:@b.g1t}.</summary>
    private static IReadOnlyDictionary<int, string> ParseSlots(JsonElement obj)
    {
        var map = new Dictionary<int, string>();
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (int.TryParse(p.Name, out int slot) && p.Value.ValueKind == JsonValueKind.String)
                    map[slot] = p.Value.GetString()!;
        return map;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>저작 메시 에셋 심볼릭 참조(@파일이름).</summary>
public sealed record MeshRefs(string? G1m, string? Grp, string? Mtl);

/// <summary>한 재질 = 변형-영향 여부 + 변형별 (슬롯 인덱스 → @텍스처).</summary>
public sealed record MaterialSpec(bool VariationAffecting, IReadOnlyList<IReadOnlyDictionary<int, string>> Slots);
