using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

// The asymmetry that makes this worth a queue: a lost move means the camera didn't budge, which is
// obvious from the video. A stop that overtakes its own move means a camera that keeps turning
// until something else stops it - and on a quick tap, with two independent requests, which one
// wins is a coin flip.
public class PtzCommandQueueTests
{
    [Fact]
    public async Task Commands_run_in_the_order_they_were_issued()
    {
        var queue = new PtzCommandQueue();
        var order = new List<string>();

        // The move is deliberately the slow one: without a queue the stop would finish first and
        // the camera would be sent stop-then-move.
        queue.Enqueue(async () =>
        {
            await Task.Delay(60);
            lock (order) order.Add("move");
        });
        queue.Enqueue(() =>
        {
            lock (order) order.Add("stop");
            return Task.CompletedTask;
        });

        await queue.WaitForIdleAsync();

        Assert.Equal(new[] { "move", "stop" }, order);
    }

    [Fact]
    public async Task A_burst_of_taps_stays_in_order()
    {
        var queue = new PtzCommandQueue();
        var order = new List<int>();

        for (var i = 0; i < 12; i++)
        {
            var index = i;
            queue.Enqueue(async () =>
            {
                // Alternating delays, so anything running concurrently would interleave visibly.
                await Task.Delay(index % 2 == 0 ? 12 : 1);
                lock (order) order.Add(index);
            });
        }

        await queue.WaitForIdleAsync();

        Assert.Equal(Enumerable.Range(0, 12), order);
    }

    [Fact]
    public async Task Only_one_command_is_ever_in_flight()
    {
        var queue = new PtzCommandQueue();
        var running = 0;
        var maxConcurrent = 0;

        for (var i = 0; i < 8; i++)
        {
            queue.Enqueue(async () =>
            {
                var now = Interlocked.Increment(ref running);
                lock (queue) maxConcurrent = Math.Max(maxConcurrent, now);
                await Task.Delay(8);
                Interlocked.Decrement(ref running);
            });
        }

        await queue.WaitForIdleAsync();

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task A_command_that_throws_does_not_break_the_chain()
    {
        // If a failed move left a faulted task behind, the stop queued after it would never run -
        // which is the exact failure this class exists to prevent, reintroduced by the error path.
        var queue = new PtzCommandQueue();
        var stopped = false;

        queue.Enqueue(() => throw new InvalidOperationException("the camera refused the move"));
        queue.Enqueue(() =>
        {
            stopped = true;
            return Task.CompletedTask;
        });

        await queue.WaitForIdleAsync();

        Assert.True(stopped);
    }

    [Fact]
    public async Task A_command_that_faults_asynchronously_also_does_not_break_the_chain()
    {
        var queue = new PtzCommandQueue();
        var stopped = false;

        queue.Enqueue(async () =>
        {
            await Task.Delay(5);
            throw new HttpRequestException("the network went away mid-move");
        });
        queue.Enqueue(() =>
        {
            stopped = true;
            return Task.CompletedTask;
        });

        await queue.WaitForIdleAsync();

        Assert.True(stopped);
    }

    [Fact]
    public async Task An_idle_queue_is_immediately_idle()
    {
        var queue = new PtzCommandQueue();

        await queue.WaitForIdleAsync();

        // And a queue that has fully drained accepts more work without accumulating anything.
        var ran = false;
        queue.Enqueue(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        await queue.WaitForIdleAsync();
        Assert.True(ran);
    }
}
