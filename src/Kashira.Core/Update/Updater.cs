using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kashira.Core.Update;

public sealed record UpdateInfo(Version Version, string TagName, string AssetName, string DownloadUrl);

/// <summary>
/// GitHub Releases(latest) 기반 자기 업데이트. 별도 서버 없음.
/// 흐름: latest 릴리스 조회 → tag 버전 비교 → 현재 OS용 asset 다운로드 → exe 교체 → 재실행.
/// 단일 self-contained exe 모델 유지(교체 대상 = 실행파일 하나).
/// </summary>
public static class Updater
{
    public const string Owner = "umin135";
    public const string Repo = "KashiraModManager";

    public static Version CurrentVersion =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>더 새 버전이 있으면 UpdateInfo, 없거나 실패 시 null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = MakeClient();
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = ParseVersion(tag);
            if (latest is null || latest <= CurrentVersion) return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0)
                return null;

            var rid = Rid();
            JsonElement? pick = null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Contains(rid, StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
            }
            pick ??= assets.EnumerateArray().First();

            var asset = pick.Value;
            var assetName = asset.GetProperty("name").GetString() ?? "";
            var dl = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (string.IsNullOrEmpty(dl)) return null;

            return new UpdateInfo(latest, tag, assetName, dl);
        }
        catch { return null; }
    }

    /// <summary>새 exe 다운로드 → 실행파일 교체 → 새 프로세스 실행. 이후 호출측이 앱 종료.</summary>
    public static async Task ApplyAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName
                  ?? throw new InvalidOperationException("cannot resolve current exe path");
        var neu = exe + ".new";
        var old = exe + ".old";

        using (var http = MakeClient())
        await using (var src = await http.GetStreamAsync(info.DownloadUrl, ct))
        await using (var dst = File.Create(neu))
            await src.CopyToAsync(dst, ct);

        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);        // 실행 중 exe rename (Win/Linux 모두 허용)
        File.Move(neu, exe);
        if (!OperatingSystem.IsWindows()) TryChmodExec(exe);

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>시작 시 이전 업데이트의 잔여 .old 정리.</summary>
    public static void CleanupOld()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is null) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* ignore */ }
    }

    // ---- helpers ----

    private static HttpClient MakeClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kashira-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static string Rid() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux-x64"
        : "osx-x64";

    private static void TryChmodExec(string path)
    {
        try { Process.Start(new ProcessStartInfo("chmod", $"+x \"{path}\"") { UseShellExecute = false }); }
        catch { /* ignore */ }
    }
}
