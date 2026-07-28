using PalPeek.Core;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace PalPeek;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly HostStateStore _state;
    private readonly MainWindow _window;
    private readonly Action _exit;
    private readonly Action _settings;
    private readonly Action _uninstall;
    private readonly Icon _baseIcon;
    private readonly Icon _sharingIcon;
    private string? _notifiedSessionId;
    private bool _disposed;

    public TrayController(
        MainWindow window,
        HostStateStore state,
        Action exit,
        Action settings,
        Action uninstall)
    {
        _window = window;
        _state = state;
        _exit = exit;
        _settings = settings;
        _uninstall = uninstall;
        _baseIcon = LoadAppIcon();
        _sharingIcon = CreateSharingIcon(_baseIcon);
        _icon = new Forms.NotifyIcon
        {
            Text = "PalPeek · 0 人观看",
            Icon = _baseIcon,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        state.Changed += State_Changed;
        window.Closing += (_, args) =>
        {
            args.Cancel = true;
            window.Hide();
        };
        UpdateStatus(state.Get());
    }

    private static Icon LoadAppIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/palpeek.ico"));
        if (resource is null)
            return SystemIcons.Application;

        using (resource.Stream)
        using (var icon = new Icon(resource.Stream))
            return (Icon)icon.Clone();
    }

    private static Icon CreateSharingIcon(Icon baseIcon)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(Brushes.White, 18, 18, 14, 14);
            using var dot = new SolidBrush(Color.FromArgb(255, 224, 75, 87));
            graphics.FillEllipse(dot, 20, 20, 10, 10);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    private void State_Changed(object? sender, HostStatus status)
    {
        if (_disposed)
            return;

        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
            UpdateStatus(status);
        else
            _ = dispatcher.BeginInvoke(() => UpdateStatus(status));
    }

    private void UpdateStatus(HostStatus status)
    {
        if (_disposed)
            return;

        var hasSharedGame = status.Game is not null;
        _icon.Icon = hasSharedGame ? _sharingIcon : _baseIcon;

        var text = $"PalPeek · {status.ViewerCount} 人观看";
        if (_icon.Text != text)
            _icon.Text = text;

        if (status.Game is not null &&
            status.CaptureState == CaptureState.Ready &&
            !string.Equals(
                _notifiedSessionId,
                status.Game.SessionId,
                StringComparison.Ordinal))
        {
            _notifiedSessionId = status.Game.SessionId;
            _icon.ShowBalloonTip(
                5000,
                "PalPeek",
                $"《{status.Game.Name}》已开始向好友显示。",
                Forms.ToolTipIcon.Info);
        }
        else if (status.Game is null && _notifiedSessionId is not null)
        {
            _notifiedSessionId = null;
            _icon.ShowBalloonTip(
                5000,
                "PalPeek",
                "分享已结束。",
                Forms.ToolTipIcon.Info);
        }
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 PalPeek", null, (_, _) => ShowWindow());
        menu.Items.Add("设置…", null, (_, _) =>
        {
            ShowWindow();
            _settings();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("卸载 PalPeek…", null, (_, _) => _uninstall());
        menu.Items.Add("退出", null, (_, _) => _exit());
        return menu;
    }

    public void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _state.Changed -= State_Changed;
        _icon.Visible = false;
        _icon.Dispose();
        _sharingIcon.Dispose();
        _baseIcon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
