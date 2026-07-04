using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Games;
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

    /// <summary>DebugMods 폴더의 주입 대상 파일들.</summary>
    public ObservableCollection<DebugModItemViewModel> DebugMods { get; } = new();

    /// <summary>Mods 폴더의 .ktmod 패키지들 (표시만, 미구현).</summary>
    public ObservableCollection<string> Mods { get; } = new();

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
            foreach (var f in Directory.EnumerateFiles(_ws.ModsDir, "*.ktmod").OrderBy(x => x))
                Mods.Add(Path.GetFileName(f));

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
        var files = Core.Patching.DebugMods.List(_ws.DebugModsDir);
        if (files.Count == 0) { Status = "No DebugMods files to inject. Drop 0x{ktid}.ext files there first."; return; }

        Status = "Applying…";
        var reps = files.Select(e => new PatchEngine.Replacement(e.FileKtid, File.ReadAllBytes(e.FullPath))).ToList();
        var report = await Task.Run(() => PatchEngine.Apply(_ws, reps));

        Refresh(); // 상태 재계산(디스크 기준)
        var notFound = report.NotFound.Count > 0
            ? $"  Not found in any DB: {string.Join(", ", report.NotFound.Select(k => $"0x{k:x8}"))}"
            : "";
        Status = $"Applied {report.Applied}/{report.Requested}.{notFound}";
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
