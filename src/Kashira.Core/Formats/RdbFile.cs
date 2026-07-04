using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>RDB 엔트리(에셋 1개의 인덱스). 위치 메타는 Location32(data_size 0x0D) 기준.</summary>
public sealed class RdbEntry
{
    public int Index { get; init; }
    public int Pos { get; init; }
    public long EntrySize { get; init; }
    public long DataSize { get; init; }
    public long FileSize { get; set; }
    public uint FileKtid { get; init; }
    public uint TypeInfoKtid { get; init; }
    public uint Flags { get; init; }
    public int FdataId { get; set; }
    public long FdataOffset { get; set; }
    public long SizeInCont { get; set; }
    public int MetaStart { get; init; }
}

/// <summary>
/// root.rdb / system.rdb 파서 + 리다이렉트 패처. 원본 불변 — 내부 버퍼(Data) 를 수정 후 저장한다.
/// 참조: 02_rdb_fdata_format.md, tools/verify/katana_rdb.py + patch.py
/// </summary>
public sealed class RdbFile
{
    public byte[] Data { get; private set; }
    public int HeaderSize { get; }
    public uint FileCount { get; private set; }
    public IReadOnlyList<RdbEntry> Entries { get; }

    private readonly Dictionary<uint, RdbEntry> _byKtid;

    private RdbFile(byte[] data, int headerSize, uint fileCount, List<RdbEntry> entries)
    {
        Data = data;
        HeaderSize = headerSize;
        FileCount = fileCount;
        Entries = entries;
        _byKtid = new Dictionary<uint, RdbEntry>();
        foreach (var e in entries) _byKtid[e.FileKtid] = e; // 마지막 우선(중복 시)
    }

    public static RdbFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static RdbFile Parse(byte[] data)
    {
        if (data.Length < 0x20 || data[0] != (byte)'_' || data[1] != (byte)'D')
            throw new InvalidDataException("RDB magic mismatch (_DRK)");

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x08));
        uint fileCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x10));

        var entries = new List<RdbEntry>();
        int pos = headerSize, idx = 0;
        while (pos + 0x30 <= data.Length)
        {
            if (data.AsSpan(pos, 4).IndexOf("IDRK"u8) != 0) break;

            long entrySize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 0x08));
            long dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 0x10));
            long fileSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 0x18));
            uint fileKtid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 0x24));
            uint typeInfo = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 0x28));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 0x2C));

            int metaStart = pos + (int)entrySize - (int)dataSize;
            long fOff = -1, sCont = -1; int fId = -1;
            if (dataSize == 0x0D)
            {
                fOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(metaStart + 0x02));
                sCont = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(metaStart + 0x06));
                fId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(metaStart + 0x0A));
            }
            else if (dataSize == 0x11)
            {
                long hi = data[metaStart + 0x02];
                long lo = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(metaStart + 0x06));
                fOff = (hi << 32) | lo;
                sCont = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(metaStart + 0x0A));
                fId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(metaStart + 0x0E));
            }

            entries.Add(new RdbEntry
            {
                Index = idx++, Pos = pos, EntrySize = entrySize, DataSize = dataSize,
                FileSize = fileSize, FileKtid = fileKtid, TypeInfoKtid = typeInfo, Flags = flags,
                FdataId = fId, FdataOffset = fOff, SizeInCont = sCont, MetaStart = metaStart,
            });

            pos += (int)((entrySize + 3) & ~3L); // 4바이트 정렬 stride
        }
        return new RdbFile(data, headerSize, fileCount, entries);
    }

    public RdbEntry? Find(uint fileKtid) => _byKtid.GetValueOrDefault(fileKtid);

    /// <summary>엔트리의 위치 메타 + file_size 를 새 값으로 덮어쓴다(Location32 전용). entry_size 불변.</summary>
    public void Redirect(RdbEntry e, int fdataId, long offset, long sizeInCont, long fileSize)
    {
        if (e.DataSize != 0x0D)
            throw new NotSupportedException($"data_size 0x{e.DataSize:x} (Location40) 미지원");

        BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(e.MetaStart + 0x02), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(e.MetaStart + 0x06), (uint)sizeInCont);
        BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(e.MetaStart + 0x0A), (ushort)fdataId);
        BinaryPrimitives.WriteUInt64LittleEndian(Data.AsSpan(e.Pos + 0x18), (ulong)fileSize);

        e.FdataId = fdataId; e.FdataOffset = offset; e.SizeInCont = sizeInCont; e.FileSize = fileSize;
    }

    public void Save(string path) => File.WriteAllBytes(path, Data);
}
