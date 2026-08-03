using Microsoft.Extensions.Hosting;
using PalPeek.Core;

namespace PalPeek;

public sealed class WebStreamCoordinator : BackgroundService
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private readonly LeaseManager _leases;
    private readonly HostStateStore _state;
    private readonly PalPeekOptions _options;
    private readonly SunshineBridge _sunshine;
    private readonly WebMediaBuffer _buffer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _activeSessionId;
    private DateTimeOffset? _unusedSince;

    public WebStreamCoordinator(
        LeaseManager leases,
        HostStateStore state,
        PalPeekOptions options,
        SunshineBridge sunshine,
        WebMediaBuffer buffer)
    {
        _leases = leases;
        _state = state;
        _options = options;
        _sunshine = sunshine;
        _buffer = buffer;
    }

    public async Task EnsureStartedAsync(GameInfo game, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeSessionId == game.SessionId)
                return;
            if (_activeSessionId is not null)
                await StopStreamCoreAsync(cancellationToken);
            _buffer.Reset(game.SessionId);
            await _sunshine.StartWebStreamAsync(
                game.SessionId,
                _options.BrowserSharing.Quality,
                WebMediaPipeService.PipeName,
                cancellationToken);
            _activeSessionId = game.SessionId;
            _unusedSince = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(stoppingToken);
            try
            {
                var status = _state.Get();
                var sessionId = status.Game?.SessionId;
                var browserViewers = sessionId is null
                    ? 0
                    : _leases.Active(sessionId).Count(x => x.Transport == ViewerTransport.Browser);
                if (_activeSessionId is not null &&
                    (_activeSessionId != sessionId || !_options.BrowserSharing.Enabled))
                {
                    await StopStreamCoreAsync(stoppingToken);
                }
                else if (_activeSessionId is not null && browserViewers == 0)
                {
                    _unusedSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - _unusedSince >= StopGrace)
                        await StopStreamCoreAsync(stoppingToken);
                }
                else if (browserViewers > 0)
                {
                    _unusedSince = null;
                }
            }
            finally
            {
                _gate.Release();
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopStreamCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task StopStreamCoreAsync(CancellationToken cancellationToken)
    {
        if (_activeSessionId is null)
            return;
        var old = _activeSessionId;
        _activeSessionId = null;
        _unusedSince = null;
        try
        {
            await _sunshine.StopWebStreamAsync(old, cancellationToken);
        }
        finally
        {
            _buffer.Clear();
        }
    }
}
