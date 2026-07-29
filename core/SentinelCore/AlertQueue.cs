using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public interface IAlertQueue
{
    ValueTask<bool> TryEnqueueAsync(IncomingAlert alert, CancellationToken cancellationToken);
    IAsyncEnumerable<IncomingAlert> ReadAllAsync(CancellationToken cancellationToken);
    int ApproximateCount { get; }

    // Discards everything currently waiting, without analyzing it - for when the queue has
    // fallen far enough behind that catching up isn't worth it. Whatever AlertProcessor has
    // already dequeued and is actively analyzing is untouched; only what's still waiting behind
    // it is cleared. Returns what was dropped so the caller can mark those rows' database
    // records as no longer queued - otherwise AlertRecoveryService would resume them right back
    // into the queue on the next restart.
    IReadOnlyList<IncomingAlert> Clear();
}

public sealed class AlertQueue : IAlertQueue
{
    private readonly Channel<IncomingAlert> _channel;
    private int _count;

    public AlertQueue(IOptions<BridgeOptions> options)
    {
        _channel = Channel.CreateBounded<IncomingAlert>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            // Found while adding Clear(): DropWrite makes Writer.TryWrite return true even when
            // full - it "succeeds" by silently discarding the item instead of adding it. That
            // meant a saturated queue accepted every alert while quietly dropping most of them:
            // TryEnqueueAsync reported success (Program.cs returned 202, wrote a "queued" DB row
            // that nothing would ever pick up) and still incremented _count for an alert that was
            // never actually added, so ApproximateCount could drift upward without bound. The
            // "queue is full, return 503" branch a few lines down was effectively unreachable.
            // Wait is what this code's own logic already assumes: TryWrite genuinely fails when
            // there's no room, with nothing silently thrown away.
            FullMode = BoundedChannelFullMode.Wait,
            // Not SingleReader: Clear() below reads from this channel from a different call path
            // than AlertProcessor's own ReadAllAsync loop, so there are genuinely two possible
            // concurrent readers now - SingleReader=true only optimizes for exactly one and its
            // behavior under a second concurrent reader is unsupported.
            SingleReader = false,
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

    public IReadOnlyList<IncomingAlert> Clear()
    {
        var cleared = new List<IncomingAlert>();
        while (_channel.Reader.TryRead(out var alert))
        {
            Interlocked.Decrement(ref _count);
            cleared.Add(alert);
        }
        return cleared;
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
