using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Kashira.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>KatanaEngine 게임 폴더 선택기. 취소 시 null.</summary>
    public async Task<string?> PickGameFolderAsync()
    {
        var top = GetTopLevel(this);
        if (top is null) return null;

        var result = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a KatanaEngine game folder",
            AllowMultiple = false,
        });

        return result.FirstOrDefault()?.TryGetLocalPath();
    }
}
