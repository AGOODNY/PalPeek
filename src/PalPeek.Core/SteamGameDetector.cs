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
    private readonly SteamCatalog _catalog;
    private readonly IProcessSource _processes;
    private readonly IWindowLocator _windows;
    private readonly TimeProvider _clock;

    private Candidate? _candidate;
    private GameInfo? _active;
    private DateTimeOffset? _missingSince;

    public SteamGameDetector(
        SteamCatalog catalog,
        IProcessSource? processes = null,
        IWindowLocator? windows = null,
        TimeProvider? clock = null)
    {
        _catalog = catalog;
        _processes = processes ?? new ProcessSource();
        _windows = windows ?? new WindowLocator();
        _clock = clock ?? TimeProvider.System;
    }

    public DetectionResult Tick()
    {
        var now = _clock.GetUtcNow();
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
        var picked = matches.FirstOrDefault(x => x.Process.ProcessId == foregroundPid);
        if (picked.App is null)
            picked = matches[0];
        var pickedApp = picked.App!;

        var processIds = ProcessTree.DescendantsOf(picked.Process.ProcessId);
        var candidates = _windows.FindForProcesses(processIds);
        if (candidates.Count == 0)
        {
            _candidate = null;
            return new(CaptureState.WindowUnavailable, null, "已检测到 Steam 游戏，正在等待可捕获窗口。");
        }

        var selectedWindow = SelectWindow(candidates);
        if (_candidate is null ||
            _candidate.App.AppId != pickedApp.AppId ||
            _candidate.Window.Handle != selectedWindow.Handle)
        {
            _candidate = new Candidate(pickedApp, picked.Process.ProcessId, selectedWindow, now);
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
}
