using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

// End-to-end over the worker, the runner and the store together, with only the MQTT publisher
// faked - so what these assert is that a schedule coming due really does reach the broker, and
// that the cases where it deliberately should not are the ones intended.
//
// Timing is controlled by writing NextDueUtc directly rather than by waiting for wall-clock
// time to arrive, which keeps the tests instant and free of sleep-and-hope.
public sealed class ScheduleWorkerTests
{
    [Fact]
    public async Task ADueSchedule_FiresAndRecordsAnOkRun()
    {
        await using var harness = new Harness();
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Porch light on", "18:00"), token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunUntilAsync(() => harness.Publisher.Published.Count > 0);

        var published = Assert.Single(harness.Publisher.Published);
        Assert.Equal("home/schedule", published.Topic);

        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal(ScheduleRun.StatusOk, run.Status);

        var updated = await harness.Store.GetAsync(schedule.Id, token);
        Assert.Equal(1, updated!.RunCount);

        // Advanced past now, so the same firing cannot be picked up a second time.
        Assert.NotNull(updated.NextDueUtc);
        Assert.True(updated.NextDueUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AScheduleThatIsNotDueYet_DoesNotFire()
    {
        await using var harness = new Harness();
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Later today", "18:00"), token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddHours(3), token);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(300));

        Assert.Empty(harness.Publisher.Published);
        Assert.Empty(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
    }

    [Fact]
    public async Task ADisabledSchedule_DoesNotFireEvenWhenOverdue()
    {
        await using var harness = new Harness();
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Off", "18:00") with { Enabled = false }, token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(300));

        Assert.Empty(harness.Publisher.Published);
    }

    [Fact]
    public async Task AFiringMissedWhileTheServerWasDown_IsRecordedButNotRun()
    {
        await using var harness = new Harness();
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Porch light on", "18:00"), token);

        // Twenty minutes past due: well beyond the on-time tolerance, and the default policy is
        // to let it go rather than switch a light on at the wrong time.
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddMinutes(-20), token);

        await harness.RunUntilAsync(async () => (await harness.Store.GetRunsAsync(schedule.Id, 10, token)).Count > 0);

        Assert.Empty(harness.Publisher.Published);

        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal(ScheduleRun.StatusMissed, run.Status);
        Assert.Equal(0, (await harness.Store.GetAsync(schedule.Id, token))!.RunCount);
    }

    [Fact]
    public async Task AMissedFiringSetToCatchUp_RunsOnTheWayBackUp()
    {
        await using var harness = new Harness();
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(
            Daily("Arm for the night", "22:00") with { CatchUpPolicy = Schedule.CatchUpRunOnce }, token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddMinutes(-20), token);

        await harness.RunUntilAsync(() => harness.Publisher.Published.Count > 0);

        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal(ScheduleRun.StatusOk, run.Status);
    }

    [Fact]
    public async Task AOneShot_DisablesItselfOnceItHasFired()
    {
        await using var harness = new Harness();
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(
            Base("Unlock the gate at three") with
            {
                Kind = Schedule.KindOnce,
                RunAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2)
            },
            token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunUntilAsync(() => harness.Publisher.Published.Count > 0);

        var updated = await harness.Store.GetAsync(schedule.Id, token);

        // Off, but still there - the run that mattered is in its history, and deleting it would
        // take that with it.
        Assert.False(updated!.Enabled);
        Assert.Equal(1, updated.RunCount);
        Assert.Single(harness.Publisher.Published);
    }

    [Fact]
    public async Task AFailingAction_IsRecordedAsFailedRatherThanStoppingTheScheduler()
    {
        await using var harness = new Harness(mqttEnabled: false);
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Porch light on", "18:00"), token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunUntilAsync(async () => (await harness.Store.GetRunsAsync(schedule.Id, 10, token)).Count > 0);

        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal(ScheduleRun.StatusFailed, run.Status);

        // The message has to say what to do about it, since the history is where someone looks
        // when they find the light did not come on.
        Assert.Contains("MQTT is not enabled", run.Detail);
    }

    [Fact]
    public async Task ASunFollowingSchedule_WithNoLocationSet_StaysUnscheduledInsteadOfGuessing()
    {
        // (0, 0) is what an unset pair of coordinates looks like - see GeoCoordinate.IsValid.
        await using var harness = new Harness(location: new GeoCoordinate(0, 0));
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(
            Base("Close the coop when it gets dark") with
            {
                Kind = Schedule.KindSolar,
                SolarEvent = Schedule.SolarDusk,
                SolarElevationDegrees = SolarCalculator.CivilTwilightDegrees
            },
            token);

        await harness.RunForAsync(TimeSpan.FromMilliseconds(300));

        Assert.Empty(harness.Publisher.Published);
        Assert.Null((await harness.Store.GetAsync(schedule.Id, token))!.NextDueUtc);
    }

    [Fact]
    public async Task ASunFollowingSchedule_WithALocation_GetsAFiringTimeOnItsOwn()
    {
        await using var harness = new Harness(location: new GeoCoordinate(38.2527, -85.7585));
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(
            Base("Close the coop when it gets dark") with
            {
                Kind = Schedule.KindSolar,
                SolarEvent = Schedule.SolarDusk,
                SolarElevationDegrees = SolarCalculator.CivilTwilightDegrees,
                SolarOffsetMinutes = 20
            },
            token);

        await harness.RunUntilAsync(async () => (await harness.Store.GetAsync(schedule.Id, token))!.NextDueUtc is not null);

        var nextDue = (await harness.Store.GetAsync(schedule.Id, token))!.NextDueUtc;

        Assert.NotNull(nextDue);
        Assert.True(nextDue > DateTimeOffset.UtcNow);

        // Dusk is never more than a day away anywhere this schedule makes sense.
        Assert.True(nextDue < DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task ARunAlreadyInFlight_IsNotStartedAgainConcurrently()
    {
        await using var harness = new Harness();
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(
            Base("Every ten seconds") with { Kind = Schedule.KindInterval, IntervalSeconds = 10 }, token);

        // Two overlapping calls into the runner, which is what a manual "run now" landing on top
        // of a scheduled firing looks like.
        harness.Publisher.BlockUntilReleased();
        var first = harness.Runner.ExecuteAsync(schedule, DateTimeOffset.UtcNow, token);
        await WaitUntilAsync(() => harness.Publisher.Waiting);

        var second = await harness.Runner.ExecuteAsync(schedule, DateTimeOffset.UtcNow, token);
        Assert.Equal(ScheduleRun.StatusSkipped, second.Status);

        harness.Publisher.Release();
        Assert.Equal(ScheduleRun.StatusOk, (await first).Status);
    }

    // --- weather -----------------------------------------------------------------------------

    private static readonly IReadOnlyList<ScheduleWeatherTest> Sprinklers =
    [
        new("rainPast", ScheduleWeatherTest.OpAtMost, 0.5, 3),
        new("rainNext", ScheduleWeatherTest.OpAtMost, 0.1, 1)
    ];

    [Fact]
    public async Task AWeatherGatedSchedule_RunsWhenTheWeatherAgrees_AndSaysWhy()
    {
        var weather = new FakeScheduleWeather { Verdict = new(true, "0.2 inches of rain in the last 3 days") };
        await using var harness = new Harness(weather: weather);
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Sprinklers", "06:00") with { WeatherTests = Sprinklers }, token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunUntilAsync(() => harness.Publisher.Published.Count > 0);

        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal(ScheduleRun.StatusOk, run.Status);
        Assert.Contains("weather: 0.2 inches of rain in the last 3 days", run.Detail);
    }

    [Fact]
    public async Task AWeatherGatedSchedule_IsSkippedWhenATestFails_WithTheMeasurementInTheHistory()
    {
        var weather = new FakeScheduleWeather
        {
            Verdict = new(false, "1.2 inches of rain in the last 3 days (needs 0.5 inches of rain or less in the last 3 days)")
        };
        await using var harness = new Harness(weather: weather);
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Sprinklers", "06:00") with { WeatherTests = Sprinklers }, token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunUntilAsync(async () => (await harness.Store.GetRunsAsync(schedule.Id, 10, token)).Count > 0);

        Assert.Empty(harness.Publisher.Published);
        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal(ScheduleRun.StatusSkipped, run.Status);
        Assert.StartsWith("skipped: 1.2 inches of rain in the last 3 days", run.Detail);

        // Skipped is shown on the row but isn't a run: the count and last-run time stay put.
        var updated = await harness.Store.GetAsync(schedule.Id, token);
        Assert.Equal(0, updated!.RunCount);
        Assert.Null(updated.LastRunUtc);
        Assert.StartsWith("skipped:", updated.LastDetail);
    }

    [Fact]
    public async Task AWeatherGatedSchedule_FailsClosedWhenTheWeatherIsUnavailable()
    {
        var weather = new FakeScheduleWeather { Verdict = new(null, "couldn't check the weather: the dashboard didn't answer") };
        await using var harness = new Harness(weather: weather);
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Sprinklers", "06:00") with { WeatherTests = Sprinklers }, token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunUntilAsync(async () => (await harness.Store.GetRunsAsync(schedule.Id, 10, token)).Count > 0);

        Assert.Empty(harness.Publisher.Published);
        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal(ScheduleRun.StatusSkipped, run.Status);
        Assert.Equal("skipped: couldn't check the weather: the dashboard didn't answer", run.Detail);
    }

    [Fact]
    public async Task AScheduleWithoutWeatherTests_NeverAsksForTheWeather()
    {
        var weather = new FakeScheduleWeather();
        await using var harness = new Harness(weather: weather);
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Daily("Porch light on", "18:00"), token);
        await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);

        await harness.RunUntilAsync(() => harness.Publisher.Published.Count > 0);
        Assert.Equal(0, weather.Calls);
    }

    [Fact]
    public async Task AWeatherSchedule_FiresOnTheChangeToTrue_ThenNotAgainUntilItHasClearedAndTheQuietPeriodPassed()
    {
        var weather = new FakeScheduleWeather { Verdict = new(true, "a low of 28°F in the next 2 days") };
        await using var harness = new Harness(weather: weather);
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Base("Frost cloth") with
        {
            Kind = Schedule.KindWeather,
            WeatherTests = [new("lowNext", ScheduleWeatherTest.OpAtMost, 32, 2)]
        }, token);

        async Task CheckAsync(int expectedCalls)
        {
            await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);
            await harness.RunUntilAsync(() => weather.Calls >= expectedCalls);
        }

        // First check: true for the first time - fires.
        await CheckAsync(1);
        Assert.Single(harness.Publisher.Published);
        var afterFirst = await harness.Store.GetAsync(schedule.Id, token);
        Assert.True(afterFirst!.WeatherHeld);
        Assert.NotNull(afterFirst.WeatherFiredAtUtc);
        Assert.Contains("weather: a low of 28°F", Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token)).Detail);
        // The next check is on the half-hour grid, not the action's own timing.
        Assert.True(afterFirst.NextDueUtc > DateTimeOffset.UtcNow);
        Assert.True(afterFirst.NextDueUtc <= DateTimeOffset.UtcNow.AddMinutes(30));

        // Still true: no second firing.
        await CheckAsync(2);
        Assert.Single(harness.Publisher.Published);

        // Clears, then comes back inside the quiet period: still nothing.
        weather.Verdict = new(false, "a low of 40°F in the next 2 days (needs a low of 32°F or colder in the next 2 days)");
        await CheckAsync(3);
        Assert.False((await harness.Store.GetAsync(schedule.Id, token))!.WeatherHeld);
        weather.Verdict = new(true, "a low of 30°F in the next 2 days");
        await CheckAsync(4);
        Assert.Single(harness.Publisher.Published);

        // Ordinary checks record nothing - the history is for firings.
        Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));

        // Cleared again with the quiet period long over: the next true fires.
        await harness.Store.SetWeatherStateAsync(schedule.Id, false, DateTimeOffset.UtcNow.AddHours(-13), token);
        await CheckAsync(5);
        Assert.Equal(2, harness.Publisher.Published.Count);
    }

    [Fact]
    public async Task AWeatherSchedule_RecordsAnOutageOnce_AndChangesNothing()
    {
        var weather = new FakeScheduleWeather { Verdict = new(null, "couldn't check the weather: no location set") };
        await using var harness = new Harness(weather: weather);
        var token = TestContext.Current.CancellationToken;

        var schedule = await harness.Store.CreateAsync(Base("Frost cloth") with
        {
            Kind = Schedule.KindWeather,
            WeatherTests = [new("lowNext", ScheduleWeatherTest.OpAtMost, 32, 2)]
        }, token);

        for (var check = 1; check <= 3; check++)
        {
            await harness.Store.SetNextDueAsync(schedule.Id, DateTimeOffset.UtcNow.AddSeconds(-2), token);
            await harness.RunUntilAsync(() => weather.Calls >= check);
        }

        Assert.Empty(harness.Publisher.Published);
        var run = Assert.Single(await harness.Store.GetRunsAsync(schedule.Id, 10, token));
        Assert.Equal("skipped: couldn't check the weather: no location set", run.Detail);
        Assert.Null((await harness.Store.GetAsync(schedule.Id, token))!.WeatherHeld);
    }

    // --- helpers -----------------------------------------------------------------------------

    private static Schedule Daily(string name, string timeOfDay) =>
        Base(name) with { Kind = Schedule.KindDaily, TimeOfDay = timeOfDay };

    private static Schedule Base(string name) => new(
        Guid.NewGuid(), name, true, Schedule.KindDaily, "", [], 0, "", "", "", null,
        Schedule.SolarSunset, 0, SolarCalculator.CivilTwilightDegrees,
        new AutomationAction(
            AutomationAction.TypeMqtt, null, "", null,
            "home/schedule", "{{schedule_name}}", false, "", "POST", "", "", "application/json"),
        Schedule.CatchUpSkip, null, "", "", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10, CancellationToken.None);
        }

        throw new TimeoutException("The condition was not met in time.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _tempDir = Directory.CreateTempSubdirectory().FullName;
        private readonly ScheduleWorker _worker;

        // Louisville, KY unless a test says otherwise - somewhere with an ordinary sunset, so a
        // sun-following schedule has a next firing within the day.
        private static readonly GeoCoordinate DefaultLocation = new(38.2527, -85.7585);

        public Harness(bool mqttEnabled = true, GeoCoordinate? location = null, IScheduleWeather? weather = null)
        {
            location ??= DefaultLocation;

            Store = new SqliteScheduleStore(
                Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = _tempDir }),
                new FakeHostEnvironment(_tempDir));

            var mqttOptions = new StaticOptionsMonitor<MqttOptions>(new MqttOptions { Enabled = mqttEnabled });
            var deviceStore = new EmptyDeviceStore();
            var bus = new MqttDeviceBus(
                mqttOptions,
                new StaticOptionsMonitor<DeviceHubOptions>(new DeviceHubOptions { Enabled = false }),
                deviceStore,
                NullLogger<MqttDeviceBus>.Instance);

            var dispatcher = new AutomationActionDispatcher(
                new HttpClient(), Publisher, mqttOptions, deviceStore, bus);

            var context = new ScheduleContext(
                new StaticOptionsMonitor<ScheduleOptions>(new ScheduleOptions()),
                new StaticOptionsMonitor<LocationOptions>(new LocationOptions
                {
                    Latitude = location.Latitude,
                    Longitude = location.Longitude
                }),
                NullLogger<ScheduleContext>.Instance);

            Runner = new ScheduleRunner(Store, dispatcher, NullLogger<ScheduleRunner>.Instance);
            _worker = new ScheduleWorker(
                Store, Runner, context, weather ?? new FakeScheduleWeather(), NullLogger<ScheduleWorker>.Instance);
        }

        public SqliteScheduleStore Store { get; }

        public ScheduleRunner Runner { get; }

        public BlockingMqttPublisher Publisher { get; } = new();

        public async Task RunUntilAsync(Func<bool> condition)
        {
            await _worker.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(condition);
            }
            finally
            {
                await _worker.StopAsync(CancellationToken.None);
            }
        }

        public async Task RunUntilAsync(Func<Task<bool>> condition)
        {
            await _worker.StartAsync(CancellationToken.None);
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (await condition()) return;
                    await Task.Delay(10, CancellationToken.None);
                }

                throw new TimeoutException("The condition was not met in time.");
            }
            finally
            {
                await _worker.StopAsync(CancellationToken.None);
            }
        }

        // For the cases that assert nothing happens - there is no event to wait for, so the only
        // option is to let the worker have a few passes and check it stayed quiet.
        public async Task RunForAsync(TimeSpan duration)
        {
            await _worker.StartAsync(CancellationToken.None);
            await Task.Delay(duration, CancellationToken.None);
            await _worker.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            Store.Dispose();
            await Task.Run(() => TempDirectories.Delete(_tempDir));
        }
    }

    // A publisher that can be held open mid-publish, so a second run can be attempted while the
    // first is genuinely still going.
    private sealed class BlockingMqttPublisher : IMqttPublisher
    {
        private readonly ConcurrentQueue<(string Topic, string Payload, bool Retain)> _published = new();
        private TaskCompletionSource? _gate;

        public IReadOnlyCollection<(string Topic, string Payload, bool Retain)> Published => _published;

        public bool Waiting { get; private set; }

        public void BlockUntilReleased() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
        {
            if (_gate is { } gate)
            {
                Waiting = true;
                await gate.Task;
                Waiting = false;
            }

            _published.Enqueue((topic, payload, retain));
        }
    }

    // Answers every weather check with whatever the test last set, and counts the checks.
    internal sealed class FakeScheduleWeather : IScheduleWeather
    {
        private int _calls;

        public ScheduleWeatherVerdict Verdict { get; set; } = new(true, "fine weather");

        public int Calls => Volatile.Read(ref _calls);

        public Task<ScheduleWeatherVerdict> CheckAsync(
            IReadOnlyList<ScheduleWeatherTest> tests, TimeZoneInfo zone, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Verdict);
        }
    }

    private sealed class EmptyDeviceStore : IDeviceStore
    {
        public Task<IReadOnlyList<HubDevice>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HubDevice>>([]);

        public Task<IReadOnlyList<HubDevice>> GetEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HubDevice>>([]);

        public Task<HubDevice?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<HubDevice?>(null);

        public Task<HubDevice> CreateAsync(HubDevice device, CancellationToken cancellationToken) =>
            Task.FromResult(device);

        public Task<HubDevice?> UpdateAsync(Guid id, HubDevice device, CancellationToken cancellationToken) =>
            Task.FromResult<HubDevice?>(device);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task UpdateOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
