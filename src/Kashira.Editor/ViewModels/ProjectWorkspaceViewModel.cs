using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Mods;

namespace Kashira.Editor.ViewModels;

/// <summary>
/// 로드된 프로젝트 편집 화면. Content(파일관리자+메타 미리보기) / Content_Legacy(raw 목록+폴더열기) 두 모드.
/// 프로젝트 폴더를 FolderWatcher 로 감시해(디바운스) 파일 변경 시 자동 갱신. g1t 임포트/컴파일은 다음 단계.
/// </summary>
public partial class ProjectWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly KtmodProject _project;
    private readonly Action _back;
    private readonly Func<string, string, Task<string?>>? _filePicker;
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

    // "g1m으로 새 코스튬" 폼
    [ObservableProperty] private string _newSetName = "body";
    [ObservableProperty] private string _newTargetCostume = "";
    [ObservableProperty] private string _g1mPath = "";
    [ObservableProperty] private string _grpPath = "";

    public ProjectWorkspaceViewModel(KtmodProject project, Action back,
                                     Func<string, string, Task<string?>>? filePicker = null)
    {
        _project = project;
        _back = back;
        _filePicker = filePicker;
        Refresh();
        // 프로젝트 폴더(Content/Content_Legacy 포함) 감시 → 변경 시 UI 스레드에서 갱신
        _watcher = new FolderWatcher(_project.ProjectDir,
            () => Dispatcher.UIThread.Post(Refresh));
    }

    [RelayCommand]
    private async Task PickG1m()
    {
        if (_filePicker is null) return;
        var p = await _filePicker("Select a g1m mesh", "g1m");
        if (!string.IsNullOrEmpty(p)) G1mPath = p;
    }

    [RelayCommand]
    private async Task PickGrp()
    {
        if (_filePicker is null) return;
        var p = await _filePicker("Select the matching grp", "grp");
        if (!string.IsNullOrEmpty(p)) GrpPath = p;
    }

    [RelayCommand]
    private void GenerateCostume()
    {
        if (string.IsNullOrWhiteSpace(NewSetName)) { Status = "Enter a set name."; return; }
        if (string.IsNullOrWhiteSpace(NewTargetCostume)) { Status = "Enter a target costume (e.g. KAS_COS_006)."; return; }
        if (!File.Exists(G1mPath)) { Status = "Pick a g1m file."; return; }
        try
        {
            // grp 는 선택: 있으면 보존, 없으면 g1m 슬라이싱으로 단일 파츠(0x3057221F) 자동 생성
            byte[]? grp = File.Exists(GrpPath) ? File.ReadAllBytes(GrpPath) : null;
            int n = CostumeScaffold.GenerateFromG1m(_project, NewSetName.Trim(), NewTargetCostume.Trim(),
                File.ReadAllBytes(G1mPath), grp);
            Status = grp is null
                ? $"Created set '{NewSetName}' ({n} materials, grp auto-generated). Fill texture slots to customize."
                : $"Created set '{NewSetName}' ({n} materials). Fill texture slots to customize.";
            // 폴더 감시가 자동 갱신하지만 즉시 반영
            Refresh();
        }
        catch (Exception ex) { Status = $"Generate failed: {ex.Message}"; }
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
