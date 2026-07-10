using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Kashira.Core.Mods;
using Kashira.Editor.ViewModels;

namespace Kashira.Editor.Views;

public partial class ProjectLauncherView : UserControl
{
    public ProjectLauncherView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ProjectLauncherViewModel vm
            && this.FindControl<ListBox>("RecentList")?.SelectedItem is EditorProjects.Recent recent)
        {
            vm.OpenRecentCommand.Execute(recent);
        }
    }
}
