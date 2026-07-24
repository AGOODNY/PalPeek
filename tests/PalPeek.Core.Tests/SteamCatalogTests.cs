using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class SteamCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "palpeek-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParsesManifestAndMatchesExecutable()
    {
        var steamApps = Path.Combine(_root, "steamapps");
        var install = Path.Combine(steamApps, "common", "Example Game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_42.acf"), """
        "AppState"
        {
            "appid" "42"
            "name" "Example Game"
            "installdir" "Example Game"
        }
        """);

        var catalog = SteamCatalog.FromLibraries(new[] { _root });
        var app = catalog.MatchExecutable(Path.Combine(install, "bin", "game.exe"));

        Assert.NotNull(app);
        Assert.Equal((uint)42, app.AppId);
        Assert.Equal("Example Game", app.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
