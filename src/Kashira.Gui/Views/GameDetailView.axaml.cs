using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kashira.Gui.ViewModels;

namespace Kashira.Gui.Views;

public partial class GameDetailView : UserControl
{
    public GameDetailView()
    {
        InitializeComponent();
    }

    /// <summary>Mods 목록에서 항목 더블클릭 → ktmod 상세(표시 전용) 창을 연다. 동작에는 영향 없음.</summary>
    private void OnModDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ModRowVM row } && TopLevel.GetTopLevel(this) is Window owner)
            new KtmodDetailWindow { DataContext = row.CreateDetail() }.Show(owner);
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            new AboutWindow().ShowDialog(owner);
    }

    private async void OnCopySteamClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GameDetailViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.SteamLaunchCommand);
    }
}
