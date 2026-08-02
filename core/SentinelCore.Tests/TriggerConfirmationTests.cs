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
}
