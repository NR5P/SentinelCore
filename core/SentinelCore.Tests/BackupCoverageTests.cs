extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
//
// That first claim was untrue for as long as it took SCHEDULED RECORDING to be added to
// SettingsSchema with no matching property on SettingsSnapshot: the section saved and ran
// perfectly, and every backup taken in that window silently left it out.
// SettingsSchemaTests.EverySchemaSection_HasAPropertyOnTheSnapshotClientsReadItsValuesFrom is
// what makes the claim true now, and the round-trip below is the end-to-end proof for the
// section that broke it.
public sealed class BackupCoverageTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try { TempDirectories.Delete(directory); }
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

    // The guard against the drift this file exists for: every table any SentinelCore service
    // creates is either carried by the backup or on the deliberately-excluded list with its
    // reason. It reads the tables out of the source rather than a list kept here, because a
    // list kept here is exactly how RecordingSchedules and the main app's Devices, AutomationRules
    // and Schedules tables all went unbacked-up: nobody added them to it. A new table now fails
    // this test by name until someone decides which side it belongs on.
    [Fact]
    public void EveryTableAnyServiceCreates_IsEitherBackedUp_OrDeliberatelyExcluded()
    {
        // Table -> where it lives in BackupDocument ("Section" or "Section.Property").
        var backedUp = new Dictionary<string, string>
        {
            ["Cameras"] = "Cameras",
            ["CameraGroups"] = "CameraGroups",
            ["TriggerConfigs"] = "TriggerZones",
            ["GateChecks"] = "GateChecks",
            ["Accounts"] = "Accounts",
            ["LiveOrders"] = "LiveOrder",
            ["RecordingSchedules"] = "RecordingSchedules",
            ["Spaces"] = "Organizer.Spaces",
            ["Items"] = "Organizer.Items",
            ["FeedTokens"] = "Organizer.FeedTokens",
            ["Meta"] = "Organizer.Preferences",
            ["Devices"] = "Hub.Devices",
            ["AutomationRules"] = "Hub.AutomationRules",
            ["Schedules"] = "Hub.Schedules",
            ["Automations"] = "OrganizerAutomations",
            ["AlarmSensors"] = "Alarm.Sensors",
            ["AlarmSettings"] = "Alarm.Config",
        };

        // Excluded on purpose, each for a reason recorded in BackupEndpoints:
        var deliberatelyExcluded = new Dictionary<string, string>
        {
            ["Sessions"] = "a live login; restoring one onto another machine would hand it over",
            ["TriggerEvents"] = "event history, not configuration",
            ["ReviewedEvents"] = "which events someone has looked at; history",
            ["AlertEvents"] = "event history",
            ["Recordings"] = "the list of recorded videos; history, and the videos aren't in the backup",
            ["ScheduleRuns"] = "a schedule's run log; history",
            ["NuisanceAdvisories"] = "suggestions worked out from recent events; regenerated",
            ["PackageWatches"] = "open package watches; runtime state measured in hours",
            ["PushDevices"] = "each app re-registers every time it connects",
            ["Removals"] = "the organizer's sync log; a restored server tells devices to resync instead",
            ["Runs"] = "a board automation's run log; history",
            ["DueFired"] = "which due cards a board automation has already fired for; runtime memory",
            ["AlarmEvents"] = "the alarm's log of arms, disarms and triggers; history",
        };

        var root = RepositoryCoreDirectory();
        var tables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in new[] { "SentinelCore", "SentinelCore.Web" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(file), @"CREATE TABLE IF NOT EXISTS (\w+)"))
                tables.Add(match.Groups[1].Value);
        }

        Assert.Contains("Cameras", tables);
        foreach (var table in tables)
        {
            Assert.True(
                backedUp.ContainsKey(table) || deliberatelyExcluded.ContainsKey(table),
                $"Table {table} is new: nothing says whether it's in the backup. If it holds configuration, add it " +
                "to BackupDocument/BackupService and to the backed-up list here; if it's history or runtime state, " +
                "add it to the excluded list with the reason.");
        }

        foreach (var (table, location) in backedUp)
        {
            var parts = location.Split('.');
            var property = typeof(BackupDocument).GetProperty(parts[0]);
            Assert.True(property is not null, $"{table} is listed as backed up in BackupDocument.{parts[0]}, which doesn't exist.");
            if (parts.Length == 2)
            {
                var inner = Nullable.GetUnderlyingType(property!.PropertyType) ?? property.PropertyType;
                Assert.True(inner.GetProperty(parts[1]) is not null,
                    $"{table} is listed as backed up in {location}, but {inner.Name} has no {parts[1]}.");
            }
        }

        foreach (var table in deliberatelyExcluded.Keys)
            Assert.False(typeof(BackupDocument).GetProperty(table) is not null,
                $"{table} is on the deliberately-excluded list but appears in BackupDocument - update one or the other.");
    }

    private static string RepositoryCoreDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SentinelCore.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Couldn't find core/SentinelCore.slnx above the test output directory.");
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

    // The section that proved the settings half of the backup *could* drift. Worth an end-to-end
    // test of its own rather than trusting the snapshot correspondence alone, because the thing
    // that goes missing here is the free-space floor that stops full-bitrate recordings filling
    // the disk - a setting whose absence is invisible until it matters.
    [Fact]
    public async Task ExportingScheduledRecording_CarriesItToAnInstallThatHasNeverSetIt()
    {
        var ct = TestContext.Current.CancellationToken;

        using var source = CreateFactory(TempDirectory());
        using var sourceClient = source.CreateClient();

        using (var saved = await sourceClient.PutAsJsonAsync("/api/settings/scheduled-recording",
            new { RetentionDays = 3, MinimumFreeGigabytes = 42.0 }, ct))
            saved.EnsureSuccessStatusCode();

        using var exported = await sourceClient.GetAsync("/api/backup/export", ct);
        exported.EnsureSuccessStatusCode();
        var backup = await exported.Content.ReadAsStringAsync(ct);

        using var destination = CreateFactory(TempDirectory());
        using var destinationClient = destination.CreateClient();

        using var imported = await destinationClient.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var restored = (await destinationClient.GetFromJsonAsync<JsonElement>("/api/settings", ct))
            .GetProperty("scheduledRecording");

        Assert.Equal(3, restored.GetProperty("retentionDays").GetInt32());
        Assert.Equal(42.0, restored.GetProperty("minimumFreeGigabytes").GetDouble());
    }

    // Restore writes cameras, trigger zones and gate checks straight into their stores, which
    // made it the one way into an install that answered to none of the rules the endpoints
    // enforce. The same trigger zone the PUT refuses with six separate errors used to import
    // without a word and leave a camera that either never fires or never stops - and none of
    // that announces itself. A zone with an impossible object size logs nothing; it just quietly
    // stops being a camera that watches.
    [Fact]
    public async Task ABackupCarryingValuesTheEndpointsRefuse_IsRejectedAndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = TempDirectory();
        using var factory = CreateFactory(directory);
        using var client = factory.CreateClient();

        var cameraId = Guid.NewGuid();
        var backup = $$"""
        {
          "schemaVersion": 2,
          "exportedAt": "2026-08-25T00:00:00Z",
          "cameras": [{
            "id": "{{cameraId}}", "name": "Imported", "streamUrl": "file:///etc/passwd",
            "username": null, "password": null, "includeAudio": false, "ptzEnabled": false,
            "onvifPtzServiceUrl": null, "onvifProfileToken": null, "isBrowserPublished": false,
            "createdAt": "2026-08-25T00:00:00Z"
          }],
          "triggerZones": [{
            "cameraId": "{{cameraId}}", "enabled": true,
            "zonePolygon": [{"x":-5,"y":-5},{"x":9,"y":-5},{"x":9,"y":9}],
            "minTriggerArea": 0, "cooldownSeconds": -10, "maxTriggersPerMinute": 0,
            "shadowSensitivity": 500, "recordClips": false, "clipPreSeconds": 5,
            "clipPostSeconds": 10, "sendToAlerts": false, "alertProfile": "default",
            "sensorDeviceId": "", "sensorTriggerOn": "on", "sensorCooldownSeconds": 30,
            "zones": null, "sensorMeasurement": "", "sensorThreshold": 0,
            "nightWindowEnabled": true, "nightStartHour": 99, "nightEndHour": -3
          }]
        }
        """;

        using var response = await client.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

        // Every problem named, and attributed to the thing that has it - a list saying only
        // "invalid backup" would leave someone with no idea which of forty cameras to look at.
        Assert.Contains(errors, e => e.Contains("Imported") && e.Contains("rtsp://"));
        Assert.Contains(errors, e => e.Contains("Object size"));
        Assert.Contains(errors, e => e.Contains("Zone points"));
        Assert.Contains(errors, e => e.Contains("Night window hours"));

        // Each zone problem names the camera it belongs to, so a file with forty cameras in it
        // says which one to go and look at.
        Assert.All(
            errors.Where(e => e.StartsWith("Trigger zone", StringComparison.Ordinal)),
            e => Assert.Contains(cameraId.ToString(), e, StringComparison.OrdinalIgnoreCase));

        // Rejected whole: the camera the file carried was never written, so a failed restore
        // leaves the install exactly as it was rather than half-changed.
        var cameras = await client.GetFromJsonAsync<JsonElement>("/api/cameras", ct);
        Assert.Equal(0, cameras.GetArrayLength());
    }

    // A camera may belong to only one group, and that is not bookkeeping: GetGroupForCameraAsync
    // resolves a camera's group with FirstOrDefault, so two groups claiming the same camera means
    // whichever row comes back first wins and the other group's companion cameras quietly never
    // record. The endpoint refuses it; restore used to be the way around it.
    [Fact]
    public async Task ABackupWhoseCameraGroupsBreakTheirOwnRules_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(TempDirectory());
        using var client = factory.CreateClient();

        var shared = Guid.NewGuid();
        var second = Guid.NewGuid();
        var ghost = Guid.NewGuid();

        var backup = $$"""
        {
          "schemaVersion": 2, "exportedAt": "2026-08-25T00:00:00Z",
          "cameras": [
            { "id": "{{shared}}", "name": "A", "streamUrl": "rtsp://10.0.0.5/main", "username": null,
              "password": null, "includeAudio": false, "ptzEnabled": false, "onvifPtzServiceUrl": null,
              "onvifProfileToken": null, "isBrowserPublished": false, "createdAt": "2026-08-25T00:00:00Z" },
            { "id": "{{second}}", "name": "B", "streamUrl": "rtsp://10.0.0.6/main", "username": null,
              "password": null, "includeAudio": false, "ptzEnabled": false, "onvifPtzServiceUrl": null,
              "onvifProfileToken": null, "isBrowserPublished": false, "createdAt": "2026-08-25T00:00:00Z" }
          ],
          "cameraGroups": [
            { "id": "{{Guid.NewGuid()}}", "name": "First", "cameraIds": ["{{shared}}", "{{second}}"],
              "correlationWindowSeconds": 25, "recordCompanionClips": true, "analyzeCompanionCaptures": false },
            { "id": "{{Guid.NewGuid()}}", "name": "Second", "cameraIds": ["{{shared}}", "{{ghost}}"],
              "correlationWindowSeconds": 99999, "recordCompanionClips": true, "analyzeCompanionCaptures": false }
          ]
        }
        """;

        using var response = await client.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

        Assert.Contains(errors, e => e.Contains("already in the group"));
        Assert.Contains(errors, e => e.Contains("Correlation window"));
        Assert.Contains(errors, e => e.Contains(ghost.ToString()) && e.Contains("does not exist"));

        var groups = await client.GetFromJsonAsync<JsonElement>("/api/camera-groups", ct);
        Assert.Equal(0, groups.GetArrayLength());
    }

    // An account with no username cannot be signed in to and cannot be told apart in the accounts
    // list; a role outside the enum matches no authorization policy and renders as a bare number.
    [Fact]
    public async Task ABackupWhoseAccountsAreUnusable_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(TempDirectory());
        using var client = factory.CreateClient();

        var backup = $$"""
        {
          "schemaVersion": 2, "exportedAt": "2026-08-25T00:00:00Z",
          "accounts": [
            { "id": "{{Guid.NewGuid()}}", "username": "", "passwordHash": "PBKDF2.SHA256.1000.c2FsdA==.aGFzaA==",
              "role": 0, "displayName": null, "createdAt": "2026-08-25T00:00:00Z" },
            { "id": "{{Guid.NewGuid()}}", "username": "odd", "passwordHash": "PBKDF2.SHA256.1000.c2FsdA==.aGFzaA==",
              "role": 99, "displayName": null, "createdAt": "2026-08-25T00:00:00Z" }
          ]
        }
        """;

        using var response = await client.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

        Assert.Contains(errors, e => e.Contains("username is required"));
        Assert.Contains(errors, e => e.Contains("neither Admin nor Viewer"));
    }

    // OwnerKey is the primary key of the row a live-order entry becomes. It is declared
    // non-nullable, which the deserializer does not enforce, so a file missing the field used to
    // reach SQLite as a null parameter and throw there - a 500 with a stack trace, raised after
    // the cameras above it had already been written and could not be taken back.
    [Fact]
    public async Task ABackupWithAMalformedLiveOrderEntry_IsRejectedBeforeAnythingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(TempDirectory());
        using var client = factory.CreateClient();

        var backup = $$"""
        {
          "schemaVersion": 2, "exportedAt": "2026-08-25T00:00:00Z",
          "cameras": [
            { "id": "{{Guid.NewGuid()}}", "name": "A", "streamUrl": "rtsp://10.0.0.5/main", "username": null,
              "password": null, "includeAudio": false, "ptzEnabled": false, "onvifPtzServiceUrl": null,
              "onvifProfileToken": null, "isBrowserPublished": false, "createdAt": "2026-08-25T00:00:00Z" }
          ],
          "liveOrder": [ { "cameraIds": [] } ]
        }
        """;

        using var response = await client.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The camera listed ahead of the bad entry is not left behind by a half-finished restore.
        var cameras = await client.GetFromJsonAsync<JsonElement>("/api/cameras", ct);
        Assert.Equal(0, cameras.GetArrayLength());
    }

    // The settings half. RetentionDays carries [Range(1, 3650)] and is validated on start as well
    // as on save, so a zero arriving through a restore does not misbehave at runtime - it stops
    // the main app booting at all. That surfaces on the next restart, long after the restore
    // looked finished, and on a machine somebody is in the middle of rebuilding.
    [Fact]
    public async Task ABackupCarryingSettingsTheEndpointRefuses_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(TempDirectory());
        using var client = factory.CreateClient();

        var backup = """
        {
          "schemaVersion": 2,
          "exportedAt": "2026-08-25T00:00:00Z",
          "settings": {
            "bridge": {
              "apiKey": "0123456789abcdef0123456789abcdef", "dataDirectory": "data",
              "saveImages": true, "retentionDays": 0, "unverifiedMotionRetentionDays": 3,
              "allowedImageContentTypes": ["image/jpeg"], "maximumImageBytes": 15728640,
              "queueCapacity": 100, "imageWidth": 1024
            }
          }
        }
        """;

        using var response = await client.PostAsync(
            "/api/backup/import", new StringContent(backup, System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        Assert.Contains(errors, e => e.Contains("RetentionDays"));
    }

    // The guard on all of the above being too strict, and the one that matters most: the shipped
    // configuration itself has to survive a round trip. A validation that refuses a real backup
    // would be a worse bug than the one it was added for - it fails at exactly the moment someone
    // is rebuilding a machine and has nothing else left.
    [Fact]
    public async Task TheShippedConfiguration_StillExportsAndImportsCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var shipped = Path.Combine(RepositoryRootOrSkip(), "core/SentinelCore/appsettings.json");
        Assert.SkipUnless(File.Exists(shipped), "The shipped appsettings.json isn't in this checkout.");

        var sourceDirectory = TempDirectory();
        var sourceSettings = Path.Combine(sourceDirectory, "appsettings.json");
        File.Copy(shipped, sourceSettings, overwrite: true);

        // appsettings.json is live config on an installed machine, not a fixture - the checkout
        // this runs in is usually the one the server is running from, so whatever the operator has
        // turned on is in the file being read. Auth is the section that decides whether this test
        // can call anything: /api/backup is AdminOnly, and the synthetic Admin that makes it pass
        // unauthenticated only exists while Auth:Enabled is false. An install that turned dashboard
        // login on ships that `true` in this file, and its accounts live in accounts.db - which is
        // deliberately not copied here, so the host boots with login required and no account to
        // satisfy it, and every request 401s. That failed on the box this project actually runs on
        // while passing everywhere else, and deploy.sh runs under `set -e`, so it blocked the
        // restart rather than just reporting.
        //
        // Auth is pinned off in the copy because it is incidental to what this asserts: that the
        // *settings values* survive an export/import round trip. The Auth section's own
        // round-tripping is covered with the other sections, and login enforcement by
        // WebAuthorizationTests.
        var root = JsonNode.Parse(File.ReadAllText(sourceSettings))!.AsObject();
        root["Auth"] = new JsonObject { ["Enabled"] = false };
        File.WriteAllText(sourceSettings, root.ToJsonString());

        using var source = CreateFactory(sourceDirectory, writeEmptySettings: false);
        using var sourceClient = source.CreateClient();

        using var exported = await sourceClient.GetAsync("/api/backup/export", ct);
        exported.EnsureSuccessStatusCode();
        var payload = await exported.Content.ReadAsStringAsync(ct);

        using var destination = CreateFactory(TempDirectory());
        using var destinationClient = destination.CreateClient();
        using var imported = await destinationClient.PostAsync(
            "/api/backup/import", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);

        var body = await imported.Content.ReadAsStringAsync(ct);
        Assert.True(imported.StatusCode == HttpStatusCode.OK,
            $"The shipped configuration no longer restores through its own backup: {body}");
    }

    private static string RepositoryRootOrSkip()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "core"))) return directory.FullName;
            directory = directory.Parent;
        }
        return "";
    }

    // The other half of that: a backup whose contents are legal still restores. Guards against
    // the validation above being too strict to let a real export back in.
    [Fact]
    public async Task ABackupOfAnOrdinaryCameraAndZone_StillRestores()
    {
        var ct = TestContext.Current.CancellationToken;

        using var source = CreateFactory(TempDirectory());
        using var sourceClient = source.CreateClient();
        var cameraId = await AddCameraAsync(sourceClient, "Gate", ct);

        using (var zone = await sourceClient.PutAsJsonAsync($"/api/cameras/{cameraId}/trigger-config", new
        {
            Enabled = true,
            ZonePolygon = new[] { new { X = 0.1, Y = 0.1 }, new { X = 0.9, Y = 0.1 }, new { X = 0.9, Y = 0.9 } },
            MinTriggerArea = 0.02, CooldownSeconds = 30, MaxTriggersPerMinute = 6, ShadowSensitivity = 50,
            RecordClips = false, ClipPreSeconds = 5, ClipPostSeconds = 10, SendToAlerts = false,
            AlertProfile = "default", SensorDeviceId = "", SensorTriggerOn = "on", SensorCooldownSeconds = 30,
            SensorMeasurement = "", SensorThreshold = 0.0,
            NightWindowEnabled = true, NightStartHour = 21, NightEndHour = 7,
            BypassFastLocal = true, KeepOnlyAlertable = true
        }, ct))
            zone.EnsureSuccessStatusCode();

        using var exported = await sourceClient.GetAsync("/api/backup/export", ct);
        var payload = await exported.Content.ReadAsStringAsync(ct);

        using var destination = CreateFactory(TempDirectory());
        using var destinationClient = destination.CreateClient();
        using var imported = await destinationClient.PostAsync(
            "/api/backup/import", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var restored = await destinationClient.GetFromJsonAsync<JsonElement>($"/api/cameras/{cameraId}/trigger-config", ct);
        Assert.Equal(0.02, restored.GetProperty("minTriggerArea").GetDouble(), 3);
        Assert.Equal(21, restored.GetProperty("nightStartHour").GetInt32());
    }

    // Two guarantees this file's header makes that nothing was checking. Restoring the same file
    // twice has to update what it created rather than duplicate it - which only holds because
    // cameras and groups are restored under their original ids - and a restored install has to
    // export back to the document it was restored from. The second is the stronger property: it
    // says nothing is quietly transformed on the way in, which is exactly the class of thing that
    // goes unnoticed until somebody restores a backup of a backup.
    [Fact]
    public async Task RestoringTwiceChangesNothing_AndTheRestoredInstallExportsTheSameDocument()
    {
        var ct = TestContext.Current.CancellationToken;

        using var source = CreateFactory(TempDirectory());
        using var sourceClient = source.CreateClient();

        var gate = await AddCameraAsync(sourceClient, "Gate", ct);
        var door = await AddCameraAsync(sourceClient, "Front Door", ct);

        using (var group = await sourceClient.PostAsJsonAsync("/api/camera-groups", new
        {
            Name = "Front of house", CameraIds = new[] { gate, door }, CorrelationWindowSeconds = 25,
            RecordCompanionClips = true, AnalyzeCompanionCaptures = false
        }, ct))
            group.EnsureSuccessStatusCode();

        using (var zone = await sourceClient.PutAsJsonAsync($"/api/cameras/{gate}/trigger-config", new
        {
            Enabled = true,
            ZonePolygon = new[] { new { X = 0.1, Y = 0.1 }, new { X = 0.9, Y = 0.1 }, new { X = 0.9, Y = 0.9 } },
            MinTriggerArea = 0.02, CooldownSeconds = 30, MaxTriggersPerMinute = 6, ShadowSensitivity = 50,
            RecordClips = false, ClipPreSeconds = 5, ClipPostSeconds = 10, SendToAlerts = false,
            AlertProfile = "default", SensorDeviceId = "", SensorTriggerOn = "on", SensorCooldownSeconds = 30,
            SensorMeasurement = "", SensorThreshold = 0.0,
            NightWindowEnabled = true, NightStartHour = 21, NightEndHour = 7,
            BypassFastLocal = true, KeepOnlyAlertable = true
        }, ct))
            zone.EnsureSuccessStatusCode();

        using var exported = await sourceClient.GetAsync("/api/backup/export", ct);
        var original = await exported.Content.ReadAsStringAsync(ct);

        using var destination = CreateFactory(TempDirectory());
        using var destinationClient = destination.CreateClient();

        for (var pass = 1; pass <= 2; pass++)
        {
            using var imported = await destinationClient.PostAsync("/api/backup/import",
                new StringContent(original, System.Text.Encoding.UTF8, "application/json"), ct);
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        }

        Assert.Equal(2, (await destinationClient.GetFromJsonAsync<JsonElement>("/api/cameras", ct)).GetArrayLength());
        Assert.Equal(1, (await destinationClient.GetFromJsonAsync<JsonElement>("/api/camera-groups", ct)).GetArrayLength());

        using var reExported = await destinationClient.GetAsync("/api/backup/export", ct);
        var again = await reExported.Content.ReadAsStringAsync(ct);

        // ExportedAt is stamped per export and is the one field expected to differ.
        static string WithoutTimestamp(string json)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node.Remove("exportedAt");
            return node.ToJsonString();
        }

        Assert.Equal(WithoutTimestamp(original), WithoutTimestamp(again));
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
        if (writeEmptySettings) File.WriteAllText(settingsPath, WebHostTestSettings.LoginOffSettings);

        return new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath, dataDirectory));
    }
}
