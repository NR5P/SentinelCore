using SentinelCore;
using Xunit;

namespace SentinelCore.Tests;

// The Telegram chat worker long-polls Telegram: it asks the API to hold the connection open for
// PollTimeoutSeconds and only answer when an update arrives. That only works while the HttpClient
// is willing to wait longer than the poll it is carrying, which Program.cs arranges by sizing the
// client's timeout as PollTimeoutSeconds + PollTimeoutHeadroomSeconds.
//
// The trap is that only one half of that invariant is fixed. The client's timeout is read once at
// startup; PollTimeoutSeconds is read from IOptionsMonitor on every iteration and hot-reloads with
// the rest of appsettings. So raising it in Settings - a change with no obvious blast radius -
// leaves every poll asking Telegram to hold longer than the client will wait, and each one dies on
// HttpClient.Timeout instead of returning updates. Chat stops answering completely until someone
// restarts the service, and the only evidence is a warning that looks like a network blip.
//
// ClampPollTimeout is the guard, so these are the cases that decide whether the feature survives a
// settings change.
public sealed class TelegramChatPollTimeoutTests
{
    // The configured-at-startup case: client timeout is exactly poll + headroom, so nothing clamps
    // and the long poll runs at its full configured length.
    [Fact]
    public void APollThatFitsTheClientBudget_IsLeftAlone()
    {
        var clientTimeout = TimeSpan.FromSeconds(25 + TelegramChatWorker.PollTimeoutHeadroomSeconds);

        Assert.Equal(25, TelegramChatWorker.ClampPollTimeout(25, clientTimeout));
    }

    // The regression this exists for: PollTimeoutSeconds hot-reloaded upward past what the client
    // will wait for. Without the clamp the poll asks for 120s against a 40s client and every single
    // request times out; with it the poll stays inside the budget and chat keeps working.
    [Fact]
    public void APollRaisedAboveTheClientTimeout_IsClampedInsideIt()
    {
        var clientTimeout = TimeSpan.FromSeconds(40);

        var effective = TelegramChatWorker.ClampPollTimeout(120, clientTimeout);

        Assert.Equal(25, effective);
        Assert.True(effective + TelegramChatWorker.PollTimeoutHeadroomSeconds <= clientTimeout.TotalSeconds);
    }

    // Raising it by even a little still breaks polling, because the headroom is what absorbs
    // Telegram's own response time - so the clamp has to bite before the poll merely *equals* the
    // client timeout, not after.
    [Fact]
    public void APollInsideTheTimeoutButInsideTheHeadroom_IsStillClamped()
    {
        var clientTimeout = TimeSpan.FromSeconds(40);

        Assert.Equal(25, TelegramChatWorker.ClampPollTimeout(30, clientTimeout));
    }

    // A client timeout shorter than the headroom leaves no room for a long poll at all. Falling
    // back to 0 turns getUpdates into an ordinary immediate-return call, which still delivers
    // updates on the next loop iteration - degraded, but not broken.
    [Fact]
    public void AClientTimeoutSmallerThanTheHeadroom_FallsBackToAnImmediatePoll()
    {
        Assert.Equal(0, TelegramChatWorker.ClampPollTimeout(25, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, TelegramChatWorker.ClampPollTimeout(25, TimeSpan.FromSeconds(TelegramChatWorker.PollTimeoutHeadroomSeconds)));
    }

    // Timeout.InfiniteTimeSpan is HttpClient's "wait forever", so there is no budget to clamp
    // against and the configured poll should pass through untouched rather than being read as a
    // negative budget and collapsing to 0.
    [Fact]
    public void AnInfiniteClientTimeout_ImposesNoBudget()
    {
        Assert.Equal(25, TelegramChatWorker.ClampPollTimeout(25, Timeout.InfiniteTimeSpan));
    }

    // Defensive: a negative PollTimeoutSeconds would be appended to the getUpdates query string
    // verbatim, which Telegram rejects as a bad request.
    [Fact]
    public void ANegativeConfiguredPoll_IsFlooredAtZero()
    {
        Assert.Equal(0, TelegramChatWorker.ClampPollTimeout(-1, TimeSpan.FromSeconds(40)));
    }
}
