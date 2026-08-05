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

    public SettingsWindowFactory(
        PalPeekOptions options,
        ConfigStore config,
        StartupManager startup,
        SharingControl sharing,
        SteamCatalog catalog,
        GameArtworkService artwork)
    {
        _options = options;
        _config = config;
        _startup = startup;
        _sharing = sharing;
        _catalog = catalog;
        _artwork = artwork;
    }

    public SettingsWindow Create() =>
        new(
            _options,
            _config,
            _startup,
            _sharing,
            _catalog,
            _artwork);
}
