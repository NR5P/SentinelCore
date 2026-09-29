using System.Globalization;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The Weather section: decoding the server's answers (core/SentinelCore.Web/WeatherService.cs and
/// WeatherMapService.cs), the routes asked for, and the logic ported from the dashboard's weather.js.
/// </summary>
public class WeatherTests
{
    private static SentinelClient ClientFor(FakeServer server) =>
        new(() => server.BaseUrl, NullSentinelLog.Instance);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // A fixed zone rather than "America/Chicago", so the tests read the same on any machine: UTC-5.
    private static readonly WeatherClock Central = new(
        TimeZoneInfo.CreateCustomTimeZone("Test Central", TimeSpan.FromHours(-5), "Test Central", "Test Central"), English);

    private const string ReportJson = """
        {
          "state": "ok",
          "location": { "latitude": 41.25, "longitude": -96.0, "label": "Home", "city": "Omaha", "state": "NE",
                        "timeZone": "America/Chicago", "radarStation": "KOAX", "forecastOffice": "OAX" },
          "current": { "observedAt": "2026-09-29T19:52:00+00:00", "station": "KOMA", "stationName": "Eppley Airfield",
                       "description": "Mostly Cloudy", "icon": "bkn", "isDaytime": true, "temperatureF": 72,
                       "feelsLikeF": 74, "dewpointF": 66, "humidityPercent": 71, "windMph": 12, "windGustMph": 22,
                       "windDirection": "S", "pressureInHg": 29.92, "visibilityMiles": 10 },
          "daily": [
            { "name": "This Afternoon", "startTime": "2026-09-29T14:00:00-05:00", "endTime": "2026-09-29T18:00:00-05:00",
              "isDaytime": true, "temperatureF": 78, "precipitationChance": 20, "windSpeed": "10 mph", "windDirection": "S",
              "icon": "tsra_sct", "shortForecast": "Chance Showers", "detailedForecast": "A chance of showers." },
            { "name": "Tonight", "startTime": "2026-09-29T18:00:00-05:00", "endTime": "2026-09-30T06:00:00-05:00",
              "isDaytime": false, "temperatureF": 61, "precipitationChance": 60, "windSpeed": "5 mph", "windDirection": "SE",
              "icon": "tsra", "shortForecast": "Thunderstorms", "detailedForecast": "Storms likely." },
            { "name": "Wednesday", "startTime": "2026-09-30T06:00:00-05:00", "endTime": "2026-09-30T18:00:00-05:00",
              "isDaytime": true, "temperatureF": 70, "precipitationChance": null, "windSpeed": "15 mph", "windDirection": "NW",
              "icon": "few", "shortForecast": "Sunny", "detailedForecast": "Sunny." }
          ],
          "hourly": [
            { "startTime": "2026-09-29T15:00:00-05:00", "endTime": "2026-09-29T16:00:00-05:00", "isDaytime": true,
              "temperatureF": 76, "precipitationChance": 10, "humidityPercent": 60, "windSpeed": "10 mph", "windDirection": "S",
              "icon": "bkn", "shortForecast": "Mostly Cloudy" }
          ],
          "alerts": [
            { "id": "urn:1", "event": "Tornado Warning", "severity": "Extreme", "urgency": "Immediate", "certainty": "Observed",
              "headline": "Tornado Warning until 4:15 PM", "description": "A tornado was seen.", "instruction": "Take cover.",
              "ends": "2026-09-29T21:15:00+00:00", "areaDescription": "Douglas", "sender": "NWS Omaha",
              "messageType": "Alert", "references": [] }
          ],
          "sun": { "sunrise": "2026-09-29T12:20:00+00:00", "sunset": "2026-09-30T00:10:00+00:00" },
          "radar": { "station": "KOAX" },
          "forecastUpdatedAt": "2026-09-29T19:00:00+00:00",
          "fetchedAt": "2026-09-29T20:00:00+00:00",
          "stale": false,
          "problems": []
        }
        """;

    // ---------------------------------------------------------------- Decoding and routes

    [Fact]
    public async Task Report_decodes_every_part()
    {
        using var server = new FakeServer().OnGet("/api/weather", ReportJson);
        using var client = ClientFor(server);

        var report = await client.WeatherAsync();

        Assert.True(report.IsOk);
        Assert.Equal("Home", report.Location!.DisplayName);
        Assert.Equal("America/Chicago", report.Location.TimeZone);
        Assert.Equal(72, report.Current!.TemperatureF);
        Assert.Equal(29.92, report.Current.PressureInHg);
        Assert.Equal(3, report.Daily.Count);
        Assert.Null(report.Daily[2].PrecipitationChance);
        Assert.Single(report.Hourly);
        var alert = Assert.Single(report.Alerts);
        Assert.True(alert.IsSevere);
        Assert.Equal("KOAX", report.Radar!.Station);
        Assert.NotNull(report.Sun!.Sunset);
    }

    [Fact]
    public async Task A_report_that_only_explains_itself_still_decodes()
    {
        using var server = new FakeServer().OnGet("/api/weather",
            """{"state":"unconfigured","message":"Set a location in Settings.","daily":[],"hourly":[],"alerts":[],"fetchedAt":"2026-09-29T20:00:00Z","stale":false,"problems":[]}""");
        using var client = ClientFor(server);

        var report = await client.WeatherAsync();

        Assert.False(report.IsOk);
        Assert.Equal(WeatherStates.Unconfigured, report.State);
        Assert.Equal("Set a location in Settings.", report.Message);
        Assert.Null(report.Location);
        Assert.Null(report.Current);
    }

    [Fact]
    public async Task Details_decode_including_the_severe_outlook()
    {
        using var server = new FakeServer().OnGet("/api/weather/details", """
            {"state":"ok",
             "totals":{"rainNext24HoursInches":0.42,"rainNext7DaysInches":1.1,"snowNext24HoursInches":0,"snowNext7DaysInches":0,
                       "iceNext7DaysInches":0,"thunderChanceNext24Hours":55,"thunderChanceNext7Days":70},
             "hours":[{"startTime":"2026-09-29T20:00:00+00:00","feelsLikeF":74,"gustMph":22,"skyCoverPercent":80,"rainInches":0.02,"snowInches":0}],
             "pressure":{"changeInHgPer3Hours":-0.05,"trend":"falling"},
             "discussion":{"office":"OAX","issuedAt":"2026-09-29T18:00:00+00:00","text":"Storms this evening."},
             "severe":{"days":[{"day":1,"category":"ENH","name":"Enhanced","rank":4,"color":"#f6b35c"},{"day":2,"category":null,"name":"","rank":0}],
                       "tornadoPercent":5,"hailPercent":30,"windPercent":30,"significantTornado":false,"significantHail":true,"significantWind":false,
                       "issuedAt":"2026-09-29T16:30:00+00:00"},
             "fetchedAt":"2026-09-29T20:00:00+00:00","problems":[]}
            """);
        using var client = ClientFor(server);

        var details = await client.WeatherDetailsAsync();

        Assert.Equal(0.42, details.Totals!.RainNext24HoursInches);
        Assert.Equal("falling", details.Pressure!.Trend);
        Assert.Equal("OAX", details.Discussion!.Office);
        Assert.Equal("ENH", details.Severe!.Days[0].Category);
        Assert.Null(details.Severe.Days[1].Category);
        Assert.True(details.Severe.SignificantHail);
        Assert.Equal(22, Assert.Single(details.Hours).GustMph);
    }

    [Fact]
    public async Task Map_and_storms_ask_with_the_right_query_and_decode_polygons()
    {
        using var server = new FakeServer()
            .OnGet("/api/weather/map", """
                {"home":{"latitude":41.25,"longitude":-96,"label":"Home"},"homeStation":"KOAX","station":"KDMX",
                 "stations":[{"id":"KOAX","name":"Omaha","latitude":41.32,"longitude":-96.37}],
                 "layers":[{"id":"reflectivity","name":"Reflectivity","scope":"national","description":"d","times":["2026-09-29T19:50:00Z"]}],
                 "maximumRadarZoom":12,"maximumBaseZoom":16,"problems":[]}
                """)
            .OnGet("/api/weather/storms", """
                {"cells":[{"id":"KOAX-A1","radar":"KOAX","stormId":"A1","latitude":41,"longitude":-96.5,"threat":3,"tornadic":false,
                           "rotation":true,"mesoStrength":"6","track":[{"minutes":15,"latitude":41.1,"longitude":-96.3}],
                           "distanceMiles":25.4,"bearingCompass":"SW","towardHome":true,"closestMiles":0.4,"etaMinutes":35}],
                 "warnings":[{"id":"w1","phenomena":"TO","significance":"W","name":"Tornado Warning","containsHome":true,
                              "polygons":[[[[-96.1,41.2],[-96.0,41.3],[-95.9,41.2],[-96.1,41.2]]]]}],
                 "outlook":{"areas":[],"issuedAt":null},"fetchedAt":"2026-09-29T20:00:00Z","problems":[]}
                """);
        using var client = ClientFor(server);

        var map = await client.WeatherMapAsync("KDMX");
        var storms = await client.WeatherStormsNearAsync(41.25, -96.0);

        Assert.Equal("KDMX", map.Station);
        Assert.Equal("national", Assert.Single(map.Layers).Scope);
        Assert.Contains("GET /api/weather/map?station=KDMX", server.Requests);
        Assert.Contains("GET /api/weather/storms?west=-98.5&south=39.25&east=-93.5&north=43.25", server.Requests);

        var cell = Assert.Single(storms.Cells);
        Assert.Equal(35, cell.EtaMinutes);
        Assert.Equal(15, Assert.Single(cell.Track).Minutes);
        var warning = Assert.Single(storms.Warnings);
        Assert.Equal(-96.1, warning.Polygons[0][0][0][0]);
        Assert.Equal(41.2, warning.Polygons[0][0][0][1]);
    }

    [Fact]
    public async Task Radar_picture_asks_for_the_scope_and_kind()
    {
        using var server = new FakeServer().OnGet("/api/weather/radar", "GIF89a");
        using var client = ClientFor(server);

        var bytes = await client.WeatherRadarImageAsync("national", "loop");

        Assert.Equal("GIF89a"u8.ToArray(), bytes);
        Assert.Contains("GET /api/weather/radar?scope=national&kind=loop", server.Requests);
    }

    // ---------------------------------------------------------------- Grouping and time

    private static WeatherPeriod Period(string name, string start, bool day, int? temp, int? chance = null) => new()
    {
        Name = name,
        StartTime = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture),
        IsDaytime = day,
        TemperatureF = temp,
        PrecipitationChance = chance,
        WindDirection = "S",
        WindSpeed = "10 mph",
    };

    [Fact]
    public void Half_days_group_into_calendar_days_in_the_locations_zone()
    {
        var periods = new[]
        {
            // Tonight starts before today's day period is known - the evening report.
            Period("Tonight", "2026-09-29T18:00:00-05:00", false, 61, 60),
            Period("Wednesday", "2026-09-30T06:00:00-05:00", true, 70, 10),
            Period("Wednesday Night", "2026-09-30T18:00:00-05:00", false, 55),
            // 11pm local is 04:00 UTC the next day: still Thursday's evening in Central.
            Period("Thursday", "2026-10-01T06:00:00-05:00", true, 68),
            Period("Thursday Night", "2026-10-01T23:00:00-05:00", false, 50),
        };

        var days = WeatherDay.Group(periods, Central);

        Assert.Equal(new[] { "2026-09-29", "2026-09-30", "2026-10-01" }, days.Select(day => day.Key));
        Assert.Null(days[0].Day);
        Assert.Equal("Tonight", days[0].Lead.Name);
        Assert.Equal(61, days[0].Low);
        Assert.Null(days[0].High);
        Assert.Equal(70, days[1].High);
        Assert.Equal(55, days[1].Low);
        Assert.Equal(10, days[1].Chance);
        Assert.Equal("💧 10% · S 10 mph", days[1].Meta);
        Assert.Equal(new[] { "Wednesday", "Wednesday Night" }, days[1].Periods.Select(period => period.Name));
        Assert.Equal(50, days[2].Low);
    }

    [Fact]
    public void Range_bars_sit_within_the_weeks_range()
    {
        var periods = new[]
        {
            Period("Today", "2026-09-29T06:00:00-05:00", true, 80),
            Period("Tonight", "2026-09-29T18:00:00-05:00", false, 60),
            Period("Wednesday", "2026-09-30T06:00:00-05:00", true, 70),
        };
        var (min, max) = WeatherDay.WeekRange(periods)!.Value;
        var days = WeatherDay.Group(periods, Central);

        Assert.Equal((60, 80), (min, max));
        Assert.Equal((0.0, 1.0), days[0].RangeWithin(min, max));

        // One temperature still gets a visible mark, and never runs off the end.
        var (left, width) = days[1].RangeWithin(min, max)!.Value;
        Assert.Equal(0.03, width, 3);
        Assert.Equal(0.5, left, 3);
        var hot = WeatherDay.Group(new[] { Period("Hot", "2026-10-01T06:00:00-05:00", true, 80) }, Central)[0];
        var (hotLeft, hotWidth) = hot.RangeWithin(min, max)!.Value;
        Assert.True(hotLeft + hotWidth <= 1.0 + 1e-9);
    }

    [Fact]
    public void The_clock_formats_in_the_locations_zone()
    {
        var at = DateTimeOffset.Parse("2026-09-29T20:05:00Z", CultureInfo.InvariantCulture);

        Assert.Equal("3:05 PM", Central.Clock(at));
        Assert.Equal("3 PM", Central.Hour(at));
        Assert.Equal("Tue", Central.Weekday(at));
        Assert.Equal("Tue 3:05 PM", Central.WeekdayClock(at));

        var european = new WeatherClock(Central.Zone, CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("15:05", european.Clock(at));
        Assert.Equal("15:00", european.Hour(at));
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_this_machines()
    {
        Assert.Equal(TimeZoneInfo.Local, WeatherClock.For("Nowhere/Special").Zone);
        Assert.Equal(TimeZoneInfo.Local, WeatherClock.For(null).Zone);
        Assert.Equal(TimeSpan.FromHours(9), WeatherClock.For("Asia/Tokyo").Zone.BaseUtcOffset);
    }

    // ---------------------------------------------------------------- Text

    private static WeatherHour Hour(int offset, int? chance, string forecast = "Showers") => new()
    {
        StartTime = DateTimeOffset.Parse("2026-09-29T15:00:00-05:00", CultureInfo.InvariantCulture).AddHours(offset),
        PrecipitationChance = chance,
        ShortForecast = forecast,
    };

    [Fact]
    public void Next_precipitation_reads_like_the_dashboard()
    {
        Assert.Null(WeatherText.NextPrecipitation(Array.Empty<WeatherHour>(), Central));

        var dry = Enumerable.Range(0, 12).Select(i => Hour(i, 0)).ToList();
        Assert.Equal("No rain expected in the next 12 hours.", WeatherText.NextPrecipitation(dry, Central));

        var later = Enumerable.Range(0, 12).Select(i => Hour(i, i >= 3 ? 70 : 10, "Chance Thunderstorms")).ToList();
        Assert.Equal("Storms likely from around 6 PM (70%).", WeatherText.NextPrecipitation(later, Central));

        var stopping = Enumerable.Range(0, 12).Select(i => Hour(i, i < 2 ? 80 : 10, "Light Snow")).ToList();
        Assert.Equal("Snow likely until around 5 PM.", WeatherText.NextPrecipitation(stopping, Central));

        var allDay = Enumerable.Range(0, 12).Select(i => Hour(i, 90, "Rain")).ToList();
        Assert.Equal("Rain likely for the next 12 hours.", WeatherText.NextPrecipitation(allDay, Central));

        var slight = Enumerable.Range(0, 12).Select(i => Hour(i, i == 4 ? 30 : 5, "Patchy Drizzle")).ToList();
        Assert.Equal("Slight chance of drizzle around 7 PM (30%).", WeatherText.NextPrecipitation(slight, Central));
    }

    [Fact]
    public void Glyphs_follow_day_and_night_and_fall_back_to_a_thermometer()
    {
        Assert.Equal("☀", WeatherText.Glyph("skc", true));
        Assert.Equal("🌙", WeatherText.Glyph("skc", false));
        Assert.Equal("☀", WeatherText.Glyph("skc", null));
        Assert.Equal("🌡", WeatherText.Glyph("mystery", true));
        Assert.Equal("🌡", WeatherText.Glyph(null, true));
    }

    [Fact]
    public void Small_formatters_match_the_dashboard()
    {
        Assert.Equal("—", WeatherText.Inches(null));
        Assert.Equal("0\"", WeatherText.Inches(0));
        Assert.Equal("trace", WeatherText.Inches(0.004));
        Assert.Equal("0.42\"", WeatherText.Inches(0.42));
        Assert.Equal("1.5\"", WeatherText.Inches(1.5, 1));

        Assert.Equal(" (muggy)", WeatherText.DewComfort(66));
        Assert.Equal(" (dry)", WeatherText.DewComfort(25));
        Assert.Equal("", WeatherText.DewComfort(50));

        Assert.Equal("calm", WeatherText.WindText(new WeatherCurrent { WindMph = 0 }));
        Assert.Equal("S 12 mph", WeatherText.WindText(new WeatherCurrent { WindMph = 12, WindDirection = "S" }));
        Assert.Null(WeatherText.WindText(new WeatherCurrent()));

        Assert.Equal("29.92 inHg ↓ falling", WeatherText.PressureText(new WeatherCurrent { PressureInHg = 29.92 }, new WeatherPressureTrend { Trend = "falling" }));
        Assert.Equal("29.92 inHg", WeatherText.PressureText(new WeatherCurrent { PressureInHg = 29.92 }, null));

        var sun = new WeatherSun
        {
            Sunrise = DateTimeOffset.Parse("2026-09-29T12:20:00Z", CultureInfo.InvariantCulture),
            Sunset = DateTimeOffset.Parse("2026-09-30T00:10:00Z", CultureInfo.InvariantCulture),
        };
        Assert.Equal("11h 50m", WeatherText.DaylightText(sun));
        Assert.Null(WeatherText.DaylightText(new WeatherSun()));

        Assert.Equal("2 of 5 · slight", WeatherText.OutlookScale("SLGT"));
        Assert.Equal("no severe storms expected", WeatherText.OutlookScale(null));
        Assert.Equal(3, WeatherText.OutlookLevel("ENH"));

        var location = new WeatherLocation { Label = "Home", City = "Omaha", State = "NE", Latitude = 41.25, Longitude = -96, ForecastOffice = "OAX" };
        Assert.Equal("near Omaha, NE · 41.2500, -96.0000 · forecast office OAX", WeatherText.PlaceMeta(location));
        Assert.Equal("Omaha, NE", new WeatherLocation { City = "Omaha", State = "NE" }.DisplayName);
    }

    [Fact]
    public void High_and_low_come_from_the_first_day_and_night()
    {
        var daily = new List<WeatherPeriod>
        {
            Period("Tonight", "2026-09-29T18:00:00-05:00", false, 61),
            Period("Wednesday", "2026-09-30T06:00:00-05:00", true, 70),
        };
        Assert.Equal("H 70°  L 61°", WeatherText.HighLow(daily));
    }

    [Fact]
    public void The_moon_phase_is_right_on_known_dates()
    {
        // A new moon: 2026-09-11 03:27 UTC. A full moon: 2026-09-26 16:49 UTC.
        var newMoon = MoonPhase.At(DateTimeOffset.Parse("2026-09-11T03:27:00Z", CultureInfo.InvariantCulture));
        Assert.Equal("New moon", newMoon.Name);
        Assert.True(newMoon.IlluminationPercent <= 2);

        var fullMoon = MoonPhase.At(DateTimeOffset.Parse("2026-09-26T16:49:00Z", CultureInfo.InvariantCulture));
        Assert.Equal("Full moon", fullMoon.Name);
        Assert.True(fullMoon.IlluminationPercent >= 98);
    }

    [Fact]
    public void Hour_tips_join_the_details_by_start_time()
    {
        var hour = new WeatherHour
        {
            StartTime = DateTimeOffset.Parse("2026-09-29T15:00:00-05:00", CultureInfo.InvariantCulture),
            ShortForecast = "Showers",
            WindDirection = "S",
            WindSpeed = "10 mph",
            HumidityPercent = 80,
        };
        var details = new WeatherDetails
        {
            Hours = new()
            {
                new WeatherHourDetail
                {
                    StartTime = DateTimeOffset.Parse("2026-09-29T20:00:00Z", CultureInfo.InvariantCulture),
                    GustMph = 25, SkyCoverPercent = 90, RainInches = 0.1,
                },
            },
        };

        var byTime = WeatherText.HourDetailsByTime(details);
        var extra = byTime.GetValueOrDefault(hour.StartTime.UtcTicks);

        Assert.NotNull(extra);
        Assert.Equal("Showers · wind S 10 mph · gusts 25 mph · humidity 80% · cloud 90% · rain 0.10\"", WeatherText.HourTip(hour, extra));
        Assert.Equal("10 S", WeatherText.HourWind(hour));
    }

    // ---------------------------------------------------------------- Storms

    private static WeatherStormCell Cell(string id, int threat, double miles, bool toward = false, int? eta = null) => new()
    {
        Id = id,
        Threat = threat,
        DistanceMiles = miles,
        BearingCompass = "SW",
        TowardHome = toward,
        EtaMinutes = eta,
        ClosestMiles = 0.5,
        HeadingCompass = "NE",
        SpeedMph = 30,
    };

    [Fact]
    public void Storms_near_home_put_incoming_and_dangerous_first()
    {
        var storms = new WeatherStorms
        {
            Cells = new()
            {
                Cell("near-weak", 0, 5),
                Cell("far", 4, 150),
                Cell("strong", 2, 40),
                Cell("incoming", 0, 60, toward: true, eta: 50),
            },
        };

        var list = WeatherText.StormsNearHome(storms);

        Assert.Equal(new[] { "incoming", "strong", "near-weak" }, list.Select(cell => cell.Id));
    }

    [Fact]
    public void The_banner_covers_warnings_over_home_and_storms_on_their_way()
    {
        Assert.Null(WeatherText.StormBanner(null));
        Assert.Null(WeatherText.StormBanner(new WeatherStorms { Cells = new() { Cell("a", 3, 10) } }));

        var storms = new WeatherStorms
        {
            Cells = new()
            {
                Cell("slow", 1, 30, toward: true, eta: 40),
                Cell("fast", 1, 20, toward: true, eta: 15),
            },
            Warnings = new()
            {
                new WeatherWarningArea { Phenomena = "SV", Name = "Severe Thunderstorm Warning", ContainsHome = true, HailInches = 1.25, WindMph = 60 },
                new WeatherWarningArea { Phenomena = "FL", Name = "Flood Warning", ContainsHome = true },
            },
        };

        var banner = WeatherText.StormBanner(storms)!;

        Assert.Equal("moderate", banner.Level);
        Assert.Equal("Severe Thunderstorm Warning", Assert.Single(banner.Covering).Name);
        Assert.Equal(new[] { "fast", "slow" }, banner.Incoming.Select(cell => cell.Id));
        Assert.Equal("Home is inside a Severe Thunderstorm Warning · hail 1.25\" · wind 60 mph", WeatherText.WarningLine(banner.Covering[0], Central));
        Assert.Equal("Strong storm heading this way · about 15 min out, passing overhead · now 20 mi SW", WeatherText.IncomingLine(banner.Incoming[0]));

        var tornado = new WeatherStorms
        {
            Warnings = new() { new WeatherWarningArea { Phenomena = "TO", Name = "Tornado Warning", ContainsHome = true } },
        };
        Assert.Equal("severe", WeatherText.StormBanner(tornado)!.Level);
    }

    [Fact]
    public void Cells_describe_themselves()
    {
        var tvs = new WeatherStormCell { Tornadic = true, Rotation = true, MesoStrength = "8", MaxHailInches = 1.75, Threat = 4 };
        Assert.Equal("Possible tornado", WeatherText.CellTitle(tvs));
        Assert.Equal("tornado vortex signature · mesocyclone (rank 8) · hail to 1.75\"", WeatherText.CellHazards(tvs));

        Assert.Equal("Severe hail storm", WeatherText.CellTitle(new WeatherStormCell { Threat = 2 }));
        Assert.Equal("40% hail", WeatherText.CellHazards(new WeatherStormCell { HailProbability = 40 }));

        var cell = Cell("x", 1, 12.4);
        Assert.Equal("12 mi SW · moving NE 30 mph", WeatherText.CellMeta(cell));
        Assert.Equal("12 mi SW · nearly stationary", WeatherText.CellMeta(new WeatherStormCell { DistanceMiles = 12.4, BearingCompass = "SW" }));
    }
}
