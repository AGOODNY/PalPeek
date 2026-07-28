using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class SharingControlTests
{
    private static GameInfo Game(string session = "session-1") =>
        new(10, "Test Game", @"C:\Games\Test", 42, 100, session);

    [Fact]
    public void SharingIsDisabledBeforeAGameIsDetected()
    {
        var control = new SharingControl();

        Assert.False(control.Get().SharingEnabled);
        Assert.False(control.StartSharing());
    }

    [Fact]
    public void ADetectedGameStartsSharingAutomatically()
    {
        var control = new SharingControl();
        control.UpdateDetection(Game(), null);

        Assert.True(control.Get().SharingEnabled);
    }

    [Fact]
    public void StopSharingSuppressesTheCurrentGame()
    {
        var control = new SharingControl();
        control.UpdateDetection(Game(), null);
        control.StopSharing();

        control.UpdateDetection(Game(), "window updated");

        Assert.False(control.Get().SharingEnabled);
    }

    [Fact]
    public void ANewGameStartsSharingAfterThePreviousGameWasStopped()
    {
        var control = new SharingControl();
        control.UpdateDetection(Game(), null);
        control.StopSharing();

        control.UpdateDetection(Game("session-2"), null);

        Assert.True(control.Get().SharingEnabled);
    }

    [Fact]
    public void SharingCanBeResumedForTheCurrentGame()
    {
        var control = new SharingControl();
        control.UpdateDetection(Game(), null);
        control.StopSharing();

        Assert.True(control.StartSharing());
        Assert.True(control.Get().SharingEnabled);
    }

    [Fact]
    public void InvisibleModeImmediatelySuppressesTheCurrentGame()
    {
        var options = new PalPeekOptions();
        var control = new SharingControl(options);
        control.UpdateDetection(Game(), null);

        options.Invisible = true;
        control.RefreshPolicy();

        Assert.False(control.Get().SharingEnabled);
        Assert.Equal(SharingBlockReason.Invisible, control.Get().BlockReason);
        Assert.False(control.StartSharing());
    }

    [Fact]
    public void ABlockedGameStaysSuppressedAcrossSessions()
    {
        var options = new PalPeekOptions { BlockedGameAppIds = [10] };
        var control = new SharingControl(options);

        control.UpdateDetection(Game(), null);
        control.UpdateDetection(Game("session-2"), null);

        Assert.False(control.Get().SharingEnabled);
        Assert.Equal(SharingBlockReason.GameDisabled, control.Get().BlockReason);
    }

    [Fact]
    public void RemovingAGameBlockRestoresSharing()
    {
        var options = new PalPeekOptions { BlockedGameAppIds = [10] };
        var control = new SharingControl(options);
        control.UpdateDetection(Game(), null);

        options.BlockedGameAppIds.Clear();
        control.RefreshPolicy();

        Assert.True(control.Get().SharingEnabled);
        Assert.Equal(SharingBlockReason.None, control.Get().BlockReason);
    }
}
