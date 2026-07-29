using PalPeek.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PalPeek;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FriendDiscovery _discovery;
    private readonly MoonlightLauncher _moonlight;
    private readonly ITailscaleService _tailscale;
    private readonly HostStateStore _hostState;
    private readonly SharingControl _sharing;
    private readonly GameArtworkService _artwork;
    private readonly DiagnosticsWindow _diagnosticsPage;
    private string _networkStatus = "正在检查 Tailscale…";
    private bool _isNetworkLoading = true;
    private string _localStatus = "没有运行中的游戏";
    private string _shareButtonText = "等待游戏";
    private bool _canToggleShare;
    private bool _isShareLoading;
    private HostStatus? _latestHostStatus;
    private string _bannerText = string.Empty;
    private Visibility _bannerVisibility = Visibility.Collapsed;
    private BannerKind _bannerKind;
    private CancellationTokenSource? _bannerAutoHide;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private FriendStream? _activeWatch;
    private WatchStage? _watchStage;
    private bool _watchTransitioning;
    private string _localGameName = "等待 Steam 游戏";
    private string _localShareState = "未检测到游戏";
    private string _localViewerText = $"0 / {Protocol.MaxViewers} 人观看";
    private double _localViewerPercent;
    private string _localQualityText = "等待串流参数";
    private ImageSource _localArtwork;
    private uint? _localArtworkAppId;

    public MainWindow(
        FriendDiscovery discovery,
        MoonlightLauncher moonlight,
        ITailscaleService tailscale,
        HostStateStore hostState,
        SharingControl sharing,
        SettingsWindowFactory settingsFactory,
        DiagnosticsWindowFactory diagnosticsFactory,
        GameArtworkService artwork)
    {
        _artwork = artwork;
        _localArtwork = artwork.CreatePlaceholder(0, "PALPEEK");
        InitializeComponent();
        DataContext = this;
        _discovery = discovery;
        _moonlight = moonlight;
        _tailscale = tailscale;
        _hostState = hostState;
        _sharing = sharing;
        var settingsPage = settingsFactory.Create();
        settingsPage.UninstallRequested += (_, _) =>
            UninstallRequested?.Invoke(this, EventArgs.Empty);
        settingsPage.HelpRequested += (_, _) => OpenHelp();
        SettingsPage.Content = settingsPage;
        _diagnosticsPage = diagnosticsFactory.Create();
        DiagnosticsPage.Content = _diagnosticsPage;
        HelpPage.Content = new FaqWindow();
        HallNav.IsChecked = true;
        _discovery.Changed += Discovery_Changed;
        _discovery.ProbeFailed += Discovery_ProbeFailed;
        _tailscale.SnapshotChanged += Tailscale_SnapshotChanged;
        _hostState.Changed += HostState_Changed;
        _sharing.Changed += Sharing_Changed;
        Loaded += async (_, _) => await RefreshNetworkAsync();
        Closed += (_, _) =>
        {
            _watchCancellation?.Cancel();
            _bannerAutoHide?.Cancel();
            _discovery.Changed -= Discovery_Changed;
            _discovery.ProbeFailed -= Discovery_ProbeFailed;
            _tailscale.SnapshotChanged -= Tailscale_SnapshotChanged;
            _hostState.Changed -= HostState_Changed;
            _sharing.Changed -= Sharing_Changed;
        };
        StateChanged += (_, _) =>
            MaximizeButton.Content = WindowState == WindowState.Maximized
                ? "\uE923"
                : "\uE922";
        UpdateSharingUi(_sharing.Get());
    }

    public ObservableCollection<FriendCard> Friends { get; } = new();
    public Visibility EmptyVisibility => Friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string LocalGameName
    {
        get => _localGameName;
        private set => Set(ref _localGameName, value);
    }

    public string LocalShareState
    {
        get => _localShareState;
        private set => Set(ref _localShareState, value);
    }

    public string LocalViewerText
    {
        get => _localViewerText;
        private set => Set(ref _localViewerText, value);
    }

    public double LocalViewerPercent
    {
        get => _localViewerPercent;
        private set => Set(ref _localViewerPercent, value);
    }

    public string LocalQualityText
    {
        get => _localQualityText;
        private set => Set(ref _localQualityText, value);
    }

    public ImageSource LocalArtwork
    {
        get => _localArtwork;
        private set => Set(ref _localArtwork, value);
    }

    public string NetworkStatus
    {
        get => _networkStatus;
        private set => Set(ref _networkStatus, value);
    }

    public bool IsNetworkLoading
    {
        get => _isNetworkLoading;
        private set => Set(ref _isNetworkLoading, value);
    }

    public string LocalStatus
    {
        get => _localStatus;
        private set => Set(ref _localStatus, value);
    }

    public string ShareButtonText
    {
        get => _shareButtonText;
        private set => Set(ref _shareButtonText, value);
    }

    public bool CanToggleShare
    {
        get => _canToggleShare;
        private set => Set(ref _canToggleShare, value);
    }

    public bool IsShareLoading
    {
        get => _isShareLoading;
        private set => Set(ref _isShareLoading, value);
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
    public event EventHandler? UninstallRequested;

    private async Task RefreshNetworkAsync()
    {
        IsNetworkLoading = true;
        try
        {
            var snapshot = await _tailscale.GetSnapshotAsync();
            ApplyNetworkSnapshot(snapshot);
        }
        finally
        {
            IsNetworkLoading = false;
        }
    }

    private void Tailscale_SnapshotChanged(object? sender, TailscaleSnapshot snapshot) =>
        Dispatcher.Invoke(() => ApplyNetworkSnapshot(snapshot));

    private void ApplyNetworkSnapshot(TailscaleSnapshot snapshot)
    {
        NetworkStatus = snapshot.Running
            ? $"Tailscale 已连接 · {snapshot.SelfIp}"
            : snapshot.Error ?? "Tailscale 未连接";
        if (snapshot.Running)
            HideBanner(BannerKind.Network);
        else
            ShowBanner(
                snapshot.Error ?? "请安装并连接 Tailscale。",
                BannerKind.Network);
    }

    private void Discovery_Changed(object? sender, IReadOnlyList<FriendStream> streams) =>
        Dispatcher.Invoke(() =>
        {
            RefreshFriendCards(streams);
            if (streams.Count > 0)
                HideBanner(BannerKind.Discovery);
        });

    private void Discovery_ProbeFailed(object? sender, FriendDiscoveryError error) =>
        Dispatcher.Invoke(() =>
            ShowBanner(
                $"{error.Node.HostName}：{error.Message}",
                BannerKind.Discovery,
                TimeSpan.FromSeconds(12)));

    private void HostState_Changed(object? sender, HostStatus status) =>
        Dispatcher.Invoke(() =>
        {
            _latestHostStatus = status;
            UpdateSharingUi(_sharing.Get());
            if (status.CaptureState is CaptureState.HostUnavailable or CaptureState.AudioUnavailable)
                ShowBanner(status.Message ?? "捕获组件不可用。", BannerKind.Host);
            else
                HideBanner(BannerKind.Host);
        });

    private void Sharing_Changed(object? sender, SharingSnapshot snapshot) =>
        Dispatcher.Invoke(() => UpdateSharingUi(snapshot));

    private void UpdateSharingUi(SharingSnapshot snapshot)
    {
        var game = snapshot.DetectedGame;
        var viewerCount = _latestHostStatus?.ViewerCount ?? 0;
        ShareButtonText = snapshot.DetectedGame is null
            ? "等待游戏"
            : snapshot.BlockReason switch
            {
                SharingBlockReason.Invisible => "隐身中",
                SharingBlockReason.GameDisabled => "已禁用此游戏",
                SharingBlockReason.ManuallyStopped => "恢复分享",
                _ => "停止分享"
            };
        CanToggleShare = snapshot.DetectedGame is not null &&
                         snapshot.BlockReason is SharingBlockReason.None or
                             SharingBlockReason.ManuallyStopped;
        LocalStatus = snapshot.DetectedGame is null
            ? "没有运行中的游戏"
            : snapshot.BlockReason switch
            {
                SharingBlockReason.Invisible => $"隐身中 · {snapshot.DetectedGame.Name} 不会显示给好友",
                SharingBlockReason.GameDisabled => $"已始终禁止共享 {snapshot.DetectedGame.Name}",
                SharingBlockReason.ManuallyStopped => $"已停止分享 {snapshot.DetectedGame.Name}",
                _ => $"正在分享 {snapshot.DetectedGame.Name} · {_latestHostStatus?.ViewerCount ?? 0}/{Protocol.MaxViewers} 人观看"
            };
        LocalGameName = game?.Name ?? "等待 Steam 游戏";
        LocalShareState = game is null
            ? "未检测到游戏"
            : snapshot.BlockReason switch
            {
                SharingBlockReason.Invisible => "隐身中",
                SharingBlockReason.GameDisabled => "已禁止共享",
                SharingBlockReason.ManuallyStopped => "分享已暂停",
                _ => "正在共享"
            };
        LocalViewerText = $"{viewerCount} / {Protocol.MaxViewers} 人观看";
        LocalViewerPercent = viewerCount * 100d / Protocol.MaxViewers;
        LocalQualityText = _latestHostStatus?.Quality switch
        {
            StreamQuality.P720_30 => "720P / 30FPS / 2 MBPS",
            StreamQuality.P720_60 => "720P / 60FPS / 4 MBPS",
            StreamQuality.P1080_60 => "1080P / 60FPS / 8 MBPS",
            _ => "等待串流参数"
        };
        if (game is null)
        {
            _localArtworkAppId = null;
            LocalArtwork = _artwork.CreatePlaceholder(0, "PALPEEK");
        }
        else if (_localArtworkAppId != game.AppId)
        {
            _localArtworkAppId = game.AppId;
            LocalArtwork = _artwork.CreatePlaceholder(game.AppId, game.Name);
            _ = LoadLocalArtworkAsync(game.AppId, game.Name);
        }
        IsShareLoading = snapshot.SharingEnabled &&
                         (_latestHostStatus?.Game?.SessionId != snapshot.DetectedGame?.SessionId ||
                          _latestHostStatus?.CaptureState == CaptureState.Stabilizing);
    }

    private async Task LoadLocalArtworkAsync(uint appId, string name)
    {
        var image = await _artwork.GetArtworkAsync(appId, name);
        if (_localArtworkAppId == appId)
            LocalArtwork = image;
    }

    private void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _sharing.Get();
        if (current.SharingEnabled)
        {
            _sharing.StopSharing();
            ShowBanner(
                "已停止分享，正在结束观战会话。",
                BannerKind.Status,
                TimeSpan.FromSeconds(5));
            return;
        }

        if (!_sharing.StartSharing())
        {
            ShowBanner(
                "请先启动一个 Steam 游戏，并等待 PalPeek 检测到游戏窗口。",
                BannerKind.Status,
                TimeSpan.FromSeconds(8));
            return;
        }
        ShowBanner(
            "已恢复分享当前游戏。",
            BannerKind.Status,
            TimeSpan.FromSeconds(5));
    }

    private async void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watchTransitioning ||
            sender is not System.Windows.Controls.Button { Tag: FriendCard card })
            return;
        await RestartWatchAsync(card.Stream);
    }

    private void HallNav_Checked(object sender, RoutedEventArgs e) =>
        NavigateTo(AppPage.Hall);

    private void SettingsNav_Checked(object sender, RoutedEventArgs e) =>
        NavigateTo(AppPage.Settings);

    private async void DiagnosticsNav_Checked(object sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.Diagnostics);
        await _diagnosticsPage.RefreshIfNeededAsync();
    }

    private void HelpNav_Checked(object sender, RoutedEventArgs e) =>
        NavigateTo(AppPage.Help);

    private void NavigateTo(AppPage page)
    {
        if (HallPage is null ||
            SettingsPage is null ||
            DiagnosticsPage is null ||
            HelpPage is null)
        {
            return;
        }
        HallPage.Visibility = page == AppPage.Hall ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == AppPage.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = page == AppPage.Help ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAndActivate()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    public void OpenSettings()
    {
        ShowAndActivate();
        SettingsNav.IsChecked = true;
    }

    public void OpenDiagnostics()
    {
        ShowAndActivate();
        DiagnosticsNav.IsChecked = true;
    }

    public void OpenHelp()
    {
        ShowAndActivate();
        HelpNav.IsChecked = true;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private async Task RestartWatchAsync(FriendStream stream)
    {
        if (_watchTransitioning)
            return;

        _watchTransitioning = true;
        try
        {
            await StopWatchAsync();
            StartWatch(stream);
        }
        finally
        {
            _watchTransitioning = false;
        }
    }

    private void StartWatch(FriendStream stream)
    {
        var cancellation = new CancellationTokenSource();
        _watchCancellation = cancellation;
        _activeWatch = stream;
        _watchStage = WatchStage.Reserving;
        RefreshFriendCards(_discovery.Current);
        var task = RunWatchAsync(stream, cancellation.Token);
        _watchTask = task;
        _ = ObserveWatchCompletionAsync(task, cancellation);
    }

    private async Task StopWatchAsync()
    {
        var cancellation = _watchCancellation;
        var task = _watchTask;
        if (cancellation is null || task is null)
            return;

        cancellation.Cancel();
        await task;
        ClearWatch(task, cancellation);
    }

    private async Task RunWatchAsync(FriendStream stream, CancellationToken cancellationToken)
    {
        try
        {
            HideBanner();
            var progress = new Progress<WatchProgress>(
                update => WatchProgress_Changed(stream, update));
            await _moonlight.WatchAsync(stream, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowBanner(
                ex.Message,
                BannerKind.Watch,
                TimeSpan.FromSeconds(15));
        }
    }

    private async Task ObserveWatchCompletionAsync(
        Task task,
        CancellationTokenSource cancellation)
    {
        await task;
        ClearWatch(task, cancellation);
    }

    private void ClearWatch(Task task, CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_watchTask, task))
            return;

        _watchTask = null;
        _watchCancellation = null;
        _activeWatch = null;
        _watchStage = null;
        cancellation.Dispose();
        RefreshFriendCards(_discovery.Current);
    }

    private bool IsActiveWatch(FriendStream stream) =>
        _activeWatch is { } active &&
        IsSameStream(active, stream);

    private bool IsConnectingWatch(FriendStream stream) =>
        IsActiveWatch(stream) && _watchStage is not null and not WatchStage.Streaming;

    private void WatchProgress_Changed(FriendStream stream, WatchProgress progress)
    {
        if (_activeWatch is null || !IsSameStream(_activeWatch, stream))
            return;
        _watchStage = progress.Stage;
        ShowBanner(
            progress.Stage == WatchStage.Streaming
                ? "播放器已启动。关闭播放器即可结束观看。"
                : progress.Message,
            BannerKind.Watch,
            progress.Stage == WatchStage.Streaming
                ? TimeSpan.FromSeconds(5)
                : null);
        RefreshFriendCards(_discovery.Current);
    }

    private void RefreshFriendCards(IReadOnlyList<FriendStream> streams)
    {
        var cards = streams
            .Select(stream => new FriendCard(
                stream,
                _artwork,
                IsActiveWatch(stream),
                IsConnectingWatch(stream)))
            .ToArray();
        Friends.Clear();
        foreach (var card in cards)
            Friends.Add(card);
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private static bool IsSameStream(FriendStream left, FriendStream right) =>
        left.Node.Id.Equals(right.Node.Id, StringComparison.OrdinalIgnoreCase) &&
        left.Status.Game?.SessionId == right.Status.Game?.SessionId;

    public async Task OpenWatchUriAsync(string uriText)
    {
        ShowAndActivate();
        HallNav.IsChecked = true;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("palpeek", StringComparison.OrdinalIgnoreCase))
        {
            ShowBanner(
                "观战链接无效。",
                BannerKind.Status,
                TimeSpan.FromSeconds(10));
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
                await RestartWatchAsync(match);
                return;
            }
            await Task.Delay(750);
        }
        ShowBanner(
            "好友不在线，或观战链接已经失效。",
            BannerKind.Status,
            TimeSpan.FromSeconds(10));
    }

    private void ShowBanner(
        string message,
        BannerKind kind = BannerKind.Status,
        TimeSpan? autoHideAfter = null)
    {
        _bannerAutoHide?.Cancel();
        _bannerAutoHide?.Dispose();
        _bannerAutoHide = null;
        _bannerKind = kind;
        BannerText = message;
        BannerVisibility = Visibility.Visible;
        if (autoHideAfter is null)
            return;

        var cancellation = new CancellationTokenSource();
        _bannerAutoHide = cancellation;
        _ = HideBannerAfterAsync(kind, autoHideAfter.Value, cancellation);
    }

    private async Task HideBannerAfterAsync(
        BannerKind kind,
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            await Dispatcher.InvokeAsync(() => HideBanner(kind));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HideBanner(BannerKind? kind = null)
    {
        if (kind is not null && _bannerKind != kind)
            return;
        _bannerAutoHide?.Cancel();
        _bannerAutoHide?.Dispose();
        _bannerAutoHide = null;
        BannerVisibility = Visibility.Collapsed;
    }

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

internal enum AppPage
{
    Hall,
    Settings,
    Diagnostics,
    Help
}

internal enum BannerKind
{
    Status,
    Network,
    Discovery,
    Host,
    Watch
}

public sealed class FriendCard : INotifyPropertyChanged
{
    private readonly bool _isActive;
    private ImageSource _artwork;

    public FriendCard(
        FriendStream stream,
        GameArtworkService artwork,
        bool isActive = false,
        bool isConnecting = false)
    {
        Stream = stream;
        _isActive = isActive;
        IsConnecting = isConnecting;
        var game = stream.Status.Game;
        _artwork = artwork.CreatePlaceholder(game?.AppId ?? 0, game?.Name ?? "PALPEEK");
        _ = LoadArtworkAsync(artwork);
    }

    public FriendStream Stream { get; }
    public string Nickname => Stream.Status.Nickname;
    public string GameName => Stream.Status.Game?.Name ?? "游戏已结束";
    public string WatchButtonText => IsConnecting
        ? "正在连接…"
        : _isActive
            ? "正在观看"
            : "立即观战";
    public string QualityText => QualityLabel(Stream.Status.Quality);
    public string ViewerText => $"{Stream.Status.ViewerCount}/{Protocol.MaxViewers} 人观看";
    public string HostText =>
        $"{Stream.Node.HostName} · PalPeek v{Stream.Status.Version}" +
        (Stream.Status.CanWatch || string.IsNullOrWhiteSpace(Stream.Status.Message)
            ? string.Empty
            : $" · {Stream.Status.Message}");
    public string Detail =>
        $"在线 · {QualityLabel(Stream.Status.Quality)} · {Stream.Status.ViewerCount}/{Protocol.MaxViewers} 人观看 · " +
        $"{Stream.Node.HostName} · v{Stream.Status.Version}" +
        (Stream.Status.CanWatch || string.IsNullOrWhiteSpace(Stream.Status.Message)
            ? string.Empty
            : $" · 准备中：{Stream.Status.Message}");
    public bool CanWatch => Stream.Status.CanWatch && !_isActive;
    public bool IsConnecting { get; }
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task LoadArtworkAsync(GameArtworkService artwork)
    {
        var game = Stream.Status.Game;
        if (game is not null)
            Artwork = await artwork.GetArtworkAsync(game.AppId, game.Name);
    }

    private static string QualityLabel(StreamQuality quality) =>
        quality switch
        {
            StreamQuality.P720_30 => "720P 30FPS",
            StreamQuality.P720_60 => "720P 60FPS",
            StreamQuality.P1080_60 => "1080P 60FPS",
            _ => "未知画质"
        };
}
