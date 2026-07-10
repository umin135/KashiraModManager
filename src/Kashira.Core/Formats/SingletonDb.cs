using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// 싱글톤 DB (DOK) 파서/직렬화기 + 레코드 편집 프리미티브.
/// 포맷(실측, tools/verify/ktmod_install.py 참조):
///   파일 = 헤더(0x1C) + [IDOK 레코드]* (각 4바이트 정렬)
///   헤더: @0x00 magic "_DOK0000", @0x08 헤더크기(0x1C), @0x0C 0x0E,
///         @0x10 레코드개수, @0x14 database hash, @0x18 total_size
///   IDOK: magic "IDOK0000"(8) + @+0x08 레코드크기(content, 미패딩) +
///         @+0x0C oid + @+0x10 type + @+0x14 prop_count +
///         prop_meta[pc×12: (type u32, array_count u32, name_hash u32)] + values
/// 오프셋/룩업 테이블 없음. 레코드는 oid 오름차순 전역정렬(엔진 이진탐색) → 삽입 시 정렬 위치 유지.
/// </summary>
public sealed class SingletonDb
{
    public const int HeaderSize = 0x1C;

    /// <summary>prop 타입별 단위 바이트. 인덱스=타입. 0=가변/미지원.</summary>
    private static readonly int[] Unit = { 1, 1, 2, 2, 4, 4, 0, 0, 4, 0, 16, 0, 8, 12 };

    public static int UnitSize(uint propType) => propType < (uint)Unit.Length ? Unit[(int)propType] : 0;

    /// <summary>DOK 헤더 원본 0x1C 바이트(직렬화 시 count/total_size만 갱신).</summary>
    private readonly byte[] _header;

    /// <summary>레코드 목록(항상 oid 오름차순 유지).</summary>
    public List<IdokRecord> Records { get; }

    private readonly Dictionary<uint, IdokRecord> _byOid;

    private SingletonDb(byte[] header, List<IdokRecord> records)
    {
        _header = header;
        Records = records;
        _byOid = records.ToDictionary(r => r.Oid);
    }

    public static SingletonDb Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data.Slice(0, 4).IndexOf("_DOK"u8) != 0)
            throw new InvalidDataException("Not a DOK (missing _DOK magic)");

        var header = data.Slice(0, HeaderSize).ToArray();
        var records = new List<IdokRecord>();
        int off = HeaderSize;
        while (off + 8 <= data.Length && data.Slice(off, 8).IndexOf("IDOK"u8) == 0)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off + 0x08));
            int padded = (size + 3) & ~3;
            records.Add(IdokRecord.Parse(data.Slice(off, padded)));
            off += padded;
        }
        return new SingletonDb(header, records);
    }

    public IdokRecord? Find(uint oid) => _byOid.TryGetValue(oid, out var r) ? r : null;

    public bool Contains(uint oid) => _byOid.ContainsKey(oid);

    /// <summary>새 레코드를 oid 오름차순 위치에 삽입(정렬 불변식 유지). oid 중복이면 예외.</summary>
    public void Insert(IdokRecord rec)
    {
        if (_byOid.ContainsKey(rec.Oid))
            throw new InvalidOperationException($"oid 0x{rec.Oid:X8} already exists");
        int i = 0;
        while (i < Records.Count && Records[i].Oid < rec.Oid) i++;
        Records.Insert(i, rec);
        _byOid[rec.Oid] = rec;
    }

    /// <summary>미사용 oid 배정(정렬 삽입용). seed 이상에서 빈 값 탐색.</summary>
    public uint AllocOid(uint seed = 0x0FB00000)
    {
        while (_byOid.ContainsKey(seed)) seed++;
        return seed;
    }

    /// <summary>DOK 바이트로 직렬화. 헤더 count(@0x10)/total_size(@0x18) 자동 갱신.</summary>
    public byte[] Serialize()
    {
        var body = new List<byte>();
        foreach (var r in Records) body.AddRange(r.Build());

        var outBuf = new byte[HeaderSize + body.Count];
        _header.CopyTo(outBuf, 0);
        body.CopyTo(outBuf, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(0x10), (uint)Records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(0x18), (uint)outBuf.Length);
        return outBuf;
    }
}

/// <summary>DOK 내 IDOK 레코드. 프로퍼티 목록으로 모델링(값은 raw 바이트 보관).</summary>
public sealed class IdokRecord
{
    public uint Oid { get; set; }
    public uint Type { get; }
    public List<IdokProp> Props { get; }

    public IdokRecord(uint oid, uint type, List<IdokProp> props)
    {
        Oid = oid;
        Type = type;
        Props = props;
    }

    public static IdokRecord Parse(ReadOnlySpan<byte> rec)
    {
        uint oid = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x0C));
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x10));
        int pc = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(0x14));
        int meta = 0x18;
        int vp = meta + pc * 12;
        var props = new List<IdokProp>(pc);
        for (int i = 0; i < pc; i++)
        {
            int mo = meta + i * 12;
            uint pt = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(mo));
            int ac = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(mo + 4));
            uint ph = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(mo + 8));
            int sz = SingletonDb.UnitSize(pt) * ac;
            props.Add(new IdokProp(pt, ph, ac, rec.Slice(vp, sz).ToArray()));
            vp += sz;
        }
        return new IdokRecord(oid, type, props);
    }

    /// <summary>새 oid 로 깊은 복사(모든 prop 값 복제). 새 레코드 생성(insert_record)용 템플릿 복제.</summary>
    public IdokRecord Clone(uint newOid) =>
        new(newOid, Type, Props.Select(p => new IdokProp(p.Type, p.NameHash, p.Count, (byte[])p.Value.Clone())).ToList());

    public IdokProp? Prop(uint nameHash) => Props.FirstOrDefault(p => p.NameHash == nameHash);

    /// <summary>단일 u32 prop 값 읽기(없으면 0).</summary>
    public uint ReadU32(uint nameHash) => Prop(nameHash) is { } p && p.Value.Length >= 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(p.Value) : 0u;

    /// <summary>u32 배열 prop 읽기.</summary>
    public uint[] ReadU32Array(uint nameHash)
    {
        var p = Prop(nameHash);
        if (p is null) return Array.Empty<uint>();
        var outArr = new uint[p.Value.Length / 4];
        for (int i = 0; i < outArr.Length; i++)
            outArr[i] = BinaryPrimitives.ReadUInt32LittleEndian(p.Value.AsSpan(i * 4));
        return outArr;
    }

    /// <summary>단일 u32 prop 값을 in-place 교체(크기 불변). prop 없거나 값이 4바이트 미만이면 false.</summary>
    public bool SetU32(uint nameHash, uint value)
    {
        var p = Prop(nameHash);
        if (p is null || p.Value.Length < 4) return false;
        BinaryPrimitives.WriteUInt32LittleEndian(p.Value.AsSpan(0), value);
        return true;
    }

    /// <summary>u32 배열 prop 교체(크기 변경 허용 — array_count 자동 갱신).</summary>
    public void SetU32Array(uint nameHash, ReadOnlySpan<uint> values)
    {
        var p = Prop(nameHash) ?? throw new InvalidOperationException($"prop 0x{nameHash:X8} 없음");
        var buf = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4), values[i]);
        p.Count = values.Length;
        p.Value = buf;
    }

    /// <summary>완성된 IDOK 레코드 바이트(size@+0x08 계산 + 4바이트 정렬 패딩).</summary>
    public byte[] Build()
    {
        int pc = Props.Count;
        int valBytes = Props.Sum(p => p.Value.Length);
        int content = 0x18 + pc * 12 + valBytes;
        int padded = (content + 3) & ~3;

        var rec = new byte[padded];
        "IDOK"u8.CopyTo(rec);
        "0000"u8.CopyTo(rec.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x08), (uint)content);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x0C), Oid);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x10), Type);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x14), (uint)pc);

        int meta = 0x18;
        int vp = meta + pc * 12;
        for (int i = 0; i < pc; i++)
        {
            int mo = meta + i * 12;
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(mo), Props[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(mo + 4), (uint)Props[i].Count);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(mo + 8), Props[i].NameHash);
            Props[i].Value.CopyTo(rec.AsSpan(vp));
            vp += Props[i].Value.Length;
        }
        return rec;
    }
}

/// <summary>IDOK 프로퍼티. Type=prop 타입(단위크기 결정), NameHash=식별자, Value=raw 값 바이트.</summary>
public sealed class IdokProp
{
    public uint Type { get; }
    public uint NameHash { get; }
    public int Count { get; set; }
    public byte[] Value { get; set; }

    public IdokProp(uint type, uint nameHash, int count, byte[] value)
    {
        Type = type;
        NameHash = nameHash;
        Count = count;
        Value = value;
    }
}
