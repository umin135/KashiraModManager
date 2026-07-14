using System.Globalization;
using System.Text.Json;

namespace Kashira.Core.Doa6;

/// <summary>
/// 셰이더 타입 카탈로그 — sid 셰이더-타입 노드(matB)에 사람이 읽는 이름을 붙인 게임별 공유 목록.
/// 배포: <c>&lt;Editor&gt;/res/&lt;game&gt;/shaders.json</c> (커뮤니티 확장, 에디터 import 로 자동 증식).
///
/// 셰이더 적용 = 도너 메시의 sid 자식 통째 복사(TEST4 검증). 카탈로그 엔트리는
/// matB 식별자 + 이름 + 도너 메시(exampleMeshes[0]) 를 제공한다.
/// 표시 = <c>이름(값)</c>, 미라벨은 <c>unknown(0x…)</c>.
/// 참조: _docs/_plans/custom_material_editing_plan.md, tools/verify/sid_shaders.py
/// </summary>
public sealed class ShaderCatalog
{
    public sealed record Entry(uint MatB, string Name, uint? DonorMeshHash,
                               IReadOnlyList<string> MeshTypes, int Occ, string Note)
    {
        /// <summary>UI 표시 = "이름(0x값)" / 미라벨 "unknown(0x값)".</summary>
        public string Display => $"{(string.IsNullOrWhiteSpace(Name) ? "unknown" : Name)} (0x{MatB:x8})";
    }

    private readonly Dictionary<uint, Entry> _byMatB;

    private ShaderCatalog(Dictionary<uint, Entry> byMatB) => _byMatB = byMatB;

    public static ShaderCatalog Empty { get; } = new(new());

    /// <summary>res 디렉터리 + 게임 → shaders.json 로드. 파일 없으면 빈 카탈로그.</summary>
    public static ShaderCatalog LoadForGame(string resDir, string game)
        => LoadFile(Path.Combine(resDir, game, "shaders.json"));

    public static ShaderCatalog LoadFile(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;

    public static ShaderCatalog Parse(string json)
    {
        var map = new Dictionary<uint, Entry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("shaders", out var shaders)) return new(map);

        foreach (var kv in shaders.EnumerateObject())
        {
            if (!TryHex(kv.Name, out var matB)) continue;
            var v = kv.Value;
            string name = Str(v, "name");
            string note = Str(v, "note");
            int occ = v.TryGetProperty("occ", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
            var types = new List<string>();
            if (v.TryGetProperty("meshTypes", out var mt) && mt.ValueKind == JsonValueKind.Array)
                foreach (var t in mt.EnumerateArray()) if (t.ValueKind == JsonValueKind.String) types.Add(t.GetString()!);
            uint? donor = null;
            if (v.TryGetProperty("exampleMeshes", out var ex) && ex.ValueKind == JsonValueKind.Array)
                foreach (var m in ex.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.String && TryHex(m.GetString()!, out var mh)) { donor = mh; break; }

            map[matB] = new Entry(matB, name, donor, types, occ, note);
        }
        return new ShaderCatalog(map);
    }

    public Entry? Get(uint matB) => _byMatB.GetValueOrDefault(matB);
    public bool Contains(uint matB) => _byMatB.ContainsKey(matB);
    public IEnumerable<Entry> All => _byMatB.Values;

    /// <summary>카탈로그에 있으면 "이름(값)", 없으면 "unknown(값)".</summary>
    public string Display(uint matB)
        => Get(matB)?.Display ?? $"unknown (0x{matB:x8})";

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static bool TryHex(string s, out uint v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, null, out v);
    }
}
