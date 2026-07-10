using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Games;
using Kashira.Core.Mods;

namespace Kashira.Editor.ViewModels;

/// <summary>
/// 로드된 프로젝트 편집 화면. Content(파일관리자+메타 미리보기) / Content_Legacy(raw 목록+폴더열기) 두 모드.
/// 프로젝트 폴더를 FolderWatcher 로 감시해(디바운스) 파일 변경 시 자동 갱신.
/// Content 저작 진입점 = Kissaki 번들 임포트(g1m/g1t/mtl/grp 를 연결 해석해 완성된 매니페스트 생성).
/// </summary>
public partial class ProjectWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly KtmodProject _project;
    private readonly Action _back;
    private readonly Func<string, string, Task<string?>>? _filePicker;
    private readonly Func<Task<string?>>? _folderPicker;
    private readonly FolderWatcher _watcher;
    private string? _lastInspectedPath;

    public string Name => _project.Name;
    public string TargetGame => _project.TargetGame;
    public string ProjectDir => _project.ProjectDir;

    // Content 탭
    public ObservableCollection<ProjectContent.ContentNode> ContentTree { get; } = new();
    [ObservableProperty] private ProjectContent.ContentNode? _selectedNode;
    public ObservableCollection<ProjectContent.MetaLine> Metadata { get; } = new();
    [ObservableProperty] private bool _hasMetadata;

    // Content_Legacy 탭
    public ObservableCollection<ProjectContent.LegacyFile> LegacyFiles { get; } = new();
    [ObservableProperty] private bool _hasLegacy;

    [ObservableProperty] private string _status = "";

    // "Kissaki 번들 임포트" 폼
    [ObservableProperty] private string _newSetName = "body";
    [ObservableProperty] private string _newTargetCostume = "";
    [ObservableProperty] private string _bundleDir = "";

    public ProjectWorkspaceViewModel(KtmodProject project, Action back,
                                     Func<string, string, Task<string?>>? filePicker = null,
                                     Func<Task<string?>>? folderPicker = null)
    {
        _project = project;
        _back = back;
        _filePicker = filePicker;
        _folderPicker = folderPicker;
        Refresh();
        // 프로젝트 폴더(Content/Content_Legacy 포함) 감시 → 변경 시 UI 스레드에서 갱신
        _watcher = new FolderWatcher(_project.ProjectDir,
            () => Dispatcher.UIThread.Post(Refresh));
    }

    [RelayCommand]
    private async Task PickBundle()
    {
        if (_folderPicker is null) return;
        var p = await _folderPicker();
        if (!string.IsNullOrEmpty(p)) BundleDir = p;
    }

    [RelayCommand]
    private void ImportBundle()
    {
        if (string.IsNullOrWhiteSpace(NewSetName)) { Status = "세트 이름을 입력하세요."; return; }
        if (!Directory.Exists(BundleDir)) { Status = "Kissaki 번들 폴더를 선택하세요."; return; }
        var ws = ResolveWorkspace();
        if (ws is null)
        {
            Status = $"게임 '{_project.TargetGame}' 을(를) 찾을 수 없음 — Manager 에서 게임을 먼저 설정/감지하세요.";
            return;
        }
        try
        {
            var r = BundleImporter.Import(ws, _project, NewSetName.Trim(), BundleDir, NewTargetCostume.Trim());
            Status = $"임포트 완료: 소스 {r.SourceCostume} → 재질 {r.Materials} · 변형 {r.Variations} · 텍스처 {r.Textures}"
                     + (r.MissingG1t > 0 ? $" (번들에 없는 g1t {r.MissingG1t}개 — HashByName 미매칭)" : "");
            Refresh();
        }
        catch (Exception ex) { Status = $"임포트 실패: {ex.Message}"; }
    }

    /// <summary>프로젝트의 TargetGame(예: DOA6LR)에 맞는 설치 게임을 GameLibrary 에서 찾아 워크스페이스 구성.</summary>
    private GameWorkspace? ResolveWorkspace()
    {
        var games = GameLibrary.Load();
        var game = games.FirstOrDefault(g => g.ExePath is not null
                       && string.Equals(Path.GetFileNameWithoutExtension(g.ExePath), _project.TargetGame,
                                         StringComparison.OrdinalIgnoreCase))
                   ?? games.FirstOrDefault(g => string.Equals(g.ProfileId, _project.TargetGame,
                                         StringComparison.OrdinalIgnoreCase));
        return game is null ? null : new GameWorkspace(game);
    }

    [RelayCommand]
    private void Refresh()
    {
        ContentTree.Clear();
        foreach (var child in ProjectContent.ContentRoot(_project).Children) ContentTree.Add(child);

        LegacyFiles.Clear();
        foreach (var f in ProjectContent.ListLegacy(_project)) LegacyFiles.Add(f);
        HasLegacy = LegacyFiles.Count > 0;

        // 트리 재빌드로 선택이 풀려도 마지막 미리보기는 유지(파일이 아직 있으면)
        if (_lastInspectedPath is not null && File.Exists(_lastInspectedPath))
            PopulateMetadata(_lastInspectedPath);
        else
            ClearMetadata();
    }

    partial void OnSelectedNodeChanged(ProjectContent.ContentNode? value)
    {
        if (value is { IsDirectory: false })
        {
            _lastInspectedPath = value.FullPath;
            PopulateMetadata(value.FullPath);
        }
    }

    private void PopulateMetadata(string path)
    {
        Metadata.Clear();
        foreach (var line in ProjectContent.Inspect(path)) Metadata.Add(line);
        HasMetadata = Metadata.Count > 0;
    }

    private void ClearMetadata()
    {
        Metadata.Clear();
        HasMetadata = false;
    }

    [RelayCommand]
    private void Back()
    {
        Dispose();
        _back();
    }

    [RelayCommand]
    private void OpenProjectFolder() => OpenInExplorer(_project.ProjectDir);

    [RelayCommand]
    private void OpenLegacyFolder()
    {
        Directory.CreateDirectory(_project.ContentLegacyDir);
        OpenInExplorer(_project.ContentLegacyDir);
    }

    [RelayCommand]
    private void Build()
    {
        try
        {
            string output = Path.Combine(
                Path.GetDirectoryName(_project.ProjectDir)!, _project.Name + ".ktmod");
            _project.Build(output);
            Status = $"빌드 완료: {output}";
        }
        catch (Exception ex) { Status = $"빌드 실패: {ex.Message}"; }
    }

    private void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { Status = $"폴더 열기 실패: {ex.Message}"; }
    }

    public void Dispose() => _watcher.Dispose();
}
