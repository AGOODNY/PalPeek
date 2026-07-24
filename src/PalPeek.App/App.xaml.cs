using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalPeek.Core;
using System.IO;
using System.Windows;

namespace PalPeek;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayController? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
                services.AddSingleton<EventHub>();
                services.AddSingleton<HostStateStore>();
                services.AddSingleton<SharingControl>();
                services.AddSingleton<SunshineBridge>();
                services.AddHostedService(
                    sp => sp.GetRequiredService<SunshineBridge>());
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
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"PalPeek 启动失败：{ex.Message}", "PalPeek",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        _tray = new TrayController(window, _host.Services.GetRequiredService<HostStateStore>(), ShutdownApplication);
        window.Show();

        if (e.Args.FirstOrDefault()?.StartsWith("palpeek://", StringComparison.OrdinalIgnoreCase) == true)
            _ = window.OpenWatchUriAsync(e.Args[0]);
    }

    private async void ShutdownApplication()
    {
        _tray?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        Shutdown();
    }
}
