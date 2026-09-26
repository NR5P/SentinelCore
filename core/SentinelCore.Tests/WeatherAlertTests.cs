extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// WEATHER ALERTS: which NWS alerts reach the owner, that each reaches them once, what the message
// says, and that the settings section saves and reads back. The NWS itself is a fixture -
// WeatherServiceTests covers the wire.
public sealed class WeatherAlertTests : IDisposable
{
    private readonly string _directory;

    // The worker's clock. The fixture alerts are dated from it, so they are live when the test
    // runs rather than on the day it was written.
    private readonly ManualTime _clock = new(DateTimeOffset.UtcNow);

    public WeatherAlertTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"weather-alerts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Classification ----

    [Theory]
    [InlineData("Tornado Warning", WeatherAlertLevel.Warning)]
    [InlineData("Tornado Watch", WeatherAlertLevel.Watch)]
    [InlineData("Wind Advisory", WeatherAlertLevel.Advisory)]
    [InlineData("Special Weather Statement", WeatherAlertLevel.Advisory)]
    [InlineData("Extreme Wind Warning", WeatherAlertLevel.Warning)]
    [InlineData("Flash Flood Warning", WeatherAlertLevel.Warning)]
    public void LevelOf_ReadsTheNwsSuffix(string eventName, WeatherAlertLevel expected) =>
        Assert.Equal(expected, WeatherAlertPolicy.LevelOf(eventName));

    [Theory]
    [InlineData("Tornado Warning", WeatherAlertPolicy.Tornado)]
    [InlineData("Severe Thunderstorm Watch", WeatherAlertPolicy.SevereThunderstorm)]
    [InlineData("Flash Flood Warning", WeatherAlertPolicy.Flood)]
    [InlineData("Coastal Flood Advisory", WeatherAlertPolicy.Flood)]
    [InlineData("Winter Storm Warning", WeatherAlertPolicy.Winter)]
    [InlineData("Blizzard Warning", WeatherAlertPolicy.Winter)]
    [InlineData("Freezing Rain Advisory", WeatherAlertPolicy.Winter)]
    [InlineData("Wind Chill Warning", WeatherAlertPolicy.Temperature)]
    [InlineData("Extreme Cold Warning", WeatherAlertPolicy.Temperature)]
    [InlineData("Heat Advisory", WeatherAlertPolicy.Temperature)]
    [InlineData("Freeze Warning", WeatherAlertPolicy.Temperature)]
    [InlineData("High Wind Warning", WeatherAlertPolicy.Wind)]
    [InlineData("Wind Advisory", WeatherAlertPolicy.Wind)]
    [InlineData("Gale Warning", WeatherAlertPolicy.Wind)]
    [InlineData("Hurricane Watch", WeatherAlertPolicy.Tropical)]
    [InlineData("Tropical Storm Warning", WeatherAlertPolicy.Tropical)]
    [InlineData("Storm Surge Warning", WeatherAlertPolicy.Tropical)]
    [InlineData("Red Flag Warning", WeatherAlertPolicy.Fire)]
    [InlineData("Dense Fog Advisory", WeatherAlertPolicy.Other)]
    [InlineData("Air Quality Alert", WeatherAlertPolicy.Other)]
    [InlineData("Small Craft Advisory", WeatherAlertPolicy.Other)]
    public void CategoryOf_SortsEveryCommonEvent(string eventName, string expected) =>
        Assert.Equal(expected, WeatherAlertPolicy.CategoryOf(eventName));

    // ---- Selection ----

    [Fact]
    public void Select_KeepsOnlyTheKindsAndLevelAskedFor()
    {
        var options = new WeatherAlertOptions { Enabled = true, MinimumLevel = WeatherAlertOptions.LevelWatch, Wind = false };
        var alerts = new[]
        {
            Alert("1", "Tornado Warning", "Extreme"),
            Alert("2", "Tornado Watch", "Severe"),
            Alert("3", "Wind Advisory", "Minor"),        // below the level, and wind is off anyway
            Alert("4", "High Wind Warning", "Severe"),   // wind is off
            Alert("5", "Dense Fog Advisory", "Minor"),   // "other" is off by default
            Alert("6", "Flood Advisory", "Minor")        // flood is on, but advisories are below the level
        };

        var selection = WeatherAlertPolicy.Select(options, alerts, _ => false);

        Assert.Equal(["1", "2"], selection.ToSend.Select(a => a.Id));
        Assert.Empty(selection.SkippedFollowUps);
    }

    [Fact]
    public void Select_EverythingLevel_IncludesAdvisories_AndOtherWhenTicked()
    {
        var options = new WeatherAlertOptions { Enabled = true, MinimumLevel = WeatherAlertOptions.LevelAdvisory, Other = true };
        var alerts = new[] { Alert("1", "Dense Fog Advisory", "Minor"), Alert("2", "Flood Advisory", "Minor") };

        var selection = WeatherAlertPolicy.Select(options, alerts, _ => false);

        Assert.Equal(2, selection.ToSend.Count);
    }

    [Fact]
    public void Select_WarningsOnly_DropsWatches()
    {
        var options = new WeatherAlertOptions { Enabled = true, MinimumLevel = WeatherAlertOptions.LevelWarning };
        var alerts = new[] { Alert("1", "Tornado Watch", "Severe"), Alert("2", "Tornado Warning", "Extreme") };

        var selection = WeatherAlertPolicy.Select(options, alerts, _ => false);

        Assert.Equal(["2"], selection.ToSend.Select(a => a.Id));
    }

    [Fact]
    public void Select_SkipsWhatWasAlreadySent_AndFollowUpsToIt()
    {
        var options = new WeatherAlertOptions { Enabled = true };
        var seen = new HashSet<string> { "old" };
        var alerts = new[]
        {
            Alert("old", "Tornado Warning", "Extreme"),
            Alert("update", "Tornado Warning", "Extreme", references: ["old"]),
            Alert("fresh", "Severe Thunderstorm Warning", "Severe")
        };

        var selection = WeatherAlertPolicy.Select(options, alerts, seen.Contains);

        Assert.Equal(["fresh"], selection.ToSend.Select(a => a.Id));
        Assert.Equal(["update"], selection.SkippedFollowUps.Select(a => a.Id));
    }

    [Fact]
    public void Select_SendsFollowUps_WhenAskedTo()
    {
        var options = new WeatherAlertOptions { Enabled = true, NotifyOnUpdates = true };
        var seen = new HashSet<string> { "old" };
        var alerts = new[] { Alert("update", "Tornado Warning", "Extreme", references: ["old"]) };

        var selection = WeatherAlertPolicy.Select(options, alerts, seen.Contains);

        Assert.Equal(["update"], selection.ToSend.Select(a => a.Id));
        Assert.Empty(selection.SkippedFollowUps);
    }

    [Fact]
    public void Select_CapsEachCheck_WorstFirst()
    {
        var options = new WeatherAlertOptions { Enabled = true, MinimumLevel = WeatherAlertOptions.LevelAdvisory, Other = true };
        var alerts = Enumerable.Range(0, 8).Select(i => Alert($"minor{i}", "Dense Fog Advisory", "Minor"))
            .Append(Alert("tornado", "Tornado Warning", "Extreme"))
            .ToList();

        var selection = WeatherAlertPolicy.Select(options, alerts, _ => false);

        Assert.Equal(WeatherAlertPolicy.MaximumPerCycle, selection.ToSend.Count);
        Assert.Equal("tornado", selection.ToSend[0].Id);
    }

    // ---- Wording ----

    [Fact]
    public void Body_UnwrapsTheNwsText_AndSaysWhenAndWhere()
    {
        var alert = Alert("1", "Tornado Warning", "Extreme") with
        {
            Headline = "Tornado Warning issued September 25 at 10:50AM EDT until September 25 at 11:30AM EDT by NWS Louisville KY",
            Description = "* WHAT...A tornado was observed on radar\nnear Shively, moving northeast at\n35 mph.\n\n* WHERE...Jefferson County.",
            Instruction = "TAKE COVER NOW! Move to a basement\nor an interior room.",
            Ends = new DateTimeOffset(2026, 9, 25, 15, 30, 0, TimeSpan.Zero),
            AreaDescription = "Jefferson; Oldham",
            Sender = "NWS Louisville KY"
        };

        var body = WeatherAlertPolicy.Body(alert, "America/New_York");

        Assert.StartsWith("Tornado Warning issued", body);
        Assert.Contains("TAKE COVER NOW! Move to a basement or an interior room.", body);
        Assert.Contains("A tornado was observed on radar near Shively, moving northeast at 35 mph.\n\n* WHERE...Jefferson County.", body);
        Assert.Contains("In effect until Fri 11:30 AM.", body);
        Assert.Contains("Areas: Jefferson; Oldham.", body);
        Assert.Contains("Issued by NWS Louisville KY.", body);
        Assert.Equal("Tornado Warning - Louisville, KY", WeatherAlertPolicy.Subject(alert, "Louisville, KY"));
    }

    [Fact]
    public void Body_TrimsAWinterStormEssay()
    {
        var alert = Alert("1", "Winter Storm Warning", "Severe") with { Description = new string('x', 5000) };

        var body = WeatherAlertPolicy.Body(alert, null);

        Assert.True(body.Length < 1100, $"body was {body.Length} characters");
        Assert.Contains("…", body);
    }

    [Fact]
    public void Sample_IsUnmistakablyATest()
    {
        var sample = WeatherAlertPolicy.Sample(DateTimeOffset.UtcNow, "Louisville, KY");

        Assert.Contains("TEST", sample.Headline);
        Assert.Contains("test", WeatherAlertPolicy.Body(sample, null), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Tornado Warning", sample.Event);
    }

    // ---- The worker end to end (with the NWS and the channels faked) ----

    [Fact]
    public async Task Worker_SendsEachNewAlertOnce_AndRemembersAcrossRestarts()
    {
        var statePath = Path.Combine(_directory, "seen.json");
        var source = new FakeSource();
        var notifier = new FakeNotifier { Accept = true };
        source.Alerts = [Alert("t1", "Tornado Warning", "Extreme"), Alert("f1", "Dense Fog Advisory", "Minor")];
        var options = new WeatherAlertOptions { Enabled = true };

        var first = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(1, first.Sent);
        var message = Assert.Single(notifier.Sent);
        Assert.Equal("Tornado Warning - Louisville, KY", message.Subject);
        Assert.True(source.LastFresh);

        // Same alerts still up on the next check: nothing new to say.
        var second = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, second.Sent);
        Assert.Single(notifier.Sent);

        // A new worker with a fresh state object (a restart) reads the file and still stays quiet.
        var restarted = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, restarted.Sent);

        // The reissue references the one already sent, so it is skipped and itself remembered -
        // the reissue of the reissue is skipped too.
        source.Alerts = [Alert("t2", "Tornado Warning", "Extreme", references: ["t1"])];
        var update = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, update.Sent);
        Assert.Equal(1, update.SkippedFollowUps);
        source.Alerts = [Alert("t3", "Tornado Warning", "Extreme", references: ["t2"])];
        var again = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, again.Sent);
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task Worker_StaysQuiet_WhileTheFeedStillListsAnAlertPastItsExpiry()
    {
        // The NWS feed can lag its own expiry times. An alert still listed days after it
        // "expired" was already sent, so neither the next check nor a restart may send it again.
        var statePath = Path.Combine(_directory, "seen.json");
        var source = new FakeSource { Alerts = [Alert("t1", "Tornado Warning", "Extreme")] };
        var notifier = new FakeNotifier { Accept = true };
        var options = new WeatherAlertOptions { Enabled = true };

        var first = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(1, first.Sent);

        _clock.Advance(TimeSpan.FromHours(2));   // just past its end
        var atEnd = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, atEnd.Sent);

        _clock.Advance(WeatherAlertState.Grace + TimeSpan.FromDays(2));   // long past end + grace
        var late = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, late.Sent);
        var lateAgain = await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, lateAgain.Sent);
        Assert.Single(notifier.Sent);

        // Once it finally leaves the feed it is forgotten after the grace period.
        source.Alerts = [];
        _clock.Advance(WeatherAlertState.Grace + TimeSpan.FromMinutes(1));
        await CreateWorker(source, notifier, statePath).RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(0, new WeatherAlertState(statePath).Count);
    }

    [Fact]
    public async Task Worker_RetriesWhenNoChannelAcceptedIt()
    {
        var statePath = Path.Combine(_directory, "seen.json");
        var source = new FakeSource { Alerts = [Alert("t1", "Tornado Warning", "Extreme")] };
        var notifier = new FakeNotifier { Accept = false };
        var options = new WeatherAlertOptions { Enabled = true };
        var worker = CreateWorker(source, notifier, statePath);

        var first = await worker.RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(1, first.Failed);

        notifier.Accept = true;
        var second = await worker.RunOnceAsync(options, CancellationToken.None);
        Assert.Equal(1, second.Sent);
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task Worker_DoesNothingWhenThereIsNothingToCheck()
    {
        var source = new FakeSource { Alerts = null };
        var notifier = new FakeNotifier { Accept = true };

        var run = await CreateWorker(source, notifier, Path.Combine(_directory, "seen.json"))
            .RunOnceAsync(new WeatherAlertOptions { Enabled = true }, CancellationToken.None);

        Assert.Equal(WeatherAlertRun.Nothing, run);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public void State_ForgetsExpiredIds_AfterAGracePeriod()
    {
        var state = new WeatherAlertState(Path.Combine(_directory, "seen.json"));
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        state.MarkSeen("old", now.AddDays(-3));
        state.MarkSeen("recent", now.AddHours(-2));
        state.MarkSeen("live", now.AddHours(2));

        var pruned = state.Prune(now);

        Assert.Equal(1, pruned);
        Assert.False(state.Contains("old"));
        Assert.True(state.Contains("recent"));
        Assert.True(state.Contains("live"));
    }

    // ---- The settings section through the real pipeline ----

    [Fact]
    public async Task Settings_SaveAndReadBack_AndRefuseANonsenseLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        var settingsPath = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(settingsPath, WebHostTestSettings.LoginOffSettings);
        using var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath, _directory));
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync("/api/settings/weather-alerts", new
        {
            Enabled = true,
            CheckIntervalMinutes = 3,
            MinimumLevel = "Warning",
            Tornado = true,
            SevereThunderstorm = false,
            Flood = true,
            Winter = false,
            Wind = false,
            Temperature = false,
            Tropical = false,
            Fire = false,
            Other = false,
            NotifyOnUpdates = true
        }, ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var snapshot = await client.GetAsync("/api/settings", ct);
        var settings = await snapshot.Content.ReadFromJsonAsync<JsonElement>(ct);
        var section = settings.GetProperty("weatherAlerts");
        Assert.True(section.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, section.GetProperty("checkIntervalMinutes").GetInt32());
        Assert.Equal("warning", section.GetProperty("minimumLevel").GetString());
        Assert.False(section.GetProperty("severeThunderstorm").GetBoolean());
        Assert.True(section.GetProperty("notifyOnUpdates").GetBoolean());

        using var bad = await client.PutAsJsonAsync("/api/settings/weather-alerts", new { Enabled = true, MinimumLevel = "loud", Tornado = true }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var nothingTicked = await client.PutAsJsonAsync("/api/settings/weather-alerts", new
        {
            Enabled = true, MinimumLevel = "watch",
            Tornado = false, SevereThunderstorm = false, Flood = false, Winter = false, Wind = false,
            Temperature = false, Tropical = false, Fire = false, Other = false
        }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, nothingTicked.StatusCode);
    }

    [Fact]
    public async Task TestButton_SaysSoWhenNoChannelIsSetUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var settingsPath = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(settingsPath, WebHostTestSettings.LoginOffSettings);
        using var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath, _directory));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/settings/weather-alerts/test", null, ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.False(payload.GetProperty("sent").GetBoolean());
        Assert.Contains("Email", payload.GetProperty("error").GetString());
    }

    // ---- Plumbing ----

    private WeatherAlertWorker CreateWorker(FakeSource source, FakeNotifier notifier, string statePath)
    {
        var settingsPath = Path.Combine(_directory, "appsettings.json");
        if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, "{}");
        return new WeatherAlertWorker(
            source, notifier, new AppSettingsFile(settingsPath), new WeatherAlertState(statePath),
            NullLogger<WeatherAlertWorker>.Instance, _clock);
    }

    // In effect from half an hour ago for another ninety minutes, by the worker's clock.
    private WeatherAlert Alert(string id, string eventName, string severity, string[]? references = null) => new()
    {
        Id = id,
        Event = eventName,
        Severity = severity,
        Headline = $"{eventName} in effect",
        Description = "Details.",
        Onset = _clock.Now.AddMinutes(-30),
        Ends = _clock.Now.AddMinutes(90),
        Expires = _clock.Now.AddMinutes(90),
        AreaDescription = "Jefferson",
        Sender = "NWS Louisville KY",
        MessageType = references is null ? "Alert" : "Update",
        References = references ?? []
    };

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeSource : IWeatherAlertSource
    {
        public IReadOnlyList<WeatherAlert>? Alerts { get; set; }
        public bool LastFresh { get; private set; }

        public Task<WeatherAlertFeed?> GetActiveAlertsAsync(bool fresh, CancellationToken cancellationToken)
        {
            LastFresh = fresh;
            return Task.FromResult(Alerts is null ? null : new WeatherAlertFeed(Alerts, "Louisville, KY", "America/New_York"));
        }
    }

    private sealed class FakeNotifier : IOwnerNotifier
    {
        public bool Accept { get; set; }
        public List<(string Subject, string Text)> Sent { get; } = [];

        public Task<bool> SendAsync(string subject, string text, CancellationToken cancellationToken)
        {
            Sent.Add((subject, text));
            return Task.FromResult(Accept);
        }
    }
}
