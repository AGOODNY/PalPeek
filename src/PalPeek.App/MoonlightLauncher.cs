using PalPeek.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PalPeek;

public sealed class MoonlightLauncher
{
    private const string SettingsFileName = "Moonlight.ini";
    private const string SettingsOrganizationDirectory =
        "Moonlight Game Streaming Project";
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan PairingProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PairingPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatRequestTimeout = TimeSpan.FromSeconds(3);
    private const int ReservationAttempts = 3;
    private readonly PalPeekOptions _options;
    private readonly HttpClient _http =
        PeerHttpClient.Create(Timeout.InfiniteTimeSpan);
    private readonly HttpClient _pairingHttp =
        PeerHttpClient.Create(Timeout.InfiniteTimeSpan);
    private readonly object _diagnosticsGate = new();
    private WatchDiagnosticsSnapshot _diagnostics =
        new(false, false, false, false, null, null, DateTimeOffset.MinValue);

    public MoonlightLauncher(PalPeekOptions options) => _options = options;

    public string ExecutablePath { get; } = Path.Combine(
        AppContext.BaseDirectory, "runtime", "moonlight", "moonlight.exe");
    public string ProfileDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalPeek",
        "moonlight-profile");
    private string SettingsPath => Path.Combine(
        ProfileDirectory,
        SettingsOrganizationDirectory,
        SettingsFileName);

    public bool IsInstalled => File.Exists(ExecutablePath);
    public WatchDiagnosticsSnapshot Diagnostics
    {
        get
        {
            lock (_diagnosticsGate)
                return _diagnostics;
        }
    }

    public async Task WatchAsync(
        FriendStream friend,
        IProgress<WatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ResetDiagnostics();
        try
        {
            await WatchCoreAsync(friend, progress, cancellationToken);
            UpdateDiagnostics(current => current with
            {
                IsStreaming = false,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateDiagnostics(current => current with
            {
                IsStreaming = false,
                LastError = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            throw;
        }
        catch (Exception ex)
        {
            UpdateDiagnostics(current => current with
            {
                IsStreaming = false,
                LastError = ex.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            throw;
        }
    }

    private async Task WatchCoreAsync(
        FriendStream friend,
        IProgress<WatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsInstalled)
            throw new FileNotFoundException("未找到内置 Moonlight 播放组件。", ExecutablePath);
        if (friend.Status.Game is null)
            throw new InvalidOperationException("好友的观战会话已经结束。");

        ReportProgress(progress, WatchStage.Reserving, "正在申请观看名额…");
        var baseUrl = $"http://{friend.Node.Ip}:{Protocol.ApiPort}";
        var viewerId = Environment.MachineName;
        using var reserve = await ReserveAsync(
            baseUrl,
            friend.Status.Game.SessionId,
            viewerId,
            progress,
            cancellationToken);
        if (!reserve.IsSuccessStatusCode)
            throw await CreateApiException(reserve, cancellationToken);
        var lease = await reserve.Content.ReadFromJsonAsync<ReservationResponse>(
            cancellationToken: cancellationToken)
            ?? throw new IOException("好友没有返回观看名额。");
        UpdateDiagnostics(current => current with
        {
            ReservationSucceeded = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        using var heartbeatCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(
            baseUrl,
            lease.LeaseId,
            heartbeatCancellation.Token);
        try
        {
            ReportProgress(progress, WatchStage.Pairing, "正在连接主播并检查配对…");
            await AwaitWithHeartbeatAsync(
                EnsurePairedAsync(friend.Node.Ip, baseUrl, viewerId, cancellationToken),
                heartbeat);
            UpdateDiagnostics(current => current with
            {
                PairingSucceeded = true,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            ReportProgress(progress, WatchStage.StartingPlayer, "正在启动播放器…");
            using var moonlight = StartStream(friend.Node.Ip);
            UpdateDiagnostics(current => current with
            {
                PlayerStarted = true,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            ReportProgress(progress, WatchStage.Streaming, "播放器已启动。");
            try
            {
                var playerExit = moonlight.WaitForExitAsync(cancellationToken);
                await AwaitWithHeartbeatAsync(playerExit, heartbeat);
            }
            catch
            {
                try
                {
                    if (!moonlight.HasExited)
                        moonlight.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                throw;
            }
            if (moonlight.ExitCode != 0)
                throw new InvalidOperationException($"播放器意外退出（代码 {moonlight.ExitCode}）。");
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try { await heartbeat; } catch { }

            using var releaseTimeout = new CancellationTokenSource(ApiRequestTimeout);
            try
            {
                await _http.DeleteAsync(
                    $"{baseUrl}/api/v1/reservations/{lease.LeaseId}",
                    releaseTimeout.Token);
            }
            catch { }
        }
    }

    private static async Task AwaitWithHeartbeatAsync(
        Task operation,
        Task heartbeat)
    {
        var completed = await Task.WhenAny(operation, heartbeat);
        if (completed == heartbeat)
            await heartbeat;
        await operation;
    }

    private async Task<HttpResponseMessage> ReserveAsync(
        string baseUrl,
        string sessionId,
        string viewerId,
        IProgress<WatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= ReservationAttempts; attempt++)
        {
            if (attempt > 1)
            {
                ReportProgress(
                    progress,
                    WatchStage.Reserving,
                    $"首次连接响应较慢，正在自动重试（{attempt}/{ReservationAttempts}）…");
            }

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ApiRequestTimeout);
            try
            {
                return await _http.PostAsJsonAsync(
                    $"{baseUrl}/api/v1/reservations",
                    new ReservationRequest(
                        Protocol.SchemaVersion,
                        sessionId,
                        viewerId,
                        _options.Nickname),
                    timeout.Token);
            }
            catch (OperationCanceledException ex)
                when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }

            if (attempt < ReservationAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500 * attempt),
                    cancellationToken);
            }
        }

        if (lastError is OperationCanceledException)
        {
            throw new TimeoutException(
                $"连接好友超时，已自动尝试 {ReservationAttempts} 次。请确认双方 Tailscale 在线后再试。",
                lastError);
        }

        throw new IOException(
            $"无法连接好友的 PalPeek 服务，已自动尝试 {ReservationAttempts} 次。" +
            "请检查 Tailscale、防火墙和 Tailnet ACL。",
            lastError);
    }

    private void ResetDiagnostics()
    {
        lock (_diagnosticsGate)
        {
            _diagnostics = new WatchDiagnosticsSnapshot(
                false,
                false,
                false,
                false,
                null,
                null,
                DateTimeOffset.UtcNow);
        }
    }

    private void ReportProgress(
        IProgress<WatchProgress>? progress,
        WatchStage stage,
        string message)
    {
        UpdateDiagnostics(current => current with
        {
            CurrentStage = stage,
            IsStreaming = stage == WatchStage.Streaming,
            LastError = null,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        progress?.Report(new WatchProgress(stage, message));
    }

    private void UpdateDiagnostics(
        Func<WatchDiagnosticsSnapshot, WatchDiagnosticsSnapshot> update)
    {
        lock (_diagnosticsGate)
            _diagnostics = update(_diagnostics);
    }

    private async Task EnsurePairedAsync(
        string host,
        string baseUrl,
        string viewerId,
        CancellationToken cancellationToken)
    {
        PrepareProfileDirectory();
        var existingCertificate = ReadHostCertificate(host);
        if (existingCertificate is not null &&
            await IsHostPairedAsync(host, cancellationToken))
        {
            return;
        }

        var pin = MoonlightCommandLine.GeneratePairingPin();
        using var pairing = StartProcess(
            MoonlightCommandLine.Pair(host, pin),
            redirectOutput: true,
            hideWindow: true);
        var output = new StringBuilder();
        void Observe(string? line)
        {
            if (line is null)
                return;
            lock (output)
                output.AppendLine(line);
        }

        pairing.OutputDataReceived += (_, e) => Observe(e.Data);
        pairing.ErrorDataReceived += (_, e) => Observe(e.Data);
        pairing.BeginOutputReadLine();
        pairing.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PairingTimeout);
        using var hideCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var hideWindow = HideWindowAsync(pairing, hideCancellation.Token);
        try
        {
            await SubmitPairingPinAsync(
                baseUrl,
                viewerId,
                pin,
                timeout.Token);
            await WaitForPairingCompletionAsync(
                host,
                pairing,
                output,
                timeout.Token);
            await StopProcessAsync(pairing, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await StopProcessAsync(pairing, CancellationToken.None);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"自动配对等待超过 {PairingTimeout.TotalSeconds:0} 秒。主播端没有确认到对应的 Moonlight 请求，请重试。",
                    ex);
            }
            throw;
        }
        finally
        {
            hideCancellation.Cancel();
            try { await hideWindow; } catch (OperationCanceledException) { }
        }
    }

    private async Task WaitForPairingCompletionAsync(
        string host,
        Process pairing,
        StringBuilder output,
        CancellationToken cancellationToken)
    {
        // Do not stop Moonlight as soon as its local certificate appears. The host
        // may still be committing the client certificate at that point, and starting
        // a stream in that small window makes Sunshine reset the RTSP connection.
        await pairing.WaitForExitAsync(cancellationToken);
        pairing.WaitForExit(); // Flush asynchronous output handlers.
        var detail = output.ToString().Trim();
        if (pairing.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrEmpty(detail)
                    ? $"自动配对失败：Moonlight 退出代码 {pairing.ExitCode}。"
                    : $"自动配对失败：{detail}");
        }

        // A successful pair command is followed by a live authenticated query.
        // This verifies that Sunshine has accepted and persisted the client before
        // the RTSP session is allowed to start.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await IsHostPairedAsync(host, cancellationToken))
                return;
            await Task.Delay(PairingPollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            "自动配对尚未在主播端生效，请稍候后重新点击观看。");
    }

    private async Task<bool> IsHostPairedAsync(
        string host,
        CancellationToken cancellationToken)
    {
        using var probe = StartProcess(
            MoonlightCommandLine.List(host),
            redirectOutput: true,
            hideWindow: true);
        var standardOutput = probe.StandardOutput.ReadToEndAsync();
        var standardError = probe.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PairingProbeTimeout);
        try
        {
            await probe.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(standardOutput, standardError);
            return probe.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync(probe, CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            return false;
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(probe, CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }
    }

    private string? ReadHostCertificate(string host)
    {
        try
        {
            return File.Exists(SettingsPath)
                ? MoonlightProfile.GetHostCertificate(File.ReadAllText(SettingsPath), host)
                : null;
        }
        catch (IOException)
        {
            // QSettings replaces the INI file while flushing. Retry on the next poll.
            return null;
        }
    }

    private static async Task HideWindowAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                ShowWindow(process.MainWindowHandle, ShowWindowCommand.Hide);
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private static async Task StopProcessAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (InvalidOperationException) { }
    }

    private async Task SubmitPairingPinAsync(
        string baseUrl,
        string viewerId,
        string pin,
        CancellationToken cancellationToken)
    {
        using var response = await _pairingHttp.PostAsJsonAsync(
            $"{baseUrl}/api/v1/pair",
            new PairRequest(Protocol.SchemaVersion, viewerId, pin),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiException(response, cancellationToken);
    }

    private Process StartStream(string host)
        => StartProcess(
            MoonlightCommandLine.Stream(host, _options.Quality),
            redirectOutput: false);

    private Process StartProcess(
        IEnumerable<string> arguments,
        bool redirectOutput,
        bool hideWindow = false)
    {
        PrepareProfileDirectory();
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = ProfileDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = redirectOutput || hideWindow,
            WindowStyle = hideWindow
                ? ProcessWindowStyle.Hidden
                : ProcessWindowStyle.Normal
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo);
        return process ?? throw new InvalidOperationException("无法启动 Moonlight。");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, ShowWindowCommand command);

    private enum ShowWindowCommand
    {
        Hide = 0
    }

    private void PrepareProfileDirectory()
    {
        Directory.CreateDirectory(ProfileDirectory);

        var packagedDirectory = Path.GetDirectoryName(ExecutablePath)!;
        var packagedPortableMarker = Path.Combine(packagedDirectory, "portable.dat");
        var profilePortableMarker = Path.Combine(ProfileDirectory, "portable.dat");
        if (File.Exists(packagedPortableMarker) && !File.Exists(profilePortableMarker))
            File.Copy(packagedPortableMarker, profilePortableMarker);

        // Older PalPeek builds asked portable Moonlight to store its state beside
        // moonlight.exe. Preserve any pairing created by an elevated installer run,
        // then keep all future state in the per-user writable profile.
        var legacySettingsCandidates = new[]
        {
            Path.Combine(
                packagedDirectory,
                SettingsOrganizationDirectory,
                SettingsFileName),
            Path.Combine(packagedDirectory, SettingsFileName),
            Path.Combine(ProfileDirectory, SettingsFileName)
        };
        if (!File.Exists(SettingsPath))
        {
            var legacySettings = legacySettingsCandidates.FirstOrDefault(File.Exists);
            if (legacySettings is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.Copy(legacySettings, SettingsPath);
            }
        }
    }

    private async Task HeartbeatAsync(string baseUrl, string leaseId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, cancellationToken);
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HeartbeatRequestTimeout);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    $"{baseUrl}/api/v1/reservations/{leaseId}/heartbeat");
                response = await _http.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A transient control-plane timeout must not tear down a healthy
                // Moonlight media stream. The next heartbeat will retry.
                continue;
            }
            catch (HttpRequestException)
            {
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                    continue;
                if (PeerConnectionPolicy.IsTransientHeartbeatFailure(response.StatusCode))
                    continue;

                throw await CreateApiException(response, cancellationToken);
            }
        }
    }

    private static async Task<Exception> CreateApiException(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(
                cancellationToken: cancellationToken);
            return new InvalidOperationException(error?.Message ?? $"请求失败：{(int)response.StatusCode}");
        }
        catch (JsonException)
        {
            return new InvalidOperationException($"请求失败：{(int)response.StatusCode}");
        }
    }
}

public enum WatchStage
{
    Reserving,
    Pairing,
    StartingPlayer,
    Streaming
}

public sealed record WatchProgress(WatchStage Stage, string Message);
public sealed record WatchDiagnosticsSnapshot(
    bool ReservationSucceeded,
    bool PairingSucceeded,
    bool PlayerStarted,
    bool IsStreaming,
    WatchStage? CurrentStage,
    string? LastError,
    DateTimeOffset UpdatedAt);
