using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kashira.Gui.Views;

public partial class GameDetailView : UserControl
{
    public GameDetailView()
    {
        InitializeComponent();
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            new AboutWindow().ShowDialog(owner);
    }
}
