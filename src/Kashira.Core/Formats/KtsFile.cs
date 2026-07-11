using System.Buffers.Binary;
using System.Text.Json;

namespace Kashira.Core.Formats;

/// <summary>
/// KTS("GSTK") = 텍스처 슬롯 → 셰이더 카테고리 스키마. 슬롯 j = KTS/MPR ktid/g1m 텍스처엔트리 j (동일 인덱싱).
/// 포맷(_docs/_singletonDB/Format/kts_convert.py, eternity_common/DOA6):
///   헤더 0x10: "GSTK" + 0x00000000 + entry_count u16@0x08 + 0x0001 + 0x00000001
///   엔트리 12B × count: (index u32, primary u16, physics u16, 0x0004, 0x0004)
/// primary = 셰이더 텍스처 카테고리(1=alb,2=rfr,3=nmh,5=occ,41=air,47=wtm,37=shl,62=s4m,55=occ2…).
/// **KTS 는 g1m 0x10002 재질 텍스처 엔트리에서 그대로 유도된다**(primary=tex_type2, physics=unk_06, 순서 동일).
/// </summary>
public static class KtsFile
{
    private static ReadOnlySpan<byte> Magic => "GSTK"u8;

    /// <summary>KTS 슬롯 하나: primary(셰이더 카테고리), physics(물리 카테고리, 0=미사용).</summary>
    public readonly record struct Slot(int Primary, int Physics);

    /// <summary>KTS(GSTK) 바이트 → 슬롯 목록.</summary>
    public static IReadOnlyList<Slot> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x10 || !data.Slice(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("KTS magic(GSTK) 불일치");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x08));
        var slots = new List<Slot>(count);
        for (int i = 0; i < count && 0x10 + i * 12 + 8 <= data.Length; i++)
        {
            int off = 0x10 + i * 12;
            slots.Add(new Slot(
                BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(off + 4)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(off + 6))));
        }
        return slots;
    }

    /// <summary>슬롯 목록 → KTS(GSTK) 바이트.</summary>
    public static byte[] Build(IReadOnlyList<Slot> slots)
    {
        var buf = new byte[0x10 + slots.Count * 12];
        Magic.CopyTo(buf);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x08), (ushort)slots.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x0A), 0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x0C), 0x00000001);
        for (int i = 0; i < slots.Count; i++)
        {
            int off = 0x10 + i * 12;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), (uint)i);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 4), (ushort)slots[i].Primary);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 6), (ushort)slots[i].Physics);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 8), 0x0004);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 0x0A), 0x0004);
        }
        return buf;
    }

    /// <summary>
    /// 카테고리(primary) → physics 카테고리 레지스트리(실측 KTS, g1m_format.md §8.3).
    /// g1m 의 unk_06 은 일부 슬롯(예 occ)에서 실제 KTS physics 와 어긋나므로 이 표로 유도한다.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> PhysicsByCategory = new Dictionary<int, int>
    {
        [1] = 1, [2] = 59, [3] = 8, [5] = 0, [8] = 0, [19] = 0, [21] = 0, [30] = 30,
        [37] = 37, [41] = 0, [47] = 47, [55] = 0, [62] = 0,
    };

    /// <summary>
    /// g1m 재질의 텍스처 슬롯 → KTS 슬롯. primary=카테고리(tex_type2), physics=레지스트리(미등록이면 g1m unk_06 폴백).
    /// g1m 이 KTS 의 원천 — base 바디 포함 클론/추정 없이 KTS 를 정확히 재생성한다.
    /// </summary>
    public static IReadOnlyList<Slot> SlotsFromG1mMaterial(IReadOnlyList<G1mFile.TexSlot> matSlots)
        => matSlots.Select(s => new Slot(
            s.Primary,
            PhysicsByCategory.TryGetValue(s.Primary, out var ph) ? ph : s.Physics)).ToList();

    /// <summary>g1m 재질 → KTS(GSTK) 바이트.</summary>
    public static byte[] FromG1mMaterial(IReadOnlyList<G1mFile.TexSlot> matSlots)
        => Build(SlotsFromG1mMaterial(matSlots));

    // ── JSON 왕복(프로젝트 저장용, 편집 가능) ─────────────────────
    private sealed record Dto(int primary, int physics);

    /// <summary>KTS 슬롯 → JSON `{"slots":[{"primary":1,"physics":1},…]}`.</summary>
    public static string ToJson(IReadOnlyList<Slot> slots)
        => JsonSerializer.Serialize(
            new { slots = slots.Select(s => new Dto(s.Primary, s.Physics)).ToList() },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>JSON → KTS 슬롯.</summary>
    public static IReadOnlyList<Slot> FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var slots = new List<Slot>();
        if (doc.RootElement.TryGetProperty("slots", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                slots.Add(new Slot(
                    e.TryGetProperty("primary", out var p) ? p.GetInt32() : 0,
                    e.TryGetProperty("physics", out var ph) ? ph.GetInt32() : 0));
        return slots;
    }
}
