using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The hand-off between a camera's capture loop and its motion-detection loop.
//
// The behaviour being pinned here is that it DROPS rather than queues. Queueing is what let a
// single stall turn into permanent latency: ffmpeg blocks the moment nothing reads its pipe, the
// camera transports never drop a frame of their own, so a backlog was never paid back and every
// later frame inherited the delay. Triggers then fired seconds after the event and photographed an
// empty scene.
public sealed class DetectionFrameSlotTests
{
    private const int FrameSize = 8;

    private static readonly DateTimeOffset Base = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    // "Keeps up" is established by waiting for the consumer to say so, not by sleeping long enough
    // that it probably has.
    //
    // This used to publish every 20ms and trust that the reader had been scheduled in between. That
    // holds right up until something else in the run is busy: adding a test elsewhere in the suite
    // that shells out to ffmpeg was enough to make this fail roughly one run in five, on a slot that
    // was behaving perfectly. The property being pinned has nothing to do with wall-clock time - it
    // is that a frame taken out of the slot before the next goes in is never counted as dropped - so
    // the handshake tests it exactly, and the same way every time.
    //
    // The timeout is a liveness bound rather than a scheduling assumption: if a frame really did go
    // missing the semaphore would never be released, and a test that hangs tells you far less than
    // one that fails.
    [Fact]
    public async Task AConsumerThatKeepsUp_SeesEveryFrame()
    {
        var slot = new DetectionFrameSlot(FrameSize);
        var seen = new List<DateTimeOffset>();
        var consumed = new SemaphoreSlim(0);

        var reading = Task.Run(async () =>
        {
            await foreach (var (buffer, at) in slot.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                seen.Add(at);
                slot.Return(buffer);
                consumed.Release();
            }
        }, TestContext.Current.CancellationToken);

        var buffer = slot.Rent();
        for (var i = 0; i < 5; i++)
        {
            buffer = slot.Publish(buffer, Base.AddSeconds(i));
            Assert.True(
                await consumed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                $"the consumer never received frame {i}");
        }

        slot.Complete();
        await reading;

        Assert.Equal(5, seen.Count);
        Assert.Equal(0, slot.Skipped);
    }

    // The whole point: a consumer that never reads must not cause frames to pile up. The newest
    // frame wins and the rest are counted as skipped.
    [Fact]
    public void WhenTheConsumerNeverReads_OnlyTheNewestFrameSurvives()
    {
        var slot = new DetectionFrameSlot(FrameSize);

        var buffer = slot.Rent();
        for (var i = 0; i < 4; i++) buffer = slot.Publish(buffer, Base.AddSeconds(i));

        Assert.Equal(3, slot.Skipped);
    }

    [Fact]
    public async Task AfterFallingBehind_TheConsumerGetsTheNEWESTFrameNotTheOldest()
    {
        // This is the assertion that separates this design from a queue. A queue would hand back
        // Base+0 - the moment that has already passed - which is precisely the bug.
        var slot = new DetectionFrameSlot(FrameSize);

        var buffer = slot.Rent();
        for (var i = 0; i < 4; i++) buffer = slot.Publish(buffer, Base.AddSeconds(i));
        slot.Complete();

        var seen = new List<DateTimeOffset>();
        await foreach (var (taken, at) in slot.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            seen.Add(at);
            slot.Return(taken);
        }

        Assert.Single(seen);
        Assert.Equal(Base.AddSeconds(3), seen[0]);
    }

    // Dropped frames hand their buffer back, so sustained overload allocates nothing - which is
    // when allocation pressure would be least welcome.
    [Fact]
    public void DroppedFramesRecycleTheirBuffers()
    {
        var slot = new DetectionFrameSlot(FrameSize);
        var first = slot.Rent();

        var second = slot.Publish(first, Base);          // fills the slot, hands back a fresh buffer
        var third = slot.Publish(second, Base.AddSeconds(1)); // displaces the first, recycling it

        Assert.Equal(1, slot.Skipped);
        Assert.Same(first, third);
    }

    [Fact]
    public void PublishingAfterCompletion_HandsTheBufferBackRatherThanLosingIt()
    {
        var slot = new DetectionFrameSlot(FrameSize);
        slot.Complete();

        var buffer = slot.Rent();

        Assert.Same(buffer, slot.Publish(buffer, Base));
    }

    [Fact]
    public void RentedBuffersAreTheRequestedFrameSize() =>
        Assert.Equal(FrameSize, new DetectionFrameSlot(FrameSize).Rent().Length);
}
