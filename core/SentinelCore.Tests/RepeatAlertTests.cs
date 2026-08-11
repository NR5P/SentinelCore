using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelCore;
using Xunit;

namespace SentinelCore.Tests;

// The three parts of what happens when one camera fires again within seconds - the case a real
// arrival always produces. Measured against the sequence that prompted this: a truck stopping
// outside the gate at 21:46:35, pulling into the driveway at 21:46:40, and somebody standing at
// the gate at 21:46:52. Three triggers, one arrival, three full analyses queued behind each other
// and three notifications sent.
public sealed class RepeatAlertTests
{
    // --- Queue supersede -----------------------------------------------------------------

    [Fact]
    public async Task Supersede_StandsDownAWaitingAlertFromTheSameCamera()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = NewQueue();

        var first = MakeAlert("gate");
        var second = MakeAlert("gate");

        Assert.Null((await queue.TryEnqueueAsync(first, supersedeWaiting: true, ct)).Superseded);
        var outcome = await queue.TryEnqueueAsync(second, supersedeWaiting: true, ct);

        Assert.True(outcome.Accepted);
        Assert.Equal(first.Id, outcome.Superseded?.Id);

        // The stood-down alert is skipped on the way out, so the processor only ever sees the
        // newer frame - which is the entire point, since analyzing the older one is what used to
        // delay this one by a full analysis.
        Assert.Equal([second.Id], await DrainAsync(queue, ct));
    }

    [Fact]
    public async Task Supersede_LeavesOtherCamerasAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = NewQueue();

        var gate = MakeAlert("gate");
        var side = MakeAlert("side of house");

        await queue.TryEnqueueAsync(gate, supersedeWaiting: true, ct);
        var outcome = await queue.TryEnqueueAsync(side, supersedeWaiting: true, ct);

        Assert.Null(outcome.Superseded);
        Assert.Equal([gate.Id, side.Id], await DrainAsync(queue, ct));
    }

    [Fact]
    public async Task Supersede_DoesNotTouchAnAlertAlreadyBeingAnalyzed()
    {
        // The one case where superseding would be actively harmful: an alert the processor has
        // already dequeued is mid-analysis, and there is nothing to gain by throwing away work
        // that is most of the way done. Only what is still WAITING may be stood down.
        var ct = TestContext.Current.CancellationToken;
        var queue = NewQueue();

        var inProgress = MakeAlert("gate");
        await queue.TryEnqueueAsync(inProgress, supersedeWaiting: true, ct);

        await foreach (var item in queue.ReadAllAsync(ct))
        {
            Assert.Equal(inProgress.Id, item.Id);
            break;
        }

        var outcome = await queue.TryEnqueueAsync(MakeAlert("gate"), supersedeWaiting: true, ct);
        Assert.Null(outcome.Superseded);
    }

    [Fact]
    public async Task Supersede_OffLeavesEveryAlertQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = NewQueue();

        var first = MakeAlert("gate");
        var second = MakeAlert("gate");

        await queue.TryEnqueueAsync(first, supersedeWaiting: false, ct);
        var outcome = await queue.TryEnqueueAsync(second, supersedeWaiting: false, ct);

        Assert.Null(outcome.Superseded);
        Assert.Equal([first.Id, second.Id], await DrainAsync(queue, ct));
    }

    [Fact]
    public async Task Supersede_ClearForgetsWhatWasWaiting()
    {
        // A stale entry surviving a clear would let an alert enqueued afterward stand down one
        // that is no longer in the queue at all.
        var ct = TestContext.Current.CancellationToken;
        var queue = NewQueue();

        await queue.TryEnqueueAsync(MakeAlert("gate"), supersedeWaiting: true, ct);
        queue.Clear();

        var outcome = await queue.TryEnqueueAsync(MakeAlert("gate"), supersedeWaiting: true, ct);
        Assert.Null(outcome.Superseded);
    }

    // --- Notification de-duplication -----------------------------------------------------

    [Fact]
    public void Duplicate_SameSubjectsWithinTheWindowIsARepeat()
    {
        var deduplicator = NewDeduplicator(windowSeconds: 90);
        var at = DateTimeOffset.UtcNow;

        var first = PersonAndTruck();
        var second = PersonAndTruck();

        Assert.Null(deduplicator.FindRecentDuplicate(first, at));
        Assert.Equal(first.Id, deduplicator.FindRecentDuplicate(second, at.AddSeconds(12)));
    }

    [Fact]
    public void Duplicate_APersonAppearingBesideTheTruckIsNotARepeat()
    {
        // The distinction the per-channel category cooldowns could not draw, and the reason this
        // exists: the truck moving from outside the gate to the driveway is the same subject in a
        // different place, but somebody getting out of it is news.
        var deduplicator = NewDeduplicator(windowSeconds: 90);
        var at = DateTimeOffset.UtcNow;

        var truckOutside = TruckOnly();
        var truckInside = TruckOnly();
        var personAppears = PersonAndTruck();

        Assert.Null(deduplicator.FindRecentDuplicate(truckOutside, at));
        Assert.Equal(truckOutside.Id, deduplicator.FindRecentDuplicate(truckInside, at.AddSeconds(5)));
        Assert.Null(deduplicator.FindRecentDuplicate(personAppears, at.AddSeconds(17)));
    }

    [Fact]
    public void Duplicate_PastTheWindowIsNotARepeat()
    {
        var deduplicator = NewDeduplicator(windowSeconds: 90);
        var at = DateTimeOffset.UtcNow;

        Assert.Null(deduplicator.FindRecentDuplicate(PersonAndTruck(), at));
        Assert.Null(deduplicator.FindRecentDuplicate(PersonAndTruck(), at.AddSeconds(91)));
    }

    [Fact]
    public void Duplicate_DifferentCamerasNeverRepeatEachOther()
    {
        var deduplicator = NewDeduplicator(windowSeconds: 90);
        var at = DateTimeOffset.UtcNow;

        var gate = PersonAndTruck();
        var side = PersonAndTruck("side of house");

        Assert.Null(deduplicator.FindRecentDuplicate(gate, at));
        Assert.Null(deduplicator.FindRecentDuplicate(side, at.AddSeconds(3)));
    }

    [Fact]
    public void Duplicate_WindowOfZeroDisablesTheCheckEntirely()
    {
        var deduplicator = NewDeduplicator(windowSeconds: 0);
        var at = DateTimeOffset.UtcNow;

        Assert.Null(deduplicator.FindRecentDuplicate(PersonAndTruck(), at));
        Assert.Null(deduplicator.FindRecentDuplicate(PersonAndTruck(), at.AddSeconds(1)));
    }

    [Fact]
    public void Duplicate_AGateCheckIsNeverARepeat()
    {
        // GateCheckDecision only recommends on a state transition, and a gate-open check has
        // nobody in frame by construction - so the subject signature would call every one of them
        // a repeat of the last.
        var deduplicator = NewDeduplicator(windowSeconds: 90);
        var at = DateTimeOffset.UtcNow;

        var first = GateCheck();
        Assert.Null(deduplicator.FindRecentDuplicate(first, at));
        Assert.Null(deduplicator.FindRecentDuplicate(GateCheck(), at.AddSeconds(2)));
    }

    [Fact]
    public void Duplicate_SuppressesEveryChannelThroughShouldNotify()
    {
        // Every notifying action gates on ShouldNotify, so setting this is what makes the
        // suppression reach email, Telegram, push, Discord, MQTT, HomeKit and automation rules
        // without each of them knowing about it.
        var result = PersonAndTruck();
        Assert.True(AlertNotificationPolicy.ShouldNotify(result));

        result.DuplicateOfAlertId = Guid.NewGuid();
        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void Duplicate_StillCountsAsAConfirmedSubjectForTheTriggersTab()
    {
        // The split this feature depends on: a repeat is not worth a second notification and is
        // absolutely worth keeping the footage of, so trigger confirmation must not inherit the
        // suppression - see AlertNotificationPolicy's own note on AnalysisFoundSubject.
        var result = PersonAndTruck();
        result.DuplicateOfAlertId = Guid.NewGuid();

        Assert.True(AlertNotificationPolicy.AnalysisFoundSubject(result));
    }

    // --- Helpers -------------------------------------------------------------------------

    private static AlertQueue NewQueue() =>
        new(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", QueueCapacity = 10 }));

    private static NotificationDeduplicator NewDeduplicator(int windowSeconds) =>
        new(new StaticOptionsMonitor(new RepeatAlertOptions { DuplicateNotificationWindowSeconds = windowSeconds }),
            NullLogger<NotificationDeduplicator>.Instance);

    private static IncomingAlert MakeAlert(string camera) => new(
        Guid.NewGuid(), camera, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "gate", "image/jpeg", "shot.jpg", [1, 2, 3]);

    // Reads the way AlertProcessor does - through ReadAllAsync, which is where a stood-down alert
    // is skipped. Clear() deliberately does not skip, so it cannot be used to observe this.
    // Bounded by a timeout because the writer is never completed and the reader would otherwise
    // wait forever on an empty queue.
    private static async Task<List<Guid>> DrainAsync(AlertQueue queue, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var seen = new List<Guid>();
        try
        {
            await foreach (var alert in queue.ReadAllAsync(timeout.Token))
            {
                seen.Add(alert.Id);

                // Counted down before the yield, so nothing left to read means this was the last.
                if (queue.ApproximateCount == 0) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing more arrived, which for these tests is the end of the queue.
        }

        return seen;
    }

    private static AlertResult TruckOnly() => new()
    {
        Id = Guid.NewGuid(),
        Camera = "gate camera front door",
        Profile = "gate",
        Status = "completed",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Confidence = 0.85,
            Vehicle = new VehicleAnalysis { Visible = true, Location = "outside_gate_entrance", Activity = "stopped" },
            Summary = "A red pickup truck is parked in the narrow entrance outside the gate."
        }
    };

    private static AlertResult PersonAndTruck(string camera = "gate camera front door") => new()
    {
        Id = Guid.NewGuid(),
        Camera = camera,
        Profile = "gate",
        Status = "completed",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Confidence = 0.85,
            Person = new PersonAnalysis { Visible = true, Location = "inside_driveway", Activity = "standing" },
            Vehicle = new VehicleAnalysis { Visible = true, Location = "inside_driveway", Activity = "parked" },
            Summary = "A red pickup truck is parked in the driveway near the gate."
        }
    };

    private static AlertResult GateCheck() => new()
    {
        Id = Guid.NewGuid(),
        Camera = "gate camera front door",
        Profile = GateCheckDecision.ProfileName,
        Status = "completed",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Confidence = 0.9,
            Gate = new GateAnalysis { State = "open" },
            Summary = "The gate is open."
        }
    };

    private sealed class StaticOptionsMonitor(RepeatAlertOptions value) : IOptionsMonitor<RepeatAlertOptions>
    {
        public RepeatAlertOptions CurrentValue { get; } = value;
        public RepeatAlertOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RepeatAlertOptions, string?> listener) => null;
    }
}
