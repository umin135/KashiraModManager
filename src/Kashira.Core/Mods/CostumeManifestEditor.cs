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

    private static JsonObject Load(string path)
        => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
           ?? throw new InvalidDataException($"매니페스트 파싱 실패: {path}");

    private static void Save(JsonObject root, string path)
        => File.WriteAllText(path, root.ToJsonString(WriteOpts));
}
