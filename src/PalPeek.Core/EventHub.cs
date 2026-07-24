using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PalPeek.Core;

public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<PalPeekEvent>> _subscribers = new();

    public void Publish(PalPeekEvent item)
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.Writer.TryWrite(item);
    }

    public async IAsyncEnumerable<PalPeekEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<PalPeekEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _subscribers[id] = channel;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}
