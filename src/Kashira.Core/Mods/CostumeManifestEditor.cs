using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kashira.Core.Mods;

/// <summary>
/// 코스튬 매니페스트(Content/&lt;set&gt;.json) 슬롯 편집기. JsonNode DOM 으로 편집해 **미편집 필드는 그대로 보존**한다.
/// - 재질 슬롯: Materials[m].Variations[col][category] = "@파일". 상속(null) 변형을 편집하면 첫 정의 변형을 복제해 실체화.
/// - base 슬롯: BaseKtid[category] = "@파일".
/// atRef=null 이면 해당 카테고리 제거.
/// </summary>
public static class CostumeManifestEditor
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>재질 m·변형 col·카테고리 category 슬롯 설정(또는 제거).</summary>
    public static void SetMaterialSlot(string manifestPath, int material, int col, int category, string? atRef)
    {
        var root = Load(manifestPath);
        if (root["Materials"] is not JsonArray mats || material < 0 || material >= mats.Count) return;
        if (mats[material] is not JsonObject mat || mat["Variations"] is not JsonArray vars) return;
        if (col < 0 || col >= vars.Count) return;

        JsonObject entry;
        if (vars[col] is JsonObject existing) entry = existing;
        else
        {
            // 상속(null) 변형을 편집 → 첫 정의된 변형을 깊은 복제해 실체화(부분 정의로 인한 미로드 방지).
            JsonObject? template = null;
            foreach (var v in vars) if (v is JsonObject o) { template = o; break; }
            entry = template is null ? new JsonObject() : JsonNode.Parse(template.ToJsonString())!.AsObject();
            vars[col] = entry;
        }
        SetKey(entry, category, atRef);
        Save(root, manifestPath);
    }

    /// <summary>재질 m 의 셰이더 타입(sid matB) 설정(또는 제거). null = 제거(g1m/도너 원본 유지).</summary>
    public static void SetMaterialShader(string manifestPath, int material, uint? matB)
    {
        var root = Load(manifestPath);
        if (root["Materials"] is not JsonArray mats || material < 0 || material >= mats.Count) return;
        if (mats[material] is not JsonObject mat) return;
        if (matB is { } v) mat["Shader"] = $"0x{v:x8}";
        else mat.Remove("Shader");
        Save(root, manifestPath);
    }

    /// <summary>base 형태(BaseKtid) 카테고리 슬롯 설정(또는 제거).</summary>
    public static void SetBaseSlot(string manifestPath, int category, string? atRef)
    {
        var root = Load(manifestPath);
        var bk = root["BaseKtid"] as JsonObject;
        if (bk is null)
        {
            if (atRef is null) return;
            bk = new JsonObject();
            root["BaseKtid"] = bk;
        }
        SetKey(bk, category, atRef);
        Save(root, manifestPath);
    }

    private static void SetKey(JsonObject obj, int category, string? atRef)
    {
        string key = category.ToString();
        if (atRef is null) obj.Remove(key);
        else obj[key] = atRef;
    }

    // ── 변형 추가/삭제(코스튬 단위) ────────────────────────────

    /// <summary>변형 1개 추가(코스튬 단위): VariationCount++ 및 모든 재질 Variations 에 null(상속) 열 추가.</summary>
    public static void AddVariation(string manifestPath)
        => ResizeAllVariations(manifestPath, GetVariationCount(manifestPath, out var root, out _) + 1, root!);

    /// <summary>변형 1개 삭제(코스튬 단위): VariationCount-- 및 모든 재질 Variations 마지막 열 제거(최소 1 유지).</summary>
    public static void RemoveVariation(string manifestPath)
    {
        int cur = GetVariationCount(manifestPath, out var root, out _);
        if (cur <= 1) return;
        ResizeAllVariations(manifestPath, cur - 1, root!);
    }

    /// <summary>현재 변형 수 = VariationCount(있으면), 없으면 재질 Variations 최대 길이(최소 1).</summary>
    public static int GetVariationCount(string manifestPath)
        => GetVariationCount(manifestPath, out _, out _);

    private static int GetVariationCount(string manifestPath, out JsonObject? root, out JsonArray? mats)
    {
        root = Load(manifestPath);
        mats = root["Materials"] as JsonArray;
        if (root["VariationCount"] is JsonValue v && v.TryGetValue<int>(out var n) && n >= 1) return n;
        int max = 1;
        if (mats is not null)
            foreach (var m in mats)
                if (m is JsonObject mo && mo["Variations"] is JsonArray va) max = Math.Max(max, va.Count);
        return max;
    }

    private static void ResizeAllVariations(string manifestPath, int count, JsonObject root)
    {
        root["VariationCount"] = count;
        if (root["Materials"] is JsonArray mats)
            foreach (var m in mats)
                if (m is JsonObject mo && mo["Variations"] is JsonArray vars)
                {
                    while (vars.Count < count) vars.Add((JsonNode?)null);          // 상속(·) 열 추가
                    while (vars.Count > count) vars.RemoveAt(vars.Count - 1);        // 마지막 열 제거
                }
        Save(root, manifestPath);
    }

    private static JsonObject Load(string path)
        => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
           ?? throw new InvalidDataException($"매니페스트 파싱 실패: {path}");

    private static void Save(JsonObject root, string path)
        => File.WriteAllText(path, root.ToJsonString(WriteOpts));
}
