using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kashira.Core.Doa6;

/// <summary>
/// 셰이더 오버라이드 사이드카 — g1m 옆 <c>&lt;g1m&gt;.shaders.json</c> = { 메시이름해시 → 셰이더(matB) }.
/// 에디터가 메시별 셰이더 지정을 저장하고, install 이 읽어 <see cref="ShaderOverridePlan"/> 입력으로 쓴다.
/// (grp.json/mtl.json 과 같은 사이드카 패턴.) 형식: <c>{ "0x1fe387e1": "0x4bf9b7f1" }</c>.
/// </summary>
public sealed class ShaderOverrideSidecar
{
    /// <summary>메시 이름해시 → 지정 셰이더 matB.</summary>
    public Dictionary<uint, uint> Overrides { get; } = new();

    public static string PathFor(string g1mPath) => g1mPath + ".shaders.json";

    public bool IsEmpty => Overrides.Count == 0;

    public static ShaderOverrideSidecar Load(string path)
    {
        var s = new ShaderOverrideSidecar();
        if (File.Exists(path))
            foreach (var kv in ParseJson(File.ReadAllText(path))) s.Overrides[kv.Key] = kv.Value;
        return s;
    }

    /// <summary>사이드카 JSON → {메시해시 → matB}. (zip 등에서 읽은 텍스트용.)</summary>
    public static Dictionary<uint, uint> ParseJson(string json)
    {
        var map = new Dictionary<uint, uint>();
        using var doc = JsonDocument.Parse(json);
        foreach (var kv in doc.RootElement.EnumerateObject())
            if (TryHex(kv.Name, out var mesh) && kv.Value.ValueKind == JsonValueKind.String
                && TryHex(kv.Value.GetString()!, out var matB))
                map[mesh] = matB;
        return map;
    }

    public static ShaderOverrideSidecar LoadFor(string g1mPath) => Load(PathFor(g1mPath));

    /// <summary>저장. 비어 있으면 사이드카 삭제(깔끔).</summary>
    public void Save(string path)
    {
        if (IsEmpty) { if (File.Exists(path)) File.Delete(path); return; }
        var sb = new StringBuilder("{\n");
        int i = 0;
        foreach (var kv in Overrides.OrderBy(k => k.Key))
            sb.Append($"  \"0x{kv.Key:x8}\": \"0x{kv.Value:x8}\"{(++i < Overrides.Count ? "," : "")}\n");
        sb.Append("}\n");
        File.WriteAllText(path, sb.ToString());
    }

    public void SaveFor(string g1mPath) => Save(PathFor(g1mPath));

    public void Set(uint mesh, uint matB) => Overrides[mesh] = matB;
    public void Clear(uint mesh) => Overrides.Remove(mesh);
    public uint? Get(uint mesh) => Overrides.TryGetValue(mesh, out var v) ? v : null;

    private static bool TryHex(string s, out uint v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, null, out v);
    }
}
