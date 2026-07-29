using PalPeek.Core;

namespace PalPeek;

public enum DiagnosticLevel
{
    Success,
    Failure,
    Notice,
    Waiting
}

public sealed record DiagnosticItem(
    string Name,
    DiagnosticLevel Level,
    string Summary,
    string Action)
{
    public string StatusText => Level switch
    {
        DiagnosticLevel.Success => "正常",
        DiagnosticLevel.Failure => "异常",
        DiagnosticLevel.Notice => "提示",
        _ => "等待"
    };

    public string StatusColor => Level switch
    {
        DiagnosticLevel.Success => "#2C8C68",
        DiagnosticLevel.Failure => "#C94F5D",
        DiagnosticLevel.Notice => "#B7791F",
        _ => "#697386"
    };

    public string ActionText =>
        string.IsNullOrWhiteSpace(Action) ? string.Empty : $"建议：{Action}";
}

public sealed record DiagnosticReport(
    IReadOnlyList<DiagnosticItem> Items,
    DateTimeOffset GeneratedAt);

public sealed class DiagnosticsService
{
    private readonly ITailscaleService _tailscale;
    private readonly FriendDiscovery _friends;
    private readonly HostStateStore _hostState;
    private readonly SharingControl _sharing;
    private readonly SunshineBridge _sunshine;
    private readonly MoonlightLauncher _moonlight;

    public DiagnosticsService(
        ITailscaleService tailscale,
        FriendDiscovery friends,
        HostStateStore hostState,
        SharingControl sharing,
        SunshineBridge sunshine,
        MoonlightLauncher moonlight)
    {
        _tailscale = tailscale;
        _friends = friends;
        _hostState = hostState;
        _sharing = sharing;
        _sunshine = sunshine;
        _moonlight = moonlight;
    }

    public async Task<DiagnosticReport> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var tailnet = await _tailscale.GetSnapshotAsync(cancellationToken);
        var friends = _friends.Diagnostics;
        var host = _hostState.Get();
        var sharing = _sharing.Get();
        var watch = _moonlight.Diagnostics;
        SunshineRuntimeStatus? runtime = null;
        string? runtimeError = null;

        if (_sunshine.IsRunning)
        {
            try
            {
                runtime = await _sunshine.GetStatusAsync(cancellationToken);
            }
            catch (Exception ex) when (
                ex is IOException or TimeoutException or
                InvalidOperationException or SunshineProtocolException)
            {
                runtimeError = ex.Message;
            }
        }

        var items = new List<DiagnosticItem>
        {
            TailscaleItem(tailnet),
            FriendDiscoveryItem(tailnet, friends),
            PeerApiItem(tailnet, friends),
            ReservationItem(watch),
            SunshineItem(sharing, runtimeError),
            GameWindowItem(sharing, host, runtime),
            GameAudioItem(sharing, host, runtime),
            EncoderItem(sharing, runtime, runtimeError),
            PairingItem(watch),
            PlayerItem(watch)
        };

        return new DiagnosticReport(items, DateTimeOffset.Now);
    }

    private static DiagnosticItem TailscaleItem(TailscaleSnapshot snapshot) =>
        snapshot.Running
            ? Success(
                "Tailscale",
                $"已连接，当前地址为 {snapshot.SelfIp}。",
                "保持 Tailscale 在后台运行。")
            : Failure(
                "Tailscale",
                snapshot.Error ?? "Tailscale 未连接。",
                "安装或打开 Tailscale，登录后确认状态为“已连接”。");

    private static DiagnosticItem FriendDiscoveryItem(
        TailscaleSnapshot tailnet,
        FriendDiscoveryDiagnostics diagnostics)
    {
        if (!tailnet.Running)
            return Waiting(
                "好友发现",
                "正在等待 Tailscale 连接。",
                "先完成上一项 Tailscale 检查。");
        if (diagnostics.TailnetPeerCount == 0)
            return Waiting(
                "好友发现",
                "Tailscale 已连接，但没有发现其他 PalPeek 设备。",
                "确认好友在线、位于同一 Tailnet，并已启动 PalPeek。");
        return Success(
            "好友发现",
            $"已发现 {diagnostics.TailnetPeerCount} 台 Tailnet 设备。",
            "如果目标好友未出现，请检查 Tailnet ACL 和设备在线状态。");
    }

    private static DiagnosticItem PeerApiItem(
        TailscaleSnapshot tailnet,
        FriendDiscoveryDiagnostics diagnostics)
    {
        if (!tailnet.Running || diagnostics.TailnetPeerCount == 0)
            return Waiting(
                "PalPeek API",
                $"正在等待可检测的 TCP {Protocol.ApiPort} 服务。",
                "先确保双方设备在线并启动 PalPeek。");
        if (diagnostics.ReachableApiCount == 0)
            return Notice(
                "PalPeek API",
                $"{diagnostics.TailnetPeerCount} 台 Tailnet 设备当前均无法访问 PalPeek API，可能是设备关机、离线或未启动 PalPeek。",
                $"如果目标设备确认在线且已启动 PalPeek，再检查防火墙、Tailnet ACL 和 TCP {Protocol.ApiPort}。");

        var detail = diagnostics.FailedApiCount == 0
            ? $"已连接 {diagnostics.ReachableApiCount} 台 PalPeek 设备。"
            : $"已连接 {diagnostics.ReachableApiCount} 台，另有 {diagnostics.FailedApiCount} 台无法访问。";
        return diagnostics.FailedApiCount == 0
            ? Success("PalPeek API", detail, "API 状态正常。")
            : Notice(
                "PalPeek API",
                detail,
                $"这些设备可能已关机、离线或未启动 PalPeek；确认在线后仍无法连接，再检查防火墙和 TCP {Protocol.ApiPort}。");
    }

    private static DiagnosticItem ReservationItem(WatchDiagnosticsSnapshot watch)
    {
        if (watch.ReservationSucceeded)
            return Success(
                "观看名额",
                "最近一次观战已成功取得观看名额。",
                "关闭播放器后名额会自动释放。");
        if (watch.LastError is not null &&
            watch.CurrentStage == WatchStage.Reserving)
            return Failure(
                "观看名额",
                watch.LastError,
                "确认好友仍在分享且观看人数未满，然后重新点击观看。");
        return Waiting(
            "观看名额",
            "尚未申请观看名额。",
            "在好友卡片上点击观看后，这里会显示结果。");
    }

    private DiagnosticItem SunshineItem(
        SharingSnapshot sharing,
        string? runtimeError)
    {
        if (!_sunshine.IsInstalled)
            return Failure(
                "Sunshine",
                "未找到内置 Sunshine Host。",
                "重新安装完整的 PalPeek 安装包，不要只复制 PalPeek.exe。");
        if (_sunshine.Lifecycle.State is
            SunshineProcessState.Faulted or SunshineProcessState.NotInstalled)
            return Failure(
                "Sunshine",
                _sunshine.Lifecycle.Message ?? "Sunshine Host 不可用。",
                "停止并恢复分享；若仍失败，请重新安装 PalPeek。");
        if (runtimeError is not null)
            return Failure(
                "Sunshine",
                runtimeError,
                "停止并恢复分享，让 PalPeek 自动重启 Sunshine Host。");
        if (_sunshine.IsRunning)
            return Success(
                "Sunshine",
                $"Sunshine Host 正在运行（PID {_sunshine.Lifecycle.ProcessId}）。",
                "Sunshine 由 PalPeek 自动管理，无需单独打开。");
        return Waiting(
            "Sunshine",
            sharing.DetectedGame is null
                ? "尚未启动 Sunshine；检测到可分享游戏后会自动启动。"
                : "正在等待 Sunshine Host 启动。",
            "通过 Steam 启动游戏，并等待窗口检测完成。");
    }

    private static DiagnosticItem GameWindowItem(
        SharingSnapshot sharing,
        HostStatus host,
        SunshineRuntimeStatus? runtime)
    {
        if (sharing.DetectedGame is not null &&
            host.CaptureState != CaptureState.WindowUnavailable &&
            runtime?.Capture != SunshineCaptureStatus.Error)
            return Success(
                "游戏窗口",
                $"已锁定《{sharing.DetectedGame.Name}》的可见游戏窗口。",
                "请保持游戏窗口存在，不要关闭窗口。");

        if (sharing.DetectionMessage?.Contains(
                "权限",
                StringComparison.Ordinal) == true)
            return Failure(
                "游戏窗口",
                "游戏以管理员身份运行，PalPeek 权限不足。",
                "关闭游戏与 PalPeek，然后以相同权限重新启动。");

        if (runtime?.Capture == SunshineCaptureStatus.Error)
            return Failure(
                "游戏窗口",
                runtime.Message ?? "Sunshine 无法捕获已锁定的游戏窗口。",
                "恢复游戏主窗口并切到前台，然后停止并恢复分享。");

        if (host.CaptureState == CaptureState.WindowUnavailable ||
            sharing.DetectionMessage?.Contains(
                "等待可捕获窗口",
                StringComparison.Ordinal) == true)
            return Notice(
                "游戏窗口",
                "检测到 Steam 进程，但暂未找到大于 320×200 的可见游戏窗口。",
                "Wallpaper Engine 等 Steam 辅助程序可能没有可分享窗口；如要观战，请启动或恢复真正的游戏主窗口。");

        return Waiting(
            "游戏窗口",
            "正在等待 Steam 游戏和可见主窗口。",
            "通过 Steam 启动游戏，并等待约 5–10 秒。");
    }

    private static DiagnosticItem GameAudioItem(
        SharingSnapshot sharing,
        HostStatus host,
        SunshineRuntimeStatus? runtime)
    {
        if (sharing.DetectedGame is null)
            return Waiting(
                "游戏音频",
                "正在等待游戏窗口。",
                "游戏窗口锁定后会自动检查进程树音频。");
        if (host.CaptureState == CaptureState.AudioUnavailable ||
            runtime?.Audio == SunshineAudioStatus.Error)
            return Failure(
                "游戏音频",
                runtime?.Message ?? "无法获得目标游戏的进程音频。",
                "确认游戏正在播放声音；停止并恢复分享后重试。");
        if (runtime?.Audio is SunshineAudioStatus.Ready or SunshineAudioStatus.Capturing)
            return Success(
                "游戏音频",
                "游戏进程树音频已就绪。",
                "PalPeek 不会采集其他应用的系统声音。");
        return Waiting(
            "游戏音频",
            "正在初始化游戏进程树音频。",
            "让游戏播放声音，并等待数秒后刷新。");
    }

    private static DiagnosticItem EncoderItem(
        SharingSnapshot sharing,
        SunshineRuntimeStatus? runtime,
        string? runtimeError)
    {
        if (sharing.DetectedGame is null)
            return Waiting(
                "编码器",
                "正在等待游戏窗口。",
                "游戏窗口锁定后会自动探测 H.264 编码器。");
        if (runtimeError is not null ||
            runtime?.Encoding == SunshineEncodingStatus.Error)
            return Failure(
                "编码器",
                runtime?.Message ?? runtimeError ?? "编码器不支持当前配置。",
                "更新显卡驱动，关闭占用硬件编码器的程序，并尝试较低画质。");
        if (runtime?.Encoding is
            SunshineEncodingStatus.Ready or SunshineEncodingStatus.Streaming)
            return Success(
                "编码器",
                runtime.Encoding == SunshineEncodingStatus.Streaming
                    ? "H.264 编码器正在串流。"
                    : "H.264 编码器已就绪。",
                "编码器状态正常。");
        return Waiting(
            "编码器",
            "正在探测可用的 H.264 编码器。",
            "等待数秒后刷新；如持续等待，请更新显卡驱动。");
    }

    private static DiagnosticItem PairingItem(WatchDiagnosticsSnapshot watch)
    {
        if (watch.PairingSucceeded)
            return Success(
                "Moonlight 配对",
                "最近一次观战已完成 Moonlight 配对。",
                "配对凭据保存在当前用户的 PalPeek 配置目录中。");
        if (watch.LastError is not null &&
            watch.ReservationSucceeded &&
            watch.CurrentStage == WatchStage.Pairing)
            return Failure(
                "Moonlight 配对",
                watch.LastError,
                "确认主播仍在分享，并重新点击观看以自动配对。");
        return Waiting(
            "Moonlight 配对",
            "尚未开始 Moonlight 配对。",
            "取得观看名额后 PalPeek 会自动完成配对。");
    }

    private DiagnosticItem PlayerItem(WatchDiagnosticsSnapshot watch)
    {
        if (!_moonlight.IsInstalled)
            return Failure(
                "播放器",
                "未找到内置 Moonlight 播放器。",
                "重新安装完整的 PalPeek 安装包。");
        if (watch.PlayerStarted)
            return Success(
                "播放器",
                watch.IsStreaming
                    ? "Moonlight 播放器正在运行。"
                    : "最近一次 Moonlight 播放器已成功启动。",
                "关闭播放器即可退出观战并释放名额。");
        if (watch.LastError is not null && watch.PairingSucceeded)
            return Failure(
                "播放器",
                watch.LastError.Contains("无法启动", StringComparison.Ordinal)
                    ? "配对成功，但 Moonlight 未能启动。"
                    : watch.LastError,
                "重新安装完整安装包，并确认安全软件未拦截 moonlight.exe。");
        return Waiting(
            "播放器",
            "正在等待配对完成。",
            "配对成功后 PalPeek 会自动启动播放器。");
    }

    private static DiagnosticItem Success(
        string name,
        string summary,
        string action) =>
        new(name, DiagnosticLevel.Success, summary, action);

    private static DiagnosticItem Failure(
        string name,
        string summary,
        string action) =>
        new(name, DiagnosticLevel.Failure, summary, action);

    private static DiagnosticItem Notice(
        string name,
        string summary,
        string action) =>
        new(name, DiagnosticLevel.Notice, summary, action);

    private static DiagnosticItem Waiting(
        string name,
        string summary,
        string action) =>
        new(name, DiagnosticLevel.Waiting, summary, action);
}
