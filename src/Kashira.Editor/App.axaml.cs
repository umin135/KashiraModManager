using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kashira.Editor.ViewModels;
using Kashira.Editor.Views;

namespace Kashira.Editor;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new EditorShellViewModel();
            var window = new MainWindow { DataContext = shell };
            shell.FolderPicker = window.PickFolderAsync;
            shell.ProjectPicker = window.PickProjectAsync;
            shell.Initialize();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
