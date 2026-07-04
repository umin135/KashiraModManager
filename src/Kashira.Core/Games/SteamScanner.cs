using Kashira.Core.Steam;
using Microsoft.Win32;

namespace Kashira.Core.Games;

/// <summary>
/// Steam 라이브러리를 훑어 KatanaEngine 게임을 감지한다.
/// 흐름: Steam 경로 → libraryfolders.vdf → appmanifest_*.acf(installdir) → 게임폴더 → 엔진 감지.
/// 감지는 제네릭(root.rdb 존재)이라 알려지지 않은 KatanaEngine 게임도 잡힌다.
/// OS 분기: Windows 는 레지스트리, Linux 는 ~/.steam 등.
/// </summary>
public static class SteamScanner
{
    private const string DefaultPackageDir = "fdata_package";

    public static IReadOnlyList<GameInstall> Scan()
    {
        var results = new List<GameInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steam = FindSteamPath();
        if (steam is null) return results;

        foreach (var lib in LibraryFolders(steam))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var acf in SafeEnumerate(steamapps, "appmanifest_*.acf"))
            {
                var gi = TryManifest(steamapps, acf);
                if (gi is not null && seen.Add(gi.InstallPath))
                    results.Add(gi);
            }
        }
        return results;
    }

    private static GameInstall? TryManifest(string steamapps, string acfPath)
    {
        try
        {
            var app = Vdf.Parse(File.ReadAllText(acfPath)).Child("AppState");
            var installdir = app?.Value("installdir");
            if (app is null || string.IsNullOrEmpty(installdir)) return null;

            var gamePath = Path.Combine(steamapps, "common", installdir);
            if (!IsKatanaGame(gamePath, DefaultPackageDir)) return null;

            int.TryParse(app.Value("appid"), out var appid);
            var profile = GameProfile.ByAppId(appid);
            var name = profile?.DisplayName ?? app.Value("name") ?? installdir;

            return new GameInstall
            {
                Id = profile?.Id ?? $"app{appid}",
                DisplayName = name,
                InstallPath = gamePath,
                SteamAppId = appid,
                PackageDir = DefaultPackageDir,
                Databases = DiscoverDatabases(gamePath, DefaultPackageDir),
                Supported = true,
                ProfileId = profile?.Id,
                Verified = profile?.Verified ?? false,
                ExePath = FindMainExe(gamePath),
            };
        }
        catch
        {
            return null; // 손상된 매니페스트는 건너뜀
        }
    }

    /// <summary>수동 추가: 폴더 경로에서 GameInstall 구성 (KatanaEngine 아니면 null).</summary>
    public static GameInstall? FromPath(string gamePath)
    {
        if (!IsKatanaGame(gamePath, DefaultPackageDir)) return null;
        return new GameInstall
        {
            Id = "manual:" + gamePath,
            DisplayName = new DirectoryInfo(gamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name,
            InstallPath = gamePath,
            SteamAppId = 0,
            PackageDir = DefaultPackageDir,
            Databases = DiscoverDatabases(gamePath, DefaultPackageDir),
            Supported = true,
            ProfileId = null,
            Verified = false,
            ExePath = FindMainExe(gamePath),
        };
    }

    /// <summary>KatanaEngine 게임 판정: {packageDir}/root.rdb 존재.</summary>
    public static bool IsKatanaGame(string gamePath, string packageDir = DefaultPackageDir) =>
        File.Exists(Path.Combine(gamePath, packageDir, "root.rdb"));

    /// <summary>packageDir 안의 유효한 인덱스 DB(*.rdb ↔ *.rdx 쌍) 이름들.</summary>
    public static IReadOnlyList<string> DiscoverDatabases(string gamePath, string packageDir = DefaultPackageDir)
    {
        var pkg = Path.Combine(gamePath, packageDir);
        if (!Directory.Exists(pkg)) return Array.Empty<string>();
        return Directory.EnumerateFiles(pkg, "*.rdb")
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .Where(n => File.Exists(Path.Combine(pkg, n + ".rdx")))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> LibraryFolders(string steam)
    {
        var libs = new List<string> { steam };
        foreach (var vdf in new[]
        {
            Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steam, "config", "libraryfolders.vdf"),
        })
        {
            if (!File.Exists(vdf)) continue;
            try
            {
                var root = Vdf.Parse(File.ReadAllText(vdf));
                var lf = root.Child("libraryfolders") ?? root;
                foreach (var child in lf.Children.Values)
                {
                    var p = child.Value("path");
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) libs.Add(p);
                }
            }
            catch { /* ignore */ }
            break; // 존재하는 첫 vdf 사용
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindSteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string p && Directory.Exists(p))
                    return p;
            }
            catch { /* ignore */ }

            foreach (var c in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
                if (Directory.Exists(c)) return c;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var rel in new[]
            {
                ".steam/steam", ".local/share/Steam", ".steam/root",
                ".var/app/com.valvesoftware.Steam/.local/share/Steam",
            })
            {
                var p = Path.Combine(home, rel.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>게임 대표 실행 파일 추정: 루트의 non-helper .exe 중 가장 큰 것.</summary>
    public static string? FindMainExe(string gameRoot)
    {
        if (!Directory.Exists(gameRoot)) return null;
        try
        {
            var best = Directory.EnumerateFiles(gameRoot, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(p => !IsHelperExe(Path.GetFileNameWithoutExtension(p)))
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.Length)
                .FirstOrDefault();
            return best?.FullName;
        }
        catch { return null; }
    }

    private static bool IsHelperExe(string name)
    {
        string n = name.ToLowerInvariant();
        string[] skip =
        {
            "unitycrashhandler", "crashreport", "crashpad", "vc_redist", "vcredist",
            "dxsetup", "dotnet", "unins", "notification_helper", "steam_", "eac", "easyanticheat",
        };
        return skip.Any(s => n.Contains(s));
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
