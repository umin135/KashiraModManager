using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kashira.Editor.ViewModels;

/// <summary>셸: 프로젝트 런처 ↔ 워크스페이스 네비게이션. CurrentPage 를 ContentControl 이 표시.</summary>
public partial class EditorShellViewModel : ViewModelBase
{
    private readonly ProjectLauncherViewModel _launcher;
    private Func<Task<string?>>? _folderPicker;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    /// <summary>View 가 확장자 필터 파일 선택기를 주입(g1m/grp 등). (title, ext) → 경로.</summary>
    public Func<string, string, Task<string?>>? FilePicker { get; set; }

    public EditorShellViewModel()
    {
        _launcher = new ProjectLauncherViewModel
        {
            OpenProject = project =>
                CurrentPage = new ProjectWorkspaceViewModel(project, GoToLauncher, FilePicker, _folderPicker),
        };
        _currentPage = _launcher;
    }

    /// <summary>View 가 폴더 선택기를 주입(New 위치 선택 + 번들 폴더 선택).</summary>
    public Func<Task<string?>>? FolderPicker
    {
        set { _launcher.FolderPicker = value; _folderPicker = value; }
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
