using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Weather on the main app's Schedules: "every morning at 6, sprinklers on - only if there's been
// half an inch of rain or less in the last 3 days", and "when a freeze shows up in the forecast".
// The worker side is in ScheduleWorkerTests; this is the measuring, the verdict, the storage and
// the validation. The weather is a fixture, never the network.
public sealed class ScheduleWeatherTests
{
    private const string Chicago = "America/Chicago";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(Chicago);
    // Saturday 17 Oct 2026, 6:00 CDT.
    private static readonly DateTimeOffset Saturday6am = DateTimeOffset.Parse("2026-10-17T11:00:00Z");

    // ---- the dashboard's measuring endpoint ---------------------------------------------------

    [Fact]
    public void Evaluate_TooMuchRain_FailsWithWhatWasMeasured()
    {
        // 0.7 + 0.5 over the last three days (today included); the 5 inches before that is outside.
        var outlook = Outlook([5, 0.7, 0, 0.5]);
        var results = ScheduleWeatherEndpoints.Evaluate(Request(("rainPast", "atMost", 0.5, 3)), outlook, Saturday6am);

        var result = Assert.Single(results);
        Assert.False(result.Holds);
        Assert.Equal(1.2, result.Value);
        Assert.Equal("1.2 inches of rain in the last 3 days", result.Observed);
        Assert.Equal("0.5 inches of rain or less in the last 3 days", result.Wanted);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Evaluate_DryWeek_AndADryForecast_BothHold()
    {
        // The fixture forecasts half an inch (12.7 mm) a day out, so "the next day" sees none of it
        // but "the next 2 days" does.
        var outlook = Outlook([0, 0.1, 0, 0]);
        var results = ScheduleWeatherEndpoints.Evaluate(
            Request(("rainPast", "atMost", 0.5, 3), ("rainNext", "atMost", 0.1, 1), ("rainNext", "atMost", 0.1, 2)),
            outlook, Saturday6am);

        Assert.True(results[0].Holds);
        Assert.Equal("0.1 inches of rain in the last 3 days", results[0].Observed);
        Assert.True(results[1].Holds);
        Assert.Equal("0 inches of rain forecast in the next day", results[1].Observed);
        Assert.False(results[2].Holds);
        Assert.Equal("0.5 inches of rain forecast in the next 2 days", results[2].Observed);
    }

    [Fact]
    public void Evaluate_NoWeather_IsAProblemNotAPass()
    {
        var results = ScheduleWeatherEndpoints.Evaluate(Request(("rainPast", "atMost", 0.5, 3)), null, Saturday6am);

        var result = Assert.Single(results);
        Assert.Null(result.Holds);
        Assert.Contains("LOCATION", result.Problem);
    }

    [Fact]
    public void Evaluate_AnUnknownMetric_IsAProblem()
    {
        var result = Assert.Single(ScheduleWeatherEndpoints.Evaluate(Request(("humidity", "atMost", 50, 1)), Outlook([0]), Saturday6am));
        Assert.Null(result.Holds);
        Assert.Contains("humidity", result.Problem);
    }

    [Fact]
    public void Evaluate_Temperatures_ReadNaturally()
    {
        var outlook = Outlook([0], low: 28);
        var results = ScheduleWeatherEndpoints.Evaluate(
            Request(("lowNext", "atMost", 32, 7), ("tempNow", "atLeast", 40, 1)), outlook, Saturday6am);

        Assert.True(results[0].Holds);
        Assert.Equal("a low of 28°F in the next 7 days", results[0].Observed);
        Assert.True(results[1].Holds);
        Assert.Equal("50°F right now", results[1].Observed);
    }

    // ---- the main app's verdict ---------------------------------------------------------------

    [Fact]
    public void Verdict_AllHold_RunsAndQuotesTheMeasurements()
    {
        var verdict = ScheduleWeatherVerdict.From(
        [
            new(true, "0.1 inches of rain in the last 3 days", "0.5 inches of rain or less in the last 3 days", null),
            new(true, "0 inches of rain forecast in the next day", "0.1 inches of rain or less forecast in the next day", null)
        ]);

        Assert.True(verdict.Holds);
        Assert.Equal("0.1 inches of rain in the last 3 days; 0 inches of rain forecast in the next day", verdict.Detail);
    }

    [Fact]
    public void Verdict_OneFails_SaysWhichAndWhatItNeeded()
    {
        var verdict = ScheduleWeatherVerdict.From(
        [
            new(false, "1.2 inches of rain in the last 3 days", "0.5 inches of rain or less in the last 3 days", null),
            new(true, "0 inches of rain forecast in the next day", "0.1 inches of rain or less forecast in the next day", null)
        ]);

        Assert.False(verdict.Holds);
        Assert.Equal("1.2 inches of rain in the last 3 days (needs 0.5 inches of rain or less in the last 3 days)", verdict.Detail);
    }

    [Fact]
    public void Verdict_AnyUnknown_FailsClosed_EvenIfAnotherTestFailed()
    {
        var verdict = ScheduleWeatherVerdict.From(
        [
            new(false, "1.2 inches of rain in the last 3 days", "0.5 inches of rain or less in the last 3 days", null),
            new(null, "", "0.1 inches of rain or less forecast in the next day", "the forecast rain and snow amounts couldn't be fetched")
        ]);

        Assert.Null(verdict.Holds);
        Assert.Equal("couldn't check the weather: the forecast rain and snow amounts couldn't be fetched", verdict.Detail);
        Assert.Null(ScheduleWeatherVerdict.From([]).Holds);
    }

    // ---- storage ------------------------------------------------------------------------------

    [Fact]
    public async Task Store_RoundTripsWeatherTestsAndState()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var token = TestContext.Current.CancellationToken;
            using var store = BuildStore(tempDir);
            var created = await store.CreateAsync(Base("Sprinklers") with
            {
                WeatherTests = [new("rainPast", "atMost", 0.5, 3), new("rainNext", "atMost", 0.1, 1)]
            }, token);

            var fetched = (await store.GetAsync(created.Id, token))!;
            Assert.Equal(created.WeatherTests, fetched.WeatherTests);
            Assert.Null(fetched.WeatherHeld);

            var firedAt = DateTimeOffset.Parse("2026-10-17T11:00:00Z");
            await store.SetWeatherStateAsync(created.Id, true, firedAt, token);
            fetched = (await store.GetAsync(created.Id, token))!;
            Assert.True(fetched.WeatherHeld);
            Assert.Equal(firedAt, fetched.WeatherFiredAtUtc);

            // An edit replaces the tests but leaves the fired-state alone, so editing a weather
            // schedule can't reset its quiet period.
            await store.UpdateAsync(created.Id, fetched with { WeatherTests = [], WeatherHeld = null, WeatherFiredAtUtc = null }, token);
            fetched = (await store.GetAsync(created.Id, token))!;
            Assert.Empty(fetched.WeatherTests);
            Assert.True(fetched.WeatherHeld);
            Assert.Equal(firedAt, fetched.WeatherFiredAtUtc);
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    [Fact]
    public async Task Store_AddsTheWeatherColumnsToAnOlderDatabase()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var token = TestContext.Current.CancellationToken;
            Guid id;
            using (var store = BuildStore(tempDir))
                id = (await store.CreateAsync(Base("From before weather"), token)).Id;

            // Back to the shape the table had before this feature.
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(tempDir, "schedules.db")};Pooling=false"))
            {
                connection.Open();
                using var drop = connection.CreateCommand();
                drop.CommandText = """
                    ALTER TABLE Schedules DROP COLUMN WeatherTests;
                    ALTER TABLE Schedules DROP COLUMN WeatherHeld;
                    ALTER TABLE Schedules DROP COLUMN WeatherFiredAtUtc;
                    """;
                drop.ExecuteNonQuery();
            }

            using (var store = BuildStore(tempDir))
            {
                var fetched = await store.GetAsync(id, token);
                Assert.NotNull(fetched);
                Assert.Empty(fetched.WeatherTests);
                Assert.Null(fetched.WeatherHeld);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TempDirectories.Delete(tempDir);
        }
    }

    // ---- validation and timing ----------------------------------------------------------------

    [Fact]
    public void Validate_AWeatherScheduleNeedsATest_AndTestsMustBeKnown()
    {
        var context = Context();

        var noTests = Dto(Schedule.KindWeather);
        Assert.Contains(ScheduleEndpoints.Validate(noTests, context), e => e.Contains("at least one weather test"));

        var unknown = Dto(Schedule.KindDaily);
        unknown.WeatherTests = [new("humidity", "atMost", 50, 1), new("rainPast", "below", 1, 3)];
        var errors = ScheduleEndpoints.Validate(unknown, context);
        Assert.Contains(errors, e => e.Contains("humidity"));
        Assert.Contains(errors, e => e.Contains("atLeast"));

        var good = Dto(Schedule.KindWeather);
        good.WeatherTests = [new("lowNext", "atMost", 32, 2)];
        Assert.Empty(ScheduleEndpoints.Validate(good, context));
    }

    [Fact]
    public void ToSchedule_NormalisesTestsAndClampsWindows()
    {
        var dto = Dto(Schedule.KindDaily);
        dto.WeatherTests = [new("RAINPAST", "ATMOST", 0.5, 30), new("rainNext", "atLeast", 1, 0)];

        var schedule = ScheduleEndpoints.ToSchedule(dto, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(new ScheduleWeatherTest("rainPast", "atMost", 0.5, 14), schedule.WeatherTests[0]);
        Assert.Equal(new ScheduleWeatherTest("rainNext", "atLeast", 1, 1), schedule.WeatherTests[1]);
    }

    [Fact]
    public void AWeatherSchedule_ChecksOnTheHalfHour_InsideItsWindow()
    {
        var schedule = Base("Frost cloth") with { Kind = Schedule.KindWeather };

        var next = ScheduleTiming.NextOccurrence(schedule, DateTimeOffset.Parse("2026-10-17T11:07:00Z"), Zone, null);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T11:30:00Z"), next);

        // 07:00-21:00 Chicago: at 22:10 the next check is 07:00 tomorrow.
        var windowed = schedule with { WindowStart = "07:00", WindowEnd = "21:00" };
        var overnight = ScheduleTiming.NextOccurrence(windowed, DateTimeOffset.Parse("2026-10-18T03:10:00Z"), Zone, null);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T12:00:00Z"), overnight);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static ScheduleWeatherRequest Request(params (string Metric, string Op, double Value, int Window)[] tests) => new()
    {
        TimeZone = Chicago,
        Tests = [.. tests.Select(t => new ScheduleWeatherTestDto { Metric = t.Metric, Op = t.Op, Value = t.Value, Window = t.Window })]
    };

    // Rain history ending today, a forecast whose coldest night is `low` three nights out, and
    // half an inch of rain forecast starting a day from now.
    private static WeatherOutlook Outlook(double[] rainByDay, int low = 45)
    {
        var now = Saturday6am;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);
        var past = rainByDay.Select((rain, i) => new WeatherHistoryDay(today.AddDays(i - rainByDay.Length + 1), rain, 0, 40, 60)).ToList();
        var periods = new List<WeatherPeriod>();
        for (var day = 0; day < 7; day++)
        {
            var date = today.AddDays(day);
            var dayStart = OrganizerAutomationSchedule.ToUtc(date, TimeSpan.FromHours(6), Zone);
            periods.Add(new WeatherPeriod { Name = date.DayOfWeek.ToString(), StartTime = dayStart, EndTime = dayStart.AddHours(12), IsDaytime = true, TemperatureF = 70, PrecipitationChance = 20 });
            periods.Add(new WeatherPeriod { Name = $"{date.DayOfWeek} Night", StartTime = dayStart.AddHours(12), EndTime = dayStart.AddHours(24), IsDaytime = false, TemperatureF = day == 3 ? low : 45 });
        }
        var rain = new List<GridValue> { new(now.AddDays(1), now.AddDays(1).AddHours(6), 12.7, "wmoUnit:mm") };
        var grid = new GridSet(rain, [], [], [], [], [], []);
        return new WeatherOutlook { FetchedAt = now, TimeZone = Chicago, Periods = periods, Grid = grid, TemperatureNowF = 50, Past = past };
    }

    private static ScheduleDto Dto(string kind) => new()
    {
        Name = "Sprinklers",
        Kind = kind,
        TimeOfDay = "06:00",
        ActionType = AutomationAction.TypeMqtt,
        MqttTopic = "home/sprinklers",
        MqttPayload = "on"
    };

    private static ScheduleContext Context() => new(
        new StaticOptionsMonitor<ScheduleOptions>(new ScheduleOptions()),
        new StaticOptionsMonitor<LocationOptions>(new LocationOptions()),
        NullLogger<ScheduleContext>.Instance);

    private static SqliteScheduleStore BuildStore(string dataDirectory) => new(
        Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = dataDirectory }),
        new FakeHostEnvironment(dataDirectory));

    private static Schedule Base(string name) => new(
        Guid.NewGuid(), name, true, Schedule.KindDaily, "06:00", [], 0, "", "", "", null,
        Schedule.SolarSunset, 0, SolarCalculator.CivilTwilightDegrees,
        new AutomationAction(
            AutomationAction.TypeMqtt, null, "", null,
            "home/sprinklers", "on", false, "", "POST", "", "", "application/json"),
        Schedule.CatchUpSkip, null, "", "", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

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
