using System.Buffers.Binary;
using System.Linq;

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

    /// <summary>주어진 TypeKtid 를 가진 첫 엔트리(새 엔트리 복제용 템플릿).</summary>
    public RdbEntry? FindByType(uint typeKtid) => Entries.FirstOrDefault(e => e.TypeInfoKtid == typeKtid);

    /// <summary>
    /// template 엔트리를 복제해 새 file_ktid 엔트리 바이트(4바이트 정렬 stride 포함)를 만든다.
    /// entry_type/flags/타입/프로퍼티블록은 같은 타입 원본에서 복사, file_ktid·file_size·위치만 교체.
    /// (Location32 전용) 삽입은 <see cref="InsertEntriesSorted"/> 로 별도 수행.
    /// </summary>
    public byte[] BuildClonedEntry(RdbEntry template, uint fileKtid, int fdataId,
                                   long offset, long sizeInCont, long fileSize)
    {
        if (template.DataSize != 0x0D)
            throw new NotSupportedException($"template data_size 0x{template.DataSize:x} 미지원");

        int stride = (int)((template.EntrySize + 3) & ~3L);
        var blob = new byte[stride];
        Array.Copy(Data, template.Pos, blob, 0, stride);

        int metaStart = (int)template.EntrySize - (int)template.DataSize; // blob 내 상대 오프셋
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x24), fileKtid);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0x18), (ulong)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(metaStart + 0x02), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(metaStart + 0x06), (uint)sizeInCont);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(metaStart + 0x0A), (ushort)fdataId);
        return blob;
    }

    /// <summary>
    /// 새 엔트리들을 file_ktid 오름차순 정렬 위치에 삽입해 Data 를 재구성한다.
    /// RDB 는 file_ktid 로 정렬돼 있으므로(엔진 이진탐색 대비) 끝에 append 하지 않는다.
    /// 리다이렉트(기존 엔트리 in-place 수정)를 모두 마친 뒤 호출할 것 — 그 변경도 함께 보존된다.
    /// 헤더 file_count 를 삽입 개수만큼 증가. 호출 후 Entries/Find 의 Pos 는 무효(사용 금지).
    /// </summary>
    public void InsertEntriesSorted(IReadOnlyList<(uint Ktid, byte[] Blob)> newEntries)
    {
        if (newEntries.Count == 0) return;

        var all = new List<(uint Ktid, byte[] Arr, int Off, int Len)>(Entries.Count + newEntries.Count);
        foreach (var e in Entries)
        {
            int stride = (int)((e.EntrySize + 3) & ~3L);
            all.Add((e.FileKtid, Data, e.Pos, stride)); // 기존(리다이렉트 반영된) 바이트
        }
        foreach (var (ktid, blob) in newEntries)
            all.Add((ktid, blob, 0, blob.Length));

        var ordered = all.OrderBy(x => x.Ktid).ToList(); // 안정 정렬 → 기존은 순서 유지

        using var ms = new MemoryStream(Data.Length + newEntries.Sum(n => n.Blob.Length));
        ms.Write(Data, 0, HeaderSize);
        foreach (var (_, arr, off, len) in ordered) ms.Write(arr, off, len);
        Data = ms.ToArray();

        FileCount += (uint)newEntries.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(0x10), FileCount);
    }

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

    /// <summary>엔트리의 CompressionType(flags bit 20-25)을 None 으로 — 무압축 저장 블록과 맞춘다.</summary>
    public void SetUncompressed(RdbEntry e)
    {
        uint flags = e.Flags & ~(0x3Fu << 20);
        BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(e.Pos + 0x2C), flags);
    }

    public void Save(string path) => File.WriteAllBytes(path, Data);
}
