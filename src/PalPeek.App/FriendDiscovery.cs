using Microsoft.Extensions.Hosting;
using PalPeek.Core;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalPeek;

public sealed record FriendStream(NodeIdentity Node, HostStatus Status);
public sealed record FriendDiscoveryError(NodeIdentity Node, string Message);

public sealed class FriendDiscovery : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ITailscaleService _tailscale;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ConcurrentDictionary<string, FriendStream> _active = new();
    private readonly ConcurrentDictionary<string, string> _reportedProtocolErrors = new();

    public FriendDiscovery(ITailscaleService tailscale) => _tailscale = tailscale;

    public event EventHandler<IReadOnlyList<FriendStream>>? Changed;
    public event EventHandler<FriendDiscoveryError>? ProbeFailed;
    public IReadOnlyList<FriendStream> Current => _active.Values
        .OrderBy(x => x.Status.Nickname, StringComparer.CurrentCultureIgnoreCase).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await _tailscale.GetSnapshotAsync(stoppingToken);
            var peerIds = snapshot.Peers.Select(x => x.Id).ToHashSet();
            foreach (var stale in _active.Keys.Where(x => !peerIds.Contains(x)))
                _active.TryRemove(stale, out _);

            await Task.WhenAll(snapshot.Peers.Select(peer => ProbeAsync(peer, stoppingToken)));
            Changed?.Invoke(this, Current);
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task ProbeAsync(NodeIdentity peer, CancellationToken token)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<HostStatus>(
                $"http://{peer.Ip}:{Protocol.ApiPort}/api/v1/status", JsonOptions, token);
            _reportedProtocolErrors.TryRemove(peer.Id, out _);
            if (status?.Game is not null)
                _active[peer.Id] = new FriendStream(peer, status);
            else
                _active.TryRemove(peer.Id, out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _active.TryRemove(peer.Id, out _);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _active.TryRemove(peer.Id, out _);
            const string message = "好友返回了无法识别的状态，双方 PalPeek 版本可能不兼容。";
            if (_reportedProtocolErrors.TryAdd(peer.Id, message))
                ProbeFailed?.Invoke(this, new FriendDiscoveryError(peer, message));
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
