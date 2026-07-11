using System.Buffers.Binary;
using System.Text;

namespace Kashira.Core.Formats;

/// <summary>
/// g1m G1MG Section 0x10003(PropertySetPallete) 파서/에디터. 재질 i ↔ 프로퍼티셋 i.
/// **머티리얼 타입 = `nMtrID` 프로퍼티**(0=기본오파크, 1=SSS피부, 2=레이어드웻, 6=알파컷아웃, 11=coeffs …).
/// 부가: rmIndex(렌더 파이프라인), fThick(SSS 두께), fRpAlphaThres(알파 컷아웃 임계).
/// 프로퍼티셋 구조: [set_count u32] 다음 셋마다 [props u32] + 프로퍼티 `[total u32][name_len u32][unk u64][name utf8][data]`.
/// nMtrID 편집은 값(u32) 제자리 교체 = **크기 불변(Tier-1)**.
/// </summary>
public static class G1mMaterialProps
{
    public const uint SectionId = 0x10003;

    /// <summary>재질 하나의 프로퍼티 요약. NMtrIDOffset = 0x10003 inner 내 nMtrID 값 오프셋(-1=없음).</summary>
    public sealed record MatProp(int Index, int NMtrID, int NMtrIDOffset, int? RmIndex, float? FThick, float? AlphaThres);

    /// <summary>컨테이너의 0x10003 → 재질별 프로퍼티. 섹션 없으면 빈 목록.</summary>
    public static IReadOnlyList<MatProp> Read(G1mContainer c)
    {
        var sec = c.FindSection(SectionId);
        return sec is null ? Array.Empty<MatProp>() : Parse(sec.Inner);
    }

    public static IReadOnlyList<MatProp> Parse(ReadOnlySpan<byte> inner)
    {
        var result = new List<MatProp>();
        if (inner.Length < 4) return result;
        int setCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(inner);
        int off = 4;
        for (int m = 0; m < setCount; m++)
        {
            if (off + 4 > inner.Length) break;
            int props = (int)BinaryPrimitives.ReadUInt32LittleEndian(inner[off..]);
            off += 4;
            int nmtr = 0, nmtrOff = -1; int? rm = null; float? fth = null, ath = null;
            for (int p = 0; p < props && off + 0x10 <= inner.Length; p++)
            {
                int total = (int)BinaryPrimitives.ReadUInt32LittleEndian(inner[off..]);
                int nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(inner[(off + 4)..]);
                if (total < 0x10 || off + total > inner.Length || off + 0x10 + nameLen > inner.Length) break;
                string name = Encoding.UTF8.GetString(inner.Slice(off + 0x10, nameLen)).TrimEnd('\0');
                int dataOff = off + 0x10 + nameLen;
                int dataLen = total - (0x10 + nameLen);
                if (dataLen >= 4)
                {
                    uint u = BinaryPrimitives.ReadUInt32LittleEndian(inner[dataOff..]);
                    switch (name)
                    {
                        case "nMtrID": nmtr = (int)u; nmtrOff = dataOff; break;
                        case "rmIndex": rm = (int)u; break;
                        case "fThick": fth = BitConverter.Int32BitsToSingle((int)u); break;
                        case "fRpAlphaThres": ath = BitConverter.Int32BitsToSingle((int)u); break;
                    }
                }
                off += total;
            }
            result.Add(new MatProp(m, nmtr, nmtrOff, rm, fth, ath));
        }
        return result;
    }

    /// <summary>재질 matIndex 의 nMtrID 값을 제자리 교체(크기 불변). 컨테이너의 0x10003 inner 를 수정. 성공=true.</summary>
    public static bool SetNMtrID(G1mContainer c, int matIndex, int value)
    {
        var sec = c.FindSection(SectionId);
        if (sec is null) return false;
        var props = Parse(sec.Inner);
        if (matIndex < 0 || matIndex >= props.Count) return false;
        int o = props[matIndex].NMtrIDOffset;
        if (o < 0 || o + 4 > sec.Inner.Length) return false;
        BinaryPrimitives.WriteUInt32LittleEndian(sec.Inner.AsSpan(o), (uint)value);
        return true;
    }

    /// <summary>nMtrID → 사람이 읽는 타입명(현재까지 규명).</summary>
    public static string TypeName(int nMtrID) => nMtrID switch
    {
        0 => "기본 오파크",
        1 => "SSS 피부",
        2 => "레이어드 웻",
        6 => "알파 컷아웃",
        11 => "coeffs",
        _ => "(미확정)",
    };
}
