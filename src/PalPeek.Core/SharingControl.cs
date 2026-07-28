namespace PalPeek.Core;

public sealed record SharingSnapshot(
    GameInfo? DetectedGame,
    bool SharingEnabled,
    string? DetectionMessage,
    SharingBlockReason BlockReason = SharingBlockReason.None);

public enum SharingBlockReason
{
    None,
    ManuallyStopped,
    Invisible,
    GameDisabled
}

public sealed class SharingControl
{
    private readonly object _gate = new();
    private readonly PalPeekOptions _options;
    private SharingSnapshot _snapshot = new(null, false, null);
    private string? _stoppedSessionId;

    public SharingControl(PalPeekOptions? options = null) =>
        _options = options ?? new PalPeekOptions();

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

            next = CreateSnapshot(game, message);
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
            if (_options.Invisible ||
                _options.BlockedGameAppIds.Contains(_snapshot.DetectedGame.AppId))
                return false;
            _stoppedSessionId = null;
            next = CreateSnapshot(
                _snapshot.DetectedGame,
                _snapshot.DetectionMessage);
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
            next = CreateSnapshot(
                _snapshot.DetectedGame,
                _snapshot.DetectionMessage);
            if (next == _snapshot)
                return;
            _snapshot = next;
        }
        Changed?.Invoke(this, next);
    }

    public void RefreshPolicy()
    {
        SharingSnapshot next;
        lock (_gate)
        {
            next = CreateSnapshot(
                _snapshot.DetectedGame,
                _snapshot.DetectionMessage);
            if (next == _snapshot)
                return;
            _snapshot = next;
        }
        Changed?.Invoke(this, next);
    }

    private SharingSnapshot CreateSnapshot(GameInfo? game, string? message)
    {
        var reason = game is null
            ? SharingBlockReason.None
            : _options.Invisible
                ? SharingBlockReason.Invisible
                : _options.BlockedGameAppIds.Contains(game.AppId)
                    ? SharingBlockReason.GameDisabled
                    : game.SessionId == _stoppedSessionId
                        ? SharingBlockReason.ManuallyStopped
                        : SharingBlockReason.None;
        return new SharingSnapshot(
            game,
            game is not null && reason == SharingBlockReason.None,
            message,
            reason);
    }
}
