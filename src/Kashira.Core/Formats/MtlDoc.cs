using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kashira.Core.Formats;

/// <summary>
/// mtl 의 사람이 읽고 편집하는 JSON 표현(JSON-first). 스키마는 _docs/_singletonDB/Format/mtl_convert.py 와 동일:
///   { "num_mat", "names":[{"hash":"0x..","mat_ids":[..]}], "cloths":[{"src","dst"}], "ponytails":[..] }
/// 프로젝트엔 &lt;set&gt;.mtl.json 으로 두고, Manager 가 설치 시에만 raw mtl 로 변환한다.
/// </summary>
public sealed class MtlDoc
{
    [JsonPropertyName("num_mat")] public int NumMat { get; set; }
    [JsonPropertyName("names")] public List<NameEntry> Names { get; set; } = new();
    [JsonPropertyName("cloths")] public List<Pair> Cloths { get; set; } = new();
    [JsonPropertyName("ponytails")] public List<Pair> Ponytails { get; set; } = new();

    public sealed class NameEntry
    {
        [JsonPropertyName("hash")] public string Hash { get; set; } = "0x00000000";
        [JsonPropertyName("mat_ids")] public List<int> MatIds { get; set; } = new();
    }

    public sealed class Pair
    {
        [JsonPropertyName("src")] public uint Src { get; set; }
        [JsonPropertyName("dst")] public uint Dst { get; set; }
    }

    /// <summary>g1m 재질개수로 베이스라인 생성(1:1, 합성 name_hash=i+1).</summary>
    public static MtlDoc Baseline(int numMat)
    {
        var doc = new MtlDoc { NumMat = numMat };
        for (int i = 0; i < numMat; i++)
            doc.Names.Add(new NameEntry { Hash = Hex((uint)(i + 1)), MatIds = { i } });
        return doc;
    }

    /// <summary>기존 raw mtl → 편집 문서(임포트용).</summary>
    public static MtlDoc FromBinary(ReadOnlySpan<byte> raw)
    {
        var m = MtlFile.Parse(raw);
        var doc = new MtlDoc { NumMat = m.NumMat };
        foreach (var (hash, mats) in m.Names)
            doc.Names.Add(new NameEntry { Hash = Hex(hash), MatIds = mats.ToList() });
        return doc;
    }

    /// <summary>raw mtl 바이너리로 직렬화(설치 시 Manager 가 호출).</summary>
    public byte[] ToBinary()
    {
        int size = 0x10 + Names.Sum(n => 8 + n.MatIds.Count * 4) + (Cloths.Count + Ponytails.Count) * 8;
        var buf = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x00), (uint)Names.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x04), (uint)NumMat);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x08), (uint)Cloths.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x0C), (uint)Ponytails.Count);
        int off = 0x10;
        foreach (var n in Names)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), ParseHash(n.Hash));
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 4), (uint)n.MatIds.Count);
            off += 8;
            foreach (var mid in n.MatIds) { BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), (uint)mid); off += 4; }
        }
        foreach (var c in Cloths) WritePair(buf, ref off, c);
        foreach (var p in Ponytails) WritePair(buf, ref off, p);
        return buf;
    }

    private static void WritePair(byte[] buf, ref int off, Pair p)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), p.Src);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 4), p.Dst);
        off += 8;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static MtlDoc FromJson(string json) =>
        JsonSerializer.Deserialize<MtlDoc>(json) ?? throw new InvalidDataException("mtl.json 파싱 실패");

    internal static string Hex(uint v) => "0x" + v.ToString("X8");
    internal static uint ParseHash(string s) =>
        Convert.ToUInt32(s.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Trim()[2..] : s.Trim(), 16);
}
