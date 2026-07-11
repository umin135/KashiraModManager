using System.Buffers.Binary;
using Kashira.Core.Formats;
using Kashira.Core.Patching;

namespace Kashira.Core.Mods;

/// <summary>
/// Editor 워크스페이스용: 프로젝트의 Content/ · Content_Legacy/ 를 나열하고 에셋 메타데이터를 뽑는다.
/// - Content_Legacy: 사용자가 직접 놓는 raw 파일(0x{ktid}.{ext}) 목록.
/// - Content: 파일 관리자처럼 트리 탐색 + 선택 에셋 메타 미리보기.
/// </summary>
public static class ProjectContent
{
    public sealed record LegacyFile(string FileName, string Ext, uint? Ktid, long Size);
    public sealed record ContentNode(string Name, string FullPath, bool IsDirectory, List<ContentNode> Children);
    public sealed record MetaLine(string Key, string Value);

    /// <summary>Content_Legacy 의 raw 파일 목록(파일명에서 ktid 파싱).</summary>
    public static List<LegacyFile> ListLegacy(KtmodProject project)
    {
        var list = new List<LegacyFile>();
        var dir = project.ContentLegacyDir;
        if (!Directory.Exists(dir)) return list;
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            uint? ktid = DebugMods.TryParseKtid(stem, out var k) ? k : null;
            list.Add(new LegacyFile(name, ext, ktid, new FileInfo(path).Length));
        }
        return list.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Content/ 를 루트로 한 파일 트리(폴더 먼저, 이름순).</summary>
    public static ContentNode ContentRoot(KtmodProject project)
        => BuildNode("Content", project.ContentDir);

    /// <summary>Content_Legacy/ 를 루트로 한 파일 트리(통합 브라우저용).</summary>
    public static ContentNode LegacyRoot(KtmodProject project)
        => BuildNode("Content_Legacy", project.ContentLegacyDir);

    /// <summary>언리얼식 Content Browser 좌측: 폴더"만" 트리(파일 제외). 루트 = Content/(+Content_Legacy/).</summary>
    public static List<ContentNode> FolderRoots(KtmodProject project, bool content, bool legacy)
    {
        var roots = new List<ContentNode>();
        if (content) roots.Add(FolderOnlyNode("Content", project.ContentDir));
        if (legacy) roots.Add(FolderOnlyNode("Content_Legacy", project.ContentLegacyDir));
        return roots;
    }

    private static ContentNode FolderOnlyNode(string name, string dir)
    {
        var children = new List<ContentNode>();
        if (Directory.Exists(dir))
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                children.Add(FolderOnlyNode(Path.GetFileName(sub), sub));
        return new ContentNode(name, dir, true, children);
    }

    /// <summary>언리얼식 Content Browser 우측: 한 폴더의 직속 항목(하위폴더 + 파일, 재귀 X).</summary>
    public static List<ContentNode> FolderItems(string dir)
    {
        var items = new List<ContentNode>();
        if (!Directory.Exists(dir)) return items;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            items.Add(new ContentNode(Path.GetFileName(sub), sub, true, new()));
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            items.Add(new ContentNode(Path.GetFileName(file), file, false, new()));
        return items;
    }

    private static ContentNode BuildNode(string name, string dir)
    {
        var children = new List<ContentNode>();
        if (Directory.Exists(dir))
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                children.Add(BuildNode(Path.GetFileName(sub), sub));
            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                children.Add(new ContentNode(Path.GetFileName(file), file, false, new()));
        }
        return new ContentNode(name, dir, true, children);
    }

    /// <summary>에셋 파일 메타데이터(포맷 인지). 파싱 실패 시 최소 정보만.</summary>
    public static List<MetaLine> Inspect(string filePath)
    {
        var lines = new List<MetaLine>();
        var fi = new FileInfo(filePath);
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        lines.Add(new("File", Path.GetFileName(filePath)));
        lines.Add(new("Type", ext.Length == 0 ? "(none)" : ext));
        lines.Add(new("Size", HumanSize(fi.Exists ? fi.Length : 0)));

        try
        {
            switch (ext)
            {
                case "json": InspectJson(filePath, lines); break;
                case "mtl": InspectMtl(filePath, lines); break;
                case "g1t": InspectG1t(filePath, lines); break;
                case "g1m": InspectMagic(filePath, lines); break;
            }
        }
        catch { lines.Add(new("Note", "메타 파싱 실패(형식 확인 필요)")); }

        return lines;
    }

    private static void InspectJson(string path, List<MetaLine> lines)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".mtl.json", StringComparison.OrdinalIgnoreCase))
        {
            var d = MtlDoc.FromJson(File.ReadAllText(path));
            lines.Add(new("Kind", "mtl (JSON)"));
            lines.Add(new("Materials(names)", d.Names.Count.ToString()));
            lines.Add(new("g1m palette(num_mat)", d.NumMat.ToString()));
            return;
        }
        if (name.EndsWith(".grp.json", StringComparison.OrdinalIgnoreCase))
        {
            var d = GrpDoc.FromJson(File.ReadAllText(path));
            lines.Add(new("Kind", "grp (JSON)"));
            lines.Add(new("Parts", d.Entries.Count.ToString()));
            return;
        }
        var cm = CostumeManifest.Parse(File.ReadAllText(path));
        if (cm is null) { lines.Add(new("Manifest", "코스튬 매니페스트 아님")); return; }
        lines.Add(new("ModType", cm.ModType));
        lines.Add(new("TargetCostume", cm.TargetCostume));
        lines.Add(new("Mode", cm.IsAuthored ? "Authored" : cm.IsSwap ? "Swap" : "(unset)"));
        if (cm.IsSwap) lines.Add(new("SourceCostume", cm.SourceCostume ?? ""));
        if (cm.IsAuthored)
        {
            lines.Add(new("VariationCount", cm.VariationCount.ToString()));
            lines.Add(new("Materials", cm.Materials.Count.ToString()));
            if (cm.Mesh is { } m) lines.Add(new("Mesh", $"g1m={m.G1m} grp={m.Grp} mtl={m.Mtl}"));
        }
    }

    private static void InspectMtl(string path, List<MetaLine> lines)
    {
        var mtl = MtlFile.Parse(File.ReadAllBytes(path));
        lines.Add(new("Materials(names)", mtl.NumNames.ToString()));
        lines.Add(new("g1m palette(num_mat)", mtl.NumMat.ToString()));
        lines.Add(new("Cloths", mtl.NumCloths.ToString()));
        lines.Add(new("Ponytails", mtl.NumPonytails.ToString()));
    }

    private static void InspectG1t(string path, List<MetaLine> lines)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 0x14 || d[0] != (byte)'G' || d[1] != (byte)'T') { lines.Add(new("g1t", "헤더 아님")); return; }
        lines.Add(new("Magic", System.Text.Encoding.ASCII.GetString(d, 0, 8)));
        int tableOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x0C));
        int numTex = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x10));
        lines.Add(new("Textures", numTex.ToString()));
        // 첫 텍스처 헤더: (tableOff + 첫 엔트리 오프셋)에서 byte1=format
        if (tableOff > 0 && tableOff + 4 <= d.Length)
        {
            int firstOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(tableOff));
            int th = tableOff + firstOff;
            if (th + 2 <= d.Length) lines.Add(new("Format(tex0)", $"0x{d[th + 1]:X2} {FormatName(d[th + 1])}"));
        }
    }

    private static void InspectMagic(string path, List<MetaLine> lines)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        int n = fs.Read(head);
        var ascii = new string(System.Text.Encoding.ASCII.GetString(head[..n]).Select(c => char.IsControl(c) ? '.' : c).ToArray());
        lines.Add(new("Magic", ascii));
    }

    private static string FormatName(byte fmt) => fmt switch
    {
        0x59 => "BC1/DXT1",
        0x5B => "BC3/DXT5",
        0x5F => "BC7",
        0x60 => "BC6H",
        _ => "?",
    };

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };
}
