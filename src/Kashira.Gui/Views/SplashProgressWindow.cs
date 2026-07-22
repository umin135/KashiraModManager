using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kashira.Core.Patching;

namespace Kashira.Gui.Views;

/// <summary>
/// 실행옵션(--run) 자동 패치 전용 스플래시 로딩바.
/// 테두리 없는 작은 창으로 패치 진행률을 표시하고, 끝나면 닫힌다(게임 실행 직전).
/// 코드비하인드로만 구성 — MainWindow 계열 XAML/뷰모델과 독립.
/// </summary>
public sealed class SplashProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;

    public SplashProgressWindow()
    {
        Title = "Kashira Mod Manager";
        Width = 440;
        Height = 132;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;      // 테두리 없는 스플래시(Avalonia 12)
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        Background = new SolidColorBrush(Color.Parse("#1e1e24"));

        var title = new TextBlock
        {
            Text = "Kashira — applying mods before launch",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
        };
        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Height = 8,
            IsIndeterminate = true,
        };
        _status = new TextBlock
        {
            Text = "Preparing…",
            Foreground = new SolidColorBrush(Color.Parse("#b0b0c0")),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#3a3a46")),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(22, 20),
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { title, _bar, _status },
            },
        };
    }

    /// <summary>진행률 갱신(항상 UI 스레드에서 반영).</summary>
    public void Update(PatchProgress p)
    {
        void Apply()
        {
            if (p.Fraction is { } f)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = f;
            }
            else
            {
                _bar.IsIndeterminate = true;
            }
            _status.Text = p.Message;
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }
}
