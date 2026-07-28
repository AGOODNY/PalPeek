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
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PairingProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PairingPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly PalPeekOptions _options;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient _pairingHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

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

    public async Task WatchAsync(
        FriendStream friend,
        IProgress<WatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            throw new FileNotFoundException("未找到内置 Moonlight 播放组件。", ExecutablePath);
        if (friend.Status.Game is null)
            throw new InvalidOperationException("好友的观战会话已经结束。");

        progress?.Report(new WatchProgress(WatchStage.Reserving, "正在申请观看名额…"));
        var baseUrl = $"http://{friend.Node.Ip}:{Protocol.ApiPort}";
        var viewerId = Environment.MachineName;
        using var reserve = await _http.PostAsJsonAsync(
            $"{baseUrl}/api/v1/reservations",
            new ReservationRequest(Protocol.SchemaVersion, friend.Status.Game.SessionId,
                viewerId, _options.Nickname),
            cancellationToken);
        if (!reserve.IsSuccessStatusCode)
            throw await CreateApiException(reserve, cancellationToken);
        var lease = await reserve.Content.ReadFromJsonAsync<ReservationResponse>(
            cancellationToken: cancellationToken)
            ?? throw new IOException("好友没有返回观看名额。");

        try
        {
            progress?.Report(new WatchProgress(WatchStage.Pairing, "正在连接主播并检查配对…"));
            await EnsurePairedAsync(friend.Node.Ip, baseUrl, viewerId, cancellationToken);
            progress?.Report(new WatchProgress(WatchStage.StartingPlayer, "正在启动播放器…"));
            using var moonlight = StartStream(friend.Node.Ip);
            progress?.Report(new WatchProgress(WatchStage.Streaming, "播放器已启动。"));
            using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = HeartbeatAsync(baseUrl, lease.LeaseId, heartbeatCancellation.Token);
            try
            {
                var playerExit = moonlight.WaitForExitAsync(cancellationToken);
                var completed = await Task.WhenAny(playerExit, heartbeat);
                if (completed == heartbeat)
                    await heartbeat;
                await playerExit;
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
            finally
            {
                heartbeatCancellation.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { }
            }
            if (moonlight.ExitCode != 0)
                throw new InvalidOperationException($"播放器意外退出（代码 {moonlight.ExitCode}）。");
        }
        finally
        {
            try { await _http.DeleteAsync($"{baseUrl}/api/v1/reservations/{lease.LeaseId}", CancellationToken.None); }
            catch { }
        }
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
                existingCertificate,
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
        string? existingCertificate,
        Process pairing,
        StringBuilder output,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var certificate = ReadHostCertificate(host);
            if (certificate is not null &&
                (existingCertificate is null ||
                 !string.Equals(
                     certificate,
                     existingCertificate,
                     StringComparison.Ordinal)))
            {
                // Moonlight only persists the server certificate after the complete
                // cryptographic pairing handshake succeeds.
                return;
            }

            // A host reset may keep the same Sunshine certificate while losing its
            // paired-client database. In that case, validate the new pairing against
            // the live host instead of waiting for the certificate value to change.
            if (existingCertificate is not null &&
                await IsHostPairedAsync(host, cancellationToken))
            {
                return;
            }

            if (pairing.HasExited)
            {
                pairing.WaitForExit(); // Flush asynchronous output handlers.
                var detail = output.ToString().Trim();
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(detail)
                        ? "自动配对失败：Moonlight 在保存配对凭据前退出。"
                        : $"自动配对失败：{detail}");
            }

            await Task.Delay(PairingPollInterval, cancellationToken);
        }
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
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Put, $"{baseUrl}/api/v1/reservations/{leaseId}/heartbeat");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await CreateApiException(response, cancellationToken);
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
