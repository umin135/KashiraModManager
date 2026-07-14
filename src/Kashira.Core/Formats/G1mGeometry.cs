using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// g1m 지오메트리 정밀 분석(읽기). tools/verify/g1m_geom.py 분석부 포트(게임검증). raw 크기 대신 구조.
/// G1MG 서브섹션 inner = [count u32][entries]. 참조 오프셋은 g1m_geom.py·g1m-decode-corrections 실측.
/// </summary>
public static class G1mGeometry
{
    public sealed record Submesh(int Index, int VbRef, int IbRef, int Attribute, int Material,
                                 int VbStart, int NumVerts, int IbStart, int NumIndices)
    {
        public int Tris => NumIndices / 3;
        public int Vsize { get; init; }
        public int Layout { get; init; } = -1;
        public int ClothId { get; init; }
    }

    public sealed record VertexBuffer(int Index, int VertexSize, int NumVerts, int Layout);
    public sealed record IndexBuffer(int Index, int NumIndices, int IndexType)
    {
        public string TypeName => IndexType switch { 8 => "u8", 0x10 => "u16", 0x20 => "u32", _ => $"0x{IndexType:X}" };
    }
    public sealed record Semantic(int Kind, int DataType, int Offset, int Layer)
    {
        public string KindName => Kind switch
        {
            0 => "POSITION", 1 => "BLENDWEIGHT", 2 => "BLENDINDICES", 3 => "NORMAL",
            4 => "TANGENT", 5 => "TEXCOORD", 6 => "BITANGENT", _ => $"sem{Kind}",
        };
        public string DataTypeName => DataType switch
        {
            0 => "Float1", 1 => "Float2", 2 => "Float3", 3 => "Float4", 5 => "UByte4",
            7 => "UShort4", 9 => "UInt4", 0x0A => "Half2", 0x0B => "Half4", 0x0D => "NormUByte4",
            _ => $"dt{DataType:X}",
        };
    }
    public sealed record Layout(int Index, IReadOnlyList<Semantic> Semantics);

    public sealed record Info(
        int SubmeshCount, int VertexBufferCount, int IndexBufferCount,
        int MaterialCount, int BoneCount, int PaletteCount,
        long TotalVerts, long TotalTris,
        IReadOnlyList<Submesh> Submeshes,
        IReadOnlyList<VertexBuffer> VertexBuffers,
        IReadOnlyList<IndexBuffer> IndexBuffers,
        IReadOnlyList<Layout> Layouts,
        IReadOnlyList<int> PaletteSizes);

    private static int U(byte[] b, int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static int U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

    public static Info Analyze(G1mContainer c)
    {
        var vbs = ParseVertexBuffers(c, out var vb2lay);
        var ibs = ParseIndexBuffers(c);
        var layouts = ParseLayouts(c);
        var cloth = ParseClothFlags(c);
        var raw = ParseSubmeshes(c);

        // 서브메시 enrich: vsize(VB) · layout(vb2lay) · cloth
        var subs = new List<Submesh>(raw.Count);
        foreach (var s in raw)
        {
            int vsize = s.VbRef >= 0 && s.VbRef < vbs.Count ? vbs[s.VbRef].VertexSize : 0;
            int lay = vb2lay.TryGetValue(s.VbRef, out var l) ? l : -1;
            cloth.TryGetValue(s.Index, out var cl);
            subs.Add(s with { Vsize = vsize, Layout = lay, ClothId = cl });
        }

        int boneCount = 0;
        var g1ms = c.FindChunk(G1mContainer.G1msSig);
        if (g1ms is not null && g1ms.Inner.Length >= 0x0A)
            boneCount = U16(g1ms.Inner, 0x08);

        var palSizes = ParsePaletteSizes(c);

        long totalV = 0, totalI = 0;
        foreach (var s in subs) { totalV += s.NumVerts; totalI += s.NumIndices; }

        return new Info(
            SecCount(c, 0x10008), vbs.Count, ibs.Count,
            SecCount(c, 0x10002), boneCount, palSizes.Count,
            totalV, totalI / 3, subs, vbs, ibs, layouts, palSizes);
    }

    private static int SecCount(G1mContainer c, uint id)
    {
        var s = c.FindSection(id);
        return s is not null && s.Inner.Length >= 4 ? U(s.Inner, 0) : 0;
    }

    /// <summary>Submesh(0x10008) 원시 엔트리(0x38). 리빌드/조인용.</summary>
    public static IReadOnlyList<Submesh> ParseSubmeshes(G1mContainer c)
    {
        var list = new List<Submesh>();
        var s = c.FindSection(0x10008);
        if (s is null || s.Inner.Length < 4) return list;
        var d = s.Inner;
        int n = U(d, 0), off = 4;
        for (int i = 0; i < n && off + 0x38 <= d.Length; i++)
        {
            list.Add(new Submesh(i,
                U(d, off + 0x04), U(d, off + 0x1C), U(d, off + 0x14), U(d, off + 0x18),
                U(d, off + 0x28), U(d, off + 0x2C), U(d, off + 0x30), U(d, off + 0x34)));
            off += 0x38;
        }
        return list;
    }

    /// <summary>VertexBuffer(0x10004): (vsize, numv). vb2lay 채움(레이아웃 파싱 후 병합용 placeholder).</summary>
    public static IReadOnlyList<VertexBuffer> ParseVertexBuffers(G1mContainer c, out Dictionary<int, int> vb2lay)
    {
        vb2lay = LayoutRefMap(c);
        var list = new List<VertexBuffer>();
        var s = c.FindSection(0x10004);
        if (s is null || s.Inner.Length < 4) return list;
        var d = s.Inner;
        int n = U(d, 0), p = 4;
        for (int i = 0; i < n && p + 0x10 <= d.Length; i++)
        {
            int vsz = U(d, p + 4), nv = U(d, p + 8);
            p += 0x10;
            list.Add(new VertexBuffer(i, vsz, nv, vb2lay.TryGetValue(i, out var l) ? l : -1));
            p += vsz * nv;
        }
        return list;
    }

    /// <summary>IndexBuffer(0x10007): (numi, itype). 4바이트 정렬 데이터.</summary>
    public static IReadOnlyList<IndexBuffer> ParseIndexBuffers(G1mContainer c)
    {
        var list = new List<IndexBuffer>();
        var s = c.FindSection(0x10007);
        if (s is null || s.Inner.Length < 4) return list;
        var d = s.Inner;
        int n = U(d, 0), p = 4;
        for (int i = 0; i < n && p + 0x0C <= d.Length; i++)
        {
            int ni = U(d, p), it = U(d, p + 4);
            p += 0x0C;
            list.Add(new IndexBuffer(i, ni, it));
            int isz = it switch { 8 => 1, 0x10 => 2, 0x20 => 4, _ => 2 };
            p += ((ni * isz) + 3) & ~3;
        }
        return list;
    }

    /// <summary>VtxAttributes(0x10005): 레이아웃별 시맨틱.</summary>
    public static IReadOnlyList<Layout> ParseLayouts(G1mContainer c)
    {
        var list = new List<Layout>();
        var s = c.FindSection(0x10005);
        if (s is null || s.Inner.Length < 4) return list;
        var d = s.Inner;
        int n = U(d, 0), p = 4;
        for (int li = 0; li < n && p + 4 <= d.Length; li++)
        {
            int nref = U(d, p); p += 4 + nref * 4;
            if (p + 4 > d.Length) break;
            int nsem = U(d, p); p += 4;
            var sems = new List<Semantic>(nsem);
            for (int j = 0; j < nsem && p + 8 <= d.Length; j++)
            {
                // bidx@0, off@2, dt@4, sem@6, layer@7
                sems.Add(new Semantic(Kind: d[p + 6], DataType: U16(d, p + 4), Offset: U16(d, p + 2), Layer: d[p + 7]));
                p += 8;
            }
            list.Add(new Layout(li, sems));
        }
        return list;
    }

    private static Dictionary<int, int> LayoutRefMap(G1mContainer c)
    {
        var map = new Dictionary<int, int>();
        var s = c.FindSection(0x10005);
        if (s is null || s.Inner.Length < 4) return map;
        var d = s.Inner;
        int n = U(d, 0), p = 4;
        for (int li = 0; li < n && p + 4 <= d.Length; li++)
        {
            int nref = U(d, p); p += 4;
            for (int j = 0; j < nref && p + 4 <= d.Length; j++) { map[U(d, p)] = li; p += 4; }
            if (p + 4 > d.Length) break;
            int nsem = U(d, p); p += 4 + nsem * 8;
        }
        return map;
    }

    /// <summary>MeshGroups(0x10009) → 서브메시 인덱스 → cloth_id. 엔트리는 "@"(0x40) 이름 마커로 순회(오프셋 이견 회피).</summary>
    public static Dictionary<int, int> ParseClothFlags(G1mContainer c)
    {
        var map = new Dictionary<int, int>();
        var s = c.FindSection(0x10009);
        if (s is null) return map;
        var d = s.Inner;
        int p = 0x20;
        while (p < d.Length && d[p] != 0x40) p += 4;   // 첫 '@' 로 이동(4바이트 정렬)
        while (p + 0x1C <= d.Length && d[p] == 0x40)
        {
            int cloth = U16(d, p + 0x10);
            int idxc = U(d, p + 0x18);
            for (int k = 0; k < idxc && p + 0x1C + k * 4 + 4 <= d.Length; k++)
                map[U(d, p + 0x1C + k * 4)] = cloth;
            p += 0x1C + idxc * 4;
        }
        return map;
    }

    /// <summary>메시그룹 엔트리 = 이름해시(@XXXXXXXX) + meshType(0리지드/1NUNO/2NUNV/4SOFT) + extid + 서브메시 인덱스들.</summary>
    public sealed record MeshGroup(uint NameHash, int MeshType, uint ExtId, IReadOnlyList<int> Submeshes);

    /// <summary>
    /// MeshGroups(0x10009) → 엔트리 목록. 이름해시 = sid/grp 조인 키.
    /// 한 이름해시가 여러 엔트리(LOD/패스)로 나뉠 수 있음(호출측에서 그룹핑).
    /// </summary>
    public static IReadOnlyList<MeshGroup> ParseMeshGroups(G1mContainer c)
    {
        var list = new List<MeshGroup>();
        var s = c.FindSection(0x10009);
        if (s is null) return list;
        var d = s.Inner;
        int p = 0x20;
        while (p < d.Length && d[p] != 0x40) p += 4;
        while (p + 0x1C <= d.Length && d[p] == 0x40)
        {
            uint hash = ParseNameHash(d, p);
            int meshType = U16(d, p + 0x10);
            uint extId = (uint)U(d, p + 0x14);
            int idxc = U(d, p + 0x18);
            var subs = new List<int>(idxc);
            for (int k = 0; k < idxc && p + 0x1C + k * 4 + 4 <= d.Length; k++)
                subs.Add(U(d, p + 0x1C + k * 4));
            list.Add(new MeshGroup(hash, meshType, extId, subs));
            p += 0x1C + idxc * 4;
        }
        return list;
    }

    /// <summary>메시그룹 이름 "@XXXXXXXX"(대문자 hex) → u32 해시. 파싱 실패 시 0.</summary>
    private static uint ParseNameHash(byte[] d, int p)
    {
        uint v = 0;
        for (int k = 1; k <= 8; k++)
        {
            int cc = d[p + k];
            int digit = cc is >= (byte)'0' and <= (byte)'9' ? cc - '0'
                      : cc is >= (byte)'A' and <= (byte)'F' ? cc - 'A' + 10
                      : cc is >= (byte)'a' and <= (byte)'f' ? cc - 'a' + 10 : -1;
            if (digit < 0) return 0;
            v = (v << 4) | (uint)digit;
        }
        return v;
    }

    /// <summary>메시그룹 이름해시 재작성(size-preserving) — {old→new}. 반환=바뀐 엔트리 수. fresh 해시 격리용.</summary>
    public static int RenameMeshGroups(G1mContainer c, IReadOnlyDictionary<uint, uint> map)
    {
        var s = c.FindSection(0x10009);
        return s is null ? 0 : RenameInSection(s.Inner, map);
    }

    /// <summary>0x10009 inner 바이트에서 메시그룹 이름해시를 제자리 재작성. 순수 함수(테스트용). 크기 불변.</summary>
    public static int RenameInSection(byte[] d, IReadOnlyDictionary<uint, uint> map)
    {
        int p = 0x20, count = 0;
        while (p < d.Length && d[p] != 0x40) p += 4;
        while (p + 0x1C <= d.Length && d[p] == 0x40)
        {
            uint hash = ParseNameHash(d, p);
            if (map.TryGetValue(hash, out var nh)) { WriteNameHash(d, p, nh); count++; }
            int idxc = U(d, p + 0x18);
            p += 0x1C + idxc * 4;
        }
        return count;
    }

    /// <summary>메시그룹 이름 "@XXXXXXXX"(대문자 hex) 8글자를 제자리 덮어쓰기.</summary>
    private static void WriteNameHash(byte[] d, int p, uint hash)
    {
        string hex = hash.ToString("X8");
        for (int k = 0; k < 8; k++) d[p + 1 + k] = (byte)hex[k];
    }

    /// <summary>JointPalettes(0x10006): 팔레트별 엔트리 수.</summary>
    public static IReadOnlyList<int> ParsePaletteSizes(G1mContainer c)
    {
        var list = new List<int>();
        var s = c.FindSection(0x10006);
        if (s is null || s.Inner.Length < 4) return list;
        var d = s.Inner;
        int n = U(d, 0), p = 4;
        for (int i = 0; i < n && p + 4 <= d.Length; i++)
        {
            int cnt = U(d, p); p += 4 + cnt * 12;
            list.Add(cnt);
        }
        return list;
    }
}
