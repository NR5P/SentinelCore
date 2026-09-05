using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The AI-confirmed triggers filter: motion alone puts a trigger in the database, but with
// TriggerConfirmation on it stays out of the Triggers tab until the alert pipeline's analysis
// agrees something was actually in frame. See TriggerConfirmationService.
public sealed class TriggerConfirmationTests
{
    private static SqliteTriggerStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        return new SqliteTriggerStore(root);
    }

    private static Task<TriggerEvent> AddAsync(SqliteTriggerStore store, Guid cameraId, TriggerConfirmation confirmation) =>
        store.AddAsync(cameraId, $"{Guid.NewGuid():N}.jpg", 0.1, null, confirmation, CancellationToken.None);

    // --- What the Triggers tab actually lists -------------------------------------------------

    [Fact]
    public async Task GetRecentAsync_ByDefault_ListsOnlyConfirmedTriggers()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        var confirmed = await AddAsync(store, cameraId, TriggerConfirmation.Confirmed);
        await AddAsync(store, cameraId, TriggerConfirmation.Rejected);
        await AddAsync(store, cameraId, TriggerConfirmation.Pending);

        var listed = await store.GetRecentAsync(50, null, includeUnconfirmed: false, CancellationToken.None);

        Assert.Single(listed);
        Assert.Equal(confirmed.Id, listed[0].Id);
    }

    // Pending is hidden as well as rejected - showing a trigger and then yanking it away when the
    // verdict lands would be worse than the wait.
    [Fact]
    public async Task GetRecentAsync_IncludeUnconfirmed_ListsRejectedAndPendingToo()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        await AddAsync(store, cameraId, TriggerConfirmation.Confirmed);
        await AddAsync(store, cameraId, TriggerConfirmation.Rejected);
        await AddAsync(store, cameraId, TriggerConfirmation.Pending);

        var listed = await store.GetRecentAsync(50, null, includeUnconfirmed: true, CancellationToken.None);

        Assert.Equal(3, listed.Count);
        Assert.Contains(listed, e => e.Confirmation == TriggerConfirmation.Rejected);
        Assert.Contains(listed, e => e.Confirmation == TriggerConfirmation.Pending);
    }

    // The camera filter and the confirmation filter have to compose - they're built into one
    // WHERE clause, and an AND that came out as an OR would leak other cameras' triggers.
    [Fact]
    public async Task GetRecentAsync_CameraFilterAndConfirmationFilterApplyTogether()
    {
        using var store = NewStore(out _);
        var wanted = Guid.NewGuid();
        var other = Guid.NewGuid();

        var wantedConfirmed = await AddAsync(store, wanted, TriggerConfirmation.Confirmed);
        await AddAsync(store, wanted, TriggerConfirmation.Rejected);
        await AddAsync(store, other, TriggerConfirmation.Confirmed);

        var listed = await store.GetRecentAsync(50, wanted, includeUnconfirmed: false, CancellationToken.None);

        Assert.Single(listed);
        Assert.Equal(wantedConfirmed.Id, listed[0].Id);
    }

    // The day timeline follows the same rule as the tab: a rejected trigger shouldn't clutter it.
    [Fact]
    public async Task GetEventsInRangeAsync_ExcludesRejectedTriggers()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var confirmed = await AddAsync(store, cameraId, TriggerConfirmation.Confirmed);
        await AddAsync(store, cameraId, TriggerConfirmation.Rejected);

        var listed = await store.GetEventsInRangeAsync(
            cameraId, now.AddMinutes(-5), now.AddMinutes(5), CancellationToken.None);

        Assert.Single(listed);
        Assert.Equal(confirmed.Id, listed[0].Id);
    }

    // --- The verdict landing ------------------------------------------------------------------

    [Fact]
    public async Task SetConfirmationAsync_MovesAPendingTriggerIntoTheList()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();
        var trigger = await AddAsync(store, cameraId, TriggerConfirmation.Pending);

        Assert.Empty(await store.GetRecentAsync(50, null, includeUnconfirmed: false, CancellationToken.None));

        await store.SetConfirmationAsync(trigger.Id, TriggerConfirmation.Confirmed, judged: true, CancellationToken.None);

        var listed = await store.GetRecentAsync(50, null, includeUnconfirmed: false, CancellationToken.None);
        Assert.Single(listed);
        Assert.Equal(TriggerConfirmation.Confirmed, listed[0].Confirmation);
    }

    [Fact]
    public async Task SetConfirmationAsync_RejectingKeepsTheRowAndItsImageOnDisk()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();
        var trigger = await AddAsync(store, cameraId, TriggerConfirmation.Pending);

        await store.SetConfirmationAsync(trigger.Id, TriggerConfirmation.Rejected, judged: true, CancellationToken.None);

        // Still fetchable by id - /api/triggers/{id}/image and /clip keep working for anything
        // "show hidden" lists.
        var fetched = await ((ITriggerEventStore)store).GetAsync(trigger.Id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(TriggerConfirmation.Rejected, fetched!.Confirmation);
        Assert.Equal(trigger.ImagePath, fetched.ImagePath);
    }

    // --- Throwing away video of nothing -------------------------------------------------------
    //
    // The store half of "delete the clip the AI found nothing in" (see
    // TriggerConfirmationService.DiscardClipIfRejectedAsync for the policy and the unlink). Every
    // one of these is about the same hazard: a clip covers a span of time, so it belongs to every
    // trigger that fired inside it, and handing back a file a confirmed trigger still points at
    // destroys real footage silently - the row keeps its path and only renders as a dead video
    // link. That has happened here once already, which is why DeleteUnconfirmedBeforeAsync guards
    // the same way.

    private static async Task<TriggerEvent> AddWithClipAsync(
        SqliteTriggerStore store, Guid cameraId, TriggerConfirmation confirmation, string clipPath)
    {
        var trigger = await AddAsync(store, cameraId, confirmation);
        await store.SetClipPathAsync(trigger.Id, clipPath, CancellationToken.None);
        return trigger;
    }

    [Fact]
    public async Task ReleaseRejectedClipAsync_HandsBackTheClip_AndClearsEveryRejectedRowPointingAtIt()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();
        var clip = "/clips/one.mp4";

        // Two triggers that fired inside the same clip window, both rejected - the wind blowing a
        // branch twice, which is exactly what this feature is for.
        var first = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Rejected, clip);
        var second = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Rejected, clip);

        Assert.Equal(clip, await store.ReleaseRejectedClipAsync(first.Id, CancellationToken.None));

        // Both rows let go of it, or the survivor renders as a dead video link.
        var events = (ITriggerEventStore)store;
        Assert.Null((await events.GetAsync(first.Id, CancellationToken.None))!.ClipPath);
        Assert.Null((await events.GetAsync(second.Id, CancellationToken.None))!.ClipPath);

        // And the file is handed out exactly once, however many rows shared it.
        Assert.Null(await store.ReleaseRejectedClipAsync(second.Id, CancellationToken.None));
    }

    // The snapshot is the whole point of keeping the row: "show hidden" is how you tell a zone
    // firing on a spider web from one that is tuned correctly, and that needs the picture.
    [Fact]
    public async Task ReleaseRejectedClipAsync_LeavesTheRowAndItsSnapshotAlone()
    {
        using var store = NewStore(out _);
        var trigger = await AddWithClipAsync(store, Guid.NewGuid(), TriggerConfirmation.Rejected, "/clips/one.mp4");

        await store.ReleaseRejectedClipAsync(trigger.Id, CancellationToken.None);

        var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(TriggerConfirmation.Rejected, stored!.Confirmation);
        Assert.Equal(trigger.ImagePath, stored.ImagePath);
    }

    // The one file this must never hand over.
    [Fact]
    public async Task ReleaseRejectedClipAsync_KeepsAClipAConfirmedTriggerShares()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();
        var clip = "/clips/arrival.mp4";

        var rejected = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Rejected, clip);
        var confirmed = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Confirmed, clip);

        Assert.Null(await store.ReleaseRejectedClipAsync(rejected.Id, CancellationToken.None));

        var stored = await ((ITriggerEventStore)store).GetAsync(confirmed.Id, CancellationToken.None);
        Assert.Equal(clip, stored!.ClipPath);
    }

    // A sibling still waiting on its verdict holds the file too. Not forever: if that verdict also
    // comes back rejected it arrives here in turn, and by then nothing is holding it.
    [Fact]
    public async Task ReleaseRejectedClipAsync_WaitsForAPendingSibling_ThenCollectsTheClip()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();
        var clip = "/clips/wind.mp4";

        var rejected = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Rejected, clip);
        var pending = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Pending, clip);

        Assert.Null(await store.ReleaseRejectedClipAsync(rejected.Id, CancellationToken.None));

        await store.SetConfirmationAsync(pending.Id, TriggerConfirmation.Rejected, judged: true, CancellationToken.None);

        Assert.Equal(clip, await store.ReleaseRejectedClipAsync(pending.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseRejectedClipAsync_IgnoresATriggerThatWasNotRejected()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        var confirmed = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Confirmed, "/clips/a.mp4");
        var pending = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Pending, "/clips/b.mp4");

        Assert.Null(await store.ReleaseRejectedClipAsync(confirmed.Id, CancellationToken.None));
        Assert.Null(await store.ReleaseRejectedClipAsync(pending.Id, CancellationToken.None));
    }

    // The record button. A person asked for this video, and the AI having a different opinion about
    // what is in frame is not a reason to delete what they asked for - the verdict still decides
    // whether it is SHOWN, which is the reversible half.
    [Fact]
    public async Task ReleaseRejectedClipAsync_NeverTakesAClipSomebodyRecordedOnPurpose()
    {
        using var store = NewStore(out _);
        var trigger = await AddAsync(store, Guid.NewGuid(), TriggerConfirmation.Rejected);
        await store.ProtectClipAsync(trigger.Id, CancellationToken.None);
        await store.SetClipPathAsync(trigger.Id, "/clips/pressed-record.mp4", CancellationToken.None);

        Assert.Null(await store.ReleaseRejectedClipAsync(trigger.Id, CancellationToken.None));

        var stored = await ((ITriggerEventStore)store).GetAsync(trigger.Id, CancellationToken.None);
        Assert.Equal("/clips/pressed-record.mp4", stored!.ClipPath);
    }

    // And it protects the file, not just the row: a rejected motion trigger that happens to share
    // the clip can't collect it either.
    [Fact]
    public async Task ReleaseRejectedClipAsync_KeepsAClipADeliberateRecordingShares()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();
        var clip = "/clips/shared-with-a-press.mp4";

        var pressed = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Rejected, clip);
        await store.ProtectClipAsync(pressed.Id, CancellationToken.None);
        var motion = await AddWithClipAsync(store, cameraId, TriggerConfirmation.Rejected, clip);

        Assert.Null(await store.ReleaseRejectedClipAsync(motion.Id, CancellationToken.None));
        Assert.Equal(clip, (await ((ITriggerEventStore)store).GetAsync(pressed.Id, CancellationToken.None))!.ClipPath);
    }

    // The ordinary order of events: the analysis answers while the post-roll is still running, so
    // at the moment of rejection there is no clip yet. The recorder asks again once it has cut one
    // - that second ask is the one that does the work, and it has to still find the file releasable.
    [Fact]
    public async Task ReleaseRejectedClipAsync_WhenTheVerdictBeatsTheClip_CollectsItOnTheSecondAsk()
    {
        using var store = NewStore(out _);
        var trigger = await AddAsync(store, Guid.NewGuid(), TriggerConfirmation.Rejected);

        Assert.Null(await store.ReleaseRejectedClipAsync(trigger.Id, CancellationToken.None));

        await store.SetClipPathAsync(trigger.Id, "/clips/late.mp4", CancellationToken.None);

        Assert.Equal("/clips/late.mp4", await store.ReleaseRejectedClipAsync(trigger.Id, CancellationToken.None));
    }

    // --- Restart recovery ---------------------------------------------------------------------

    [Fact]
    public async Task GetPendingBeforeAsync_ReturnsOnlyStrandedPendingTriggers()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        var stranded = await AddAsync(store, cameraId, TriggerConfirmation.Pending);
        var confirmed = await AddAsync(store, cameraId, TriggerConfirmation.Confirmed);
        var rejected = await AddAsync(store, cameraId, TriggerConfirmation.Rejected);

        // Everything was just written, so a cutoff in the future catches all of it - what's being
        // checked is that only the Pending row is eligible, not the age arithmetic.
        var ids = await store.GetPendingBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        Assert.Single(ids);
        Assert.Equal(stranded.Id, ids[0]);
        Assert.DoesNotContain(confirmed.Id, ids);
        Assert.DoesNotContain(rejected.Id, ids);
    }

    [Fact]
    public async Task GetPendingBeforeAsync_LeavesAFreshPendingTriggerAlone()
    {
        using var store = NewStore(out _);
        await AddAsync(store, Guid.NewGuid(), TriggerConfirmation.Pending);

        // A poll that's still legitimately waiting on a slow analysis must not be swept out from
        // under itself.
        var ids = await store.GetPendingBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(-10), CancellationToken.None);

        Assert.Empty(ids);
    }

    // --- Upgrading an existing install --------------------------------------------------------

    // The migration's DEFAULT decides what a year of existing trigger history looks like on the
    // first run after upgrading. 'pending' would have blanked the tab.
    [Fact]
    public async Task ExistingTriggersFromBeforeThisFeature_MigrateToConfirmedAndStayVisible()
    {
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cameraId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();

        // A TriggerEvents table as it was before the Confirmation column existed.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "triggers.db")}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE TriggerEvents (
                    Id TEXT PRIMARY KEY,
                    CameraId TEXT NOT NULL,
                    OccurredAt TEXT NOT NULL,
                    ImagePath TEXT NOT NULL,
                    TriggerAreaFraction REAL NOT NULL
                );
                INSERT INTO TriggerEvents (Id, CameraId, OccurredAt, ImagePath, TriggerAreaFraction)
                VALUES ($id, $cameraId, $occurredAt, 'legacy.jpg', 0.2);
                """;
            command.Parameters.AddWithValue("$id", legacyId.ToString("N"));
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));
            command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var store = new SqliteTriggerStore(root);
        var listed = await store.GetRecentAsync(50, null, includeUnconfirmed: false, CancellationToken.None);

        Assert.Single(listed);
        Assert.Equal(legacyId, listed[0].Id);
        Assert.Equal(TriggerConfirmation.Confirmed, listed[0].Confirmation);
    }

    // --- The rule the verdict is made with ----------------------------------------------------

    // TriggerConfirmationService deliberately asks HasVisibleSubject, not ShouldNotify. This is
    // the case that separates them: with the gate closed, a person inside is suppressed as a
    // notification (you don't need a phone buzz for your own family arriving home) but is
    // absolutely a real trigger, and hiding that footage would be a bug.
    [Fact]
    public void SuppressedNotification_WithAPersonInFrame_StillCountsAsARealTrigger()
    {
        var analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Gate = new GateAnalysis { State = "closed" },
            Person = new PersonAnalysis { Visible = true, Location = "inside_driveway" }
        };
        var result = new AlertResult { Profile = "gate", Status = "completed", Analysis = analysis };

        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
        Assert.True(AlertNotificationPolicy.HasVisibleSubject(analysis));
    }

    [Fact]
    public void AnEmptyFrame_IsNotARealTrigger()
    {
        // What a branch in the wind or a moth on the lens analyses to: the model saw nothing.
        Assert.False(AlertNotificationPolicy.HasVisibleSubject(new VisionAnalysis()));
    }

    [Theory]
    [InlineData("person")]
    [InlineData("vehicle")]
    [InlineData("animal")]
    [InlineData("package")]
    public void AnySubjectInFrame_IsARealTrigger(string subject)
    {
        var analysis = subject switch
        {
            "person" => new VisionAnalysis { Person = new PersonAnalysis { Visible = true } },
            "vehicle" => new VisionAnalysis { Vehicle = new VehicleAnalysis { Visible = true } },
            "animal" => new VisionAnalysis { Animal = new AnimalAnalysis { Visible = true } },
            _ => new VisionAnalysis { Package = new PackageAnalysis { Visible = true } }
        };

        Assert.True(AlertNotificationPolicy.HasVisibleSubject(analysis));
    }

    // --- Ageing out rejected motion ------------------------------------------------------------
    //
    // This is the one query in the store that destroys data, and the blast radius of getting the
    // WHERE clause wrong is the entire event history rather than a rendering glitch. Each clause is
    // asserted separately for that reason: what it deletes, and the three things it must not.

    [Fact]
    public async Task DeleteUnconfirmedBeforeAsync_RemovesOnlyRejectedMotionPastTheCutoff()
    {
        using var store = NewStore(out _);
        ITriggerEventStore events = store;
        var cameraId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var oldRejected = await store.AddAsync(
            cameraId, "old-rejected.jpg", 0.1, null, TriggerConfirmation.Rejected,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        var oldConfirmed = await store.AddAsync(
            cameraId, "old-confirmed.jpg", 0.1, null, TriggerConfirmation.Confirmed,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        // Still waiting on a verdict. Age is not enough to delete it - the answer may yet arrive,
        // and the startup sweeper is what resolves the ones a restart orphaned.
        var oldPending = await store.AddAsync(
            cameraId, "old-pending.jpg", 0.1, null, TriggerConfirmation.Pending,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        var recentRejected = await store.AddAsync(
            cameraId, "recent-rejected.jpg", 0.1, null, TriggerConfirmation.Rejected,
            CancellationToken.None, occurredAt: now.AddHours(-1));

        var files = await events.DeleteUnconfirmedBeforeAsync(now.AddDays(-3), CancellationToken.None);

        Assert.Equal(["old-rejected.jpg"], files);

        var remaining = await store.GetRecentAsync(50, null, includeUnconfirmed: true, CancellationToken.None);
        var ids = remaining.Select(e => e.Id).ToList();
        Assert.DoesNotContain(oldRejected.Id, ids);
        Assert.Contains(oldConfirmed.Id, ids);
        Assert.Contains(oldPending.Id, ids);
        Assert.Contains(recentRejected.Id, ids);
    }

    // Two triggers close enough together land inside one clip window and get patched with the same
    // ClipPath. The AI then judges them independently and routinely disagrees, so "this row is
    // rejected" is NOT the same statement as "nothing else needs this file" - and acting as though
    // it were deletes confirmed footage while leaving the confirmed row pointing at the hole.
    // Found in production on 2026-08-15: three confirmed gate events had already lost their clips.
    [Fact]
    public async Task DeleteUnconfirmedBeforeAsync_KeepsAClipAConfirmedRowStillShares()
    {
        using var store = NewStore(out _);
        ITriggerEventStore events = store;
        var cameraId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        const string shared = "/clips/shared.mp4";

        // Same clip, same moment, opposite verdicts - the rejected one is old enough to sweep.
        var rejected = await store.AddAsync(
            cameraId, "rejected.jpg", 0.1, null, TriggerConfirmation.Rejected,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        var confirmed = await store.AddAsync(
            cameraId, "confirmed.jpg", 0.1, null, TriggerConfirmation.Confirmed,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        await store.SetClipPathAsync(rejected.Id, shared, CancellationToken.None);
        await store.SetClipPathAsync(confirmed.Id, shared, CancellationToken.None);

        // A clip owned only by the rejected row, to prove the sweep still reclaims the usual case.
        var soleRejected = await store.AddAsync(
            cameraId, "sole-rejected.jpg", 0.1, null, TriggerConfirmation.Rejected,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        await store.SetClipPathAsync(soleRejected.Id, "/clips/sole.mp4", CancellationToken.None);

        var files = await events.DeleteUnconfirmedBeforeAsync(now.AddDays(-3), CancellationToken.None);

        Assert.DoesNotContain(shared, files);
        Assert.Contains("/clips/sole.mp4", files);
        // The rows still go; it is only the unlinking that has to respect the survivor.
        var ids = (await store.GetRecentAsync(50, null, includeUnconfirmed: true, CancellationToken.None))
            .Select(e => e.Id).ToList();
        Assert.DoesNotContain(rejected.Id, ids);
        Assert.Contains(confirmed.Id, ids);
    }

    // The sweep's own "removed N files" line only ever sees the post-filter list, so a preserved
    // clip looks identical there to one that was never a candidate. Since this guarantee already
    // failed silently in production once, the store says so itself - and that line is the only
    // evidence a later reader gets, so it is worth pinning.
    [Fact]
    public async Task DeleteUnconfirmedBeforeAsync_ReportsTheFilesItHeldBack()
    {
        var logger = new CapturingLogger();
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        using var store = new SqliteTriggerStore(root, logger);
        ITriggerEventStore events = store;
        var cameraId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var rejected = await store.AddAsync(
            cameraId, "rejected.jpg", 0.1, null, TriggerConfirmation.Rejected,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        var confirmed = await store.AddAsync(
            cameraId, "confirmed.jpg", 0.1, null, TriggerConfirmation.Confirmed,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        await store.SetClipPathAsync(rejected.Id, "/clips/shared.mp4", CancellationToken.None);
        await store.SetClipPathAsync(confirmed.Id, "/clips/shared.mp4", CancellationToken.None);

        await events.DeleteUnconfirmedBeforeAsync(now.AddDays(-3), CancellationToken.None);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("kept 1 file(s)", line);
    }

    // ...and stays quiet the rest of the time. A line on every sweep would be noise that stops
    // being read, which is the same failure as not logging it at all.
    [Fact]
    public async Task DeleteUnconfirmedBeforeAsync_SaysNothingWhenNoFileWasShared()
    {
        var logger = new CapturingLogger();
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        using var store = new SqliteTriggerStore(root, logger);
        ITriggerEventStore events = store;
        var now = DateTimeOffset.UtcNow;

        var rejected = await store.AddAsync(
            Guid.NewGuid(), "rejected.jpg", 0.1, null, TriggerConfirmation.Rejected,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        await store.SetClipPathAsync(rejected.Id, "/clips/sole.mp4", CancellationToken.None);

        var files = await events.DeleteUnconfirmedBeforeAsync(now.AddDays(-3), CancellationToken.None);

        Assert.Contains("/clips/sole.mp4", files);
        Assert.Empty(logger.Messages);
    }

    private sealed class CapturingLogger : ILogger<SqliteTriggerStore>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    // Two rejected rows sharing one clip should hand it back once, not once per row - the count
    // goes straight into the "removed N files" log line the sweep is judged by.
    [Fact]
    public async Task DeleteUnconfirmedBeforeAsync_ReturnsASharedClipOnlyOnce()
    {
        using var store = NewStore(out _);
        ITriggerEventStore events = store;
        var cameraId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        const string shared = "/clips/both-rejected.mp4";

        foreach (var name in new[] { "a.jpg", "b.jpg" })
        {
            var row = await store.AddAsync(
                cameraId, name, 0.1, null, TriggerConfirmation.Rejected,
                CancellationToken.None, occurredAt: now.AddDays(-10));
            await store.SetClipPathAsync(row.Id, shared, CancellationToken.None);
        }

        var files = await events.DeleteUnconfirmedBeforeAsync(now.AddDays(-3), CancellationToken.None);

        Assert.Single(files, f => f == shared);
    }

    // --- Orphan sweep ---------------------------------------------------------------------------
    //
    // Files with no row can only be found by diffing the directory against the database, which
    // means "I can't see an owner" is the delete condition - and the two things that legitimately
    // have no owner are a clip still being written and the live snapshot. Both are asserted here
    // because getting either wrong deletes data that is actively in use, silently.

    private static (string, DateTimeOffset) OnDisk(string path, DateTimeOffset lastWrite) => (path, lastWrite);

    [Fact]
    public void SelectOrphans_DeletesUnreferencedFilesPastTheGrace()
    {
        var now = DateTimeOffset.UtcNow;
        var referenced = new HashSet<string>(
            [MotionRetentionWorker.NormalizePath("/data/trigger-images/cam/kept.jpg")],
            StringComparer.Ordinal);

        var orphans = MotionRetentionWorker.SelectOrphans(
            [
                OnDisk("/data/trigger-images/cam/kept.jpg", now.AddHours(-5)),
                OnDisk("/data/trigger-images/cam/orphan.jpg", now.AddHours(-5))
            ],
            referenced,
            now.AddHours(-1));

        Assert.Equal(["/data/trigger-images/cam/orphan.jpg"], orphans);
    }

    // A clip's row is written when motion fires, with ClipPath still null, and patched only once
    // the post-roll has elapsed. So an unreferenced, very recent file is the normal appearance of
    // a clip the recorder is in the middle of writing - deleting it would corrupt live output.
    [Fact]
    public void SelectOrphans_SparesFilesInsideTheGrace_SoClipsBeingWrittenSurvive()
    {
        var now = DateTimeOffset.UtcNow;

        var orphans = MotionRetentionWorker.SelectOrphans(
            [OnDisk("/data/trigger-clips/cam/being-written.mp4", now.AddMinutes(-2))],
            new HashSet<string>(StringComparer.Ordinal),
            now.AddHours(-1));

        Assert.Empty(orphans);
    }

    // Continuously rewritten by ffmpeg and referenced by no row, ever. Its mtime is always recent
    // in practice, but a camera whose zone was switched off leaves a stale one behind - which would
    // then age past the grace and look exactly like an orphan.
    [Fact]
    public void SelectOrphans_NeverDeletesTheLiveSnapshot()
    {
        var now = DateTimeOffset.UtcNow;

        var orphans = MotionRetentionWorker.SelectOrphans(
            [OnDisk("/data/trigger-images/cam/_latest.jpg", now.AddDays(-30))],
            new HashSet<string>(StringComparer.Ordinal),
            now.AddHours(-1));

        Assert.Empty(orphans);
    }

    // The database stores absolute paths and the directory walk produces its own spelling of the
    // same file. If the two don't compare equal, every referenced file reads as unowned and the
    // sweep deletes the entire history in one pass.
    [Fact]
    public void SelectOrphans_MatchesReferencedPathsThroughDifferentSpellings()
    {
        var now = DateTimeOffset.UtcNow;
        var referenced = new HashSet<string>(
            [MotionRetentionWorker.NormalizePath("/data/trigger-images/cam/kept.jpg")],
            StringComparer.Ordinal);

        var orphans = MotionRetentionWorker.SelectOrphans(
            [OnDisk("/data/trigger-images/cam/../cam/kept.jpg", now.AddHours(-5))],
            referenced,
            now.AddHours(-1));

        Assert.Empty(orphans);
    }

    [Fact]
    public async Task GetAllReferencedFilesAsync_ReturnsImagesAndClipsFromEveryVerdict()
    {
        using var store = NewStore(out _);
        ITriggerEventStore events = store;
        var cameraId = Guid.NewGuid();

        var confirmed = await AddAsync(store, cameraId, TriggerConfirmation.Confirmed);
        await events.SetClipPathAsync(confirmed.Id, "confirmed.mp4", CancellationToken.None);
        await AddAsync(store, cameraId, TriggerConfirmation.Rejected);

        var referenced = await events.GetAllReferencedFilesAsync(CancellationToken.None);

        // Both rows' images plus the one clip: a sweep that only looked at confirmed rows would
        // treat every rejected row's still-live image as an orphan.
        Assert.Equal(3, referenced.Count);
        Assert.Contains("confirmed.mp4", referenced);
    }

    // The clip is the reason this exists at all - it is thousands of times the size of the
    // thumbnail, and a sweep that returned only image paths would reclaim almost nothing.
    [Fact]
    public async Task DeleteUnconfirmedBeforeAsync_ReturnsBothTheImageAndTheClip()
    {
        using var store = NewStore(out _);
        ITriggerEventStore events = store;
        var cameraId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var rejected = await store.AddAsync(
            cameraId, "frame.jpg", 0.1, null, TriggerConfirmation.Rejected,
            CancellationToken.None, occurredAt: now.AddDays(-10));
        await events.SetClipPathAsync(rejected.Id, "motion.mp4", CancellationToken.None);

        var files = await events.DeleteUnconfirmedBeforeAsync(now.AddDays(-3), CancellationToken.None);

        Assert.Contains("frame.jpg", files);
        Assert.Contains("motion.mp4", files);
    }
}
