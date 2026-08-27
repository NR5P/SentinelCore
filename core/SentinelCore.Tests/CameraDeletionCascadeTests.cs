extern alias WebHost;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// Deleting a camera used to remove one row - the camera - and leave everything configured *about*
// that camera behind, still switched on.
//
// The gate camera replaced on 2026-08-27 is what this is written from. Its predecessor was deleted
// and left an enabled trigger config pointing at an id no camera had any more. Nothing crashed:
// TriggerZoneMonitor resolves each config against the camera store and skips what it cannot find,
// so the row simply sat there - surviving every restart, exported into config backups and restored
// out of them again, and waiting to come back to life the day something reused the id.
//
// The line these tests draw is configuration versus history. Settings for a camera that no longer
// exists are meaningless and are removed; recordings and trigger events are what happened, and
// deleting a camera is not a statement about what it saw.
public sealed class CameraDeletionCascadeTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _settingsPath;

    public CameraDeletionCascadeTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"camera-cascade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            .UseSetting("Bridge:DataDirectory", _dataDirectory)
            .UseSetting("InternetExposed:Enabled", "false"));

    [Fact]
    public async Task DeletingACamera_TakesItsTriggerConfigWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        using (var scope = factory.Services.CreateScope())
        {
            var configs = scope.ServiceProvider.GetRequiredService<ITriggerConfigStore>();
            await configs.SaveAsync(TriggerConfig.Disabled(id) with { Enabled = true }, ct);
        }

        using (var deleted = await client.DeleteAsync($"/api/cameras/{id}", ct))
            deleted.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var configs = scope.ServiceProvider.GetRequiredService<ITriggerConfigStore>();

            // GetAsync answers with a disabled default for a camera it has no row for, so the
            // question worth asking is whether the row is still in the table at all - that is what
            // an export copies and a restore brings back.
            Assert.DoesNotContain(await configs.GetAllAsync(ct), config => config.CameraId == id);
            Assert.DoesNotContain(await configs.GetEnabledAsync(ct), config => config.CameraId == id);
        }
    }

    [Fact]
    public async Task DeletingACamera_TakesItsGateCheckWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        using (var scope = factory.Services.CreateScope())
        {
            var gateChecks = scope.ServiceProvider.GetRequiredService<IGateCheckStore>();
            await gateChecks.SaveAsync(GateCheckConfig.Disabled(id) with { Enabled = true }, ct);
        }

        using (var deleted = await client.DeleteAsync($"/api/cameras/{id}", ct))
            deleted.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var gateChecks = scope.ServiceProvider.GetRequiredService<IGateCheckStore>();
            Assert.Null(await gateChecks.GetAsync(id, ct));
        }
    }

    [Fact]
    public async Task DeletingACamera_TakesItsRecordingSchedulesWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        using (var scope = factory.Services.CreateScope())
        {
            var schedules = scope.ServiceProvider.GetRequiredService<IRecordingScheduleStore>();
            await schedules.SaveAsync(NightlySchedule(id), ct);
            // Two, because the cleanup has to sweep a camera's schedules rather than one of them.
            await schedules.SaveAsync(NightlySchedule(id) with { Id = Guid.NewGuid(), Name = "Second window" }, ct);
        }

        using (var deleted = await client.DeleteAsync($"/api/cameras/{id}", ct))
            deleted.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var schedules = scope.ServiceProvider.GetRequiredService<IRecordingScheduleStore>();
            Assert.Empty(await schedules.GetForCameraAsync(id, ct));
            Assert.DoesNotContain(await schedules.GetAllAsync(ct), schedule => schedule.CameraId == id);
        }
    }

    // The deliberate other half. A trigger event is a photograph of something that happened, and
    // its incident may hold companion captures from cameras that still exist - removing the camera
    // is a change to the setup, not a decision to destroy the record.
    [Fact]
    public async Task DeletingACamera_LeavesWhatItAlreadyRecorded_Alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        using (var scope = factory.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<ITriggerEventStore>();
            await events.AddAsync(id, "/images/whatever.jpg", 0.25, null, TriggerConfirmation.Confirmed, ct);
        }

        using (var deleted = await client.DeleteAsync($"/api/cameras/{id}", ct))
            deleted.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<ITriggerEventStore>();
            var kept = await events.GetRecentAsync(50, id, includeUnconfirmed: true, ct);
            Assert.Single(kept);
        }
    }

    // Cleaning up after a camera that was never there must not invent a success: the 404 is what
    // tells a client its idea of the camera list is stale.
    [Fact]
    public async Task DeletingACameraThatDoesNotExist_IsStill404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.DeleteAsync($"/api/cameras/{Guid.NewGuid()}", ct);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private static RecordingSchedule NightlySchedule(Guid cameraId) => new(
        Id: Guid.NewGuid(),
        CameraId: cameraId,
        Name: "Overnight",
        Enabled: true,
        Kind: RecordingSchedule.KindDaily,
        TimeOfDay: "23:00",
        DaysOfWeek: [0, 1, 2, 3, 4, 5, 6],
        CronExpression: string.Empty,
        RunAtUtc: null,
        SolarEvent: string.Empty,
        SolarOffsetMinutes: 0,
        SolarElevationDegrees: 0,
        DurationSeconds: RecordingSchedule.MinimumDurationSeconds,
        IncludeAudio: false,
        LastStartedUtc: null,
        LastStatus: string.Empty,
        LastDetail: string.Empty,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static async Task<Guid> AddCameraAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/cameras",
            new { Name = "Gate Camera", StreamUrl = "rtsp://10.0.0.5/main", IncludeAudio = false }, ct);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return created.GetProperty("id").GetGuid();
    }
}
