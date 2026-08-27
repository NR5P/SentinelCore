using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// What an enqueue did. Superseded is the alert this one stood down - see AlertQueue.TryEnqueueAsync -
// and is null on the ordinary path where nothing from that camera was waiting.
public sealed record EnqueueResult(bool Accepted, IncomingAlert? Superseded)
{
    public static readonly EnqueueResult Rejected = new(false, null);
}

public interface IAlertQueue
{
    // supersedeWaiting is the ingest path's setting (see RepeatAlertOptions): an alert from a
    // camera that already has one WAITING here is a second look at the same moment, and the newer
    // frame is the better one to spend an analysis on. Off for AlertRecoveryService, which replays
    // a restart's backlog where every alert is already historical and none supersedes another.
    ValueTask<EnqueueResult> TryEnqueueAsync(IncomingAlert alert, bool supersedeWaiting, CancellationToken cancellationToken);

    // The alert from this camera that is still waiting, if any - so the ingest path can look at the
    // frame it is about to stand down before deciding to. Advisory only: the consumer may dequeue it
    // an instant later, which is why the supersede decision is still re-made under the lock inside
    // TryEnqueueAsync. See BurstFramePicker.
    IncomingAlert? PeekWaiting(string camera);

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

// The analysis queue, and - since a camera retriggering is the normal way this fills up - the
// place that decides a burst doesn't deserve one full analysis per frame.
//
// AlertProcessor is a single serial consumer and one analysis costs whatever the vision backend
// costs (minutes, on a CPU-only box), so a camera that fires three times in twenty seconds used to
// put three near-identical frames in line and answer the last of them a full analysis-time later
// than the first. That last frame is routinely the interesting one: the truck arrives, then someone
// gets out of it. Standing down a frame that is still WAITING in favour of a newer one from the
// same camera keeps the queue on the live edge of the event instead of working through its history.
//
// Only ever the waiting ones. An alert AlertProcessor has already dequeued is mid-analysis, and
// there is nothing to gain by abandoning work that is already most of the way done.
public sealed class AlertQueue : IAlertQueue
{
    private readonly Channel<IncomingAlert> _channel;
    private int _count;

    // Guards both maps together with the write/read of the channel itself, because
    // "is anything from this camera still waiting?" has to be answered against the same instant
    // the item is added or removed. Contended a handful of times a minute at most.
    private readonly object _gate = new();

    // The most recent alert enqueued per camera that has not yet been handed to AlertProcessor.
    // Holds the same IncomingAlert instance the channel holds, so it costs a reference, not a copy
    // of the image.
    private readonly Dictionary<string, IncomingAlert> _waitingByCamera = new(StringComparer.OrdinalIgnoreCase);

    // Ids that have been stood down since they were written. A channel has no way to remove an
    // item, so the skip happens on the way out instead - see ReadAllAsync.
    private readonly HashSet<Guid> _superseded = [];

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

    public IncomingAlert? PeekWaiting(string camera)
    {
        lock (_gate)
        {
            return _waitingByCamera.TryGetValue(camera, out var waiting) ? waiting : null;
        }
    }

    public ValueTask<EnqueueResult> TryEnqueueAsync(IncomingAlert alert, bool supersedeWaiting, CancellationToken cancellationToken)
    {
        IncomingAlert? superseded;

        // TryWrite is synchronous and non-blocking, so it can be held inside the lock - and has to
        // be. Registering the alert as "waiting" after releasing the lock would race the consumer:
        // it could dequeue and deregister this alert first, and the late registration would then
        // leave a live analysis looking like it was still in the queue, ready to be stood down by
        // the next frame that arrived.
        lock (_gate)
        {
            if (!_channel.Writer.TryWrite(alert))
                return ValueTask.FromResult(EnqueueResult.Rejected);

            Interlocked.Increment(ref _count);

            superseded = supersedeWaiting && _waitingByCamera.TryGetValue(alert.Camera, out var waiting)
                ? waiting
                : null;

            if (superseded is not null) _superseded.Add(superseded.Id);
            _waitingByCamera[alert.Camera] = alert;
        }

        return ValueTask.FromResult(new EnqueueResult(true, superseded));
    }

    public IReadOnlyList<IncomingAlert> Clear()
    {
        var cleared = new List<IncomingAlert>();

        lock (_gate)
        {
            while (_channel.Reader.TryRead(out var alert))
            {
                Interlocked.Decrement(ref _count);
                cleared.Add(alert);
            }

            // Nothing is waiting any more, so nothing can be superseded. Leaving these populated
            // would let a stale entry stand down an alert enqueued after the clear.
            _waitingByCamera.Clear();
            _superseded.Clear();
        }

        return cleared;
    }

    public async IAsyncEnumerable<IncomingAlert> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var alert in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _count);

            bool skip;
            lock (_gate)
            {
                skip = _superseded.Remove(alert.Id);

                // Only when it is still this alert: a newer one from the same camera has already
                // taken the slot, and clearing that would let the one after it supersede an alert
                // that is about to start analysis.
                if (_waitingByCamera.TryGetValue(alert.Camera, out var waiting) && waiting.Id == alert.Id)
                    _waitingByCamera.Remove(alert.Camera);
            }

            // Its database row was rewritten as "superseded" by whoever enqueued the replacement,
            // so there is nothing left to do for it here.
            if (skip) continue;

            yield return alert;
        }
    }
}
