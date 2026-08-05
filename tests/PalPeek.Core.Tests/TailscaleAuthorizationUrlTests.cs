using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class TailscaleAuthorizationUrlTests
{
    [Theory]
    [InlineData("To enable Funnel, visit: https://login.tailscale.com/admin/feature/abc", "https://login.tailscale.com/admin/feature/abc")]
    [InlineData("Open (https://login.tailscale.com/a/abc).", "https://login.tailscale.com/a/abc")]
    public void FindsTrustedAuthorizationUrl(string output, string expected)
    {
        Assert.Equal(expected, TailscaleAuthorizationUrl.Find(output)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://login.tailscale.com/a/abc")]
    [InlineData("https://login.tailscale.com.evil.example/a/abc")]
    [InlineData("https://example.com@login.tailscale.com/a/abc")]
    [InlineData("https://login.tailscale.com:8443/a/abc")]
    [InlineData("https://tailscale.com/a/abc")]
    [InlineData("https://example.com/a/abc")]
    [InlineData("")]
    public void RejectsUntrustedUrl(string output)
    {
        Assert.Null(TailscaleAuthorizationUrl.Find(output));
    }
}
