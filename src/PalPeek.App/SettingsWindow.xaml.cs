using PalPeek.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PalPeek;

public partial class SettingsWindow : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly PalPeekOptions _options;
    private readonly ConfigStore _config;
    private readonly StartupManager _startup;
    private readonly SharingControl _sharing;
    private QualityOption _selectedQualityOption;
    private string _saveStatus = string.Empty;

    public SettingsWindow(
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
        Games = new ObservableCollection<GameSharingOption>(
            catalog.Apps
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(game => new GameSharingOption(
                    game.AppId,
                    game.Name,
                    _options.BlockedGameAppIds.Contains(game.AppId),
                    SetGameBlocked,
                    artwork.CreatePlaceholder(game.AppId, game.Name))));
        foreach (var game in Games)
            _ = game.LoadArtworkAsync(artwork);
        QualityOptions =
        [
            new QualityOption(
                StreamQuality.P720_30,
                "省流",
                "720p30 / 2 Mbps · 网络受限时更稳定"),
            new QualityOption(
                StreamQuality.P720_60,
                "流畅",
                "720p60 / 4 Mbps · 推荐默认值"),
            new QualityOption(
                StreamQuality.P1080_60,
                "清晰",
                "1080p60 / 8 Mbps · 需要更好的网络")
        ];
        _selectedQualityOption = QualityOptions.First(
            option => option.Value == _options.Quality);
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<GameSharingOption> Games { get; }
    public IReadOnlyList<QualityOption> QualityOptions { get; }
    public Visibility EmptyGamesVisibility =>
        Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool Invisible
    {
        get => _options.Invisible;
        set
        {
            if (_options.Invisible == value)
                return;
            _options.Invisible = value;
            SaveSharingSettings(value ? "隐身已开启。" : "隐身已关闭。");
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _options.StartWithWindows;
        set
        {
            if (_options.StartWithWindows == value)
                return;
            try
            {
                _startup.SetEnabled(value);
                _options.StartWithWindows = value;
                _config.Save(_options);
                SaveStatus = value ? "开机自启已开启。" : "开机自启已关闭。";
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"无法修改开机自启：{ex.Message}",
                    "PalPeek 设置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                OnPropertyChanged();
            }
        }
    }

    public QualityOption SelectedQualityOption
    {
        get => _selectedQualityOption;
        set
        {
            if (value is null || _selectedQualityOption == value)
                return;
            _selectedQualityOption = value;
            _options.Quality = value.Value;
            _config.Save(_options);
            SaveStatus = $"观战画质已设为“{value.Name}”，下次连接生效。";
            OnPropertyChanged();
        }
    }

    public bool IsQuality72030
    {
        get => SelectedQualityOption.Value == StreamQuality.P720_30;
        set
        {
            if (value)
                SelectQuality(StreamQuality.P720_30);
        }
    }

    public bool IsQuality72060
    {
        get => SelectedQualityOption.Value == StreamQuality.P720_60;
        set
        {
            if (value)
                SelectQuality(StreamQuality.P720_60);
        }
    }

    public bool IsQuality108060
    {
        get => SelectedQualityOption.Value == StreamQuality.P1080_60;
        set
        {
            if (value)
                SelectQuality(StreamQuality.P1080_60);
        }
    }

    public string SaveStatus
    {
        get => _saveStatus;
        private set
        {
            if (_saveStatus == value)
                return;
            _saveStatus = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? UninstallRequested;
    public event EventHandler? HelpRequested;

    private void SetGameBlocked(uint appId, bool blocked)
    {
        if (blocked)
            _options.BlockedGameAppIds.Add(appId);
        else
            _options.BlockedGameAppIds.Remove(appId);
        SaveSharingSettings(blocked ? "已禁止共享该游戏。" : "已允许共享该游戏。");
    }

    private void SaveSharingSettings(string message)
    {
        _config.Save(_options);
        _sharing.RefreshPolicy();
        SaveStatus = message;
    }

    private void FaqButton_Click(object sender, RoutedEventArgs e)
        => HelpRequested?.Invoke(this, EventArgs.Empty);

    private void UninstallButton_Click(object sender, RoutedEventArgs e) =>
        UninstallRequested?.Invoke(this, EventArgs.Empty);

    private void SelectQuality(StreamQuality quality)
    {
        SelectedQualityOption = QualityOptions.First(option => option.Value == quality);
        OnPropertyChanged(nameof(IsQuality72030));
        OnPropertyChanged(nameof(IsQuality72060));
        OnPropertyChanged(nameof(IsQuality108060));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record QualityOption(
    StreamQuality Value,
    string Name,
    string Detail);

public sealed class GameSharingOption : INotifyPropertyChanged
{
    private readonly Action<uint, bool> _changed;
    private bool _isBlocked;
    private ImageSource _artwork;

    public GameSharingOption(
        uint appId,
        string name,
        bool isBlocked,
        Action<uint, bool> changed,
        ImageSource artwork)
    {
        AppId = appId;
        Name = name;
        _isBlocked = isBlocked;
        _changed = changed;
        _artwork = artwork;
    }

    public uint AppId { get; }
    public string Name { get; }
    public string AppIdText => $"Steam App ID {AppId}";
    public ImageSource Artwork
    {
        get => _artwork;
        private set
        {
            if (ReferenceEquals(_artwork, value))
                return;
            _artwork = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Artwork)));
        }
    }

    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            if (_isBlocked == value)
                return;
            _isBlocked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBlocked)));
            _changed(AppId, value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadArtworkAsync(GameArtworkService artwork)
    {
        Artwork = await artwork.GetArtworkAsync(AppId, Name);
    }
}
