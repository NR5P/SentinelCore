using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SentinelCore.Web;

// Hands the newest decoded frame from a camera's capture loop to its processing loop, and nothing
// else. See TriggerZoneMonitor, which owns one of these per detection session.
//
// Capacity one with DropOldest is the entire design: when processing is slower than capture, the
// frame already waiting is discarded in favour of the newer one. That is deliberately lossy, and it
// is the correct behaviour rather than a compromise - a motion detector wants the most recent view
// of a scene, and acting on a queued stale frame means acting on a moment that has already passed.
//
// Queueing instead is what let a single stall become permanent latency. ffmpeg writes 172KB frames
// into a 64KB pipe, so it blocks the moment nothing is reading; the camera transports used here
// (Reolink HTTP-FLV, RTSP over TCP) never drop a frame of their own; so whatever backed up during
// one bad moment was still there afterwards and every later frame inherited the delay. Triggers
// then fired seconds after the event, and the photograph taken at that point showed an empty scene.
//
// The same choice is made by every established design in this space: ZoneMinder's analysis daemon
// skips frames rather than let its capture daemon overtake it, Frigate drops frames when its
// detection queue is full and publishes the loss as skipped_fps. Skipped is counted here for the
// same reason - silently dropping frames and silently keeping up look identical from outside.
public sealed class DetectionFrameSlot
{
    // Buffers are recycled rather than allocated per frame. At most three are ever live: one being
    // filled, one waiting in the slot, one being processed.
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly Channel<(byte[] Buffer, DateTimeOffset At)> _channel;
    private readonly int _frameSizeBytes;
    private long _skipped;

    public DetectionFrameSlot(int frameSizeBytes)
    {
        _frameSizeBytes = frameSizeBytes;
        _channel = Channel.CreateBounded<(byte[] Buffer, DateTimeOffset At)>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            },
            dropped =>
            {
                // Recycling the displaced frame's buffer here is what stops the pool growing while
                // frames are being dropped - which is exactly when allocation pressure would be
                // least welcome.
                Interlocked.Increment(ref _skipped);
                _pool.Add(dropped.Buffer);
            });
    }

    // How many frames the processing loop never saw. A steadily climbing count means detection
    // cannot keep up with this camera - which costs sensitivity, but never freshness.
    public long Skipped => Interlocked.Read(ref _skipped);

    public byte[] Rent() => _pool.TryTake(out var buffer) ? buffer : new byte[_frameSizeBytes];

    public void Return(byte[] buffer) => _pool.Add(buffer);

    // Publishes a filled buffer and returns one to fill next. A refused write means the channel has
    // been completed - the session is shutting down - so the caller keeps the buffer it had.
    public byte[] Publish(byte[] buffer, DateTimeOffset at) =>
        _channel.Writer.TryWrite((buffer, at)) ? Rent() : buffer;

    public IAsyncEnumerable<(byte[] Buffer, DateTimeOffset At)> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
