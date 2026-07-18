using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public interface IAlertQueue
{
    ValueTask<bool> TryEnqueueAsync(IncomingAlert alert, CancellationToken cancellationToken);
    IAsyncEnumerable<IncomingAlert> ReadAllAsync(CancellationToken cancellationToken);
    int ApproximateCount { get; }
}

public sealed class AlertQueue : IAlertQueue
{
    private readonly Channel<IncomingAlert> _channel;
    private int _count;

    public AlertQueue(IOptions<BridgeOptions> options)
    {
        _channel = Channel.CreateBounded<IncomingAlert>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public int ApproximateCount => Math.Max(0, Volatile.Read(ref _count));

    public async ValueTask<bool> TryEnqueueAsync(IncomingAlert alert, CancellationToken cancellationToken)
    {
        if (!_channel.Writer.TryWrite(alert))
            return false;

        Interlocked.Increment(ref _count);
        await Task.CompletedTask;
        return true;
    }

    public async IAsyncEnumerable<IncomingAlert> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var alert in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _count);
            yield return alert;
        }
    }
}
