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
    private readonly SharingControl _sharing;
    private string _networkStatus = "正在检查 Tailscale…";
    private string _localStatus = "没有运行中的游戏";
    private string _shareButtonText = "等待游戏";
    private bool _canToggleShare;
    private HostStatus? _latestHostStatus;
    private string _bannerText = string.Empty;
    private Visibility _bannerVisibility = Visibility.Collapsed;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private FriendStream? _activeWatch;
    private bool _watchTransitioning;

    public MainWindow(
        FriendDiscovery discovery,
        MoonlightLauncher moonlight,
        ITailscaleService tailscale,
        HostStateStore hostState,
        SharingControl sharing)
    {
        InitializeComponent();
        DataContext = this;
        _discovery = discovery;
        _moonlight = moonlight;
        _tailscale = tailscale;
        _hostState = hostState;
        _sharing = sharing;
        _discovery.Changed += Discovery_Changed;
        _discovery.ProbeFailed += Discovery_ProbeFailed;
        _hostState.Changed += HostState_Changed;
        _sharing.Changed += Sharing_Changed;
        Loaded += async (_, _) => await RefreshNetworkAsync();
        Closed += (_, _) =>
        {
            _watchCancellation?.Cancel();
            _discovery.Changed -= Discovery_Changed;
            _discovery.ProbeFailed -= Discovery_ProbeFailed;
            _hostState.Changed -= HostState_Changed;
            _sharing.Changed -= Sharing_Changed;
        };
        UpdateSharingUi(_sharing.Get());
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
            var cards = streams.Select(x => new FriendCard(x, IsActiveWatch(x))).ToArray();
            Friends.Clear();
            foreach (var card in cards)
                Friends.Add(card);
            OnPropertyChanged(nameof(EmptyVisibility));
        });

    private void Discovery_ProbeFailed(object? sender, FriendDiscoveryError error) =>
        Dispatcher.Invoke(() =>
            ShowBanner($"{error.Node.HostName}：{error.Message}"));

    private void HostState_Changed(object? sender, HostStatus status) =>
        Dispatcher.Invoke(() =>
        {
            _latestHostStatus = status;
            UpdateSharingUi(_sharing.Get());
            if (status.CaptureState is CaptureState.HostUnavailable or CaptureState.AudioUnavailable)
                ShowBanner(status.Message ?? "捕获组件不可用。");
        });

    private void Sharing_Changed(object? sender, SharingSnapshot snapshot) =>
        Dispatcher.Invoke(() => UpdateSharingUi(snapshot));

    private void UpdateSharingUi(SharingSnapshot snapshot)
    {
        ShareButtonText = snapshot.DetectedGame is null
            ? "等待游戏"
            : snapshot.SharingEnabled ? "停止分享" : "恢复分享";
        CanToggleShare = snapshot.DetectedGame is not null;
        LocalStatus = snapshot.DetectedGame is null
            ? "没有运行中的游戏"
            : snapshot.SharingEnabled
                ? $"正在分享 {snapshot.DetectedGame.Name} · {_latestHostStatus?.ViewerCount ?? 0}/{Protocol.MaxViewers} 人观看"
                : $"已停止分享 {snapshot.DetectedGame.Name}";
    }

    private void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _sharing.Get();
        if (current.SharingEnabled)
        {
            _sharing.StopSharing();
            ShowBanner("已停止分享，正在结束观战会话。");
            return;
        }

        if (!_sharing.StartSharing())
        {
            ShowBanner("请先启动一个 Steam 游戏，并等待 PalPeek 检测到游戏窗口。");
            return;
        }
        ShowBanner("已恢复分享当前游戏。");
    }

    private async void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watchTransitioning ||
            sender is not System.Windows.Controls.Button { Tag: FriendCard card })
            return;
        await RestartWatchAsync(card.Stream);
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e) =>
        UninstallRequested?.Invoke(this, EventArgs.Empty);

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
            await _moonlight.WatchAsync(stream, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message);
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
        cancellation.Dispose();
    }

    private bool IsActiveWatch(FriendStream stream) =>
        _activeWatch is { } active &&
        active.Node.Id.Equals(stream.Node.Id, StringComparison.OrdinalIgnoreCase) &&
        active.Status.Game?.SessionId == stream.Status.Game?.SessionId;

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
                await RestartWatchAsync(match);
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
    private readonly bool _canResume;

    public FriendCard(FriendStream stream, bool canResume = false)
    {
        Stream = stream;
        _canResume = canResume;
    }

    public FriendStream Stream { get; }
    public string Nickname => Stream.Status.Nickname;
    public string GameName => Stream.Status.Game?.Name ?? "游戏已结束";
    public string WatchButtonText => _canResume ? $"重新进入 {GameName}" : $"观看 {GameName}";
    public string Detail =>
        $"在线 · {QualityText(Stream.Status.Quality)} · {Stream.Status.ViewerCount}/{Protocol.MaxViewers} 人观看 · " +
        $"{Stream.Node.HostName} · v{Stream.Status.Version}" +
        (Stream.Status.CanWatch || string.IsNullOrWhiteSpace(Stream.Status.Message)
            ? string.Empty
            : $" · 准备中：{Stream.Status.Message}");
    public bool CanWatch => Stream.Status.CanWatch || _canResume;

    private static string QualityText(StreamQuality quality) =>
        quality == StreamQuality.P1080_60 ? "1080p60" : "720p60";
}
