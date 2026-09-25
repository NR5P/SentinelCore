using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SentinelCore.Web;

// The Weather tab's data: current conditions, an hourly and a seven-day forecast, active watches
// and warnings, and the radar picture, all from the US National Weather Service's free public API
// (api.weather.gov) for the latitude and longitude in Settings → LOCATION. No key, no account.
//
// This is the second thing the location is used for, and the second place it leaves the
// building (the first is pressing Find in Settings): the two coordinates go to the NWS to ask
// what the weather is there. That happens only while somebody has the Weather tab open - nothing
// polls in the background - and every answer is cached here, so a house full of open dashboards
// asks the NWS once rather than once per browser, and a forecast that was fetched a minute ago is
// served from memory. Outside the United States the NWS has no data, and the tab says so plainly
// rather than showing an empty page.
//
// Each part of the report is fetched and cached on its own. Observations failing (a station down
// for maintenance is normal) leaves the forecast on screen with a note, rather than taking the
// whole tab down with it; and when the NWS can't be reached at all, the last answer is shown
// marked as such rather than nothing.
public sealed class WeatherService(
    IHttpClientFactory httpClientFactory,
    AppSettingsFile settings,
    ILogger<WeatherService> logger,
    TimeProvider? clock = null) : IWeatherAlertSource
{
    public const string ApiBaseUrl = "https://api.weather.gov";
    public const string RadarBaseUrl = "https://radar.weather.gov/ridge/standard";

    // The NWS API refuses requests without an identifying User-Agent. The product name says who
    // is asking without saying anything about the install.
    public const string UserAgent = "SentinelCore/1.0 (self-hosted camera alert system; weather tab)";

    // The national radar composite, shown when "national" is picked instead of the local station.
    public const string NationalRadarStation = "CONUS";

    // How long each answer is trusted before the NWS is asked again, matched to how often the
    // underlying product actually changes: a point's forecast grid never moves, forecasts are
    // reissued a few times a day, observations land roughly hourly, alerts can appear any minute,
    // and radar frames arrive every few minutes (the image itself is served with max-age=120).
    public static readonly TimeSpan PointTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan ForecastTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StationsTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan ObservationTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AlertsTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RadarTtl = TimeSpan.FromMinutes(2);

    // After a fetch fails with nothing cached to fall back on, don't ask again for this long.
    // Two browsers refreshing a dashboard whose server has lost its internet connection would
    // otherwise each fire a fresh round of doomed requests every time they reloaded.
    public static readonly TimeSpan RetryHold = TimeSpan.FromSeconds(60);

    // Two days of hours is what the strip on the tab can usefully show; the NWS sends 156.
    public const int MaximumHourlyPeriods = 48;

    // How many nearby observation stations to try before giving up on current conditions. The
    // nearest one being silent (down for maintenance, or a sensor failing quality control) is
    // routine, and the next one over is usually a few miles away.
    private const int MaximumStationsToTry = 3;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // One gate across the whole cache: every call is a handful of short requests to one service,
    // and serialising them is what stops two browsers opening the tab at once from each asking the
    // NWS for the same forecast.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new();
    private string? _cachedFor;

    public async Task<WeatherReport> GetReportAsync(CancellationToken ct)
    {
        var (options, coordinate) = await ReadLocationAsync(ct);
        var now = _clock.GetUtcNow();

        if (coordinate is null)
        {
            return new WeatherReport
            {
                State = WeatherReportState.Unconfigured,
                Message = "No location is set. Set one under Settings → LOCATION to see the weather here.",
                FetchedAt = now
            };
        }

        await _gate.WaitAsync(ct);
        try
        {
            ForgetIfLocationChanged(coordinate);

            var point = await CachedAsync("point", PointTtl, c => FetchPointAsync(coordinate, c), ct);
            if (point.Value is null)
            {
                return new WeatherReport
                {
                    State = WeatherReportState.Unreachable,
                    Message = "Couldn't reach the National Weather Service - this server may have no internet access, or weather.gov may be down. " + point.Problem,
                    FetchedAt = now
                };
            }

            if (!point.Value.Supported)
            {
                return new WeatherReport
                {
                    State = WeatherReportState.Unsupported,
                    Message = "The National Weather Service has no forecast for this location. It covers the United States and its territories, so the Weather tab can't show anything for the coordinates in Settings → LOCATION.",
                    Location = LocationFor(options, coordinate, point.Value),
                    FetchedAt = now
                };
            }

            var info = point.Value;
            var forecast = await CachedAsync("forecast", ForecastTtl, c => FetchForecastAsync(info.ForecastUrl, c), ct);
            var hourly = await CachedAsync("hourly", ForecastTtl, c => FetchHourlyAsync(info.HourlyUrl, c), ct);
            var current = await CachedAsync("current", ObservationTtl, c => FetchCurrentAsync(info.StationsUrl, c), ct);
            var alerts = await CachedAsync("alerts", AlertsTtl, c => FetchAlertsAsync(coordinate, c), ct);

            var problems = new List<string>();
            var stale = point.Stale;
            void Note<T>(Fetched<T> fetched, string what) where T : class
            {
                stale |= fetched.Stale;
                if (fetched.Problem is null) return;
                problems.Add(fetched.Value is null
                    ? $"{what} unavailable: {fetched.Problem}."
                    : $"{what} may be out of date: {fetched.Problem}.");
            }
            Note(forecast, "The forecast");
            Note(hourly, "The hourly forecast");
            Note(current, "Current conditions");
            Note(alerts, "Watches and warnings");

            return new WeatherReport
            {
                State = WeatherReportState.Ok,
                Location = LocationFor(options, coordinate, info),
                Current = current.Value,
                Daily = forecast.Value?.Periods ?? [],
                Hourly = TrimHourly(hourly.Value?.Periods ?? [], now),
                Alerts = alerts.Value ?? [],
                Sun = SunFor(coordinate, info.TimeZone, now),
                Radar = info.RadarStation is null ? null : new WeatherRadar { Station = info.RadarStation },
                ForecastUpdatedAt = forecast.Value?.UpdatedAt,
                FetchedAt = now,
                Stale = stale,
                Problems = problems
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    // The radar picture, proxied so the browser talks to this server only and so several open
    // dashboards share one download. `scope` is "local" (the station nearest the location) or
    // "national" (the whole-country composite); `kind` is "loop" (the animated last hour) or
    // "still" (the latest frame alone).
    public async Task<RadarImage> GetRadarAsync(string scope, string kind, CancellationToken ct)
    {
        var suffix = kind switch { "loop" => "loop", "still" => "0", _ => null };
        if (suffix is null || scope is not ("local" or "national"))
            return RadarImage.Failed(400, "Unknown radar image. scope is local or national; kind is loop or still.");

        await _gate.WaitAsync(ct);
        try
        {
            string station;
            if (scope == "national")
            {
                station = NationalRadarStation;
            }
            else
            {
                var (_, coordinate) = await ReadLocationAsync(ct);
                if (coordinate is null) return RadarImage.Failed(404, "No location is set.");
                ForgetIfLocationChanged(coordinate);

                var point = await CachedAsync("point", PointTtl, c => FetchPointAsync(coordinate, c), ct);
                if (point.Value is null) return RadarImage.Failed(502, "Couldn't reach the National Weather Service. " + point.Problem);
                if (!point.Value.Supported || point.Value.RadarStation is null)
                    return RadarImage.Failed(404, "There is no National Weather Service radar station for this location.");
                station = point.Value.RadarStation;
            }

            var url = $"{RadarBaseUrl}/{station}_{suffix}.gif";
            var image = await CachedAsync($"radar:{station}:{suffix}", RadarTtl, c => FetchBytesAsync(url, c), ct);
            return image.Value is null
                ? RadarImage.Failed(502, "Couldn't fetch the radar picture. " + image.Problem)
                : new RadarImage(image.Value, 200, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The alerts alone, for WeatherAlertWorker. `fresh` skips the five-minute cache (the answer
    // still lands in it, so the tab benefits). Null for every "nothing to check" case: no
    // location, outside NWS coverage, or the NWS unreachable with nothing cached.
    public async Task<WeatherAlertFeed?> GetActiveAlertsAsync(bool fresh, CancellationToken ct)
    {
        var (options, coordinate) = await ReadLocationAsync(ct);
        if (coordinate is null) return null;

        await _gate.WaitAsync(ct);
        try
        {
            ForgetIfLocationChanged(coordinate);

            var point = await CachedAsync("point", PointTtl, c => FetchPointAsync(coordinate, c), ct);
            if (point.Value is null || !point.Value.Supported) return null;

            var alerts = await CachedAsync("alerts", fresh ? TimeSpan.Zero : AlertsTtl, c => FetchAlertsAsync(coordinate, c), ct);
            if (alerts.Value is null) return null;

            var nearest = string.Join(", ", new[] { point.Value.City, point.Value.State }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var place = !string.IsNullOrWhiteSpace(options.Label) ? options.Label
                : nearest.Length > 0 ? nearest
                : PointPath(coordinate);
            return new WeatherAlertFeed(alerts.Value, place, point.Value.TimeZone);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(LocationOptions Options, GeoCoordinate? Coordinate)> ReadLocationAsync(CancellationToken ct)
    {
        var options = await settings.GetSectionAsync<LocationOptions>(LocationOptions.SectionName, ct) ?? new LocationOptions();
        var coordinate = new GeoCoordinate(options.Latitude, options.Longitude);
        return (options, coordinate.IsValid ? coordinate : null);
    }

    // Everything cached belongs to one location. Somebody moving the pin in Settings must not be
    // shown the old town's forecast until the point cache times out a day later.
    private void ForgetIfLocationChanged(GeoCoordinate coordinate)
    {
        var key = PointPath(coordinate);
        if (_cachedFor == key) return;
        _cache.Clear();
        _failedAt.Clear();
        _cachedFor = key;
    }

    // Serves the cached value while it is fresh, fetches when it isn't, and on a failed fetch
    // hands back the stale value (flagged) if there is one - a forecast from forty minutes ago is
    // far more useful than an error while weather.gov has a bad minute.
    private async Task<Fetched<T>> CachedAsync<T>(
        string key, TimeSpan ttl, Func<CancellationToken, Task<T>> fetch, CancellationToken ct) where T : class
    {
        var now = _clock.GetUtcNow();
        _cache.TryGetValue(key, out var entry);
        if (entry is not null && now - entry.FetchedAt < ttl)
            return new Fetched<T>((T)entry.Value, false, null);

        if (entry is null && _failedAt.TryGetValue(key, out var failedAt) && now - failedAt < RetryHold)
            return new Fetched<T>(null, false, "not retried yet after a recent failure");

        try
        {
            var value = await fetch(ct);
            _cache[key] = new CacheEntry(value, now);
            _failedAt.Remove(key);
            return new Fetched<T>(value, false, null);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Weather fetch failed for {Key}", key);
            _failedAt[key] = now;
            var problem = Describe(ex);
            return entry is null
                ? new Fetched<T>(null, false, problem)
                : new Fetched<T>((T)entry.Value, true, problem);
        }
    }

    private static bool IsUpstreamFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or WeatherUpstreamException or InvalidOperationException or KeyNotFoundException;

    private static string Describe(Exception ex) => ex switch
    {
        WeatherUpstreamException upstream => upstream.Message,
        TaskCanceledException => "the National Weather Service took too long to answer",
        HttpRequestException => "the National Weather Service could not be reached",
        _ => "the National Weather Service sent an answer this server could not read"
    };

    // ---- Fetching and parsing each NWS product ----

    private async Task<PointInfo> FetchPointAsync(GeoCoordinate at, CancellationToken ct)
    {
        using var response = await SendAsync($"{ApiBaseUrl}/points/{PointPath(at)}", ct);

        // The one 404 that means something: the NWS answers it, with a problem type of
        // InvalidPoint, for anywhere it has no forecast grid - the rest of the world. Cached like
        // any other point answer, so a dashboard in Europe doesn't ask again every ten minutes.
        if (response.StatusCode == HttpStatusCode.NotFound) return PointInfo.Unsupported;
        EnsureSuccess(response);

        using var document = await ReadJsonAsync(response, ct);
        var properties = document.RootElement.GetProperty("properties");
        var relative = properties.TryGetProperty("relativeLocation", out var rel) && rel.TryGetProperty("properties", out var relProps)
            ? relProps
            : default;

        return new PointInfo
        {
            Supported = true,
            ForecastUrl = Str(properties, "forecast") ?? throw new WeatherUpstreamException("the National Weather Service point answer had no forecast link"),
            HourlyUrl = Str(properties, "forecastHourly") ?? throw new WeatherUpstreamException("the National Weather Service point answer had no hourly forecast link"),
            StationsUrl = Str(properties, "observationStations") ?? throw new WeatherUpstreamException("the National Weather Service point answer had no stations link"),
            City = Str(relative, "city"),
            State = Str(relative, "state"),
            TimeZone = Str(properties, "timeZone"),
            RadarStation = Str(properties, "radarStation"),
            ForecastOffice = Str(properties, "cwa")
        };
    }

    private async Task<ForecastSet> FetchForecastAsync(string url, CancellationToken ct)
    {
        using var document = await GetJsonAsync(url, ct);
        var properties = document.RootElement.GetProperty("properties");
        var periods = new List<WeatherPeriod>();
        foreach (var period in Periods(properties))
        {
            periods.Add(new WeatherPeriod
            {
                Name = Str(period, "name") ?? "",
                StartTime = Time(period, "startTime") ?? default,
                EndTime = Time(period, "endTime") ?? default,
                IsDaytime = period.TryGetProperty("isDaytime", out var day) && day.ValueKind == JsonValueKind.True,
                TemperatureF = RoundInt(ToFahrenheit(Num(period, "temperature"), Str(period, "temperatureUnit"))),
                PrecipitationChance = RoundInt(Quantity(period, "probabilityOfPrecipitation").Value),
                WindSpeed = Str(period, "windSpeed"),
                WindDirection = Str(period, "windDirection"),
                Icon = IconCode(Str(period, "icon")),
                ShortForecast = Str(period, "shortForecast") ?? "",
                DetailedForecast = Str(period, "detailedForecast") ?? ""
            });
        }
        return new ForecastSet(periods, Time(properties, "updateTime") ?? Time(properties, "generatedAt"));
    }

    private async Task<HourlySet> FetchHourlyAsync(string url, CancellationToken ct)
    {
        using var document = await GetJsonAsync(url, ct);
        var properties = document.RootElement.GetProperty("properties");
        var hours = new List<WeatherHour>();
        foreach (var period in Periods(properties))
        {
            hours.Add(new WeatherHour
            {
                StartTime = Time(period, "startTime") ?? default,
                EndTime = Time(period, "endTime") ?? default,
                IsDaytime = period.TryGetProperty("isDaytime", out var day) && day.ValueKind == JsonValueKind.True,
                TemperatureF = RoundInt(ToFahrenheit(Num(period, "temperature"), Str(period, "temperatureUnit"))),
                PrecipitationChance = RoundInt(Quantity(period, "probabilityOfPrecipitation").Value),
                HumidityPercent = RoundInt(Quantity(period, "relativeHumidity").Value),
                WindSpeed = Str(period, "windSpeed"),
                WindDirection = Str(period, "windDirection"),
                Icon = IconCode(Str(period, "icon")),
                ShortForecast = Str(period, "shortForecast") ?? ""
            });
        }
        return new HourlySet(hours);
    }

    private async Task<WeatherCurrent> FetchCurrentAsync(string stationsUrl, CancellationToken ct)
    {
        var stations = await CachedAsync("stations", StationsTtl, c => FetchStationsAsync(stationsUrl, c), ct);
        if (stations.Value is null) throw new WeatherUpstreamException(stations.Problem ?? "no observation stations were listed");
        if (stations.Value.Count == 0) throw new WeatherUpstreamException("the National Weather Service lists no observation station near this location");

        string? lastProblem = null;
        foreach (var station in stations.Value.Take(MaximumStationsToTry))
        {
            try
            {
                using var document = await GetJsonAsync($"{ApiBaseUrl}/stations/{Uri.EscapeDataString(station.Id)}/observations/latest", ct);
                var properties = document.RootElement.GetProperty("properties");
                var temperature = Quantity(properties, "temperature");
                // A station whose thermometer has nothing to say has nothing to say - the rest of
                // its fields are usually null too. Try the next one over.
                if (temperature.Value is null) { lastProblem = $"station {station.Id} has not reported recently"; continue; }

                var windChill = Quantity(properties, "windChill");
                var heatIndex = Quantity(properties, "heatIndex");
                var dewpoint = Quantity(properties, "dewpoint");
                var wind = Quantity(properties, "windSpeed");
                var gust = Quantity(properties, "windGust");
                var icon = Str(properties, "icon");

                return new WeatherCurrent
                {
                    ObservedAt = Time(properties, "timestamp"),
                    Station = station.Id,
                    StationName = station.Name,
                    Description = Str(properties, "textDescription") ?? "",
                    Icon = IconCode(icon),
                    IsDaytime = icon is null ? null : !icon.Contains("/night/", StringComparison.Ordinal),
                    TemperatureF = RoundInt(ToFahrenheit(temperature.Value, temperature.Unit)) ?? 0,
                    FeelsLikeF = RoundInt(ToFahrenheit(windChill.Value, windChill.Unit) ?? ToFahrenheit(heatIndex.Value, heatIndex.Unit)),
                    DewpointF = RoundInt(ToFahrenheit(dewpoint.Value, dewpoint.Unit)),
                    HumidityPercent = RoundInt(Quantity(properties, "relativeHumidity").Value),
                    WindMph = RoundInt(ToMph(wind.Value, wind.Unit)),
                    WindGustMph = RoundInt(ToMph(gust.Value, gust.Unit)),
                    WindDirection = Compass(Quantity(properties, "windDirection").Value),
                    PressureInHg = Round(ToInchesOfMercury(Quantity(properties, "barometricPressure").Value), 2),
                    VisibilityMiles = Round(ToMiles(Quantity(properties, "visibility").Value), 1)
                };
            }
            catch (WeatherUpstreamException ex)
            {
                lastProblem = ex.Message;
            }
        }

        throw new WeatherUpstreamException(lastProblem ?? "no nearby station has reported recently");
    }

    private async Task<List<StationInfo>> FetchStationsAsync(string stationsUrl, CancellationToken ct)
    {
        var separator = stationsUrl.Contains('?') ? '&' : '?';
        using var document = await GetJsonAsync($"{stationsUrl}{separator}limit={MaximumStationsToTry}", ct);
        var stations = new List<StationInfo>();
        if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return stations;

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var properties)) continue;
            var id = Str(properties, "stationIdentifier");
            if (string.IsNullOrWhiteSpace(id)) continue;
            stations.Add(new StationInfo(id, Str(properties, "name") ?? id));
        }
        return stations;
    }

    private async Task<List<WeatherAlert>> FetchAlertsAsync(GeoCoordinate at, CancellationToken ct)
    {
        using var document = await GetJsonAsync($"{ApiBaseUrl}/alerts/active?status=actual&point={PointPath(at)}", ct);
        var alerts = new List<WeatherAlert>();
        if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return alerts;

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var properties)) continue;
            alerts.Add(new WeatherAlert
            {
                Id = Str(properties, "id") ?? Str(feature, "id") ?? Guid.NewGuid().ToString("N"),
                Event = Str(properties, "event") ?? "Alert",
                Severity = Str(properties, "severity") ?? "Unknown",
                Urgency = Str(properties, "urgency") ?? "Unknown",
                Certainty = Str(properties, "certainty") ?? "Unknown",
                Headline = Str(properties, "headline") ?? "",
                Description = Str(properties, "description") ?? "",
                Instruction = Str(properties, "instruction") ?? "",
                Onset = Time(properties, "onset") ?? Time(properties, "effective"),
                Ends = Time(properties, "ends") ?? Time(properties, "expires"),
                Expires = Time(properties, "expires"),
                AreaDescription = Str(properties, "areaDesc") ?? "",
                Sender = Str(properties, "senderName") ?? "",
                MessageType = Str(properties, "messageType") ?? "Alert",
                References = ReferenceIds(properties)
            });
        }

        // Worst first. The NWS's own order is by issue time, which puts a fog advisory above a
        // tornado warning if the fog came in later.
        return alerts.OrderBy(a => SeverityRank(a.Severity)).ThenBy(a => a.Ends ?? DateTimeOffset.MaxValue).ToList();
    }

    // An update or cancellation carries the ids of the alerts it replaces. What WeatherAlertWorker
    // uses to tell "a new warning" from "the same warning, reissued".
    private static List<string> ReferenceIds(JsonElement properties)
    {
        var ids = new List<string>();
        if (!properties.TryGetProperty("references", out var references) || references.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var reference in references.EnumerateArray())
        {
            var id = Str(reference, "identifier") ?? Str(reference, "@id");
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        return ids;
    }

    private async Task<byte[]> FetchBytesAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        EnsureSuccess(response);
        return await ReadJsonAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(nameof(WeatherService));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/geo+json, application/json, image/gif");
        return await client.SendAsync(request, ct);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new WeatherUpstreamException($"the National Weather Service answered HTTP {(int)response.StatusCode}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static IEnumerable<JsonElement> Periods(JsonElement properties) =>
        properties.TryGetProperty("periods", out var periods) && periods.ValueKind == JsonValueKind.Array
            ? periods.EnumerateArray()
            : [];

    // ---- Shaping ----

    private static WeatherLocation LocationFor(LocationOptions options, GeoCoordinate coordinate, PointInfo point) => new()
    {
        Latitude = coordinate.Latitude,
        Longitude = coordinate.Longitude,
        Label = options.Label,
        City = point.City,
        State = point.State,
        TimeZone = point.TimeZone,
        RadarStation = point.RadarStation,
        ForecastOffice = point.ForecastOffice
    };

    // The hourly product starts at the top of the current hour, so the first entry is usually
    // already under way - keep it (it is "now") and drop anything that has entirely passed, which
    // a cached answer can contain by the time it's served.
    public static IReadOnlyList<WeatherHour> TrimHourly(IReadOnlyList<WeatherHour> hours, DateTimeOffset now) =>
        hours.Where(h => h.EndTime > now).Take(MaximumHourlyPeriods).ToList();

    // Sunrise and sunset from the same local arithmetic the schedules use, in the location's own
    // time zone - the one the NWS reports for the point, which is right even when the browser and
    // the server are somewhere else.
    public static WeatherSun? SunFor(GeoCoordinate at, string? timeZoneId, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        DateTimeOffset? OnToday(bool evening)
        {
            // The calculator works in UTC days; the local day we want can straddle two of them.
            for (var offset = -1; offset <= 1; offset++)
            {
                var utc = evening ? SolarCalculator.Sunset(today.AddDays(offset), at) : SolarCalculator.Sunrise(today.AddDays(offset), at);
                if (utc is null) continue;
                var local = TimeZoneInfo.ConvertTime(utc.Value, zone);
                if (DateOnly.FromDateTime(local.DateTime) == today) return local;
            }
            return null;
        }

        var sunrise = OnToday(evening: false);
        var sunset = OnToday(evening: true);
        return sunrise is null && sunset is null ? null : new WeatherSun { Sunrise = sunrise, Sunset = sunset };
    }

    // The NWS wants at most four decimals in a point path (more gets a redirect), which is also
    // the precision the Settings form saves.
    public static string PointPath(GeoCoordinate at) =>
        $"{at.Latitude.ToString("0.####", CultureInfo.InvariantCulture)},{at.Longitude.ToString("0.####", CultureInfo.InvariantCulture)}";

    // "https://api.weather.gov/icons/land/day/rain_showers,40/tsra,60?size=medium" → "rain_showers".
    // The code is all the tab needs to pick a glyph; the browser never loads the NWS picture.
    public static string? IconCode(string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return null;
        var path = iconUrl;
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];

        const string marker = "/icons/";
        var start = path.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        var segments = path[(start + marker.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        // set (land) / day-or-night / code[,chance] [/ second code[,chance]]
        if (segments.Length < 3) return null;
        var code = segments[2];
        var comma = code.IndexOf(',');
        return comma >= 0 ? code[..comma] : code;
    }

    private static readonly string[] CompassPoints =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    public static string? Compass(double? degrees)
    {
        if (degrees is null) return null;
        var normalized = ((degrees.Value % 360) + 360) % 360;
        return CompassPoints[(int)Math.Round(normalized / 22.5) % CompassPoints.Length];
    }

    public static int SeverityRank(string? severity) => severity switch
    {
        "Extreme" => 0,
        "Severe" => 1,
        "Moderate" => 2,
        "Minor" => 3,
        _ => 4
    };

    // ---- Units. Observations arrive metric (wmoUnit:degC, km_h-1, Pa, m); forecasts arrive in
    // US units already. Everything the tab shows is US, matching the NWS's own public products.

    public static double? ToFahrenheit(double? value, string? unit)
    {
        if (value is null) return null;
        if (unit is not null && (unit.EndsWith("degF", StringComparison.Ordinal) || unit == "F")) return value;
        return value * 9 / 5 + 32;
    }

    public static double? ToMph(double? value, string? unit)
    {
        if (value is null) return null;
        if (unit is null) return value * 0.621371;
        if (unit.EndsWith("m_s-1", StringComparison.Ordinal)) return value * 2.236936;
        if (unit.EndsWith("mi_h-1", StringComparison.Ordinal)) return value;
        if (unit.EndsWith("kn", StringComparison.Ordinal) || unit.EndsWith("knot", StringComparison.Ordinal)) return value * 1.150779;
        return value * 0.621371;
    }

    public static double? ToInchesOfMercury(double? pascals) => pascals / 3386.389;

    public static double? ToMiles(double? metres) => metres / 1609.344;

    private static int? RoundInt(double? value) => value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);

    private static double? Round(double? value, int decimals) => value is null ? null : Math.Round(value.Value, decimals, MidpointRounding.AwayFromZero);

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name)
    {
        var text = Str(element, name);
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    // An NWS quantity is { unitCode, value } with value often null - a sensor that didn't report.
    private static (double? Value, string? Unit) Quantity(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var quantity) || quantity.ValueKind != JsonValueKind.Object)
            return (null, null);
        return (Num(quantity, "value"), Str(quantity, "unitCode"));
    }

    private sealed record CacheEntry(object Value, DateTimeOffset FetchedAt);

    private sealed record Fetched<T>(T? Value, bool Stale, string? Problem) where T : class;

    private sealed class PointInfo
    {
        public static readonly PointInfo Unsupported = new() { Supported = false };

        public bool Supported { get; init; }
        public string ForecastUrl { get; init; } = "";
        public string HourlyUrl { get; init; } = "";
        public string StationsUrl { get; init; } = "";
        public string? City { get; init; }
        public string? State { get; init; }
        public string? TimeZone { get; init; }
        public string? RadarStation { get; init; }
        public string? ForecastOffice { get; init; }
    }

    private sealed record StationInfo(string Id, string Name);

    private sealed record ForecastSet(List<WeatherPeriod> Periods, DateTimeOffset? UpdatedAt);

    private sealed record HourlySet(List<WeatherHour> Periods);
}

// A non-2xx from the NWS, or an answer missing the part we needed. Its message is written to be
// shown on the tab as the reason a section is missing.
public sealed class WeatherUpstreamException(string message) : Exception(message);

public sealed record RadarImage(byte[]? Bytes, int Status, string? Problem)
{
    public static RadarImage Failed(int status, string problem) => new(null, status, problem);
}

// ---- What the tab receives. Serialised camelCase by ASP.NET like every other /api answer. ----

public static class WeatherReportState
{
    public const string Ok = "ok";
    public const string Unconfigured = "unconfigured";
    public const string Unsupported = "unsupported";
    public const string Unreachable = "unreachable";
}

public sealed class WeatherReport
{
    public string State { get; init; } = WeatherReportState.Ok;
    public string? Message { get; init; }
    public WeatherLocation? Location { get; init; }
    public WeatherCurrent? Current { get; init; }
    public IReadOnlyList<WeatherPeriod> Daily { get; init; } = [];
    public IReadOnlyList<WeatherHour> Hourly { get; init; } = [];
    public IReadOnlyList<WeatherAlert> Alerts { get; init; } = [];
    public WeatherSun? Sun { get; init; }
    public WeatherRadar? Radar { get; init; }
    public DateTimeOffset? ForecastUpdatedAt { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public bool Stale { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public sealed class WeatherLocation
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Label { get; init; } = "";
    public string? City { get; init; }
    public string? State { get; init; }
    public string? TimeZone { get; init; }
    public string? RadarStation { get; init; }
    public string? ForecastOffice { get; init; }
}

public sealed class WeatherCurrent
{
    public DateTimeOffset? ObservedAt { get; init; }
    public string Station { get; init; } = "";
    public string StationName { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Icon { get; init; }
    public bool? IsDaytime { get; init; }
    public int TemperatureF { get; init; }
    public int? FeelsLikeF { get; init; }
    public int? DewpointF { get; init; }
    public int? HumidityPercent { get; init; }
    public int? WindMph { get; init; }
    public int? WindGustMph { get; init; }
    public string? WindDirection { get; init; }
    public double? PressureInHg { get; init; }
    public double? VisibilityMiles { get; init; }
}

public sealed class WeatherPeriod
{
    public string Name { get; init; } = "";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public bool IsDaytime { get; init; }
    public int? TemperatureF { get; init; }
    public int? PrecipitationChance { get; init; }
    public string? WindSpeed { get; init; }
    public string? WindDirection { get; init; }
    public string? Icon { get; init; }
    public string ShortForecast { get; init; } = "";
    public string DetailedForecast { get; init; } = "";
}

public sealed class WeatherHour
{
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public bool IsDaytime { get; init; }
    public int? TemperatureF { get; init; }
    public int? PrecipitationChance { get; init; }
    public int? HumidityPercent { get; init; }
    public string? WindSpeed { get; init; }
    public string? WindDirection { get; init; }
    public string? Icon { get; init; }
    public string ShortForecast { get; init; } = "";
}

// A record, so callers (and tests) can derive a variant with `with`.
public sealed record WeatherAlert
{
    public string Id { get; init; } = "";
    public string Event { get; init; } = "";
    public string Severity { get; init; } = "Unknown";
    public string Urgency { get; init; } = "Unknown";
    public string Certainty { get; init; } = "Unknown";
    public string Headline { get; init; } = "";
    public string Description { get; init; } = "";
    public string Instruction { get; init; } = "";
    public DateTimeOffset? Onset { get; init; }
    public DateTimeOffset? Ends { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public string AreaDescription { get; init; } = "";
    public string Sender { get; init; } = "";

    // "Alert", "Update" or "Cancel", and for the latter two the ids this one replaces.
    public string MessageType { get; init; } = "Alert";
    public IReadOnlyList<string> References { get; init; } = [];
}

public sealed class WeatherSun
{
    public DateTimeOffset? Sunrise { get; init; }
    public DateTimeOffset? Sunset { get; init; }
}

public sealed class WeatherRadar
{
    public string Station { get; init; } = "";
}
