using PalPeek.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace PalPeek;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private readonly PalPeekOptions _options;
    private readonly ConfigStore _config;
    private readonly StartupManager _startup;
    private readonly SharingControl _sharing;
    private string _saveStatus = string.Empty;

    public SettingsWindow(
        PalPeekOptions options,
        ConfigStore config,
        StartupManager startup,
        SharingControl sharing,
        SteamCatalog catalog)
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
                    SetGameBlocked)));
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<GameSharingOption> Games { get; }
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
    {
        var window = new FaqWindow { Owner = this };
        window.ShowDialog();
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e) =>
        UninstallRequested?.Invoke(this, EventArgs.Empty);

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class GameSharingOption : INotifyPropertyChanged
{
    private readonly Action<uint, bool> _changed;
    private bool _isBlocked;

    public GameSharingOption(
        uint appId,
        string name,
        bool isBlocked,
        Action<uint, bool> changed)
    {
        AppId = appId;
        Name = name;
        _isBlocked = isBlocked;
        _changed = changed;
    }

    public uint AppId { get; }
    public string Name { get; }
    public string AppIdText => $"Steam App ID {AppId}";

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
}
