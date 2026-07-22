using System;
using System.Buffers.Binary;
using System.IO;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using Kashira.Core.Formats;

namespace Kashira.Editor.Services;

/// <summary>
/// 텍스처 임포트/익스포트: g1t ↔ DDS(무손실 컨테이너 교환) · g1t ↔ TGA(후처리 디코드/BC 인코딩). Editor 전용(BCnEncoder).
/// 디코드는 <see cref="TextureDecode"/>(후처리 공용). 배열 텍스처(arraySize&gt;1)는 미지원(대부분의 MPR 은 비배열).
/// </summary>
public static class TextureIo
{
    // ── Export ────────────────────────────────────────────────

    /// <summary>g1t → DDS(같은 폴더, .dds). 무손실 블록 복사. 반환=출력 경로.</summary>
    public static string ExportDds(string g1tPath)
    {
        var r = G1tFile.Parse(File.ReadAllBytes(g1tPath));
        var t = r.Textures[0];
        if (t.ArraySize > 1) throw new NotSupportedException("Array textures are not supported for DDS export");
        int dxgi = G1tToDxgi(t.Format);
        if (dxgi == 0) throw new NotSupportedException($"Unsupported format {G1tFile.FormatName(t.Format)}");
        string outPath = Path.ChangeExtension(g1tPath, ".dds");
        File.WriteAllBytes(outPath, DdsFile.Build(new DdsFile.Image(dxgi, t.Width, t.Height, t.Mips, t.Data)));
        return outPath;
    }

    /// <summary>g1t → TGA(같은 폴더, .tga). 첫 mip 디코드 → 32bpp. 반환=출력 경로.</summary>
    public static string ExportTga(string g1tPath)
    {
        var (w, h, rgba) = DecodeMip0(g1tPath);
        string outPath = Path.ChangeExtension(g1tPath, ".tga");
        File.WriteAllBytes(outPath, WriteTga(w, h, rgba));
        return outPath;
    }

    // ── Import (replace existing g1t, 구조 보존) ───────────────

    /// <summary>DDS/TGA → 기존 g1t 의 tex0 교체(헤더/구조 보존). g1tPath 를 덮어쓴다.</summary>
    public static void ReplaceFromFile(string g1tPath, string imagePath)
    {
        var orig = File.ReadAllBytes(g1tPath);
        var t0 = G1tFile.Parse(orig).Textures[0];
        if (t0.ArraySize > 1) throw new NotSupportedException("Array textures are not supported for replacement");
        string ext = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();

        byte[] result = ext switch
        {
            "dds" => ReplaceFromDds(orig, File.ReadAllBytes(imagePath)),
            "tga" => ReplaceFromTga(orig, t0.Format, imagePath),
            _ => throw new NotSupportedException($"Unsupported format: .{ext} (dds/tga only)"),
        };
        File.WriteAllBytes(g1tPath, result);
    }

    private static byte[] ReplaceFromDds(byte[] orig, byte[] ddsBytes)
    {
        var dds = DdsFile.Parse(ddsBytes);
        byte fmt = DxgiToG1t(dds.DxgiFormat);
        if (fmt == 0) throw new NotSupportedException($"Unsupported DXGI format {dds.DxgiFormat}");
        return G1tFile.ReplaceTexture0(orig, fmt, dds.Width, dds.Height, dds.MipCount, 0, dds.Data);
    }

    private static byte[] ReplaceFromTga(byte[] orig, byte targetFmt, string tgaPath)
    {
        var (w, h, rgba) = ReadTga(File.ReadAllBytes(tgaPath));
        var comp = G1tToCompression(targetFmt)
                   ?? throw new NotSupportedException($"Unsupported encoding format {G1tFile.FormatName(targetFmt)}");

        var enc = new BcEncoder { OutputOptions = { Format = comp, GenerateMipMaps = true, Quality = CompressionQuality.Balanced } };
        var colors = ToColors(rgba);
        byte[][] mips = enc.EncodeToRawBytes(new ReadOnlyMemory2D<ColorRgba32>(colors, h, w));

        int total = 0;
        foreach (var m in mips) total += m.Length;
        var data = new byte[total];
        int off = 0;
        foreach (var m in mips) { Array.Copy(m, 0, data, off, m.Length); off += m.Length; }

        return G1tFile.ReplaceTexture0(orig, targetFmt, w, h, mips.Length, 0, data);
    }

    // ── 디코드 헬퍼 ───────────────────────────────────────────

    private static (int w, int h, byte[] rgba) DecodeMip0(string g1tPath)
    {
        var t = G1tFile.Parse(File.ReadAllBytes(g1tPath)).Textures[0];
        int size = MipBytes(t.Format, t.Width, t.Height);
        if (size > t.Data.Length) size = t.Data.Length;
        var input = new byte[size];
        Array.Copy(t.Data, input, size);
        // 후처리 디코드(BC4 그레이·BC5 노말Z·BC6H Reinhard) — TexturePreview 와 동일 경로.
        var rgba = TextureDecode.Decode(input, t.Width, t.Height, t.Format, TextureDecode.SwizzleType(t))
                   ?? throw new NotSupportedException($"Unsupported decode format {G1tFile.FormatName(t.Format)}");
        return (t.Width, t.Height, rgba);
    }

    private static ColorRgba32[] ToColors(byte[] rgba)
    {
        var c = new ColorRgba32[rgba.Length / 4];
        for (int i = 0; i < c.Length; i++)
            c[i] = new ColorRgba32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
        return c;
    }

    private static int MipBytes(byte fmt, int w, int h) => G1tFile.MipByteSize(fmt, w, h);

    // ── TGA (비압축 32bpp) ─────────────────────────────────────

    private static byte[] WriteTga(int w, int h, byte[] rgba)
    {
        var buf = new byte[18 + w * h * 4];
        buf[2] = 2;   // uncompressed true-color
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x0C), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x0E), (ushort)h);
        buf[0x10] = 32;    // bpp
        buf[0x11] = 0x28;  // top-origin(bit5) + 8 alpha bits
        int o = 18;
        for (int i = 0; i < w * h; i++)
        {
            // TGA truecolor = BGRA
            buf[o] = rgba[i * 4 + 2]; buf[o + 1] = rgba[i * 4 + 1];
            buf[o + 2] = rgba[i * 4]; buf[o + 3] = rgba[i * 4 + 3];
            o += 4;
        }
        return buf;
    }

    private static (int w, int h, byte[] rgba) ReadTga(byte[] buf)
    {
        if (buf.Length < 18) throw new InvalidDataException("tga: header too short");
        int idLen = buf[0];
        int imageType = buf[2];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x0C));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x0E));
        int bpp = buf[0x10];
        int desc = buf[0x11];
        bool topOrigin = (desc & 0x20) != 0;
        if (imageType != 2 && imageType != 10) throw new NotSupportedException($"tga: unsupported type {imageType} (uncompressed/RLE true-color only)");
        if (bpp != 32 && bpp != 24) throw new NotSupportedException($"tga: unsupported bpp {bpp}");
        int bytesPP = bpp / 8;
        int dataOff = 18 + idLen; // 컬러맵 없음 가정

        var pixels = new byte[w * h * 4];
        if (imageType == 2)
        {
            for (int i = 0; i < w * h; i++)
            {
                int s = dataOff + i * bytesPP;
                if (s + bytesPP > buf.Length) break;
                pixels[i * 4] = buf[s + 2]; pixels[i * 4 + 1] = buf[s + 1]; pixels[i * 4 + 2] = buf[s];
                pixels[i * 4 + 3] = bytesPP == 4 ? buf[s + 3] : (byte)255;
            }
        }
        else // RLE (10)
        {
            int p = dataOff, i = 0;
            while (i < w * h && p < buf.Length)
            {
                int packet = buf[p++]; int count = (packet & 0x7F) + 1;
                if ((packet & 0x80) != 0) // RLE packet
                {
                    if (p + bytesPP > buf.Length) break;
                    byte b = buf[p], g = buf[p + 1], rr = buf[p + 2], a = bytesPP == 4 ? buf[p + 3] : (byte)255;
                    p += bytesPP;
                    for (int k = 0; k < count && i < w * h; k++, i++)
                    { pixels[i * 4] = rr; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = b; pixels[i * 4 + 3] = a; }
                }
                else // raw packet
                {
                    for (int k = 0; k < count && i < w * h; k++, i++)
                    {
                        if (p + bytesPP > buf.Length) break;
                        pixels[i * 4] = buf[p + 2]; pixels[i * 4 + 1] = buf[p + 1]; pixels[i * 4 + 2] = buf[p];
                        pixels[i * 4 + 3] = bytesPP == 4 ? buf[p + 3] : (byte)255;
                        p += bytesPP;
                    }
                }
            }
        }

        if (!topOrigin) FlipVertical(pixels, w, h);
        return (w, h, pixels);
    }

    private static void FlipVertical(byte[] px, int w, int h)
    {
        int stride = w * 4;
        var tmp = new byte[stride];
        for (int y = 0; y < h / 2; y++)
        {
            int top = y * stride, bot = (h - 1 - y) * stride;
            Array.Copy(px, top, tmp, 0, stride);
            Array.Copy(px, bot, px, top, stride);
            Array.Copy(tmp, 0, px, bot, stride);
        }
    }

    // ── 포맷 매핑 ─────────────────────────────────────────────

    private static int G1tToDxgi(byte fmt) => G1tFile.FormatName(fmt) switch
    {
        "BC1" => DdsFile.DXGI_BC1_UNORM, "BC2" => DdsFile.DXGI_BC2_UNORM, "BC3" => DdsFile.DXGI_BC3_UNORM,
        "BC4" => DdsFile.DXGI_BC4_UNORM, "BC5" => DdsFile.DXGI_BC5_UNORM, "BC6H" => DdsFile.DXGI_BC6H_UF16,
        "BC7" => DdsFile.DXGI_BC7_UNORM, "RGBA8" => DdsFile.DXGI_R8G8B8A8_UNORM, "BGRA8" => DdsFile.DXGI_B8G8R8A8_UNORM,
        _ => 0,
    };

    private static byte DxgiToG1t(int dxgi) => dxgi switch
    {
        DdsFile.DXGI_BC1_UNORM or DdsFile.DXGI_BC1_SRGB => 0x59,
        DdsFile.DXGI_BC2_UNORM or DdsFile.DXGI_BC2_SRGB => 0x5A,
        DdsFile.DXGI_BC3_UNORM or DdsFile.DXGI_BC3_SRGB => 0x5B,
        DdsFile.DXGI_BC4_UNORM => 0x5C,
        DdsFile.DXGI_BC5_UNORM => 0x5D,
        DdsFile.DXGI_BC6H_UF16 => 0x5E,
        DdsFile.DXGI_BC7_UNORM or DdsFile.DXGI_BC7_SRGB => 0x5F,
        DdsFile.DXGI_R8G8B8A8_UNORM => 0x09,
        DdsFile.DXGI_B8G8R8A8_UNORM => 0x0A,
        _ => 0,
    };

    private static CompressionFormat? G1tToCompression(byte fmt) => G1tFile.FormatName(fmt) switch
    {
        "RGBA8" => CompressionFormat.Rgba, "BGRA8" => CompressionFormat.Bgra,
        "BC1" => CompressionFormat.Bc1, "BC2" => CompressionFormat.Bc2, "BC3" => CompressionFormat.Bc3,
        "BC4" => CompressionFormat.Bc4, "BC5" => CompressionFormat.Bc5, "BC6H" => CompressionFormat.Bc6U,
        "BC7" => CompressionFormat.Bc7,
        _ => null,
    };
}
