using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kashira.Core.Games;
using Kashira.Core.Mods;

namespace Kashira.Gui.ViewModels;

/// <summary>
/// ktmod 상세(순수 표시용) 창의 데이터. 메타데이터 + thumb/preview 이미지 갤러리.
/// 실제 패치/설치 동작에는 아무 영향을 주지 않는다(읽기 전용 뷰).
/// </summary>
public partial class KtmodDetailViewModel : ViewModelBase, IDisposable
{
    public string Name { get; }
    public string Author { get; }
    public string Target { get; }
    public string Description { get; }
    public bool IsCompatible { get; }
    public string CompatText { get; }
    public string FileCountText { get; }

    /// <summary>대표 썸네일(있으면) + preview 갤러리 이미지들.</summary>
    public ObservableCollection<Bitmap> Images { get; } = new();
    public bool HasImages => Images.Count > 0;
    public bool HasGallery => Images.Count > 1;

    /// <summary>크게 보여줄 현재 이미지(갤러리에서 클릭 선택).</summary>
    [ObservableProperty] private Bitmap? _selectedImage;

    public KtmodDetailViewModel(KtmodPackage pkg, GameInstall game)
    {
        Name = pkg.Name;
        Author = string.IsNullOrWhiteSpace(pkg.Author) ? "Unknown author" : pkg.Author;
        Target = string.IsNullOrWhiteSpace(pkg.Target) ? "(none)" : pkg.Target;
        Description = string.IsNullOrWhiteSpace(pkg.Description) ? "(no description)" : pkg.Description;
        IsCompatible = pkg.MatchesGame(game);
        CompatText = IsCompatible ? "Compatible with this game" : "Not for this game";
        FileCountText = $"{pkg.Legacy.Count} file(s)";

        if (pkg.ThumbPng is { } thumb && Decode(thumb) is { } tb) Images.Add(tb);
        foreach (var p in pkg.Previews)
            if (Decode(p) is { } b) Images.Add(b);
        SelectedImage = Images.Count > 0 ? Images[0] : null;
    }

    private static Bitmap? Decode(byte[] png)
    {
        try { using var ms = new MemoryStream(png); return new Bitmap(ms); }
        catch { return null; }
    }

    public void Dispose()
    {
        foreach (var b in Images) b.Dispose();
        Images.Clear();
        SelectedImage = null;
    }
}
