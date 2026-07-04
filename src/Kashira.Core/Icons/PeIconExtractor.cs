using System.Buffers.Binary;

namespace Kashira.Core.Icons;

/// <summary>
/// Windows PE(.exe) 리소스에서 아이콘을 추출해 완전한 .ico 바이트로 반환한다.
/// System.Drawing 미사용(순수 바이트 파싱) → Windows/Linux 동일 동작.
/// 파싱 실패 시 null (호출측이 플레이스홀더로 폴백).
/// </summary>
public static class PeIconExtractor
{
    private const int RT_ICON = 3;
    private const int RT_GROUP_ICON = 14;

    public static byte[]? ExtractIco(string exePath)
    {
        try
        {
            var d = File.ReadAllBytes(exePath);
            return Extract(d);
        }
        catch { return null; }
    }

    private static byte[]? Extract(byte[] d)
    {
        if (d.Length < 0x40 || d[0] != (byte)'M' || d[1] != (byte)'Z') return null;
        int pe = I32(d, 0x3C);
        if (pe <= 0 || pe + 24 > d.Length || U32(d, pe) != 0x00004550) return null; // "PE\0\0"

        int numSections = U16(d, pe + 6);
        int sizeOpt = U16(d, pe + 20);
        int opt = pe + 24;
        ushort magic = U16(d, opt);
        int dataDir = opt + (magic == 0x20b ? 112 : 96); // PE32+ vs PE32
        uint resRva = U32(d, dataDir + 2 * 8);            // 데이터 디렉터리 index 2 = resource
        if (resRva == 0) return null;

        int secTable = opt + sizeOpt;
        var secs = new (uint va, uint vsize, uint raw, uint praw)[numSections];
        for (int i = 0; i < numSections; i++)
        {
            int s = secTable + i * 40;
            if (s + 40 > d.Length) return null;
            secs[i] = (U32(d, s + 12), U32(d, s + 8), U32(d, s + 16), U32(d, s + 20));
        }

        int RvaToOff(uint rva)
        {
            foreach (var s in secs)
            {
                uint span = Math.Max(s.vsize, s.raw);
                if (rva >= s.va && rva < s.va + span) return (int)(rva - s.va + s.praw);
            }
            return -1;
        }

        int resBase = RvaToOff(resRva);
        if (resBase < 0) return null;

        // RT_GROUP_ICON → 첫 그룹의 GRPICONDIR
        int grpDir = FindSubdirById(d, resBase, resBase, RT_GROUP_ICON);
        if (grpDir < 0) return null;
        int grpLeaf = FirstLeaf(d, resBase, grpDir);
        if (grpLeaf < 0) return null;
        int grpOff = RvaToOff(U32(d, grpLeaf)); // DATA_ENTRY.OffsetToData(RVA)
        if (grpOff < 0 || grpOff + 6 > d.Length) return null;

        int iconDir = FindSubdirById(d, resBase, resBase, RT_ICON);
        if (iconDir < 0) return null;

        int count = U16(d, grpOff + 4);
        if (count <= 0 || count > 256) return null;

        // 이미지 수집
        var images = new List<byte[]>(count);
        var entries = new List<(byte w, byte h, byte cc, byte res, ushort planes, ushort bits)>(count);
        for (int i = 0; i < count; i++)
        {
            int e = grpOff + 6 + i * 14;
            if (e + 14 > d.Length) return null;
            byte w = d[e], h = d[e + 1], cc = d[e + 2], res = d[e + 3];
            ushort planes = U16(d, e + 4), bits = U16(d, e + 6);
            ushort id = U16(d, e + 12);

            byte[] img = Array.Empty<byte>();
            int idSub = FindSubdirById(d, resBase, iconDir, id);
            if (idSub >= 0)
            {
                int leaf = FirstLeaf(d, resBase, idSub);
                if (leaf >= 0)
                {
                    uint rva = U32(d, leaf);
                    uint size = U32(d, leaf + 4);
                    int off = RvaToOff(rva);
                    if (off >= 0 && off + size <= d.Length && size > 0)
                        img = d.AsSpan(off, (int)size).ToArray();
                }
            }
            images.Add(img);
            entries.Add((w, h, cc, res, planes, bits));
        }

        // ICO 조립: ICONDIR + ICONDIRENTRY[] + 이미지들
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);      // reserved
        bw.Write((ushort)1);      // type = icon
        bw.Write((ushort)count);
        int dataOffset = 6 + count * 16;
        for (int i = 0; i < count; i++)
        {
            var en = entries[i];
            bw.Write(en.w); bw.Write(en.h); bw.Write(en.cc); bw.Write(en.res);
            bw.Write(en.planes); bw.Write(en.bits);
            bw.Write((uint)images[i].Length);
            bw.Write((uint)dataOffset);
            dataOffset += images[i].Length;
        }
        foreach (var img in images) bw.Write(img);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>디렉터리에서 정수 id 가 일치하는 엔트리의 하위 디렉터리 절대 오프셋.</summary>
    private static int FindSubdirById(byte[] d, int resBase, int dirOff, int id)
    {
        if (dirOff + 16 > d.Length) return -1;
        int named = U16(d, dirOff + 12), idc = U16(d, dirOff + 14);
        int e = dirOff + 16;
        for (int i = 0; i < named + idc; i++, e += 8)
        {
            if (e + 8 > d.Length) break;
            uint nameId = U32(d, e);
            uint offData = U32(d, e + 4);
            if ((nameId & 0x80000000) == 0 && (int)nameId == id)
                return resBase + (int)(offData & 0x7FFFFFFF);
        }
        return -1;
    }

    /// <summary>디렉터리에서 첫 엔트리를 따라 내려가 리프(DATA_ENTRY) 절대 오프셋 반환.</summary>
    private static int FirstLeaf(byte[] d, int resBase, int dirOff)
    {
        if (dirOff + 16 > d.Length) return -1;
        int named = U16(d, dirOff + 12), idc = U16(d, dirOff + 14);
        if (named + idc == 0) return -1;
        int e = dirOff + 16;
        uint offData = U32(d, e + 4);
        if ((offData & 0x80000000) != 0)
            return FirstLeaf(d, resBase, resBase + (int)(offData & 0x7FFFFFFF));
        return resBase + (int)offData; // IMAGE_RESOURCE_DATA_ENTRY
    }

    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    private static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
}
