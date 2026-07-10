using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Kashira.Editor.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>폴더 선택기(New 프로젝트 위치). 취소 시 null.</summary>
    public async Task<string?> PickFolderAsync()
    {
        var top = GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a location for the project",
            AllowMultiple = false,
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>확장자 필터 파일 선택기(예: g1m/grp). 취소 시 null.</summary>
    public async Task<string?> PickFileAsync(string title, string ext)
    {
        var top = GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } },
            },
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>프로젝트 열기 선택기(.ktproj 파일 선택). 취소 시 null.</summary>
    public async Task<string?> PickProjectAsync()
    {
        var top = GetTopLevel(this);
        if (top is null) return null;
        var result = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a Kashira project (.ktproj)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Kashira project") { Patterns = new[] { "*.ktproj" } },
            },
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }
}
