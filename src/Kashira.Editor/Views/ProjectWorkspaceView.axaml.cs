using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Kashira.Editor.ViewModels;

namespace Kashira.Editor.Views;

public partial class ProjectWorkspaceView : UserControl
{
    public ProjectWorkspaceView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>재질 그리드 셀 클릭 → VM 에 셀 선택 통지.</summary>
    private void OnCellClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is GridCellVM cell &&
            DataContext is ProjectWorkspaceViewModel vm)
            vm.SelectCell(cell);
    }

    /// <summary>Content Browser 그리드 더블클릭 → 폴더 진입 / 에셋 선택.</summary>
    private void OnBrowserActivate(object? sender, TappedEventArgs e)
    {
        if (DataContext is ProjectWorkspaceViewModel vm)
            vm.ActivateBrowserItem(vm.SelectedBrowserItem);
    }
}
