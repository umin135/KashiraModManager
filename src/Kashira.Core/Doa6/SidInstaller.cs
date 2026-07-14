using System.Text.Json;
using Kashira.Core.Formats;
using Kashira.Core.Patching;

namespace Kashira.Core.Doa6;

/// <summary>
/// Character.sid(전역 셰이더/렌더 레지스트리) 설치 — pristine sid 로드 → 메시 등록 적용 → Replacement.
///
/// 멱등: AssetExtractor 는 백업 rdb + 원본 fdata 를 읽어 항상 pristine sid 를 준다(전략 B).
/// 활성 모드 전체의 등록을 한 목록으로 넘기면 매 install pristine 에서 재계산.
///
/// 격리: 커스텀 메시는 fresh 해시 → RegisterFromDonor(도너 셰이더 복사, 소프트/리지드는 자식 개수로 상속).
/// 전역 공유 해시는 건드리지 않는다.
/// 참조: [[editor-3layer-role-split]], _docs/_plans/sid_patching_plan.md
/// </summary>
public static class SidInstaller
{
    /// <summary>새 메시 해시를 도너 메시의 셰이더로 등록.</summary>
    public sealed record Registration(uint NewMeshHash, uint DonorMeshHash);

    /// <summary>등록 목록 → Character.sid Replacement(변경 없으면 null). PatchEngine 이 system.rdb 리다이렉트(file_size 갱신).</summary>
    public static PatchEngine.Replacement? BuildReplacement(AssetExtractor ex, IReadOnlyCollection<Registration> registrations)
    {
        if (registrations.Count == 0) return null;

        var bytes = ex.Extract(CharacterSid.FileKtid);
        if (bytes is null) return null;                          // 게임 미연결/sid 없음

        var sid = CharacterSid.Parse(bytes);
        foreach (var r in registrations)
        {
            if (sid.IsRegistered(r.NewMeshHash)) continue;       // 이미(전역 원본 or 이전 등록) → skip, 멱등
            if (!sid.IsRegistered(r.DonorMeshHash)) continue;    // 도너 미등록 → skip(방어)
            sid.RegisterFromDonor(r.NewMeshHash, r.DonorMeshHash);
        }

        return sid.IsDirty
            ? new PatchEngine.Replacement(CharacterSid.FileKtid, sid.Build(), Ext: "sid")
            : null;
    }

    /// <summary>
    /// sid 등록 드라이버 JSON 읽기(최소 검증/수동용). 없으면 빈 목록.
    /// 형식: <c>[ { "new": "0x1faa87e1", "donor": "0x1fe387e1" } ]</c> (hex 문자열).
    /// </summary>
    public static List<Registration> ReadJson(string path)
    {
        var list = new List<Registration>();
        if (!File.Exists(path)) return list;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (TryHex(e, "new", out var nh) && TryHex(e, "donor", out var dh))
                list.Add(new Registration(nh, dh));
        }
        return list;

        static bool TryHex(JsonElement e, string prop, out uint v)
        {
            v = 0;
            if (!e.TryGetProperty(prop, out var p) || p.ValueKind != JsonValueKind.String) return false;
            var s = p.GetString()!.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
        }
    }
}
