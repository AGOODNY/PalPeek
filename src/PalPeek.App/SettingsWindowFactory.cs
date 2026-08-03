using PalPeek.Core;

namespace PalPeek;

public sealed class SettingsWindowFactory
{
    private readonly PalPeekOptions _options;
    private readonly ConfigStore _config;
    private readonly StartupManager _startup;
    private readonly SharingControl _sharing;
    private readonly SteamCatalog _catalog;
    private readonly GameArtworkService _artwork;
    private readonly WebInviteService _webInvites;
    private readonly FunnelManager _funnel;
    private readonly HostStateStore _hostState;

    public SettingsWindowFactory(
        PalPeekOptions options,
        ConfigStore config,
        StartupManager startup,
        SharingControl sharing,
        SteamCatalog catalog,
        GameArtworkService artwork,
        WebInviteService webInvites,
        FunnelManager funnel,
        HostStateStore hostState)
    {
        _options = options;
        _config = config;
        _startup = startup;
        _sharing = sharing;
        _catalog = catalog;
        _artwork = artwork;
        _webInvites = webInvites;
        _funnel = funnel;
        _hostState = hostState;
    }

    public SettingsWindow Create() =>
        new(
            _options,
            _config,
            _startup,
            _sharing,
            _catalog,
            _artwork,
            _webInvites,
            _funnel,
            _hostState);
}
