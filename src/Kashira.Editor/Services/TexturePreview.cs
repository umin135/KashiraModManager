using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kashira.Core.Formats;

namespace Kashira.Editor.Services;

/// <summary>
/// g1t → Avalonia 비트맵 디코더. Editor 전용. Core 의 <see cref="G1tFile"/> 로 구조를 파싱하고,
/// 첫 텍스처의 (썸네일은 작은) mip 을 <see cref="TextureDecode"/> 로 후처리 디코드해 RGBA 로 렌더한다.
/// </summary>
public static class TexturePreview
{
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
        if (tex.Width <= 0 || tex.Height <= 0 || tex.Width > 8192 || tex.Height > 8192) return null;

        var (offset, size, w, h) = SelectMip(tex, maxSize);
        if (size <= 0 || offset + size > tex.Data.Length) return null;
        var input = new byte[size];
        Array.Copy(tex.Data, offset, input, 0, size);

        // 디코드 + 포맷별 후처리(BC4 그레이·BC5 노말Z·BC6H Reinhard). ex_swizzle_type 은 확장헤더에서.
        var rgba = TextureDecode.Decode(input, w, h, tex.Format, TextureDecode.SwizzleType(tex));
        return rgba is null ? null : ToBitmap(rgba, w, h);
    }

    /// <summary>
    /// 선택한 mip 의 (오프셋, 바이트수, 폭, 높이). maxSize&lt;=0 이면 mip0(전체, slice0), 그 외 maxSize 이하 최대 mip.
    /// 배열 텍스처(arraySize≥2)는 mip 이 슬라이스마다 인터리브 저장(mip0×slices, mip1×slices…)되므로
    /// 다음 mip 으로 넘어갈 때 오프셋을 slices 배로 전진한다. slice0 만 디코드.
    /// </summary>
    private static (int offset, int size, int w, int h) SelectMip(G1tFile.Texture tex, int maxSize)
    {
        int size0 = MipBytes(tex.Format, tex.Width, tex.Height);
        if (maxSize <= 0) return (0, size0, tex.Width, tex.Height); // mip0 slice0 는 항상 오프셋 0

        int slices = Math.Max(1, tex.ArraySize);
        int mips = Math.Max(1, tex.Mips);
        int offset = 0;
        var lastValid = (offset: 0, size: size0, w: tex.Width, h: tex.Height);
        for (int i = 0; i < mips; i++)
        {
            int w = Math.Max(1, tex.Width >> i);
            int h = Math.Max(1, tex.Height >> i);
            int size = MipBytes(tex.Format, w, h);          // 한 슬라이스 크기
            if (offset + size > tex.Data.Length) break;     // slice0 이 안 들어가면 직전 유효 mip
            lastValid = (offset, size, w, h);
            if (Math.Max(w, h) <= maxSize) return lastValid; // 치수 감소 → 첫 적합 = 최대 적합
            offset += size * slices;                        // 이 mip 의 전 슬라이스를 건너뛴다
        }
        return lastValid; // maxSize 이하가 없으면 가장 작은(마지막) 유효 mip
    }

    private static int MipBytes(byte fmt, int w, int h) => G1tFile.MipByteSize(fmt, w, h);

    /// <summary>후처리된 RGBA8888(w*h*4) → WriteableBitmap. 프리뷰는 알파를 불투명으로(콘텐츠 확인 우선).</summary>
    private static Bitmap ToBitmap(byte[] rgba, int w, int h)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                     PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, o = x * 4;
                // 데이터 맵 다수가 alpha=0 이라 그대로 쓰면 완전 투명 → 불투명 렌더. 채널/알파 토글은 후속.
                row[o] = rgba[s]; row[o + 1] = rgba[s + 1]; row[o + 2] = rgba[s + 2]; row[o + 3] = 255;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return wb;
    }
}
