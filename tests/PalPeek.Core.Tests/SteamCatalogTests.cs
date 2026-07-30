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

    [Theory]
    [InlineData("艾尔登法环 / Elden Ring")]
    [InlineData("女神异闻录5皇家版")]
    [InlineData("DARK SOULS™ III")]
    [InlineData("Don't Starve")]
    [InlineData("No Man's Sky 无人深空")]
    [InlineData("《Spiritfarer®》Farewell版")]
    [InlineData("STEINS;GATE")]
    [InlineData("100% Orange Juice")]
    public void PreservesRepresentativeInternationalGameNames(string name)
    {
        var steamApps = Path.Combine(_root, "steamapps");
        var install = Path.Combine(steamApps, "common", name);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_42.acf"), $$"""
        "AppState"
        {
            "appid" "42"
            "name" "{{name}}"
            "installdir" "{{name}}"
        }
        """);

        var catalog = SteamCatalog.FromLibraries([_root]);

        Assert.Equal(name, Assert.Single(catalog.Apps).Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
