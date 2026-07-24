namespace PalPeek.Core;

public sealed record SharingSnapshot(
    GameInfo? DetectedGame,
    bool SharingEnabled,
    string? DetectionMessage);

public sealed class SharingControl
{
    private readonly object _gate = new();
    private SharingSnapshot _snapshot = new(null, false, null);
    private string? _stoppedSessionId;

    public event EventHandler<SharingSnapshot>? Changed;

    public SharingSnapshot Get()
    {
        lock (_gate)
            return _snapshot;
    }

    public void UpdateDetection(GameInfo? game, string? message)
    {
        SharingSnapshot next;
        lock (_gate)
        {
            if (game is null)
                _stoppedSessionId = null;
            else if (_snapshot.DetectedGame?.SessionId != game.SessionId)
                _stoppedSessionId = null;

            var sharingEnabled = game is not null && game.SessionId != _stoppedSessionId;
            next = new SharingSnapshot(game, sharingEnabled, message);
            if (next == _snapshot)
                return;
            _snapshot = next;
        }
        Changed?.Invoke(this, next);
    }

    public bool StartSharing()
    {
        SharingSnapshot next;
        lock (_gate)
        {
            if (_snapshot.DetectedGame is null)
                return false;
            _stoppedSessionId = null;
            next = _snapshot with { SharingEnabled = true };
            if (next == _snapshot)
                return true;
            _snapshot = next;
        }
        Changed?.Invoke(this, next);
        return true;
    }

    public void StopSharing()
    {
        SharingSnapshot next;
        lock (_gate)
        {
            _stoppedSessionId = _snapshot.DetectedGame?.SessionId;
            next = _snapshot with { SharingEnabled = false };
            if (next == _snapshot)
                return;
            _snapshot = next;
        }
        Changed?.Invoke(this, next);
    }
}
