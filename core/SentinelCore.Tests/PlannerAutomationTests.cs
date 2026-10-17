using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Reminders-and-calendar automations and weather tests: "every Saturday remind us to water the
// plants - unless it's rained an inch this week", "when a freeze shows up in the forecast, remind
// the admins to check the pipes". Plus the Admins space they can be for, and what the wall kiosk
// shows. The weather is a fixture, never the network.
public sealed class PlannerAutomationTests : IDisposable
{
    private const string Chicago = "America/Chicago";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(Chicago);
    // Saturday 17 Oct 2026, 8:00 CDT.
    private static readonly DateTimeOffset Saturday8am = DateTimeOffset.Parse("2026-10-17T13:00:00Z");

    private readonly List<string> _directories = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        foreach (var directory in _directories)
        {
            try { TempDirectories.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeWeather : IAutomationWeather
    {
        public WeatherOutlook? Outlook { get; set; }
        public int Calls { get; private set; }
        public Task<WeatherOutlook?> GetOutlookAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Outlook);
        }
    }

    private sealed class SilentNotifier : IOrganizerAutomationNotifier
    {
        public List<string> Pushes { get; } = [];
        public Task<AutomationChannels> ChannelsAsync(CancellationToken cancellationToken) => Task.FromResult(new AutomationChannels(true, true));
        public Task<AutomationSendResult> PushAsync(IReadOnlyCollection<string>? usernames, string title, string body, string? organizerItemId, CancellationToken cancellationToken)
        {
            Pushes.Add(body);
            return Task.FromResult(new AutomationSendResult(true, "Notified."));
        }
        public Task<AutomationSendResult> EmailAsync(IReadOnlyCollection<string> usernames, IReadOnlyCollection<string> addresses, string subject, string body, CancellationToken cancellationToken) =>
            Task.FromResult(new AutomationSendResult(true, "Emailed."));
    }

    private sealed record Bench(SqliteOrganizerStore Store, SqliteOrganizerAutomationStore Automations, OrganizerAutomationEngine Engine,
        ManualTime Time, FakeWeather Weather, SilentNotifier Notifier)
    {
        public List<OrganizerItem> All(string kind) =>
            [.. Store.GetItemsAsync(OrganizerUser.Shared, kind, null, CancellationToken.None).Result];
    }

    private async Task<Bench> NewBenchAsync(DateTimeOffset now)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"planner-automation-{Guid.NewGuid():N}");
        _directories.Add(directory);
        var store = new SqliteOrganizerStore(directory);
        var automations = new SqliteOrganizerAutomationStore(directory);
        _disposables.Add(store);
        _disposables.Add(automations);
        var time = new ManualTime(now);
        var weather = new FakeWeather();
        var notifier = new SilentNotifier();
        var engine = new OrganizerAutomationEngine(store, automations, notifier, time, () => { }, NullLogger.Instance, weather);
        await engine.InitializeAsync(Ct);
        return new Bench(store, automations, engine, time, weather, notifier);
    }

    // A week of rain history ending today, and a forecast whose coldest night is `low` on the
    // night three days out (Tuesday).
    private static WeatherOutlook Outlook(DateTimeOffset now, double[] rainByDay, int low = 45, int high = 70)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);
        var past = rainByDay.Select((rain, i) => new WeatherHistoryDay(today.AddDays(i - rainByDay.Length + 1), rain, 0, 40, 60)).ToList();
        var periods = new List<WeatherPeriod>();
        for (var day = 0; day < 7; day++)
        {
            var date = today.AddDays(day);
            var dayStart = OrganizerAutomationSchedule.ToUtc(date, TimeSpan.FromHours(6), Zone);
            periods.Add(new WeatherPeriod { Name = date.DayOfWeek.ToString(), StartTime = dayStart, EndTime = dayStart.AddHours(12), IsDaytime = true, TemperatureF = high, PrecipitationChance = 20 });
            periods.Add(new WeatherPeriod { Name = $"{date.DayOfWeek} Night", StartTime = dayStart.AddHours(12), EndTime = dayStart.AddHours(24), IsDaytime = false, TemperatureF = day == 3 ? low : 45 });
        }
        var rain = new List<GridValue> { new(now.AddDays(1), now.AddDays(1).AddHours(6), 12.7, "wmoUnit:mm") };
        var grid = new GridSet(rain, [], [], [], [], [], []);
        return new WeatherOutlook { FetchedAt = now, TimeZone = Chicago, Periods = periods, Grid = grid, TemperatureNowF = 50, Past = past };
    }

    private static OrganizerAutomation WaterThePlants() => new()
    {
        Id = "water",
        Name = "Water the plants",
        SpaceId = OrganizerSpaceIds.Family,
        TimeZone = Chicago,
        Trigger = new BoardAutomationTrigger
        {
            Type = OrganizerAutomationTypes.Schedule,
            Schedule = new BoardAutomationSchedule { Repeat = "daily", Time = "08:00", Days = [6], TimeZone = Chicago },
        },
        Conditions = [new BoardAutomationCondition { Type = "weather", Metric = WeatherRuleMetrics.RainPast, Op = "atMost", Value = 1, Window = 7 }],
        Actions = [new BoardAutomationAction { Type = "createReminder", Title = "Water the plants", InDays = 0, Time = "09:00", SkipIfExists = true }],
    };

    private static OrganizerAutomation CheckThePipes() => new()
    {
        Id = "pipes",
        Name = "Freeze coming",
        SpaceId = OrganizerSpaceIds.Family,
        TimeZone = Chicago,
        Trigger = new BoardAutomationTrigger
        {
            Type = OrganizerAutomationTypes.Weather,
            Weather = new BoardAutomationCondition { Type = "weather", Metric = WeatherRuleMetrics.LowNext, Op = "atMost", Value = 32, Window = 7 },
        },
        Actions =
        [
            new BoardAutomationAction { Type = "createReminder", Title = "Check the pipes - low of {weather} {weatherDay}", InDays = -1, FromWeatherDay = true, Priority = 3 },
            new BoardAutomationAction { Type = "notify", Everyone = true, Text = "Freeze {weatherDay}: {weather}" },
        ],
    };

    // ---- the weather math ---------------------------------------------------------------------

    [Fact]
    public void RainPast_AddsUpTheLastNDays_IncludingToday()
    {
        var outlook = Outlook(Saturday8am, [5, 0.25, 0, 0.5, 0, 0, 0, 0.1]);
        var week = WeatherRuleMetrics.Measure(outlook, WeatherRuleMetrics.RainPast, 7, Saturday8am, Zone);
        Assert.Equal(0.85, week.Value);
        var twoDays = WeatherRuleMetrics.Measure(outlook, WeatherRuleMetrics.RainPast, 2, Saturday8am, Zone);
        Assert.Equal(0.1, twoDays.Value);
    }

    [Fact]
    public void LowNext_FindsTheColdestNight_AndItsDay()
    {
        var outlook = Outlook(Saturday8am, [0], low: 28);
        var measure = WeatherRuleMetrics.Measure(outlook, WeatherRuleMetrics.LowNext, 7, Saturday8am, Zone);
        Assert.Equal(28, measure.Value);
        Assert.Equal(new DateOnly(2026, 10, 20), measure.Day);
        Assert.Equal("28°F", measure.Words);
        // Only two days ahead: the freeze on day three isn't in it.
        Assert.Equal(45, WeatherRuleMetrics.Measure(outlook, WeatherRuleMetrics.LowNext, 2, Saturday8am, Zone).Value);
    }

    [Fact]
    public void RainNext_ConvertsTheGridToInches()
    {
        var measure = WeatherRuleMetrics.Measure(Outlook(Saturday8am, [0]), WeatherRuleMetrics.RainNext, 3, Saturday8am, Zone);
        Assert.Equal(0.5, measure.Value);
    }

    [Fact]
    public void NoHistory_IsNotTheSameAsNoRain()
    {
        var outlook = Outlook(Saturday8am, [0]);
        var measure = WeatherRuleMetrics.Measure(new WeatherOutlook { Periods = outlook.Periods, Past = null }, WeatherRuleMetrics.RainPast, 7, Saturday8am, Zone);
        Assert.Null(measure.Value);
        Assert.NotNull(measure.Problem);
    }

    // ---- rules --------------------------------------------------------------------------------

    [Fact]
    public async Task WaterThePlants_RemindsOnADryWeek()
    {
        var bench = await NewBenchAsync(Saturday8am.AddMinutes(-1));
        bench.Weather.Outlook = Outlook(Saturday8am, [0, 0, 0.2, 0, 0, 0, 0]);
        await bench.Engine.SaveAsync(WaterThePlants(), Ct);

        bench.Time.Now = Saturday8am.AddSeconds(1);
        await bench.Engine.RunDueSchedulesAsync(Ct);

        var reminder = Assert.Single(bench.All(OrganizerKinds.Reminder));
        Assert.Equal("Water the plants", reminder.Fields["title"]!.GetValue<string>());
        Assert.Equal(OrganizerSpaceIds.Family, reminder.SpaceId);
        Assert.Equal("2026-10-17T09:00:00-05:00", reminder.Fields["due"]!.GetValue<string>());
        Assert.False(reminder.Fields["allDay"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WaterThePlants_SaysWhyItSkipped_AfterAWetWeek()
    {
        var bench = await NewBenchAsync(Saturday8am.AddMinutes(-1));
        bench.Weather.Outlook = Outlook(Saturday8am, [0, 0.8, 0.6, 0, 0, 0, 0]);
        await bench.Engine.SaveAsync(WaterThePlants(), Ct);

        bench.Time.Now = Saturday8am.AddSeconds(1);
        await bench.Engine.RunDueSchedulesAsync(Ct);

        Assert.Empty(bench.All(OrganizerKinds.Reminder));
        var run = Assert.Single(await bench.Automations.GetRunsAsync("water", Ct));
        Assert.True(run.Ok);
        Assert.Contains("1.4 inches", run.Message);
    }

    [Fact]
    public async Task WeatherUnavailable_DoesNothing_AndSaysSo()
    {
        var bench = await NewBenchAsync(Saturday8am.AddMinutes(-1));
        bench.Weather.Outlook = null;
        await bench.Engine.SaveAsync(WaterThePlants(), Ct);
        bench.Time.Now = Saturday8am.AddSeconds(1);
        await bench.Engine.RunDueSchedulesAsync(Ct);

        Assert.Empty(bench.All(OrganizerKinds.Reminder));
        Assert.Contains("LOCATION", Assert.Single(await bench.Automations.GetRunsAsync("water", Ct)).Message);
    }

    [Fact]
    public async Task Freeze_FiresOnceWhenItAppears_WithTheDayBefore_AndRearmsAfterItPasses()
    {
        var bench = await NewBenchAsync(Saturday8am);
        bench.Weather.Outlook = Outlook(Saturday8am, [0], low: 40);
        await bench.Engine.SaveAsync(CheckThePipes(), Ct);

        // No freeze yet.
        await bench.Engine.RunWeatherRulesAsync(Ct);
        Assert.Empty(bench.All(OrganizerKinds.Reminder));

        // Half an hour later the forecast shows 28 on Tuesday night.
        bench.Time.Now = Saturday8am.AddMinutes(31);
        bench.Weather.Outlook = Outlook(bench.Time.Now, [0], low: 28);
        await bench.Engine.RunWeatherRulesAsync(Ct);
        var reminder = Assert.Single(bench.All(OrganizerKinds.Reminder));
        Assert.Equal("Check the pipes - low of 28°F Tuesday night", reminder.Fields["title"]!.GetValue<string>());
        Assert.Equal("2026-10-19", reminder.Fields["due"]!.GetValue<string>());
        Assert.Equal(3, reminder.Fields["priority"]!.GetValue<int>());
        Assert.Equal("Freeze Tuesday night: 28°F", Assert.Single(bench.Notifier.Pushes));

        // Still freezing at the next look: nothing new.
        bench.Time.Now = bench.Time.Now.AddMinutes(31);
        await bench.Engine.RunWeatherRulesAsync(Ct);
        Assert.Single(bench.All(OrganizerKinds.Reminder));

        // A day later it's warm again, then another freeze shows up: a second reminder.
        bench.Time.Now = bench.Time.Now.AddDays(1);
        bench.Weather.Outlook = Outlook(bench.Time.Now, [0], low: 40);
        await bench.Engine.RunWeatherRulesAsync(Ct);
        bench.Time.Now = bench.Time.Now.AddMinutes(31);
        bench.Weather.Outlook = Outlook(bench.Time.Now, [0], low: 30);
        await bench.Engine.RunWeatherRulesAsync(Ct);
        Assert.Equal(2, bench.All(OrganizerKinds.Reminder).Count);
    }

    [Fact]
    public async Task Freeze_FlickeringForecast_IsOneReminder()
    {
        var bench = await NewBenchAsync(Saturday8am);
        bench.Weather.Outlook = Outlook(Saturday8am, [0], low: 31);
        await bench.Engine.SaveAsync(CheckThePipes(), Ct);
        await bench.Engine.RunWeatherRulesAsync(Ct);
        Assert.Single(bench.All(OrganizerKinds.Reminder));

        foreach (var low in new[] { 33, 31, 33, 31 })
        {
            bench.Time.Now = bench.Time.Now.AddMinutes(31);
            bench.Weather.Outlook = Outlook(bench.Time.Now, [0], low: low);
            await bench.Engine.RunWeatherRulesAsync(Ct);
        }
        Assert.Single(bench.All(OrganizerKinds.Reminder));
    }

    [Fact]
    public async Task Weather_IsCheckedEveryHalfHour_NotEveryLoop()
    {
        var bench = await NewBenchAsync(Saturday8am);
        bench.Weather.Outlook = Outlook(Saturday8am, [0], low: 40);
        await bench.Engine.SaveAsync(CheckThePipes(), Ct);
        await bench.Engine.RunWeatherRulesAsync(Ct);
        bench.Time.Now = Saturday8am.AddMinutes(5);
        await bench.Engine.RunWeatherRulesAsync(Ct);
        Assert.Equal(1, bench.Weather.Calls);
    }

    [Fact]
    public async Task CreateEvent_AllDayAndTimed_InTheRulesSpace()
    {
        var bench = await NewBenchAsync(Saturday8am);
        var rule = new OrganizerAutomation
        {
            Id = "events",
            Name = "Events",
            SpaceId = OrganizerSpaceIds.Family,
            TimeZone = Chicago,
            Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.Schedule, Schedule = new BoardAutomationSchedule { Repeat = "daily", Time = "07:00", TimeZone = Chicago } },
            Actions =
            [
                new BoardAutomationAction { Type = "createEvent", Title = "Bin day", InDays = 2, SkipIfExists = true },
                new BoardAutomationAction { Type = "createEvent", Title = "Mow", InDays = 1, Time = "17:30", DurationMinutes = 90, Location = "yard" },
            ],
        };
        await bench.Automations.SaveAsync(rule, Ct);
        await bench.Engine.RunNowAsync("events", Ct);
        await bench.Engine.RunNowAsync("events", Ct);

        var events = bench.All(OrganizerKinds.Event);
        var bins = Assert.Single(events, e => e.Fields["title"]!.GetValue<string>() == "Bin day");
        Assert.Equal("2026-10-19", bins.Fields["start"]!.GetValue<string>());
        Assert.Equal("2026-10-20", bins.Fields["end"]!.GetValue<string>());
        Assert.True(bins.Fields["allDay"]!.GetValue<bool>());
        // Mow has no "skip if there", so two runs make two.
        var mow = events.Where(e => e.Fields["title"]!.GetValue<string>() == "Mow").ToList();
        Assert.Equal(2, mow.Count);
        Assert.Equal("2026-10-18T17:30:00-05:00", mow[0].Fields["start"]!.GetValue<string>());
        Assert.Equal("2026-10-18T19:00:00-05:00", mow[0].Fields["end"]!.GetValue<string>());
        Assert.Equal("yard", mow[0].Fields["location"]!.GetValue<string>());
    }

    [Fact]
    public async Task SkipIfExists_LooksAtOpenRemindersOnly()
    {
        var bench = await NewBenchAsync(Saturday8am.AddMinutes(-1));
        bench.Weather.Outlook = Outlook(Saturday8am, [0]);
        await bench.Engine.SaveAsync(WaterThePlants(), Ct);
        await bench.Engine.RunNowAsync("water", Ct);
        await bench.Engine.RunNowAsync("water", Ct);
        var reminder = Assert.Single(bench.All(OrganizerKinds.Reminder));

        // Ticked off: next time makes a fresh one.
        var done = new OrganizerChange { Id = reminder.Id, Kind = OrganizerKinds.Reminder };
        done.Fields["completed"] = true;
        done.Clocks["completed"] = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():D13}-0000-phone";
        await bench.Store.ApplyChangesAsync(OrganizerUser.Shared, [done], null, Ct);
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.RunNowAsync("water", Ct);
        Assert.Equal(2, bench.All(OrganizerKinds.Reminder).Count);
    }

    // ---- validation ---------------------------------------------------------------------------

    [Fact]
    public async Task Validation_PlannerRules()
    {
        var bench = await NewBenchAsync(Saturday8am);
        var directory = Path.Combine(Path.GetTempPath(), $"planner-accounts-{Guid.NewGuid():N}");
        _directories.Add(directory);
        var accounts = new SqliteAccountStore(directory);
        _disposables.Add(accounts);
        Assert.Empty(await OrganizerAutomationEndpoints.ValidateAsync(WaterThePlants(), OrganizerUser.Shared, bench.Store, accounts, Ct));
        Assert.Empty(await OrganizerAutomationEndpoints.ValidateAsync(CheckThePipes(), OrganizerUser.Shared, bench.Store, accounts, Ct));

        var noSpace = WaterThePlants();
        noSpace.SpaceId = null;
        Assert.Contains(await OrganizerAutomationEndpoints.ValidateAsync(noSpace, OrganizerUser.Shared, bench.Store, accounts, Ct), e => e.Contains("who the reminders"));

        var cardStep = WaterThePlants();
        cardStep.Actions.Add(new BoardAutomationAction { Type = "createCard", Title = "x" });
        Assert.NotEmpty(await OrganizerAutomationEndpoints.ValidateAsync(cardStep, OrganizerUser.Shared, bench.Store, accounts, Ct));

        var badWeather = WaterThePlants();
        badWeather.Conditions[0].Window = 30;
        Assert.Contains(await OrganizerAutomationEndpoints.ValidateAsync(badWeather, OrganizerUser.Shared, bench.Store, accounts, Ct), e => e.Contains("look back"));

        var weatherDayWithoutWeather = WaterThePlants();
        weatherDayWithoutWeather.Conditions.Clear();
        weatherDayWithoutWeather.Actions[0].FromWeatherDay = true;
        Assert.Contains(await OrganizerAutomationEndpoints.ValidateAsync(weatherDayWithoutWeather, OrganizerUser.Shared, bench.Store, accounts, Ct), e => e.Contains("day of the weather"));
    }

    // ---- the Admins space and the kiosk -------------------------------------------------------

    [Fact]
    public async Task AdminsSpace_FollowsTheAdmins_AndCantBeEditedByHand()
    {
        var bench = await NewBenchAsync(Saturday8am);
        Assert.True(await bench.Store.SyncAdminsSpaceAsync(["Morgan", "alex"], Ct));
        Assert.False(await bench.Store.SyncAdminsSpaceAsync(["alex", "morgan"], Ct));

        var morgan = new OrganizerUser("morgan", null);
        var riley = new OrganizerUser("riley", null);
        Assert.Contains(await bench.Store.GetVisibleSpacesAsync(morgan, Ct), s => s.Id == OrganizerSpaceIds.Admins);
        Assert.DoesNotContain(await bench.Store.GetVisibleSpacesAsync(riley, Ct), s => s.Id == OrganizerSpaceIds.Admins);

        Assert.Null(await bench.Store.UpdateGroupAsync(morgan, OrganizerSpaceIds.Admins, "Mine", ["morgan"], null, Ct));
        Assert.False(await bench.Store.DeleteGroupAsync(morgan, OrganizerSpaceIds.Admins, Ct));

        // Riley promoted, Alex demoted.
        Assert.True(await bench.Store.SyncAdminsSpaceAsync(["morgan", "riley"], Ct));
        Assert.Contains(await bench.Store.GetVisibleSpacesAsync(riley, Ct), s => s.Id == OrganizerSpaceIds.Admins);
        Assert.DoesNotContain(await bench.Store.GetVisibleSpacesAsync(new OrganizerUser("alex", null), Ct), s => s.Id == OrganizerSpaceIds.Admins);
    }

    [Fact]
    public void Kiosk_ShowsOpenDatedTopLevelReminders_WithTheirAudience()
    {
        var now = Saturday8am;
        OrganizerItem Reminder(string id, string space, JsonObject fields, string? parent = null) => new()
        {
            Id = id, Kind = OrganizerKinds.Reminder, SpaceId = space, ParentId = parent, Fields = fields,
        };
        Assert.Equal("everyone", KioskReminders.Shown(Reminder("a", "family", new JsonObject { ["title"] = "Bins", ["due"] = "2026-10-17" }), now)!.Audience);
        Assert.Equal("admins", KioskReminders.Shown(Reminder("b", OrganizerSpaceIds.Admins, new JsonObject { ["title"] = "Pipes", ["due"] = "2026-10-17T18:00:00-05:00" }), now)!.Audience);
        Assert.Null(KioskReminders.Shown(Reminder("c", "family", new JsonObject { ["title"] = "Done", ["due"] = "2026-10-17", ["completed"] = true }), now));
        Assert.Null(KioskReminders.Shown(Reminder("d", "family", new JsonObject { ["title"] = "Someday" }), now));
        Assert.Null(KioskReminders.Shown(Reminder("e", "family", new JsonObject { ["title"] = "Next week", ["due"] = "2026-10-24" }), now));
        Assert.Null(KioskReminders.Shown(Reminder("f", "family", new JsonObject { ["title"] = "Step", ["due"] = "2026-10-17" }, parent: "a"), now));
    }
}
