using System.Text.Json;

namespace Kashira.Core.Games;

/// <summary>감지/추가된 게임 목록을 사용자 설정 폴더에 영속화한다.</summary>
public static class GameLibrary
{
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kashira");

    public static string ConfigPath => Path.Combine(ConfigDir, "games.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<GameInstall> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new();
            return JsonSerializer.Deserialize<List<GameInstall>>(File.ReadAllText(ConfigPath)) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(IEnumerable<GameInstall> games)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(games, Options));
        }
        catch { /* 저장 실패는 무시(다음 실행에 재스캔 가능) */ }
    }
}
