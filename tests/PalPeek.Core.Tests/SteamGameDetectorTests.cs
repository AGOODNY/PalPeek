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

    private string CreateGame()
    {
        var steamApps = Path.Combine(_root, "steamapps");
        var install = Path.Combine(steamApps, "common", "Detector Game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_99.acf"), """
        "AppState"
        {
            "appid" "99"
            "name" "Detector Game"
            "installdir" "Detector Game"
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
