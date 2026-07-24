using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class SteamGameDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "palpeek-detector-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StabilizesRebindsWindowAndStopsAfterGracePeriod()
    {
        var install = CreateGame();
        var processes = new FakeProcesses
        {
            Items = new[] { new ProcessSnapshot(100, Path.Combine(install, "game.exe")) }
        };
        var windows = new FakeWindows
        {
            Items = new[] { new WindowCandidate(100, (nint)123, "Game", 1280 * 720) },
            Foreground = 100
        };
        var clock = new FakeTime();
        var detector = new SteamGameDetector(
            SteamCatalog.FromLibraries(new[] { _root }), processes, windows, clock);

        Assert.Equal(CaptureState.Stabilizing, detector.Tick().State);
        clock.Advance(TimeSpan.FromSeconds(5));
        var ready = detector.Tick();
        Assert.Equal(CaptureState.Ready, ready.State);
        Assert.Equal(123, ready.Game!.WindowHandle);

        windows.Items = new[] { new WindowCandidate(100, (nint)456, "Game", 1920 * 1080) };
        var rebound = detector.Tick();
        Assert.Equal(456, rebound.Game!.WindowHandle);
        Assert.Equal(ready.Game.SessionId, rebound.Game.SessionId);

        windows.Items = Array.Empty<WindowCandidate>();
        Assert.Equal(CaptureState.WindowUnavailable, detector.Tick().State);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(CaptureState.Idle, detector.Tick().State);
    }

    [Fact]
    public void RefreshesCatalogAndSelectsTheForegroundGame()
    {
        var initialCatalog = SteamCatalog.FromLibraries(new[] { _root });
        var isaac = CreateGame("The Binding of Isaac: Rebirth", 250900, "The Binding of Isaac Rebirth");
        var hollowKnight = CreateGame("Hollow Knight", 367520);
        var refreshedCatalog = SteamCatalog.FromLibraries(new[] { _root });
        var processes = new FakeProcesses
        {
            Items = new[]
            {
                new ProcessSnapshot(100, Path.Combine(isaac, "isaac-ng.exe")),
                new ProcessSnapshot(200, Path.Combine(hollowKnight, "hollow_knight.exe"))
            }
        };
        var windows = new FakeWindows
        {
            Items = new[]
            {
                new WindowCandidate(100, (nint)123, "Isaac", 1280 * 720),
                new WindowCandidate(200, (nint)456, "Hollow Knight", 1280 * 720)
            },
            Foreground = 200
        };
        var clock = new FakeTime();
        var detector = new SteamGameDetector(
            initialCatalog, processes, windows, clock, () => refreshedCatalog);

        Assert.Equal(CaptureState.Idle, detector.Tick().State);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(CaptureState.Stabilizing, detector.Tick().State);
        clock.Advance(TimeSpan.FromSeconds(5));
        var ready = detector.Tick();

        Assert.Equal(CaptureState.Ready, ready.State);
        Assert.Equal((uint)367520, ready.Game!.AppId);
        Assert.Equal("Hollow Knight", ready.Game.Name);
        Assert.Equal(200, ready.Game.ProcessId);
    }

    private string CreateGame(
        string name = "Detector Game",
        uint appId = 99,
        string? installDirectory = null)
    {
        installDirectory ??= name;
        var steamApps = Path.Combine(_root, "steamapps");
        var install = Path.Combine(steamApps, "common", installDirectory);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(steamApps, $"appmanifest_{appId}.acf"), $$"""
        "AppState"
        {
            "appid" "{{appId}}"
            "name" "{{name}}"
            "installdir" "{{installDirectory}}"
        }
        """);
        return install;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeProcesses : IProcessSource
    {
        public IReadOnlyList<ProcessSnapshot> Items { get; set; } = Array.Empty<ProcessSnapshot>();
        public IReadOnlyList<ProcessSnapshot> Snapshot() => Items;
    }

    private sealed class FakeWindows : IWindowLocator
    {
        public IReadOnlyList<WindowCandidate> Items { get; set; } = Array.Empty<WindowCandidate>();
        public int? Foreground { get; set; }
        public IReadOnlyList<WindowCandidate> FindForProcesses(IReadOnlySet<int> processIds) =>
            Items.Where(x => processIds.Contains(x.ProcessId)).ToArray();
        public int? ForegroundProcessId() => Foreground;
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
