using Microsoft.Extensions.Hosting;
using PalPeek.Core;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;

namespace PalPeek;

public sealed record FriendStream(NodeIdentity Node, HostStatus Status);

public sealed class FriendDiscovery : BackgroundService
{
    private readonly ITailscaleService _tailscale;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ConcurrentDictionary<string, FriendStream> _active = new();

    public FriendDiscovery(ITailscaleService tailscale) => _tailscale = tailscale;

    public event EventHandler<IReadOnlyList<FriendStream>>? Changed;
    public IReadOnlyList<FriendStream> Current => _active.Values
        .OrderBy(x => x.Status.Nickname, StringComparer.CurrentCultureIgnoreCase).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await _tailscale.GetSnapshotAsync(stoppingToken);
            var onlineIds = snapshot.Peers.Where(x => x.Online).Select(x => x.Id).ToHashSet();
            foreach (var stale in _active.Keys.Where(x => !onlineIds.Contains(x)))
                _active.TryRemove(stale, out _);

            await Task.WhenAll(snapshot.Peers.Where(x => x.Online).Select(peer => ProbeAsync(peer, stoppingToken)));
            Changed?.Invoke(this, Current);
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task ProbeAsync(NodeIdentity peer, CancellationToken token)
    {
        try
        {
            var status = await _http.GetFromJsonAsync<HostStatus>(
                $"http://{peer.Ip}:{Protocol.ApiPort}/api/v1/status", token);
            if (status?.Game is not null)
                _active[peer.Id] = new FriendStream(peer, status);
            else
                _active.TryRemove(peer.Id, out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _active.TryRemove(peer.Id, out _);
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
