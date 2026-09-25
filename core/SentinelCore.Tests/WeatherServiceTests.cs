extern alias WebHost;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// WeatherService against a scripted National Weather Service: what the tab is told when the
// location is unset, when it is outside the NWS's coverage, when one product fails and the rest
// don't, and that the cache actually spares weather.gov the repeat requests it is there to spare.
// Everything on the wire is a fixture shaped like the real answers captured on 2026-09-25.
public sealed class WeatherServiceTests : IDisposable
{
    private const double Latitude = 38.2527;
    private const double Longitude = -85.7585;

    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly ScriptedNws _nws = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero));

    public WeatherServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"weather-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "appsettings.json");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task NoLocation_ReportsUnconfigured_WithoutAskingTheNws()
    {
        WriteLocation(0, 0);
        var service = CreateService();

        var report = await service.GetReportAsync(CancellationToken.None);

        Assert.Equal(WeatherReportState.Unconfigured, report.State);
        Assert.Contains("LOCATION", report.Message);
        Assert.Empty(_nws.Requests);
    }

    [Fact]
    public async Task FullReport_ShapesEveryProductForTheTab()
    {
        WriteLocation(Latitude, Longitude, "Louisville, KY");
        _nws.ScriptHappyPath();
        var service = CreateService();

        var report = await service.GetReportAsync(CancellationToken.None);

        Assert.Equal(WeatherReportState.Ok, report.State);
        Assert.False(report.Stale);
        Assert.Empty(report.Problems);

        Assert.NotNull(report.Location);
        Assert.Equal("Louisville", report.Location.City);
        Assert.Equal("KY", report.Location.State);
        Assert.Equal("America/New_York", report.Location.TimeZone);
        Assert.Equal("Louisville, KY", report.Location.Label);
        Assert.Equal("KLVX", report.Radar?.Station);

        // Observations arrive metric and leave in the units the NWS's own public pages use.
        var current = Assert.IsType<WeatherCurrent>(report.Current);
        Assert.Equal("KLOU", current.Station);
        Assert.Equal("Louisville, Bowman Field Airport", current.StationName);
        Assert.Equal("Clear", current.Description);
        Assert.Equal(64, current.TemperatureF);          // 18 °C
        Assert.Equal(50, current.DewpointF);             // 10 °C
        Assert.Equal(60, current.HumidityPercent);       // 59.52
        Assert.Equal(5, current.WindMph);                // 7.416 km/h
        Assert.Null(current.WindGustMph);                // value: null
        Assert.Equal("E", current.WindDirection);        // 90°
        Assert.Equal(30.26, current.PressureInHg);       // 102472.12 Pa
        Assert.Equal(10.0, current.VisibilityMiles);     // 16093.44 m
        Assert.Equal("skc", current.Icon);
        Assert.True(current.IsDaytime);
        Assert.Null(current.FeelsLikeF);

        // The seven-day list is passed through with the icon reduced to its code.
        Assert.Equal(2, report.Daily.Count);
        Assert.Equal("Today", report.Daily[0].Name);
        Assert.Equal(76, report.Daily[0].TemperatureF);
        Assert.Equal(0, report.Daily[0].PrecipitationChance);
        Assert.Equal("few", report.Daily[0].Icon);
        Assert.Equal("bkn", report.Daily[1].Icon);
        Assert.False(report.Daily[1].IsDaytime);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 13, 51, 11, TimeSpan.Zero), report.ForecastUpdatedAt);

        // The hour that ended before "now" (14:00-15:00Z at a 15:00Z clock) is dropped; the one
        // under way (15:00-16:00Z) and the rest stay, in order.
        Assert.Equal(2, report.Hourly.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 11, 0, 0, TimeSpan.FromHours(-4)), report.Hourly[0].StartTime);
        Assert.Equal(66, report.Hourly[0].HumidityPercent);
        Assert.Equal("rain_showers", report.Hourly[1].Icon);

        var alert = Assert.Single(report.Alerts);
        Assert.Equal("Wind Advisory", alert.Event);
        Assert.Equal("Moderate", alert.Severity);
        Assert.Contains("Jefferson", alert.AreaDescription);

        // Sunrise and sunset for the day at the location, in its own zone.
        Assert.NotNull(report.Sun?.Sunrise);
        Assert.NotNull(report.Sun?.Sunset);
        Assert.Equal(TimeSpan.FromHours(-4), report.Sun!.Sunrise!.Value.Offset);
        Assert.InRange(report.Sun.Sunrise.Value.Hour, 6, 8);
        Assert.InRange(report.Sun.Sunset!.Value.Hour, 18, 20);

        // Every request carried the User-Agent the NWS insists on.
        Assert.All(_nws.Requests, request => Assert.Equal(WeatherService.UserAgent, request.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task OutsideNwsCoverage_ReportsUnsupported_AndDoesNotAskAgain()
    {
        WriteLocation(51.5, -0.12, "London");
        _nws.Respond("/points/", HttpStatusCode.NotFound, """{"type":"https://api.weather.gov/problems/InvalidPoint","status":404}""");
        var service = CreateService();

        var first = await service.GetReportAsync(CancellationToken.None);
        var second = await service.GetReportAsync(CancellationToken.None);

        Assert.Equal(WeatherReportState.Unsupported, first.State);
        Assert.Contains("United States", first.Message);
        Assert.Equal(WeatherReportState.Unsupported, second.State);
        // One point lookup, cached like any other answer - not one per refresh.
        Assert.Single(_nws.Requests);
    }

    [Fact]
    public async Task NwsUnreachable_ReportsUnreachable_AndHoldsOffRetrying()
    {
        WriteLocation(Latitude, Longitude);
        _nws.Fail("/points/", new HttpRequestException("connection refused"));
        var service = CreateService();

        var first = await service.GetReportAsync(CancellationToken.None);
        var second = await service.GetReportAsync(CancellationToken.None);

        Assert.Equal(WeatherReportState.Unreachable, first.State);
        Assert.Contains("could not be reached", first.Message);
        Assert.Equal(WeatherReportState.Unreachable, second.State);
        // The second call within the hold does not fire another doomed request.
        Assert.Single(_nws.Requests);

        _clock.Advance(WeatherService.RetryHold + TimeSpan.FromSeconds(1));
        await service.GetReportAsync(CancellationToken.None);
        Assert.Equal(2, _nws.Requests.Count);
    }

    [Fact]
    public async Task ObservationsFailing_LeavesTheForecastOnScreen_WithANote()
    {
        WriteLocation(Latitude, Longitude);
        _nws.ScriptHappyPath();
        _nws.Respond("/observations/latest", HttpStatusCode.ServiceUnavailable, "");
        var service = CreateService();

        var report = await service.GetReportAsync(CancellationToken.None);

        Assert.Equal(WeatherReportState.Ok, report.State);
        Assert.Null(report.Current);
        Assert.Equal(2, report.Daily.Count);
        var problem = Assert.Single(report.Problems);
        Assert.StartsWith("Current conditions unavailable", problem);
        Assert.Contains("HTTP 503", problem);
        // Three stations offered, all three tried before giving up.
        Assert.Equal(3, _nws.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/observations/latest")));
    }

    [Fact]
    public async Task SilentNearestStation_FallsThroughToTheNextOne()
    {
        WriteLocation(Latitude, Longitude);
        _nws.ScriptHappyPath();
        _nws.Respond("/stations/KLOU/observations/latest", HttpStatusCode.OK, Fixtures.ObservationWithNoTemperature);
        var service = CreateService();

        var report = await service.GetReportAsync(CancellationToken.None);

        Assert.Equal("KSDF", report.Current?.Station);
    }

    [Fact]
    public async Task SecondCallWithinTheTtls_ServesFromCache()
    {
        WriteLocation(Latitude, Longitude);
        _nws.ScriptHappyPath();
        var service = CreateService();

        await service.GetReportAsync(CancellationToken.None);
        var requestsAfterFirst = _nws.Requests.Count;
        _clock.Advance(TimeSpan.FromMinutes(4));
        await service.GetReportAsync(CancellationToken.None);

        Assert.Equal(requestsAfterFirst, _nws.Requests.Count);

        // Past the alerts TTL only the alerts are asked for again - the forecast and the point
        // are still fresh.
        _clock.Advance(TimeSpan.FromMinutes(2));
        await service.GetReportAsync(CancellationToken.None);
        var refetched = _nws.Requests.Skip(requestsAfterFirst).Select(r => r.RequestUri!.AbsolutePath).ToList();
        Assert.Equal(["/alerts/active"], refetched);
    }

    [Fact]
    public async Task ForecastFailingAfterItWasCached_ServesTheOldOneMarkedStale()
    {
        WriteLocation(Latitude, Longitude);
        _nws.ScriptHappyPath();
        var service = CreateService();
        await service.GetReportAsync(CancellationToken.None);

        _clock.Advance(WeatherService.ForecastTtl + TimeSpan.FromMinutes(1));
        _nws.Fail("/gridpoints/LMK/50,78/forecast", new TaskCanceledException("timed out"));
        var report = await service.GetReportAsync(CancellationToken.None);

        Assert.Equal(WeatherReportState.Ok, report.State);
        Assert.True(report.Stale);
        Assert.Equal(2, report.Daily.Count);
        Assert.Contains(report.Problems, p => p.Contains("may be out of date") && p.Contains("too long"));
    }

    [Fact]
    public async Task MovingTheLocation_ForgetsTheOldTownsForecast()
    {
        WriteLocation(Latitude, Longitude);
        _nws.ScriptHappyPath();
        var service = CreateService();
        await service.GetReportAsync(CancellationToken.None);
        var requestsAfterFirst = _nws.Requests.Count;

        WriteLocation(40.7128, -74.006, "New York");
        await service.GetReportAsync(CancellationToken.None);

        Assert.Contains(_nws.Requests.Skip(requestsAfterFirst), r => r.RequestUri!.AbsolutePath == "/points/40.7128,-74.006");
    }

    [Fact]
    public async Task Radar_ProxiesTheStationLoop_AndTheNationalComposite()
    {
        WriteLocation(Latitude, Longitude);
        _nws.ScriptHappyPath();
        var service = CreateService();

        var local = await service.GetRadarAsync("local", "loop", CancellationToken.None);
        var national = await service.GetRadarAsync("national", "still", CancellationToken.None);
        var bogus = await service.GetRadarAsync("regional", "loop", CancellationToken.None);

        Assert.Equal(200, local.Status);
        Assert.Equal(Fixtures.Gif, local.Bytes);
        Assert.Contains(_nws.Requests, r => r.RequestUri!.ToString() == $"{WeatherService.RadarBaseUrl}/KLVX_loop.gif");
        Assert.Equal(200, national.Status);
        Assert.Contains(_nws.Requests, r => r.RequestUri!.ToString() == $"{WeatherService.RadarBaseUrl}/CONUS_0.gif");
        Assert.Equal(400, bogus.Status);

        // A second request for the same frame inside two minutes is served from memory.
        var before = _nws.Requests.Count;
        await service.GetRadarAsync("local", "loop", CancellationToken.None);
        Assert.Equal(before, _nws.Requests.Count);
    }

    [Fact]
    public async Task Radar_WithNoLocation_Is404()
    {
        WriteLocation(0, 0);
        var service = CreateService();

        var image = await service.GetRadarAsync("local", "loop", CancellationToken.None);

        Assert.Equal(404, image.Status);
        Assert.Empty(_nws.Requests);
    }

    [Theory]
    [InlineData("https://api.weather.gov/icons/land/day/few?size=medium", "few")]
    [InlineData("https://api.weather.gov/icons/land/night/bkn?size=small", "bkn")]
    [InlineData("https://api.weather.gov/icons/land/day/rain_showers,40/tsra,60?size=medium", "rain_showers")]
    [InlineData("https://api.weather.gov/icons/land/day/tsra_hi,30", "tsra_hi")]
    [InlineData("not an icon url", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void IconCode_KeepsOnlyTheFirstConditionCode(string? url, string? expected) =>
        Assert.Equal(expected, WeatherService.IconCode(url));

    [Theory]
    [InlineData(0, "N")]
    [InlineData(90, "E")]
    [InlineData(180, "S")]
    [InlineData(270, "W")]
    [InlineData(348.75, "N")]
    [InlineData(22.5, "NNE")]
    [InlineData(45, "NE")]
    [InlineData(-90, "W")]
    public void Compass_RoundsToSixteenPoints(double degrees, string expected) =>
        Assert.Equal(expected, WeatherService.Compass(degrees));

    [Fact]
    public void Units_ConvertMetricObservationsToUs()
    {
        Assert.Equal(64.4, WeatherService.ToFahrenheit(18, "wmoUnit:degC")!.Value, 3);
        Assert.Equal(76, WeatherService.ToFahrenheit(76, "F"));
        Assert.Equal(4.608, WeatherService.ToMph(7.416, "wmoUnit:km_h-1")!.Value, 3);
        Assert.Equal(22.369, WeatherService.ToMph(10, "wmoUnit:m_s-1")!.Value, 3);
        Assert.Equal(30.26, WeatherService.ToInchesOfMercury(102472.12)!.Value, 2);
        Assert.Equal(10.0, WeatherService.ToMiles(16093.44)!.Value, 3);
        Assert.Null(WeatherService.ToFahrenheit(null, "wmoUnit:degC"));
    }

    [Fact]
    public void Alerts_RankWorstFirst()
    {
        Assert.True(WeatherService.SeverityRank("Extreme") < WeatherService.SeverityRank("Severe"));
        Assert.True(WeatherService.SeverityRank("Severe") < WeatherService.SeverityRank("Moderate"));
        Assert.True(WeatherService.SeverityRank("Moderate") < WeatherService.SeverityRank("Minor"));
        Assert.True(WeatherService.SeverityRank("Minor") < WeatherService.SeverityRank(null));
    }

    // ---- Plumbing ----

    private WeatherService CreateService() =>
        new(new SingleHandlerFactory(_nws), new AppSettingsFile(_settingsPath), NullLogger<WeatherService>.Instance, _clock);

    private void WriteLocation(double latitude, double longitude, string label = "")
    {
        var json = $$"""
            {
              "Auth": { "Enabled": false },
              "Location": { "Latitude": {{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "Longitude": {{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "Label": "{{label}}" }
            }
            """;
        File.WriteAllText(_settingsPath, json);
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // Routes by the longest matching path fragment, so a test can override one product on top of
    // the happy path. Records every request so a test can count what reached the NWS.
    private sealed class ScriptedNws : HttpMessageHandler
    {
        private readonly List<(string Fragment, Func<HttpResponseMessage> Respond)> _routes = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Respond(string fragment, HttpStatusCode status, string body, string contentType = "application/geo+json") =>
            _routes.Add((fragment, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) }));

        public void RespondBytes(string fragment, byte[] bytes) =>
            _routes.Add((fragment, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));

        public void Fail(string fragment, Exception exception) =>
            _routes.Add((fragment, () => throw exception));

        public void ScriptHappyPath()
        {
            Respond("/points/", HttpStatusCode.OK, Fixtures.Point);
            Respond("/gridpoints/LMK/50,78/forecast", HttpStatusCode.OK, Fixtures.Forecast);
            Respond("/gridpoints/LMK/50,78/forecast/hourly", HttpStatusCode.OK, Fixtures.Hourly);
            Respond("/gridpoints/LMK/50,78/stations", HttpStatusCode.OK, Fixtures.Stations);
            Respond("/observations/latest", HttpStatusCode.OK, Fixtures.Observation);
            Respond("/alerts/active", HttpStatusCode.OK, Fixtures.Alerts);
            RespondBytes(".gif", Fixtures.Gif);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            // Later registrations win over earlier ones of the same length, so an override added
            // after ScriptHappyPath takes effect.
            var route = _routes
                .Where(r => url.Contains(r.Fragment, StringComparison.Ordinal))
                .Select((r, index) => (r, index))
                .OrderByDescending(x => x.r.Fragment.Length)
                .ThenByDescending(x => x.index)
                .Select(x => x.r)
                .FirstOrDefault();
            if (route.Respond is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("unscripted: " + url) });
            return Task.FromResult(route.Respond());
        }
    }

    private static class Fixtures
    {
        public static readonly byte[] Gif = "GIF89a-not-really"u8.ToArray();

        public const string Point = """
            {
              "properties": {
                "cwa": "LMK",
                "forecast": "https://api.weather.gov/gridpoints/LMK/50,78/forecast",
                "forecastHourly": "https://api.weather.gov/gridpoints/LMK/50,78/forecast/hourly",
                "observationStations": "https://api.weather.gov/gridpoints/LMK/50,78/stations",
                "relativeLocation": { "properties": { "city": "Louisville", "state": "KY" } },
                "timeZone": "America/New_York",
                "radarStation": "KLVX"
              }
            }
            """;

        public const string Forecast = """
            {
              "properties": {
                "updateTime": "2026-09-25T13:51:11+00:00",
                "periods": [
                  {
                    "number": 1, "name": "Today",
                    "startTime": "2026-09-25T10:00:00-04:00", "endTime": "2026-09-25T18:00:00-04:00",
                    "isDaytime": true, "temperature": 76, "temperatureUnit": "F",
                    "probabilityOfPrecipitation": { "unitCode": "wmoUnit:percent", "value": 0 },
                    "windSpeed": "2 to 8 mph", "windDirection": "E",
                    "icon": "https://api.weather.gov/icons/land/day/few?size=medium",
                    "shortForecast": "Sunny", "detailedForecast": "Sunny, with a high near 76."
                  },
                  {
                    "number": 2, "name": "Tonight",
                    "startTime": "2026-09-25T18:00:00-04:00", "endTime": "2026-09-26T06:00:00-04:00",
                    "isDaytime": false, "temperature": 53, "temperatureUnit": "F",
                    "probabilityOfPrecipitation": { "unitCode": "wmoUnit:percent", "value": null },
                    "windSpeed": "3 to 8 mph", "windDirection": "NE",
                    "icon": "https://api.weather.gov/icons/land/night/bkn?size=medium",
                    "shortForecast": "Mostly Cloudy", "detailedForecast": "Mostly cloudy, with a low around 53."
                  }
                ]
              }
            }
            """;

        public const string Hourly = """
            {
              "properties": {
                "periods": [
                  {
                    "startTime": "2026-09-25T10:00:00-04:00", "endTime": "2026-09-25T11:00:00-04:00",
                    "isDaytime": true, "temperature": 62, "temperatureUnit": "F",
                    "probabilityOfPrecipitation": { "value": 0 }, "relativeHumidity": { "value": 70 },
                    "windSpeed": "2 mph", "windDirection": "S",
                    "icon": "https://api.weather.gov/icons/land/day/few?size=small", "shortForecast": "Sunny"
                  },
                  {
                    "startTime": "2026-09-25T11:00:00-04:00", "endTime": "2026-09-25T12:00:00-04:00",
                    "isDaytime": true, "temperature": 65, "temperatureUnit": "F",
                    "probabilityOfPrecipitation": { "value": 0 }, "relativeHumidity": { "value": 66 },
                    "windSpeed": "3 mph", "windDirection": "S",
                    "icon": "https://api.weather.gov/icons/land/day/few?size=small", "shortForecast": "Sunny"
                  },
                  {
                    "startTime": "2026-09-25T12:00:00-04:00", "endTime": "2026-09-25T13:00:00-04:00",
                    "isDaytime": true, "temperature": 68, "temperatureUnit": "F",
                    "probabilityOfPrecipitation": { "value": 30 }, "relativeHumidity": { "value": 60 },
                    "windSpeed": "5 mph", "windDirection": "SW",
                    "icon": "https://api.weather.gov/icons/land/day/rain_showers,30?size=small", "shortForecast": "Chance Rain Showers"
                  }
                ]
              }
            }
            """;

        public const string Stations = """
            {
              "features": [
                { "properties": { "stationIdentifier": "KLOU", "name": "Louisville, Bowman Field Airport" } },
                { "properties": { "stationIdentifier": "KSDF", "name": "Louisville, Standiford Field" } },
                { "properties": { "stationIdentifier": "KJVY", "name": "Clark Regional Airport" } }
              ]
            }
            """;

        public const string Observation = """
            {
              "properties": {
                "timestamp": "2026-09-25T14:20:00+00:00",
                "textDescription": "Clear",
                "icon": "https://api.weather.gov/icons/land/day/skc?size=medium",
                "temperature": { "unitCode": "wmoUnit:degC", "value": 18 },
                "dewpoint": { "unitCode": "wmoUnit:degC", "value": 10 },
                "windDirection": { "unitCode": "wmoUnit:degree_(angle)", "value": 90 },
                "windSpeed": { "unitCode": "wmoUnit:km_h-1", "value": 7.416 },
                "windGust": { "unitCode": "wmoUnit:km_h-1", "value": null },
                "barometricPressure": { "unitCode": "wmoUnit:Pa", "value": 102472.12 },
                "relativeHumidity": { "unitCode": "wmoUnit:percent", "value": 59.52339300817 },
                "windChill": { "unitCode": "wmoUnit:degC", "value": null },
                "heatIndex": { "unitCode": "wmoUnit:degC", "value": null },
                "visibility": { "unitCode": "wmoUnit:m", "value": 16093.44 }
              }
            }
            """;

        public const string ObservationWithNoTemperature = """
            {
              "properties": {
                "timestamp": "2026-09-25T14:20:00+00:00",
                "textDescription": "",
                "temperature": { "unitCode": "wmoUnit:degC", "value": null }
              }
            }
            """;

        public const string Alerts = """
            {
              "features": [
                {
                  "id": "https://api.weather.gov/alerts/urn:oid:2.49.0.1.840.0.1",
                  "properties": {
                    "id": "urn:oid:2.49.0.1.840.0.1",
                    "event": "Wind Advisory",
                    "severity": "Moderate", "urgency": "Expected", "certainty": "Likely",
                    "headline": "Wind Advisory issued September 25 at 9:00AM EDT until September 25 at 8:00PM EDT by NWS Louisville KY",
                    "description": "* WHAT...Southwest winds 20 to 30 mph with gusts up to 45 mph.",
                    "instruction": "Use extra caution when driving.",
                    "onset": "2026-09-25T09:00:00-04:00", "ends": "2026-09-25T20:00:00-04:00", "expires": "2026-09-25T20:00:00-04:00",
                    "areaDesc": "Jefferson; Oldham",
                    "senderName": "NWS Louisville KY",
                    "status": "Actual"
                  }
                }
              ]
            }
            """;
    }
}
