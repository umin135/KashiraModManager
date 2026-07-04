using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kashira.Gui.ViewModels;

/// <summary>셸: 런처↔게임 상세 네비게이션을 담당. CurrentPage 를 ContentControl 이 표시.</summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly LauncherViewModel _launcher;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    public MainWindowViewModel()
    {
        _launcher = new LauncherViewModel
        {
            OpenGame = game => CurrentPage = new GameDetailViewModel(game, GoToLauncher),
        };
        _currentPage = _launcher;
    }

    /// <summary>View 가 폴더 선택기를 주입.</summary>
    public Func<Task<string?>>? FolderPicker
    {
        set => _launcher.FolderPicker = value;
    }

    public Task InitializeAsync() => _launcher.InitializeAsync();

    private void GoToLauncher() => CurrentPage = _launcher;
}
