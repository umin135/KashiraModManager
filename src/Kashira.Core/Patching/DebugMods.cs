using System.Globalization;

namespace Kashira.Core.Patching;

/// <summary>
/// DebugMods 폴더의 날것 파일(0x{file_ktid}.{ext})을 교체 목록으로 로드.
/// 파일명에서 file_ktid 만 사용(타입은 RDB 기존 엔트리에서 옴).
/// </summary>
public static class DebugMods
{
    /// <summary>주입할 파일 1개. Ext 는 확장자(점 제외, 소문자) — 신규 등록 시 타입 결정용.</summary>
    public sealed record Entry(uint FileKtid, string FileName, string FullPath, long Size, string Ext);

    public static List<Entry> List(string debugModsDir)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(debugModsDir)) return list;

        foreach (var path in Directory.EnumerateFiles(debugModsDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (TryParseKtid(name, out var ktid))
            {
                var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                list.Add(new Entry(ktid, Path.GetFileName(path), path, new FileInfo(path).Length, ext));
            }
        }
        return list.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool TryParseKtid(string name, out uint ktid)
    {
        name = name.Trim();
        var span = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? name.AsSpan(2) : name.AsSpan();
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ktid);
    }
}
