using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Games;
using Kashira.Core.Mods;
using Kashira.Core.Patching;

namespace Kashira.Gui.ViewModels;

public enum GameDetailTab { DebugMods, Mods }

/// <summary>레벨2 게임 상세: DebugMods(주입 파일 리스트) / Mods(.ktmod, 표시만) 탭 전환 + Apply/Revert.</summary>
public partial class GameDetailViewModel : ViewModelBase
{
    private readonly GameInstall _game;
    private readonly GameWorkspace _ws;
    private readonly Action _back;

    public string GameName => _game.DisplayName;
    public string InstallPath => _game.InstallPath;

    /// <summary>Steam 실행옵션에 붙여넣을 문자열 (게임 실행 전 모드 자동 Apply).</summary>
    public string SteamLaunchCommand
    {
        get
        {
            var exe = Environment.ProcessPath ?? "Kashira.exe";
            return $"\"{exe}\" --steam-run -- %command%";
        }
    }

    /// <summary>DebugMods 폴더의 주입 대상 파일들.</summary>
    public ObservableCollection<DebugModItemViewModel> DebugMods { get; } = new();

    /// <summary>Mods 폴더의 .ktmod 패키지들.</summary>
    public ObservableCollection<KtmodItemViewModel> Mods { get; } = new();

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _needsReapply;
    [ObservableProperty] private string _patchStateText = "";
    [ObservableProperty] private GameDetailTab _tab = GameDetailTab.DebugMods;

    public bool IsDebugTab => Tab == GameDetailTab.DebugMods;
    public bool IsModsTab => Tab == GameDetailTab.Mods;

    public bool DebugModsEmpty => DebugMods.Count == 0;
    public bool ModsEmpty => Mods.Count == 0;

    partial void OnTabChanged(GameDetailTab value)
    {
        OnPropertyChanged(nameof(IsDebugTab));
        OnPropertyChanged(nameof(IsModsTab));
    }

    [RelayCommand] private void ShowDebug() => Tab = GameDetailTab.DebugMods;
    [RelayCommand] private void ShowMods() => Tab = GameDetailTab.Mods;

    public GameDetailViewModel(GameInstall game, Action back)
    {
        _game = game;
        _ws = new GameWorkspace(game);
        _back = back;
        _ws.EnsureFolders();
        Refresh();
    }

    [RelayCommand]
    private void Back() => _back();

    [RelayCommand]
    private void Refresh()
    {
        DebugMods.Clear();
        foreach (var e in Core.Patching.DebugMods.List(_ws.DebugModsDir))
            DebugMods.Add(new DebugModItemViewModel(e));

        Mods.Clear();
        if (Directory.Exists(_ws.ModsDir))
            foreach (var f in Directory.EnumerateFiles(_ws.ModsDir, "*.ktmod").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var pkg = KtmodPackage.Load(f);
                if (pkg is not null) Mods.Add(new KtmodItemViewModel(pkg, _game));
            }

        var st = PatchEngine.GetStatus(_ws);
        NeedsReapply = st == PatchStatus.NeedsReapply;
        PatchStateText = st switch
        {
            PatchStatus.Patched => "PATCHED",
            PatchStatus.NeedsReapply => "NEEDS RE-APPLY",
            _ => "CLEAN",
        };
        OnPropertyChanged(nameof(DebugModsEmpty));
        OnPropertyChanged(nameof(ModsEmpty));
        Status = $"{DebugMods.Count} inject file(s), {Mods.Count} .ktmod  ·  DB: {string.Join(", ", _ws.Databases)}  ·  {PatchStateText}";
    }

    [RelayCommand]
    private async Task Apply()
    {
        Status = "Applying…";
        var gather = await Task.Run(() => ModApplier.Gather(_ws, _game));
        if (gather.Replacements.Count == 0)
        {
            Status = "Nothing to apply. Drop 0x{ktid}.ext files into DebugMods, or a compatible .ktmod into Mods.";
            return;
        }

        var report = await Task.Run(() => PatchEngine.Apply(_ws, gather.Replacements));

        Refresh(); // 상태 재계산(디스크 기준)
        var incompat = gather.Incompatible.Count > 0
            ? $"  Skipped (wrong game): {string.Join(", ", gather.Incompatible)}"
            : "";
        Status = $"Applied {report.Applied}/{report.Requested}  ·  DebugMods {gather.DebugCount}, ktmod {gather.KtmodCount}.{incompat}";
    }

    [RelayCommand]
    private async Task Revert()
    {
        Status = "Reverting…";
        await Task.Run(() => PatchEngine.Revert(_ws));
        Refresh();
        Status = "Reverted to original.";
    }

    [RelayCommand]
    private void OpenFolder() => OpenInExplorer(IsModsTab ? _ws.ModsDir : _ws.DebugModsDir);

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}

/// <summary>DebugMods 파일 1개의 표시용 래퍼.</summary>
public sealed class DebugModItemViewModel
{
    private readonly Core.Patching.DebugMods.Entry _e;
    public DebugModItemViewModel(Core.Patching.DebugMods.Entry e) => _e = e;

    public string FileName => _e.FileName;
    public string KtidHex => $"0x{_e.FileKtid:x8}";
    public string SizeText => _e.Size >= 1024 * 1024
        ? $"{_e.Size / 1024.0 / 1024.0:0.0} MB"
        : $"{_e.Size / 1024.0:0.0} KB";
}

/// <summary>Mods 탭의 .ktmod 패키지 1개 표시용 래퍼.</summary>
public sealed class KtmodItemViewModel
{
    private readonly KtmodPackage _pkg;
    private readonly GameInstall _game;

    public KtmodItemViewModel(KtmodPackage pkg, GameInstall game)
    {
        _pkg = pkg;
        _game = game;
        if (pkg.ThumbPng is not null)
        {
            try { Thumb = new Bitmap(new MemoryStream(pkg.ThumbPng)); }
            catch { /* 손상 PNG → 플레이스홀더 */ }
        }
    }

    public string Name => _pkg.Name;
    public string Author => string.IsNullOrWhiteSpace(_pkg.Author) ? "Unknown author" : _pkg.Author!;
    public string Description => string.IsNullOrWhiteSpace(_pkg.Description) ? "(no description)" : _pkg.Description!;
    public string TargetText => string.IsNullOrWhiteSpace(_pkg.Target) ? "Target: (none)" : $"Target: {_pkg.Target}";
    public string InfoLine => $"{_pkg.Legacy.Count} file(s)  ·  {(IsCompatible ? "Compatible" : "Not this game")}";
    public bool IsCompatible => _pkg.MatchesGame(_game);

    public Bitmap? Thumb { get; }
    public bool HasThumb => Thumb is not null;
}
