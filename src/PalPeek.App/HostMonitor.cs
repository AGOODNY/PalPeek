using Microsoft.Extensions.Hosting;
using PalPeek.Core;

namespace PalPeek;

public sealed class HostMonitor : BackgroundService
{
    private readonly SteamGameDetector _detector;
    private readonly ITailscaleService _tailscale;
    private readonly SunshineBridge _sunshine;
    private readonly HostStateStore _state;
    private readonly LeaseManager _leases;
    private readonly EventHub _events;
    private readonly PalPeekOptions _options;
    private GameInfo? _lastGame;

    public HostMonitor(
        SteamGameDetector detector,
        ITailscaleService tailscale,
        SunshineBridge sunshine,
        HostStateStore state,
        LeaseManager leases,
        EventHub events,
        PalPeekOptions options)
    {
        _detector = detector;
        _tailscale = tailscale;
        _sunshine = sunshine;
        _state = state;
        _leases = leases;
        _events = events;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var tailnet = await _tailscale.GetSnapshotAsync(stoppingToken);
            var detection = _detector.Tick();
            var message = tailnet.Running ? detection.Message : tailnet.Error;
            var captureState = detection.State;

            if (detection.Game is not null && tailnet.Running)
            {
                try
                {
                    var host = await _sunshine.EnsureTargetAsync(detection.Game, stoppingToken);
                    captureState = MapCaptureState(host);
                    message = host.Message ?? detection.Message;
                    _lastGame = detection.Game;
                }
                catch (Exception ex)
                {
                    captureState = CaptureState.HostUnavailable;
                    message = ex.Message;
                }
            }
            else if (_lastGame is not null)
            {
                var old = _lastGame;
                _lastGame = null;
                _leases.ClearSession(old.SessionId);
                try
                {
                    if (_sunshine.IsRunning)
                        await _sunshine.EndSessionAsync(old.SessionId, stoppingToken);
                }
                catch (Exception ex)
                {
                    message = $"结束 Sunshine 会话时发生错误：{ex.Message}";
                }

                try
                {
                    await _sunshine.StopHostAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    message = $"停止 Sunshine Host 时发生错误：{ex.Message}";
                }
                Publish("stream.unavailable", old);
            }

            foreach (var expired in _leases.Prune())
                Publish("viewer.left", detection.Game, expired.ViewerName);

            var viewers = detection.Game is null ? 0 : _leases.Active(detection.Game.SessionId).Count;
            var status = new HostStatus(
                Protocol.SchemaVersion,
                _options.Nickname,
                BuildInfo.Version,
                tailnet.Running,
                detection.Game,
                captureState,
                _options.Quality,
                viewers,
                tailnet.Running && detection.Game is not null &&
                captureState == CaptureState.Ready && viewers < Protocol.MaxViewers,
                message);
            var previous = _state.Get();
            _state.Set(status);

            if (previous.Game?.SessionId != status.Game?.SessionId && status.Game is not null)
                Publish("stream.available", status.Game);
            else if (status.Game is not null && previous != status)
                Publish("stream.updated", status.Game);

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private static CaptureState MapCaptureState(SunshineRuntimeStatus status)
    {
        if (status.Capture == SunshineCaptureStatus.Error)
            return CaptureState.WindowUnavailable;
        if (status.Audio == SunshineAudioStatus.Error)
            return CaptureState.AudioUnavailable;
        if (status.Encoding == SunshineEncodingStatus.Error)
            return CaptureState.HostUnavailable;
        if (status.Target is null ||
            status.Capture == SunshineCaptureStatus.Idle ||
            status.Encoding is SunshineEncodingStatus.WaitingForTarget or SunshineEncodingStatus.Probing)
            return CaptureState.Stabilizing;
        return CaptureState.Ready;
    }

    private void Publish(string type, GameInfo? game, string? viewer = null) =>
        _events.Publish(new PalPeekEvent(
            Protocol.SchemaVersion,
            Guid.NewGuid().ToString("N"),
            type,
            DateTimeOffset.UtcNow,
            _options.Nickname,
            game?.SessionId,
            game,
            viewer,
            game is null ? null : $"palpeek://watch/{Environment.MachineName}/{game.SessionId}"));
}
