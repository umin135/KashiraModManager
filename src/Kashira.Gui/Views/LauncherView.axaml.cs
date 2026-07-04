using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kashira.Gui.Views;

public partial class LauncherView : UserControl
{
    public LauncherView()
    {
        InitializeComponent();
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            new AboutWindow().ShowDialog(owner);
    }
}
