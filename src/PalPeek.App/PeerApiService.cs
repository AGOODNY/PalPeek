using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalPeek.Core;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalPeek;

public sealed class PeerApiService : BackgroundService
{
    private readonly ITailscaleService _tailscale;
    private readonly HostStateStore _state;
    private readonly LeaseManager _leases;
    private readonly SunshineBridge _sunshine;
    private readonly EventHub _events;
    private readonly PalPeekOptions _options;
    private WebApplication? _app;

    public PeerApiService(
        ITailscaleService tailscale,
        HostStateStore state,
        LeaseManager leases,
        SunshineBridge sunshine,
        EventHub events,
        PalPeekOptions options)
    {
        _tailscale = tailscale;
        _state = state;
        _leases = leases;
        _sunshine = sunshine;
        _events = events;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TailscaleSnapshot snapshot;
        do
        {
            snapshot = await _tailscale.GetSnapshotAsync(stoppingToken);
            if (!snapshot.Running)
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        } while (!snapshot.Running && !stoppingToken.IsCancellationRequested);

        if (snapshot.SelfIp is null)
            return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Parse(snapshot.SelfIp), Protocol.ApiPort));
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            var current = await _tailscale.GetSnapshotAsync(context.RequestAborted);
            if (!current.ContainsAddress(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new ApiError(Protocol.SchemaVersion, "not_tailnet_peer", "仅允许当前 Tailnet 中的好友访问。"),
                    context.RequestAborted);
                return;
            }
            await next();
        });

        _app.MapGet("/api/v1/status", () => Results.Ok(_state.Get()));
        _app.MapPost("/api/v1/pair", async (PairRequest request, CancellationToken token) =>
        {
            if (request.SchemaVersion != Protocol.SchemaVersion ||
                request.Pin.Length != 4 || !request.Pin.All(char.IsDigit))
                return Results.BadRequest(new ApiError(Protocol.SchemaVersion, "invalid_pair_request", "配对请求无效。"));
            try
            {
                await _sunshine.PairAsync(request.Pin, request.ClientId, token);
                return Results.Ok(new { schemaVersion = Protocol.SchemaVersion, paired = true });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        });
        _app.MapPost("/api/v1/reservations", (ReservationRequest request) =>
        {
            var status = _state.Get();
            if (request.SchemaVersion != Protocol.SchemaVersion ||
                status.Game is null || status.Game.SessionId != request.SessionId ||
                status.CaptureState != CaptureState.Ready)
                return Results.Conflict(new ApiError(Protocol.SchemaVersion, "session_unavailable", "观战会话已结束或暂不可用。"));
            try
            {
                var lease = _leases.Reserve(request.SessionId, request.ViewerId, request.ViewerName);
                _events.Publish(Event("viewer.joined", status.Game, request.ViewerName));
                return Results.Ok(new ReservationResponse(
                    Protocol.SchemaVersion,
                    lease.Id,
                    lease.SessionId,
                    snapshot.SelfIp,
                    lease.ExpiresAt,
                    $"palpeek://watch/{Environment.MachineName}/{lease.SessionId}"));
            }
            catch (LeaseCapacityException ex)
            {
                return Results.Conflict(new ApiError(Protocol.SchemaVersion, "viewer_limit", ex.Message));
            }
        });
        _app.MapPut("/api/v1/reservations/{id}/heartbeat", (string id) =>
        {
            try
            {
                var lease = _leases.Heartbeat(id);
                return Results.Ok(new
                {
                    schemaVersion = Protocol.SchemaVersion,
                    leaseId = lease.Id,
                    expiresAt = lease.ExpiresAt
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new ApiError(Protocol.SchemaVersion, "lease_expired", ex.Message));
            }
        });
        _app.MapDelete("/api/v1/reservations/{id}", (string id) =>
        {
            var lease = _leases.ReleaseLease(id);
            if (lease is null)
                return Results.NotFound(new ApiError(
                    Protocol.SchemaVersion, "lease_not_found", "观看名额不存在。"));
            var status = _state.Get();
            _events.Publish(Event("viewer.left", status.Game, lease.ViewerName, lease.SessionId));
            return Results.Ok(new { schemaVersion = Protocol.SchemaVersion, released = true });
        });
        _app.MapGet("/api/v1/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await foreach (var item in _events.Subscribe(context.RequestAborted))
            {
                await context.Response.WriteAsync(
                    $"event: {item.Type}\ndata: {JsonSerializer.Serialize(item)}\n\n",
                    context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });

        await _app.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            await _app.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private PalPeekEvent Event(
        string type,
        GameInfo? game,
        string? viewer,
        string? sessionId = null)
    {
        sessionId ??= game?.SessionId;
        return new(Protocol.SchemaVersion, Guid.NewGuid().ToString("N"), type,
            DateTimeOffset.UtcNow, _options.Nickname, sessionId, game, viewer,
            game is null ? null : $"palpeek://watch/{Environment.MachineName}/{game.SessionId}");
    }
}
