using System.Globalization;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// The Weather section: core/SentinelCore.Web/WeatherEndpoints.cs. Every route needs a login and
/// none needs an admin. The server talks to the National Weather Service (and the Storm Prediction
/// Center and the Iowa Environmental Mesonet) and caches the answers, so asking often is cheap.
/// </summary>
public sealed partial class SentinelClient
{
    private const string WeatherBase = "/api/weather";

    /// <summary>
    /// Always 200 with a report. Check <see cref="WeatherReport.State"/>: anything but "ok" carries
    /// a <see cref="WeatherReport.Message"/> explaining itself.
    /// </summary>
    public Task<WeatherReport> WeatherAsync(CancellationToken ct = default) =>
        GetAsync<WeatherReport>(WeatherBase, ct: ct);

    /// <summary>
    /// The NWS warnings the app-wide banner flashes, filtered by the owner's settings and sorted
    /// worst first. Null from a server too old to have the route (404) - nothing to show, not an error.
    /// </summary>
    public async Task<WeatherWarnings?> WeatherWarningsAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<WeatherWarnings>($"{WeatherBase}/warnings", ct: ct).ConfigureAwait(false);
        }
        catch (SentinelServerException error) when (error.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Precipitation totals, hourly feels-like and gusts, the pressure trend, the forecast discussion and the SPC outlook.</summary>
    public Task<WeatherDetails> WeatherDetailsAsync(CancellationToken ct = default) =>
        GetAsync<WeatherDetails>($"{WeatherBase}/details", ct: ct);

    /// <summary>
    /// The NWS radar picture as an animated GIF - the fallback when the interactive map can't run.
    /// </summary>
    /// <param name="scope">"local" (the radar nearest home) or "national".</param>
    /// <param name="kind">"loop" or "still".</param>
    public Task<byte[]> WeatherRadarImageAsync(string scope = "local", string kind = "loop", CancellationToken ct = default) =>
        GetBytesAsync($"{WeatherBase}/radar?scope={Uri.EscapeDataString(scope)}&kind={Uri.EscapeDataString(kind)}", ct);

    /// <summary>The radar map's home, radar sites, layers and frame times. <paramref name="station"/> picks whose per-radar layers to list.</summary>
    public Task<WeatherMapInfo> WeatherMapAsync(string? station = null, CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(station)
            ? null
            : new[] { new KeyValuePair<string, string>("station", station.Trim()) };
        return GetAsync<WeatherMapInfo>($"{WeatherBase}/map", query, ct);
    }

    /// <summary>Storm cells, warning outlines and today's outlook inside a box of degrees.</summary>
    public Task<WeatherStorms> WeatherStormsAsync(
        double west, double south, double east, double north, CancellationToken ct = default)
    {
        static string Degrees(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var query = new[]
        {
            new KeyValuePair<string, string>("west", Degrees(west)),
            new KeyValuePair<string, string>("south", Degrees(south)),
            new KeyValuePair<string, string>("east", Degrees(east)),
            new KeyValuePair<string, string>("north", Degrees(north)),
        };
        return GetAsync<WeatherStorms>($"{WeatherBase}/storms", query, ct);
    }

    /// <summary>
    /// Lightning flashes inside a box of degrees over the radar loop's window. Pass the
    /// <paramref name="epoch"/> and <paramref name="after"/> (its sequence) of the last answer for only
    /// what's new (<see cref="WeatherLightning.Incremental"/> then says to append). Null from a server
    /// too old to have the route (404).
    /// </summary>
    public async Task<WeatherLightning?> WeatherLightningAsync(
        double west, double south, double east, double north,
        string? epoch = null, long? after = null, CancellationToken ct = default)
    {
        static string Degrees(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var query = new List<KeyValuePair<string, string>>
        {
            new("west", Degrees(west)),
            new("south", Degrees(south)),
            new("east", Degrees(east)),
            new("north", Degrees(north)),
        };
        if (!string.IsNullOrEmpty(epoch) && after is { } sequence)
        {
            query.Add(new("epoch", epoch));
            query.Add(new("after", sequence.ToString(CultureInfo.InvariantCulture)));
        }
        try
        {
            return await GetAsync<WeatherLightning>($"{WeatherBase}/lightning", query, ct).ConfigureAwait(false);
        }
        catch (SentinelServerException error) when (error.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// The storms around home - the same box the dashboard asks for (2.5 degrees of longitude and
    /// 2 of latitude either side), which is what the "storms near home" list and banner come from.
    /// </summary>
    public Task<WeatherStorms> WeatherStormsNearAsync(double latitude, double longitude, CancellationToken ct = default) =>
        WeatherStormsAsync(longitude - 2.5, latitude - 2, longitude + 2.5, latitude + 2, ct);
}
