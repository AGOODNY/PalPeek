using PalPeek.Core;
using System.Net;

namespace PalPeek.Core.Tests;

public sealed class LeaseManagerTests
{
    [Fact]
    public void FourthViewerIsRejected()
    {
        var leases = new LeaseManager();
        leases.Reserve("session", "one", "One");
        leases.Reserve("session", "two", "Two");
        leases.Reserve("session", "three", "Three");

        var error = Assert.Throws<LeaseCapacityException>(
            () => leases.Reserve("session", "four", "Four"));
        Assert.Equal("观看人数已满", error.Message);
    }

    [Fact]
    public void SameViewerRenewsExistingLease()
    {
        var leases = new LeaseManager();
        var first = leases.Reserve("session", "one", "One");
        var second = leases.Reserve("session", "one", "One");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(leases.Active("session"));
    }

    [Fact]
    public void BrowserAndMoonlightShareTheSameThreeViewerLimit()
    {
        var leases = new LeaseManager();
        leases.Reserve("session", "moonlight-one", "One", ViewerTransport.Moonlight);
        leases.Reserve("session", "browser-one", "Two", ViewerTransport.Browser);
        leases.Reserve("session", "browser-two", "Three", ViewerTransport.Browser);

        Assert.Throws<LeaseCapacityException>(() =>
            leases.Reserve("session", "moonlight-two", "Four", ViewerTransport.Moonlight));
        Assert.Equal(2, leases.Active("session").Count(x => x.Transport == ViewerTransport.Browser));
    }

    [Fact]
    public void ExpiredBrowserLeaseReleasesCapacity()
    {
        var clock = new AdjustableTimeProvider();
        var leases = new LeaseManager(clock);
        leases.Reserve("session", "one", "One", ViewerTransport.Browser);
        leases.Reserve("session", "two", "Two", ViewerTransport.Browser);
        leases.Reserve("session", "three", "Three", ViewerTransport.Moonlight);

        clock.Advance(TimeSpan.FromSeconds(16));

        var replacement = leases.Reserve("session", "four", "Four", ViewerTransport.Browser);
        Assert.Equal(ViewerTransport.Browser, replacement.Transport);
        Assert.Single(leases.Active("session"));
    }

    [Fact]
    public void ClearingOneSessionLeavesOtherSessions()
    {
        var leases = new LeaseManager();
        leases.Reserve("one", "a", "A");
        leases.Reserve("two", "b", "B");

        leases.ClearSession("one");

        Assert.Empty(leases.Active("one"));
        Assert.Single(leases.Active("two"));
    }

    [Fact]
    public async Task ConcurrentReservationsNeverExceedThree()
    {
        var leases = new LeaseManager();
        var attempts = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() =>
            {
                try
                {
                    leases.Reserve("session", index.ToString(), $"Viewer {index}");
                    return true;
                }
                catch (LeaseCapacityException)
                {
                    return false;
                }
            }));

        var accepted = await Task.WhenAll(attempts);

        Assert.Equal(Protocol.MaxViewers, accepted.Count(x => x));
        Assert.Equal(Protocol.MaxViewers, leases.Active("session").Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void TransientHeartbeatFailuresAreRetried(HttpStatusCode statusCode)
    {
        Assert.True(PeerConnectionPolicy.IsTransientHeartbeatFailure(statusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void PermanentHeartbeatFailuresEndTheLease(HttpStatusCode statusCode)
    {
        Assert.False(PeerConnectionPolicy.IsTransientHeartbeatFailure(statusCode));
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
