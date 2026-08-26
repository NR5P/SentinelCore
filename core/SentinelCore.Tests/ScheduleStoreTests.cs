using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

public sealed class ScheduleStoreTests
{
    [Fact]
    public async Task CreateGetUpdateDelete_RoundTrips()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var store = BuildStore(tempDir);
            var token = TestContext.Current.CancellationToken;

            var created = await store.CreateAsync(Daily("Porch light on", "18:00"), token);

            var fetched = await store.GetAsync(created.Id, token);
            Assert.NotNull(fetched);
            Assert.Equal("Porch light on", fetched.Name);
            Assert.Equal("18:00", fetched.TimeOfDay);
            Assert.Equal(AutomationAction.TypeDevice, fetched.Action.Type);
            Assert.Equal(created.Action.DeviceId, fetched.Action.DeviceId);

            var updated = await store.UpdateAsync(
                created.Id, created with { Name = "Porch light on, later", TimeOfDay = "19:30" }, token);
            Assert.NotNull(updated);
            Assert.Equal("19:30", (await store.GetAsync(created.Id, token))!.TimeOfDay);

            Assert.True(await store.DeleteAsync(created.Id, token));
            Assert.Null(await store.GetAsync(created.Id, token));
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    [Fact]
    public async Task RoundTripsEveryTimingKindsOwnFields()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var store = BuildStore(tempDir);
            var token = TestContext.Current.CancellationToken;

            var runAt = new DateTimeOffset(2026, 8, 4, 15, 0, 0, TimeSpan.Zero);
            var schedules = new[]
            {
                Daily("Weekdays", "07:00") with { DaysOfWeek = [1, 2, 3, 4, 5] },
                Base("Every quarter hour") with
                {
                    Kind = Schedule.KindInterval, IntervalSeconds = 900, WindowStart = "09:00", WindowEnd = "17:00"
                },
                Base("Cron") with { Kind = Schedule.KindCron, CronExpression = "0 6 * * 1-5" },
                Base("One shot") with { Kind = Schedule.KindOnce, RunAtUtc = runAt },
                Base("Coop door") with
                {
                    Kind = Schedule.KindSolar,
                    SolarEvent = Schedule.SolarDusk,
                    SolarElevationDegrees = SolarCalculator.CivilTwilightDegrees,
                    SolarOffsetMinutes = 20
                }
            };

            foreach (var schedule in schedules) await store.CreateAsync(schedule, token);

            var all = await store.GetAllAsync(token);
            Assert.Equal(5, all.Count);

            var interval = all.Single(s => s.Kind == Schedule.KindInterval);
            Assert.Equal(900, interval.IntervalSeconds);
            Assert.Equal("09:00", interval.WindowStart);

            var weekdays = all.Single(s => s.Name == "Weekdays");
            Assert.Equal([1, 2, 3, 4, 5], weekdays.DaysOfWeek);

            var once = all.Single(s => s.Kind == Schedule.KindOnce);
            Assert.Equal(runAt, once.RunAtUtc);

            var coop = all.Single(s => s.Kind == Schedule.KindSolar);
            Assert.Equal(Schedule.SolarDusk, coop.SolarEvent);
            Assert.Equal(SolarCalculator.CivilTwilightDegrees, coop.SolarElevationDegrees);
            Assert.Equal(20, coop.SolarOffsetMinutes);
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    [Fact]
    public async Task GetEnabled_ReturnsOnlyEnabledSchedules()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var store = BuildStore(tempDir);
            var token = TestContext.Current.CancellationToken;

            await store.CreateAsync(Daily("On", "07:00"), token);
            var off = await store.CreateAsync(Daily("Off", "08:00") with { Enabled = false }, token);

            var enabled = await store.GetEnabledAsync(token);

            Assert.Equal("On", Assert.Single(enabled).Name);

            await store.SetEnabledAsync(off.Id, true, token);
            Assert.Equal(2, (await store.GetEnabledAsync(token)).Count);
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    [Fact]
    public async Task RecordRun_AppendsHistoryAndUpdatesTheSummaryColumns()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var store = BuildStore(tempDir);
            var token = TestContext.Current.CancellationToken;

            var schedule = await store.CreateAsync(Daily("Porch light on", "18:00"), token);
            var firedAt = DateTimeOffset.UtcNow;
            var nextDue = firedAt.AddDays(1);

            // The worker sets the next due time before it dispatches, so recording the run must
            // leave that value alone rather than recomputing it.
            await store.SetNextDueAsync(schedule.Id, nextDue, token);
            await store.RecordRunAsync(
                new ScheduleRun(Guid.NewGuid(), schedule.Id, firedAt, firedAt, ScheduleRun.StatusOk, "switched Porch Light on", 42),
                token);

            var updated = await store.GetAsync(schedule.Id, token);
            Assert.NotNull(updated);
            Assert.Equal(ScheduleRun.StatusOk, updated.LastStatus);
            Assert.Equal("switched Porch Light on", updated.LastDetail);
            Assert.Equal(1, updated.RunCount);
            Assert.NotNull(updated.LastRunUtc);
            Assert.Equal(nextDue.UtcDateTime, updated.NextDueUtc!.Value.UtcDateTime, TimeSpan.FromSeconds(1));

            var run = Assert.Single(await store.GetRunsAsync(schedule.Id, 50, token));
            Assert.Equal(42, run.DurationMs);
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    [Fact]
    public async Task RecordRun_ForAMissedFiring_ShowsTheStatusWithoutClaimingItRan()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var store = BuildStore(tempDir);
            var token = TestContext.Current.CancellationToken;

            var schedule = await store.CreateAsync(Daily("Arm at night", "22:00"), token);
            var now = DateTimeOffset.UtcNow;

            await store.RecordRunAsync(
                new ScheduleRun(Guid.NewGuid(), schedule.Id, now.AddHours(-6), now, ScheduleRun.StatusMissed, "server was down", 0),
                token);

            var updated = await store.GetAsync(schedule.Id, token);

            // Visible as the current state, but it did not run - so neither the count nor the
            // last-run time may move, or a week of downtime would report a week of firings.
            Assert.Equal(ScheduleRun.StatusMissed, updated!.LastStatus);
            Assert.Equal(0, updated.RunCount);
            Assert.Null(updated.LastRunUtc);
            Assert.Single(await store.GetRunsAsync(schedule.Id, 50, token));
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    [Fact]
    public async Task RecordRun_PrunesHistorySoAFastIntervalCannotGrowTheFileForever()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var store = BuildStore(tempDir);
            var token = TestContext.Current.CancellationToken;

            var schedule = await store.CreateAsync(
                Base("Every ten seconds") with { Kind = Schedule.KindInterval, IntervalSeconds = 10 }, token);

            var start = DateTimeOffset.UtcNow.AddHours(-1);
            for (var i = 0; i < 230; i++)
            {
                var firedAt = start.AddSeconds(i * 10);
                await store.RecordRunAsync(
                    new ScheduleRun(Guid.NewGuid(), schedule.Id, firedAt, firedAt, ScheduleRun.StatusOk, $"run {i}", 1),
                    token);
            }

            var runs = await store.GetRunsAsync(schedule.Id, 500, token);

            Assert.Equal(200, runs.Count);

            // The newest survive, not the oldest.
            Assert.Equal("run 229", runs[0].Detail);
            Assert.Equal("run 30", runs[^1].Detail);

            // The count is the true total, not what happens to be left in the history.
            Assert.Equal(230, (await store.GetAsync(schedule.Id, token))!.RunCount);
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    [Fact]
    public async Task Delete_TakesTheRunHistoryWithIt()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var store = BuildStore(tempDir);
            var token = TestContext.Current.CancellationToken;

            var schedule = await store.CreateAsync(Daily("Porch light on", "18:00"), token);
            var now = DateTimeOffset.UtcNow;
            await store.RecordRunAsync(
                new ScheduleRun(Guid.NewGuid(), schedule.Id, now, now, ScheduleRun.StatusOk, "ok", 1), token);

            await store.DeleteAsync(schedule.Id, token);

            Assert.Empty(await store.GetRunsAsync(schedule.Id, 50, token));
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    private static SqliteScheduleStore BuildStore(string dataDirectory) =>
        new(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = dataDirectory }),
            new FakeHostEnvironment(dataDirectory));

    private static Schedule Daily(string name, string timeOfDay) =>
        Base(name) with { Kind = Schedule.KindDaily, TimeOfDay = timeOfDay };

    private static Schedule Base(string name) => new(
        Guid.NewGuid(), name, true, Schedule.KindDaily, "", [], 0, "", "", "", null,
        Schedule.SolarSunset, 0, SolarCalculator.CivilTwilightDegrees,
        new AutomationAction(
            AutomationAction.TypeDevice, Guid.NewGuid(), "on", 60,
            "", "", false, "", "POST", "", "", "application/json"),
        Schedule.CatchUpSkip, null, "", "", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }
}
