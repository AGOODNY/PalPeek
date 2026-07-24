using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace PalPeek.Core;

public sealed record TailscaleSnapshot(
    bool Running,
    string? SelfId,
    string? SelfIp,
    IReadOnlyList<NodeIdentity> Peers,
    string? Error)
{
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
    Task<TailscaleSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class TailscaleService : ITailscaleService
{
    private readonly string _executable;

    public TailscaleService(string? executable = null) =>
        _executable = executable ?? FindExecutable();

    public async Task<TailscaleSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_executable) || !File.Exists(_executable))
            return new(false, null, null, Array.Empty<NodeIdentity>(), "未安装 Tailscale。");

        try
        {
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
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                return new(false, null, null, Array.Empty<NodeIdentity>(), await stderr);
            return Parse(await stdout);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            return new(false, null, null, Array.Empty<NodeIdentity>(), ex.Message);
        }
    }

    internal static TailscaleSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var self = root.TryGetProperty("Self", out var selfNode) ? selfNode : default;
        var selfId = GetString(self, "ID");
        var selfIp = GetFirstIp(self);
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

        return new(selfIp is not null, selfId, selfIp, peers, selfIp is null ? "Tailscale 未连接。" : null);
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
}
