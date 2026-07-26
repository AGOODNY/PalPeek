namespace PalPeek.Core;

public static class MoonlightProfile
{
    public static string? GetHostCertificate(string settings, string host)
    {
        var section = string.Empty;
        var matchingHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var certificates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in settings.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            if (!string.Equals(section, "hosts", StringComparison.OrdinalIgnoreCase))
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            var separator = key.IndexOf('\\');
            if (separator <= 0 || separator == key.Length - 1)
                continue;

            var index = key[..separator];
            var property = key[(separator + 1)..];
            if (IsAddressProperty(property) &&
                string.Equals(value, host, StringComparison.OrdinalIgnoreCase))
            {
                matchingHosts.Add(index);
            }
            else if (string.Equals(property, "srvcert", StringComparison.OrdinalIgnoreCase) &&
                     HasCertificate(value))
            {
                certificates[index] = value;
            }
        }

        foreach (var index in matchingHosts)
        {
            if (certificates.TryGetValue(index, out var certificate))
                return certificate;
        }
        return null;
    }

    private static bool IsAddressProperty(string property) =>
        property.Equals("manualaddress", StringComparison.OrdinalIgnoreCase) ||
        property.Equals("localaddress", StringComparison.OrdinalIgnoreCase) ||
        property.Equals("remoteaddress", StringComparison.OrdinalIgnoreCase) ||
        property.Equals("ipv6address", StringComparison.OrdinalIgnoreCase);

    private static bool HasCertificate(string value) =>
        value.Contains("BEGIN CERTIFICATE", StringComparison.OrdinalIgnoreCase) ||
        (value.StartsWith("@ByteArray(", StringComparison.Ordinal) &&
         !value.Equals("@ByteArray()", StringComparison.Ordinal));
}
