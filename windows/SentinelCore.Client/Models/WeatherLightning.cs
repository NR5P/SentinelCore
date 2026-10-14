namespace SentinelCore.Client.Models;

// Lightning on the radar map: GET /api/weather/lightning (core/SentinelCore.Web/LightningService.cs),
// the flashes NOAA's GOES-19 Lightning Mapper saw inside a box over the radar loop's window.
// The map page (Live/radar.html) asks for these itself, the way the dashboard's weather.js does;
// this is the same answer typed for anything on the .NET side, plus the merge rules both follow.

/// <summary>One answer from /api/weather/lightning.</summary>
public sealed class WeatherLightning
{
    public const string Loading = "loading";
    public const string Ready = "ready";

    /// <summary>"loading" while the server is still reading the first files in, then "ready".</summary>
    public string State { get; init; } = Loading;

    /// <summary>Which run of the server's feed this came from. Send it back with <see cref="Sequence"/> for only-new flashes.</summary>
    public string Epoch { get; init; } = "";
    public long Sequence { get; init; }

    /// <summary>True when <see cref="Flashes"/> is only what's new since the epoch/after asked about - append it; false, replace.</summary>
    public bool Incremental { get; init; }
    public DateTimeOffset? CoverageStart { get; init; }
    public DateTimeOffset? CoverageEnd { get; init; }

    /// <summary>Unix milliseconds every flash's seconds count from.</summary>
    public long Origin { get; init; }

    /// <summary>True when the server capped how many flashes it sent.</summary>
    public bool Truncated { get; init; }

    /// <summary>[latitude, longitude, seconds after <see cref="Origin"/>, energy in femtojoules], oldest first.</summary>
    public List<double[]> Flashes { get; init; } = new();

    public bool IsReady => string.Equals(State, Ready, StringComparison.OrdinalIgnoreCase);

    /// <summary>The flashes as positions and times.</summary>
    public IEnumerable<LightningFlash> Decode()
    {
        foreach (var row in Flashes)
        {
            if (row is null || row.Length < 3) continue;
            var energy = row.Length > 3 ? row[3] : 0;
            yield return new LightningFlash(
                row[0],
                row[1],
                DateTimeOffset.FromUnixTimeMilliseconds(Origin + (long)Math.Round(row[2] * 1000)),
                energy);
        }
    }
}

public readonly record struct LightningFlash(double Latitude, double Longitude, DateTimeOffset Time, double EnergyFemtojoules);

/// <summary>
/// The flashes the map keeps between polls - the rules of weather.js's loadLightning: an
/// incremental answer is appended, any other replaces, and nothing older than 95 minutes is kept.
/// </summary>
public sealed class LightningHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(95);
    public static readonly TimeSpan ReadyRefresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LoadingRefresh = TimeSpan.FromSeconds(4);

    private List<LightningFlash> _flashes = new();

    public string? Epoch { get; private set; }
    public long? Sequence { get; private set; }
    public string? State { get; private set; }
    public IReadOnlyList<LightningFlash> Flashes => _flashes;

    /// <summary>How long to wait before asking again: 30 s once ready, 4 s while still loading.</summary>
    public TimeSpan NextPoll => string.Equals(State, WeatherLightning.Ready, StringComparison.OrdinalIgnoreCase)
        ? ReadyRefresh
        : LoadingRefresh;

    /// <summary>Takes an answer; <paramref name="now"/> sets the 95-minute cutoff.</summary>
    public void Apply(WeatherLightning answer, DateTimeOffset now)
    {
        var cutoff = now - Keep;
        var fresh = answer.Decode();
        var merged = answer.Incremental ? _flashes.Concat(fresh) : fresh;
        _flashes = merged.Where(flash => flash.Time >= cutoff).OrderBy(flash => flash.Time).ToList();
        Epoch = answer.Epoch;
        Sequence = answer.Sequence;
        State = answer.State;
    }

    public void Clear()
    {
        _flashes = new();
        Epoch = null;
        Sequence = null;
        State = null;
    }
}
