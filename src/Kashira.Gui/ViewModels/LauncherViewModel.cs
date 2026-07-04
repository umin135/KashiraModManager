using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kashira.Core.Games;
using Kashira.Core.Icons;

namespace Kashira.Gui.ViewModels;

/// <summary>레벨1 런처: 게임 감지/추가/선택.</summary>
public partial class LauncherViewModel : ViewModelBase
{
    public string Title => "Kashira";
    public string Subtitle => "Katana Engine Universal Mod Manager";

    public ObservableCollection<GameItemViewModel> Games { get; } = new();

    /// <summary>폴더 선택기 (View 가 주입).</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>게임 선택 시 상세로 이동 (셸이 주입).</summary>
    public Action<GameInstall>? OpenGame { get; set; }

    [ObservableProperty]
    private GameItemViewModel? _selectedGame;

    [ObservableProperty]
    private string _status = "Scan for games or add one manually.";

    private bool _initialized;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var saved = GameLibrary.Load();
        if (saved.Count == 0) return;
        foreach (var item in await BuildItemsAsync(saved)) Games.Add(item);
        Status = $"{Games.Count} game(s) loaded.";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        Status = "Scanning Steam libraries…";
        var found = await Task.Run(SteamScanner.Scan);

        var known = Paths();
        var fresh = found.Where(g => !known.Contains(g.InstallPath)).ToList();
        foreach (var item in await BuildItemsAsync(fresh)) Games.Add(item);

        Persist();
        Status = Games.Count == 0
            ? "No supported games found. Check your Steam/game install, or use New to add one."
            : $"{Games.Count} game(s) — {fresh.Count} new this scan.";
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (FolderPicker is null) return;
        var path = await FolderPicker();
        if (string.IsNullOrEmpty(path)) return;

        if (Paths().Contains(path)) { Status = "That folder is already in the list."; return; }

        var game = SteamScanner.FromPath(path);
        if (game is null) { Status = "Not a KatanaEngine game (no fdata_package/root.rdb)."; return; }

        var items = await BuildItemsAsync(new[] { game });
        Games.Add(items[0]);
        Persist();
        Status = $"Added '{game.DisplayName}'.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedGame is null) return;
        Games.Remove(SelectedGame);
        SelectedGame = null;
        Persist();
        Status = "Removed.";
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void Select()
    {
        if (SelectedGame is not null) OpenGame?.Invoke(SelectedGame.Model);
    }

    private bool HasSelection() => SelectedGame is not null;
    private bool CanSelect() => SelectedGame is { Model.Supported: true };

    partial void OnSelectedGameChanged(GameItemViewModel? value)
    {
        SelectCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    private HashSet<string> Paths() =>
        new(Games.Select(g => g.Model.InstallPath), StringComparer.OrdinalIgnoreCase);

    private void Persist() => GameLibrary.Save(Games.Select(g => g.Model));

    private static Task<List<GameItemViewModel>> BuildItemsAsync(IEnumerable<GameInstall> games) =>
        Task.Run(() => games.Select(g =>
            new GameItemViewModel(g, g.ExePath is null ? null : PeIconExtractor.ExtractIco(g.ExePath))).ToList());
}
