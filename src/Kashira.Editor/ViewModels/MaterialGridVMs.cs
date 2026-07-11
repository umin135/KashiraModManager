using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kashira.Core.Mods;
using Kashira.Editor.Services;

namespace Kashira.Editor.ViewModels;

/// <summary>재질 그리드(재질×변형×카테고리) 표시용 VM. CostumeGrid 모델 + 썸네일. 편집 좌표 포함(Phase 3b).</summary>
public sealed class GridMaterialVM
{
    public int MaterialIndex { get; }           // -1 = Base form(BaseKtid)
    public string Name { get; }
    public IReadOnlyList<string> ColumnHeaders { get; }
    public IReadOnlyList<GridRowVM> Rows { get; }
    /// <summary>필수 카테고리(albedo=1) 누락 = 경고.</summary>
    public bool MissingAlbedo { get; }

    public GridMaterialVM(CostumeGrid.Material m)
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
