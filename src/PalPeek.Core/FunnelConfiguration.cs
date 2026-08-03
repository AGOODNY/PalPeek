using System.Text.Json;

namespace PalPeek.Core;

public sealed record FunnelMapping(
    string HostName,
    int Port,
    string? Path,
    string? ProxyTarget,
    bool Public);

public sealed record FunnelConfigurationSnapshot(IReadOnlyList<FunnelMapping> Mappings)
{
    public FunnelMapping? FindTarget(string target) => Mappings.FirstOrDefault(x =>
        x.Public && string.Equals(
            NormalizeTarget(x.ProxyTarget), NormalizeTarget(target),
            StringComparison.OrdinalIgnoreCase));

    public bool IsPublicPortOccupied(int port) =>
        Mappings.Any(x => x.Public && x.Port == port);

    public static FunnelConfigurationSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var publicAuthorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("AllowFunnel", out var allow) &&
            allow.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in allow.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.True)
                    publicAuthorities.Add(property.Name);
            }
        }

        var result = new List<FunnelMapping>();
        if (!root.TryGetProperty("Web", out var web) ||
            web.ValueKind != JsonValueKind.Object)
            return new FunnelConfigurationSnapshot(result);

        foreach (var site in web.EnumerateObject())
        {
            var (host, port) = ParseAuthority(site.Name);
            if (site.Value.ValueKind != JsonValueKind.Object ||
                !site.Value.TryGetProperty("Handlers", out var handlers) ||
                handlers.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var handler in handlers.EnumerateObject())
            {
                var proxy = handler.Value.ValueKind == JsonValueKind.Object &&
                            handler.Value.TryGetProperty("Proxy", out var value)
                    ? value.GetString()
                    : null;
                result.Add(new FunnelMapping(
                    host,
                    port,
                    handler.Name,
                    proxy,
                    publicAuthorities.Contains(site.Name)));
            }
        }
        return new FunnelConfigurationSnapshot(result);
    }

    private static (string Host, int Port) ParseAuthority(string authority)
    {
        if (Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var uri))
            return (uri.Host, uri.Port);
        var separator = authority.LastIndexOf(':');
        return separator > 0 && int.TryParse(authority[(separator + 1)..], out var port)
            ? (authority[..separator], port)
            : (authority, 443);
    }

    private static string? NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        return target.Trim().TrimEnd('/').Replace("localhost", "127.0.0.1",
            StringComparison.OrdinalIgnoreCase);
    }
}
