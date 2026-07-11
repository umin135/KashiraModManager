using System;
using System.Collections.Generic;
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
/// 로드된 프로젝트 편집 화면 — 언리얼식 셸.
/// Content Browser(하단): 좌 폴더트리 + 우 폴더-직속 항목 그리드. 에셋 선택 → CurrentAsset.
/// Outliner(좌): CurrentAsset 의 "목차"(섹션). Details(우): 선택 섹션의 상세. Viewport(중앙): 프리뷰/재질 그리드.
/// 게이팅: DOA6LR = Content + Content_Legacy 동시. 비-DOA6LR = Content_Legacy 만(저작 비활성).
/// </summary>
public partial class ProjectWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly KtmodProject _project;
    private readonly Action _back;
    private readonly Func<string, string, Task<string?>>? _filePicker;
    private readonly Func<Task<string?>>? _folderPicker;
    private readonly Func<Task<string?>>? _projectPicker;
    private readonly Action<KtmodProject>? _openProject;
    private readonly FolderWatcher _watcher;

    public string Name => _project.Name;
    public string TargetGame => _project.TargetGame;
    public string ProjectDir => _project.ProjectDir;
    public bool IsAuthoringGame { get; }
    public bool IsLegacyOnly => !IsAuthoringGame;
    public string GameModeText => IsAuthoringGame ? "저작 모드" : "Legacy 전용";

    // ── Content Browser ───────────────────────────────────────
    public ObservableCollection<ProjectContent.ContentNode> FolderTree { get; } = new();
    [ObservableProperty] private ProjectContent.ContentNode? _selectedFolder;
    public ObservableCollection<BrowserItemVM> FolderItems { get; } = new();
    [ObservableProperty] private BrowserItemVM? _selectedBrowserItem;

    // ── Outliner (목차) ───────────────────────────────────────
    public ObservableCollection<AssetSectionVM> OutlinerSections { get; } = new();
    [ObservableProperty] private AssetSectionVM? _selectedSection;
    [ObservableProperty] private string _currentAssetName = "(선택 없음)";
    private string? _currentAssetPath;

    // ── Details ───────────────────────────────────────────────
    public ObservableCollection<ProjectContent.MetaLine> Details { get; } = new();
    [ObservableProperty] private bool _hasDetails;

    // ── Viewport: 텍스처 프리뷰 / 재질 그리드 ──────────────────
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _hasPreview;
    private readonly List<GridMaterialVM> _gridMaterials = new();
    [ObservableProperty] private GridMaterialVM? _selectedGridMaterial;
    [ObservableProperty] private bool _hasGrid;
    private string? _currentManifestPath;

    // 그리드 셀 편집
    [ObservableProperty] private GridCellVM? _selectedCell;
    [ObservableProperty] private string _selectedCellInfo = "셀을 클릭해 선택하세요.";

    // ── 상태/게임 ─────────────────────────────────────────────
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _gameStatus = "";

    // ── Import Kissaki bundle (저작) ──────────────────────────
    [ObservableProperty] private string _newSetName = "body";
    [ObservableProperty] private string _newTargetCostume = "";
    [ObservableProperty] private string _bundleDir = "";

    // ── File 메뉴: 최근 프로젝트 ──────────────────────────────
    public ObservableCollection<EditorProjects.Recent> Recent { get; } = new();

    public ProjectWorkspaceViewModel(KtmodProject project, Action back,
                                     Func<string, string, Task<string?>>? filePicker = null,
                                     Func<Task<string?>>? folderPicker = null,
                                     Func<Task<string?>>? projectPicker = null,
                                     Action<KtmodProject>? openProject = null)
    {
        _project = project;
        _back = back;
        _filePicker = filePicker;
        _folderPicker = folderPicker;
        _projectPicker = projectPicker;
        _openProject = openProject;
        IsAuthoringGame = GameCatalog.IsAuthoringTarget(project.TargetGame);
        LoadRecent();
        Refresh();
        UpdateGameStatus();
        _watcher = new FolderWatcher(_project.ProjectDir, () => Dispatcher.UIThread.Post(Refresh));
    }

    private void LoadRecent()
    {
        Recent.Clear();
        foreach (var r in EditorProjects.Load()) Recent.Add(r);
    }

    private void UpdateGameStatus()
    {
        var g = GameLibrary.Load().FirstOrDefault(x => GameCatalog.Matches(x, _project.TargetGame));
        GameStatus = g is null
            ? $"게임 '{_project.TargetGame}' 미연결 — Manager 에서 추가/감지"
            : $"{g.DisplayName} · {GameModeText}";
    }

    // ── Content Browser: 폴더 트리/그리드 ─────────────────────

    [RelayCommand]
    private void Refresh()
    {
        string? keepFolder = SelectedFolder?.FullPath;
        FolderTree.Clear();
        foreach (var root in ProjectContent.FolderRoots(_project, content: IsAuthoringGame, legacy: true))
            FolderTree.Add(root);

        var target = keepFolder is not null ? FindNode(FolderTree, keepFolder) : null;
        SelectedFolder = target ?? FolderTree.FirstOrDefault();
        RebuildFolderItems();

        if (_currentAssetPath is not null && File.Exists(_currentAssetPath))
            SetCurrentAsset(_currentAssetPath);
        else
            ClearCurrentAsset();
    }

    partial void OnSelectedFolderChanged(ProjectContent.ContentNode? value) => RebuildFolderItems();

    private void RebuildFolderItems()
    {
        FolderItems.Clear();
        if (SelectedFolder is null) return;
        foreach (var node in ProjectContent.FolderItems(SelectedFolder.FullPath))
            FolderItems.Add(new BrowserItemVM(node));
    }

    /// <summary>그리드 항목 활성화(더블클릭): 폴더 진입 / 파일을 목차·디테일·뷰포트에 로드. 단일 클릭은 하이라이트만.</summary>
    public void ActivateBrowserItem(BrowserItemVM? item)
    {
        if (item is null) return;
        if (item.IsDirectory)
        {
            var node = FindNode(FolderTree, item.FullPath);
            if (node is not null) SelectedFolder = node;
        }
        else SetCurrentAsset(item.FullPath);
    }

    private static ProjectContent.ContentNode? FindNode(IEnumerable<ProjectContent.ContentNode> nodes, string fullPath)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return n;
            var hit = FindNode(n.Children, fullPath);
            if (hit is not null) return hit;
        }
        return null;
    }

    // ── CurrentAsset → Outliner 목차 + Viewport ───────────────

    private void SetCurrentAsset(string path)
    {
        _currentAssetPath = path;
        CurrentAssetName = Path.GetFileName(path);
        PreviewImage?.Dispose();
        PreviewImage = null; HasPreview = false;
        HasGrid = false; SelectedGridMaterial = null; _gridMaterials.Clear();
        _currentManifestPath = null; SelectedCell = null; SelectedCellInfo = "셀을 클릭해 선택하세요.";
        OutlinerSections.Clear();

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "json" && TryBuildManifestSections(path)) { }
        else if (ext == "g1t")
        {
            PreviewImage = TexturePreview.FromG1t(path);
            OutlinerSections.Add(new AssetSectionVM("Texture", "texture", ProjectContent.Inspect(path)));
        }
        else if (ext == "g1m")
            OutlinerSections.Add(new AssetSectionVM("Mesh info", "mesh", ProjectContent.Inspect(path)));
        else
            OutlinerSections.Add(new AssetSectionVM("File info", "file", ProjectContent.Inspect(path)));

        SelectedSection = OutlinerSections.FirstOrDefault();
    }

    private bool TryBuildManifestSections(string path)
    {
        CostumeGrid.Model? model;
        try { model = CostumeGrid.Build(_project, path); } catch { model = null; }
        if (model is null) return false;

        _currentManifestPath = path;
        _gridMaterials.Clear();
        if (model.BaseForm is not null) _gridMaterials.Add(new GridMaterialVM(model.BaseForm));
        foreach (var m in model.Materials) _gridMaterials.Add(new GridMaterialVM(m));

        foreach (var gm in _gridMaterials)
            OutlinerSections.Add(new AssetSectionVM(gm.Name, "material", MaterialDetails(gm), gm));
        OutlinerSections.Add(new AssetSectionVM("Manifest", "file", ProjectContent.Inspect(path)));
        return _gridMaterials.Count > 0;
    }

    private static List<ProjectContent.MetaLine> MaterialDetails(GridMaterialVM gm)
    {
        var l = new List<ProjectContent.MetaLine>
        {
            new("Kind", gm.MaterialIndex < 0 ? "Base form" : $"Material {gm.MaterialIndex}"),
            new("Variations", gm.ColumnHeaders.Count.ToString()),
            new("Categories", gm.Rows.Count.ToString()),
        };
        if (gm.MissingAlbedo) l.Add(new("경고", "⚠ albedo 누락"));
        return l;
    }

    partial void OnSelectedSectionChanged(AssetSectionVM? value)
    {
        Details.Clear();
        if (value is not null) foreach (var line in value.Details) Details.Add(line);
        HasDetails = Details.Count > 0;

        if (value?.Material is { } gm)
        {
            SelectedGridMaterial = gm;
            HasGrid = true; HasPreview = false;
        }
        else
        {
            HasGrid = false;
            HasPreview = PreviewImage is not null;
        }
    }

    private void ClearCurrentAsset()
    {
        _currentAssetPath = null; _currentManifestPath = null;
        CurrentAssetName = "(선택 없음)";
        OutlinerSections.Clear(); SelectedSection = null;
        Details.Clear(); HasDetails = false;
        PreviewImage?.Dispose(); PreviewImage = null; HasPreview = false;
        HasGrid = false; SelectedGridMaterial = null; _gridMaterials.Clear();
    }

    // ── 그리드 셀 편집 ────────────────────────────────────────

    public void SelectCell(GridCellVM? cell)
    {
        if (SelectedCell is not null) SelectedCell.IsSelected = false;
        SelectedCell = cell;
        if (cell is null) { SelectedCellInfo = "셀을 클릭해 선택하세요."; return; }
        cell.IsSelected = true;
        string mat = cell.MaterialIndex < 0 ? "Base" : $"Material {cell.MaterialIndex}";
        string col = cell.MaterialIndex < 0 ? "base" : $"var{cell.Column}";
        SelectedCellInfo = $"{mat} · {col} · {cell.Role} (cat{cell.Category})";
    }

    [RelayCommand]
    private void AssignSelectedCell()
    {
        if (_currentManifestPath is null || SelectedCell is null) { Status = "먼저 그리드 셀을 선택하세요."; return; }
        if (SelectedBrowserItem is null || !SelectedBrowserItem.FullPath.EndsWith(".g1t", StringComparison.OrdinalIgnoreCase))
        { Status = "Content 브라우저에서 배정할 g1t 를 먼저 선택하세요."; return; }
        var cell = SelectedCell;
        string atRef = "@" + Path.GetFileName(SelectedBrowserItem.FullPath);
        try
        {
            if (cell.MaterialIndex < 0)
                CostumeManifestEditor.SetBaseSlot(_currentManifestPath, cell.Category, atRef);
            else
                CostumeManifestEditor.SetMaterialSlot(_currentManifestPath, cell.MaterialIndex, cell.Column, cell.Category, atRef);
            RebuildAfterEdit(cell);
            Status = $"배정: {cell.Role} ← {Path.GetFileName(SelectedBrowserItem.FullPath)}";
        }
        catch (Exception ex) { Status = $"배정 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private void ClearSelectedCell()
    {
        if (_currentManifestPath is null || SelectedCell is null) { Status = "먼저 그리드 셀을 선택하세요."; return; }
        var cell = SelectedCell;
        if (cell.Inherited) { Status = "상속 셀은 지울 것이 없습니다."; return; }
        try
        {
            if (cell.MaterialIndex < 0)
                CostumeManifestEditor.SetBaseSlot(_currentManifestPath, cell.Category, null);
            else
                CostumeManifestEditor.SetMaterialSlot(_currentManifestPath, cell.MaterialIndex, cell.Column, cell.Category, null);
            RebuildAfterEdit(cell);
            Status = $"지움: {cell.Role}";
        }
        catch (Exception ex) { Status = $"지우기 실패: {ex.Message}"; }
    }

    private void RebuildAfterEdit(GridCellVM edited)
    {
        int mat = edited.MaterialIndex, col = edited.Column, cat = edited.Category;
        if (_currentManifestPath is null) return;
        SetCurrentAsset(_currentManifestPath);
        // 같은 재질 섹션 + 셀 재선택
        var section = OutlinerSections.FirstOrDefault(s => s.Material?.MaterialIndex == mat);
        if (section is not null) SelectedSection = section;
        var reCell = SelectedGridMaterial?.Rows.FirstOrDefault(r => r.Category == cat)?.Cells.FirstOrDefault(c => c.Column == col);
        SelectCell(reCell);
    }

    // ── 텍스처 임포트/익스포트 ────────────────────────────────

    private string? SelectedG1tPath =>
        _currentAssetPath is { } p && Path.GetExtension(p).Equals(".g1t", StringComparison.OrdinalIgnoreCase) ? p : null;

    [RelayCommand]
    private void ExportDds()
    {
        if (SelectedG1tPath is not { } p) return;
        try { Status = $"DDS 내보냄: {Path.GetFileName(TextureIo.ExportDds(p))}"; Refresh(); }
        catch (Exception ex) { Status = $"DDS 내보내기 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private void ExportTga()
    {
        if (SelectedG1tPath is not { } p) return;
        try { Status = $"TGA 내보냄: {Path.GetFileName(TextureIo.ExportTga(p))}"; Refresh(); }
        catch (Exception ex) { Status = $"TGA 내보내기 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ReplaceTexture()
    {
        if (SelectedG1tPath is not { } p) return;
        if (_filePicker is null) { Status = "파일 선택기를 사용할 수 없습니다."; return; }
        var img = await _filePicker("교체할 텍스처 선택 (dds/tga)", "dds,tga");
        if (string.IsNullOrEmpty(img)) return;
        try
        {
            TextureIo.ReplaceFromFile(p, img);
            TexturePreview.ClearCache();
            SetCurrentAsset(p);
            Status = $"교체 완료: {Path.GetFileName(p)} ← {Path.GetFileName(img)}";
        }
        catch (Exception ex) { Status = $"교체 실패: {ex.Message}"; }
    }

    // ── Import Kissaki bundle ─────────────────────────────────

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
        var game = GameLibrary.Load().FirstOrDefault(g => GameCatalog.Matches(g, _project.TargetGame));
        if (game is null) { Status = $"게임 '{_project.TargetGame}' 미연결 — Manager 에서 설정/감지하세요."; return; }
        try
        {
            var r = BundleImporter.Import(new GameWorkspace(game), _project, NewSetName.Trim(), BundleDir, NewTargetCostume.Trim());
            Status = $"임포트 완료: 소스 {r.SourceCostume} → 재질 {r.Materials} · 변형 {r.Variations} · 텍스처 {r.Textures}"
                     + (r.MissingG1t > 0 ? $" (미매칭 g1t {r.MissingG1t})" : "");
            Refresh();
        }
        catch (Exception ex) { Status = $"임포트 실패: {ex.Message}"; }
    }

    // ── 빌드/폴더/파일메뉴 ────────────────────────────────────

    /// <summary>빌드 → 연결된 게임의 _Kashira/Mods 에 .ktmod 생성. 미연결 시 프로젝트 폴더 옆에.</summary>
    [RelayCommand]
    private async Task Build()
    {
        var game = GameLibrary.Load().FirstOrDefault(g => GameCatalog.Matches(g, _project.TargetGame));
        Status = "빌드 중…";
        try
        {
            string output = await Task.Run(() =>
            {
                if (game is not null)
                {
                    var ws = new GameWorkspace(game); ws.EnsureFolders();
                    string p = Path.Combine(ws.ModsDir, _project.Name + ".ktmod");
                    _project.Build(p); return p;
                }
                string local = Path.Combine(Path.GetDirectoryName(_project.ProjectDir)!, _project.Name + ".ktmod");
                _project.Build(local); return local;
            });
            Status = game is not null ? $"빌드 완료 → 게임 Mods: {output}" : $"빌드 완료(게임 미연결 — 로컬): {output}";
        }
        catch (Exception ex) { Status = $"빌드 실패: {ex.Message}"; }
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
    private void BackToLauncher() { Dispose(); _back(); }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (_projectPicker is null || _openProject is null) { _back(); return; }
        var path = await _projectPicker();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var project = KtmodProject.Load(path);
            EditorProjects.Touch(project, DateTime.UtcNow.ToString("o"));
            Dispose(); _openProject(project);
        }
        catch (Exception ex) { Status = $"열기 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenRecent(EditorProjects.Recent? recent)
    {
        if (recent is null || _openProject is null) return;
        try
        {
            var project = KtmodProject.Load(recent.Path);
            EditorProjects.Touch(project, DateTime.UtcNow.ToString("o"));
            Dispose(); _openProject(project);
        }
        catch (Exception ex) { Status = $"열기 실패: {ex.Message}"; }
    }

    private void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { Status = $"폴더 열기 실패: {ex.Message}"; }
    }

    public void Dispose() => _watcher.Dispose();
}
