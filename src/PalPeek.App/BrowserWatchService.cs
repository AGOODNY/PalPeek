using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalPeek.Core;
using System.Net;
using System.Threading.RateLimiting;

namespace PalPeek;

public sealed record WebAuthRequest(string InviteId, string Password, string ViewerName);
public sealed record WebViewerRequest(string? SessionId);

public sealed class BrowserWatchService : BackgroundService
{
    public const string SessionCookie = "palpeek_web_session";
    private readonly PalPeekOptions _options;
    private readonly WebInviteService _invites;
    private readonly HostStateStore _state;
    private readonly LeaseManager _leases;
    private readonly WebStreamCoordinator _streams;
    private readonly WebMediaBuffer _media;
    private WebApplication? _app;

    public BrowserWatchService(
        PalPeekOptions options,
        WebInviteService invites,
        HostStateStore state,
        LeaseManager leases,
        WebStreamCoordinator streams,
        WebMediaBuffer media)
    {
        _options = options;
        _invites = invites;
        _state = state;
        _leases = leases;
        _streams = streams;
        _media = media;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, _options.BrowserSharing.LocalPort));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetFixedWindowLimiter(
                    "global",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 900,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
            options.AddPolicy("web-auth", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    AuthPartition(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
            options.AddConcurrencyLimiter("web-media", limiter =>
            {
                limiter.PermitLimit = 12;
                limiter.QueueLimit = 0;
            });
        });

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self'; " +
                "img-src 'self' data:; media-src 'self' blob:; connect-src 'self'; " +
                "worker-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Permissions-Policy"] =
                "camera=(), microphone=(), geolocation=(), gamepad=()";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl = "no-store, max-age=0";
                context.Response.Headers.Pragma = "no-cache";
            }
            await next();
        });
        _app.UseRateLimiter();

        MapStaticAssets(_app);
        MapApi(_app);
        await _app.RunAsync(stoppingToken);
    }

    private void MapStaticAssets(WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/watch"));
        app.MapGet("/watch", () => StaticFile("index.html", "text/html; charset=utf-8"));
        app.MapGet("/watch/{inviteId}", (string inviteId) =>
            StaticFile("index.html", "text/html; charset=utf-8"));
        app.MapGet("/web/app.css", () => StaticFile("app.css", "text/css; charset=utf-8"));
        app.MapGet("/web/app.js", () => StaticFile("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/web/hls.min.js", () => StaticFile("hls.min.js", "text/javascript; charset=utf-8"));
    }

    private void MapApi(WebApplication app)
    {
        app.MapPost("/api/web/v1/auth/{inviteId}", (
            HttpContext context,
            string inviteId,
            WebAuthRequest request) =>
        {
            if (!_options.BrowserSharing.Enabled)
                return WatchingDisabled();
            if (request.InviteId != inviteId)
                return InvalidCredentials();
            WebAuthSession? session;
            try
            {
                session = _invites.Authenticate(inviteId, request.Password, request.ViewerName);
            }
            catch (ArgumentException)
            {
                return InvalidCredentials();
            }
            if (session is null)
                return InvalidCredentials();
            context.Response.Cookies.Append(SessionCookie, session.Id, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Path = "/"
            });
            return Results.Ok(new { authenticated = true, viewerName = session.ViewerName });
        }).RequireRateLimiting("web-auth");

        app.MapGet("/api/web/v1/auth", (HttpContext context) =>
        {
            var session = Authenticate(context);
            return Results.Ok(new
            {
                authenticated = session is not null,
                viewerName = session?.ViewerName
            });
        });

        app.MapDelete("/api/web/v1/auth", (HttpContext context) =>
        {
            _invites.SignOut(context.Request.Cookies[SessionCookie]);
            context.Response.Cookies.Delete(SessionCookie, new CookieOptions
            {
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
            return Results.Ok(new { signedOut = true });
        });

        app.MapGet("/api/web/v1/status", (HttpContext context) =>
        {
            var auth = Authenticate(context);
            if (auth is null)
                return Results.Unauthorized();
            var status = _state.Get();
            if (!_options.BrowserSharing.Enabled)
                return Results.Ok(new { online = false, canWatch = false, message = "主播未开放网页观战。" });
            return Results.Ok(new
            {
                online = true,
                host = status.Nickname,
                game = status.Game is null ? null : new
                {
                    status.Game.Name,
                    status.Game.SessionId
                },
                captureState = status.CaptureState.ToString(),
                quality = _options.BrowserSharing.Quality.ToString(),
                status.ViewerCount,
                maxViewers = Protocol.MaxViewers,
                canWatch = status.Game is not null &&
                           status.CaptureState == CaptureState.Ready &&
                           status.ViewerCount < Protocol.MaxViewers,
                status.Message
            });
        });

        app.MapPost("/api/web/v1/viewers", async (
            HttpContext context,
            WebViewerRequest request,
            CancellationToken cancellationToken) =>
        {
            var auth = Authenticate(context);
            if (auth is null)
                return Results.Unauthorized();
            var status = _state.Get();
            if (!_options.BrowserSharing.Enabled || status.Game is null ||
                status.CaptureState != CaptureState.Ready ||
                request.SessionId != status.Game.SessionId)
                return Results.Conflict(new { code = "session_unavailable", message = "主播尚未开播或分享已经结束。" });
            ViewerLease? lease = null;
            try
            {
                lease = _leases.Reserve(
                    status.Game.SessionId,
                    auth.Id,
                    auth.ViewerName,
                    ViewerTransport.Browser);
                await _streams.EnsureStartedAsync(status.Game, cancellationToken);
            }
            catch (LeaseCapacityException ex)
            {
                return Results.Conflict(new { code = "viewer_limit", message = ex.Message });
            }
            catch
            {
                if (lease is not null)
                    _leases.Release(lease.Id);
                throw;
            }
            if (lease is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new
            {
                leaseId = lease.Id,
                lease.ExpiresAt,
                playlist = $"/api/web/v1/playback/{lease.Id}/live.m3u8"
            });
        });

        app.MapPut("/api/web/v1/viewers/{id}/heartbeat", (
            HttpContext context,
            string id) =>
        {
            var auth = Authenticate(context);
            var lease = auth is null ? null : _leases.Find(id);
            if (lease is null || lease.Transport != ViewerTransport.Browser ||
                lease.ViewerId != auth!.Id)
                return Results.NotFound(new { code = "lease_expired", message = "观战名额已过期。" });
            try
            {
                var renewed = _leases.Heartbeat(id);
                return Results.Ok(new { renewed.ExpiresAt });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { code = "lease_expired", message = "观战名额已过期。" });
            }
        });

        app.MapDelete("/api/web/v1/viewers/{id}", (HttpContext context, string id) =>
        {
            var auth = Authenticate(context);
            var lease = auth is null ? null : _leases.Find(id);
            if (lease is null || lease.Transport != ViewerTransport.Browser ||
                lease.ViewerId != auth!.Id)
                return Results.NotFound();
            _leases.Release(id);
            return Results.Ok(new { released = true });
        });

        app.MapGet("/api/web/v1/playback/{id}/live.m3u8", async (
            HttpContext context,
            string id,
            CancellationToken cancellationToken) =>
        {
            var lease = AuthorizedLease(context, id);
            if (lease is null)
                return Results.Unauthorized();
            string? playlist = null;
            for (var attempt = 0; attempt < 50 && playlist is null; attempt++)
            {
                playlist = _media.BuildPlaylist(lease.SessionId);
                if (playlist is null)
                    await Task.Delay(100, cancellationToken);
            }
            return playlist is null
                ? Results.Json(
                    new { code = "stream_starting", message = _media.GetError(lease.SessionId) ?? "视频正在准备。" },
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Text(playlist, "application/vnd.apple.mpegurl");
        }).RequireRateLimiting("web-media");

        app.MapGet("/api/web/v1/playback/{id}/init.mp4", (
            HttpContext context,
            string id) =>
        {
            var lease = AuthorizedLease(context, id);
            if (lease is null)
                return Results.Unauthorized();
            var payload = _media.GetInitialization(lease.SessionId);
            return payload is null ? Results.NotFound() : Results.Bytes(payload, "video/mp4");
        }).RequireRateLimiting("web-media");

        app.MapGet("/api/web/v1/playback/{id}/segment-{sequence:long}.m4s", (
            HttpContext context,
            string id,
            long sequence) =>
        {
            var lease = AuthorizedLease(context, id);
            if (lease is null)
                return Results.Unauthorized();
            var segment = _media.GetSegment(lease.SessionId, sequence);
            return segment is null
                ? Results.NotFound()
                : Results.Bytes(segment.Payload, "video/iso.segment");
        }).RequireRateLimiting("web-media");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
            await _app.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private WebAuthSession? Authenticate(HttpContext context) =>
        _invites.ValidateSession(context.Request.Cookies[SessionCookie]);

    private ViewerLease? AuthorizedLease(HttpContext context, string id)
    {
        var auth = Authenticate(context);
        var lease = auth is null ? null : _leases.Find(id);
        var current = _state.Get().Game?.SessionId;
        return lease is not null && lease.Transport == ViewerTransport.Browser &&
               lease.ViewerId == auth!.Id && lease.SessionId == current &&
               _options.BrowserSharing.Enabled
            ? lease
            : null;
    }

    private static IResult InvalidCredentials() => Results.Json(
        new { code = "invalid_credentials", message = "链接或口令无效。" },
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult WatchingDisabled() => Results.Json(
        new { code = "watching_disabled", message = "主播尚未开启网页观战。" },
        statusCode: StatusCodes.Status403Forbidden);

    private static string AuthPartition(HttpContext context)
    {
        var invite = context.Request.RouteValues["inviteId"]?.ToString() ?? "unknown";
        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (context.Connection.RemoteIpAddress is not null &&
            IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
                source = forwarded.Split(',')[0].Trim();
        }
        return $"{invite}|{source}";
    }

    private static IResult StaticFile(string name, string contentType)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "web", name);
        return File.Exists(path)
            ? Results.File(path, contentType)
            : Results.NotFound();
    }
}
