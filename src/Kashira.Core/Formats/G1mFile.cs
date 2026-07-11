using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// g1m 최소 리더. 구조(tools/verify/g1m_split.py):
///   최상위 헤더 0x18: magic "_M1G", version, file_size@0x08, header_size@0x0C(=0x18), unk, num_chunks@0x14
///   청크: [sig u32][ver 4B][size u32][data…] (size 로 다음 청크 이동)
///   G1MG(0x47314D47): 공통 0x0C + mid 0x24(num_sections@0x2C) + 섹션들[id u32, size u32(헤더포함), inner]
///   섹션 0x10002 = 재질 개수. 섹션 0x10009 = MeshGroups(파츠 슬라이싱 근거).
/// </summary>
public static class G1mFile
{
    private const uint G1MG = 0x47314D47;
    private const uint Sec_MaterialCount = 0x10002;
    private const uint Sec_MeshGroups = 0x10009;

    /// <summary>g1m 의 재질 팔레트 크기(mtl num_mat).</summary>
    public static int MaterialCount(ReadOnlySpan<byte> g1m)
        => (int)BinaryPrimitives.ReadUInt32LittleEndian(Section(g1m, Sec_MaterialCount));

    /// <summary>
    /// g1m 재질의 텍스처 슬롯 하나(0x10002 G1MGTexture, 12B). 재질 텍스처 구조의 원천.
    /// BaseKtidSlot=tex_id(base ktid 전역 슬롯), Primary=KTS 카테고리(tex_type2), Physics=KTS physics(unk_06).
    /// 슬롯 순서 = KTS/MPR ktid 슬롯 순서. (eternity_common/DOA6/G1mFile.h G1MGTexture 교차검증)
    /// </summary>
    public readonly record struct TexSlot(int BaseKtidSlot, int Primary, int Physics);

    /// <summary>
    /// g1m 0x10002 재질별 텍스처 슬롯 목록. KTS(primary/physics)·base ktid 슬롯 매핑·MPR 슬롯 순서의 단일 원천.
    /// 재질 i 의 슬롯 j = KTS slot j = MPR ktid slot j. BaseKtidSlot 은 base ktid 의 전역 슬롯.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TexSlot>> Materials(ReadOnlySpan<byte> g1m)
    {
        var sec = Section(g1m, Sec_MaterialCount);
        int matCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(sec);
        int p = 4;
        var mats = new List<IReadOnlyList<TexSlot>>(matCount);
        for (int m = 0; m < matCount; m++)
        {
            if (p + 16 > sec.Length) break;
            int numTex = (int)BinaryPrimitives.ReadUInt32LittleEndian(sec.Slice(p + 4));
            p += 16;
            var slots = new List<TexSlot>(numTex);
            for (int i = 0; i < numTex && p + 12 <= sec.Length; i++)
            {
                int texId = BinaryPrimitives.ReadUInt16LittleEndian(sec.Slice(p));       // tex_id
                int primary = BinaryPrimitives.ReadUInt16LittleEndian(sec.Slice(p + 4)); // tex_type2 = KTS primary
                int physics = BinaryPrimitives.ReadUInt16LittleEndian(sec.Slice(p + 6)); // unk_06   = KTS physics
                slots.Add(new TexSlot(texId, primary, physics));
                p += 12;
            }
            mats.Add(slots);
        }
        return mats;
    }

    /// <summary>grp 단일 파츠 생성용 슬라이싱 정보. Sm1/Sm2=mesh entry 수, CountT/CountBigT=idx_count 합.</summary>
    public readonly record struct Slicing(int Sm1, int Sm2, int CountT, int CountBigT);

    /// <summary>
    /// MeshGroups(0x10009)에서 t/T 블록의 mesh entry 수(sm1/sm2)와 각 블록 idx_count 합을 계산.
    /// grp 단일 파츠: set_count_t=Sm1, set_count_T=Sm2, count_t=CountT, count_T=CountBigT.
    /// </summary>
    public static Slicing MeshGroupSlicing(ReadOnlySpan<byte> g1m)
    {
        var mg = Section(g1m, Sec_MeshGroups);
        // 실측(002): sm1(t-block entry수)@0x10, sm2(T-block)@0x14
        int sm1 = (int)BinaryPrimitives.ReadUInt32LittleEndian(mg.Slice(0x10));
        int sm2 = (int)BinaryPrimitives.ReadUInt32LittleEndian(mg.Slice(0x14));

        // 첫 mesh entry(이름 '@'=0x40, 4정렬)까지 스캔
        int p = 0x20;
        while (p < mg.Length && !(mg[p] == 0x40 && p % 4 == 0)) p += 4;

        int countT = 0, countBigT = 0;
        for (int i = 0; i < sm1 + sm2; i++)
        {
            if (p + 0x1C > mg.Length) break;
            int idxc = (int)BinaryPrimitives.ReadUInt32LittleEndian(mg.Slice(p + 0x18));
            if (i < sm1) countT += idxc; else countBigT += idxc;
            p += 0x1C + idxc * 4;
        }
        return new Slicing(sm1, sm2, countT, countBigT);
    }

    /// <summary>G1MG 청크 안 지정 섹션의 inner(id/size 헤더 이후) 스팬. 없으면 예외.</summary>
    private static ReadOnlySpan<byte> Section(ReadOnlySpan<byte> g1m, uint sectionId)
    {
        if (g1m.Length < 0x18 || g1m[0] != (byte)'_' || g1m[1] != (byte)'M' || g1m[2] != (byte)'1' || g1m[3] != (byte)'G')
            throw new InvalidDataException("g1m magic(_M1G) 불일치");

        int numChunks = (int)BinaryPrimitives.ReadUInt32LittleEndian(g1m.Slice(0x14));
        int pos = (int)BinaryPrimitives.ReadUInt32LittleEndian(g1m.Slice(0x0C));

        for (int c = 0; c < numChunks && pos + 0x0C <= g1m.Length; c++)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(g1m.Slice(pos));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(g1m.Slice(pos + 8));
            if (size < 0x0C || pos + size > g1m.Length) break;

            if (sig == G1MG)
            {
                var chunk = g1m.Slice(pos, size);
                int ns = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(0x2C));
                int sp = 0x30;
                for (int s = 0; s < ns && sp + 8 <= chunk.Length; s++)
                {
                    uint sid = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(sp));
                    int ssz = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(sp + 4));
                    if (ssz < 8 || sp + ssz > chunk.Length) break;
                    if (sid == sectionId) return chunk.Slice(sp + 8, ssz - 8);
                    sp += ssz;
                }
                throw new InvalidDataException($"g1m G1MG 에 섹션 0x{sectionId:X} 없음");
            }
            pos += size;
        }
        throw new InvalidDataException("g1m G1MG 청크 없음");
    }
}
