using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kashira.Core.Doa6;
using Kashira.Core.Mods;
using Kashira.Editor.Services;

namespace Kashira.Editor.ViewModels;

/// <summary>
/// 재질 그리드(재질×변형×카테고리) 표시용 VM. CostumeGrid 모델 + 썸네일 + 셰이더 타입(재질 단위).
/// 셰이더 = 재질 슬롯 단위 편집(설계 §8) — install 시 Manager 가 이 재질을 쓰는 메시그룹으로 팬아웃.
/// </summary>
public sealed partial class GridMaterialVM : ObservableObject
{
    public int MaterialIndex { get; }           // -1 = Base form(BaseKtid)
    public string Name { get; }
    public IReadOnlyList<string> ColumnHeaders { get; }
    public IReadOnlyList<GridRowVM> Rows { get; }
    /// <summary>필수 카테고리(albedo=1) 누락 = 경고.</summary>
    public bool MissingAlbedo { get; }

    // ── 셰이더 타입(재질 단위) ────────────────────────────────
    public bool CanEditShader { get; }          // 실제 재질(≥0)만. Base form(-1) 제외.
    public IReadOnlyList<ShaderChoiceVM> ShaderOptions { get; }
    [ObservableProperty] private ShaderChoiceVM _selectedShader;

    private readonly System.Action<int, uint?>? _onShaderChanged;
    private bool _suppress;

    public GridMaterialVM(CostumeGrid.Material m, ShaderCatalog? catalog = null,
                          System.Action<int, uint?>? onShaderChanged = null)
    {
        MaterialIndex = m.Index;
        Name = m.Name;
        ColumnHeaders = m.ColumnHeaders;
        var rows = new List<GridRowVM>(m.Rows.Count);
        bool hasAlbedo = false;
        foreach (var r in m.Rows)
        {
            if (r.Category == 1) hasAlbedo = true;
            rows.Add(new GridRowVM(r, m.Index));
        }
        Rows = rows;
        MissingAlbedo = !hasAlbedo;

        _onShaderChanged = onShaderChanged;
        CanEditShader = m.Index >= 0;

        var opts = new List<ShaderChoiceVM> { new(null, "(미지정 · g1m 원본 유지)") };  // 첫 항목 = 오버라이드 없음
        if (CanEditShader && catalog is not null)
            foreach (var e in catalog.All) opts.Add(new ShaderChoiceVM(e.MatB, e.Display));
        // 직접입력/미등록 matB 도 표시(카탈로그에 없으면 unknown 항목 추가)
        if (m.Shader is { } cur && opts.All(o => o.MatB != cur))
            opts.Add(new ShaderChoiceVM(cur, $"unknown (0x{cur:x8})"));
        ShaderOptions = opts;

        _selectedShader = m.Shader is { } s ? opts.First(o => o.MatB == s) : opts[0];
    }

    partial void OnSelectedShaderChanged(ShaderChoiceVM value)
    {
        if (_suppress || !CanEditShader) return;
        _onShaderChanged?.Invoke(MaterialIndex, value.MatB);        // null → 오버라이드 제거
    }
}

public sealed class GridRowVM
{
    public string Role { get; }
    public int Category { get; }
    public IReadOnlyList<GridCellVM> Cells { get; }

    public GridRowVM(CostumeGrid.Row r, int materialIndex)
    {
        Role = r.Role;
        Category = r.Category;
        var cells = new List<GridCellVM>(r.Cells.Count);
        foreach (var c in r.Cells) cells.Add(new GridCellVM(c, materialIndex));
        Cells = cells;
    }
}

public sealed partial class GridCellVM : ObservableObject
{
    public int MaterialIndex { get; }   // -1 = base
    public int Column { get; }
    public int Category { get; }
    public string Role { get; }

    public Bitmap? Thumb { get; }
    public bool Inherited { get; }
    public bool Missing { get; }
    public bool HasThumb => Thumb is not null;
    public string Glyph { get; }
    public string Tooltip { get; }

    [ObservableProperty] private bool _isSelected;

    public GridCellVM(CostumeGrid.Cell c, int materialIndex)
    {
        MaterialIndex = materialIndex;
        Column = c.Column;
        Category = c.Category;
        Role = c.Role;
        Inherited = c.Inherited;

        if (c.Inherited)
        {
            Glyph = "·";
            Tooltip = "상속(base 폴백) — 배정하면 변형이 실체화됩니다";
        }
        else if (c.FullPath is not null)
        {
            Thumb = TexturePreview.ThumbnailFromG1t(c.FullPath, 128);
            Missing = Thumb is null;
            Glyph = Thumb is null ? "?" : "";
            Tooltip = c.FileName ?? "";
        }
        else if (c.AtRef is not null)
        {
            Missing = true;
            Glyph = "?";
            Tooltip = $"미해결: {c.FileName} (Content 에 없음)";
        }
        else
        {
            Glyph = "＋";
            Tooltip = "(비어 있음) — 텍스처를 배정하세요";
        }
    }
}
