using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Games;
using Kashira.Core.Mods;

namespace Kashira.Editor.ViewModels;

/// <summary>프로젝트 런처: 최근 프로젝트 / 새 프로젝트 생성 / 기존 프로젝트 열기.</summary>
public partial class ProjectLauncherViewModel : ViewModelBase
{
    public string Title => "Kashira Editor";
    public string Subtitle => "Create or open a .ktmod project";

    public ObservableCollection<EditorProjects.Recent> Recent { get; } = new();

    /// <summary>New Project 게임 드롭다운(매니저 저장 GameLibrary 우선 + 미설치 프로파일).</summary>
    public ObservableCollection<GameOption> GameOptions { get; } = new();

    /// <summary>폴더 선택기(New 위치). View 가 주입.</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>프로젝트 열기 선택기(폴더 또는 .ktproj). View 가 주입.</summary>
    public Func<Task<string?>>? ProjectPicker { get; set; }

    /// <summary>프로젝트 로드 완료 시 워크스페이스로 이동(셸이 주입).</summary>
    public Action<KtmodProject>? OpenProject { get; set; }

    [ObservableProperty] private string _newName = "My Costume Mod";
    [ObservableProperty] private string _newLocation = "";
    [ObservableProperty] private GameOption? _selectedGame;
    [ObservableProperty] private bool _hasInstalledGames;
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

        GameOptions.Clear();
        var options = GameCatalog.Options();
        foreach (var o in options) GameOptions.Add(o);
        HasInstalledGames = options.Any(o => o.Installed);
        // 저작(DOA6LR) 우선 선택, 없으면 첫 설치 게임, 그마저 없으면 첫 옵션
        SelectedGame = options.FirstOrDefault(o => o.IsAuthoring && o.Installed)
                       ?? options.FirstOrDefault(o => o.Installed)
                       ?? options.FirstOrDefault();
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
        if (SelectedGame is null) { Status = "타겟 게임을 선택하세요 (Manager 에서 게임을 추가/감지하세요)."; return; }
        try
        {
            System.IO.Directory.CreateDirectory(NewLocation);
            var now = DateTime.UtcNow.ToString("o");
            var project = KtmodProject.Create(NewLocation, NewName.Trim(), SelectedGame.Key, now);
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
