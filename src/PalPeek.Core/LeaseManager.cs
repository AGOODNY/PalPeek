using System.Collections.Concurrent;

namespace PalPeek.Core;

public sealed record ViewerLease(
    string Id,
    string SessionId,
    string ViewerId,
    string ViewerName,
    ViewerTransport Transport,
    DateTimeOffset ExpiresAt);

public sealed class LeaseManager
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, ViewerLease> _leases = new();
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public LeaseManager(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public IReadOnlyCollection<ViewerLease> Active(string sessionId)
    {
        lock (_gate)
        {
            PruneCore();
            return _leases.Values.Where(x => x.SessionId == sessionId).ToArray();
        }
    }

    public ViewerLease Reserve(
        string sessionId,
        string viewerId,
        string viewerName,
        ViewerTransport transport = ViewerTransport.Moonlight)
    {
        lock (_gate)
        {
            PruneCore();
            var existing = _leases.Values.FirstOrDefault(x =>
                x.SessionId == sessionId && x.ViewerId == viewerId &&
                x.Transport == transport);
            if (existing is not null)
                return RenewCore(existing);

            if (_leases.Values.Count(x => x.SessionId == sessionId) >= Protocol.MaxViewers)
                throw new LeaseCapacityException();

            var lease = new ViewerLease(
                Guid.NewGuid().ToString("N"),
                sessionId,
                viewerId,
                viewerName,
                transport,
                _clock.GetUtcNow() + LeaseDuration);
            _leases[lease.Id] = lease;
            return lease;
        }
    }

    public ViewerLease? Find(string id)
    {
        lock (_gate)
        {
            PruneCore();
            return _leases.TryGetValue(id, out var lease) ? lease : null;
        }
    }

    public ViewerLease Heartbeat(string id)
    {
        lock (_gate)
        {
            PruneCore();
            if (!_leases.TryGetValue(id, out var current))
                throw new KeyNotFoundException("观看名额已过期，请重新进入观战。");
            return RenewCore(current);
        }
    }

    public bool Release(string id) => ReleaseLease(id) is not null;

    public ViewerLease? ReleaseLease(string id)
    {
        lock (_gate)
            return _leases.TryRemove(id, out var lease) ? lease : null;
    }

    public IReadOnlyCollection<ViewerLease> ClearSession(string sessionId)
    {
        lock (_gate)
        {
            var removed = new List<ViewerLease>();
            foreach (var pair in _leases.Where(x => x.Value.SessionId == sessionId))
            {
                if (_leases.TryRemove(pair.Key, out var lease))
                    removed.Add(lease);
            }
            return removed;
        }
    }

    public IReadOnlyCollection<ViewerLease> Prune()
    {
        lock (_gate)
            return PruneCore();
    }

    private ViewerLease RenewCore(ViewerLease current)
    {
        var renewed = current with { ExpiresAt = _clock.GetUtcNow() + LeaseDuration };
        _leases[current.Id] = renewed;
        return renewed;
    }

    private IReadOnlyCollection<ViewerLease> PruneCore()
    {
        var removed = new List<ViewerLease>();
        var now = _clock.GetUtcNow();
        foreach (var pair in _leases.Where(x => x.Value.ExpiresAt <= now))
        {
            if (_leases.TryRemove(pair.Key, out var lease))
                removed.Add(lease);
        }
        return removed;
    }
}

public sealed class LeaseCapacityException : Exception
{
    public LeaseCapacityException() : base("观看人数已满") { }
}
