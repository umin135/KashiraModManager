using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Kashira.Core.Mods;
using Kashira.Editor.Services;

namespace Kashira.Editor.ViewModels;

/// <summary>Content Browser 그리드의 한 항목(하위폴더 또는 파일). 아이콘/썸네일 포함.</summary>
public sealed class BrowserItemVM
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string Icon { get; }        // Material Design Icon 이름(mdi-*)
    public string Kind { get; }        // 표시용 타입명
    public Bitmap? Thumb { get; }
    public bool HasThumb => Thumb is not null;

    public BrowserItemVM(ProjectContent.ContentNode node)
    {
        Name = node.Name;
        FullPath = node.FullPath;
        IsDirectory = node.IsDirectory;
        if (node.IsDirectory) { Icon = "folder"; Kind = "Folder"; return; }

        var ext = Path.GetExtension(node.FullPath).TrimStart('.').ToLowerInvariant();
        var name = node.Name.ToLowerInvariant();
        (Icon, Kind) = ext switch
        {
            "g1t" => ("texture", "Texture (g1t)"),
            "dds" or "tga" => ("texture", "Image"),
            "g1m" => ("mesh", "Mesh (g1m)"),
            "json" when name.EndsWith(".kts.json") => ("file", "KTS"),
            "json" when name.EndsWith(".mtl.json") => ("file", "Material list"),
            "json" when name.EndsWith(".grp.json") => ("file", "Group"),
            "json" => ("material", "Costume manifest"),
            "mtl" => ("file", "Material (mtl)"),
            "grp" => ("file", "Group (grp)"),
            "kts" => ("file", "KTS"),
            _ => ("file", ext.Length == 0 ? "File" : ext),
        };
        if (ext == "g1t") Thumb = TexturePreview.ThumbnailFromG1t(node.FullPath, 128);
    }
}

/// <summary>Outliner "목차": 선택 에셋의 편집/열람 섹션. Material 이 있으면 뷰포트에 그 재질 그리드를 표시.</summary>
public sealed class AssetSectionVM
{
    public string Title { get; }
    public string Icon { get; }
    public GridMaterialVM? Material { get; }
    public IReadOnlyList<ProjectContent.MetaLine> Details { get; }

    public AssetSectionVM(string title, string icon,
                          IReadOnlyList<ProjectContent.MetaLine> details,
                          GridMaterialVM? material = null)
    {
        Title = title;
        Icon = icon;
        Details = details;
        Material = material;
    }
}
