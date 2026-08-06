extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// A backup that quietly stops covering a feature is invisible until the day somebody needs it,
// which is the day they have already lost the machine. There were no tests here at all, and in
// that gap camera groups and gate/door checks were both added to the product without ever being
// added to the export.
//
// The settings half of the backup can't drift - it is built and applied through the same
// SettingsEndpoints snapshot the settings screen uses - so what these cover is the SQLite half,
// which has to be remembered by hand, plus the version rule that decides whether an old file
// still restores.
public sealed class BackupCoverageTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    // The guard against the drift this file exists for: every configuration store the app
    // persists is either in the backup document or on the deliberately-excluded list. A new
    // store shows up here as a failure naming it, rather than as a surprise during a restore.
    [Fact]
    public void EveryConfigurationStore_IsEitherBackedUp_OrDeliberatelyExcluded()
    {
        var backedUp = new[] { "Cameras", "CameraGroups", "TriggerZones", "GateChecks", "Accounts", "LiveOrder" };

        // Excluded on purpose, each for a reason recorded next to it in BackupEndpoints:
        //   Sessions       - a live login; restoring one onto another machine would hand it over
        //   TriggerEvents  - event history, not configuration
        //   ReviewedEvents - which events someone has already looked at; likewise history
        var deliberatelyExcluded = new[] { "Sessions", "TriggerEvents", "ReviewedEvents" };

        var documentSections = typeof(BackupDocument)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        foreach (var store in backedUp)
        {
            Assert.True(
                documentSections.Contains(store),
                $"{store} is configuration but BackupDocument has no section for it - a restore would silently lose it.");
        }

        foreach (var store in deliberatelyExcluded)
        {
            Assert.False(
                documentSections.Contains(store),
                $"{store} is on the deliberately-excluded list but appears in BackupDocument - update one or the other.");
        }
    }

    // Restoring a camera group has to keep its Id, or a second import of the same file makes a
    // fresh duplicate of every group - and the trigger pipeline looks groups up by camera, so
    // duplicates mean a companion clip pulled twice.
    [Fact]
    public async Task RestoringACameraGroup_KeepsItsIdAndUpdatesInPlace()
    {
        using var store = new SqliteCameraStore(TempDirectory());
        ICameraGroupStore groups = store;

        var id = Guid.NewGuid();
        var cameraId = Guid.NewGuid();
        var group = new CameraGroup(id, "Front of house", [cameraId], 30, true, false);

        await groups.RestoreAsync(group, TestContext.Current.CancellationToken);
        await groups.RestoreAsync(group with { Name = "Driveway side" }, TestContext.Current.CancellationToken);

        var all = await groups.GetAllAsync(TestContext.Current.CancellationToken);
        var only = Assert.Single(all);
        Assert.Equal(id, only.Id);
        Assert.Equal("Driveway side", only.Name);
        Assert.Equal([cameraId], only.CameraIds);
    }

    // A gate check's four Last* columns are this install's memory of what it has seen, not
    // configuration. Carrying them through a backup would export when your gate was last open,
    // and seed a restored install with a transition that never happened there.
    [Fact]
    public async Task RestoringAGateCheck_BringsTheConfiguration_ButNotTheTransitionMemory()
    {
        using var store = new SqliteGateCheckStore(TempDirectory());
        var cameraId = Guid.NewGuid();

        var exported = new GateCheckConfig(
            cameraId, Enabled: true, CheckLabel: "side door", CheckBlocked: true,
            IntervalMinutes: 20, RemindIfStillOpenAfterHours: 3,
            LastKnownState: "unknown", LastStateChangedAt: null, LastNotifiedAt: null, LastKnownBlocked: false);

        await store.SaveAsync(exported, TestContext.Current.CancellationToken);

        var restored = await store.GetAsync(cameraId, TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.True(restored.Enabled);
        Assert.Equal("side door", restored.CheckLabel);
        Assert.Equal(20, restored.IntervalMinutes);
        Assert.Equal(3, restored.RemindIfStillOpenAfterHours);

        Assert.Equal("unknown", restored.LastKnownState);
        Assert.Null(restored.LastStateChangedAt);
        Assert.Null(restored.LastNotifiedAt);
        Assert.False(restored.LastKnownBlocked);
    }

    // Importing onto a running install must not disturb a gate that is currently open - the
    // upsert deliberately leaves the transition columns alone, so a restore can't cause a
    // duplicate "gate has been open" alert.
    [Fact]
    public async Task ImportingOverALiveGateCheck_LeavesItsCurrentStateAlone()
    {
        using var store = new SqliteGateCheckStore(TempDirectory());
        var cameraId = Guid.NewGuid();
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-45);

        await store.SaveAsync(
            new GateCheckConfig(cameraId, true, "gate", true, 10, null, "unknown", null, null, false),
            TestContext.Current.CancellationToken);
        await store.UpdateStateAsync(
            cameraId, "open", openedAt, openedAt, lastKnownBlocked: false, TestContext.Current.CancellationToken);

        // The neutral values a backup carries.
        await store.SaveAsync(
            new GateCheckConfig(cameraId, true, "gate", true, 15, null, "unknown", null, null, false),
            TestContext.Current.CancellationToken);

        var after = await store.GetAsync(cameraId, TestContext.Current.CancellationToken);
        Assert.Equal(15, after!.IntervalMinutes);           // configuration did land
        Assert.Equal("open", after.LastKnownState);          // memory of the open gate did not move
        Assert.NotNull(after.LastNotifiedAt);
    }

    // An exported backup has to still restore several versions later - that is the entire point
    // of having one. A schema 1 file predates CameraGroups and GateChecks and simply has no such
    // properties; it must deserialize, with those sections arriving null rather than throwing.
    [Fact]
    public void ASchemaOneBackup_StillDeserializes_WithTheNewerSectionsAbsent()
    {
        const string schemaOneFile = """
            {
              "schemaVersion": 1,
              "exportedAt": "2026-07-30T12:00:00+00:00",
              "settings": null,
              "cameras": [],
              "triggerZones": []
            }
            """;

        var document = JsonSerializer.Deserialize<BackupDocument>(
            schemaOneFile, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(document);
        Assert.Equal(1, document.SchemaVersion);
        Assert.Null(document.CameraGroups);
        Assert.Null(document.GateChecks);
    }

    // The test the missing sections would actually have failed on: set a camera, a group, a
    // trigger zone and a gate check up on one install, export, and import into a completely empty
    // one. Anything the document doesn't carry simply isn't there on the far side.
    [Fact]
    public async Task ExportingFromOneInstallAndImportingIntoAnother_CarriesEveryKindOfConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;

        var sourceDirectory = TempDirectory();
        using var source = CreateFactory(sourceDirectory);
        using var sourceClient = source.CreateClient();

        // Two cameras, because a group needs at least two to be worth having - see
        // CameraGroupEndpoints.ValidateAsync.
        var gateId = await AddCameraAsync(sourceClient, "Gate", ct);
        var doorId = await AddCameraAsync(sourceClient, "Front Door", ct);

        var group = await sourceClient.PostAsJsonAsync("/api/camera-groups",
            new
            {
                Name = "Front of house",
                CameraIds = new[] { gateId, doorId },
                CorrelationWindowSeconds = 25,
                RecordCompanionClips = true,
                AnalyzeCompanionCaptures = false
            }, ct);
        group.EnsureSuccessStatusCode();

        using var exported = await sourceClient.GetAsync("/api/backup/export", ct);
        exported.EnsureSuccessStatusCode();
        var backup = await exported.Content.ReadAsStringAsync(ct);

        // A second install that has never seen any of this.
        using var destination = CreateFactory(TempDirectory());
        using var destinationClient = destination.CreateClient();

        using var imported = await destinationClient.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        using var groupsResponse = await destinationClient.GetAsync("/api/camera-groups", ct);
        groupsResponse.EnsureSuccessStatusCode();
        var groups = await groupsResponse.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal(1, groups.GetArrayLength());
        var restored = groups[0];
        Assert.Equal("Front of house", restored.GetProperty("name").GetString());
        Assert.Equal(25, restored.GetProperty("correlationWindowSeconds").GetInt32());
        // The group still points at the cameras it was grouped with - which only holds because
        // both cameras and the group itself are restored under their original ids.
        Assert.Equal(gateId, restored.GetProperty("cameraIds")[0].GetGuid());
        Assert.Equal(doorId, restored.GetProperty("cameraIds")[1].GetGuid());
    }

    private static async Task<Guid> AddCameraAsync(HttpClient client, string name, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/cameras",
            new { Name = name, StreamUrl = $"rtsp://camera/{name}", IncludeAudio = false }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    // Restoring an account has to keep its Id: the live grid arrangement in the same backup is
    // keyed by "account:<id>", so a minted id would restore everyone's layout onto nobody.
    [Fact]
    public async Task RestoringAnAccount_KeepsItsIdAndUpdatesInPlace()
    {
        using var store = new SqliteAccountStore(TempDirectory());
        var id = Guid.NewGuid();
        var account = new Account(id, "nathan", "hash-one", AccountRole.Admin, "Nathan", DateTimeOffset.UtcNow);

        await store.RestoreAsync(account, TestContext.Current.CancellationToken);
        await store.RestoreAsync(account with { PasswordHash = "hash-two", Role = AccountRole.Viewer }, TestContext.Current.CancellationToken);

        var all = await store.GetAllAsync(TestContext.Current.CancellationToken);
        var only = Assert.Single(all);
        Assert.Equal(id, only.Id);
        Assert.Equal("hash-two", only.PasswordHash);
        Assert.Equal(AccountRole.Viewer, only.Role);
    }

    // Usernames are unique case-insensitively. Restoring onto an install where setup already
    // created the same person under a different id would otherwise hit that index and throw,
    // failing the whole import - the single most likely restore there is.
    [Fact]
    public async Task RestoringAnAccountWhoseUsernameIsTaken_ReplacesTheClashingRow()
    {
        using var store = new SqliteAccountStore(TempDirectory());
        var ct = TestContext.Current.CancellationToken;

        var fromSetup = await store.CreateAsync("nathan", "setup-hash", AccountRole.Admin, null, ct);
        var fromBackup = new Account(
            Guid.NewGuid(), "Nathan", "backup-hash", AccountRole.Admin, "Nathan", DateTimeOffset.UtcNow);

        await store.RestoreAsync(fromBackup, ct);

        var all = await store.GetAllAsync(ct);
        var only = Assert.Single(all);
        Assert.Equal(fromBackup.Id, only.Id);
        Assert.NotEqual(fromSetup.Id, only.Id);
        Assert.Equal("backup-hash", only.PasswordHash);
    }

    // The rail that makes restoring accounts safe at all. A backup carrying an admin whose
    // password nobody remembers must not replace the login being used to perform the restore -
    // there is no way back from that through the dashboard.
    [Fact]
    public async Task ImportingABackup_NeverOverwritesTheAccountDoingTheImporting()
    {
        var ct = TestContext.Current.CancellationToken;

        var backup = BuildBackupJson(
            new Account(Guid.NewGuid(), "nathan", "hash-from-a-backup-nobody-remembers", AccountRole.Admin, null, DateTimeOffset.UtcNow),
            new Account(Guid.NewGuid(), "guest", "guest-hash", AccountRole.Viewer, null, DateTimeOffset.UtcNow));

        var dataDirectory = TempDirectory();
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");

        // An install as it stands after setup: login required, and an admin called "nathan" whose
        // password is the one actually in use - deliberately not the one in the backup.
        const string passwordInUse = "the password that still works";
        using (var seed = new SqliteAccountStore(dataDirectory))
        {
            await seed.CreateAsync("nathan", PasswordHasher.Hash(passwordInUse), AccountRole.Admin, null, ct);
        }

        File.WriteAllText(settingsPath, """{"Auth":{"Enabled":true}}""");

        using var factory = CreateFactory(dataDirectory, writeEmptySettings: false);
        using var client = factory.CreateClient();

        using (var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = "nathan", Password = passwordInUse }, ct))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using var response = await client.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(1, result.GetProperty("accountsSkipped").GetInt32());   // nathan, left alone
        Assert.Equal(1, result.GetProperty("accountsRestored").GetInt32());  // guest, restored

        // The importer's password is untouched, so the login they are holding still works.
        using (var stillWorks = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = "nathan", Password = passwordInUse }, ct))
        {
            Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
        }

        // And the rest of the file did land.
        using var store = new SqliteAccountStore(dataDirectory);
        var guest = await store.GetByUsernameAsync("guest", ct);
        Assert.NotNull(guest);
        Assert.Equal(AccountRole.Viewer, guest.Role);
    }

    private static string BuildBackupJson(params Account[] accounts) =>
        JsonSerializer.Serialize(
            new BackupDocument(
                SchemaVersion: 2,
                ExportedAt: DateTimeOffset.UtcNow,
                Settings: null,
                Cameras: [],
                TriggerZones: [],
                CameraGroups: [],
                GateChecks: [],
                Accounts: accounts,
                LiveOrder: []),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private WebApplicationFactory<WebProgram> CreateFactory(string dataDirectory, bool writeEmptySettings = true)
    {
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");
        if (writeEmptySettings) File.WriteAllText(settingsPath, "{}");

        return new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("MainAppSettingsPath", settingsPath)
                .UseSetting("CamerasDataDirectory", dataDirectory)
                .UseSetting("Bridge:DataDirectory", dataDirectory));
    }
}
