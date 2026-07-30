using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PalPeek.Core;

public sealed record ProcessSnapshot(
    int ProcessId,
    string? ExecutablePath,
    string? ExecutableName = null,
    bool ExecutablePathAccessDenied = false)
{
    public string? EffectiveExecutableName =>
        !string.IsNullOrWhiteSpace(ExecutableName)
            ? Path.GetFileName(ExecutableName)
            : string.IsNullOrWhiteSpace(ExecutablePath)
                ? null
                : Path.GetFileName(ExecutablePath);
}

public interface IProcessSource
{
    IReadOnlyList<ProcessSnapshot> Snapshot();
}

public sealed class ProcessSource : IProcessSource
{
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const int ErrorAccessDenied = 5;
    private const int MaximumPathLength = 32_768;

    public IReadOnlyList<ProcessSnapshot> Snapshot()
    {
        var result = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int processId;
                string? executableName;
                try
                {
                    processId = process.Id;
                    executableName = process.ProcessName + ".exe";
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    continue;
                }

                var path = ResolveExecutablePath(process, processId);
                if (!string.IsNullOrWhiteSpace(path.Path) ||
                    !string.IsNullOrWhiteSpace(executableName))
                    result.Add(new ProcessSnapshot(
                        processId,
                        path.Path,
                        executableName,
                        path.AccessDenied));
            }
        }
        return result;
    }

    private static ProcessPathResult ResolveExecutablePath(
        Process process,
        int processId)
    {
        var accessDenied = false;
        using (var handle = OpenProcess(
                   ProcessQueryLimitedInformation,
                   inheritHandle: false,
                   (uint)processId))
        {
            if (!handle.IsInvalid)
            {
                var buffer = new StringBuilder(MaximumPathLength);
                var length = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref length))
                    return new(buffer.ToString(), false);
                accessDenied = Marshal.GetLastWin32Error() == ErrorAccessDenied;
            }
            else
            {
                accessDenied = Marshal.GetLastWin32Error() == ErrorAccessDenied;
            }
        }

        try
        {
            var path = process.MainModule?.FileName;
            return new(path, accessDenied);
        }
        catch (Win32Exception ex)
        {
            return new(null, accessDenied || ex.NativeErrorCode == ErrorAccessDenied);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return new(null, accessDenied || ex is UnauthorizedAccessException);
        }
    }

    private sealed record ProcessPathResult(string? Path, bool AccessDenied);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref int size);
}

public sealed record DetectionResult(CaptureState State, GameInfo? Game, string? Message);

public sealed class SteamGameDetector
{
    private const string CompatibilityReadyMessage =
        "反作弊保护阻止了常规进程读取，PalPeek 已通过兼容模式识别游戏。";
    private const string CompatibilityWaitingMessage =
        "检测到受反作弊保护的游戏窗口，但无法读取常规进程信息；PalPeek 正在尝试兼容识别。";
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
    private bool _activeUsesCompatibilityMode;
    private DateTimeOffset? _missingSince;
    private readonly Dictionary<string, uint?> _protectedExecutableMatches =
        new(StringComparer.OrdinalIgnoreCase);

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
            try
            {
                _catalog = _refreshCatalog();
                foreach (var key in _protectedExecutableMatches
                             .Where(pair => pair.Value is null)
                             .Select(pair => pair.Key)
                             .ToArray())
                    _protectedExecutableMatches.Remove(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var snapshots = _processes.Snapshot();
        var protectedProcesses = snapshots
            .Where(process =>
                string.IsNullOrWhiteSpace(process.ExecutablePath) &&
                process.ExecutablePathAccessDenied &&
                !string.IsNullOrWhiteSpace(process.EffectiveExecutableName))
            .ToArray();
        var protectedProcessIds =
            protectedProcesses.Select(process => process.ProcessId).ToHashSet();
        var protectedWindows = protectedProcessIds.Count == 0
            ? Array.Empty<WindowCandidate>()
            : _windows.FindForProcesses(protectedProcessIds);
        var visibleProtectedProcessIds =
            protectedWindows.Select(window => window.ProcessId).ToHashSet();

        var matches = new List<ProcessMatch>();
        foreach (var process in snapshots)
        {
            var app = _catalog.MatchExecutable(process.ExecutablePath);
            var compatibilityMode = false;
            if (app is null &&
                string.IsNullOrWhiteSpace(process.ExecutablePath) &&
                process.ExecutablePathAccessDenied &&
                visibleProtectedProcessIds.Contains(process.ProcessId))
            {
                app = MatchProtectedExecutable(process.EffectiveExecutableName);
                compatibilityMode = app is not null;
            }
            if (app is not null)
                matches.Add(new ProcessMatch(process, app, compatibilityMode));
        }

        if (_active is not null)
        {
            var sameApp = matches.Where(x => x.App.AppId == _active.AppId).ToArray();
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
                return new(
                    CaptureState.Ready,
                    _active,
                    _activeUsesCompatibilityMode ? CompatibilityReadyMessage : null);
            }

            _missingSince ??= now;
            if (now - _missingSince < StopGrace)
                return new(CaptureState.WindowUnavailable, _active, "游戏窗口暂时不可用。");
            _active = null;
            _activeUsesCompatibilityMode = false;
            _candidate = null;
            _missingSince = null;
            return new(CaptureState.Idle, null, null);
        }

        if (matches.Count == 0)
        {
            _candidate = null;
            if (visibleProtectedProcessIds.Count > 0)
                return new(CaptureState.WindowUnavailable, null, CompatibilityWaitingMessage);
            return new(CaptureState.Idle, null, null);
        }

        var foregroundPid = _windows.ForegroundProcessId();
        var games = matches
            .GroupBy(x => x.App.AppId)
            .Select(group =>
            {
                var processIds = group
                    .SelectMany(x => ProcessTree.DescendantsOf(x.Process.ProcessId))
                    .ToHashSet();
                return new GameCandidate(
                    group.First().App,
                    group.First().Process.ProcessId,
                    processIds,
                    _windows.FindForProcesses(processIds),
                    group.Any(match => match.CompatibilityMode));
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
                pickedGame.App,
                pickedGame.RootProcessId,
                selectedWindow,
                now,
                pickedGame.CompatibilityMode);
            return new(
                CaptureState.Stabilizing,
                null,
                pickedGame.CompatibilityMode
                    ? "反作弊保护阻止了常规进程读取，已启用兼容识别，正在等待游戏窗口稳定。"
                    : "正在等待游戏窗口稳定。");
        }

        if (now - _candidate.SeenSince < StabilizeFor)
            return new(
                CaptureState.Stabilizing,
                null,
                _candidate.CompatibilityMode
                    ? "反作弊保护阻止了常规进程读取，已启用兼容识别，正在等待游戏窗口稳定。"
                    : "正在等待游戏窗口稳定。");

        _active = new GameInfo(
            _candidate.App.AppId,
            _candidate.App.Name,
            _candidate.App.InstallDirectory,
            _candidate.Window.ProcessId,
            _candidate.Window.Handle.ToInt64(),
            Guid.NewGuid().ToString("N"));
        _activeUsesCompatibilityMode = _candidate.CompatibilityMode;
        _candidate = null;
        return new(
            CaptureState.Ready,
            _active,
            _activeUsesCompatibilityMode ? CompatibilityReadyMessage : null);
    }

    private SteamApp? MatchProtectedExecutable(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return null;
        var fileName = Path.GetFileName(executableName);
        if (_protectedExecutableMatches.TryGetValue(fileName, out var cachedAppId))
        {
            if (cachedAppId is null)
                return null;
            var cached = _catalog.FindByAppId(cachedAppId.Value);
            if (cached is not null)
                return cached;
            _protectedExecutableMatches.Remove(fileName);
        }

        var match = _catalog.MatchExecutableName(fileName);
        _protectedExecutableMatches[fileName] = match?.AppId;
        return match;
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
        DateTimeOffset SeenSince,
        bool CompatibilityMode);

    private sealed record GameCandidate(
        SteamApp App,
        int RootProcessId,
        IReadOnlySet<int> ProcessIds,
        IReadOnlyList<WindowCandidate> Windows,
        bool CompatibilityMode);

    private sealed record ProcessMatch(
        ProcessSnapshot Process,
        SteamApp App,
        bool CompatibilityMode);
}
