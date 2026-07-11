using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Kashira.Core.Mods;
using Kashira.Editor.Services;

namespace Kashira.Editor.ViewModels;

/// <summary>재질 그리드(재질×변형×카테고리) 표시용 VM. CostumeGrid 모델 + 썸네일. 읽기 전용(Phase 3a).</summary>
public sealed class GridMaterialVM
{
    public string Name { get; }
    public IReadOnlyList<string> ColumnHeaders { get; }
    public IReadOnlyList<GridRowVM> Rows { get; }

    public GridMaterialVM(CostumeGrid.Material m)
    {
        Name = m.Name;
        ColumnHeaders = m.ColumnHeaders;
        var rows = new List<GridRowVM>(m.Rows.Count);
        foreach (var r in m.Rows) rows.Add(new GridRowVM(r));
        Rows = rows;
    }
}

public sealed class GridRowVM
{
    public string Role { get; }
    public int Category { get; }
    public IReadOnlyList<GridCellVM> Cells { get; }

    public GridRowVM(CostumeGrid.Row r)
    {
        Role = r.Role;
        Category = r.Category;
        var cells = new List<GridCellVM>(r.Cells.Count);
        foreach (var c in r.Cells) cells.Add(new GridCellVM(c));
        Cells = cells;
    }
}

public sealed class GridCellVM
{
    public Bitmap? Thumb { get; }
    public bool Inherited { get; }
    public bool Missing { get; }
    public bool HasThumb => Thumb is not null;
    public string Glyph { get; }        // 썸네일 없을 때 표시(· / ? / 빈)
    public string Tooltip { get; }

    public GridCellVM(CostumeGrid.Cell c)
    {
        Inherited = c.Inherited;
        if (c.Inherited)
        {
            Glyph = "·";
            Tooltip = "상속(base 폴백)";
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
            Glyph = "";
            Tooltip = "(없음)";
        }
    }
}
