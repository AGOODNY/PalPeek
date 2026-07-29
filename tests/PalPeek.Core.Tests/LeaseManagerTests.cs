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
}
