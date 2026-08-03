using Microsoft.Extensions.Hosting;
using System.IO.Pipes;
using System.Text;

namespace PalPeek;

public sealed class WebMediaPipeService : BackgroundService
{
    public const string PipeName = "PalPeekWebMedia";
    private readonly WebMediaBuffer _buffer;

    public WebMediaPipeService(WebMediaBuffer buffer) => _buffer = buffer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                await ReadConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                await Task.Delay(250, stoppingToken);
            }
        }
    }

    private async Task ReadConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await WebMediaProtocol.ReadAsync(stream, cancellationToken);
            if (message is null)
                return;
            switch (message.Type)
            {
                case 1:
                    _buffer.SetInitialization(message.SessionId, message.Payload);
                    break;
                case 2:
                    _buffer.AddSegment(message.SessionId, new WebMediaSegment(
                        message.Sequence,
                        message.DurationMilliseconds,
                        message.Payload));
                    break;
                case 3:
                    _buffer.SetError(message.SessionId, Encoding.UTF8.GetString(message.Payload));
                    break;
                default:
                    throw new InvalidDataException("未知的网页媒体管道消息。");
            }
        }
    }

}
