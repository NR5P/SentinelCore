using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The rule for when a webcam device's absence is reported, checked against a clock the test
// holds. What matters: a device that was never started is never "missing", a brief gap inside
// the grace period is not an outage, one outage is one alert however long it lasts, and coming
// back is reported exactly once.
public sealed class PublisherPresenceTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
    private readonly Guid _camera = Guid.NewGuid();

    [Fact]
    public void ACameraNeverSeenLive_IsNeverReportedMissing()
    {
        var presence = new PublisherPresence(Grace);

        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: false, T0));
        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: false, T0.AddHours(3)));

        var status = presence.Get(_camera);
        Assert.NotNull(status);
        Assert.Null(status.LastLiveAt);
        Assert.False(status.Alerted);
    }

    [Fact]
    public void AGapShorterThanTheGracePeriod_IsNotAnOutage()
    {
        var presence = new PublisherPresence(Grace);
        presence.Observe(_camera, live: true, T0);

        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: false, T0.AddSeconds(30)));
        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: false, T0.AddSeconds(90)));
        // Back before the grace period ran out: nothing was ever reported, so nothing recovers.
        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: true, T0.AddSeconds(100)));
        Assert.Null(presence.Get(_camera)!.MissingSince);
    }

    [Fact]
    public void AnOutage_IsReportedOnce_AndRecoveryOnce()
    {
        var presence = new PublisherPresence(Grace);
        presence.Observe(_camera, live: true, T0);

        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: false, T0.AddSeconds(30)));
        Assert.Equal(PublisherPresence.Transition.Dropped, presence.Observe(_camera, live: false, T0.AddSeconds(30 + 120)));
        // Still gone an hour later: the owner has already been told.
        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: false, T0.AddHours(1)));

        var missing = presence.Get(_camera)!;
        Assert.Equal(T0, missing.LastLiveAt);
        Assert.Equal(T0.AddSeconds(30), missing.MissingSince);
        Assert.True(missing.Alerted);

        Assert.Equal(PublisherPresence.Transition.Recovered, presence.Observe(_camera, live: true, T0.AddHours(2)));
        Assert.Equal(PublisherPresence.Transition.None, presence.Observe(_camera, live: true, T0.AddHours(2).AddSeconds(30)));

        var back = presence.Get(_camera)!;
        Assert.True(back.Live);
        Assert.False(back.Alerted);
        Assert.Null(back.MissingSince);
        Assert.Equal(T0.AddHours(2).AddSeconds(30), back.LastLiveAt);
    }

    [Fact]
    public void ASecondOutage_IsReportedAgain()
    {
        var presence = new PublisherPresence(Grace);
        presence.Observe(_camera, live: true, T0);
        presence.Observe(_camera, live: false, T0.AddMinutes(1));
        Assert.Equal(PublisherPresence.Transition.Dropped, presence.Observe(_camera, live: false, T0.AddMinutes(3)));
        Assert.Equal(PublisherPresence.Transition.Recovered, presence.Observe(_camera, live: true, T0.AddMinutes(4)));

        presence.Observe(_camera, live: false, T0.AddMinutes(10));
        Assert.Equal(PublisherPresence.Transition.Dropped, presence.Observe(_camera, live: false, T0.AddMinutes(12)));
    }

    [Fact]
    public void ForgettingACamera_DropsItFromWhatIsKnown()
    {
        var presence = new PublisherPresence(Grace);
        presence.Observe(_camera, live: true, T0);
        Assert.Contains(_camera, presence.Known());

        presence.Forget(_camera);

        Assert.DoesNotContain(_camera, presence.Known());
        Assert.Null(presence.Get(_camera));
    }
}
