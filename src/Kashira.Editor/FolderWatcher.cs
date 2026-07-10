using System;
using System.IO;
using System.Timers;

namespace Kashira.Editor;

/// <summary>
/// 폴더(하위 포함)를 FileSystemWatcher 로 감시하되, 이벤트 버스트를 디바운스해
/// "마지막 변경 후 debounceMs 경과 시 1회"만 콜백한다(매 이벤트/폴링 대비 효율적).
/// 콜백은 타이머 스레드에서 호출되므로 UI 갱신 시 호출측이 Dispatcher 로 마샬링할 것.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher? _fsw;
    private readonly System.Timers.Timer _debounce;
    private readonly Action _onChanged;

    public FolderWatcher(string dir, Action onChanged, int debounceMs = 300)
    {
        _onChanged = onChanged;
        _debounce = new System.Timers.Timer(debounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) => _onChanged();

        if (!Directory.Exists(dir)) return; // 폴더 없으면 감시 안 함(정상)

        _fsw = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _fsw.Created += OnAny;
        _fsw.Deleted += OnAny;
        _fsw.Changed += OnAny;
        _fsw.Renamed += OnAny;
        _fsw.EnableRaisingEvents = true;
    }

    private void OnAny(object? sender, FileSystemEventArgs e) => Bump();

    private void Bump()
    {
        _debounce.Stop();
        _debounce.Start(); // 마지막 이벤트 기준 재시작 → 버스트를 1회로 합침
    }

    public void Dispose()
    {
        if (_fsw is not null)
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }
        _debounce.Dispose();
    }
}
