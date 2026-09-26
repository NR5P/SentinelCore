using System.Text;
using System.Text.Json;

namespace SentinelCore.Web;

// Weather alerts to the owner: WEATHER ALERTS in Settings. Every few minutes the worker asks the
// National Weather Service what is in force for the location, keeps the ones the owner asked for
// (tornado, flood, ... at watch level or above), and sends each new one once through
// OwnerNotifier - the same email / Telegram / push path the outage and login alerts use.
//
// "Once" is the hard part. The NWS reissues a tornado warning every fifteen minutes or so as it
// tracks the storm, and each reissue is a brand-new alert id that references the one it replaces.
// WeatherAlertState remembers what has been sent, across restarts, and a follow-up to something
// already sent is skipped unless the owner asked for follow-ups.

// What the worker needs from WeatherService: the active alerts for the configured location, and
// enough about the place to write a subject line. Null when there is no location, the location
// is outside NWS coverage, or the NWS could not be reached - all of which WeatherService logs.
public interface IWeatherAlertSource
{
    Task<WeatherAlertFeed?> GetActiveAlertsAsync(bool fresh, CancellationToken cancellationToken);
}

public sealed record WeatherAlertFeed(IReadOnlyList<WeatherAlert> Alerts, string Place, string? TimeZone);

public enum WeatherAlertLevel
{
    Advisory = 0,
    Watch = 1,
    Warning = 2
}

// Pure: which alerts to send, and what to say. Everything that needs a clock or the network is in
// the worker, so all of this is testable with fixtures.
public static class WeatherAlertPolicy
{
    // Category keys, matching the checkbox fields in the WEATHER ALERTS settings section.
    public const string Tornado = "tornado";
    public const string SevereThunderstorm = "severeThunderstorm";
    public const string Flood = "flood";
    public const string Winter = "winter";
    public const string Wind = "wind";
    public const string Temperature = "temperature";
    public const string Tropical = "tropical";
    public const string Fire = "fire";
    public const string Other = "other";

    // The most sent in one check. Switching the feature on during a bad night in tornado alley,
    // or after a long outage, should not fire off twenty notifications in a row; the rest go out
    // on the next check, worst first.
    public const int MaximumPerCycle = 5;

    // How much of the NWS's description to include. Their text runs to pages for a winter storm;
    // the headline and instruction are the parts that matter on a phone.
    public const int MaximumDescriptionLength = 900;

    // "Tornado Warning" → Warning, "Flood Watch" → Watch, "Wind Advisory" / "Special Weather
    // Statement" / anything else → Advisory. The NWS is consistent about these suffixes.
    public static WeatherAlertLevel LevelOf(string eventName)
    {
        if (eventName.EndsWith("Warning", StringComparison.OrdinalIgnoreCase)) return WeatherAlertLevel.Warning;
        if (eventName.EndsWith("Watch", StringComparison.OrdinalIgnoreCase)) return WeatherAlertLevel.Watch;
        return WeatherAlertLevel.Advisory;
    }

    // Keyword order matters: "Wind Chill Warning" is cold, not wind; "Severe Thunderstorm" is
    // checked before the bare "storm" words; "Coastal Flood" is flood before it is tropical.
    public static string CategoryOf(string eventName)
    {
        var name = eventName.ToLowerInvariant();
        if (name.Contains("tornado")) return Tornado;
        if (name.Contains("severe thunderstorm")) return SevereThunderstorm;
        if (name.Contains("flood")) return Flood;
        if (name.Contains("hurricane") || name.Contains("tropical") || name.Contains("typhoon") || name.Contains("storm surge")) return Tropical;
        if (name.Contains("winter") || name.Contains("blizzard") || name.Contains("ice storm") || name.Contains("snow") ||
            name.Contains("freezing") || name.Contains("sleet") || name.Contains("lake effect")) return Winter;
        if (name.Contains("wind chill") || name.Contains("cold") || name.Contains("freeze") || name.Contains("frost") || name.Contains("heat")) return Temperature;
        if (name.Contains("wind") || name.Contains("gale")) return Wind;
        if (name.Contains("red flag") || name.Contains("fire")) return Fire;
        return Other;
    }

    public static WeatherAlertLevel MinimumLevel(WeatherAlertOptions options) => options.MinimumLevel switch
    {
        WeatherAlertOptions.LevelWarning => WeatherAlertLevel.Warning,
        WeatherAlertOptions.LevelAdvisory => WeatherAlertLevel.Advisory,
        _ => WeatherAlertLevel.Watch
    };

    public static bool CategoryEnabled(WeatherAlertOptions options, string category) => category switch
    {
        Tornado => options.Tornado,
        SevereThunderstorm => options.SevereThunderstorm,
        Flood => options.Flood,
        Winter => options.Winter,
        Wind => options.Wind,
        Temperature => options.Temperature,
        Tropical => options.Tropical,
        Fire => options.Fire,
        _ => options.Other
    };

    public static bool Wants(WeatherAlertOptions options, WeatherAlert alert) =>
        LevelOf(alert.Event) >= MinimumLevel(options) && CategoryEnabled(options, CategoryOf(alert.Event));

    // ToSend: new alerts the owner asked for, worst first, capped. SkippedFollowUps: wanted alerts
    // that only update something already sent - the worker records these as seen so the next
    // reissue (which will reference *this* id) is skipped too.
    public static WeatherAlertSelection Select(
        WeatherAlertOptions options, IReadOnlyList<WeatherAlert> alerts, Func<string, bool> alreadySeen)
    {
        var toSend = new List<WeatherAlert>();
        var followUps = new List<WeatherAlert>();

        foreach (var alert in alerts.OrderBy(a => WeatherService.SeverityRank(a.Severity)).ThenBy(a => a.Onset ?? DateTimeOffset.MaxValue))
        {
            if (!Wants(options, alert) || alreadySeen(alert.Id)) continue;

            var isFollowUp = alert.References.Any(alreadySeen);
            if (isFollowUp && !options.NotifyOnUpdates)
            {
                followUps.Add(alert);
                continue;
            }

            if (toSend.Count < MaximumPerCycle) toSend.Add(alert);
        }

        return new WeatherAlertSelection(toSend, followUps);
    }

    public static string Subject(WeatherAlert alert, string place) => $"{alert.Event} - {place}";

    // Headline, then what to do (the NWS's own instruction, in its own words), then the
    // description with the NWS's hard line-wraps undone, then when it ends and where it applies.
    public static string Body(WeatherAlert alert, string? timeZoneId)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(alert.Headline)) text.AppendLine(alert.Headline.Trim()).AppendLine();
        if (!string.IsNullOrWhiteSpace(alert.Instruction)) text.AppendLine(Flow(alert.Instruction)).AppendLine();
        if (!string.IsNullOrWhiteSpace(alert.Description)) text.AppendLine(Truncate(Flow(alert.Description), MaximumDescriptionLength)).AppendLine();

        var footer = new List<string>();
        if (alert.Ends is not null) footer.Add($"In effect until {LocalTime(alert.Ends.Value, timeZoneId)}.");
        if (!string.IsNullOrWhiteSpace(alert.AreaDescription)) footer.Add($"Areas: {alert.AreaDescription.Trim()}.");
        if (!string.IsNullOrWhiteSpace(alert.Sender)) footer.Add($"Issued by {alert.Sender.Trim()}.");
        text.Append(string.Join(" ", footer));

        return text.ToString().Trim();
    }

    // What the "send test alert" button sends: a made-up tornado warning shaped like a real one,
    // so the owner sees where a real one would land and what it looks like there.
    public static WeatherAlert Sample(DateTimeOffset now, string place) => new()
    {
        Id = "sentinelcore-test-" + now.ToUnixTimeSeconds(),
        Event = "Tornado Warning",
        Severity = "Extreme",
        Urgency = "Immediate",
        Certainty = "Observed",
        Headline = $"TEST - Tornado Warning for {place} until {now.AddMinutes(45):h:mm tt} - this is a test from Sentinel Core, there is no real warning",
        Description = "* WHAT...This is a test of Sentinel Core's weather alerts. A real tornado warning would describe what the National Weather Service has seen here.\n\n* WHERE...Wherever your location is set.\n\n* WHEN...Nothing is happening right now. This message only shows where a real alert would arrive.",
        Instruction = "No action needed. If this arrived where you wanted it, weather alerts are set up.",
        Onset = now,
        Ends = now.AddMinutes(45),
        Expires = now.AddMinutes(45),
        AreaDescription = place,
        Sender = "Sentinel Core (test)"
    };

    // NWS text arrives wrapped at ~69 columns with blank lines between paragraphs. Join the
    // wrapped lines back into paragraphs; keep the paragraph breaks.
    public static string Flow(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n\n", paragraphs
            .Select(p => string.Join(" ", p.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim())
            .Where(p => p.Length > 0));
    }

    private static string Truncate(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum].TrimEnd() + "…";

    private static string LocalTime(DateTimeOffset when, string? timeZoneId)
    {
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        var local = TimeZoneInfo.ConvertTime(when, zone);
        return local.ToString("ddd h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed record WeatherAlertSelection(IReadOnlyList<WeatherAlert> ToSend, IReadOnlyList<WeatherAlert> SkippedFollowUps);

// The alert ids already dealt with, each with when it stops mattering, kept in a small JSON file
// beside the other web-host data so a restart doesn't re-send the warning that is still up.
public sealed class WeatherAlertState(string path)
{
    // How long after an alert expires its id is still remembered. The NWS occasionally reissues
    // with an old id in the references a little after the original lapsed.
    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Dictionary<string, DateTimeOffset> _seen = new();
    private bool _loaded;

    public string Path { get; } = path;

    public bool Contains(string id)
    {
        EnsureLoaded();
        return _seen.ContainsKey(id);
    }

    public void MarkSeen(string id, DateTimeOffset until)
    {
        EnsureLoaded();
        _seen[id] = until;
    }

    public int Prune(DateTimeOffset now)
    {
        EnsureLoaded();
        var stale = _seen.Where(pair => pair.Value + Grace < now).Select(pair => pair.Key).ToList();
        foreach (var id in stale) _seen.Remove(id);
        return stale.Count;
    }

    public int Count
    {
        get { EnsureLoaded(); return _seen.Count; }
    }

    public void Save()
    {
        EnsureLoaded();
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = $"{Path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_seen, JsonOptions));
        File.Move(temporary, Path, overwrite: true);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(Path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(Path), JsonOptions);
            if (loaded is null) return;
            foreach (var pair in loaded) _seen[pair.Key] = pair.Value;
        }
        catch (JsonException)
        {
            // A corrupt file costs one repeat of whatever is currently in force, which is the
            // right side to fail on for a tornado warning.
        }
    }
}

public sealed record WeatherAlertRun(int Sent, int Failed, int SkippedFollowUps)
{
    public static readonly WeatherAlertRun Nothing = new(0, 0, 0);
}

public sealed class WeatherAlertWorker(
    IWeatherAlertSource source,
    IOwnerNotifier notifier,
    AppSettingsFile settings,
    WeatherAlertState state,
    ILogger<WeatherAlertWorker> logger,
    TimeProvider? clock = null) : BackgroundService
{
    // How often to look at the settings while the feature is off, so turning it on takes effect
    // within a minute rather than after a restart.
    public static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DisabledPollInterval;
            try
            {
                var options = await settings.GetSectionAsync<WeatherAlertOptions>(WeatherAlertOptions.SectionName, stoppingToken)
                    ?? new WeatherAlertOptions();
                if (options.Enabled)
                {
                    await RunOnceAsync(options, stoppingToken);
                    delay = TimeSpan.FromMinutes(Math.Clamp(options.CheckIntervalMinutes, 1, 60));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The weather alert check failed");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<WeatherAlertRun> RunOnceAsync(WeatherAlertOptions options, CancellationToken cancellationToken)
    {
        // Fresh, not the tab's five-minute cache: this is the one caller for whom "a warning
        // issued three minutes ago" is the whole point.
        var feed = await source.GetActiveAlertsAsync(fresh: true, cancellationToken);
        if (feed is null) return WeatherAlertRun.Nothing;

        var now = _clock.GetUtcNow();
        var selection = WeatherAlertPolicy.Select(options, feed.Alerts, state.Contains);

        // Anything already dealt with that the NWS is still listing stays remembered, however
        // stale its expiry looks - otherwise the prune below forgets it and the next check sends
        // it again.
        foreach (var alert in feed.Alerts)
            if (state.Contains(alert.Id)) state.MarkSeen(alert.Id, Until(alert, now));

        foreach (var followUp in selection.SkippedFollowUps)
            state.MarkSeen(followUp.Id, Until(followUp, now));

        var sent = 0;
        var failed = 0;
        foreach (var alert in selection.ToSend)
        {
            var delivered = await notifier.SendAsync(
                WeatherAlertPolicy.Subject(alert, feed.Place),
                WeatherAlertPolicy.Body(alert, feed.TimeZone),
                cancellationToken);

            if (delivered)
            {
                sent++;
                state.MarkSeen(alert.Id, Until(alert, now));
                logger.LogInformation("Sent weather alert: {Event} for {Place}", alert.Event, feed.Place);
            }
            else
            {
                // Not marked seen: with no channel accepting it, the next check tries again. A
                // tornado warning is the wrong thing to give up on after one failed SMTP call.
                failed++;
                logger.LogWarning("Could not deliver weather alert {Event} - no channel accepted it. Set up Email, Telegram or Push Notifications; it will be retried on the next check", alert.Event);
            }
        }

        state.Prune(now);
        try
        {
            state.Save();
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not save the weather alert state to {Path}", state.Path);
        }

        return new WeatherAlertRun(sent, failed, selection.SkippedFollowUps.Count);
    }

    // The later of the message expiry and the hazard end (a long winter warning's message can
    // expire well before the storm does), never earlier than now: the alert is in the feed, so
    // it matters at least until this check.
    private static DateTimeOffset Until(WeatherAlert alert, DateTimeOffset now)
    {
        if (alert.Expires is null && alert.Ends is null) return now.AddDays(1);
        var until = now;
        if (alert.Expires > until) until = alert.Expires.Value;
        if (alert.Ends > until) until = alert.Ends.Value;
        return until;
    }
}
