using PalPeek.Core;
using System.Diagnostics;

namespace PalPeek;

public sealed record FunnelState(
    bool Configured,
    string? HostName,
    int? Port,
    string Message,
    IReadOnlyList<int> OccupiedPorts);

public sealed class FunnelManager
{
    private static readonly int[] PublicPorts = [443, 8443, 10000];
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private readonly PalPeekOptions _options;
    private readonly ConfigStore _config;
    private readonly ITailscaleService _tailscale;
    private readonly string _executable;

    public FunnelManager(
        PalPeekOptions options,
        ConfigStore config,
        ITailscaleService tailscale)
    {
        _options = options;
        _config = config;
        _tailscale = tailscale;
        _executable = FindExecutable();
    }

    public async Task<FunnelState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_executable))
            return new(false, null, null, "未安装 Tailscale。", []);

        var tailnet = await _tailscale.GetSnapshotAsync(cancellationToken);
        if (!tailnet.Running)
            return new(
                false,
                null,
                null,
                tailnet.Error ?? "Tailscale 未连接，请先打开 Tailscale 并登录。",
                []);
        if (string.IsNullOrWhiteSpace(tailnet.SelfDnsName))
            return new(
                false,
                null,
                null,
                "当前 Tailnet 没有可用的 MagicDNS 域名。请先在 Tailscale 管理后台启用 MagicDNS，然后重试。",
                []);

        var status = await RunAsync(["funnel", "status", "--json"], cancellationToken);
        if (status.ExitCode != 0)
            return new(false, null, null, FriendlyError(status.Error), []);

        FunnelConfigurationSnapshot snapshot;
        try
        {
            snapshot = FunnelConfigurationSnapshot.Parse(status.Output);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return new(false, null, null, $"无法读取 Funnel 状态：{ex.Message}", []);
        }

        var target = LocalTarget();
        var existing = snapshot.FindTarget(target);
        var occupied = PublicPorts.Where(snapshot.IsPublicPortOccupied).ToArray();
        if (existing is not null)
        {
            SaveEndpoint(existing.HostName, existing.Port);
            return new(true, existing.HostName, existing.Port, "公网入口已就绪。", occupied);
        }

        return new(
            false,
            tailnet.SelfDnsName,
            null,
            "尚未配置 PalPeek 公网入口。",
            occupied);
    }

    public async Task<FunnelState> EnableAsync(
        int? preferredPort = null,
        CancellationToken cancellationToken = default)
    {
        var current = await GetStateAsync(cancellationToken);
        if (current.Configured)
            return current;
        if (string.IsNullOrWhiteSpace(current.HostName))
            throw new InvalidOperationException(current.Message);
        if (preferredPort is not null &&
            (!PublicPorts.Contains(preferredPort.Value) ||
             current.OccupiedPorts.Contains(preferredPort.Value)))
            throw new InvalidOperationException("选择的 Funnel 端口不可用，请重新检查公网入口状态。");
        var port = preferredPort ??
                   PublicPorts.FirstOrDefault(x => !current.OccupiedPorts.Contains(x));
        if (port == 0)
            throw new InvalidOperationException("Funnel 的 443、8443 和 10000 端口均已被其他服务占用。");

        var result = await RunAsync(
            ["funnel", "--bg", "--yes", $"--https={port}", LocalTarget()],
            cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(FriendlyError(result.Error));

        var updated = await GetStateAsync(cancellationToken);
        if (!updated.Configured)
            throw new InvalidOperationException("Tailscale 已接受配置，但公网入口尚未生效。请完成浏览器中的 Tailnet 授权后重试。");
        return updated;
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetStateAsync(cancellationToken);
        if (!current.Configured || current.Port is null)
            return;
        var result = await RunAsync(
            ["funnel", $"--https={current.Port.Value}", "off"],
            cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(FriendlyError(result.Error));
        _options.BrowserSharing.FunnelHostName = null;
        _options.BrowserSharing.FunnelPort = null;
        _config.Save(_options);
    }

    private string LocalTarget() =>
        $"http://127.0.0.1:{_options.BrowserSharing.LocalPort}";

    private void SaveEndpoint(string hostName, int port)
    {
        if (_options.BrowserSharing.FunnelHostName == hostName &&
            _options.BrowserSharing.FunnelPort == port)
            return;
        _options.BrowserSharing.FunnelHostName = hostName;
        _options.BrowserSharing.FunnelPort = port;
        _config.Save(_options);
    }

    private static string FriendlyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "Tailscale Funnel 配置失败，请确认 Tailscale 已连接后重试。";
        if (error.Contains("NoState", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Tailscale is stopped", StringComparison.OrdinalIgnoreCase))
        {
            return "Tailscale 当前已停止。请先打开 Tailscale，确认已经登录并显示“已连接”，然后重试。";
        }
        if (error.Contains("NeedsLogin", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return "Tailscale 尚未登录。请先登录 Tailscale，然后重试。";
        }
        return error.Trim();
    }

    private async Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_executable))
            return new(-1, string.Empty, "未安装 Tailscale。");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new(-1, await output, "Tailscale 命令在 2 分钟内未完成，已终止相关进程。");
        }
        return new(process.ExitCode, await output, (await error).Trim());
    }

    private static string FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Tailscale", "tailscale.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
