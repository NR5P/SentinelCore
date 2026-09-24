extern alias WebHost;
extern alias MainApp;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using SentinelCore.Web;
using Xunit;
using MainProgram = MainApp::Program;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The full-backup bundle and the nightly auto export that writes it. What matters most here is
// the promise the feature makes: a file at the destination is either complete and restorable or
// not there at all, and restoring it onto a fresh install brings back the things the plain JSON
// export used to leave behind.
public sealed class AutoExportTests : IDisposable
{
    private readonly List<string> _directories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        var path = Path.Combine(Path.GetTempPath(), $"auto-export-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    private WebApplicationFactory<WebProgram> CreateWebFactory(string dataDirectory)
    {
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");
        if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, WebHostTestSettings.LoginOffSettings);
        return new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath, dataDirectory));
    }

    private static BackupDocument MinimalDocument() =>
        new(BackupService.CurrentSchemaVersion, DateTimeOffset.UtcNow, null, [], []);

    // ---- bundle format ---------------------------------------------------------------------

    [Fact]
    public async Task AZipBundle_CarriesTheDocumentAndItsFiles()
    {
        using var buffer = new MemoryStream();
        var photo = "jpeg bytes"u8.ToArray();
        await BackupBundle.WriteZipAsync(buffer, MinimalDocument(),
            [new BackupFile("known-faces/abc/1.jpg", () => new MemoryStream(photo), DateTimeOffset.UtcNow)], Ct);

        buffer.Position = 0;
        Assert.Equal(BackupBundle.Format.Zip, BackupBundle.Detect(buffer.ToArray().AsSpan(0, 8)));

        var (document, archive) = await BackupBundle.OpenZipAsync(buffer, Ct);
        using (archive)
        {
            Assert.Equal(BackupService.CurrentSchemaVersion, document!.SchemaVersion);
            var (kind, relative, entry) = Assert.Single(BackupBundle.Files(archive));
            Assert.Equal(BackupFileKinds.KnownFacePhoto, kind);
            Assert.Equal("abc/1.jpg", relative);
            await using var stream = entry.Open();
            using var read = new MemoryStream();
            await stream.CopyToAsync(read, Ct);
            Assert.Equal(photo, read.ToArray());
        }
    }

    [Theory]
    [InlineData("files/homekit/../../etc/passwd")]
    [InlineData("files/homekit//absolute")]
    [InlineData("files/known-faces/a\\..\\b")]
    [InlineData("files/license/C:evil")]
    public void EntriesThatWouldEscapeTheirFolder_AreNeverOffered(string name)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("x");
        }
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        Assert.Empty(BackupBundle.Files(archive));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData((1 << 20) - 1)]
    [InlineData(1 << 20)]
    [InlineData((3 << 20) + 17)]
    public async Task Encryption_RoundTripsAtEveryChunkBoundary(int length)
    {
        var plaintext = RandomNumberGenerator.GetBytes(length);
        var encrypted = await EncryptAsync(plaintext, "correct horse");
        Assert.Equal(BackupBundle.Format.Encrypted, BackupBundle.Detect(encrypted.AsSpan(0, 8)));
        Assert.Equal(plaintext, await DecryptAsync(encrypted, "correct horse"));
    }

    [Fact]
    public async Task AWrongPassphrase_IsRefusedInWordsTheOwnerCanActOn()
    {
        var encrypted = await EncryptAsync("secret"u8.ToArray(), "correct horse");
        var error = await Assert.ThrowsAsync<BackupBundleException>(() => DecryptAsync(encrypted, "wrong horse"));
        Assert.Contains("Wrong passphrase", error.Message);
    }

    // The attacks the chunk framing exists for: a file cut off at a chunk boundary must not pass
    // for a complete one, and neither may one with its chunks reordered or bytes appended.
    [Fact]
    public async Task ATruncatedReorderedOrExtendedFile_NeverDecryptsAsComplete()
    {
        var plaintext = RandomNumberGenerator.GetBytes((2 << 20) + 5);
        var encrypted = await EncryptAsync(plaintext, "correct horse");
        const int header = 8 + 1 + 4 + 16 + 4;
        const int fullChunk = 4 + (1 << 20) + 16;

        var cutAtBoundary = encrypted[..(header + 2 * fullChunk)];
        await Assert.ThrowsAsync<BackupBundleException>(() => DecryptAsync(cutAtBoundary, "correct horse"));

        var cutMidChunk = encrypted[..(encrypted.Length - 3)];
        await Assert.ThrowsAsync<BackupBundleException>(() => DecryptAsync(cutMidChunk, "correct horse"));

        var swapped = (byte[])encrypted.Clone();
        encrypted.AsSpan(header, fullChunk).CopyTo(swapped.AsSpan(header + fullChunk));
        encrypted.AsSpan(header + fullChunk, fullChunk).CopyTo(swapped.AsSpan(header));
        await Assert.ThrowsAsync<BackupBundleException>(() => DecryptAsync(swapped, "correct horse"));

        byte[] extended = [.. encrypted, 0];
        await Assert.ThrowsAsync<BackupBundleException>(() => DecryptAsync(extended, "correct horse"));
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, string passphrase)
    {
        using var output = new MemoryStream();
        await BackupBundle.EncryptAsync(new MemoryStream(plaintext), output, passphrase, Ct);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] encrypted, string passphrase)
    {
        using var output = new MemoryStream();
        await BackupBundle.DecryptAsync(new MemoryStream(encrypted), output, passphrase, Ct);
        return output.ToArray();
    }

    // ---- scheduling ------------------------------------------------------------------------

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly AutoExportSettingsDto AtThree = new() { Enabled = true, Hour = 3, Minute = 0 };

    [Fact]
    public void SwitchingItOnInTheAfternoon_WaitsForTheNightRatherThanRunningOnTheSpot()
    {
        var afternoon = new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);
        var state = new AutoExportState { ArmedAtUtc = afternoon };

        Assert.Null(AutoExportSchedule.Due(AtThree, state, afternoon.AddHours(1), Utc));

        var tonight = new DateTimeOffset(2026, 9, 25, 3, 0, 30, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero), AutoExportSchedule.Due(AtThree, state, tonight, Utc));
    }

    [Fact]
    public void AServerThatWasOffAtThreeCatchesUpWhenItComesBack_OnceOnly()
    {
        var threeAm = new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);
        var state = new AutoExportState
        {
            ArmedAtUtc = threeAm.AddDays(-5),
            LastOccurrenceUtc = threeAm.AddDays(-1),
            LastResult = "ok"
        };

        var nineAm = threeAm.AddHours(6);
        Assert.Equal(threeAm, AutoExportSchedule.Due(AtThree, state, nineAm, Utc));

        var afterIt = state with { LastOccurrenceUtc = threeAm, LastAttemptUtc = nineAm, LastResult = "ok", AttemptsForOccurrence = 1 };
        Assert.Null(AutoExportSchedule.Due(AtThree, afterIt, nineAm.AddHours(2), Utc));
    }

    [Fact]
    public void AFailedNight_IsRetriedHourly_AndThenLeftForTomorrow()
    {
        var threeAm = new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);
        var failed = new AutoExportState
        {
            ArmedAtUtc = threeAm.AddDays(-5),
            LastOccurrenceUtc = threeAm,
            LastAttemptUtc = threeAm,
            LastResult = "failed",
            AttemptsForOccurrence = 1
        };

        Assert.Null(AutoExportSchedule.Due(AtThree, failed, threeAm.AddMinutes(30), Utc));
        Assert.Equal(threeAm, AutoExportSchedule.Due(AtThree, failed, threeAm.AddHours(1), Utc));

        var exhausted = failed with { AttemptsForOccurrence = AutoExportSchedule.MaxAttemptsPerNight };
        Assert.Null(AutoExportSchedule.Due(AtThree, exhausted, threeAm.AddHours(9), Utc));
    }

    [Fact]
    public void ASpringForwardTimeThatDoesntExist_StillRunsThatNight()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var options = new AutoExportSettingsDto { Enabled = true, Hour = 2, Minute = 30 };

        // 2027-03-14 02:30 doesn't happen in Chicago; the export runs at 03:00 CDT (08:00 UTC).
        var morning = new DateTimeOffset(2027, 3, 14, 12, 0, 0, TimeSpan.Zero);
        var occurrence = AutoExportSchedule.LatestOccurrence(options.Hour, options.Minute, morning, zone);
        Assert.Equal(new DateTimeOffset(2027, 3, 14, 8, 0, 0, TimeSpan.Zero), occurrence);
    }

    // ---- folder delivery -------------------------------------------------------------------

    [Fact]
    public async Task FolderDelivery_ArrivesWhole_AndRetentionOnlyEverTouchesItsOwnFiles()
    {
        var destination = TempDirectory();
        var source = Path.Combine(TempDirectory(), "bundle.tmp");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(4096), Ct);

        for (var day = 1; day <= 5; day++)
            await File.WriteAllTextAsync(Path.Combine(destination, $"sentinelcore-backup-2026-09-{day:D2}-0300.zip"), "old", Ct);
        await File.WriteAllTextAsync(Path.Combine(destination, "my-notes.txt"), "not ours", Ct);
        await File.WriteAllTextAsync(Path.Combine(destination, "sentinelcore-backup-important.zip"), "not ours either", Ct);

        var written = await AutoExportFolder.DeliverAsync(destination, source, "sentinelcore-backup-2026-09-24-0300.zip", 4096, Ct);
        Assert.Equal(await File.ReadAllBytesAsync(source, Ct), await File.ReadAllBytesAsync(written, Ct));
        Assert.Empty(Directory.EnumerateFiles(destination, "*.partial"));

        // Same minute again: a second file, not an overwrite.
        var second = await AutoExportFolder.DeliverAsync(destination, source, "sentinelcore-backup-2026-09-24-0300.zip", 4096, Ct);
        Assert.NotEqual(written, second);

        Assert.Equal(4, AutoExportFolder.Prune(destination, keep: 3));
        var left = Directory.EnumerateFiles(destination).Select(Path.GetFileName).Order().ToArray();
        Assert.Contains("my-notes.txt", left);
        Assert.Contains("sentinelcore-backup-important.zip", left);
        Assert.Contains("sentinelcore-backup-2026-09-24-0300.zip", left);
        Assert.DoesNotContain("sentinelcore-backup-2026-09-01-0300.zip", left);
        Assert.Equal(5, left.Length);
    }

    [Theory]
    [InlineData("SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", true)]
    [InlineData("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", true)]
    [InlineData("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og=", true)]
    [InlineData("MD5:12:34:56", false)]
    [InlineData("not a fingerprint", false)]
    public void HostKeyFingerprints_AreRecognisedInTheFormsSshPrintsThem(string value, bool valid) =>
        Assert.Equal(valid, AutoExportSftp.IsFingerprint(value));

    [Fact]
    public void Settings_RefuseADestinationThatCantWork_OnlyWhenSwitchedOn()
    {
        static List<string> Problems(AutoExportSettingsDto dto)
        {
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                dto, new System.ComponentModel.DataAnnotations.ValidationContext(dto), results, true);
            return [.. results.Select(r => r.ErrorMessage!)];
        }

        Assert.Empty(Problems(new AutoExportSettingsDto { Enabled = false }));
        Assert.NotEmpty(Problems(new AutoExportSettingsDto { Enabled = true, FolderPath = "" }));
        Assert.NotEmpty(Problems(new AutoExportSettingsDto { Enabled = true, FolderPath = "relative/path" }));
        Assert.Empty(Problems(new AutoExportSettingsDto { Enabled = true, FolderPath = Path.GetTempPath() }));
        Assert.NotEmpty(Problems(new AutoExportSettingsDto { Enabled = true, Destination = "sftp", SftpHost = "nas" }));
        Assert.NotEmpty(Problems(new AutoExportSettingsDto { Enabled = true, FolderPath = Path.GetTempPath(), Passphrase = "short" }));
    }

    // ---- end to end ------------------------------------------------------------------------

    // The loose ends the JSON export used to leave: an attachment's bytes and a recording
    // schedule. Both come back from the bundle onto an install that has never seen them.
    [Fact]
    public async Task AFullBundle_RestoresAttachmentsAndRecordingSchedules_OntoAFreshInstall()
    {
        var sourceDirectory = TempDirectory();
        using var source = CreateWebFactory(sourceDirectory);
        using var sourceClient = source.CreateClient();

        var bytes = "a picture of the dog"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var clock = $"{DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds():D13}-0000-web";
        using (var sync = await sourceClient.PostAsJsonAsync("/api/organizer/sync", new
        {
            cursor = 0,
            changes = new object[]
            {
                new { id = "n", kind = "note", fields = new Dictionary<string, object?> { ["spaceId"] = "family", ["title"] = "Dog" },
                      clocks = new Dictionary<string, string> { ["spaceId"] = clock, ["title"] = clock } },
                new { id = "a", kind = "attachment",
                      fields = new Dictionary<string, object?> { ["parentId"] = "n", ["hash"] = hash, ["name"] = "dog.txt", ["contentType"] = "text/plain" },
                      clocks = new Dictionary<string, string> { ["parentId"] = clock, ["hash"] = clock, ["name"] = clock, ["contentType"] = clock } },
            }
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using (var upload = await sourceClient.PutAsync($"/api/organizer/blobs/{hash}", new ByteArrayContent(bytes), Ct))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        Guid cameraId;
        using (var camera = await sourceClient.PostAsJsonAsync("/api/cameras",
            new { Name = "Porch", StreamUrl = "rtsp://camera/porch", IncludeAudio = false }, Ct))
            cameraId = (await camera.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        using (var schedule = await sourceClient.PostAsJsonAsync($"/api/cameras/{cameraId}/recording-schedules", new
        {
            Name = "Evening", Enabled = true, Kind = "daily", StartTime = "20:00", StopTime = "21:00",
            DaysOfWeek = Array.Empty<int>(), CronExpression = "", RunAtUtc = (DateTimeOffset?)null,
            SolarEvent = "", SolarOffsetMinutes = 0, SolarElevationDegrees = 0.0, DurationSeconds = (int?)null, IncludeAudio = false
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);

        using var bundle = await sourceClient.PostAsJsonAsync("/api/backup/bundle", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        var bundleBytes = await bundle.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(BackupBundle.Format.Zip, BackupBundle.Detect(bundleBytes.AsSpan(0, 8)));

        // Nothing left behind in the work directory once the download has been sent.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sourceDirectory, "backup-work")));

        // The main app isn't running in this test, so the file says what it couldn't include
        // rather than silently leaving it out.
        using (var zip = new ZipArchive(new MemoryStream(bundleBytes)))
        {
            var document = JsonSerializer.Deserialize<JsonElement>(zip.GetEntry("backup.json")!.Open());
            Assert.Contains(document.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Device Hub"));
            Assert.NotNull(zip.GetEntry($"files/organizer/{hash}"));
        }

        using var destination = CreateWebFactory(TempDirectory());
        using var destinationClient = destination.CreateClient();
        using var restore = await destinationClient.PostAsync("/api/backup/import", new ByteArrayContent(bundleBytes), Ct);
        var restored = await restore.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(1, restored.GetProperty("attachmentsRestored").GetInt32());
        Assert.Equal(1, restored.GetProperty("recordingSchedulesRestored").GetInt32());

        Assert.Equal(bytes, await destinationClient.GetByteArrayAsync($"/api/organizer/blobs/{hash}", Ct));
        var schedules = await destinationClient.GetFromJsonAsync<JsonElement>($"/api/cameras/{cameraId}/recording-schedules", Ct);
        Assert.Equal("Evening", Assert.Single(schedules.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task ExportNow_WritesAnEncryptedFileThatRestoresOnlyWithItsPassphrase()
    {
        var dataDirectory = TempDirectory();
        var destination = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();

        using (var save = await client.PutAsJsonAsync("/api/settings/auto-export", new
        {
            Enabled = false, Hour = 3, Minute = 0, Destination = "folder", FolderPath = destination,
            SftpPort = 22, KeepCount = 7, Passphrase = "a long passphrase"
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Masked like every other secret on the way back out.
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings", Ct);
        Assert.Equal(SettingsSecrets.Mask, settings.GetProperty("autoExport").GetProperty("passphrase").GetString());

        using var run = await client.PostAsync("/api/backup/auto/run", null, Ct);
        var result = await run.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.Contains(destination, result.GetProperty("detail").GetString());

        var written = Assert.Single(Directory.EnumerateFiles(destination));
        Assert.EndsWith(".scbackup", written);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/backup/auto/status", Ct);
        Assert.Equal(written, status.GetProperty("lastLocation").GetString());
        Assert.Equal("manual", status.GetProperty("lastTrigger").GetString());

        var fileBytes = await File.ReadAllBytesAsync(written, Ct);
        var otherDirectory = TempDirectory();
        using var other = CreateWebFactory(otherDirectory);
        using var otherClient = other.CreateClient();

        using (var noPassphrase = await otherClient.PostAsync("/api/backup/import", new ByteArrayContent(fileBytes), Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noPassphrase.StatusCode);
            Assert.True((await noPassphrase.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("passphraseRequired").GetBoolean());
        }

        using (var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/backup/import") { Content = new ByteArrayContent(fileBytes) })
        {
            wrong.Headers.Add("X-Backup-Passphrase", "not the passphrase");
            using var response = await otherClient.SendAsync(wrong, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var right = new HttpRequestMessage(HttpMethod.Post, "/api/backup/import") { Content = new ByteArrayContent(fileBytes) })
        {
            right.Headers.Add("X-Backup-Passphrase", "a long passphrase");
            using var response = await otherClient.SendAsync(right, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The auto-export settings themselves came across, passphrase included, so the restored
        // install carries on exporting to the same place.
        var restoredSettings = await File.ReadAllTextAsync(Path.Combine(otherDirectory, "appsettings.json"), Ct);
        Assert.Contains("a long passphrase", restoredSettings);
    }

    [Fact]
    public async Task ExportNow_WithNowhereToWrite_SaysSoAndWritesNothing()
    {
        using var factory = CreateWebFactory(TempDirectory());
        using var client = factory.CreateClient();

        using var run = await client.PostAsync("/api/backup/auto/run", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
        Assert.Contains("folder", (await run.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/backup/auto/status", Ct);
        Assert.Equal("failed", status.GetProperty("lastResult").GetString());
    }

    // ---- the main app's share ----------------------------------------------------------------

    private const string MainApiKey = "test-key-0123456789abcdef0123456789abcdef";

    private WebApplicationFactory<MainProgram> CreateMainFactory(string root)
    {
        File.WriteAllText(Path.Combine(root, "appsettings.json"), """
        {
          "PromptProfiles": {
            "default": { "DisplayName": "Test", "SystemPrompt": "Analyze this image.", "Questions": ["What is visible?"] }
          }
        }
        """);
        return new WebApplicationFactory<MainProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(root);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bridge:ApiKey"] = MainApiKey,
                ["Bridge:DataDirectory"] = root,
                ["Email:Enabled"] = "false",
                ["Telegram:Enabled"] = "false",
                ["TelegramChat:Enabled"] = "false",
                ["Discord:Enabled"] = "false",
                ["Webhook:Enabled"] = "false",
                ["Mqtt:Enabled"] = "false",
                ["Mqtt:RunEmbeddedBroker"] = "false",
                ["DeviceHub:Enabled"] = "false",
                ["StartupNotification:Enabled"] = "false",
                ["SelfMonitoring:Enabled"] = "false",
                ["DailySummary:Enabled"] = "false",
                ["InternetOutage:Enabled"] = "false",
                ["BugReports:Enabled"] = "false",
                ["PushNotifications:Enabled"] = "false",
            }));
        });
    }

    private static HttpClient Keyed(WebApplicationFactory<MainProgram> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", MainApiKey);
        return client;
    }

    // Devices, rules and schedules keep their ids - a schedule names its device by id - and a
    // second restore of the same file updates rather than duplicates.
    [Fact]
    public async Task TheMainAppsDevicesRulesAndSchedules_RestoreWithTheirIds_AndOnlyOnce()
    {
        await using var source = CreateMainFactory(TempDirectory());
        using var sourceClient = Keyed(source);

        Guid deviceId;
        using (var device = await sourceClient.PostAsJsonAsync("/api/devices", new
        {
            Name = "Porch light", Kind = "light", Room = "Outside", CommandTopic = "zigbee2mqtt/porch/set",
            StateTopic = "zigbee2mqtt/porch", Schema = "json", StateValueTemplate = "state"
        }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, device.StatusCode);
            deviceId = (await device.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        using (var schedule = await sourceClient.PostAsJsonAsync("/api/schedules", new
        {
            Name = "Porch on", Kind = "daily", TimeOfDay = "19:30", ActionType = "device", DeviceId = deviceId, DeviceAction = "on"
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);

        using (var rule = await sourceClient.PostAsJsonAsync("/api/automation-rules", new
        {
            Name = "Siren", ActionType = "mqtt", MqttTopic = "home/siren", MqttPayload = "on", Categories = new[] { "person" }
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, rule.StatusCode);

        var exported = await sourceClient.GetFromJsonAsync<JsonElement>("/api/backup/hub", Ct);
        var exportedSchedule = Assert.Single(exported.GetProperty("schedules").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, exportedSchedule.GetProperty("nextDueUtc").ValueKind);

        await using var destination = CreateMainFactory(TempDirectory());
        using var destinationClient = Keyed(destination);

        using (var validate = await destinationClient.PostAsJsonAsync("/api/backup/hub/validate", exported, Ct))
            Assert.Empty((await validate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray());

        for (var pass = 1; pass <= 2; pass++)
        {
            using var restore = await destinationClient.PostAsJsonAsync("/api/backup/hub/restore", exported, Ct);
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        }

        var devices = await destinationClient.GetFromJsonAsync<JsonElement>("/api/devices", Ct);
        Assert.Equal(deviceId, Assert.Single(devices.EnumerateArray()).GetProperty("id").GetGuid());
        var schedules = await destinationClient.GetFromJsonAsync<JsonElement>("/api/schedules", Ct);
        Assert.Equal(deviceId, Assert.Single(schedules.EnumerateArray()).GetProperty("action").GetProperty("deviceId").GetGuid());
        var rules = await destinationClient.GetFromJsonAsync<JsonElement>("/api/automation-rules", Ct);
        Assert.Equal("Siren", Assert.Single(rules.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task AHubSectionTheEditScreensWouldRefuse_IsRefusedByRestoreToo()
    {
        await using var factory = CreateMainFactory(TempDirectory());
        using var client = Keyed(factory);

        var broken = new
        {
            devices = Array.Empty<object>(),
            automationRules = new[]
            {
                new
                {
                    id = Guid.NewGuid(), name = "", enabled = true, cameras = Array.Empty<string>(), categories = Array.Empty<string>(),
                    minimumConfidence = 0.5, onlyWhenNotificationRecommended = true, duplicateCooldownMinutes = 5,
                    actionType = "mqtt", mqttTopic = "", mqttPayload = "", mqttRetain = false, webhookUrl = "", webhookMethod = "POST",
                    webhookHeaders = "", webhookBody = "", webhookContentType = "application/json",
                    createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow
                }
            },
            schedules = Array.Empty<object>()
        };

        using var restore = await client.PostAsJsonAsync("/api/backup/hub/restore", broken, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/automation-rules", Ct)).EnumerateArray());
    }
}
