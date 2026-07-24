using System.Diagnostics;

namespace PalPeek.Core;

public sealed record ProcessSnapshot(int ProcessId, string ExecutablePath);

public interface IProcessSource
{
    IReadOnlyList<ProcessSnapshot> Snapshot();
}

public sealed class ProcessSource : IProcessSource
{
    public IReadOnlyList<ProcessSnapshot> Snapshot()
    {
        var result = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                        result.Add(new ProcessSnapshot(process.Id, path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return result;
    }
}

public sealed record DetectionResult(CaptureState State, GameInfo? Game, string? Message);

public sealed class SteamGameDetector
{
    private static readonly TimeSpan StabilizeFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromSeconds(30);
    private SteamCatalog _catalog;
    private readonly Func<SteamCatalog> _refreshCatalog;
    private readonly IProcessSource _processes;
    private readonly IWindowLocator _windows;
    private readonly TimeProvider _clock;
    private DateTimeOffset _nextCatalogRefresh;

    private Candidate? _candidate;
    private GameInfo? _active;
    private DateTimeOffset? _missingSince;

    public SteamGameDetector(
        SteamCatalog catalog,
        IProcessSource? processes = null,
        IWindowLocator? windows = null,
        TimeProvider? clock = null,
        Func<SteamCatalog>? refreshCatalog = null)
    {
        _catalog = catalog;
        _refreshCatalog = refreshCatalog ?? SteamCatalog.Discover;
        _processes = processes ?? new ProcessSource();
        _windows = windows ?? new WindowLocator();
        _clock = clock ?? TimeProvider.System;
        _nextCatalogRefresh = _clock.GetUtcNow() + CatalogRefreshInterval;
    }

    public DetectionResult Tick()
    {
        var now = _clock.GetUtcNow();
        if (now >= _nextCatalogRefresh)
        {
            _nextCatalogRefresh = now + CatalogRefreshInterval;
            try { _catalog = _refreshCatalog(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var matches = _processes.Snapshot()
            .Select(x => (Process: x, App: _catalog.MatchExecutable(x.ExecutablePath)))
            .Where(x => x.App is not null)
            .ToArray();

        if (_active is not null)
        {
            var sameApp = matches.Where(x => x.App!.AppId == _active.AppId).ToArray();
            var pids = sameApp.SelectMany(x => ProcessTree.DescendantsOf(x.Process.ProcessId)).ToHashSet();
            var windows = _windows.FindForProcesses(pids);
            if (windows.Count > 0)
            {
                _missingSince = null;
                var selected = SelectWindow(windows);
                if (_active.WindowHandle != selected.Handle.ToInt64() ||
                    _active.ProcessId != selected.ProcessId)
                    _active = _active with
                    {
                        WindowHandle = selected.Handle.ToInt64(),
                        ProcessId = selected.ProcessId
                    };
                return new(CaptureState.Ready, _active, null);
            }

            _missingSince ??= now;
            if (now - _missingSince < StopGrace)
                return new(CaptureState.WindowUnavailable, _active, "游戏窗口暂时不可用。");
            _active = null;
            _candidate = null;
            _missingSince = null;
            return new(CaptureState.Idle, null, null);
        }

        if (matches.Length == 0)
        {
            _candidate = null;
            return new(CaptureState.Idle, null, null);
        }

        var foregroundPid = _windows.ForegroundProcessId();
        var games = matches
            .GroupBy(x => x.App!.AppId)
            .Select(group =>
            {
                var processIds = group
                    .SelectMany(x => ProcessTree.DescendantsOf(x.Process.ProcessId))
                    .ToHashSet();
                return new GameCandidate(
                    group.First().App!,
                    group.First().Process.ProcessId,
                    processIds,
                    _windows.FindForProcesses(processIds));
            })
            .Where(x => x.Windows.Count > 0)
            .ToArray();
        if (games.Length == 0)
        {
            _candidate = null;
            return new(CaptureState.WindowUnavailable, null, "已检测到 Steam 游戏，正在等待可捕获窗口。");
        }

        var pickedGame = games.FirstOrDefault(x =>
                             foregroundPid is not null &&
                             (x.ProcessIds.Contains(foregroundPid.Value) ||
                              x.Windows.Any(window => window.ProcessId == foregroundPid.Value)))
                         ?? games.OrderByDescending(x => x.Windows.Max(window => window.Area)).First();
        var selectedWindow = SelectWindow(pickedGame.Windows);
        if (_candidate is null ||
            _candidate.App.AppId != pickedGame.App.AppId ||
            _candidate.Window.Handle != selectedWindow.Handle)
        {
            _candidate = new Candidate(
                pickedGame.App, pickedGame.RootProcessId, selectedWindow, now);
            return new(CaptureState.Stabilizing, null, "正在等待游戏窗口稳定。");
        }

        if (now - _candidate.SeenSince < StabilizeFor)
            return new(CaptureState.Stabilizing, null, "正在等待游戏窗口稳定。");

        _active = new GameInfo(
            _candidate.App.AppId,
            _candidate.App.Name,
            _candidate.App.InstallDirectory,
            _candidate.Window.ProcessId,
            _candidate.Window.Handle.ToInt64(),
            Guid.NewGuid().ToString("N"));
        _candidate = null;
        return new(CaptureState.Ready, _active, null);
    }

    private WindowCandidate SelectWindow(IReadOnlyList<WindowCandidate> windows)
    {
        var foreground = _windows.ForegroundProcessId();
        return windows.FirstOrDefault(x => x.ProcessId == foreground)
               ?? windows.OrderByDescending(x => x.Area).First();
    }

    private sealed record Candidate(
        SteamApp App,
        int RootProcessId,
        WindowCandidate Window,
        DateTimeOffset SeenSince);

    private sealed record GameCandidate(
        SteamApp App,
        int RootProcessId,
        IReadOnlySet<int> ProcessIds,
        IReadOnlyList<WindowCandidate> Windows);
}
