using PalPeek.Core;
using System.Collections.Concurrent;

namespace PalPeek;

public sealed record WebInviteView(
    string Id,
    string Name,
    bool Enabled,
    DateTimeOffset CreatedAt,
    string? Url);

public sealed record WebAuthSession(
    string Id,
    string InviteId,
    int CredentialVersion,
    string ViewerName,
    DateTimeOffset ExpiresAt);

public sealed class WebInviteService
{
    private static readonly TimeSpan AuthLifetime = TimeSpan.FromHours(12);
    private readonly object _gate = new();
    private readonly PalPeekOptions _options;
    private readonly ConfigStore _config;
    private readonly ConcurrentDictionary<string, WebAuthSession> _sessions = new();

    public WebInviteService(PalPeekOptions options, ConfigStore config)
    {
        _options = options;
        _config = config;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<WebInviteView> List()
    {
        lock (_gate)
        {
            return _options.BrowserSharing.Invites
                .OrderBy(x => x.CreatedAt)
                .Select(ToView)
                .ToArray();
        }
    }

    public WebInviteView Create(string name, string password)
    {
        name = NormalizeName(name);
        var credentials = WebInvitePassword.Hash(password);
        WebInvite invite;
        lock (_gate)
        {
            do
            {
                invite = new WebInvite
                {
                    Id = WebInvitePassword.CreateInviteId(),
                    Name = name,
                    PasswordSalt = credentials.Salt,
                    PasswordHash = credentials.Hash
                };
            } while (_options.BrowserSharing.Invites.Any(x => x.Id == invite.Id));

            _options.BrowserSharing.Invites.Add(invite);
            _config.Save(_options);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return ToView(invite);
    }

    public void ChangePassword(string id, string password)
    {
        var credentials = WebInvitePassword.Hash(password);
        lock (_gate)
        {
            var invite = FindRequired(id);
            invite.PasswordSalt = credentials.Salt;
            invite.PasswordHash = credentials.Hash;
            invite.PasswordIterations = WebInvitePassword.Iterations;
            invite.CredentialVersion++;
            _config.Save(_options);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var invite = FindRequired(id);
            if (invite.Enabled == enabled)
                return;
            invite.Enabled = enabled;
            invite.CredentialVersion++;
            _config.Save(_options);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var invite = FindRequired(id);
            _options.BrowserSharing.Invites.Remove(invite);
            _config.Save(_options);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public WebAuthSession? Authenticate(string inviteId, string password, string viewerName)
    {
        WebInvite? candidate;
        lock (_gate)
        {
            candidate = _options.BrowserSharing.Invites.FirstOrDefault(x =>
                x.Id == inviteId && x.Enabled);
        }
        if (candidate is null || !WebInvitePassword.Verify(candidate, password))
            return null;

        viewerName = NormalizeViewerName(viewerName);
        lock (_gate)
        {
            var current = _options.BrowserSharing.Invites.FirstOrDefault(x =>
                x.Id == inviteId && x.Enabled &&
                x.CredentialVersion == candidate.CredentialVersion);
            if (current is null)
                return null;

            var session = new WebAuthSession(
                WebInvitePassword.CreateSessionId(),
                current.Id,
                current.CredentialVersion,
                viewerName,
                DateTimeOffset.UtcNow + AuthLifetime);
            _sessions[session.Id] = session;
            return session;
        }
    }

    public WebAuthSession? ValidateSession(string? sessionId, string? inviteId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !_sessions.TryGetValue(sessionId, out var session))
            return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow ||
            inviteId is not null && session.InviteId != inviteId)
        {
            _sessions.TryRemove(session.Id, out _);
            return null;
        }

        lock (_gate)
        {
            var invite = _options.BrowserSharing.Invites.FirstOrDefault(x =>
                x.Id == session.InviteId && x.Enabled &&
                x.CredentialVersion == session.CredentialVersion);
            if (invite is not null)
                return session;
        }
        _sessions.TryRemove(session.Id, out _);
        return null;
    }

    public void SignOut(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions.TryRemove(sessionId, out _);
    }

    private WebInvite FindRequired(string id) =>
        _options.BrowserSharing.Invites.FirstOrDefault(x => x.Id == id) ??
        throw new KeyNotFoundException("网页观战链接不存在。");

    private WebInviteView ToView(WebInvite invite) => new(
        invite.Id,
        invite.Name,
        invite.Enabled,
        invite.CreatedAt,
        BuildUrl(invite.Id));

    private string? BuildUrl(string id)
    {
        var host = _options.BrowserSharing.FunnelHostName?.Trim().TrimEnd('.');
        var port = _options.BrowserSharing.FunnelPort;
        if (string.IsNullOrWhiteSpace(host) || port is null)
            return null;
        var authority = port == 443 ? host : $"{host}:{port}";
        return $"https://{authority}/watch/{id}";
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 40)
            throw new ArgumentException("链接名称长度必须为 1–40 个字符。", nameof(name));
        return name;
    }

    private static string NormalizeViewerName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 24)
            throw new ArgumentException("观众昵称长度必须为 1–24 个字符。", nameof(name));
        return name;
    }
}
