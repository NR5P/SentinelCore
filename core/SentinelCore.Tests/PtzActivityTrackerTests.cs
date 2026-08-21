using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The rule these pin down is "a PTZ camera only detects while it is settled at home", and every
// case below is one of the ways that used to go wrong - or would have, given the chance. A pan
// changes every pixel in the frame, which is orders of magnitude past any zone's minimum object
// size, so anything this gets wrong shows up as a trigger and an AI call on blurred scenery.
public class PtzActivityTrackerTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (PtzActivityTracker Tracker, TestClock Clock) Build()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        return (new PtzActivityTracker(clock), clock);
    }

    [Fact]
    public void ACameraNobodyHasDrivenIsNotAssumedToBeHome()
    {
        var (tracker, _) = Build();

        // Unknown is not Home. Guessing would be cheap and wrong: the cost of guessing is a zone
        // quietly watching whatever the camera happens to be pointed at, which never errors and is
        // only noticed when something is missed.
        Assert.False(tracker.IsSettledAtHome(Guid.NewGuid(), Settle, out var reason));
        Assert.Contains("not been sent home", reason);
    }

    [Fact]
    public void DetectionStopsTheMomentAMoveStartsAndStaysOffUntilItIsBackHome()
    {
        var (tracker, clock) = Build();
        var camera = Guid.NewGuid();

        tracker.RecordHomeReached(camera);
        clock.Advance(Settle + TimeSpan.FromSeconds(1));
        Assert.True(tracker.IsSettledAtHome(camera, Settle, out _));

        tracker.RecordMoveStarted(camera);
        Assert.False(tracker.IsSettledAtHome(camera, Settle, out var moving));
        Assert.Contains("moving", moving);

        // Releasing the button stops the camera but does not bring it home - this is the state the
        // whole feature exists for, a camera parked somewhere its trigger zones no longer describe.
        tracker.RecordStopped(camera);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(tracker.IsSettledAtHome(camera, Settle, out var away));
        Assert.Contains("not at its home position", away);

        tracker.RecordHomeReached(camera);
        clock.Advance(Settle + TimeSpan.FromSeconds(1));
        Assert.True(tracker.IsSettledAtHome(camera, Settle, out _));
    }

    [Fact]
    public void ArrivingHomeDoesNotResumeDetectionUntilThePictureHasSettled()
    {
        var (tracker, clock) = Build();
        var camera = Guid.NewGuid();

        tracker.RecordHomeReached(camera);

        // These cameras overshoot slightly and coast, so frames taken immediately after the stop
        // are still full-frame motion. Resuming on arrival trades a trigger per move for a trigger
        // per arrival, which is not a fix.
        Assert.False(tracker.IsSettledAtHome(camera, Settle, out var settling));
        Assert.Contains("settling", settling);

        clock.Advance(Settle);
        Assert.True(tracker.IsSettledAtHome(camera, Settle, out _));
    }

    [Fact]
    public void AnIdleCameraIsSentHomeButOneStillBeingDrivenIsLeftAlone()
    {
        var (tracker, clock) = Build();
        var camera = Guid.NewGuid();

        tracker.RecordMoveStarted(camera);
        // Mid-press. Homing here would fight the person holding the button.
        Assert.False(tracker.ShouldSendHome(camera, Idle));

        tracker.RecordStopped(camera);
        Assert.False(tracker.ShouldSendHome(camera, Idle));

        clock.Advance(Idle - TimeSpan.FromSeconds(1));
        Assert.False(tracker.ShouldSendHome(camera, Idle));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(tracker.ShouldSendHome(camera, Idle));

        // And once it is home there is nothing left to do, so the worker stops moving it every pass.
        tracker.RecordHomeReached(camera);
        clock.Advance(Idle * 2);
        Assert.False(tracker.ShouldSendHome(camera, Idle));
    }

    [Fact]
    public void ACameraOfUnknownPositionIsSentHomeImmediatelyRatherThanAfterAnIdlePeriod()
    {
        var (tracker, _) = Build();

        // Nobody has driven it, so there is nothing to interrupt - and waiting the full idle period
        // would leave detection off for that long after every server restart.
        Assert.True(tracker.ShouldSendHome(Guid.NewGuid(), Idle));
    }

    [Fact]
    public void AHomeCommandTheCameraRefusesDoesNotWedgeDetectionOffForever()
    {
        var (tracker, clock) = Build();
        var camera = Guid.NewGuid();

        tracker.RecordHomingStarted(camera);
        Assert.False(tracker.IsSettledAtHome(camera, Settle, out _));

        // Without this the moving flag would never clear, and "moving" suppresses detection - so a
        // camera that refused one home command would stay blind until the server restarted.
        tracker.RecordHomingFailed(camera);
        clock.Advance(Idle);
        Assert.True(tracker.ShouldSendHome(camera, Idle));
    }
}
