using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Every Organizer sync that changes something calls Changed(); the phones must get one push per
// burst, not one per keystroke, and never more often than the minimum gap.
public sealed class OrganizerPushNudgerTests
{
    private static (OrganizerPushNudger Nudger, Func<int> Sent) New(TimeSpan settle, TimeSpan gap)
    {
        var sent = 0;
        var nudger = new OrganizerPushNudger(() => { Interlocked.Increment(ref sent); return Task.CompletedTask; }, settle, gap, NullLogger.Instance);
        return (nudger, () => Volatile.Read(ref sent));
    }

    [Fact]
    public async Task ABurstOfChanges_SendsOnePushAfterItSettles()
    {
        var (nudger, sent) = New(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));

        for (var i = 0; i < 10; i++) nudger.Changed();
        Assert.Equal(0, sent());

        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Equal(1, sent());
    }

    [Fact]
    public async Task AChangeSoonAfterAPush_WaitsOutTheGapThenSendsOnce()
    {
        var (nudger, sent) = New(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(800));

        nudger.Changed();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, sent());

        nudger.Changed();
        nudger.Changed();
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(1, sent());   // still inside the gap

        await Task.Delay(900, TestContext.Current.CancellationToken);
        Assert.Equal(2, sent());
    }

    [Fact]
    public async Task AFailedSend_DoesNotStopTheNextOne()
    {
        var calls = 0;
        var nudger = new OrganizerPushNudger(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("provider down");
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(30), TimeSpan.Zero, NullLogger.Instance);

        nudger.Changed();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        nudger.Changed();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref calls));
    }
}
