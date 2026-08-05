using System.Text.RegularExpressions;

namespace PalPeek.Core;

public static partial class TailscaleAuthorizationUrl
{
    [GeneratedRegex(@"https://[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpsUrlRegex();

    public static Uri? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        foreach (Match match in HttpsUrlRegex().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "login.tailscale.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo))
                continue;

            return uri;
        }

        return null;
    }
}
