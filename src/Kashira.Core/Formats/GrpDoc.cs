using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kashira.Core.Formats;

/// <summary>
/// grp 의 사람이 읽고 편집하는 JSON 표현(JSON-first). 스키마는 _docs/_singletonDB/Format/grp_convert.py 와 동일.
/// grp = 헤더 없는 GRPEntry(0x20B) 배열. 각 8×u32. 프로젝트엔 &lt;set&gt;.grp.json 으로 두고 설치 시 raw 로 변환.
/// parts_name_hash 는 기존 유효 hash 재사용 필수(임의값 불가) — 모더 grp 를 디코드해 보존한다.
/// </summary>
public sealed class GrpDoc
{
    [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = new();

    public sealed class Entry
    {
        [JsonPropertyName("parts_name_hash")] public string PartsNameHash { get; set; } = "0x00000000";
        [JsonPropertyName("parts_id")] public uint PartsId { get; set; }
        [JsonPropertyName("count_t")] public uint CountT { get; set; }
        [JsonPropertyName("count_T")] public uint CountBigT { get; set; }
        [JsonPropertyName("count_s")] public uint CountS { get; set; }
        [JsonPropertyName("set_count_t")] public uint SetCountT { get; set; }
        [JsonPropertyName("set_count_T")] public uint SetCountBigT { get; set; }
        [JsonPropertyName("set_count_s")] public uint SetCountS { get; set; }
    }

    /// <summary>
    /// g1m 슬라이싱으로 단일 파츠 grp 생성(모더가 g1m 만 준비 시). parts_name_hash 는 기본값(0x3057221F 등).
    /// set_count = mesh entry 수(sm1/sm2), count = idx_count 합. s-블록은 DOA6 항상 0.
    /// </summary>
    public static GrpDoc SinglePart(G1mFile.Slicing s, uint partsNameHash)
    {
        var doc = new GrpDoc();
        doc.Entries.Add(new Entry
        {
            PartsNameHash = MtlDoc.Hex(partsNameHash),
            PartsId = 0,
            CountT = (uint)s.CountT,
            CountBigT = (uint)s.CountBigT,
            CountS = 0,
            SetCountT = (uint)s.Sm1,
            SetCountBigT = (uint)s.Sm2,
            SetCountS = 0,
        });
        return doc;
    }

    /// <summary>raw grp → 편집 문서(모더가 가져온 grp 임포트용).</summary>
    public static GrpDoc FromBinary(ReadOnlySpan<byte> raw)
    {
        if (raw.Length % 0x20 != 0) throw new InvalidDataException($"grp 크기가 0x20 배수 아님: {raw.Length}B");
        var doc = new GrpDoc();
        for (int i = 0; i < raw.Length / 0x20; i++)
        {
            int o = i * 0x20;
            doc.Entries.Add(new Entry
            {
                PartsNameHash = MtlDoc.Hex(BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o))),
                PartsId = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o + 0x04)),
                CountT = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o + 0x08)),
                CountBigT = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o + 0x0C)),
                CountS = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o + 0x10)),
                SetCountT = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o + 0x14)),
                SetCountBigT = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o + 0x18)),
                SetCountS = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(o + 0x1C)),
            });
        }
        return doc;
    }

    /// <summary>raw grp 바이너리로 직렬화(설치 시 Manager 가 호출).</summary>
    public byte[] ToBinary()
    {
        var buf = new byte[Entries.Count * 0x20];
        for (int i = 0; i < Entries.Count; i++)
        {
            int o = i * 0x20; var e = Entries[i];
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), MtlDoc.ParseHash(e.PartsNameHash));
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0x04), e.PartsId);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0x08), e.CountT);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0x0C), e.CountBigT);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0x10), e.CountS);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0x14), e.SetCountT);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0x18), e.SetCountBigT);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 0x1C), e.SetCountS);
        }
        return buf;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static GrpDoc FromJson(string json) =>
        JsonSerializer.Deserialize<GrpDoc>(json) ?? throw new InvalidDataException("grp.json 파싱 실패");
}
