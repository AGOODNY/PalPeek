using PalPeek.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace PalPeek;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FriendDiscovery _discovery;
    private readonly MoonlightLauncher _moonlight;
    private readonly ITailscaleService _tailscale;
    private readonly HostStateStore _hostState;
    private string _networkStatus = "正在检查 Tailscale…";
    private string _localStatus = "没有运行中的游戏";
    private string _bannerText = string.Empty;
    private Visibility _bannerVisibility = Visibility.Collapsed;
    private bool _watching;

    public MainWindow(
        FriendDiscovery discovery,
        MoonlightLauncher moonlight,
        ITailscaleService tailscale,
        HostStateStore hostState)
    {
        InitializeComponent();
        DataContext = this;
        _discovery = discovery;
        _moonlight = moonlight;
        _tailscale = tailscale;
        _hostState = hostState;
        _discovery.Changed += Discovery_Changed;
        _hostState.Changed += HostState_Changed;
        Loaded += async (_, _) => await RefreshNetworkAsync();
    }

    public ObservableCollection<FriendCard> Friends { get; } = new();
    public Visibility EmptyVisibility => Friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string NetworkStatus
    {
        get => _networkStatus;
        private set => Set(ref _networkStatus, value);
    }

    public string LocalStatus
    {
        get => _localStatus;
        private set => Set(ref _localStatus, value);
    }

    public string BannerText
    {
        get => _bannerText;
        private set => Set(ref _bannerText, value);
    }

    public Visibility BannerVisibility
    {
        get => _bannerVisibility;
        private set => Set(ref _bannerVisibility, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task RefreshNetworkAsync()
    {
        var snapshot = await _tailscale.GetSnapshotAsync();
        NetworkStatus = snapshot.Running
            ? $"Tailscale 已连接 · {snapshot.SelfIp}"
            : snapshot.Error ?? "Tailscale 未连接";
        if (!snapshot.Running)
            ShowBanner(snapshot.Error ?? "请安装并连接 Tailscale。");
    }

    private void Discovery_Changed(object? sender, IReadOnlyList<FriendStream> streams) =>
        Dispatcher.Invoke(() =>
        {
            var cards = streams.Select(x => new FriendCard(x)).ToArray();
            Friends.Clear();
            foreach (var card in cards)
                Friends.Add(card);
            OnPropertyChanged(nameof(EmptyVisibility));
        });

    private void HostState_Changed(object? sender, HostStatus status) =>
        Dispatcher.Invoke(() =>
        {
            LocalStatus = status.Game is null
                ? "没有运行中的游戏"
                : $"{status.Game.Name} · {status.ViewerCount}/{Protocol.MaxViewers} 人观看";
            if (status.CaptureState is CaptureState.HostUnavailable or CaptureState.AudioUnavailable)
                ShowBanner(status.Message ?? "捕获组件不可用。");
        });

    private async void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watching || sender is not System.Windows.Controls.Button { Tag: FriendCard card })
            return;
        await WatchAsync(card.Stream);
    }

    private async Task WatchAsync(FriendStream stream)
    {
        _watching = true;
        try
        {
            HideBanner();
            await _moonlight.WatchAsync(stream);
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message);
        }
        finally { _watching = false; }
    }

    public async Task OpenWatchUriAsync(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("palpeek", StringComparison.OrdinalIgnoreCase))
        {
            ShowBanner("观战链接无效。");
            return;
        }

        var node = uri.Host;
        var session = uri.AbsolutePath.Trim('/');
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var match = _discovery.Current.FirstOrDefault(x =>
                (x.Node.HostName.Equals(node, StringComparison.OrdinalIgnoreCase) ||
                 x.Node.DnsName.Equals(node, StringComparison.OrdinalIgnoreCase)) &&
                x.Status.Game?.SessionId == session);
            if (match is not null)
            {
                await WatchAsync(match);
                return;
            }
            await Task.Delay(750);
        }
        ShowBanner("好友不在线，或观战链接已经失效。");
    }

    private void ShowBanner(string message)
    {
        BannerText = message;
        BannerVisibility = Visibility.Visible;
    }

    private void HideBanner() => BannerVisibility = Visibility.Collapsed;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FriendCard
{
    public FriendCard(FriendStream stream) => Stream = stream;
    public FriendStream Stream { get; }
    public string Nickname => Stream.Status.Nickname;
    public string GameName => Stream.Status.Game?.Name ?? "游戏已结束";
    public string Detail =>
        $"在线 · {QualityText(Stream.Status.Quality)} · {Stream.Status.ViewerCount}/{Protocol.MaxViewers} 人观看 · {Stream.Node.HostName}";
    public bool CanWatch => Stream.Status.CanWatch;

    private static string QualityText(StreamQuality quality) =>
        quality == StreamQuality.P1080_60 ? "1080p60" : "720p60";
}
