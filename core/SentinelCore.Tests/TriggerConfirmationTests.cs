using Microsoft.Data.Sqlite;
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

        await store.SetConfirmationAsync(trigger.Id, TriggerConfirmation.Confirmed, CancellationToken.None);

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

        await store.SetConfirmationAsync(trigger.Id, TriggerConfirmation.Rejected, CancellationToken.None);

        // Still fetchable by id - /api/triggers/{id}/image and /clip keep working for anything
        // "show hidden" lists.
        var fetched = await ((ITriggerEventStore)store).GetAsync(trigger.Id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(TriggerConfirmation.Rejected, fetched!.Confirmation);
        Assert.Equal(trigger.ImagePath, fetched.ImagePath);
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
