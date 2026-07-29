using PalPeek.Core;
using System.Net;

namespace PalPeek.Core.Tests;

public sealed class TailscaleServiceTests
{
    [Fact]
    public async Task MissingExecutableResultIsCachedAndOnlyNotifiedOnce()
    {
        var missingExecutable = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "tailscale.exe");
        using var service = new TailscaleService(missingExecutable);
        var notifications = 0;
        service.SnapshotChanged += (_, _) => notifications++;

        var first = await service.GetSnapshotAsync();
        var second = await service.GetSnapshotAsync();

        Assert.False(first.Running);
        Assert.Equal("未安装 Tailscale。", first.Error);
        Assert.Same(first, second);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ParsesSelfAndOnlinePeers()
    {
        const string json = """
        {
          "Self": {
            "ID": "self",
            "HostName": "alice",
            "TailscaleIPs": ["100.64.0.1", "fd7a::1"],
            "Online": true
          },
          "Peer": {
            "node-key": {
              "ID": "peer",
              "HostName": "bob",
              "DNSName": "bob.example.ts.net.",
              "TailscaleIPs": ["100.64.0.2"],
              "Online": true
            },
            "offline-node-key": {
              "ID": "offline-peer",
              "HostName": "charlie",
              "DNSName": "charlie.example.ts.net.",
              "TailscaleIPs": ["100.64.0.3"],
              "Online": false
            }
          }
        }
        """;

        var result = TailscaleService.Parse(json);

        Assert.True(result.Running);
        Assert.Equal("100.64.0.1", result.SelfIp);
        Assert.Equal(2, result.Peers.Count);
        var peer = Assert.Single(result.OnlinePeers);
        Assert.Equal("bob.example.ts.net", peer.DnsName);
        Assert.True(peer.Online);
        Assert.True(result.ContainsAddress(IPAddress.Parse("100.64.0.1")));
        Assert.True(result.ContainsAddress(IPAddress.Parse("::ffff:100.64.0.2")));
        Assert.False(result.ContainsAddress(IPAddress.Parse("100.64.0.99")));
    }
}
