using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class FunnelConfigurationTests
{
    private const string StatusJson = """
        {
          "AllowFunnel": {
            "streamer.tailnet.ts.net:443": true,
            "streamer.tailnet.ts.net:8443": true
          },
          "Web": {
            "streamer.tailnet.ts.net:443": {
              "Handlers": { "/": { "Proxy": "http://127.0.0.1:48192" } }
            },
            "streamer.tailnet.ts.net:8443": {
              "Handlers": { "/other": { "Proxy": "http://127.0.0.1:9000" } }
            },
            "streamer.tailnet.ts.net:10000": {
              "Handlers": { "/private": { "Proxy": "http://127.0.0.1:7000" } }
            }
          }
        }
        """;

    [Fact]
    public void ParseFindsExactPalPeekMapping()
    {
        var snapshot = FunnelConfigurationSnapshot.Parse(StatusJson);

        var mapping = snapshot.FindTarget("http://localhost:48192/");
        Assert.NotNull(mapping);
        Assert.Equal(443, mapping.Port);
        Assert.True(mapping.Public);
    }

    [Fact]
    public void OccupiedPublicPortDoesNotMatchPrivateServeEntry()
    {
        var snapshot = FunnelConfigurationSnapshot.Parse(StatusJson);

        Assert.True(snapshot.IsPublicPortOccupied(8443));
        Assert.False(snapshot.IsPublicPortOccupied(10000));
    }
}
