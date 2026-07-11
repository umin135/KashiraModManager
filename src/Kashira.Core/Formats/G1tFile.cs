using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// G1T 텍스처 컨테이너 파서/라이터(왕복). eternity_common/DOA6/G1tFile.* 교차검증 + 실측 DOA6 헤더.
/// - 헤더 0x20: sig 'GT1G'(LE 0x47315447) / version / file_size / table_offset@0xC / num_textures@0x10 / platform@0x14 / unk_data_size@0x18 / unk_1C.
/// - 오프셋 테이블: table_offset 에서 u32×num, 값은 **테이블 기준 상대 오프셋**(table + table[i]).
/// - 엔트리 헤더 8B: mip_sys(mips=hi4, sys=lo4) / format / dxdy(w=1&lt;&lt;lo4, h=1&lt;&lt;hi4) / unk_3[4] / extra_header_version.
///   extra_header_version&gt;0 이면 확장헤더(size@0; size&gt;=0xC→array_other@8[상위4=array_size]; size&gt;=0x10→width@0xC; size&gt;=0x14→height@0x10).
/// - 이미지 데이터 = (확장헤더 끝 ~ 다음 엔트리 / 파일 끝) 전부(모든 mip + 배열 슬라이스).
/// 실측 DOA6: extra_header_version=0x12, 확장헤더 0xC, unk_3=00 00 10 11, platform=0xA.
/// </summary>
public static class G1tFile
{
    public const uint Signature = 0x47315447; // 'GT1G' little-endian

    /// <summary>단일 텍스처(왕복 가능). Data 는 첫 mip 이상의 원시 바이트.</summary>
    public sealed record Texture(
        int Index, int Width, int Height, byte Format, int Mips, int Sys, int ArraySize,
        byte[] Unk3, byte ExtraHeaderVersion, byte[] ExtraHeader, byte[] Data);

    public sealed record Result(uint Version, uint Platform, uint Unk1C, byte[] ExtraHeaderTop, byte[] UnkData, IReadOnlyList<Texture> Textures);

    public static Result Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 0x20) throw new InvalidDataException("g1t: 파일이 너무 작음");
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != Signature)
            throw new InvalidDataException("g1t: 시그니처 아님(GT1G)");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
        int tableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x0C..]);
        int numTextures = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x10..]);
        uint platform = BinaryPrimitives.ReadUInt32LittleEndian(buf[0x14..]);
        int unkDataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x18..]);
        uint unk1C = BinaryPrimitives.ReadUInt32LittleEndian(buf[0x1C..]);

        if (tableOffset < 0x20 || tableOffset + numTextures * 4 > buf.Length)
            throw new InvalidDataException("g1t: 오프셋 테이블 범위 초과");

        byte[] extraTop = buf[0x20..tableOffset].ToArray();
        byte[] unkData = unkDataSize > 0
            ? buf.Slice(tableOffset + numTextures * 4, unkDataSize).ToArray()
            : Array.Empty<byte>();

        var entryOffsets = new int[numTextures];
        for (int i = 0; i < numTextures; i++)
            entryOffsets[i] = tableOffset + (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[(tableOffset + i * 4)..]);

        var textures = new List<Texture>(numTextures);
        for (int i = 0; i < numTextures; i++)
        {
            int off = entryOffsets[i];
            if (off + 8 > buf.Length) throw new InvalidDataException($"g1t: 엔트리 {i} 헤더 범위 초과");

            byte mipSys = buf[off];
            byte format = buf[off + 1];
            byte dxdy = buf[off + 2];
            byte[] unk3 = buf.Slice(off + 3, 4).ToArray();
            byte exVer = buf[off + 7];

            int mips = mipSys >> 4;
            int sys = mipSys & 0xF;
            int width = 1 << (dxdy & 0xF);
            int height = 1 << (dxdy >> 4);
            int arraySize = 0;
            byte[] extra = Array.Empty<byte>();

            int dataStart = off + 8;
            if (exVer > 0)
            {
                if (dataStart + 4 > buf.Length) throw new InvalidDataException($"g1t: 엔트리 {i} 확장헤더 범위 초과");
                int exSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[dataStart..]);
                if (exSize < 4 || dataStart + exSize > buf.Length) throw new InvalidDataException($"g1t: 엔트리 {i} 확장헤더 크기 오류");
                extra = buf.Slice(dataStart, exSize).ToArray();
                if (exSize >= 0xC) arraySize = buf[dataStart + 8] >> 4;
                if (exSize >= 0x10) width = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[(dataStart + 0xC)..]);
                if (exSize >= 0x14) height = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[(dataStart + 0x10)..]);
                dataStart += exSize;
            }

            int dataEnd = (i == numTextures - 1) ? buf.Length : entryOffsets[i + 1];
            if (dataStart > dataEnd || dataEnd > buf.Length)
                throw new InvalidDataException($"g1t: 엔트리 {i} 이미지 데이터 범위 오류");

            textures.Add(new Texture(i, width, height, format, mips, sys, arraySize,
                unk3, exVer, extra, buf[dataStart..dataEnd].ToArray()));
        }

        return new Result(version, platform, unk1C, extraTop, unkData, textures);
    }

    // ── 라이터 ────────────────────────────────────────────────
    private const uint DefaultVersion = 0x30303630; // "0600" (실측 DOA6)
    private static readonly byte[] DefaultUnk3 = { 0x00, 0x00, 0x10, 0x11 };

    /// <summary>전체 컨테이너 → 바이트(왕복). 배열/dims 변경 시 확장헤더 필드도 갱신한다.</summary>
    public static byte[] Build(Result r)
    {
        int tableOffset = 0x20 + r.ExtraHeaderTop.Length;
        int n = r.Textures.Count;

        // 엔트리 바이트 미리 구성
        var entryBlobs = new List<byte[]>(n);
        foreach (var t in r.Textures) entryBlobs.Add(BuildEntry(t));

        int total = tableOffset + n * 4 + r.UnkData.Length;
        foreach (var e in entryBlobs) total += e.Length;

        var buf = new byte[total];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], r.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], (uint)tableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], (uint)n);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], r.Platform);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)r.UnkData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], r.Unk1C);
        r.ExtraHeaderTop.CopyTo(span[0x20..]);

        int tablePos = tableOffset;
        int unkPos = tableOffset + n * 4;
        r.UnkData.CopyTo(span[unkPos..]);
        int entryPos = unkPos + r.UnkData.Length;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(tablePos + i * 4)..], (uint)(entryPos - tableOffset));
            entryBlobs[i].CopyTo(span[entryPos..]);
            entryPos += entryBlobs[i].Length;
        }
        return buf;
    }

    private static byte[] BuildEntry(Texture t)
    {
        // 확장헤더 갱신(array_other 및 size>=0x10 이면 width/height)
        byte[] extra = t.ExtraHeader.Length > 0 ? (byte[])t.ExtraHeader.Clone() : Array.Empty<byte>();
        byte exVer = t.ExtraHeaderVersion;
        if (exVer > 0 && extra.Length >= 0xC)
        {
            extra[8] = (byte)((extra[8] & 0x0F) | ((t.ArraySize & 0xF) << 4));
            if (extra.Length >= 0x10) BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(0xC), (uint)t.Width);
            if (extra.Length >= 0x14) BinaryPrimitives.WriteUInt32LittleEndian(extra.AsSpan(0x10), (uint)t.Height);
        }

        var buf = new byte[8 + extra.Length + t.Data.Length];
        buf[0] = (byte)((t.Sys & 0xF) | ((t.Mips & 0xF) << 4));
        buf[1] = t.Format;
        buf[2] = (byte)(Log2(t.Width) | (Log2(t.Height) << 4));
        (t.Unk3.Length == 4 ? t.Unk3 : DefaultUnk3).CopyTo(buf, 3);
        buf[7] = exVer;
        extra.CopyTo(buf, 8);
        t.Data.CopyTo(buf, 8 + extra.Length);
        return buf;
    }

    /// <summary>새 단일 텍스처 g1t(실측 DOA6 기본 구조: exVer=0x12, 확장헤더 0xC).</summary>
    public static byte[] BuildSingle(byte format, int width, int height, int mips, int arraySize, byte[] data)
    {
        var extra = new byte[0xC];
        BinaryPrimitives.WriteUInt32LittleEndian(extra, 0xC);
        extra[8] = (byte)((arraySize & 0xF) << 4);
        var tex = new Texture(0, width, height, format, Math.Max(1, mips), 0, arraySize,
            DefaultUnk3, 0x12, extra, data);
        return Build(new Result(DefaultVersion, 0xA, 0, Array.Empty<byte>(), Array.Empty<byte>(), new[] { tex }));
    }

    /// <summary>원본 g1t 의 헤더/구조를 보존한 채 tex0 의 픽셀 데이터만 교체(가장 안전한 임포트).</summary>
    public static byte[] ReplaceTexture0(ReadOnlySpan<byte> originalG1t, byte format, int width, int height, int mips, int arraySize, byte[] data)
    {
        var r = Parse(originalG1t);
        if (r.Textures.Count == 0) throw new InvalidDataException("g1t: 텍스처 없음");
        var t0 = r.Textures[0];
        var newTex = t0 with { Format = format, Width = width, Height = height, Mips = Math.Max(1, mips), ArraySize = arraySize, Data = data };
        var list = new List<Texture>(r.Textures) { [0] = newTex };
        return Build(r with { Textures = list });
    }

    private static int Log2(int v)
    {
        int r = 0;
        while ((1 << r) < v && r < 31) r++;
        return r;
    }

    /// <summary>포맷 바이트 → 사람이 읽는 이름(주요 BC/비압축만).</summary>
    public static string FormatName(byte fmt) => fmt switch
    {
        0x00 or 0x09 => "RGBA8",
        0x01 or 0x0A => "BGRA8",
        0x06 or 0x10 or 0x59 or 0x60 => "BC1",
        0x07 or 0x11 or 0x5A or 0x61 => "BC2",
        0x08 or 0x12 or 0x5B or 0x62 => "BC3",
        0x5C or 0x63 => "BC4",
        0x5D or 0x64 => "BC5",
        0x5E or 0x65 => "BC6H",
        0x5F or 0x66 => "BC7",
        _ => $"0x{fmt:X2}",
    };

    /// <summary>블록 압축 포맷이면 블록당 바이트 수(BC1/BC4=8, 그 외 BC=16), 아니면 0.</summary>
    public static int BlockBytes(byte fmt) => FormatName(fmt) switch
    {
        "BC1" or "BC4" => 8,
        "BC2" or "BC3" or "BC5" or "BC6H" or "BC7" => 16,
        _ => 0,
    };
}
