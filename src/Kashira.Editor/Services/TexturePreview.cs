using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Kashira.Core.Formats;

namespace Kashira.Editor.Services;

/// <summary>
/// g1t(및 향후 dds/tga) → Avalonia 비트맵 디코더. Editor 전용(BCnEncoder.Net).
/// Core 의 <see cref="G1tFile"/> 로 구조를 파싱하고, 첫 텍스처의 첫 mip 를 BCn 디코드해 RGBA 로 렌더한다.
/// </summary>
public static class TexturePreview
{
    private static readonly BcDecoder Decoder = new();

    // 썸네일 캐시(경로 → 작은 비트맵). 프로젝트 새로고침 시 Clear.
    private static readonly ConcurrentDictionary<string, Bitmap?> ThumbCache = new();

    public static void ClearCache() => ThumbCache.Clear();

    /// <summary>g1t 파일 경로 → 첫 텍스처 전체해상도 비트맵(뷰포트 프리뷰). 실패/미지원이면 null.</summary>
    public static Bitmap? FromG1t(string path)
    {
        try
        {
            var parsed = G1tFile.Parse(File.ReadAllBytes(path));
            if (parsed.Textures.Count == 0) return null;
            return FromTexture(parsed.Textures[0], maxSize: 0);
        }
        catch { return null; }
    }

    /// <summary>g1t → 작은 썸네일(작은 mip 을 골라 저비용 디코드, 캐시). 실패/미지원이면 null.</summary>
    public static Bitmap? ThumbnailFromG1t(string path, int maxSize = 128)
    {
        if (ThumbCache.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            var parsed = G1tFile.Parse(File.ReadAllBytes(path));
            if (parsed.Textures.Count > 0) bmp = FromTexture(parsed.Textures[0], maxSize);
        }
        catch { bmp = null; }
        ThumbCache[path] = bmp;
        return bmp;
    }

    /// <summary>maxSize&gt;0 이면 그 크기 이하의 가장 큰 mip 을 디코드(썸네일), 0 이면 mip0(전체).</summary>
    private static Bitmap? FromTexture(G1tFile.Texture tex, int maxSize)
    {
        var fmt = MapFormat(tex.Format);
        if (fmt is null) return null;
        if (tex.Width <= 0 || tex.Height <= 0 || tex.Width > 8192 || tex.Height > 8192) return null;

        var (offset, size, w, h) = SelectMip(tex, maxSize);
        if (size <= 0 || offset + size > tex.Data.Length) return null;
        var input = new byte[size];
        Array.Copy(tex.Data, offset, input, 0, size);

        ColorRgba32[] pixels;
        try { pixels = Decoder.DecodeRaw(input, w, h, fmt.Value); }
        catch { return null; }
        if (pixels.Length < w * h) return null;

        return ToBitmap(pixels, w, h);
    }

    /// <summary>선택한 mip 의 (오프셋, 바이트수, 폭, 높이). maxSize&lt;=0 이면 mip0(전체), 그 외 maxSize 이하 최대 mip.</summary>
    private static (int offset, int size, int w, int h) SelectMip(G1tFile.Texture tex, int maxSize)
    {
        int size0 = MipBytes(tex.Format, tex.Width, tex.Height);
        if (maxSize <= 0) return (0, size0, tex.Width, tex.Height);

        int mips = Math.Max(1, tex.Mips);
        int offset = 0;
        var lastValid = (offset: 0, size: size0, w: tex.Width, h: tex.Height);
        for (int i = 0; i < mips; i++)
        {
            int w = Math.Max(1, tex.Width >> i);
            int h = Math.Max(1, tex.Height >> i);
            int size = MipBytes(tex.Format, w, h);
            if (offset + size > tex.Data.Length) break;     // 데이터 부족 → 직전 유효 mip
            lastValid = (offset, size, w, h);
            if (Math.Max(w, h) <= maxSize) return lastValid; // 치수 감소하므로 첫 적합 = 최대 적합
            offset += size;
        }
        return lastValid; // maxSize 이하가 없으면 가장 작은(마지막) 유효 mip
    }

    private static int MipBytes(byte fmt, int w, int h)
    {
        int bb = G1tFile.BlockBytes(fmt);
        return bb > 0 ? ((w + 3) / 4) * ((h + 3) / 4) * bb : w * h * 4;
    }

    private static Bitmap ToBitmap(ColorRgba32[] pixels, int w, int h)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                     PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var p = pixels[y * w + x];
                int o = x * 4;
                // 프리뷰는 RGB 를 불투명으로 렌더한다(콘텐츠 확인 우선). 많은 데이터 맵이 alpha=0 이라
                // 알파를 그대로 쓰면 완전 투명으로 보인다. 채널/알파 토글은 Phase 3(Details).
                row[o] = p.r; row[o + 1] = p.g; row[o + 2] = p.b; row[o + 3] = 255;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return wb;
    }

    private static CompressionFormat? MapFormat(byte fmt) => G1tFile.FormatName(fmt) switch
    {
        "RGBA8" => CompressionFormat.Rgba,
        "BGRA8" => CompressionFormat.Bgra,
        "BC1" => CompressionFormat.Bc1,
        "BC2" => CompressionFormat.Bc2,
        "BC3" => CompressionFormat.Bc3,
        "BC4" => CompressionFormat.Bc4,
        "BC5" => CompressionFormat.Bc5,
        "BC6H" => CompressionFormat.Bc6U,
        "BC7" => CompressionFormat.Bc7,
        _ => null,
    };
}
