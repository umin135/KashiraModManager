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
using Kashira.Core.Doa6;
using Kashira.Core.Formats;
using Kashira.Core.Games;
using Kashira.Core.Mods;
using Kashira.Editor.Services;

namespace Kashira.Editor.ViewModels;

/// <summary>
/// 로드된 프로젝트 편집 화면 — 언리얼식 셸.
/// Content Browser(하단): 좌 폴더트리 + 우 폴더-직속 항목 그리드. 에셋 선택 → CurrentAsset.
/// Outliner(좌): CurrentAsset 의 "목차"(섹션). Details(우): 선택 섹션의 상세. Viewport(중앙): 프리뷰/재질 그리드.
/// 게이팅: Content/ 저작 파이프라인은 현재 무기한 보류 → 어느 게임이든 Content_Legacy 전용(ContentEnabled=false).
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

    /// <summary>Content/ 저작 파이프라인은 현재 무기한 보류(연구문서 결정).
    /// false → Content 폴더 트리·번들 임포트 등 저작 UI 를 숨기고 Content_Legacy 전용으로 동작.</summary>
    public bool ContentEnabled => false;

    public string GameModeText => "Legacy only";

    // ── Content Browser ───────────────────────────────────────
    public ObservableCollection<ProjectContent.ContentNode> FolderTree { get; } = new();
    [ObservableProperty] private ProjectContent.ContentNode? _selectedFolder;
    public ObservableCollection<BrowserItemVM> FolderItems { get; } = new();
    [ObservableProperty] private BrowserItemVM? _selectedBrowserItem;

    // ── Outliner (목차) ───────────────────────────────────────
    public ObservableCollection<AssetSectionVM> OutlinerSections { get; } = new();
    [ObservableProperty] private AssetSectionVM? _selectedSection;
    [ObservableProperty] private string _currentAssetName = "(none selected)";
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

    // g1m 재질 타입(nMtrID) 편집(0x10003 PropertySet 선택 시)
    public ObservableCollection<G1mMaterialVM> G1mMaterials { get; } = new();
    [ObservableProperty] private bool _hasG1mProps;

    // g1m 섹션 raw export/import(G4)
    [ObservableProperty] private bool _hasG1mSection;
    [ObservableProperty] private string _g1mSectionInfo = "";
    private uint? _selG1mSectionId;
    private int? _selG1mChunkIndex;

    // g1m 서브메시 분석(0x10008 선택 시, 읽기)
    public ObservableCollection<SubmeshRowVM> Submeshes { get; } = new();
    [ObservableProperty] private bool _hasSubmeshList;

    // g1m 메시 계층(0x10009 MeshGroup 선택 시, 결정 B: 메시=셰이더 → 슬롯=텍스처)
    public ObservableCollection<MeshRowVM> Meshes { get; } = new();
    [ObservableProperty] private bool _hasMeshList;
    private CharacterSid? _sidCache; private bool _sidTried;
    private ShaderCatalog? _catalogCache;
    private string? _meshG1mPath;

    // 변형 추가/삭제
    [ObservableProperty] private string _variationInfo = "";

    // 그리드 셀 편집
    [ObservableProperty] private GridCellVM? _selectedCell;
    [ObservableProperty] private string _selectedCellInfo = "Click a cell to select it.";

    // ── 상태/게임 ─────────────────────────────────────────────
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _gameStatus = "";

    // ── Import Kissaki bundle (저작) ──────────────────────────
    [ObservableProperty] private string _newSetName = "body";
    [ObservableProperty] private string _newTargetCostume = "";
    [ObservableProperty] private string _bundleDir = "";

    // ── Mod Info(작성자/썸네일/미리보기 이미지) ────────────────
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _hasThumbnail;
    public ObservableCollection<PreviewImageVM> PreviewImages { get; } = new();

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
        Author = project.Author;
        Description = project.Description;
        LoadModInfo();
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
            ? $"Game '{_project.TargetGame}' not linked — add/detect in the Manager"
            : $"{g.DisplayName} · {GameModeText}";
    }

    // ── Content Browser: 폴더 트리/그리드 ─────────────────────

    [RelayCommand]
    private void Refresh()
    {
        string? keepFolder = SelectedFolder?.FullPath;
        FolderTree.Clear();
        foreach (var root in ProjectContent.FolderRoots(_project, content: ContentEnabled, legacy: true))
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
        _currentManifestPath = null; SelectedCell = null; SelectedCellInfo = "Click a cell to select it.";
        VariationInfo = "";
        OutlinerSections.Clear();

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "json" && TryBuildManifestSections(path)) { }
        else if (ext == "g1t")
        {
            PreviewImage = TexturePreview.FromG1t(path);
            OutlinerSections.Add(new AssetSectionVM("Texture", "texture", ProjectContent.Inspect(path)));
        }
        else if (ext == "g1m")
            BuildG1mSections(path);
        else
            OutlinerSections.Add(new AssetSectionVM("File info", "file", ProjectContent.Inspect(path)));

        SelectedSection = OutlinerSections.FirstOrDefault();
    }

    /// <summary>g1m → Outliner 에 청크/G1MG 섹션 구조를 목차로 표시(G2 뷰어, G1mContainer 기반).</summary>
    private void BuildG1mSections(string path)
    {
        byte[] bytes;
        G1mContainer c;
        try { bytes = File.ReadAllBytes(path); c = G1mContainer.Parse(bytes); }
        catch { OutlinerSections.Add(new AssetSectionVM("Mesh info", "mesh", ProjectContent.Inspect(path))); return; }

        static string Ver(byte[] v) => System.Text.Encoding.ASCII.GetString(v);
        static string Human(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.0} KB" : $"{b / (1024.0 * 1024.0):0.0} MB";

        var geo = G1mGeometry.Analyze(c);
        OutlinerSections.Add(new AssetSectionVM("Mesh (g1m)", "mesh", new List<ProjectContent.MetaLine>
        {
            new("Version", Ver(c.Top[4..8])),
            new("Chunks", c.Chunks.Count.ToString()),
            new("Size", Human(bytes.Length)),
            new("Submeshes", geo.SubmeshCount.ToString()),
            new("Materials", geo.MaterialCount.ToString()),
            new("Bones", geo.BoneCount.ToString()),
            new("Triangles", geo.TotalTris.ToString("N0")),
            new("Vertices", geo.TotalVerts.ToString("N0")),
        }));

        for (int ci = 0; ci < c.Chunks.Count; ci++)
        {
            var ch = c.Chunks[ci];
            if (ch.IsG1mg)
            {
                OutlinerSections.Add(new AssetSectionVM("G1MG (geometry)", "mesh", new List<ProjectContent.MetaLine>
                {
                    new("Version", Ver(ch.Ver)),
                    new("Sections", ch.Sections!.Count.ToString()),
                }));  // 컨테이너 — 서브섹션이 export 단위
                foreach (var s in ch.Sections!)
                {
                    var det = new List<ProjectContent.MetaLine>
                    {
                        new("Id", $"0x{s.Id:X5}"),
                        new("Size", Human(s.Inner.Length)),
                    };
                    if (s.Id == 0x10002)
                        det.Add(new("Materials", geo.MaterialCount.ToString()));
                    else if (s.Id == G1mMaterialProps.SectionId)
                        det.Add(new("Edit", "Material type (nMtrID) editable"));
                    else if (s.Id == 0x10004)
                        foreach (var vb in geo.VertexBuffers)
                            det.Add(new($"VB {vb.Index}", $"vsize {vb.VertexSize} · {vb.NumVerts:N0} verts · L{vb.Layout}"));
                    else if (s.Id == 0x10005)
                        foreach (var lay in geo.Layouts)
                            det.Add(new($"L{lay.Index}", string.Join(" · ", lay.Semantics.Select(sm => sm.KindName))));
                    else if (s.Id == 0x10007)
                        foreach (var ib in geo.IndexBuffers)
                            det.Add(new($"IB {ib.Index}", $"{ib.NumIndices:N0} idx · {ib.TypeName}"));
                    else if (s.Id == 0x10006)
                        for (int pi = 0; pi < geo.PaletteSizes.Count; pi++)
                            det.Add(new($"Palette {pi}", $"{geo.PaletteSizes[pi]} bones"));
                    OutlinerSections.Add(new AssetSectionVM($"   · {G1mContainer.SectionName(s.Id)}", "file", det,
                        g1mSectionId: s.Id));
                }
            }
            else
            {
                OutlinerSections.Add(new AssetSectionVM(G1mContainer.SigName(ch.Sig),
                    ch.Sig == G1mContainer.G1msSig || ch.Sig == G1mContainer.G1mmSig ? "mesh" : "file",
                    new List<ProjectContent.MetaLine>
                    {
                        new("Version", Ver(ch.Ver)),
                        new("Size", Human(ch.Inner.Length)),
                    }, g1mChunkIndex: ci));
            }
        }
    }

    private bool TryBuildManifestSections(string path)
    {
        CostumeGrid.Model? model;
        try { model = CostumeGrid.Build(_project, path); } catch { model = null; }
        if (model is null) return false;

        _currentManifestPath = path;
        _gridMaterials.Clear();
        var catalog = LoadShaderCatalog();
        if (model.BaseForm is not null) _gridMaterials.Add(new GridMaterialVM(model.BaseForm, catalog, OnMaterialShaderChanged));
        foreach (var m in model.Materials) _gridMaterials.Add(new GridMaterialVM(m, catalog, OnMaterialShaderChanged));

        foreach (var gm in _gridMaterials)
            OutlinerSections.Add(new AssetSectionVM(gm.Name, "material", MaterialDetails(gm), gm));
        OutlinerSections.Add(new AssetSectionVM("Manifest", "file", ProjectContent.Inspect(path)));

        UpdateVariationInfo();
        return _gridMaterials.Count > 0;
    }

    // ── 변형 추가/삭제(코스튬 단위) ────────────────────────────

    private void UpdateVariationInfo()
    {
        VariationInfo = _currentManifestPath is null
            ? ""
            : $"Variations: {CostumeManifestEditor.GetVariationCount(_currentManifestPath)}";
    }

    [RelayCommand]
    private void AddVariation()
    {
        if (_currentManifestPath is null) return;
        int keep = SelectedGridMaterial?.MaterialIndex ?? int.MinValue;
        try { CostumeManifestEditor.AddVariation(_currentManifestPath); RebuildManifestKeeping(keep); Status = "Variation added (new column inherits ·)"; }
        catch (Exception ex) { Status = $"Add variation failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void RemoveVariation()
    {
        if (_currentManifestPath is null) return;
        if (CostumeManifestEditor.GetVariationCount(_currentManifestPath) <= 1) { Status = "Only one variation left — cannot reduce further."; return; }
        int keep = SelectedGridMaterial?.MaterialIndex ?? int.MinValue;
        try { CostumeManifestEditor.RemoveVariation(_currentManifestPath); RebuildManifestKeeping(keep); Status = "Last variation removed"; }
        catch (Exception ex) { Status = $"Remove variation failed: {ex.Message}"; }
    }

    private void RebuildManifestKeeping(int materialIndex)
    {
        if (_currentManifestPath is null) return;
        SetCurrentAsset(_currentManifestPath);
        var section = OutlinerSections.FirstOrDefault(s => s.Material?.MaterialIndex == materialIndex);
        if (section is not null) SelectedSection = section;
    }

    private static List<ProjectContent.MetaLine> MaterialDetails(GridMaterialVM gm)
    {
        var l = new List<ProjectContent.MetaLine>
        {
            new("Kind", gm.MaterialIndex < 0 ? "Base form" : $"Material {gm.MaterialIndex}"),
            new("Variations", gm.ColumnHeaders.Count.ToString()),
            new("Categories", gm.Rows.Count.ToString()),
        };
        if (gm.MissingAlbedo) l.Add(new("Warning", "⚠ albedo missing"));
        return l;
    }

    partial void OnSelectedSectionChanged(AssetSectionVM? value)
    {
        Details.Clear();
        if (value is not null) foreach (var line in value.Details) Details.Add(line);
        HasDetails = Details.Count > 0;

        HasG1mProps = false;
        G1mMaterials.Clear();
        HasG1mSection = false; _selG1mSectionId = null; _selG1mChunkIndex = null; G1mSectionInfo = "";
        HasSubmeshList = false; Submeshes.Clear();
        HasMeshList = false; Meshes.Clear();

        if (value?.Material is { } gm)
        {
            SelectedGridMaterial = gm;
            HasGrid = true; HasPreview = false;
        }
        else if (value is { IsG1mSection: true })
        {
            HasGrid = false; HasPreview = false;
            HasG1mSection = true;
            _selG1mSectionId = value.G1mSectionId;
            _selG1mChunkIndex = value.G1mChunkIndex;
            string size = value.Details.FirstOrDefault(d => d.Key == "Size")?.Value ?? "";
            G1mSectionInfo = $"{value.Title.TrimStart(' ', '·').Trim()}  ·  {size}";
            if (value.G1mSectionId == G1mMaterialProps.SectionId) BuildG1mPropsEditor();
            else if (value.G1mSectionId == 0x10008) BuildSubmeshList();
            else if (value.G1mSectionId == 0x10009) BuildMeshList();
        }
        else
        {
            HasGrid = false;
            HasPreview = PreviewImage is not null;
        }
    }

    // ── g1m 섹션 raw export/import(G4) ────────────────────────

    private (byte[] inner, string key)? ResolveSection(G1mContainer c)
    {
        if (_selG1mSectionId is uint id)
        {
            var s = c.FindSection(id);
            return s is null ? null : (s.Inner, G1mContainer.SectionName(id));
        }
        if (_selG1mChunkIndex is int ci && ci >= 0 && ci < c.Chunks.Count && !c.Chunks[ci].IsG1mg)
            return (c.Chunks[ci].Inner, G1mContainer.SigName(c.Chunks[ci].Sig));
        return null;
    }

    /// <summary>선택 섹션의 raw 바이트를 g1m 옆에 &lt;g1m&gt;.&lt;key&gt;.bin 으로 내보낸다.</summary>
    [RelayCommand]
    private void ExportG1mSection()
    {
        if (_currentAssetPath is not { } g1m || !HasG1mSection) return;
        try
        {
            var c = G1mContainer.Parse(File.ReadAllBytes(g1m));
            if (ResolveSection(c) is not { } r) { Status = "This section is not eligible for raw export."; return; }
            string outPath = Path.Combine(Path.GetDirectoryName(g1m)!, $"{Path.GetFileName(g1m)}.{r.key}.bin");
            File.WriteAllBytes(outPath, r.inner);
            Status = $"Section exported: {Path.GetFileName(outPath)} ({r.inner.Length} B)";
            Refresh();
        }
        catch (Exception ex) { Status = $"Section export failed: {ex.Message}"; }
    }

    /// <summary>.bin 을 선택해 현재 섹션의 raw 바이트를 교체(같은 크기=안전, 다르면 경고).</summary>
    [RelayCommand]
    private async Task ImportG1mSection()
    {
        if (_currentAssetPath is not { } g1m || !HasG1mSection) return;
        if (_filePicker is null) { Status = "File picker is unavailable."; return; }
        var f = await _filePicker("Select section .bin", "bin");
        if (string.IsNullOrEmpty(f)) return;
        try
        {
            var newInner = File.ReadAllBytes(f);
            var c = G1mContainer.Parse(File.ReadAllBytes(g1m));
            int oldLen;
            if (_selG1mSectionId is uint id)
            {
                var s = c.FindSection(id);
                if (s is null) { Status = "Section not found"; return; }
                oldLen = s.Inner.Length; s.Inner = newInner;
            }
            else if (_selG1mChunkIndex is int ci && ci >= 0 && ci < c.Chunks.Count && !c.Chunks[ci].IsG1mg)
            {
                oldLen = c.Chunks[ci].Inner.Length; c.Chunks[ci].Inner = newInner;
            }
            else { Status = "This section is not eligible for raw import."; return; }

            File.WriteAllBytes(g1m, c.Build());
            string warn = newInner.Length != oldLen
                ? " ⚠ Size changed — G1MF/references not updated, may crash (backup recommended)" : "";
            Status = $"Section imported: {Path.GetFileName(f)} ({oldLen}→{newInner.Length} B){warn}";
            ReselectG1mSectionAfterEdit();
        }
        catch (Exception ex) { Status = $"Section import failed: {ex.Message}"; }
    }

    private void ReselectG1mSectionAfterEdit()
    {
        var id = _selG1mSectionId; var ci = _selG1mChunkIndex;
        if (_currentAssetPath is not { } g1m) return;
        SetCurrentAsset(g1m);
        var sec = OutlinerSections.FirstOrDefault(s =>
            (id is not null && s.G1mSectionId == id) || (ci is not null && s.G1mChunkIndex == ci));
        if (sec is not null) SelectedSection = sec;
    }

    /// <summary>g1m 0x10008 → 서브메시 분석 목록(읽기).</summary>
    private void BuildSubmeshList()
    {
        if (_currentAssetPath is null) return;
        try
        {
            var c = G1mContainer.Parse(File.ReadAllBytes(_currentAssetPath));
            foreach (var s in G1mGeometry.Analyze(c).Submeshes) Submeshes.Add(new SubmeshRowVM(s));
            HasSubmeshList = Submeshes.Count > 0;
        }
        catch { HasSubmeshList = false; }
    }

    /// <summary>g1m 0x10009(MeshGroup) → 메시 구조 인스펙터(읽기전용). 셰이더는 재질 그리드에서 편집(설계 §8).
    /// 여기선 원본 sid 셰이더를 참고용으로만 표시. 슬롯 클릭 → 재질 텍스처 그리드로 이동.</summary>
    private void BuildMeshList()
    {
        if (_currentAssetPath is null) return;
        try
        {
            var bytes = File.ReadAllBytes(_currentAssetPath);
            var c = G1mContainer.Parse(bytes);
            var sid = LoadSid();
            var catalog = LoadShaderCatalog();
            var slotCats = SlotCategories(bytes);                             // 슬롯(재질)별 텍스처 카테고리
            _meshG1mPath = _currentAssetPath;
            foreach (var m in CostumeMeshModel.Build(c, sid))
                Meshes.Add(new MeshRowVM(m, catalog, slotCats, OnMeshSlotClicked));
            HasMeshList = Meshes.Count > 0;
        }
        catch { HasMeshList = false; }
    }

    /// <summary>재질 셰이더 타입 변경 → 매니페스트 Materials[m].Shader 저장. matB null = 미지정(제거).
    /// install 시 Manager 가 이 재질을 쓰는 메시그룹으로 팬아웃(설계 §8).</summary>
    private void OnMaterialShaderChanged(int materialIndex, uint? matB)
    {
        if (_currentManifestPath is null) return;
        try
        {
            CostumeManifestEditor.SetMaterialShader(_currentManifestPath, materialIndex, matB);
            Status = $"Shader set: Material {materialIndex} → {(matB is { } v ? $"0x{v:x8}" : "unset")}";
        }
        catch (Exception ex) { Status = $"Save shader failed: {ex.Message}"; }
    }

    /// <summary>메시 슬롯(재질 인덱스) 클릭 → 이 g1m 을 참조하는 코스튬 매니페스트의 그 재질 텍스처 그리드로 이동.</summary>
    private void OnMeshSlotClicked(int materialIndex)
    {
        if (_meshG1mPath is null) return;
        string g1mName = Path.GetFileName(_meshG1mPath);
        var manifest = FindManifestForG1m(_meshG1mPath);
        if (manifest is null)
        {
            Status = $"No costume manifest (.json) referencing @{g1mName} was found in Content/.";
            return;
        }
        SetCurrentAsset(manifest);
        var section = OutlinerSections.FirstOrDefault(s => s.Material?.MaterialIndex == materialIndex);
        if (section is not null)
        {
            SelectedSection = section;
            Status = $"Texture grid: {Path.GetFileName(manifest)} · Material {materialIndex}";
        }
        else
        {
            SelectedSection = OutlinerSections.FirstOrDefault(s => s.Material is not null);
            Status = $"{Path.GetFileName(manifest)} has no Material {materialIndex} slot (check the manifest's material count).";
        }
    }

    /// <summary>Content/ 에서 Mesh.g1m == "@&lt;g1m파일명&gt;" 인 저작 매니페스트를 찾는다(첫 항목).</summary>
    private string? FindManifestForG1m(string g1mPath)
    {
        string g1mRef = "@" + Path.GetFileName(g1mPath);
        if (!Directory.Exists(_project.ContentDir)) return null;
        foreach (var json in Directory.EnumerateFiles(_project.ContentDir, "*.json", SearchOption.AllDirectories))
        {
            CostumeManifest? cm;
            try { cm = CostumeManifest.Parse(File.ReadAllText(json)); } catch { continue; }
            if (cm?.Mesh?.G1m is { } g && string.Equals(g, g1mRef, StringComparison.OrdinalIgnoreCase))
                return json;
        }
        return null;
    }

    /// <summary>g1m 0x10002 → 재질 인덱스별 텍스처 카테고리 요약("alb·nmh·occ"). 실패 시 빈 맵.</summary>
    private static IReadOnlyDictionary<int, string> SlotCategories(byte[] g1m)
    {
        var map = new Dictionary<int, string>();
        try
        {
            var mats = G1mFile.Materials(g1m);
            for (int mi = 0; mi < mats.Count; mi++)
            {
                var cats = ShaderCategory.Sort(mats[mi].Select(s => s.Primary).Distinct());
                map[mi] = string.Join("·", cats.Select(ShaderCategory.RoleName));
            }
        }
        catch { /* 무시 — 카테고리 없이 표시 */ }
        return map;
    }

    /// <summary>셰이더 카탈로그(res/&lt;game&gt;/shaders.json) — 1회 캐시. 없으면 빈 카탈로그.</summary>
    private ShaderCatalog LoadShaderCatalog()
        => _catalogCache ??= ShaderCatalog.LoadForGame(Path.Combine(AppContext.BaseDirectory, "res"), _project.TargetGame);

    /// <summary>연결된 게임의 pristine Character.sid — best-effort, 1회 캐시. 미연결/실패 시 null.</summary>
    private CharacterSid? LoadSid()
    {
        if (_sidTried) return _sidCache;
        _sidTried = true;
        try
        {
            var g = GameLibrary.Load().FirstOrDefault(x => GameCatalog.Matches(x, _project.TargetGame));
            if (g is null) return null;
            using var ex = AssetExtractor.Open(new GameWorkspace(g));
            if (ex.Extract(CharacterSid.FileKtid) is { } bytes) _sidCache = CharacterSid.Parse(bytes);
        }
        catch { /* best-effort */ }
        return _sidCache;
    }

    /// <summary>g1m 0x10003 → 재질별 nMtrID 편집기 채우기.</summary>
    private void BuildG1mPropsEditor()
    {
        if (_currentAssetPath is null) return;
        try
        {
            var c = G1mContainer.Parse(File.ReadAllBytes(_currentAssetPath));
            foreach (var p in G1mMaterialProps.Read(c)) G1mMaterials.Add(new G1mMaterialVM(p));
            HasG1mProps = G1mMaterials.Count > 0;
        }
        catch { HasG1mProps = false; }
    }

    /// <summary>재질 타입(nMtrID) 변경을 g1m 에 적용(크기 불변, 제자리 교체 → repack).</summary>
    [RelayCommand]
    private void ApplyG1mTypes()
    {
        if (_currentAssetPath is null || G1mMaterials.Count == 0) return;
        try
        {
            var c = G1mContainer.Parse(File.ReadAllBytes(_currentAssetPath));
            int changed = 0;
            foreach (var mv in G1mMaterials)
                if (G1mMaterialProps.SetNMtrID(c, mv.Index, mv.SelectedValue)) changed++;
            File.WriteAllBytes(_currentAssetPath, c.Build());
            Status = $"Material types applied ({changed}) — {Path.GetFileName(_currentAssetPath)}";
            G1mMaterials.Clear();
            BuildG1mPropsEditor();
        }
        catch (Exception ex) { Status = $"Apply material types failed: {ex.Message}"; }
    }

    private void ClearCurrentAsset()
    {
        _currentAssetPath = null; _currentManifestPath = null;
        CurrentAssetName = "(none selected)";
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
        if (cell is null) { SelectedCellInfo = "Click a cell to select it."; return; }
        cell.IsSelected = true;
        string mat = cell.MaterialIndex < 0 ? "Base" : $"Material {cell.MaterialIndex}";
        string col = cell.MaterialIndex < 0 ? "base" : $"var{cell.Column}";
        SelectedCellInfo = $"{mat} · {col} · cat {cell.Category}";
    }

    [RelayCommand]
    private void AssignSelectedCell()
    {
        if (_currentManifestPath is null || SelectedCell is null) { Status = "Select a grid cell first."; return; }
        if (SelectedBrowserItem is null || !SelectedBrowserItem.FullPath.EndsWith(".g1t", StringComparison.OrdinalIgnoreCase))
        { Status = "Select a g1t to assign in the Content browser first."; return; }
        var cell = SelectedCell;
        string atRef = "@" + Path.GetFileName(SelectedBrowserItem.FullPath);
        try
        {
            if (cell.MaterialIndex < 0)
                CostumeManifestEditor.SetBaseSlot(_currentManifestPath, cell.Category, atRef);
            else
                CostumeManifestEditor.SetMaterialSlot(_currentManifestPath, cell.MaterialIndex, cell.Column, cell.Category, atRef);
            RebuildAfterEdit(cell);
            Status = $"Assigned: {cell.Role} ← {Path.GetFileName(SelectedBrowserItem.FullPath)}";
        }
        catch (Exception ex) { Status = $"Assign failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void ClearSelectedCell()
    {
        if (_currentManifestPath is null || SelectedCell is null) { Status = "Select a grid cell first."; return; }
        var cell = SelectedCell;
        if (cell.Inherited) { Status = "Inherited cell — nothing to clear."; return; }
        try
        {
            if (cell.MaterialIndex < 0)
                CostumeManifestEditor.SetBaseSlot(_currentManifestPath, cell.Category, null);
            else
                CostumeManifestEditor.SetMaterialSlot(_currentManifestPath, cell.MaterialIndex, cell.Column, cell.Category, null);
            RebuildAfterEdit(cell);
            Status = $"Cleared: {cell.Role}";
        }
        catch (Exception ex) { Status = $"Clear failed: {ex.Message}"; }
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
        try { Status = $"DDS exported: {Path.GetFileName(TextureIo.ExportDds(p))}"; Refresh(); }
        catch (Exception ex) { Status = $"DDS export failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void ExportTga()
    {
        if (SelectedG1tPath is not { } p) return;
        try { Status = $"TGA exported: {Path.GetFileName(TextureIo.ExportTga(p))}"; Refresh(); }
        catch (Exception ex) { Status = $"TGA export failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ReplaceTexture()
    {
        if (SelectedG1tPath is not { } p) return;
        if (_filePicker is null) { Status = "File picker is unavailable."; return; }
        var img = await _filePicker("Select replacement texture (dds/tga)", "dds,tga");
        if (string.IsNullOrEmpty(img)) return;
        try
        {
            TextureIo.ReplaceFromFile(p, img);
            TexturePreview.ClearCache();
            SetCurrentAsset(p);
            Status = $"Replaced: {Path.GetFileName(p)} ← {Path.GetFileName(img)}";
        }
        catch (Exception ex) { Status = $"Replace failed: {ex.Message}"; }
    }

    // ── Import Kissaki bundle ─────────────────────────────────

    /// <summary>번들 임포트: 누르면 폴더 선택기를 열고, 고른 번들 폴더로 바로 임포트한다.</summary>
    [RelayCommand]
    private async Task ImportBundle()
    {
        if (!IsAuthoringGame) { Status = "This game does not support authoring (Content_Legacy only)."; return; }
        if (string.IsNullOrWhiteSpace(NewSetName)) { Status = "Enter a set name."; return; }
        var game = GameLibrary.Load().FirstOrDefault(g => GameCatalog.Matches(g, _project.TargetGame));
        if (game is null) { Status = $"Game '{_project.TargetGame}' not linked — set up/detect in the Manager."; return; }
        if (_folderPicker is null) { Status = "Folder picker is unavailable."; return; }

        var dir = await _folderPicker();
        if (string.IsNullOrEmpty(dir)) return;                       // 취소
        if (!Directory.Exists(dir)) { Status = "Folder does not exist."; return; }
        BundleDir = dir;
        try
        {
            var r = BundleImporter.Import(new GameWorkspace(game), _project, NewSetName.Trim(), dir, NewTargetCostume.Trim());
            Status = $"Import complete: source {r.SourceCostume} → materials {r.Materials} · variations {r.Variations} · textures {r.Textures} · shaders {r.Shaders}"
                     + (r.MissingG1t > 0 ? $" (unmatched g1t {r.MissingG1t})" : "");
            Refresh();
        }
        catch (Exception ex) { Status = $"Import failed: {ex.Message}"; }
    }

    // ── 빌드/폴더/파일메뉴 ────────────────────────────────────

    /// <summary>빌드 → 연결된 게임의 _Kashira/Mods 에 .ktmod 생성. 미연결 시 프로젝트 폴더 옆에.</summary>
    [RelayCommand]
    private async Task Build()
    {
        var game = GameLibrary.Load().FirstOrDefault(g => GameCatalog.Matches(g, _project.TargetGame));
        Status = "Building…";
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
            Status = game is not null ? $"Build complete → game Mods: {output}" : $"Build complete (game not linked — local): {output}";
        }
        catch (Exception ex) { Status = $"Build failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenProjectFolder() => OpenInExplorer(_project.ProjectDir);

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
        catch (Exception ex) { Status = $"Open failed: {ex.Message}"; }
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
        catch (Exception ex) { Status = $"Open failed: {ex.Message}"; }
    }

    private void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { Status = $"Open folder failed: {ex.Message}"; }
    }

    // ── Mod Info(작성자/썸네일/미리보기 이미지) ────────────────

    /// <summary>디스크(thumb.png · preview/*.png)에서 썸네일·미리보기 이미지를 다시 읽는다.
    /// Bitmap 은 파일이 아닌 메모리에서 디코드 → 이후 덮어쓰기/삭제 시 파일 잠김 없음.</summary>
    private void LoadModInfo()
    {
        Thumbnail?.Dispose();
        Thumbnail = null; HasThumbnail = false;
        string thumb = Path.Combine(_project.ProjectDir, "thumb.png");
        if (File.Exists(thumb)) { Thumbnail = LoadBitmap(thumb); HasThumbnail = Thumbnail is not null; }

        foreach (var img in PreviewImages) img.Dispose();
        PreviewImages.Clear();
        string previewDir = Path.Combine(_project.ProjectDir, "preview");
        if (Directory.Exists(previewDir))
            foreach (var png in Directory.EnumerateFiles(previewDir, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                PreviewImages.Add(new PreviewImageVM(png, RemovePreviewImage));
    }

    private static Bitmap? LoadBitmap(string path)
    {
        try { using var ms = new MemoryStream(File.ReadAllBytes(path)); return new Bitmap(ms); }
        catch { return null; }
    }

    /// <summary>작성자/설명을 project.ktproj 에 저장(ModifiedUtc 갱신).</summary>
    [RelayCommand]
    private void SaveModInfo()
    {
        try
        {
            _project.Author = Author?.Trim() ?? "";
            _project.Description = Description?.Trim() ?? "";
            _project.ModifiedUtc = DateTime.UtcNow.ToString("o");
            _project.Save();
            Status = "Mod info saved";
        }
        catch (Exception ex) { Status = $"Save mod info failed: {ex.Message}"; }
    }

    /// <summary>이미지를 골라 thumb.png 로 복사(대표 썸네일).</summary>
    [RelayCommand]
    private async Task SetThumbnail()
    {
        if (_filePicker is null) { Status = "File picker is unavailable."; return; }
        var img = await _filePicker("Select thumbnail image (png)", "png");
        if (string.IsNullOrEmpty(img)) return;
        try
        {
            Thumbnail?.Dispose(); Thumbnail = null; HasThumbnail = false; // 핸들 해제 후 덮어쓰기
            string dest = Path.Combine(_project.ProjectDir, "thumb.png");
            File.Copy(img, dest, overwrite: true);
            Thumbnail = LoadBitmap(dest); HasThumbnail = Thumbnail is not null;
            Status = $"Thumbnail set: {Path.GetFileName(img)}";
        }
        catch (Exception ex) { Status = $"Set thumbnail failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void ClearThumbnail()
    {
        try
        {
            Thumbnail?.Dispose(); Thumbnail = null; HasThumbnail = false;
            string dest = Path.Combine(_project.ProjectDir, "thumb.png");
            if (File.Exists(dest)) File.Delete(dest);
            Status = "Thumbnail removed";
        }
        catch (Exception ex) { Status = $"Remove thumbnail failed: {ex.Message}"; }
    }

    /// <summary>이미지를 골라 preview/ 에 복사(미리보기 갤러리). 동일 이름은 _N 로 회피.</summary>
    [RelayCommand]
    private async Task AddImage()
    {
        if (_filePicker is null) { Status = "File picker is unavailable."; return; }
        var img = await _filePicker("Add preview image (png)", "png");
        if (string.IsNullOrEmpty(img)) return;
        try
        {
            string previewDir = Path.Combine(_project.ProjectDir, "preview");
            Directory.CreateDirectory(previewDir);
            string dest = UniquePath(previewDir, Path.GetFileName(img));
            File.Copy(img, dest);
            LoadModInfo();
            Status = $"Image added: {Path.GetFileName(dest)}";
        }
        catch (Exception ex) { Status = $"Add image failed: {ex.Message}"; }
    }

    private void RemovePreviewImage(PreviewImageVM img)
    {
        try
        {
            img.Dispose();
            if (File.Exists(img.Path)) File.Delete(img.Path);
            LoadModInfo();
            Status = $"Image deleted: {img.Name}";
        }
        catch (Exception ex) { Status = $"Delete image failed: {ex.Message}"; }
    }

    private static string UniquePath(string dir, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string p = Path.Combine(dir, fileName);
        for (int i = 1; File.Exists(p); i++) p = Path.Combine(dir, $"{stem}_{i}{ext}");
        return p;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        Thumbnail?.Dispose();
        foreach (var img in PreviewImages) img.Dispose();
    }
}
