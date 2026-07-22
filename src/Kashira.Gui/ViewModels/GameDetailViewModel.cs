using System;
using System.Collections.Generic;
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

/// <summary>레벨2 게임 상세: DebugMods(주입 파일) / Mods(프로필 기반 우선순위·enable 편집) 탭 + Apply/Revert.
/// Mods 탭: 프로필 선택/생성/삭제, 활성 프로필의 모드 순서(상단=최우선)·활성 여부를 편집(자동저장).
/// Default 프로필은 불변(전부 Enable·ABC). 활성 프로필은 Apply·Steam 자동설치 양쪽에 반영.</summary>
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

    /// <summary>선택된 프로필의 모드 목록(우선순위 순, 상단=최우선). 체크박스로 enable, ▲▼로 재정렬.</summary>
    public ObservableCollection<ModRowVM> ModRows { get; } = new();

    /// <summary>설치된 .ktmod(Name → 표시 래퍼). 프로필 해석용.</summary>
    private readonly Dictionary<string, KtmodItemViewModel> _modPkgs = new(StringComparer.OrdinalIgnoreCase);

    private ModProfiles _profiles = new();
    private bool _loadingProfile;

    /// <summary>프로필 선택 드롭다운(Default + 사용자 프로필). 선택 = 활성 프로필.</summary>
    public ObservableCollection<string> ProfileNames { get; } = new();
    [ObservableProperty] private string _selectedProfile = ModProfiles.DefaultName;
    [ObservableProperty] private string _newProfileName = "";

    public bool IsDefaultProfile => ModProfiles.IsDefault(SelectedProfile);
    public bool CanEditProfile => !IsDefaultProfile;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _needsReapply;
    [ObservableProperty] private string _patchStateText = "";
    [ObservableProperty] private GameDetailTab _tab = GameDetailTab.Mods;

    public bool IsDebugTab => Tab == GameDetailTab.DebugMods;
    public bool IsModsTab => Tab == GameDetailTab.Mods;

    public bool DebugModsEmpty => DebugMods.Count == 0;
    public bool ModsEmpty => ModRows.Count == 0;

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

        _modPkgs.Clear();
        if (Directory.Exists(_ws.ModsDir))
            foreach (var f in Directory.EnumerateFiles(_ws.ModsDir, "*.ktmod").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var pkg = KtmodPackage.Load(f);
                if (pkg is not null) _modPkgs[pkg.Name] = new KtmodItemViewModel(pkg, _game);
            }

        // 프로필 로드 → 드롭다운 채우고 활성 프로필 선택(반영은 ActivateProfile).
        _profiles = ModProfiles.Load(_ws);
        RebuildProfileList();
        _loadingProfile = true;
        SelectedProfile = ProfileNames.Contains(_profiles.Active) ? _profiles.Active : ModProfiles.DefaultName;
        _loadingProfile = false;
        ActivateProfile(SelectedProfile, persist: false);

        var st = PatchEngine.GetStatus(_ws);
        NeedsReapply = st == PatchStatus.NeedsReapply;
        PatchStateText = st switch
        {
            PatchStatus.Patched => "PATCHED",
            PatchStatus.NeedsReapply => "NEEDS RE-APPLY",
            _ => "CLEAN",
        };
        OnPropertyChanged(nameof(DebugModsEmpty));
        Status = $"{DebugMods.Count} inject file(s), {_modPkgs.Count} .ktmod  ·  Profile: {SelectedProfile}  ·  DB: {string.Join(", ", _ws.Databases)}  ·  {PatchStateText}";
    }

    private void RebuildProfileList()
    {
        // ProfileNames 를 Clear 하면 ComboBox 가 SelectedProfile 을 null 로 리셋하며 콜백을 유발 →
        // 재구성 동안은 콜백을 억제(재진입/활성값 오염 방지).
        bool prev = _loadingProfile;
        _loadingProfile = true;
        ProfileNames.Clear();
        ProfileNames.Add(ModProfiles.DefaultName);
        foreach (var p in _profiles.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            ProfileNames.Add(p.Name);
        _loadingProfile = prev;
    }

    partial void OnSelectedProfileChanged(string value)
    {
        OnPropertyChanged(nameof(IsDefaultProfile));
        OnPropertyChanged(nameof(CanEditProfile));
        if (_loadingProfile || string.IsNullOrEmpty(value)) return;
        ActivateProfile(value, persist: true);
    }

    /// <summary>프로필을 활성화: 활성값 기록 + (사용자 프로필이면) 설치 목록에 맞춰 정규화 + 행 재구성.</summary>
    private void ActivateProfile(string name, bool persist)
    {
        if (string.IsNullOrEmpty(name)) return;
        _profiles.Active = name;
        if (!ModProfiles.IsDefault(name)) _profiles.Normalize(name, _modPkgs.Keys);
        if (persist) _profiles.Save(_ws);
        BuildModRows();
        if (persist) Status = $"Active profile: {name}";
    }

    private void BuildModRows()
    {
        ModRows.Clear();
        bool editable = CanEditProfile;
        int priority = 1;
        foreach (var rm in _profiles.Resolve(SelectedProfile, _modPkgs.Keys))
        {
            if (!_modPkgs.TryGetValue(rm.Mod, out var pkg)) continue;
            ModRows.Add(new ModRowVM(pkg, rm.Enabled, editable, priority++,
                OnRowEnabledChanged, r => MoveMod(r, -1), r => MoveMod(r, +1)));
        }
        OnPropertyChanged(nameof(ModsEmpty));
    }

    /// <summary>체크박스 토글 → 활성 프로필 슬롯의 Enabled 갱신 후 저장(자동저장).</summary>
    private void OnRowEnabledChanged(ModRowVM row, bool enabled)
    {
        var p = _profiles.Find(SelectedProfile);
        if (p is null) return;
        int idx = ModRows.IndexOf(row);
        if (idx < 0 || idx >= p.Mods.Count) return;
        p.Mods[idx].Enabled = enabled;
        _profiles.Save(_ws);
        Status = $"Profile '{p.Name}' saved.";
    }

    /// <summary>▲▼ → 프로필 슬롯 순서 교환 후 저장·행 재구성(우선순위 갱신).</summary>
    private void MoveMod(ModRowVM row, int delta)
    {
        var p = _profiles.Find(SelectedProfile);
        if (p is null) return;
        int idx = ModRows.IndexOf(row);
        int j = idx + delta;
        if (idx < 0 || j < 0 || j >= p.Mods.Count) return;
        (p.Mods[idx], p.Mods[j]) = (p.Mods[j], p.Mods[idx]);
        _profiles.Save(_ws);
        BuildModRows();
        Status = $"Profile '{p.Name}' reordered.";
    }

    [RelayCommand]
    private void CreateProfile()
    {
        var name = (NewProfileName ?? "").Trim();
        if (name.Length == 0) { Status = "Enter a profile name."; return; }
        if (ModProfiles.IsDefault(name) || _profiles.Find(name) is not null)
        { Status = $"Profile '{name}' already exists."; return; }

        _profiles.CreateProfile(name, _modPkgs.Keys);
        _profiles.Active = name;
        _profiles.Save(_ws);
        RebuildProfileList();
        _loadingProfile = true;
        SelectedProfile = name;
        _loadingProfile = false;
        ActivateProfile(name, persist: true);
        NewProfileName = "";
        Status = $"Created profile '{name}'.";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (IsDefaultProfile) return;
        var name = SelectedProfile;
        _profiles.Remove(name);
        _profiles.Active = ModProfiles.DefaultName;
        _profiles.Save(_ws);
        RebuildProfileList();
        _loadingProfile = true;
        SelectedProfile = ModProfiles.DefaultName;
        _loadingProfile = false;
        ActivateProfile(ModProfiles.DefaultName, persist: true);
        Status = $"Deleted profile '{name}'.";
    }

    [RelayCommand]
    private async Task Apply()
    {
        Status = "Applying…";
        var gather = await Task.Run(() => ModApplier.Gather(_ws, _game));

        // 활성화된 모드가 없어도 Apply 를 호출한다 — 빈 목록이면 엔진이 현재 원본으로 복구(패치 해제)한다.
        // (early-return 하면 이전에 패치된 rdb 가 그대로 남아 혼란스러움.)
        var report = await Task.Run(() => PatchEngine.Apply(_ws, gather.Replacements));

        Refresh(); // 상태 재계산(디스크 기준)
        if (gather.Replacements.Count == 0)
        {
            Status = "No enabled mods — restored the game to its original files.";
            return;
        }
        var incompat = gather.Incompatible.Count > 0
            ? $"  Skipped (wrong game): {string.Join(", ", gather.Incompatible)}"
            : "";
        var conflict = gather.Conflicts > 0
            ? $", {gather.Conflicts} conflict(s) resolved by priority"
            : "";
        Status = $"Applied {report.Applied}/{report.Requested}  ·  Profile {SelectedProfile}  ·  DebugMods {gather.DebugCount}, ktmod {gather.KtmodCount}{conflict}.{incompat}";
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

    /// <summary>더블클릭 상세 창(순수 표시용)의 데이터 생성. 동작에는 영향 없음.</summary>
    public KtmodDetailViewModel CreateDetail() => new(_pkg, _game);
}

/// <summary>프로필 모드 목록의 한 행 — .ktmod 표시정보 + Enabled 토글 + ▲▼ 재정렬(우선순위).</summary>
public partial class ModRowVM : ObservableObject
{
    private readonly KtmodItemViewModel _pkg;
    private readonly Action<ModRowVM, bool>? _onEnabled;
    private readonly Action<ModRowVM>? _moveUp;
    private readonly Action<ModRowVM>? _moveDown;
    private readonly bool _suppress;

    public ModRowVM(KtmodItemViewModel pkg, bool enabled, bool editable, int priority,
                    Action<ModRowVM, bool>? onEnabled, Action<ModRowVM>? moveUp, Action<ModRowVM>? moveDown)
    {
        _pkg = pkg;
        Editable = editable;
        Priority = priority;
        _onEnabled = onEnabled;
        _moveUp = moveUp;
        _moveDown = moveDown;
        _suppress = true; Enabled = enabled; _suppress = false; // 초기 설정은 콜백 억제
    }

    public string Name => _pkg.Name;
    public string Author => _pkg.Author;
    public string Description => _pkg.Description;
    public string TargetText => _pkg.TargetText;
    public string InfoLine => _pkg.InfoLine;
    public Bitmap? Thumb => _pkg.Thumb;
    public bool HasThumb => _pkg.HasThumb;

    public int Priority { get; }
    public string PriorityText => $"#{Priority}";
    public bool Editable { get; }

    [ObservableProperty] private bool _enabled;
    partial void OnEnabledChanged(bool value) { if (!_suppress) _onEnabled?.Invoke(this, value); }

    [RelayCommand] private void MoveUp() => _moveUp?.Invoke(this);
    [RelayCommand] private void MoveDown() => _moveDown?.Invoke(this);

    /// <summary>더블클릭 상세 창 데이터 생성(표시 전용).</summary>
    public KtmodDetailViewModel CreateDetail() => _pkg.CreateDetail();
}
