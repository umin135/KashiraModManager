using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// root.rdx / system.rdx — 헤더 없음, 8바이트 엔트리 배열(fdata_id u16, padding 0xFFFF, file_hash u32).
/// 참조: 02_rdb_fdata_format.md §2, tools/verify/katana_rdb.py
/// </summary>
public sealed class RdxFile
{
    public byte[] Data { get; private set; }

    /// <summary>fdata_id → file_hash.</summary>
    public IReadOnlyDictionary<int, uint> Map { get; }

    private RdxFile(byte[] data, Dictionary<int, uint> map)
    {
        Data = data;
        Map = map;
    }

    public static RdxFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static RdxFile Parse(byte[] data)
    {
        var map = new Dictionary<int, uint>();
        for (int off = 0; off + 8 <= data.Length; off += 8)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off));
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            map[id] = hash;
        }
        return new RdxFile(data, map);
    }

    public int MaxId => Map.Count == 0 ? -1 : Map.Keys.Max();

    /// <summary>미사용 fdata_id (최대+1).</summary>
    public int NextFreeId => MaxId + 1;

    public static string FdataName(uint fileHash) => $"0x{fileHash:x8}.fdata";

    /// <summary>새 (id, hash) 엔트리를 배열 끝에 추가한 새 RdxFile 반환.</summary>
    public RdxFile WithEntry(int id, uint fileHash)
    {
        var buf = new byte[Data.Length + 8];
        Data.CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(Data.Length), (ushort)id);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(Data.Length + 2), 0xFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(Data.Length + 4), fileHash);

        var map = new Dictionary<int, uint>(Map) { [id] = fileHash };
        return new RdxFile(buf, map);
    }

    public void Save(string path) => File.WriteAllBytes(path, Data);
}
