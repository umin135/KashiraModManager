using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kashira.Gui.ViewModels;

namespace Kashira.Gui.Views;

public partial class MainWindow : Window
{
    // 뷰별 기본 창 크기 — 런처는 좁게, 게임 상세(모드 목록)는 넓게.
    private static readonly (double W, double H) LauncherSize = (720, 480);
    private static readonly (double W, double H) DetailSize = (880, 720);

    private INotifyPropertyChanged? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as INotifyPropertyChanged;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplySizeForCurrentPage();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage))
            ApplySizeForCurrentPage();
    }

    /// <summary>현재 페이지에 맞춰 창 크기를 조정한다(상세=넓게, 런처=기본). 사용자가 매번 늘릴 필요 없게.</summary>
    private void ApplySizeForCurrentPage()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var (w, h) = vm.CurrentPage is GameDetailViewModel ? DetailSize : LauncherSize;
        Width = w;
        Height = h;
    }

    /// <summary>KatanaEngine 게임 폴더 선택기. 취소 시 null.</summary>
    public async Task<string?> PickGameFolderAsync()
    {
        var top = GetTopLevel(this);
        if (top is null) return null;

        var result = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a KatanaEngine game folder",
            AllowMultiple = false,
        });

        return result.FirstOrDefault()?.TryGetLocalPath();
    }
}
