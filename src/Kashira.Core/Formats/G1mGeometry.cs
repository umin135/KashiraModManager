using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// g1m 지오메트리 정밀 분석(읽기). tools/verify/g1m_geom.py 포트(분석 부분). raw 크기 대신 구조 표시.
/// G1MG 서브섹션은 inner 가 [count u32][entries] 로 시작(서브섹션 헤더의 3번째 u32=count).
/// Submesh(0x10008): 카운트헤더 뒤 0x38 엔트리 — vb_ref@0x04, attribute@0x14, material@0x18, ib_ref@0x1C,
///   vb_start@0x28, numv@0x2C, ib_start@0x30, numi@0x34 (g1m-decode-corrections 실측).
/// 본 수 = G1MS num_bones(u16 @ 청크 0x14 = inner 0x08).
/// </summary>
public static class G1mGeometry
{
    public sealed record Submesh(int Index, int VbRef, int IbRef, int Attribute, int Material,
                                 int VbStart, int NumVerts, int IbStart, int NumIndices)
    {
        public int Tris => NumIndices / 3;
    }

    public sealed record Info(
        int SubmeshCount, int VertexBufferCount, int IndexBufferCount,
        int MaterialCount, int BoneCount, int PaletteCount,
        long TotalVerts, long TotalTris,
        IReadOnlyList<Submesh> Submeshes);

    public static Info Analyze(G1mContainer c)
    {
        int SecCount(uint id)
        {
            var s = c.FindSection(id);
            return s is not null && s.Inner.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Inner) : 0;
        }

        int boneCount = 0;
        var g1ms = c.FindChunk(G1mContainer.G1msSig);
        if (g1ms is not null && g1ms.Inner.Length >= 0x0A)
            boneCount = BinaryPrimitives.ReadUInt16LittleEndian(g1ms.Inner.AsSpan(0x08));

        var subs = ParseSubmeshes(c);
        long totalV = 0, totalI = 0;
        foreach (var s in subs) { totalV += s.NumVerts; totalI += s.NumIndices; }

        return new Info(
            SecCount(0x10008), SecCount(0x10004), SecCount(0x10007),
            SecCount(0x10002), boneCount, SecCount(0x10006),
            totalV, totalI / 3, subs);
    }

    public static IReadOnlyList<Submesh> ParseSubmeshes(G1mContainer c)
    {
        var list = new List<Submesh>();
        var s = c.FindSection(0x10008);
        if (s is null || s.Inner.Length < 4) return list;
        var d = s.Inner;
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
        int off = 4;
        static int U(byte[] b, int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        for (int i = 0; i < n && off + 0x38 <= d.Length; i++)
        {
            list.Add(new Submesh(i,
                U(d, off + 0x04), U(d, off + 0x1C), U(d, off + 0x14), U(d, off + 0x18),
                U(d, off + 0x28), U(d, off + 0x2C), U(d, off + 0x30), U(d, off + 0x34)));
            off += 0x38;
        }
        return list;
    }
}
