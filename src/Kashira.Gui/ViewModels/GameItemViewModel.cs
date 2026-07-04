using System.IO;
using Avalonia.Media.Imaging;
using Kashira.Core.Games;

namespace Kashira.Gui.ViewModels;

/// <summary>런처 목록의 한 항목. Core 의 GameInstall 을 감싸고 exe 아이콘을 노출한다.</summary>
public sealed class GameItemViewModel
{
    public GameInstall Model { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;

    public string DisplayName => Model.DisplayName;
    public string InstallPath => Model.InstallPath;

    /// <summary>아이콘 폴백용 첫 글자.</summary>
    public string InitialLetter =>
        string.IsNullOrEmpty(Model.DisplayName) ? "?" : Model.DisplayName[..1].ToUpperInvariant();

    public GameItemViewModel(GameInstall model, byte[]? icoBytes)
    {
        Model = model;
        Icon = BitmapFromIco(icoBytes);
    }

    private static Bitmap? BitmapFromIco(byte[]? ico)
    {
        if (ico is null || ico.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(ico);
            return new Bitmap(ms);
        }
        catch
        {
            return null; // 디코드 실패 → 플레이스홀더
        }
    }
}
