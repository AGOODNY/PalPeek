using PalPeek.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PalPeek;

public sealed class MoonlightLauncher
{
    private readonly PalPeekOptions _options;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public MoonlightLauncher(PalPeekOptions options) => _options = options;

    public string ExecutablePath { get; } = Path.Combine(
        AppContext.BaseDirectory, "runtime", "moonlight", "moonlight.exe");

    public bool IsInstalled => File.Exists(ExecutablePath);

    public async Task WatchAsync(FriendStream friend, CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
            throw new FileNotFoundException("未找到内置 Moonlight 播放组件。", ExecutablePath);
        if (friend.Status.Game is null)
            throw new InvalidOperationException("好友的观战会话已经结束。");

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
            await EnsurePairedAsync(friend.Node.Ip, baseUrl, viewerId, cancellationToken);
            using var moonlight = StartStream(friend.Node.Ip);
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
        using var pairing = StartProcess($"pair \"{host}\"", redirectOutput: true);
        var pinReady = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        void Observe(string? line)
        {
            if (line is null)
                return;
            lock (output)
                output.AppendLine(line);
            var match = Regex.Match(line, @"(?<!\d)\d{4}(?!\d)");
            if (match.Success)
                pinReady.TrySetResult(match.Value);
        }

        pairing.OutputDataReceived += (_, e) => Observe(e.Data);
        pairing.ErrorDataReceived += (_, e) => Observe(e.Data);
        pairing.BeginOutputReadLine();
        pairing.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var processExit = pairing.WaitForExitAsync(timeout.Token);
        var completed = await Task.WhenAny(pinReady.Task, processExit);
        if (completed == pinReady.Task)
        {
            using var response = await _http.PostAsJsonAsync(
                $"{baseUrl}/api/v1/pair",
                new PairRequest(Protocol.SchemaVersion, viewerId, await pinReady.Task),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw await CreateApiException(response, cancellationToken);
        }

        await processExit;
        pairing.WaitForExit(); // Flush asynchronous output handlers.
        var text = output.ToString();
        var alreadyPaired = text.Contains("already paired", StringComparison.OrdinalIgnoreCase);
        if (pairing.ExitCode != 0 && !alreadyPaired)
            throw new InvalidOperationException($"自动配对失败：{text.Trim()}");
    }

    private Process StartStream(string host)
    {
        var quality = _options.Quality == StreamQuality.P1080_60
            ? "--resolution 1920x1080 --fps 60 --bitrate 8000"
            : "--resolution 1280x720 --fps 60 --bitrate 4000";
        return StartProcess(
            $"stream \"{host}\" \"PalPeek Watch\" {quality} --video-codec H264 --hdr off --display-mode windowed",
            redirectOutput: false);
    }

    private Process StartProcess(string arguments, bool redirectOutput)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = redirectOutput
        });
        return process ?? throw new InvalidOperationException("无法启动 Moonlight。");
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
