using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// .mtl (material bind table, TypeKtid 0xB340861A) 파서. 실측 구조(메모리 ktmod-content-symbolic-design):
///   헤더(0x10) = num_names, num_mat(=g1m 재질팔레트 크기), num_cloths, num_ponytails
///   Names 섹션 = (name_hash u32, count u32, mat_ids u32[count]) × num_names
///   이후 Cloth/Ponytail 섹션(각 (src u32, dst u32)).
/// 저작 코스튬 설치 시 재질 이름 해시(nameArr/MRNH)의 출처.
/// </summary>
public sealed class MtlFile
{
    public int NumNames { get; }
    public int NumMat { get; }
    public int NumCloths { get; }
    public int NumPonytails { get; }

    /// <summary>재질 이름별 (name_hash, 커버하는 g1m 재질 인덱스들). Names 순서 유지.</summary>
    public IReadOnlyList<(uint NameHash, int[] MatIds)> Names { get; }

    private MtlFile(int nn, int nm, int nc, int np, List<(uint, int[])> names)
    {
        NumNames = nn; NumMat = nm; NumCloths = nc; NumPonytails = np; Names = names;
    }

    public static MtlFile Parse(ReadOnlySpan<byte> d)
    {
        int nn = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
        int nm = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4));
        int nc = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(8));
        int np = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(0x0C));

        var names = new List<(uint, int[])>(nn);
        int off = 0x10;
        for (int i = 0; i < nn; i++)
        {
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(off));
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(off + 4));
            off += 8;
            var ids = new int[count];
            for (int j = 0; j < count; j++)
                ids[j] = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(off + j * 4));
            off += count * 4;
            names.Add((hash, ids));
        }
        return new MtlFile(nn, nm, nc, np, names);
    }

    /// <summary>Names 순서의 name_hash 배열(nameArr 용).</summary>
    public uint[] NameHashes() => Names.Select(n => n.NameHash).ToArray();
}
