using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Kashira.Gui.ViewModels;
using Kashira.Gui.Views;

namespace Kashira.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            vm.FolderPicker = window.PickGameFolderAsync;
            desktop.MainWindow = window;
            _ = vm.InitializeAsync(); // 저장된 게임 목록 로드
        }

        base.OnFrameworkInitializationCompleted();
    }
}