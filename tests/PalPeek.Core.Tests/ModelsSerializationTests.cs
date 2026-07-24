using PalPeek.Core;
using System.Text.Json;

namespace PalPeek.Core.Tests;

public sealed class ModelsSerializationTests
{
    [Fact]
    public void DeserializesHostStatusWithStringEnumsUsingWebDefaults()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "nickname": "32505",
          "version": "0.1.0",
          "online": true,
          "game": {
            "appId": 250900,
            "name": "The Binding of Isaac: Rebirth",
            "installDirectory": "E:\\SteamLibrary\\steamapps\\common\\The Binding of Isaac Rebirth",
            "processId": 18708,
            "windowHandle": 4655252,
            "sessionId": "session"
          },
          "captureState": "Ready",
          "quality": "P720_60",
          "viewerCount": 0,
          "canWatch": true,
          "message": null
        }
        """;

        var status = JsonSerializer.Deserialize<HostStatus>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(status);
        Assert.Equal(CaptureState.Ready, status.CaptureState);
        Assert.Equal(StreamQuality.P720_60, status.Quality);
        Assert.Equal("The Binding of Isaac: Rebirth", status.Game?.Name);
        Assert.True(status.CanWatch);
    }
}
