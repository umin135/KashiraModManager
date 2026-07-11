using System.Buffers.Binary;
using System.Text;

namespace Kashira.Core.Formats;

/// <summary>
/// DDS 컨테이너 파서/라이터(최소, BC 위주). g1t ↔ dds 무손실 컨테이너 교환용.
/// 헤더 128B(magic 4 + DDS_HEADER 124). fourCC=="DX10" 이면 DXT10 헤더 20B 추가.
/// 쓰기는 항상 DX10 헤더(DXGI 명시 — 모호성 없음). 읽기는 DX10 + 레거시 fourCC(DXT1/3/5, ATI1/2) 지원.
/// </summary>
public static class DdsFile
{
    private const uint Magic = 0x20534444; // "DDS "

    // 필요한 DXGI 포맷 상수
    public const int DXGI_R8G8B8A8_UNORM = 28;
    public const int DXGI_BC1_UNORM = 71, DXGI_BC1_SRGB = 72;
    public const int DXGI_BC2_UNORM = 74, DXGI_BC2_SRGB = 75;
    public const int DXGI_BC3_UNORM = 77, DXGI_BC3_SRGB = 78;
    public const int DXGI_BC4_UNORM = 80;
    public const int DXGI_BC5_UNORM = 83;
    public const int DXGI_BC6H_UF16 = 95;
    public const int DXGI_BC7_UNORM = 98, DXGI_BC7_SRGB = 99;
    public const int DXGI_B8G8R8A8_UNORM = 87;

    public sealed record Image(int DxgiFormat, int Width, int Height, int MipCount, byte[] Data);

    public static Image Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 0x80 || BinaryPrimitives.ReadUInt32LittleEndian(buf) != Magic)
            throw new InvalidDataException("dds: 매직 아님");
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x0C..]);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x10..]);
        int mips = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x1C..]);
        if (mips <= 0) mips = 1;

        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(buf[0x50..]);
        string fourCc = Encoding.ASCII.GetString(buf.Slice(0x54, 4));

        int dataOffset = 0x80;
        int dxgi;
        if ((pfFlags & 0x4) != 0 && fourCc == "DX10")     // DDPF_FOURCC + DX10
        {
            if (buf.Length < 0x94) throw new InvalidDataException("dds: DXT10 헤더 부족");
            dxgi = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf[0x80..]);
            dataOffset = 0x94;
        }
        else if ((pfFlags & 0x4) != 0)
        {
            dxgi = fourCc switch
            {
                "DXT1" => DXGI_BC1_UNORM,
                "DXT3" => DXGI_BC2_UNORM,
                "DXT5" => DXGI_BC3_UNORM,
                "ATI1" or "BC4U" => DXGI_BC4_UNORM,
                "ATI2" or "BC5U" => DXGI_BC5_UNORM,
                _ => throw new InvalidDataException($"dds: 미지원 fourCC '{fourCc}'"),
            };
        }
        else
        {
            // 비압축 RGBA/BGRA 마스크 판별(간단): 32bpp 가정
            uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(buf[0x5C..]);
            dxgi = rMask == 0x00FF0000 ? DXGI_B8G8R8A8_UNORM : DXGI_R8G8B8A8_UNORM;
        }

        return new Image(dxgi, width, height, mips, buf[dataOffset..].ToArray());
    }

    /// <summary>DX10 헤더로 DDS 바이트 생성.</summary>
    public static byte[] Build(Image img)
    {
        var buf = new byte[0x94 + img.Data.Length];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x04..], 124);          // dwSize
        // flags: CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x08..], 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x0C..], (uint)img.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], (uint)img.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], (uint)LinearSize(img));  // pitchOrLinearSize
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1C..], (uint)Math.Max(1, img.MipCount));
        // pixelformat @0x4C
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x4C..], 32);           // pf dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x50..], 0x4);          // DDPF_FOURCC
        Encoding.ASCII.GetBytes("DX10").CopyTo(s[0x54..]);
        // caps @0x6C: TEXTURE (+ MIPMAP|COMPLEX if mips>1)
        uint caps = 0x1000 | (img.MipCount > 1 ? 0x400000u | 0x8u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x6C..], caps);
        // DXT10 header @0x80
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x80..], (uint)img.DxgiFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x84..], 3);            // resourceDimension = TEXTURE2D
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x88..], 0);            // miscFlag
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x8C..], 1);            // arraySize
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x90..], 0);            // miscFlags2
        img.Data.CopyTo(s[0x94..]);
        return buf;
    }

    private static int LinearSize(Image img)
    {
        int bb = DxgiBlockBytes(img.DxgiFormat);
        return bb > 0
            ? ((img.Width + 3) / 4) * ((img.Height + 3) / 4) * bb
            : img.Width * 4; // 비압축 32bpp: pitch = width*4
    }

    public static int DxgiBlockBytes(int dxgi) => dxgi switch
    {
        DXGI_BC1_UNORM or DXGI_BC1_SRGB or DXGI_BC4_UNORM => 8,
        DXGI_BC2_UNORM or DXGI_BC2_SRGB or DXGI_BC3_UNORM or DXGI_BC3_SRGB
            or DXGI_BC5_UNORM or DXGI_BC6H_UF16 or DXGI_BC7_UNORM or DXGI_BC7_SRGB => 16,
        _ => 0,
    };
}
