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
public sealed record FriendDiscoveryDiagnostics(
    int TailnetPeerCount,
    int ReachableApiCount,
    int FailedApiCount,
    string? LastApiError,
    DateTimeOffset UpdatedAt);

public sealed class FriendDiscovery : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ITailscaleService _tailscale;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ConcurrentDictionary<string, FriendStream> _active = new();
    private readonly ConcurrentDictionary<string, string> _reportedProtocolErrors = new();
    private readonly ConcurrentDictionary<string, byte> _reachableApis = new();
    private readonly ConcurrentDictionary<string, string> _apiFailures = new();
    private int _tailnetPeerCount;
    private long _updatedAtUtcTicks;

    public FriendDiscovery(ITailscaleService tailscale) => _tailscale = tailscale;

    public event EventHandler<IReadOnlyList<FriendStream>>? Changed;
    public event EventHandler<FriendDiscoveryError>? ProbeFailed;
    public IReadOnlyList<FriendStream> Current => _active.Values
        .OrderBy(x => x.Status.Nickname, StringComparer.CurrentCultureIgnoreCase).ToArray();
    public FriendDiscoveryDiagnostics Diagnostics
    {
        get
        {
            var ticks = Interlocked.Read(ref _updatedAtUtcTicks);
            return new FriendDiscoveryDiagnostics(
                Volatile.Read(ref _tailnetPeerCount),
                _reachableApis.Count,
                _apiFailures.Count,
                _apiFailures.Values.FirstOrDefault(),
                ticks == 0
                    ? DateTimeOffset.MinValue
                    : new DateTimeOffset(ticks, TimeSpan.Zero));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = await _tailscale.GetSnapshotAsync(stoppingToken);
            var onlinePeers = snapshot.OnlinePeers;
            var peerIds = onlinePeers.Select(x => x.Id).ToHashSet();
            Volatile.Write(ref _tailnetPeerCount, onlinePeers.Count);
            Interlocked.Exchange(
                ref _updatedAtUtcTicks,
                DateTimeOffset.UtcNow.UtcTicks);
            foreach (var stale in _active.Keys.Where(x => !peerIds.Contains(x)))
                _active.TryRemove(stale, out _);
            foreach (var stale in _reachableApis.Keys.Where(x => !peerIds.Contains(x)))
                _reachableApis.TryRemove(stale, out _);
            foreach (var stale in _apiFailures.Keys.Where(x => !peerIds.Contains(x)))
                _apiFailures.TryRemove(stale, out _);

            await Task.WhenAll(onlinePeers.Select(peer => ProbeAsync(peer, stoppingToken)));
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
            if (status is null)
            {
                RecordApiFailure(peer, "好友的 PalPeek API 没有返回状态。");
                return;
            }

            _reachableApis[peer.Id] = 0;
            _apiFailures.TryRemove(peer.Id, out _);
            _reportedProtocolErrors.TryRemove(peer.Id, out _);
            if (status.Game is not null)
                _active[peer.Id] = new FriendStream(peer, status);
            else
                _active.TryRemove(peer.Id, out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _active.TryRemove(peer.Id, out _);
            RecordApiFailure(
                peer,
                $"好友在线，但 TCP {Protocol.ApiPort} 无法访问。请检查双方防火墙和 Tailnet ACL。");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _active.TryRemove(peer.Id, out _);
            const string message = "好友返回了无法识别的状态，双方 PalPeek 版本可能不兼容。";
            RecordApiFailure(peer, message);
            if (_reportedProtocolErrors.TryAdd(peer.Id, message))
                ProbeFailed?.Invoke(this, new FriendDiscoveryError(peer, message));
        }
    }

    private void RecordApiFailure(NodeIdentity peer, string message)
    {
        _reachableApis.TryRemove(peer.Id, out _);
        _apiFailures[peer.Id] = message;
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
