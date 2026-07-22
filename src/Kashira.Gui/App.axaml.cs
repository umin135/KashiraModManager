using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Kashira.Core.Patching;
using Kashira.Gui.ViewModels;
using Kashira.Gui.Views;

namespace Kashira.Gui;

public partial class App : Application
{
    /// <summary>
    /// 실행옵션(--run) 모드에서 설정된다. 설정되면 App 은 일반 UI 대신 스플래시 로딩바를 띄우고
    /// 이 작업(패치)을 진행률과 함께 실행한 뒤, 끝나면 창을 닫고 종료한다(→ 게임 실행으로 복귀).
    /// </summary>
    public static Func<IProgress<PatchProgress>, Task>? RunTask;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (RunTask is not null)
            {
                // 실행옵션 자동패치 전용 — 스플래시 로딩바만 띄우고 끝나면 종료.
                var splash = new SplashProgressWindow();
                desktop.MainWindow = splash;
                _ = RunModeAsync(splash, desktop);
            }
            else
            {
                var vm = new MainWindowViewModel();
                var window = new MainWindow { DataContext = vm };
                vm.FolderPicker = window.PickGameFolderAsync;
                desktop.MainWindow = window;
                _ = vm.InitializeAsync(); // 저장된 게임 목록 로드
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunModeAsync(SplashProgressWindow splash, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var progress = new Progress<PatchProgress>(splash.Update); // UI 스레드로 마샬링
        try { await RunTask!(progress); }
        catch { /* never block launch */ }
        splash.Close();
        desktop.Shutdown();
    }
}