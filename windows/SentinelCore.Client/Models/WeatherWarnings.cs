using System.Globalization;
using System.Text.Json;

namespace SentinelCore.Client.Models;

// The app-wide weather warning banner: GET /api/weather/warnings (core/SentinelCore.Web), the
// same alerts the wall kiosk flashes (wwwroot/kiosk-weather-alert.js), already filtered by the
// owner's settings and sorted worst first. WakeMinutes and OpenRadar are for the wall tablet only.

public sealed class WeatherWarnings
{
    public bool Enabled { get; init; }
    public int WakeMinutes { get; init; }
    public bool OpenRadar { get; init; }
    public List<WeatherWarning> Alerts { get; init; } = new();
}

public static class WeatherWarningLevels
{
    public const string Warning = "warning";
    public const string Watch = "watch";
    public const string Advisory = "advisory";
}

public sealed class WeatherWarning
{
    public string Id { get; init; } = "";
    public string Event { get; init; } = "";
    /// <summary>"warning", "watch" or "advisory" - which colour the banner flashes.</summary>
    public string Level { get; init; } = WeatherWarningLevels.Advisory;
    public string Category { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Instruction { get; init; } = "";
    public DateTimeOffset? Ends { get; init; }
    /// <summary>The earlier alerts this one reissues (the NWS re-sends a warning as it tracks the storm).</summary>
    public List<string> References { get; init; } = new();
}

/// <summary>
/// Which warnings are in force and which of those this PC hasn't acknowledged yet - the logic of
/// the kiosk's kiosk-weather-alert.js, platform-neutral so it can be tested here.
/// </summary>
/// <remarks>
/// "Seen" is remembered per machine in the preference file as id → when it can be forgotten (the
/// alert's end, or a day from now when it has none). A reissue that references something already
/// seen counts as seen, and is remembered in its own right so the next reissue (which references
/// it rather than the original) is seen too after the original is forgotten. An upgrade (watch to
/// warning) is a new alert, so it flashes again.
/// </remarks>
public sealed class WeatherWarningTracker
{
    public const string SeenKey = "weatherWarningsSeen";
    private static readonly TimeSpan SeenFallback = TimeSpan.FromHours(24);

    private readonly PreferenceStore? _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _seen;
    private List<WeatherWarning> _alerts = new();

    public WeatherWarningTracker(PreferenceStore? store, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.Now);
        _seen = ReadSeen();
        Prune();
    }

    /// <summary>Every alert still in force, seen or not, worst first. Drives the Weather section's dot.</summary>
    public IReadOnlyList<WeatherWarning> Active { get; private set; } = Array.Empty<WeatherWarning>();

    /// <summary>The alerts the banner shows: in force and not yet acknowledged here.</summary>
    public IReadOnlyList<WeatherWarning> Unseen { get; private set; } = Array.Empty<WeatherWarning>();

    /// <summary>The level of the worst alert in force, or null when there is none.</summary>
    public string? ActiveLevel => Active.Count > 0 ? Active[0].Level : null;

    /// <summary>Takes the server's latest list (null: nothing, e.g. a server too old to have the route).</summary>
    public void Update(IEnumerable<WeatherWarning>? alerts)
    {
        _alerts = alerts?.ToList() ?? new List<WeatherWarning>();
        Recompute();
    }

    /// <summary>Recomputes against the clock without asking the server: alerts end between polls.</summary>
    public void Recompute()
    {
        Prune();
        var now = _now();
        Active = _alerts.Where(alert => EndsAt(alert) > now).ToList();
        Unseen = Active.Where(alert => !IsSeen(alert)).ToList();
    }

    /// <summary>The banner was clicked or dismissed: these count as seen from now on.</summary>
    public void MarkSeen(IEnumerable<WeatherWarning> alerts)
    {
        foreach (var alert in alerts)
        {
            var until = EndsAt(alert);
            _seen[alert.Id] = until;
            foreach (var reference in alert.References) _seen[reference] = until;
        }
        WriteSeen();
        Recompute();
    }

    public bool IsSeen(WeatherWarning alert)
    {
        if (_seen.ContainsKey(alert.Id)) return true;
        if (alert.References.Any(_seen.ContainsKey))
        {
            _seen[alert.Id] = EndsAt(alert);
            WriteSeen();
            return true;
        }
        return false;
    }

    private DateTimeOffset EndsAt(WeatherWarning alert) => alert.Ends ?? _now() + SeenFallback;

    private void Prune()
    {
        var now = _now();
        var expired = _seen.Where(pair => pair.Value < now).Select(pair => pair.Key).ToList();
        if (expired.Count == 0) return;
        foreach (var id in expired) _seen.Remove(id);
        WriteSeen();
    }

    private Dictionary<string, DateTimeOffset> ReadSeen()
    {
        var result = new Dictionary<string, DateTimeOffset>();
        var json = _store?.GetString(SeenKey) ?? "";
        if (json.Length == 0) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            if (raw is null) return result;
            foreach (var (id, ms) in raw) result[id] = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (Exception error) when (error is JsonException or ArgumentOutOfRangeException)
        {
            // A damaged entry forgets what was seen: the worst case is one banner shown again.
        }
        return result;
    }

    private void WriteSeen()
    {
        if (_store is null) return;
        var raw = _seen.ToDictionary(pair => pair.Key, pair => pair.Value.ToUnixTimeMilliseconds());
        _store.Set(SeenKey, JsonSerializer.Serialize(raw));
    }

    // ---------------------------------------------------------------- Banner text

    /// <summary>"until 3:45 PM" when it ends today, "until Tue 3:45 PM" otherwise, "" with no end.</summary>
    public static string UntilText(WeatherWarning alert, WeatherClock clock, DateTimeOffset now)
    {
        if (alert.Ends is not { } ends) return "";
        var time = clock.Clock(ends);
        var sameDay = clock.Local(ends).Date == clock.Local(now).Date;
        return sameDay ? $"until {time}" : $"until {clock.Weekday(ends)} {time}";
    }

    /// <summary>"+2 more" when the banner stands for more than the one it names.</summary>
    public static string MoreText(int unseenCount) => unseenCount > 1 ? $"+{unseenCount - 1} more" : "";

    /// <summary>The event name as the banner shouts it.</summary>
    public static string EventText(WeatherWarning alert) => alert.Event.ToUpper(CultureInfo.CurrentCulture);
}
