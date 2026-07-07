using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Kashira.Core.Games;
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

        // Steam 래핑 모드: 실행옵션 `"Kashira.exe" --steam-run -- %command%`
        // Steam 이 게임 실행 시 우리를 먼저 부름 → 모드 Apply 후 진짜 게임을 자식으로 실행.
        if (args.Contains("--steam-run"))
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

        ApplyForGame(gameExe); // best-effort — 실패해도 게임 실행은 막지 않음

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

    /// <summary>게임 exe 경로로 등록된 게임을 찾아 DebugMods 를 Apply. 실패는 조용히 무시.</summary>
    private static void ApplyForGame(string gameExe)
    {
        try
        {
            var game = GameLibrary.Load().FirstOrDefault(g =>
                !string.IsNullOrEmpty(g.InstallPath) &&
                gameExe.StartsWith(g.InstallPath, StringComparison.OrdinalIgnoreCase));
            if (game is null) return;

            var ws = new GameWorkspace(game);
            var files = DebugMods.List(ws.DebugModsDir);
            if (files.Count == 0) return;

            var reps = files
                .Select(f => new PatchEngine.Replacement(f.FileKtid, File.ReadAllBytes(f.FullPath), f.Ext))
                .ToList();
            PatchEngine.Apply(ws, reps);
        }
        catch { /* never block launch */ }
    }
}
