using PalPeek.Core;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = System.Windows.MessageBox;

namespace PalPeek;

public sealed record WebInviteItem(
    string Id,
    string Name,
    bool Enabled,
    string? Url)
{
    public string StateText => Enabled ? "已启用" : "已停用";
    public string ToggleText => Enabled ? "停用" : "启用";
    public string UrlText => Url ?? "配置 Funnel 后会生成固定公网地址";
}

public partial class BrowserSharingPage : System.Windows.Controls.UserControl, IDisposable
{
    private readonly PalPeekOptions _options;
    private readonly ConfigStore _config;
    private readonly WebInviteService _invites;
    private readonly FunnelManager _funnel;
    private readonly HostStateStore _hostState;
    private CancellationTokenSource? _funnelCancellation;
    private bool _loading;

    public BrowserSharingPage(
        PalPeekOptions options,
        ConfigStore config,
        WebInviteService invites,
        FunnelManager funnel,
        HostStateStore hostState)
    {
        _options = options;
        _config = config;
        _invites = invites;
        _funnel = funnel;
        _hostState = hostState;
        InitializeComponent();
        DataContext = this;
        Loaded += BrowserSharingPage_Loaded;
        _invites.Changed += Invites_Changed;
        _hostState.Changed += HostState_Changed;
    }

    public ObservableCollection<WebInviteItem> Invites { get; } = [];

    public void Dispose()
    {
        _funnelCancellation?.Cancel();
        _invites.Changed -= Invites_Changed;
        _hostState.Changed -= HostState_Changed;
    }

    private async void BrowserSharingPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        EnabledCheckBox.IsChecked = _options.BrowserSharing.Enabled;
        QualityComboBox.SelectedIndex = _options.BrowserSharing.Quality ==
            BrowserStreamQuality.P720_60 ? 1 : 0;
        ReloadInvites();
        UpdateUploadEstimate(_hostState.Get().ViewerCount);
        _loading = false;
        await RefreshFunnelAsync();
    }

    private async void EnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var enabled = EnabledCheckBox.IsChecked == true;
        if (enabled)
        {
            var answer = WpfMessageBox.Show(
                "开启后，持有有效链接和口令的互联网用户可以观看你允许分享的游戏。\n\n" +
                "PalPeek 不会开放桌面、系统音频或远程控制。是否继续？",
                "开启网页观战",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                _loading = true;
                EnabledCheckBox.IsChecked = false;
                _loading = false;
                return;
            }
            try
            {
                _funnelCancellation = new CancellationTokenSource();
                SetBusy(true, "正在配置 Tailscale Funnel，请完成浏览器中的授权…", canCancel: true);
                await EnableFunnelWithChoiceAsync(_funnelCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                _loading = true;
                EnabledCheckBox.IsChecked = false;
                _loading = false;
                SetBusy(false, "已取消配置公网入口。");
                return;
            }
            catch (Exception ex)
            {
                _loading = true;
                EnabledCheckBox.IsChecked = false;
                _loading = false;
                SetBusy(false, string.Empty);
                WpfMessageBox.Show(ex.Message, "无法开启网页观战", MessageBoxButton.OK, MessageBoxImage.Error);
                await RefreshFunnelAsync();
                return;
            }
            finally
            {
                _funnelCancellation?.Dispose();
                _funnelCancellation = null;
            }
        }
        _options.BrowserSharing.Enabled = enabled;
        _config.Save(_options);
        SetBusy(false, enabled ? "网页观战已开启。" : "网页观战已关闭，固定链接保持不变。");
        await RefreshFunnelAsync();
    }

    private async void ConfigureFunnelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_funnelCancellation is not null)
        {
            _funnelCancellation.Cancel();
            StatusText.Text = "正在取消 Funnel 配置…";
            return;
        }
        try
        {
            _funnelCancellation = new CancellationTokenSource();
            SetBusy(true, "正在配置 Tailscale Funnel，请完成浏览器中的授权…", canCancel: true);
            await EnableFunnelWithChoiceAsync(_funnelCancellation.Token);
            SetBusy(false, "公网入口已就绪。");
            ReloadInvites();
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "已取消配置公网入口。");
        }
        catch (Exception ex)
        {
            SetBusy(false, string.Empty);
            WpfMessageBox.Show(ex.Message, "Funnel 配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _funnelCancellation?.Dispose();
            _funnelCancellation = null;
        }
        await RefreshFunnelAsync();
    }

    private async void DisableFunnelButton_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show(
                "关闭公网入口后，所有固定链接会立即离线，但邀请和口令仍保留。是否继续？",
                "关闭公网入口",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            SetBusy(true, "正在关闭 PalPeek 公网入口…");
            await _funnel.DisableAsync();
            _options.BrowserSharing.Enabled = false;
            _config.Save(_options);
            EnabledCheckBox.IsChecked = false;
            SetBusy(false, "公网入口已关闭。");
            ReloadInvites();
        }
        catch (Exception ex)
        {
            SetBusy(false, string.Empty);
            WpfMessageBox.Show(ex.Message, "无法关闭公网入口", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await RefreshFunnelAsync();
    }

    private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QualityComboBox.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<BrowserStreamQuality>(item.Tag?.ToString(), out var quality))
            return;
        _options.BrowserSharing.Quality = quality;
        _config.Save(_options);
        UpdateUploadEstimate(_hostState.Get().ViewerCount);
        StatusText.Text = quality == BrowserStreamQuality.P720_60
            ? "网页画质已设为 720p60，下次网页串流生效。"
            : "网页画质已设为 720p30，下次网页串流生效。";
    }

    private void CreateInviteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var created = _invites.Create(InviteNameTextBox.Text, InvitePasswordBox.Password);
            InvitePasswordBox.Clear();
            StatusText.Text = created.Url is null
                ? "链接已创建；配置 Funnel 后即可复制公网地址。"
                : "固定链接已创建，可以复制给观众。";
        }
        catch (ArgumentException ex)
        {
            WpfMessageBox.Show(ex.Message, "无法创建链接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyInviteButton_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemFromButton(sender);
        if (item?.Url is null)
        {
            WpfMessageBox.Show("请先配置 Tailscale Funnel 公网入口。", "链接尚不可用",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        System.Windows.Clipboard.SetText(item.Url);
        StatusText.Text = "固定观战链接已复制。口令需要通过其他安全方式发送。";
    }

    private void PasswordInviteButton_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemFromButton(sender);
        if (item is null)
            return;
        var prompt = new InvitePasswordDialog { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true)
            return;
        try
        {
            _invites.ChangePassword(item.Id, prompt.Password);
            StatusText.Text = "口令已修改，旧的登录和播放会话已失效。";
        }
        catch (ArgumentException ex)
        {
            WpfMessageBox.Show(ex.Message, "无法修改口令", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleInviteButton_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemFromButton(sender);
        if (item is null)
            return;
        _invites.SetEnabled(item.Id, !item.Enabled);
        StatusText.Text = item.Enabled ? "链接已停用，现有会话已失效。" : "链接已重新启用。";
    }

    private void DeleteInviteButton_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemFromButton(sender);
        if (item is null || WpfMessageBox.Show(
                $"确定删除“{item.Name}”吗？该固定链接会永久失效。",
                "删除观战链接",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _invites.Delete(item.Id);
        StatusText.Text = "固定链接已删除。";
    }

    private async Task RefreshFunnelAsync()
    {
        var state = await _funnel.GetStateAsync();
        FunnelStatusText.Text = state.Configured
            ? $"{state.Message} https://{state.HostName}{(state.Port == 443 ? "" : $":{state.Port}")}"
            : state.Message;
        ConfigureFunnelButton.IsEnabled = !state.Configured;
        DisableFunnelButton.IsEnabled = state.Configured;
        ReloadInvites();
    }

    private async Task<FunnelState> EnableFunnelWithChoiceAsync(CancellationToken cancellationToken)
    {
        var state = await _funnel.GetStateAsync(cancellationToken);
        if (state.Configured)
            return state;
        var available = new[] { 443, 8443, 10000 }
            .Where(port => !state.OccupiedPorts.Contains(port))
            .ToArray();
        if (available.Length == 0)
            throw new InvalidOperationException("Funnel 的 443、8443 和 10000 端口均已被其他服务占用。");

        var selected = available[0];
        if (state.OccupiedPorts.Contains(443) && available.Contains(8443) && available.Contains(10000))
        {
            var choice = WpfMessageBox.Show(
                "HTTPS 443 已被其他 Funnel 服务占用，PalPeek 不会覆盖它。\n\n" +
                "选择“是”使用 8443，选择“否”使用 10000，选择“取消”返回。",
                "选择网页观战端口",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Information,
                MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel)
                throw new OperationCanceledException("已取消配置公网入口。");
            selected = choice == MessageBoxResult.Yes ? 8443 : 10000;
        }
        else if (selected != 443)
        {
            if (WpfMessageBox.Show(
                    $"HTTPS 443 已被其他 Funnel 服务占用，PalPeek 不会覆盖它。\n\n是否改用 {selected}？",
                    "公网端口冲突",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                throw new OperationCanceledException("已取消配置公网入口。");
        }
        return await _funnel.EnableAsync(selected, cancellationToken);
    }

    private void Invites_Changed(object? sender, EventArgs e) => Dispatcher.Invoke(ReloadInvites);

    private void HostState_Changed(object? sender, HostStatus status) =>
        Dispatcher.Invoke(() => UpdateUploadEstimate(status.ViewerCount));

    private void UpdateUploadEstimate(int viewerCount)
    {
        var perViewer = _options.BrowserSharing.Quality == BrowserStreamQuality.P720_60 ? 4 : 2;
        UploadEstimateText.Text = viewerCount == 0
            ? "当前没有观众，预计网页上行为 0 Mbps。"
            : $"当前共 {viewerCount} 名观众；若均使用网页，预计上行约 {viewerCount * perViewer} Mbps。";
    }

    private void ReloadInvites()
    {
        Invites.Clear();
        foreach (var invite in _invites.List())
            Invites.Add(new WebInviteItem(invite.Id, invite.Name, invite.Enabled, invite.Url));
        EmptyInvitesText.Visibility = Invites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private WebInviteItem? ItemFromButton(object sender)
    {
        var id = (sender as WpfButton)?.Tag?.ToString();
        return Invites.FirstOrDefault(x => x.Id == id);
    }

    private void SetBusy(bool busy, string message, bool canCancel = false)
    {
        ConfigureFunnelButton.IsEnabled = !busy || canCancel;
        ConfigureFunnelButton.Content = busy && canCancel ? "取消配置" : "配置入口";
        DisableFunnelButton.IsEnabled = !busy;
        EnabledCheckBox.IsEnabled = !busy;
        StatusText.Text = message;
    }
}

internal sealed class InvitePasswordDialog : Window
{
    private readonly PasswordBox _password = new() { MaxLength = 128, Padding = new Thickness(9) };

    public InvitePasswordDialog()
    {
        Title = "修改观战口令";
        Width = 390;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "输入新的固定口令",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "长度为 8–128 个字符。修改后旧会话会立即失效。",
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 6, 0, 14)
        });
        panel.Children.Add(_password);
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new WpfButton { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        var save = new WpfButton { Content = "保存口令", IsDefault = true };
        save.Click += (_, _) =>
        {
            try
            {
                WebInvitePassword.Validate(_password.Password);
                DialogResult = true;
            }
            catch (ArgumentException ex)
            {
                WpfMessageBox.Show(ex.Message, "口令无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public string Password => _password.Password;
}
