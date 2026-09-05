using Microsoft.Data.Sqlite;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Fail-open used to be permanent. A trigger the AI never got to judge is written Confirmed - the
// right call at the moment the verdict is missing - but Confirmed is what retention treats as
// "keep forever", so a camera misfiring into a saturated analysis queue kept every clip it made at
// exactly the moment it was producing the most worthless video. Measured 2026-09-03: 89 triggers in
// 40 minutes were auto-confirmed unanalysed; an earlier two-hour storm cost 1.88 GB.
//
// The Judged column separates the two meanings of Confirmed so retention can tell them apart. Only
// the clip is released - the row and the snapshot survive, because unlike a rejection there is no
// verdict saying there was nothing there.
public sealed class TriggerUnjudgedRetentionTests
{
    private static SqliteTriggerStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"trigunj-{Guid.NewGuid():N}");
        return new SqliteTriggerStore(root);
    }

    private static readonly DateTimeOffset Cutoff = DateTimeOffset.UtcNow.AddDays(-30);

    // AddAsync stamps OccurredAt as "now", so an aged row has to be backdated with raw SQL.
    private static async Task<TriggerEvent> AgedAsync(
        SqliteTriggerStore store, string root, Guid cameraId, string clipPath,
        TriggerConfirmation confirmation, bool judged, int daysOld = 60)
    {
        var e = await store.AddAsync(cameraId, $"{Guid.NewGuid():N}.jpg", 0.1, null, TriggerConfirmation.Pending, CancellationToken.None);
        await store.SetClipPathAsync(e.Id, clipPath, CancellationToken.None);
        await store.SetConfirmationAsync(e.Id, confirmation, judged, CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "triggers.db")}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TriggerEvents SET OccurredAt = $at WHERE Id = $id;";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.AddDays(-daysOld).ToString("O"));
        command.Parameters.AddWithValue("$id", e.Id.ToString("N"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return e;
    }

    private static string MakeClip(string root, string name)
    {
        var dir = Path.Combine(root, "clips");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "video");
        return path;
    }

    // --- What it releases -----------------------------------------------------------------------

    [Fact]
    public async Task AnAgedUnjudgedClip_IsReleased_ButItsRowAndSnapshotSurvive()
    {
        using var store = NewStore(out var root);
        try
        {
            var clip = MakeClip(root, "unjudged.mp4");
            var e = await AgedAsync(store, root, Guid.NewGuid(), clip, TriggerConfirmation.Confirmed, judged: false);

            var released = await ((ITriggerEventStore)store).ReleaseUnjudgedClipsBeforeAsync(Cutoff, CancellationToken.None);

            Assert.Equal([clip], released);

            // The row is the whole point of keeping it gentler than the rejected sweep: nothing
            // judged this frame, so nothing has established there was nothing in it.
            var stored = await ((ITriggerEventStore)store).GetAsync(e.Id, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(TriggerConfirmation.Confirmed, stored.Confirmation);
            Assert.False(stored.Judged);
            Assert.NotNull(stored.ImagePath);

            // Cleared so nothing renders a dead video link once the caller unlinks the file.
            Assert.Null(stored.ClipPath);
        }
        finally { TempDirectories.Delete(root); }
    }

    // --- The three things it must not touch -------------------------------------------------------

    [Fact]
    public async Task AGenuinelyConfirmedClip_IsNeverReleased()
    {
        // The AI looked and found a subject. This is the footage the whole system exists to keep,
        // and no age makes it collectable.
        using var store = NewStore(out var root);
        try
        {
            var clip = MakeClip(root, "real.mp4");
            await AgedAsync(store, root, Guid.NewGuid(), clip, TriggerConfirmation.Confirmed, judged: true);

            Assert.Empty(await ((ITriggerEventStore)store).ReleaseUnjudgedClipsBeforeAsync(Cutoff, CancellationToken.None));
            Assert.True(File.Exists(clip));
        }
        finally { TempDirectories.Delete(root); }
    }

    [Fact]
    public async Task AClipSharedWithAJudgedTrigger_StaysWhole()
    {
        // One clip routinely covers several triggers. Three confirmed gate clips were destroyed on
        // 2026-08-15 by a sweep that trusted row ownership alone.
        using var store = NewStore(out var root);
        try
        {
            var cameraId = Guid.NewGuid();
            var clip = MakeClip(root, "shared.mp4");
            await AgedAsync(store, root, cameraId, clip, TriggerConfirmation.Confirmed, judged: false);
            await AgedAsync(store, root, cameraId, clip, TriggerConfirmation.Confirmed, judged: true);

            Assert.Empty(await ((ITriggerEventStore)store).ReleaseUnjudgedClipsBeforeAsync(Cutoff, CancellationToken.None));
            Assert.True(File.Exists(clip));
        }
        finally { TempDirectories.Delete(root); }
    }

    [Fact]
    public async Task AClipSomebodyRecordedOnPurpose_IsNeverReleased()
    {
        // KeepClip is "somebody asked for this video", and the rejected sweep already honours it.
        using var store = NewStore(out var root);
        try
        {
            var clip = MakeClip(root, "manual.mp4");
            var e = await AgedAsync(store, root, Guid.NewGuid(), clip, TriggerConfirmation.Confirmed, judged: false);
            await ((ITriggerEventStore)store).ProtectClipAsync(e.Id, CancellationToken.None);

            Assert.Empty(await ((ITriggerEventStore)store).ReleaseUnjudgedClipsBeforeAsync(Cutoff, CancellationToken.None));
            Assert.True(File.Exists(clip));
        }
        finally { TempDirectories.Delete(root); }
    }

    [Fact]
    public async Task AnUnjudgedClipInsideTheWindow_IsLeftAlone()
    {
        // The window is what makes the rejects reviewable while they still say something about the
        // current settings. Same deal here.
        using var store = NewStore(out var root);
        try
        {
            var clip = MakeClip(root, "recent.mp4");
            await AgedAsync(store, root, Guid.NewGuid(), clip, TriggerConfirmation.Confirmed, judged: false, daysOld: 1);

            Assert.Empty(await ((ITriggerEventStore)store).ReleaseUnjudgedClipsBeforeAsync(Cutoff, CancellationToken.None));
            Assert.True(File.Exists(clip));
        }
        finally { TempDirectories.Delete(root); }
    }

    [Fact]
    public async Task APendingTriggerSharingTheClip_HoldsItUntilItsVerdictLands()
    {
        using var store = NewStore(out var root);
        try
        {
            var cameraId = Guid.NewGuid();
            var clip = MakeClip(root, "pending.mp4");
            await AgedAsync(store, root, cameraId, clip, TriggerConfirmation.Confirmed, judged: false);

            var stillWaiting = await store.AddAsync(
                cameraId, $"{Guid.NewGuid():N}.jpg", 0.1, null, TriggerConfirmation.Pending, CancellationToken.None);
            await store.SetClipPathAsync(stillWaiting.Id, clip, CancellationToken.None);

            Assert.Empty(await ((ITriggerEventStore)store).ReleaseUnjudgedClipsBeforeAsync(Cutoff, CancellationToken.None));
            Assert.True(File.Exists(clip));
        }
        finally { TempDirectories.Delete(root); }
    }

    // --- Migration --------------------------------------------------------------------------------

    [Fact]
    public async Task RowsFromBeforeThisColumn_ReadAsJudged()
    {
        // The load-bearing default. Assuming history was unjudged would hand the first sweep after
        // the upgrade a licence to delete the clips of every camera with AI switched off - rows
        // nothing was ever going to judge and nobody asked to have swept.
        var root = Path.Combine(Path.GetTempPath(), $"trigunj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var legacyId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "triggers.db")}"))
        {
            await connection.OpenAsync(CancellationToken.None);
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
            command.Parameters.AddWithValue("$cameraId", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.AddDays(-90).ToString("O"));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        try
        {
            using var store = new SqliteTriggerStore(root);
            var migrated = await ((ITriggerEventStore)store).GetAsync(legacyId, CancellationToken.None);

            Assert.NotNull(migrated);
            Assert.True(migrated.Judged);
            Assert.Empty(await ((ITriggerEventStore)store).ReleaseUnjudgedClipsBeforeAsync(Cutoff, CancellationToken.None));
        }
        finally { TempDirectories.Delete(root); }
    }
}
