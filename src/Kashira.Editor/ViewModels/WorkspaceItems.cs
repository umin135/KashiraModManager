using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kashira.Core.Formats;
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

/// <summary>Outliner "목차": 선택 에셋의 편집/열람 섹션. Material=재질 그리드, G1mSectionId=g1m 섹션 편집기.</summary>
public sealed class AssetSectionVM
{
    public string Title { get; }
    public string Icon { get; }
    public GridMaterialVM? Material { get; }
    /// <summary>G1MG 서브섹션 id(예 0x10003). 그 외 null.</summary>
    public uint? G1mSectionId { get; }
    /// <summary>최상위 청크 인덱스(G1MF/G1MS/NUNO 등). 그 외 null.</summary>
    public int? G1mChunkIndex { get; }
    /// <summary>raw export/import 대상(로케이터가 있으면).</summary>
    public bool IsG1mSection => G1mSectionId is not null || G1mChunkIndex is not null;
    public IReadOnlyList<ProjectContent.MetaLine> Details { get; }

    public AssetSectionVM(string title, string icon,
                          IReadOnlyList<ProjectContent.MetaLine> details,
                          GridMaterialVM? material = null, uint? g1mSectionId = null, int? g1mChunkIndex = null)
    {
        Title = title;
        Icon = icon;
        Details = details;
        Material = material;
        G1mSectionId = g1mSectionId;
        G1mChunkIndex = g1mChunkIndex;
    }
}

/// <summary>nMtrID 선택지(값 + 라벨).</summary>
public sealed record NMtrOption(int Value, string Label);

/// <summary>서브메시 분석 한 행(읽기 전용).</summary>
public sealed class SubmeshRowVM
{
    public string Sub { get; }
    public string Vb { get; }
    public string Vsize { get; }
    public string Mat { get; }
    public string Verts { get; }
    public string Tris { get; }
    public string Cloth { get; }

    public SubmeshRowVM(G1mGeometry.Submesh s)
    {
        Sub = $"sub{s.Index}";
        Vb = $"vb{s.VbRef}";
        Vsize = s.Vsize.ToString();
        Mat = $"mat{s.Material}";
        Verts = s.NumVerts.ToString("N0");
        Tris = s.Tris.ToString("N0");
        Cloth = s.ClothId == 0 ? "" : $"cloth{s.ClothId}";
    }
}

/// <summary>g1m 재질 하나의 타입(nMtrID) 편집 VM. 0x10003 PropertySet 편집기용.</summary>
public sealed partial class G1mMaterialVM : ObservableObject
{
    public static readonly NMtrOption[] Known =
    {
        new(0, "0 · 기본 오파크"), new(1, "1 · SSS 피부"), new(2, "2 · 레이어드 웻"),
        new(4, "4 · (미확정)"), new(6, "6 · 알파 컷아웃"), new(11, "11 · coeffs"),
    };

    public int Index { get; }
    public string Name { get; }
    public string Info { get; }
    public IReadOnlyList<NMtrOption> Options { get; }
    [ObservableProperty] private NMtrOption _selectedType;

    public int SelectedValue => SelectedType.Value;

    public G1mMaterialVM(G1mMaterialProps.MatProp p)
    {
        Index = p.Index;
        Name = $"Material {p.Index}";
        Info = (p.RmIndex is { } r ? $"rmIndex {r}" : "")
             + (p.FThick is { } f ? $"  fThick {f:0.##}" : "");
        var opts = new List<NMtrOption>(Known);
        if (opts.All(o => o.Value != p.NMtrID))
            opts.Insert(0, new NMtrOption(p.NMtrID, $"{p.NMtrID} · (미확정)"));
        Options = opts;
        _selectedType = opts.First(o => o.Value == p.NMtrID);
    }
}
