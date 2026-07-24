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

    public TrayController(MainWindow window, HostStateStore state, Action exit)
    {
        _window = window;
        _exit = exit;
        _icon = new Forms.NotifyIcon
        {
            Text = "PalPeek · 0 人观看",
            Icon = SystemIcons.Application,
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

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 PalPeek", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _exit());
        return menu;
    }

    private void ShowWindow()
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
