using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace PalPeek.Core;

public sealed record TailscaleSnapshot(
    bool Running,
    string? SelfId,
    string? SelfIp,
    string? SelfDnsName,
    IReadOnlyList<NodeIdentity> Peers,
    string? Error)
{
    public IReadOnlyList<NodeIdentity> OnlinePeers =>
        Peers.Where(peer => peer.Online).ToArray();

    public bool ContainsAddress(IPAddress? address)
    {
        if (address is null)
            return false;
        address = Normalize(address);
        if (IPAddress.IsLoopback(address))
            return true;
        if (IPAddress.TryParse(SelfIp, out var self) && Normalize(self).Equals(address))
            return true;
        // Tailnet membership is the access boundary. An idle peer can briefly be
        // reported as offline while its first request wakes the direct route.
        return Peers.Any(x =>
            IPAddress.TryParse(x.Ip, out var peer) &&
            Normalize(peer).Equals(address));
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

public interface ITailscaleService
{
    event EventHandler<TailscaleSnapshot>? SnapshotChanged;
    Task<TailscaleSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class TailscaleService : ITailscaleService, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(8);
    private readonly string _executable;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _cacheGate = new();
    private TailscaleSnapshot? _cachedSnapshot;
    private DateTimeOffset _cacheExpiresAt;

    public TailscaleService(string? executable = null) =>
        _executable = executable ?? FindExecutable();

    public event EventHandler<TailscaleSnapshot>? SnapshotChanged;

    public async Task<TailscaleSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var cached = GetFreshCachedSnapshot();
        if (cached is not null)
            return cached;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = GetFreshCachedSnapshot();
            if (cached is not null)
                return cached;

            var snapshot = await QuerySnapshotAsync(cancellationToken);
            return StoreSnapshot(snapshot);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<TailscaleSnapshot> QuerySnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_executable) || !File.Exists(_executable))
            return Unavailable("未安装 Tailscale。");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executable,
                Arguments = "status --json",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StatusTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryStop(process);
                try { await Task.WhenAll(stdout, stderr); } catch { }
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return Unavailable("Tailscale 状态检查超时，请确认 Tailscale 服务正在运行。");
            }

            await stderr;
            if (process.ExitCode != 0)
                return Unavailable(
                    "无法连接 Tailscale 服务，请确认 Tailscale 正在运行。");
            return Parse(await stdout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryStop(process);
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or JsonException or
                System.ComponentModel.Win32Exception)
        {
            TryStop(process);
            return Unavailable($"检查 Tailscale 状态失败：{ex.Message}");
        }
    }

    private TailscaleSnapshot? GetFreshCachedSnapshot()
    {
        lock (_cacheGate)
        {
            return _cachedSnapshot is not null &&
                   DateTimeOffset.UtcNow < _cacheExpiresAt
                ? _cachedSnapshot
                : null;
        }
    }

    private TailscaleSnapshot StoreSnapshot(TailscaleSnapshot snapshot)
    {
        bool changed;
        lock (_cacheGate)
        {
            changed = _cachedSnapshot != snapshot;
            _cachedSnapshot = snapshot;
            _cacheExpiresAt = DateTimeOffset.UtcNow + CacheLifetime;
        }

        if (changed)
            SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private static TailscaleSnapshot Unavailable(string message) =>
        new(false, null, null, null, Array.Empty<NodeIdentity>(), message);

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    internal static TailscaleSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var self = root.TryGetProperty("Self", out var selfNode) ? selfNode : default;
        var selfId = GetString(self, "ID");
        var selfIp = GetFirstIp(self);
        var selfDnsName = (GetString(self, "DNSName") ?? string.Empty).TrimEnd('.');
        var peers = new List<NodeIdentity>();

        if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in peerMap.EnumerateObject())
            {
                var node = property.Value;
                var ip = GetFirstIp(node);
                if (ip is null)
                    continue;
                peers.Add(new NodeIdentity(
                    GetString(node, "ID") ?? property.Name,
                    GetString(node, "HostName") ?? property.Name,
                    (GetString(node, "DNSName") ?? string.Empty).TrimEnd('.'),
                    ip,
                    GetBoolean(node, "Online")));
            }
        }

        return new(
            selfIp is not null,
            selfId,
            selfIp,
            string.IsNullOrWhiteSpace(selfDnsName) ? null : selfDnsName,
            peers,
            selfIp is null ? "Tailscale 未连接。" : null);
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

    private static string? GetFirstIp(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("TailscaleIPs", out var addresses) ||
            addresses.ValueKind != JsonValueKind.Array)
            return null;
        return addresses.EnumerateArray()
            .Select(x => x.GetString())
            .FirstOrDefault(x => IPAddress.TryParse(x, out var parsed) &&
                                 parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }

    private static string? GetString(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    public void Dispose() => _refreshLock.Dispose();
}
