using System.IO.Compression;
using System.Text.Json;

namespace Kashira.Core.Mods;

/// <summary>
/// Editor 의 .ktmod 저작 프로젝트(= 디스크 폴더 하나). 폴더를 직접 편집하고 Build 로 .ktmod(zip)를 낸다.
/// 폴더 구조: project.ktproj(에디터 메타, .ktmod엔 미포함) + Content/ + Content_Legacy/ + preview/ + thumb.png.
/// Build 시 메타데이터에서 mod.ini 를 생성한다(KtmodPackage 가 되읽음).
/// </summary>
public sealed class KtmodProject
{
    public const string ProjectExt = ".ktproj";
    private const int Schema = 1;

    /// <summary>프로젝트 폴더 절대경로(직렬화 안 함).</summary>
    public string ProjectDir { get; private set; } = "";

    /// <summary>&lt;프로젝트이름&gt;.ktproj 절대경로(직렬화 안 함).</summary>
    public string ProjectFilePath { get; private set; } = "";

    public string Name { get; set; } = "";
    public string TargetGame { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ModifiedUtc { get; set; } = "";

    public string ContentDir => Path.Combine(ProjectDir, "Content");
    public string ContentLegacyDir => Path.Combine(ProjectDir, "Content_Legacy");

    /// <summary>parentDir 아래 name 폴더로 새 프로젝트 생성(스켈레톤 포함). 이미 있고 비어있지 않으면 예외.</summary>
    public static KtmodProject Create(string parentDir, string name, string targetGame, string nowUtc)
    {
        string dir = Path.Combine(parentDir, SafeFolderName(name));
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new IOException($"폴더가 이미 존재하고 비어있지 않음: {dir}");

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "Content"));
        Directory.CreateDirectory(Path.Combine(dir, "Content_Legacy"));
        Directory.CreateDirectory(Path.Combine(dir, "preview"));

        var p = new KtmodProject
        {
            ProjectDir = dir,
            ProjectFilePath = Path.Combine(dir, SafeFolderName(name) + ProjectExt),
            Name = name, TargetGame = targetGame,
            CreatedUtc = nowUtc, ModifiedUtc = nowUtc,
        };
        p.Save();
        return p;
    }

    /// <summary>
    /// .ktproj 파일 경로로 로드(프로젝트 열기의 통일 진입점). 폴더 경로를 주면 그 안의 *.ktproj 를 찾는다.
    /// </summary>
    public static KtmodProject Load(string pathOrFile)
    {
        string file;
        if (Directory.Exists(pathOrFile))
        {
            file = Directory.EnumerateFiles(pathOrFile, "*" + ProjectExt).FirstOrDefault()
                   ?? throw new FileNotFoundException($"{ProjectExt} 파일 없음: {pathOrFile}");
        }
        else
        {
            if (!File.Exists(pathOrFile)) throw new FileNotFoundException($"프로젝트 파일 없음: {pathOrFile}");
            file = pathOrFile;
        }

        var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file))
                  ?? throw new InvalidDataException($"{ProjectExt} 파싱 실패");
        return new KtmodProject
        {
            ProjectDir = Path.GetDirectoryName(Path.GetFullPath(file))!,
            ProjectFilePath = Path.GetFullPath(file),
            Name = dto.Name, TargetGame = dto.TargetGame,
            Author = dto.Author, Description = dto.Description,
            CreatedUtc = dto.CreatedUtc, ModifiedUtc = dto.ModifiedUtc,
        };
    }

    /// <summary>project.ktproj 저장(ModifiedUtc 는 호출측이 갱신).</summary>
    public void Save()
    {
        Directory.CreateDirectory(ProjectDir);
        var dto = new Dto
        {
            SchemaVersion = Schema, Name = Name, TargetGame = TargetGame,
            Author = Author, Description = Description,
            CreatedUtc = CreatedUtc, ModifiedUtc = ModifiedUtc,
        };
        File.WriteAllText(ProjectFilePath, JsonSerializer.Serialize(dto, JsonOpts));
    }

    /// <summary>
    /// 프로젝트 폴더 → .ktmod(zip). mod.ini 를 메타데이터로 생성해 포함하고,
    /// Content/ · Content_Legacy/ · preview/ · thumb.png 를 담는다(project.ktproj 는 제외).
    /// </summary>
    public void Build(string outputKtmodPath)
    {
        if (File.Exists(outputKtmodPath)) File.Delete(outputKtmodPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputKtmodPath)!);

        using var zip = ZipFile.Open(outputKtmodPath, ZipArchiveMode.Create);

        var iniEntry = zip.CreateEntry("mod.ini");
        using (var w = new StreamWriter(iniEntry.Open()))
        {
            w.Write($"Target = {TargetGame}\n");
            if (!string.IsNullOrWhiteSpace(Author)) w.Write($"Author = {Author}\n");
            if (!string.IsNullOrWhiteSpace(Description)) w.Write($"Description = {Description}\n");
        }

        foreach (var sub in new[] { "Content", "Content_Legacy", "preview" })
            AddDir(zip, Path.Combine(ProjectDir, sub), sub);
        string thumb = Path.Combine(ProjectDir, "thumb.png");
        if (File.Exists(thumb)) zip.CreateEntryFromFile(thumb, "thumb.png");
    }

    private static void AddDir(ZipArchive zip, string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, $"{prefix}/{rel}");
        }
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private sealed class Dto
    {
        public int SchemaVersion { get; set; }
        public string Name { get; set; } = "";
        public string TargetGame { get; set; } = "";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string ModifiedUtc { get; set; } = "";
    }
}
