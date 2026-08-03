using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalPeek.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PalPeek;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayController? _tray;
    private readonly SingleInstanceCoordinator _singleInstance = new();
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_singleInstance.TryAcquire())
        {
            await SingleInstanceCoordinator.NotifyExistingAsync(e.Args.FirstOrDefault());
            Shutdown();
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            System.Windows.MessageBox.Show("PalPeek 仅支持 Windows 11 x64。", "PalPeek",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ConfigStore>();
                services.AddSingleton(sp => sp.GetRequiredService<ConfigStore>().Load());
                services.AddSingleton<ITailscaleService, TailscaleService>();
                services.AddSingleton<LeaseManager>();
                services.AddSingleton<WebInviteService>();
                services.AddSingleton<FunnelManager>();
                services.AddSingleton<WebMediaBuffer>();
                services.AddSingleton<EventHub>();
                services.AddSingleton<HostStateStore>();
                services.AddSingleton<SharingControl>();
                services.AddSingleton<GameArtworkService>();
                services.AddSingleton<StartupManager>();
                services.AddSingleton<SettingsWindowFactory>();
                services.AddSingleton<DiagnosticsService>();
                services.AddSingleton<DiagnosticsWindowFactory>();
                services.AddSingleton<SunshineBridge>();
                services.AddHostedService(
                    sp => sp.GetRequiredService<SunshineBridge>());
                services.AddSingleton<WebStreamCoordinator>();
                services.AddHostedService(
                    sp => sp.GetRequiredService<WebStreamCoordinator>());
                services.AddHostedService<WebMediaPipeService>();
                services.AddHostedService<BrowserWatchService>();
                services.AddSingleton<MoonlightLauncher>();
                services.AddSingleton<FriendDiscovery>();
                services.AddSingleton(sp => SteamCatalog.Discover());
                services.AddSingleton<SteamGameDetector>();
                services.AddHostedService<HostMonitor>();
                services.AddHostedService<PeerApiService>();
                services.AddHostedService<FriendDiscovery>(
                    sp => sp.GetRequiredService<FriendDiscovery>());
                services.AddSingleton<MainWindow>();
            })
            .Build();

        try
        {
            var options = _host.Services.GetRequiredService<PalPeekOptions>();
            _host.Services.GetRequiredService<StartupManager>()
                .SetEnabled(options.StartWithWindows);
        }
        catch
        {
            // The settings page reports registry errors when the user changes this
            // option. Startup itself should remain usable if Windows blocks the key.
        }

        try
        {
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"PalPeek 启动失败：{ex.Message}", "PalPeek",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            var window = _host.Services.GetRequiredService<MainWindow>();
            _tray = new TrayController(
                window,
                _host.Services.GetRequiredService<HostStateStore>(),
                ShutdownApplication,
                window.OpenSettings,
                RequestUninstall);
            window.UninstallRequested += (_, _) => RequestUninstall();
            _singleInstance.StartListening(async command =>
            {
                if (command.Equals("--shutdown-for-update", StringComparison.OrdinalIgnoreCase))
                {
                    ShutdownApplication();
                    return;
                }

                _tray.ShowWindow();
                if (command.StartsWith("palpeek://", StringComparison.OrdinalIgnoreCase))
                    await window.OpenWatchUriAsync(command);
            });

            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
                window.Show();

            var initialUri = e.Args.FirstOrDefault(
                argument => argument.StartsWith("palpeek://", StringComparison.OrdinalIgnoreCase));
            if (initialUri is not null)
                _ = window.OpenWatchUriAsync(initialUri);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"PalPeek 界面初始化失败：{ex.Message}",
                "PalPeek",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShutdownApplication();
        }
    }

    private async void ShutdownApplication()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;
        _tray?.Dispose();
        _singleInstance.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        Shutdown();
    }

    private void RequestUninstall()
    {
        var uninstaller = Path.Combine(AppContext.BaseDirectory, "unins000.exe");
        if (!File.Exists(uninstaller))
        {
            System.Windows.MessageBox.Show(
                "没有找到卸载程序。请从 Windows“设置 > 应用 > 已安装的应用”中卸载 PalPeek。",
                "卸载 PalPeek",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "确定要卸载 PalPeek 吗？\n\nPalPeek 将立即退出，并移除程序文件、快捷方式和防火墙规则。",
            "确认卸载 PalPeek",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uninstaller,
                Arguments = "/SILENT /NORESTART",
                UseShellExecute = true
            });
            ShutdownApplication();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"无法启动卸载程序：{ex.Message}",
                "卸载 PalPeek",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
