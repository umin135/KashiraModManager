namespace Kashira.Core.Games;

/// <summary>게임별 경로 규약(_Kashira/…)과 인덱스 DB 목록을 캡슐화.</summary>
public sealed class GameWorkspace
{
    public string InstallPath { get; }
    public string PackageDir { get; }               // fdata_package 절대경로
    public IReadOnlyList<string> Databases { get; } // root, system, …

    public string KashiraDir => Path.Combine(InstallPath, "_Kashira");
    public string BackupDir => Path.Combine(KashiraDir, "backup");
    public string DebugModsDir => Path.Combine(KashiraDir, "DebugMods");
    public string ModsDir => Path.Combine(KashiraDir, "Mods");
    public string PatchRecordPath => Path.Combine(KashiraDir, "rdbpatch.json");

    public GameWorkspace(GameInstall game)
    {
        InstallPath = game.InstallPath;
        PackageDir = Path.Combine(game.InstallPath, game.PackageDir);
        Databases = game.Databases.Count > 0 ? game.Databases : new[] { "root" };
    }

    public void EnsureFolders()
    {
        Directory.CreateDirectory(DebugModsDir);
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(BackupDir);
    }
}
