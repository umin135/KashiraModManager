using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// G1T 텍스처 컨테이너 파서(읽기). eternity_common/DOA6/G1tFile.* 교차검증.
/// - 헤더 0x20: sig 'GT1G'(LE 0x47315447) / version / file_size / table_offset@0xC / num_textures@0x10 / platform@0x14 / unk_data_size@0x18 / unk_1C.
/// - 오프셋 테이블: table_offset 에서 u32×num_textures, 각 값은 **테이블 기준 상대 오프셋**(table + table[i]).
/// - 엔트리 헤더 8B: mip_sys(mips=hi4, sys=lo4) / format / dxdy(w=1&lt;&lt;lo4, h=1&lt;&lt;hi4) / unk_3[4] / extra_header_version.
///   extra_header_version&gt;0 이면 뒤에 확장헤더(size@0; size&gt;=0x10→width@0xC, size&gt;=0x14→height@0x10).
/// - 이미지 데이터는 (엔트리 헤더 끝 ~ 다음 엔트리 오프셋 / 파일 끝) 사이 전부(모든 mip 포함).
/// 디코딩(BC 코덱)은 별도. 여기선 텍스처 메타 + 원시 블록만 노출한다.
/// </summary>
public static class G1tFile
{
    public const uint Signature = 0x47315447; // 'GT1G' little-endian

    /// <summary>단일 텍스처. Data 는 첫 mip 이상의 원시 바이트(포맷별 슬라이스는 디코더가).</summary>
    public sealed record Texture(int Index, int Width, int Height, byte Format, int Mips, int ArraySize, byte[] Data);

    public sealed record Result(int Version, uint Platform, IReadOnlyList<Texture> Textures);

    public static Result Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 0x20) throw new InvalidDataException("g1t: 파일이 너무 작음");
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf) != Signature)
            throw new InvalidDataException("g1t: 시그니처 아님(GT1G)");

        uint versionRaw = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
        int tableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x0C..]);
        int numTextures = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x10..]);
        uint platform = BinaryPrimitives.ReadUInt32LittleEndian(buf[0x14..]);

        if (tableOffset < 0x20 || tableOffset + numTextures * 4 > buf.Length)
            throw new InvalidDataException("g1t: 오프셋 테이블 범위 초과");

        // 각 엔트리 시작(파일 기준 절대 오프셋) = tableOffset + table[i]
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
            byte extraHeaderVersion = buf[off + 7];

            int mips = mipSys >> 4;
            int width = 1 << (dxdy & 0xF);
            int height = 1 << (dxdy >> 4);
            int arraySize = 0;

            int dataStart = off + 8;
            if (extraHeaderVersion > 0)
            {
                if (dataStart + 4 > buf.Length) throw new InvalidDataException($"g1t: 엔트리 {i} 확장헤더 범위 초과");
                int exSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[dataStart..]);
                if (exSize >= 0xC)
                    arraySize = buf[dataStart + 8] >> 4;
                if (exSize >= 0x10)
                    width = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[(dataStart + 0xC)..]);
                if (exSize >= 0x14)
                    height = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[(dataStart + 0x10)..]);
                dataStart += exSize;
            }

            int dataEnd = (i == numTextures - 1) ? buf.Length : entryOffsets[i + 1];
            if (dataStart > dataEnd || dataEnd > buf.Length)
                throw new InvalidDataException($"g1t: 엔트리 {i} 이미지 데이터 범위 오류");

            textures.Add(new Texture(i, width, height, format, mips, arraySize,
                buf[dataStart..dataEnd].ToArray()));
        }

        return new Result((int)(versionRaw & 0xFFFF), platform, textures);
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
