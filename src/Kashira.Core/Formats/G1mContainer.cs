using System.Buffers.Binary;
using System.Linq;

namespace Kashira.Core.Formats;

/// <summary>
/// G1M 컨테이너 언팩/리팩(byte-exact 왕복). tools/verify/g1m_split.py 포트. g1m 편집(Phase G1)의 토대.
/// 구조(실측): 최상위 헤더 0x18(magic "_M1G"=0x47314D5F / version / file_size@0x08 / header_size@0x0C / unk / num_chunks@0x14)
///   + 청크 N개 `[sig u32][ver 4B][size u32][data…]`.
///   G1MG 청크: 공통 0x0C + 필드 0x24(num_sections@+0x2C) + 섹션 `[id u32][size u32][data…]`.
/// Build 는 모든 size/count 를 구조에서 재계산 → 무편집이면 원본과 바이트 일치(정확성 기준).
/// 섹션 Inner 는 raw 바이트 보존(타입별 뷰는 상위 계층/G1mFile).
/// </summary>
public sealed class G1mContainer
{
    public const uint Magic = 0x47314D5F;   // 디스크 바이트 5F 4D 31 47 ("_M1G")
    public const uint G1mgSig = 0x47314D47; // "G1MG"
    public const uint G1msSig = 0x47314D53; // "G1MS"
    public const uint G1mfSig = 0x47314D46; // "G1MF"
    public const uint G1mmSig = 0x47314D4D; // "G1MM"

    public sealed class Section
    {
        public uint Id;
        public byte[] Inner = Array.Empty<byte>();
    }

    public sealed class Chunk
    {
        public uint Sig;
        public byte[] Ver = new byte[4];
        public byte[] Inner = Array.Empty<byte>();  // 비-G1MG: 공통헤더(0x0C) 이후 raw
        public byte[]? G1mgMid;                     // G1MG: 필드 0x24 바이트
        public List<Section>? Sections;             // G1MG: 서브섹션
        public bool IsG1mg => Sections is not null;
    }

    public byte[] Top { get; set; } = new byte[0x18];
    public List<Chunk> Chunks { get; } = new();

    /// <summary>첫 G1MG 청크.</summary>
    public Chunk? G1mg => Chunks.FirstOrDefault(c => c.IsG1mg);

    /// <summary>G1MG 서브섹션(id, 예 0x10002/0x10003) 조회.</summary>
    public Section? FindSection(uint id) => G1mg?.Sections?.FirstOrDefault(s => s.Id == id);

    /// <summary>최상위 청크(sig) 조회(예 G1MF/G1MS).</summary>
    public Chunk? FindChunk(uint sig) => Chunks.FirstOrDefault(c => c.Sig == sig);

    public static G1mContainer Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 0x18 || BinaryPrimitives.ReadUInt32LittleEndian(d) != Magic)
            throw new InvalidDataException("g1m: 매직 불일치(_M1G)");

        var c = new G1mContainer { Top = d[..0x18].ToArray() };
        int numChunks = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[0x14..]);
        int pos = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[0x0C..]); // header_size

        for (int i = 0; i < numChunks; i++)
        {
            if (pos + 0x0C > d.Length) throw new InvalidDataException($"g1m: 청크 {i} 헤더 범위 초과");
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 8)..]);
            if (size < 0x0C || pos + size > d.Length) throw new InvalidDataException($"g1m: 청크 {i} 크기 오류");
            var cb = d.Slice(pos, size);

            var chunk = new Chunk { Sig = sig, Ver = cb.Slice(4, 4).ToArray() };
            if (sig == G1mgSig)
            {
                chunk.G1mgMid = cb.Slice(0x0C, 0x24).ToArray();
                int ns = (int)BinaryPrimitives.ReadUInt32LittleEndian(cb[0x2C..]);
                chunk.Sections = new List<Section>(ns);
                int sp = 0x30;
                for (int s = 0; s < ns; s++)
                {
                    if (sp + 8 > cb.Length) throw new InvalidDataException($"g1m: G1MG 섹션 {s} 헤더 범위 초과");
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(cb[sp..]);
                    int ssz = (int)BinaryPrimitives.ReadUInt32LittleEndian(cb[(sp + 4)..]);
                    if (ssz < 8 || sp + ssz > cb.Length) throw new InvalidDataException($"g1m: G1MG 섹션 {s} 크기 오류");
                    chunk.Sections.Add(new Section { Id = id, Inner = cb.Slice(sp + 8, ssz - 8).ToArray() });
                    sp += ssz;
                }
            }
            else
            {
                chunk.Inner = cb.Slice(0x0C, size - 0x0C).ToArray();
            }
            c.Chunks.Add(chunk);
            pos += size;
        }
        return c;
    }

    public byte[] Build()
    {
        var chunkBlobs = Chunks.Select(BuildChunk).ToList();
        int total = 0x18 + chunkBlobs.Sum(b => b.Length);

        var buf = new byte[total];
        Top.CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x08), (uint)total);          // file_size
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x14), (uint)Chunks.Count);   // num_chunks
        // header_size@0x0C 는 Top 에서 유지(보통 0x18).
        int p = 0x18;
        foreach (var b in chunkBlobs) { b.CopyTo(buf, p); p += b.Length; }
        return buf;
    }

    private static byte[] BuildChunk(Chunk c)
    {
        if (c.IsG1mg)
        {
            var sections = c.Sections!;
            int bodyLen = sections.Sum(s => 8 + s.Inner.Length);
            var mid = (byte[])c.G1mgMid!.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(mid.AsSpan(0x20), (uint)sections.Count); // num_sections(로컬 0x2C-0x0C)
            int size = 0x0C + mid.Length + bodyLen;
            var buf = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, c.Sig);
            c.Ver.CopyTo(buf, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), (uint)size);
            mid.CopyTo(buf, 0x0C);
            int sp = 0x0C + mid.Length;
            foreach (var s in sections)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sp), s.Id);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sp + 4), (uint)(8 + s.Inner.Length));
                s.Inner.CopyTo(buf, sp + 8);
                sp += 8 + s.Inner.Length;
            }
            return buf;
        }
        else
        {
            int size = 0x0C + c.Inner.Length;
            var buf = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, c.Sig);
            c.Ver.CopyTo(buf, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), (uint)size);
            c.Inner.CopyTo(buf, 0x0C);
            return buf;
        }
    }

    /// <summary>청크 sig → 이름(로그/UI). G1MG 서브섹션 id 는 <see cref="SectionName"/>.</summary>
    public static string SigName(uint sig) => sig switch
    {
        G1mgSig => "G1MG", G1msSig => "G1MS", G1mfSig => "G1MF", G1mmSig => "G1MM",
        0x4E554E4F => "NUNO", 0x4E554E56 => "NUNV", 0x4E554E53 => "NUNS", 0x534F4654 => "SOFT",
        0x434F4C4C => "COLL", 0x45585452 => "EXTR", 0x48414952 => "HAIR",
        0x4732415F => "G2A", 0x4731415F => "G1A",
        _ => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(sig)),
    };

    public static string SectionName(uint id) => id switch
    {
        0x10001 => "Section1", 0x10002 => "Materials", 0x10003 => "PropertySet",
        0x10004 => "VertexBuffer", 0x10005 => "VtxAttributes", 0x10006 => "JointPalettes",
        0x10007 => "IndexBuffer", 0x10008 => "Submesh", 0x10009 => "MeshGroups",
        _ => $"0x{id:X5}",
    };
}
