using System;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Kashira.Core.Formats;

namespace Kashira.Editor.Services;

/// <summary>
/// g1t 블록 데이터 → RGBA8888 디코드 + 포맷별 후처리(정확한 표시).
/// 참조: _docs/g1t_decoding.md §7 — BC4=그레이스케일, BC5=노말맵 Z 재구성, BC6H=HDR→Reinhard.
/// DOA6 LR 은 ex_swizzle_type=0(스위즐/ZLIB 없음, 실측 101/101). 스위즐/ZLIB 텍스처(타 게임)는 미지원 → null.
/// </summary>
public static class TextureDecode
{
    private static readonly BcDecoder Decoder = new();

    /// <summary>g1t 텍스처의 ex_swizzle_type(확장헤더 ep+18 = extra[10]). 없으면 0.</summary>
    public static int SwizzleType(G1tFile.Texture tex)
        => tex.ExtraHeader.Length >= 11 ? tex.ExtraHeader[10] : 0;

    /// <summary>블록 데이터(첫 mip) → 후처리된 RGBA8888(길이 w*h*4). 미지원/실패면 null.</summary>
    public static byte[]? Decode(byte[] blockData, int w, int h, byte g1tFormat, int swizzleType = 0)
    {
        // 스위즐/ZLIB(비트0/1) 은 DOA6 외 게임용 — 현재 미지원(가비지 방지 위해 명시적 실패).
        if ((swizzleType & 0x03) != 0) return null;

        string fam = G1tFile.FormatName(g1tFormat);
        try
        {
            if (fam == "BC6H")
                return DecodeBc6HReinhard(blockData, w, h);

            // 비압축 float 포맷(R32F / RGBA16F / RGBA32F) — BCnEncoder 미지원, 직접 디코드.
            if (g1tFormat is 0x02 or 0x03 or 0x04 or 0x0C)
                return DecodeFloat(blockData, w, h, g1tFormat);

            var fmt = MapFormat(fam);
            if (fmt is null) return null;
            var px = Decoder.DecodeRaw(blockData, w, h, fmt.Value);
            if (px.Length < w * h) return null;
            return PostProcess(px, w, h, fam);
        }
        catch { return null; }
    }

    private static byte[] PostProcess(ColorRgba32[] px, int w, int h, string fam)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            var p = px[i];
            byte r = p.r, g = p.g, b = p.b, a = p.a;
            switch (fam)
            {
                case "BC4":                 // 단채널 → 그레이스케일
                    g = b = r; a = 255;
                    break;
                case "BC5":                 // 2채널 RG → 노말맵 Z 재구성
                    double rx = r / 127.5 - 1.0, ry = g / 127.5 - 1.0;
                    double rz2 = 1.0 - rx * rx - ry * ry;
                    double rz = rz2 > 0 ? Math.Sqrt(rz2) : 0.0;
                    r = ToByte(rx * 0.5 + 0.5); g = ToByte(ry * 0.5 + 0.5); b = ToByte(rz * 0.5 + 0.5); a = 255;
                    break;
            }
            int o = i * 4;
            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
        }
        return rgba;
    }

    /// <summary>BC6H(HDR half-float) → Reinhard 톤매핑 LDR. BCnEncoder 의 HDR 디코드 사용.</summary>
    private static byte[] DecodeBc6HReinhard(byte[] blockData, int w, int h)
    {
        var hdr = Decoder.DecodeRawHdr(blockData, w, h, CompressionFormat.Bc6U);
        if (hdr.Length < w * h) throw new InvalidOperationException("bc6h decode size insufficient");
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            var p = hdr[i];
            int o = i * 4;
            rgba[o] = Reinhard(p.r); rgba[o + 1] = Reinhard(p.g); rgba[o + 2] = Reinhard(p.b); rgba[o + 3] = 255;
        }
        return rgba;
    }

    /// <summary>비압축 float 포맷 → RGBA8. R32F=그레이스케일, RGBA16F/32F=RGB(값 0..1 클램프).</summary>
    private static byte[]? DecodeFloat(byte[] d, int w, int h, byte fmt)
    {
        int bpp = G1tFile.BytesPerPixel(fmt);
        if (bpp <= 0 || d.Length < w * h * bpp) return null;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int s = i * bpp;
            float r, g, b;
            switch (fmt)
            {
                case 0x02: // R32_FLOAT → 그레이스케일
                    r = g = b = BitConverter.ToSingle(d, s); break;
                case 0x03: case 0x0C: // R16G16B16A16_FLOAT
                    r = (float)BitConverter.ToHalf(d, s);
                    g = (float)BitConverter.ToHalf(d, s + 2);
                    b = (float)BitConverter.ToHalf(d, s + 4); break;
                case 0x04: // R32G32B32A32_FLOAT
                    r = BitConverter.ToSingle(d, s);
                    g = BitConverter.ToSingle(d, s + 4);
                    b = BitConverter.ToSingle(d, s + 8); break;
                default: return null;
            }
            int o = i * 4;
            rgba[o] = ToByte(r); rgba[o + 1] = ToByte(g); rgba[o + 2] = ToByte(b); rgba[o + 3] = 255;
        }
        return rgba;
    }

    private static byte Reinhard(float c)
    {
        if (c < 0) c = 0;
        return ToByte(c / (1.0f + c));
    }

    private static byte ToByte(double v)
    {
        int i = (int)Math.Round(v * 255.0);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    public static CompressionFormat? MapFormat(string fam) => fam switch
    {
        "RGBA8" => CompressionFormat.Rgba,
        "BGRA8" => CompressionFormat.Bgra,
        "BC1" => CompressionFormat.Bc1,
        "BC2" => CompressionFormat.Bc2,
        "BC3" => CompressionFormat.Bc3,
        "BC4" => CompressionFormat.Bc4,
        "BC5" => CompressionFormat.Bc5,
        "BC7" => CompressionFormat.Bc7,
        _ => null,     // BC6H 은 HDR 경로, 그 외 미지원
    };
}
