using Microsoft.Extensions.Hosting;
using PalPeek.Core;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PalPeek;

public sealed class SunshineBridge : IHostedService, IAsyncDisposable
{
    private const string PipeName = "PalPeekCapture";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PairingRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Process? _process;
    private bool _stopping;
    private bool _hasStarted;
    private int _restartCount;
    private SunshineLifecycleStatus _lifecycle =
        new(SunshineProcessState.Stopped, null, 0, null);

    public string PackagedExecutablePath { get; } = Path.Combine(
        AppContext.BaseDirectory, "runtime", "sunshine", "sunshine.exe");
    public string RuntimeDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalPeek",
        "sunshine-runtime");
    public string ExecutablePath => Path.Combine(RuntimeDirectory, "sunshine.exe");

    public bool IsInstalled => File.Exists(PackagedExecutablePath);
    public bool IsRunning => IsProcessRunning(_process);
    public SunshineLifecycleStatus Lifecycle => _lifecycle;

    public event EventHandler<SunshineLifecycleStatus>? LifecycleChanged;

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) =>
        StopHostAsync(cancellationToken);

    public async Task StartHostAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
                return;
            if (!IsInstalled)
            {
                SetLifecycle(SunshineProcessState.NotInstalled, null, "未找到内置 Sunshine Host。");
                throw new FileNotFoundException(
                    "未找到内置 Sunshine Host。", PackagedExecutablePath);
            }

            CleanupProcess();
            await StopOrphanedRuntimeHostsAsync(cancellationToken);
            PrepareRuntimeFiles();
            _stopping = false;
            var recovering = _hasStarted;
            if (recovering)
                _restartCount++;
            SetLifecycle(
                recovering ? SunshineProcessState.Recovering : SunshineProcessState.Starting,
                null,
                recovering ? "Sunshine 意外退出，正在恢复。" : "正在启动 Sunshine Host。");

            var startInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("palpeek.conf");
            var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Windows 未能创建 Sunshine Host 进程。");

            process.EnableRaisingEvents = true;
            process.Exited += Process_Exited;
            _process = process;
            _hasStarted = true;

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);
            Exception? lastError = null;
            while (!startup.IsCancellationRequested && IsProcessRunning(process))
            {
                try
                {
                    await QueryStatusCoreAsync(startup.Token);
                    SetLifecycle(SunshineProcessState.Running, process.Id, null);
                    return;
                }
                catch (Exception ex) when (
                    ex is IOException or TimeoutException or OperationCanceledException)
                {
                    lastError = ex;
                    if (startup.IsCancellationRequested)
                        break;
                    await Task.Delay(100, startup.Token);
                }
            }

            var detail = IsProcessRunning(process)
                ? lastError?.Message ?? "IPC 启动超时。"
                : $"Sunshine 已退出，退出码 {process.ExitCode}。";
            SetLifecycle(SunshineProcessState.Faulted, null, detail);
            throw new InvalidOperationException($"Sunshine Host 启动失败：{detail}", lastError);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void PrepareRuntimeFiles()
    {
        var packagedRoot = Path.GetDirectoryName(PackagedExecutablePath)!;
        Directory.CreateDirectory(RuntimeDirectory);
        CopyFileIfChanged(PackagedExecutablePath, ExecutablePath);
        CopyFileIfChanged(
            Path.Combine(packagedRoot, "palpeek.conf"),
            Path.Combine(RuntimeDirectory, "palpeek.conf"));

        var packagedAssets = Path.Combine(packagedRoot, "assets");
        if (!Directory.Exists(packagedAssets))
            throw new DirectoryNotFoundException($"Sunshine 运行资源缺失：{packagedAssets}");

        foreach (var source in Directory.EnumerateFiles(
                     packagedAssets, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(packagedAssets, source);
            CopyFileIfChanged(source, Path.Combine(RuntimeDirectory, "assets", relative));
        }
        var runtimeAppsPath = Path.Combine(RuntimeDirectory, "config", "apps.json");
        CopyFileIfChanged(Path.Combine(packagedAssets, "apps.json"), runtimeAppsPath);
        ValidateWatchAppDefinition(runtimeAppsPath);
    }

    private static void ValidateWatchAppDefinition(string appsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(appsPath));
        if (!document.RootElement.TryGetProperty("apps", out var apps) ||
            apps.ValueKind != JsonValueKind.Array ||
            !apps.EnumerateArray().Any(app =>
                app.TryGetProperty("name", out var name) &&
                string.Equals(
                    name.GetString(),
                    MoonlightCommandLine.AppName,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"PalPeek Host 应用配置无效，缺少“{MoonlightCommandLine.AppName}”：{appsPath}");
        }
    }

    private static void CopyFileIfChanged(string source, string destination)
    {
        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        if (destinationInfo.Exists &&
            destinationInfo.Length == sourceInfo.Length &&
            destinationInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
    }

    public async Task<SunshineRuntimeStatus> EnsureTargetAsync(
        GameInfo game,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await StartHostAsync(cancellationToken);
                var status = await GetStatusAsync(cancellationToken);
                if (status.Target?.Pid != game.ProcessId ||
                    status.Target.Hwnd != game.WindowHandle ||
                    status.Target.SessionId != game.SessionId)
                {
                    await SendAsync(new
                    {
                        protocolVersion = SunshineProtocol.Version,
                        command = "setTarget",
                        pid = game.ProcessId,
                        hwnd = game.WindowHandle,
                        appId = game.AppId.ToString(),
                        game.Name,
                        game.SessionId,
                        capture = "window",
                        audio = "processTree"
                    }, cancellationToken);
                    status = await GetStatusAsync(cancellationToken);
                }
                return status;
            }
            catch (Exception ex) when (
                attempt == 0 &&
                ex is IOException or TimeoutException)
            {
                await ForceStopCoreAsync(cancellationToken);
            }
        }

        throw new InvalidOperationException("Sunshine Host 恢复失败。");
    }

    public async Task SetTargetAsync(GameInfo game, CancellationToken cancellationToken) =>
        _ = await EnsureTargetAsync(game, cancellationToken);

    public async Task PairAsync(
        string pin,
        string clientId,
        string clientAddress,
        CancellationToken cancellationToken)
    {
        var pairingTimer = Stopwatch.StartNew();
        var useUniquePendingRequestFallback = false;
        while (true)
        {
            try
            {
                await SendAsync(new
                {
                    protocolVersion = SunshineProtocol.Version,
                    command = "pair",
                    pin,
                    clientId,
                    // Prefer the exact peer address. If Sunshine represented the
                    // same Tailscale address differently (for example mapped IPv6),
                    // retry without it. The Sunshine fork only accepts that fallback
                    // when exactly one pending Moonlight request exists.
                    clientAddress = useUniquePendingRequestFallback
                        ? string.Empty
                        : clientAddress
                }, cancellationToken);
                return;
            }
            catch (Exception ex) when (SunshineProtocol.IsRetryablePairingError(ex))
            {
                useUniquePendingRequestFallback = true;
                // Moonlight prints the PIN before Sunshine has necessarily registered
                // its asynchronous pairing request. Give that request time to arrive.
                var remaining = PairingTimeout - pairingTimer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new SunshineProtocolException(
                        "pairing_timeout",
                        "Moonlight 配对请求在 60 秒内未到达，请重新点击观看游戏。");
                }
                await Task.Delay(
                    remaining < PairingRetryDelay ? remaining : PairingRetryDelay,
                    cancellationToken);
            }
        }
    }

    public Task StopSessionsAsync(CancellationToken cancellationToken) =>
        SendAsync(new
        {
            protocolVersion = SunshineProtocol.Version,
            command = "stopSessions",
            terminateApplication = false
        }, cancellationToken);

    public Task ClearTargetAsync(CancellationToken cancellationToken) =>
        SendAsync(new
        {
            protocolVersion = SunshineProtocol.Version,
            command = "clearTarget"
        }, cancellationToken);

    public Task EndSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        SendAsync(new
        {
            protocolVersion = SunshineProtocol.Version,
            command = "sessionEnded",
            sessionId
        }, cancellationToken);

    public Task StartWebStreamAsync(
        string sessionId,
        BrowserStreamQuality quality,
        string mediaPipe,
        CancellationToken cancellationToken) =>
        SendAsync(new
        {
            protocolVersion = SunshineProtocol.Version,
            command = "startWebStream",
            sessionId,
            quality = quality.ToString(),
            mediaPipe
        }, cancellationToken);

    public Task StopWebStreamAsync(
        string sessionId,
        CancellationToken cancellationToken) =>
        SendAsync(new
        {
            protocolVersion = SunshineProtocol.Version,
            command = "stopWebStream",
            sessionId
        }, cancellationToken);

    public async Task<SunshineRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("Sunshine Host 未运行。");
        return await QueryStatusCoreAsync(cancellationToken);
    }

    public async Task StopHostAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsRunning)
            {
                CleanupProcess();
                _hasStarted = false;
                SetLifecycle(SunshineProcessState.Stopped, null, null);
                return;
            }

            _stopping = true;
            SetLifecycle(SunshineProcessState.Stopping, _process!.Id, null);
            try
            {
                var response = await SendRawAsync(new
                {
                    protocolVersion = SunshineProtocol.Version,
                    command = "shutdown"
                }, cancellationToken);
                SunshineProtocol.EnsureSuccess(response);
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or SunshineProtocolException)
            {
                // Sunshine may close the pipe before the response is delivered.
            }

            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdown.CancelAfter(ShutdownTimeout);
            try
            {
                await _process!.WaitForExitAsync(shutdown.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _process!.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }

            CleanupProcess();
            _hasStarted = false;
            SetLifecycle(SunshineProcessState.Stopped, null, null);
        }
        finally
        {
            _stopping = false;
            _lifecycleLock.Release();
        }
    }

    private async Task<SunshineRuntimeStatus> QueryStatusCoreAsync(CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(new
        {
            protocolVersion = SunshineProtocol.Version,
            command = "status"
        }, cancellationToken);
        return SunshineProtocol.ParseStatusResponse(response);
    }

    private async Task SendAsync<T>(T command, CancellationToken cancellationToken)
    {
        if (!IsRunning)
            await StartHostAsync(cancellationToken);
        var response = await SendRawAsync(command, cancellationToken);
        SunshineProtocol.EnsureSuccess(response);
    }

    private async Task<string> SendRawAsync<T>(T command, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "\n");
            await pipe.WriteAsync(payload, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            var response = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(response))
                throw new IOException("PalPeek Host 未返回 IPC 响应。");
            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PalPeek Host IPC 操作超时。", ex);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task ForceStopCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            _stopping = true;
            if (IsRunning)
            {
                _process!.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }
            CleanupProcess();
            SetLifecycle(SunshineProcessState.Faulted, null, "Sunshine IPC 失联，准备恢复。");
        }
        finally
        {
            _stopping = false;
            _lifecycleLock.Release();
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process || !ReferenceEquals(process, _process) || _stopping)
            return;
        SetLifecycle(
            SunshineProcessState.Faulted,
            null,
            $"Sunshine 意外退出，退出码 {process.ExitCode}。");
    }

    private void CleanupProcess()
    {
        if (_process is null)
            return;
        _process.Exited -= Process_Exited;
        _process.Dispose();
        _process = null;
    }

    private async Task StopOrphanedRuntimeHostsAsync(CancellationToken cancellationToken)
    {
        foreach (var process in FindOrphanedRuntimeHosts())
        {
            using (process)
            {
                try
                {
                    var response = await SendRawAsync(new
                    {
                        protocolVersion = SunshineProtocol.Version,
                        command = "shutdown"
                    }, cancellationToken);
                    SunshineProtocol.EnsureSuccess(response);
                }
                catch (Exception ex) when (
                    ex is IOException or TimeoutException or SunshineProtocolException)
                {
                    // An orphan may have lost its IPC pipe. It is still safe to stop
                    // because its executable path is PalPeek's private runtime copy.
                }

                if (!IsProcessRunning(process))
                    continue;

                using var shutdown =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shutdown.CancelAfter(ShutdownTimeout);
                try
                {
                    await process.WaitForExitAsync(shutdown.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
        }
    }

    private List<Process> FindOrphanedRuntimeHosts()
    {
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(ExecutablePath)))
        {
            try
            {
                if (string.Equals(
                        Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                        Path.GetFullPath(ExecutablePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Processes that exit during enumeration, or cannot be inspected,
                // must not be treated as PalPeek-owned.
            }

            process.Dispose();
        }

        return result;
    }

    private void SetLifecycle(SunshineProcessState state, int? processId, string? message)
    {
        var next = new SunshineLifecycleStatus(state, processId, _restartCount, message);
        if (_lifecycle == next)
            return;
        _lifecycle = next;
        LifecycleChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopHostAsync(CancellationToken.None); } catch { }
        _commandLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private static bool IsProcessRunning(Process? process)
    {
        try { return process is not null && !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }
}
