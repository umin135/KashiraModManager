using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Games;
using Kashira.Core.Mods;
using Kashira.Editor.Services;

namespace Kashira.Editor.ViewModels;

/// <summary>
/// 로드된 프로젝트 편집 화면 — 언리얼(UE) 스타일 도킹 셸.
/// 좌: Outliner(통합 트리 Content+Content_Legacy) · 중앙: Editor(재질 그리드/임포트) · 우: Details(선택 메타)
/// 하: Content Browser(통합 에셋 목록). 프로젝트 폴더를 FolderWatcher 로 감시해 변경 시 자동 갱신.
/// 게임 게이팅: DOA6LR = 저작 파이프라인, 그 외 = Content_Legacy raw redirect 전용(GameCatalog.IsAuthoringTarget).
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

    /// <summary>DOA6LR 타겟 = 저작(재질/변형/카테고리) 활성. 아니면 Legacy 전용.</summary>
    public bool IsAuthoringGame { get; }
    public bool IsLegacyOnly => !IsAuthoringGame;
    public string GameModeText => IsAuthoringGame ? "저작 모드" : "Legacy redirect 전용";

    // Outliner: 통합 프로젝트 트리(Content + Content_Legacy 루트)
    public ObservableCollection<ProjectContent.ContentNode> OutlinerRoots { get; } = new();
    [ObservableProperty] private ProjectContent.ContentNode? _selectedNode;

    // Content Browser(하단): 통합 에셋 평면 목록
    public ObservableCollection<AssetItem> BrowserAssets { get; } = new();
    [ObservableProperty] private AssetItem? _selectedAsset;
    [ObservableProperty] private bool _hasAssets;

    // Details(우): 선택 항목 메타
    public ObservableCollection<ProjectContent.MetaLine> Metadata { get; } = new();
    [ObservableProperty] private bool _hasMetadata;
    [ObservableProperty] private string _selectedName = "(선택 없음)";

    // 중앙 뷰포트 텍스처 프리뷰(g1t 디코드)
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _hasPreview;

    // 중앙 뷰포트 재질 그리드(코스튬 매니페스트 선택 시)
    public ObservableCollection<GridMaterialVM> GridMaterials { get; } = new();
    [ObservableProperty] private GridMaterialVM? _selectedGridMaterial;
    [ObservableProperty] private bool _hasGrid;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _gameStatus = "";

    // "Kissaki 번들 임포트" 폼(저작 게임에서만)
    [ObservableProperty] private string _newSetName = "body";
    [ObservableProperty] private string _newTargetCostume = "";
    [ObservableProperty] private string _bundleDir = "";

    /// <summary>Content Browser 한 행(에셋 파일). Root = "Content"/"Content_Legacy".</summary>
    public sealed record AssetItem(string Name, string FullPath, string Ext, string Size, string Root);

    public ProjectWorkspaceViewModel(KtmodProject project, Action back,
                                     Func<string, string, Task<string?>>? filePicker = null,
                                     Func<Task<string?>>? folderPicker = null)
    {
        _project = project;
        _back = back;
        _filePicker = filePicker;
        _folderPicker = folderPicker;
        IsAuthoringGame = GameCatalog.IsAuthoringTarget(project.TargetGame);
        Refresh();
        UpdateGameStatus();
        // 프로젝트 폴더(Content/Content_Legacy 포함) 감시 → 변경 시 UI 스레드에서 갱신
        _watcher = new FolderWatcher(_project.ProjectDir,
            () => Dispatcher.UIThread.Post(Refresh));
    }

    private void UpdateGameStatus()
    {
        var ws = ResolveWorkspace();
        GameStatus = ws is null
            ? $"게임 '{_project.TargetGame}' 미연결 — Manager 에서 게임을 추가/감지하세요."
            : $"게임 연결됨: {ResolveDisplayName()} · {GameModeText}";
    }

    private string ResolveDisplayName()
    {
        var g = GameLibrary.Load().FirstOrDefault(x => GameCatalog.Matches(x, _project.TargetGame));
        return g?.DisplayName ?? _project.TargetGame;
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
        if (!IsAuthoringGame) { Status = "이 게임은 저작을 지원하지 않습니다(Content_Legacy 전용)."; return; }
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
        var game = GameLibrary.Load().FirstOrDefault(g => GameCatalog.Matches(g, _project.TargetGame));
        return game is null ? null : new GameWorkspace(game);
    }

    [RelayCommand]
    private void Refresh()
    {
        // Outliner 통합 트리: 저작 게임이면 Content 우선(+ Legacy 있으면 표시), 아니면 Legacy 만.
        OutlinerRoots.Clear();
        if (IsAuthoringGame)
        {
            OutlinerRoots.Add(ProjectContent.ContentRoot(_project));
            var legacy = ProjectContent.LegacyRoot(_project);
            if (legacy.Children.Count > 0) OutlinerRoots.Add(legacy);
        }
        else
        {
            OutlinerRoots.Add(ProjectContent.LegacyRoot(_project));
        }

        // Content Browser 평면 목록
        BrowserAssets.Clear();
        if (IsAuthoringGame) AddAssets(_project.ContentDir, "Content");
        AddAssets(_project.ContentLegacyDir, "Content_Legacy");
        HasAssets = BrowserAssets.Count > 0;

        // 트리 재빌드로 선택이 풀려도 마지막 미리보기는 유지(파일이 아직 있으면)
        if (_lastInspectedPath is not null && File.Exists(_lastInspectedPath))
            PopulateMetadata(_lastInspectedPath);
        else
            ClearMetadata();
    }

    private void AddAssets(string dir, string root)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            BrowserAssets.Add(new AssetItem(Path.GetFileName(path), path,
                ext.Length == 0 ? "" : ext, HumanSize(new FileInfo(path).Length), root));
        }
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };

    partial void OnSelectedNodeChanged(ProjectContent.ContentNode? value)
    {
        if (value is { IsDirectory: false }) Select(value.FullPath);
    }

    partial void OnSelectedAssetChanged(AssetItem? value)
    {
        if (value is not null) Select(value.FullPath);
    }

    private void Select(string path)
    {
        _lastInspectedPath = path;
        SelectedName = Path.GetFileName(path);
        PopulateMetadata(path);
        LoadViewport(path);
    }

    /// <summary>선택 파일에 따라 중앙 뷰포트를 구성: 코스튬 매니페스트 → 재질 그리드, g1t → 텍스처 프리뷰.</summary>
    private void LoadViewport(string path)
    {
        ClearViewport();
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "json" && TryBuildGrid(path)) return;
        if (ext == "g1t")
        {
            PreviewImage = TexturePreview.FromG1t(path);
            HasPreview = PreviewImage is not null;
        }
    }

    private bool TryBuildGrid(string manifestPath)
    {
        CostumeGrid.Model? model;
        try { model = CostumeGrid.Build(_project, manifestPath); }
        catch { model = null; }
        if (model is null) return false;

        GridMaterials.Clear();
        if (model.BaseForm is not null) GridMaterials.Add(new GridMaterialVM(model.BaseForm));
        foreach (var m in model.Materials) GridMaterials.Add(new GridMaterialVM(m));
        SelectedGridMaterial = GridMaterials.Count > 0 ? GridMaterials[0] : null;
        HasGrid = GridMaterials.Count > 0;
        return HasGrid;
    }

    private void ClearViewport()
    {
        PreviewImage?.Dispose();
        PreviewImage = null;
        HasPreview = false;
        GridMaterials.Clear();
        SelectedGridMaterial = null;
        HasGrid = false;
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
        SelectedName = "(선택 없음)";
        ClearViewport();
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
