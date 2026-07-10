using System.Text.Json;

namespace Kashira.Core.Mods;

/// <summary>Editor 최근 프로젝트 목록을 사용자 설정 폴더에 영속화(GameLibrary 와 동일 패턴).</summary>
public static class EditorProjects
{
    /// <summary>최근 프로젝트 1건.</summary>
    public sealed record Recent(string Name, string Path, string TargetGame, string LastOpenedUtc);

    public static string ConfigDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kashira");

    public static string ConfigPath => System.IO.Path.Combine(ConfigDir, "editor_recent.json");

    private const int MaxRecent = 20;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>최근 목록 로드. 폴더가 사라진 항목은 걸러낸다(최근순 유지).</summary>
    public static List<Recent> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new();
            var list = JsonSerializer.Deserialize<List<Recent>>(File.ReadAllText(ConfigPath)) ?? new();
            return list.Where(r => Directory.Exists(r.Path)).ToList();
        }
        catch { return new(); }
    }

    public static void Save(IEnumerable<Recent> recents)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(recents.Take(MaxRecent), Options));
        }
        catch { /* 저장 실패 무시 */ }
    }

    /// <summary>프로젝트를 최근 목록 최상단으로 갱신(중복 경로 제거)하고 저장. nowUtc 는 호출측 제공.</summary>
    public static void Touch(KtmodProject project, string nowUtc)
    {
        var list = Load().Where(r => !PathEquals(r.Path, project.ProjectDir)).ToList();
        list.Insert(0, new Recent(project.Name, project.ProjectDir, project.TargetGame, nowUtc));
        Save(list);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'),
                      System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
                      StringComparison.OrdinalIgnoreCase);
}
