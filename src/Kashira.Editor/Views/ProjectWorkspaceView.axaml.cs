using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kashira.Editor.Views;

public partial class ProjectWorkspaceView : UserControl
{
    public ProjectWorkspaceView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
