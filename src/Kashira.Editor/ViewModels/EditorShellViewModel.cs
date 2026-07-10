using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kashira.Editor.ViewModels;

/// <summary>셸: 프로젝트 런처 ↔ 워크스페이스 네비게이션. CurrentPage 를 ContentControl 이 표시.</summary>
public partial class EditorShellViewModel : ViewModelBase
{
    private readonly ProjectLauncherViewModel _launcher;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    public EditorShellViewModel()
    {
        _launcher = new ProjectLauncherViewModel
        {
            OpenProject = project => CurrentPage = new ProjectWorkspaceViewModel(project, GoToLauncher),
        };
        _currentPage = _launcher;
    }

    /// <summary>View 가 폴더 선택기를 주입(New 위치 선택).</summary>
    public Func<Task<string?>>? FolderPicker
    {
        set => _launcher.FolderPicker = value;
    }

    /// <summary>View 가 프로젝트 열기 선택기를 주입(폴더 또는 .ktproj).</summary>
    public Func<Task<string?>>? ProjectPicker
    {
        set => _launcher.ProjectPicker = value;
    }

    public void Initialize() => _launcher.Initialize();

    private void GoToLauncher()
    {
        _launcher.Initialize();
        CurrentPage = _launcher;
    }
}
