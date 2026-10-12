using System.Globalization;
using System.Net;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The app-wide weather warning banner: /api/weather/warnings and the "seen" bookkeeping ported
/// from the dashboard's kiosk-weather-alert.js.
/// </summary>
public sealed class WeatherWarningsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sc-wxwarn-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string PrefsPath => Path.Combine(_directory, "settings.json");

    private WeatherWarningTracker NewTracker() => new(new PreferenceStore(PrefsPath), () => _now);

    private WeatherWarning Alert(string id, string level = "warning", double? endsInHours = 2, params string[] references) => new()
    {
        Id = id,
        Event = "Tornado Warning",
        Level = level,
        Headline = "Tornado Warning issued for Douglas County",
        Ends = endsInHours is { } hours ? _now.AddHours(hours) : null,
        References = references.ToList(),
    };

    // ---------------------------------------------------------------- Route

    [Fact]
    public async Task Warnings_decode()
    {
        using var server = new FakeServer().OnGet("/api/weather/warnings", """
            { "enabled": true, "wakeMinutes": 10, "openRadar": true,
              "alerts": [ { "id": "urn:oid:2", "event": "Tornado Warning", "level": "warning", "category": "tornado",
                            "severity": "Extreme", "headline": "Take cover", "instruction": "Go to a basement.",
                            "ends": "2026-10-12T23:30:00+00:00", "references": ["urn:oid:1"] },
                          { "id": "urn:oid:3", "event": "Flood Advisory", "level": "advisory", "category": "flood",
                            "severity": "Minor", "headline": "", "instruction": "", "ends": null, "references": [] } ] }
            """);
        using var client = new SentinelClient(() => server.BaseUrl, NullSentinelLog.Instance);

        var warnings = await client.WeatherWarningsAsync();

        Assert.NotNull(warnings);
        Assert.True(warnings!.Enabled);
        Assert.Equal(2, warnings.Alerts.Count);
        var first = warnings.Alerts[0];
        Assert.Equal("warning", first.Level);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 23, 30, 0, TimeSpan.Zero), first.Ends);
        Assert.Equal(new[] { "urn:oid:1" }, first.References);
        Assert.Null(warnings.Alerts[1].Ends);
        Assert.Contains("GET /api/weather/warnings", server.Requests);
    }

    [Fact]
    public async Task An_older_server_without_the_route_is_nothing_to_show()
    {
        using var server = new FakeServer().OnGet("/api/weather/warnings", """{"error":"Not found"}""", HttpStatusCode.NotFound);
        using var client = new SentinelClient(() => server.BaseUrl, NullSentinelLog.Instance);

        Assert.Null(await client.WeatherWarningsAsync());
    }

    // ---------------------------------------------------------------- Seen bookkeeping

    [Fact]
    public void New_alerts_are_unseen_and_ended_ones_are_dropped()
    {
        var tracker = NewTracker();
        tracker.Update(new[] { Alert("a"), Alert("b", "watch", endsInHours: -1), Alert("c", "advisory", endsInHours: null) });

        Assert.Equal(new[] { "a", "c" }, tracker.Active.Select(a => a.Id));
        Assert.Equal(new[] { "a", "c" }, tracker.Unseen.Select(a => a.Id));
        Assert.Equal("warning", tracker.ActiveLevel);
    }

    [Fact]
    public void Marking_seen_hides_the_banner_but_keeps_the_dot_and_survives_a_restart()
    {
        var tracker = NewTracker();
        var alerts = new[] { Alert("a") };
        tracker.Update(alerts);
        tracker.MarkSeen(tracker.Unseen);

        Assert.Empty(tracker.Unseen);
        Assert.Single(tracker.Active);

        var reopened = NewTracker();
        reopened.Update(alerts);
        Assert.Empty(reopened.Unseen);
    }

    [Fact]
    public void A_reissue_of_a_seen_alert_is_seen_and_remembered_in_its_own_right()
    {
        var tracker = NewTracker();
        tracker.Update(new[] { Alert("v1", endsInHours: 1) });
        tracker.MarkSeen(tracker.Unseen);

        // v2 reissues v1; v3 later reissues v2 only, after v1 has been forgotten.
        tracker.Update(new[] { Alert("v2", endsInHours: 3, references: "v1") });
        Assert.Empty(tracker.Unseen);

        _now = _now.AddHours(2);   // v1's memory has expired; v2's has not
        var reopened = NewTracker();
        reopened.Update(new[] { Alert("v3", endsInHours: 2, references: "v2") });
        Assert.Empty(reopened.Unseen);
    }

    [Fact]
    public void An_unrelated_or_upgraded_alert_flashes_again()
    {
        var tracker = NewTracker();
        tracker.Update(new[] { Alert("watch1", "watch") });
        tracker.MarkSeen(tracker.Unseen);

        tracker.Update(new[] { Alert("watch1", "watch"), Alert("warn1", "warning") });
        Assert.Equal(new[] { "warn1" }, tracker.Unseen.Select(a => a.Id));
    }

    [Fact]
    public void Seen_entries_expire_with_the_alert_or_after_a_day()
    {
        var tracker = NewTracker();
        tracker.Update(new[] { Alert("a", endsInHours: 1), Alert("b", endsInHours: null) });
        tracker.MarkSeen(tracker.Unseen);

        _now = _now.AddHours(25);
        var reopened = NewTracker();
        // Both remembered ids have expired: the same ids (if the server still sent them) show again.
        reopened.Update(new[] { new WeatherWarning { Id = "a", Ends = _now.AddHours(1) }, new WeatherWarning { Id = "b" } });
        Assert.Equal(2, reopened.Unseen.Count);
    }

    [Fact]
    public void No_alerts_or_no_server_answer_means_nothing_active()
    {
        var tracker = NewTracker();
        tracker.Update(null);
        Assert.Empty(tracker.Active);
        Assert.Null(tracker.ActiveLevel);
    }

    [Fact]
    public void A_damaged_seen_entry_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        new PreferenceStore(PrefsPath).Set(WeatherWarningTracker.SeenKey, "not json");
        var tracker = NewTracker();
        tracker.Update(new[] { Alert("a") });
        Assert.Single(tracker.Unseen);
    }

    // ---------------------------------------------------------------- Banner text

    private static readonly WeatherClock Utc = new(TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public void Until_shows_the_time_today_and_the_weekday_otherwise()
    {
        Assert.Equal("until 11:30 PM", WeatherWarningTracker.UntilText(
            new WeatherWarning { Ends = new DateTimeOffset(2026, 10, 12, 23, 30, 0, TimeSpan.Zero) }, Utc, _now));
        Assert.Equal("until Tue 1:15 AM", WeatherWarningTracker.UntilText(
            new WeatherWarning { Ends = new DateTimeOffset(2026, 10, 13, 1, 15, 0, TimeSpan.Zero) }, Utc, _now));
        Assert.Equal("", WeatherWarningTracker.UntilText(new WeatherWarning(), Utc, _now));
    }

    [Fact]
    public void More_and_event_text()
    {
        Assert.Equal("", WeatherWarningTracker.MoreText(1));
        Assert.Equal("+2 more", WeatherWarningTracker.MoreText(3));
        Assert.Equal("TORNADO WARNING", WeatherWarningTracker.EventText(new WeatherWarning { Event = "Tornado Warning" }));
    }
}
