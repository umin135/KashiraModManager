using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Mods;

namespace Kashira.Editor.ViewModels;

/// <summary>
/// 로드된 프로젝트 편집 화면. Content(파일관리자+메타 미리보기) / Content_Legacy(raw 목록+폴더열기) 두 모드.
/// g1t 임포트/컴파일 등 실제 저작 툴은 다음 단계.
/// </summary>
public partial class ProjectWorkspaceViewModel : ViewModelBase
{
    private readonly KtmodProject _project;
    private readonly Action _back;

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

    public ProjectWorkspaceViewModel(KtmodProject project, Action back)
    {
        _project = project;
        _back = back;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        ContentTree.Clear();
        foreach (var child in ProjectContent.ContentRoot(_project).Children) ContentTree.Add(child);

        LegacyFiles.Clear();
        foreach (var f in ProjectContent.ListLegacy(_project)) LegacyFiles.Add(f);
        HasLegacy = LegacyFiles.Count > 0;
    }

    partial void OnSelectedNodeChanged(ProjectContent.ContentNode? value)
    {
        Metadata.Clear();
        if (value is { IsDirectory: false })
            foreach (var line in ProjectContent.Inspect(value.FullPath)) Metadata.Add(line);
        HasMetadata = Metadata.Count > 0;
    }

    [RelayCommand]
    private void Back() => _back();

    [RelayCommand]
    private void OpenProjectFolder() => OpenInExplorer(_project.ProjectDir);

    [RelayCommand]
    private void OpenLegacyFolder()
    {
        System.IO.Directory.CreateDirectory(_project.ContentLegacyDir);
        OpenInExplorer(_project.ContentLegacyDir);
    }

    [RelayCommand]
    private void Build()
    {
        try
        {
            string output = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(_project.ProjectDir)!, _project.Name + ".ktmod");
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
}
