namespace Kashira.Core.Formats;

/// <summary>확장자 → TypeKtid 매핑 (새 엔트리 등록 시 타입 결정용). 근거: 03_ktid_system.md.</summary>
public static class AssetTypes
{
    public const uint G1s = 0x7BCD279F; // system.rdb 에 주로 존재

    private static readonly Dictionary<string, uint> ExtToType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g1m"] = 0x563BDEF1,
        ["g1t"] = 0xAFBEC60C,
        ["g1a"] = 0x6FA91671,
        ["g1s"] = G1s,
        ["mtl"] = 0x5153729B,
        ["ktid"] = 0x8E39AA37,
        ["kidsobjdb"] = 0x20A6A0BB,
        ["srsa"] = 0xBBD39F2D,
        ["srst"] = 0x0D34474D,
        ["g1h"] = 0x7A2A8A4C,
        ["g1co"] = 0xB1258984,
        ["g1n"] = 0xA1A36B1A,
        ["kidsscndb"] = 0xEDEE7EBB,
    };

    public static bool TryGetTypeKtid(string? ext, out uint typeKtid)
    {
        typeKtid = 0;
        if (string.IsNullOrEmpty(ext)) return false;
        return ExtToType.TryGetValue(ext.TrimStart('.'), out typeKtid);
    }
}
