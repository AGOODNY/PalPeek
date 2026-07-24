using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class TailscaleServiceTests
{
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
            }
          }
        }
        """;

        var result = TailscaleService.Parse(json);

        Assert.True(result.Running);
        Assert.Equal("100.64.0.1", result.SelfIp);
        var peer = Assert.Single(result.Peers);
        Assert.Equal("bob.example.ts.net", peer.DnsName);
        Assert.True(peer.Online);
    }
}
