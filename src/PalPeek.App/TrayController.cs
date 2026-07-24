using PalPeek.Core;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace PalPeek;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly Action _exit;
    private readonly Action _uninstall;

    public TrayController(MainWindow window, HostStateStore state, Action exit, Action uninstall)
    {
        _window = window;
        _exit = exit;
        _uninstall = uninstall;
        _icon = new Forms.NotifyIcon
        {
            Text = "PalPeek · 0 人观看",
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        state.Changed += (_, status) =>
        {
            var text = $"PalPeek · {status.ViewerCount} 人观看";
            if (_icon.Text != text)
                _icon.Text = text;
        };
        window.Closing += (_, args) =>
        {
            args.Cancel = true;
            window.Hide();
        };
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

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 PalPeek", null, (_, _) => ShowWindow());
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
        _icon.Visible = false;
        _icon.Dispose();
    }
}
