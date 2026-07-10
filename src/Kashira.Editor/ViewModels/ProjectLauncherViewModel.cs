using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Mods;

namespace Kashira.Editor.ViewModels;

/// <summary>프로젝트 런처: 최근 프로젝트 / 새 프로젝트 생성 / 기존 프로젝트 열기.</summary>
public partial class ProjectLauncherViewModel : ViewModelBase
{
    public string Title => "Kashira Editor";
    public string Subtitle => "Create or open a .ktmod project";

    public ObservableCollection<EditorProjects.Recent> Recent { get; } = new();

    /// <summary>폴더 선택기(New 위치). View 가 주입.</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>프로젝트 열기 선택기(폴더 또는 .ktproj). View 가 주입.</summary>
    public Func<Task<string?>>? ProjectPicker { get; set; }

    /// <summary>프로젝트 로드 완료 시 워크스페이스로 이동(셸이 주입).</summary>
    public Action<KtmodProject>? OpenProject { get; set; }

    [ObservableProperty] private string _newName = "My Costume Mod";
    [ObservableProperty] private string _newLocation = "";
    [ObservableProperty] private string _newTargetGame = "DOA6LR";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasRecent;

    public void Initialize()
    {
        Recent.Clear();
        foreach (var r in EditorProjects.Load()) Recent.Add(r);
        HasRecent = Recent.Count > 0;
        if (string.IsNullOrWhiteSpace(NewLocation))
            NewLocation = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KashiraProjects");
    }

    [RelayCommand]
    private async Task BrowseLocation()
    {
        if (FolderPicker is null) return;
        var path = await FolderPicker();
        if (!string.IsNullOrEmpty(path)) NewLocation = path;
    }

    [RelayCommand]
    private void Create()
    {
        if (string.IsNullOrWhiteSpace(NewName)) { Status = "Enter a project name."; return; }
        if (string.IsNullOrWhiteSpace(NewLocation)) { Status = "Choose a location."; return; }
        if (string.IsNullOrWhiteSpace(NewTargetGame)) { Status = "Enter a target game."; return; }
        try
        {
            System.IO.Directory.CreateDirectory(NewLocation);
            var now = DateTime.UtcNow.ToString("o");
            var project = KtmodProject.Create(NewLocation, NewName.Trim(), NewTargetGame.Trim(), now);
            EditorProjects.Touch(project, now);
            OpenProject?.Invoke(project);
        }
        catch (Exception ex) { Status = $"Create failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task Open()
    {
        if (ProjectPicker is null) return;
        var path = await ProjectPicker();
        if (string.IsNullOrEmpty(path)) return;
        LoadFrom(path);
    }

    [RelayCommand]
    private void OpenRecent(EditorProjects.Recent? recent)
    {
        if (recent is not null) LoadFrom(recent.Path);
    }

    private void LoadFrom(string path)
    {
        try
        {
            var project = KtmodProject.Load(path);
            EditorProjects.Touch(project, DateTime.UtcNow.ToString("o"));
            OpenProject?.Invoke(project);
        }
        catch (Exception ex) { Status = $"Open failed: {ex.Message}"; }
    }
}
