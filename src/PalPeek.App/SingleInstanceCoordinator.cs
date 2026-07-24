using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PalPeek;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const int CoordinationPort = 48190;
    private readonly CancellationTokenSource _stopping = new();
    private TcpListener? _server;
    private Task? _listener;

    public bool TryAcquire()
    {
        var server = new TcpListener(IPAddress.Loopback, CoordinationPort)
        {
            ExclusiveAddressUse = true
        };
        try
        {
            server.Start();
            _server = server;
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            server.Stop();
            return false;
        }
    }

    public void StartListening(Func<string, Task> handler)
    {
        if (_server is null || _listener is not null)
            throw new InvalidOperationException("PalPeek 单实例协调器尚未就绪。");

        _listener = Task.Run(async () =>
        {
            while (!_stopping.IsCancellationRequested)
            {
                try
                {
                    using var client = await _server.AcceptTcpClientAsync(_stopping.Token);
                    using var reader = new StreamReader(
                        client.GetStream(),
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false,
                        leaveOpen: false);
                    var command = await reader.ReadLineAsync(_stopping.Token) ?? string.Empty;
                    await System.Windows.Application.Current.Dispatcher
                        .InvokeAsync(() => handler(command)).Task.Unwrap();
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // A failed client must not stop future shortcut activations.
                }
            }
        });
    }

    public static async Task NotifyExistingAsync(string? command)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, CoordinationPort, timeout.Token);
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(false),
                leaveOpen: false)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(command ?? string.Empty);
        }
        catch (Exception ex) when (
            ex is SocketException or IOException or OperationCanceledException)
        {
            // The primary instance may still be starting or may have just exited.
        }
    }

    public void Dispose()
    {
        if (_stopping.IsCancellationRequested)
            return;

        _stopping.Cancel();
        _server?.Stop();
        _server = null;
        _stopping.Dispose();
    }
}
