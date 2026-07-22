using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Kashira.Gui.Views;

/// <summary>ktmod 상세(순수 표시용) 창. DataContext = KtmodDetailViewModel.</summary>
public partial class KtmodDetailWindow : Window
{
    public KtmodDetailWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose(); // 갤러리 Bitmap 해제
    }
}
