using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kashira.Core.Games;
using Kashira.Core.Mods;
using Kashira.Core.Patching;

namespace Kashira.Gui;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        Kashira.Core.Update.Updater.CleanupOld(); // 이전 업데이트 잔여물 정리

        // 헤드리스 실행-래핑 모드: `"Kashira.exe" --run -- <게임 exe/커맨드>`
        // Steam 실행옵션이면 `%command%`, 스탠드얼론이면 게임 exe 절대경로가 `--` 뒤에 온다.
        // 우리를 먼저 부름 → 모드 Apply 후 진짜 게임을 자식으로 실행. (--steam-run 은 하위호환 별칭)
        if (args.Contains("--run") || args.Contains("--steam-run"))
            return SteamRun(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// `--steam-run -- &lt;게임 실행 커맨드&gt;`: `--` 뒤가 Steam 의 %command%(게임 exe + 인자).
    /// 게임을 exe 경로로 라이브러리에서 찾아 모드 Apply → 게임을 자식 프로세스로 실행하고 종료 대기.
    /// (게임은 Steam 이 우리에게 준 환경변수를 상속 → 오버레이/플레이타임/DRM 정상)
    /// </summary>
    private static int SteamRun(string[] args)
    {
        int sep = Array.IndexOf(args, "--");
        if (sep < 0 || sep + 1 >= args.Length) return 1; // 게임 커맨드 없음
        var cmd = args[(sep + 1)..];
        var gameExe = cmd[0];

        // ★실행옵션 자동패치는 시간이 걸릴 수 있으므로, 이 모드 전용 스플래시 로딩바로 진행률을 보여준다.
        //   App 이 RunTask 를 감지하면 일반 UI 대신 스플래시만 띄우고, 패치가 끝나면 창을 닫고 종료 →
        //   여기로 복귀해 진짜 게임을 실행한다. (패치 실패해도 게임 실행은 막지 않음.)
        App.RunTask = async progress => await Task.Run(() => ApplyForGame(gameExe, progress));
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>()); }
        catch { /* never block launch */ }

        return LaunchGame(cmd);
    }

    /// <summary>패치 완료 후 진짜 게임을 자식 프로세스로 실행하고 종료 대기.</summary>
    private static int LaunchGame(string[] cmd)
    {
        var gameExe = cmd[0];
        try
        {
            var psi = new ProcessStartInfo(gameExe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(gameExe) ?? Environment.CurrentDirectory,
            };
            for (int i = 1; i < cmd.Length; i++) psi.ArgumentList.Add(cmd[i]);

            using var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode ?? 0;
        }
        catch { return 1; }
    }

    /// <summary>게임 exe 경로로 등록된 게임을 찾아 DebugMods 를 Apply. 실패는 조용히 무시. 진행률 보고(선택).</summary>
    private static void ApplyForGame(string gameExe, IProgress<PatchProgress>? progress = null)
    {
        try
        {
            progress?.Report(new PatchProgress(0, 0, "Locating game…"));
            var game = GameLibrary.Load().FirstOrDefault(g =>
                !string.IsNullOrEmpty(g.InstallPath) &&
                gameExe.StartsWith(g.InstallPath, StringComparison.OrdinalIgnoreCase));
            if (game is null) return;

            var ws = new GameWorkspace(game);
            progress?.Report(new PatchProgress(0, 0, "Loading mods…"));
            var gather = ModApplier.Gather(ws, game); // DebugMods + 호환 .ktmod

            // 켤 모드가 없고 이미 순정이면 할 일 없음. 그러나 순정이 아니면(이전 패치 잔존) Apply(빈 목록)로
            // 현재 원본으로 복구해야 한다 — 프로필에서 전부 비활성화한 채 실행하는 경우.
            if (gather.Replacements.Count == 0 && PatchEngine.GetStatus(ws) == PatchStatus.NotPatched)
                return;

            PatchEngine.Apply(ws, gather.Replacements, progress);
        }
        catch { /* never block launch */ }
    }
}
