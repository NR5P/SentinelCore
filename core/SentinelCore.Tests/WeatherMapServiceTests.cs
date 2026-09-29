extern alias WebHost;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The radar map's server side: the storm-track arithmetic that decides "heading for the house",
// the storm-cell shaping (motion direction, duplicate sightings, weak circulations), the frame
// list read from the map server's capabilities, the tile proxy refusing anything it wasn't built
// for, and the forecast-grid sums behind the rain and snow totals. Fixtures are shaped like the
// real answers captured on 2026-09-29.
public sealed class WeatherMapServiceTests : IDisposable
{
    // A spot in southern New Mexico, with a storm 15 miles to its south-west heading north-east.
    private const double HomeLat = 32.75;
    private const double HomeLon = -107.6;

    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly Scripted _upstream = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 29, 15, 10, 0, TimeSpan.Zero));

    public WeatherMapServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"weather-map-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(_settingsPath, $$"""
            { "Location": { "Latitude": {{HomeLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "Longitude": {{HomeLon.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "Label": "Test Home" } }
            """);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WeatherMapService CreateService() =>
        new(new SingleHandlerFactory(_upstream), new AppSettingsFile(_settingsPath), NullLogger<WeatherMapService>.Instance, _clock);

    // ---- Track arithmetic ----

    [Fact]
    public void ClosestApproach_StormAimedAtHome_PassesOverheadAtTheRightTime()
    {
        // 15 miles due south, heading due north at 20 mph: overhead in 45 minutes.
        var (lat, lon) = WeatherMapService.Destination(HomeLat, HomeLon, 180, 15 * 1.609344);

        var (miles, minutes) = WeatherMapService.ClosestApproach(lat, lon, 0, 20, HomeLat, HomeLon, 90);

        Assert.InRange(miles, 0, 0.1);
        Assert.InRange(minutes, 44, 46);
    }

    [Fact]
    public void ClosestApproach_StormMovingAway_IsWhereItIsNow()
    {
        var (lat, lon) = WeatherMapService.Destination(HomeLat, HomeLon, 180, 15 * 1.609344);

        var (miles, minutes) = WeatherMapService.ClosestApproach(lat, lon, 180, 20, HomeLat, HomeLon, 90);

        Assert.Equal(0, minutes);
        Assert.InRange(miles, 14.9, 15.1);
    }

    [Fact]
    public void ShapeCells_ReadsMotionAsDirectionFrom_AndFlagsAStormHeadedHome()
    {
        var (lat, lon) = WeatherMapService.Destination(HomeLat, HomeLon, 225, 15 * 1.609344);
        var cell = Cell("EPZ", "W9", lat, lon, movingFrom: 225, knots: 17.4, meso: "NONE", maxHail: 0.5, poh: 60);

        var shaped = Assert.Single(WeatherMapService.ShapeCells([cell], BoundingBox.World, new SentinelCore.GeoCoordinate(HomeLat, HomeLon), _clock.GetUtcNow()));

        // "From the south-west" is heading north-east, straight at home.
        Assert.Equal(45, shaped.HeadingDegrees);
        Assert.Equal("NE", shaped.HeadingCompass);
        Assert.Equal(20, shaped.SpeedMph);
        Assert.Equal("SW", shaped.BearingCompass);
        Assert.True(shaped.TowardHome);
        Assert.InRange(shaped.EtaMinutes!.Value, 43, 47);
        Assert.InRange(shaped.ClosestMiles!.Value, 0, 0.5);
        Assert.Equal(4, shaped.Track.Count);
        Assert.Equal(60, shaped.Track[^1].Minutes);
        Assert.Equal(1, shaped.Threat);   // 60% hail odds, but not severe hail and no rotation
    }

    [Fact]
    public void ShapeCells_WeakCirculationIsNotRotation_ButRankFiveIs()
    {
        var weak = Cell("HDX", "G1", 33.19, -106.55, meso: "4");
        var strong = Cell("HDX", "W1", 32.90, -106.29, meso: "5");

        var shaped = WeatherMapService.ShapeCells([weak, strong], BoundingBox.World, null, _clock.GetUtcNow());

        var rotating = Assert.Single(shaped, c => c.Rotation);
        Assert.Equal("HDX-W1", rotating.Id);
        Assert.Equal(3, rotating.Threat);
        var notRotating = Assert.Single(shaped, c => !c.Rotation);
        Assert.Equal("4", notRotating.MesoStrength);   // still shown, as a weak circulation
        Assert.Equal(0, notRotating.Threat);
    }

    [Fact]
    public void ShapeCells_DropsTheFartherRadarsSightingOfTheSameStorm_AndStaleCells()
    {
        var near = Cell("EPZ", "A1", 32.60, -107.79, rangeNm: 40);
        var far = Cell("HDX", "B7", 32.61, -107.80, rangeNm: 90);   // ~1.5 km away: the same storm
        var stale = Cell("ABX", "C3", 34.0, -106.0, validAt: _clock.GetUtcNow() - TimeSpan.FromHours(1));
        var stationary = Cell("EPZ", "D4", 31.0, -108.0, movingFrom: 180, knots: 0);

        var shaped = WeatherMapService.ShapeCells([far, near, stale, stationary], BoundingBox.World, null, _clock.GetUtcNow());

        Assert.Equal(["EPZ-A1", "EPZ-D4"], shaped.Select(c => c.Id).OrderBy(id => id));
        var still = shaped.Single(c => c.Id == "EPZ-D4");
        Assert.Null(still.HeadingDegrees);   // a storm standing still has no heading
        Assert.Empty(still.Track);
    }

    [Fact]
    public void ShapeCells_KeepsOnlyWhatIsInView()
    {
        var inside = Cell("EPZ", "A1", 32.6, -107.8);
        var outside = Cell("OAX", "Z9", 41.5, -96.4);

        var shaped = WeatherMapService.ShapeCells([inside, outside], BoundingBox.From(-110, 31, -105, 35), null, _clock.GetUtcNow());

        Assert.Equal("EPZ-A1", Assert.Single(shaped).Id);
    }

    [Fact]
    public void PolygonsContain_RespectsHoles()
    {
        double[][] outer = [[-108, 32], [-107, 32], [-107, 33], [-108, 33], [-108, 32]];
        double[][] hole = [[-107.7, 32.7], [-107.5, 32.7], [-107.5, 32.8], [-107.7, 32.8], [-107.7, 32.7]];
        List<List<double[][]>> polygons = [[outer, hole]];

        Assert.True(WeatherMapService.PolygonsContain(polygons, 32.2, -107.9));
        Assert.False(WeatherMapService.PolygonsContain(polygons, HomeLat, HomeLon));   // in the hole
        Assert.False(WeatherMapService.PolygonsContain(polygons, 34, -107.5));
    }

    // ---- Frames ----

    [Fact]
    public void ParseCapabilityTimes_KeepsTheLoopWindow_ThinsToTwelve_AndEndsOnTheNewest()
    {
        var now = _clock.GetUtcNow();
        // Two-minute national frames going back three hours, plus one from the future.
        var times = Enumerable.Range(0, 90).Select(i => now - TimeSpan.FromMinutes(i * 2) - TimeSpan.FromSeconds(10)).Reverse().ToList();
        times.Add(now + TimeSpan.FromHours(2));
        var xml = Capabilities(("conus_bref_qcd", times));

        var parsed = WeatherMapService.ParseCapabilityTimes(xml, now);

        var frames = parsed["conus_bref_qcd"];
        Assert.Equal(WeatherMapService.MaximumLoopFrames, frames.Count);
        Assert.Equal(WeatherMapService.FormatFrameTime(now - TimeSpan.FromSeconds(10)), frames[^1]);
        Assert.All(frames, f => Assert.InRange(DateTimeOffset.Parse(f), now - WeatherMapService.LoopWindow, now));
        Assert.Equal(frames.OrderBy(f => f, StringComparer.Ordinal), frames);
    }

    [Fact]
    public void WmsTileUrl_AsksForExactlyTheTilesSquare()
    {
        var url = WeatherMapService.WmsTileUrl("klvx", "klvx_sr_bvel", 0, 0, 0, new DateTimeOffset(2026, 9, 29, 14, 20, 0, TimeSpan.Zero));

        Assert.StartsWith($"{WeatherMapService.OpengeoBaseUrl}/klvx/ows?", url);
        Assert.Contains("layers=klvx_sr_bvel", url);
        Assert.Contains("crs=EPSG:3857", url);
        Assert.Contains("bbox=-20037508.343,-20037508.343,20037508.343,20037508.343", url);
        Assert.EndsWith("&time=2026-09-29T14%3A20%3A00.000Z", url);
    }

    // ---- The proxy refuses what it wasn't built for, without touching the network ----

    [Theory]
    [InlineData("nope", 5, 1, 1, null, null)]                              // unknown layer
    [InlineData("reflectivity", 5, 32, 1, null, null)]                     // x off the edge of zoom 5
    [InlineData("reflectivity", 14, 1, 1, null, null)]                     // past the radar's zoom
    [InlineData("base", 18, 1, 1, null, null)]                             // past the basemap's zoom
    [InlineData("velocity", 5, 1, 1, null, null)]                          // per-radar layer, no radar
    [InlineData("velocity", 5, 1, 1, "../x", null)]                        // not a station id
    [InlineData("reflectivity", 5, 1, 1, null, "2020-01-01T00:00:00Z")]   // a frame from years ago
    [InlineData("reflectivity", 5, 1, 1, null, "yesterday-ish")]           // not a time
    public async Task GetTile_RefusesBadRequests(string layer, int z, int x, int y, string? station, string? time)
    {
        var service = CreateService();

        var tile = await service.GetTileAsync(layer, z, x, y, station, time, CancellationToken.None);

        Assert.Null(tile.Bytes);
        Assert.Equal(400, tile.Status);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task GetTile_FetchesOnce_ThenServesFromMemory()
    {
        _upstream.Respond("/klvx/ows", HttpStatusCode.OK, "PNG-bytes", "image/png");
        var service = CreateService();

        var first = await service.GetTileAsync("velocity", 7, 33, 49, "KLVX", "2026-09-29T14:20:00.000Z", CancellationToken.None);
        var second = await service.GetTileAsync("velocity", 7, 33, 49, "KLVX", "2026-09-29T14:20:00.000Z", CancellationToken.None);

        Assert.Equal(200, first.Status);
        Assert.Equal("PNG-bytes"u8.ToArray(), second.Bytes);
        var request = Assert.Single(_upstream.Requests);
        Assert.Contains("layers=klvx_sr_bvel", request.RequestUri!.ToString());
        Assert.Equal(WeatherService.UserAgent, request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetTile_AnXmlServiceExceptionIsNotCachedAsAPicture()
    {
        _upstream.Respond("/conus/ows", HttpStatusCode.OK, "<ServiceExceptionReport/>", "application/vnd.ogc.se_xml");
        var service = CreateService();

        var tile = await service.GetTileAsync("reflectivity", 5, 7, 12, null, null, CancellationToken.None);

        Assert.Equal(502, tile.Status);
        Assert.Null(tile.Bytes);
    }

    [Fact]
    public async Task GetTile_BasemapIsEsriInZYXOrder_AndJpegIsLabelledAsSuch()
    {
        _upstream.RespondBytes("World_Dark_Gray_Base", [0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg");
        var service = CreateService();

        var tile = await service.GetTileAsync("base", 7, 24, 51, null, null, CancellationToken.None);

        Assert.Equal("image/jpeg", tile.ContentType);
        Assert.EndsWith("/World_Dark_Gray_Base/MapServer/tile/7/51/24", Assert.Single(_upstream.Requests).RequestUri!.ToString());
    }

    // ---- Storms, warnings and the outlook ----

    [Fact]
    public async Task GetStorms_AnnotatesCellsAndWarningsRelativeToHome()
    {
        _upstream.Respond("nexrad_attr.geojson", HttpStatusCode.OK, Fixtures.StormCells);
        _upstream.Respond("sbw.geojson", HttpStatusCode.OK, Fixtures.Warnings);
        _upstream.Respond("day1otlk_cat", HttpStatusCode.OK, Fixtures.OutlookCategorical);
        var service = CreateService();

        var storms = await service.GetStormsAsync(-110, 31, -105, 35, CancellationToken.None);

        Assert.Empty(storms.Problems);
        var cell = Assert.Single(storms.Cells);
        Assert.Equal("EPZ-W9", cell.Id);
        Assert.True(cell.TowardHome);
        Assert.Equal(0.5, cell.MaxHailInches);

        var warning = Assert.Single(storms.Warnings);
        Assert.Equal("Severe Thunderstorm Warning", warning.Name);
        Assert.Equal(1.0, warning.HailInches);
        Assert.Equal(60, warning.WindMph);
        Assert.Equal("POSSIBLE", warning.TornadoTag);
        Assert.True(warning.ContainsHome);

        Assert.NotNull(storms.Outlook);
        Assert.Equal(2, storms.Outlook.Areas.Count);
    }

    [Fact]
    public async Task GetStorms_OneFeedDown_StillAnswersWithTheRest()
    {
        _upstream.Respond("nexrad_attr.geojson", HttpStatusCode.ServiceUnavailable, "down");
        _upstream.Respond("sbw.geojson", HttpStatusCode.OK, Fixtures.Warnings);
        _upstream.Respond("day1otlk_cat", HttpStatusCode.OK, Fixtures.OutlookCategorical);
        var service = CreateService();

        var storms = await service.GetStormsAsync(null, null, null, null, CancellationToken.None);

        Assert.Empty(storms.Cells);
        Assert.Single(storms.Warnings);
        Assert.Contains(storms.Problems, p => p.Contains("Storm tracking", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSevereOutlook_PicksTheHighestCategoryContainingHome_AndTheHazardOdds()
    {
        _upstream.Respond("day1otlk_cat", HttpStatusCode.OK, Fixtures.OutlookCategorical);
        _upstream.Respond("day1otlk_torn", HttpStatusCode.OK, Fixtures.OutlookTornado);
        _upstream.Respond("day2otlk_cat", HttpStatusCode.OK, """{ "features": [] }""");
        _upstream.Respond("day3otlk_cat", HttpStatusCode.OK, """{ "features": [] }""");
        _upstream.Respond("day1otlk_hail", HttpStatusCode.OK, """{ "features": [] }""");
        _upstream.Respond("day1otlk_wind", HttpStatusCode.OK, """{ "features": [] }""");
        var service = CreateService();

        var outlook = await service.GetSevereOutlookAsync(CancellationToken.None);

        Assert.NotNull(outlook);
        Assert.Equal(3, outlook.Days.Count);
        Assert.Equal("MRGL", outlook.Days[0].Category);   // inside both TSTM and MRGL: the higher wins
        Assert.Null(outlook.Days[1].Category);
        Assert.Equal(5, outlook.TornadoPercent);
        Assert.True(outlook.SignificantTornado);
        Assert.Equal(0, outlook.HailPercent);
    }

    // ---- Forecast-grid sums (WeatherService) ----

    [Fact]
    public void ParseValidTime_ReadsTheIsoDuration()
    {
        var span = WeatherService.ParseValidTime("2026-09-29T04:00:00+00:00/P2DT14H");

        Assert.NotNull(span);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 4, 0, 0, TimeSpan.Zero), span.Value.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero), span.Value.End);
        Assert.Null(WeatherService.ParseValidTime("2026-09-29T04:00:00+00:00"));
        Assert.Null(WeatherService.ParseValidTime("garbage/PT1H"));
    }

    [Fact]
    public void AmountBetween_ProratesAPeriodThatOnlyPartlyOverlaps()
    {
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        List<GridValue> rain =
        [
            new(start, start.AddHours(6), 12, "wmoUnit:mm"),                // 2 mm an hour
            new(start.AddHours(6), start.AddHours(12), 6, "wmoUnit:mm")
        ];

        // 09:00-15:00 misses the first three hours and catches three of them.
        Assert.Equal(6, WeatherService.AmountBetween(rain, start.AddHours(-3), start.AddHours(3)), 6);
        Assert.Equal(18, WeatherService.AmountBetween(rain, start, start.AddDays(1)), 6);
        Assert.Equal(1, WeatherService.ToInches(25.4, "wmoUnit:mm"), 6);
    }

    [Fact]
    public void PressureTrend_ScalesToThreeHours_AndNamesTheTendency()
    {
        var now = new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);
        // 1.5 hPa down in three hours is ~0.044 inHg: falling, not fast.
        var falling = WeatherService.PressureTrend([(now.AddHours(-3), 101500), (now.AddHours(-1), 101420), (now, 101350)]);
        Assert.Equal("falling", falling!.Trend);
        Assert.Equal(-0.04, falling.ChangeInHgPer3Hours);

        var fast = WeatherService.PressureTrend([(now.AddHours(-3), 101500), (now, 101200)]);
        Assert.Equal("falling fast", fast!.Trend);

        Assert.Null(WeatherService.PressureTrend([(now, 101350)]));
        Assert.Null(WeatherService.PressureTrend([(now.AddMinutes(-20), 101000), (now, 101350)]));   // too short a gap
    }

    // ---- Helpers ----

    private RawStormCell Cell(
        string radar, string id, double lat, double lon,
        double movingFrom = 225, double knots = 15, string meso = "NONE", double maxHail = 0, double poh = 0,
        double rangeNm = 50, DateTimeOffset? validAt = null) => new()
    {
        Radar = radar,
        Id = id,
        Latitude = lat,
        Longitude = lon,
        RangeNm = rangeNm,
        Tvs = "NONE",
        Meso = meso,
        HailProbability = poh,
        SevereHailProbability = 0,
        MaxHailInches = maxHail,
        MaxDbz = 50,
        TopKft = 20,
        MovingFromDegrees = movingFrom,
        SpeedKnots = knots,
        ValidAt = validAt ?? _clock.GetUtcNow() - TimeSpan.FromMinutes(5)
    };

    private static string Capabilities(params (string Layer, List<DateTimeOffset> Times)[] layers)
    {
        var body = string.Concat(layers.Select(l => $"""
            <Layer queryable="1" opaque="0">
              <Name>{l.Layer}</Name>
              <Title>{l.Layer}</Title>
              <Dimension name="time" default="x" units="ISO8601" nearestValue="1">{string.Join(",", l.Times.Select(WeatherMapService.FormatFrameTime))}</Dimension>
            </Layer>
            """));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <WMS_Capabilities version="1.3.0" xmlns="http://www.opengis.net/wms">
              <Capability><Layer><Title>root</Title>{body}</Layer></Capability>
            </WMS_Capabilities>
            """;
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Scripted : HttpMessageHandler
    {
        private readonly List<(string Fragment, Func<HttpResponseMessage> Respond)> _routes = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Respond(string fragment, HttpStatusCode status, string body, string contentType = "application/geo+json") =>
            _routes.Add((fragment, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) }));

        public void RespondBytes(string fragment, byte[] bytes, string contentType) =>
            _routes.Add((fragment, () =>
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            var url = request.RequestUri!.ToString();
            var route = _routes.LastOrDefault(r => url.Contains(r.Fragment, StringComparison.Ordinal));
            return Task.FromResult(route.Respond is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("unscripted: " + url) }
                : route.Respond());
        }
    }

    private static class Fixtures
    {
        // One storm 15 mi south-west of home moving from 234° at 17 kt, one far away in Nebraska.
        public const string StormCells = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature", "id": 0,
                  "properties": { "nexrad": "EPZ", "storm_id": "W9", "azimuth": 300, "range": 40, "tvs": "NONE", "meso": "NONE",
                                  "posh": 0, "poh": 60, "max_size": 0.5, "vil": 33, "max_dbz": 60, "max_dbz_height": 12.8, "top": 21.4,
                                  "drct": 234, "sknt": 17, "valid": "2026-09-29T15:02:19Z" },
                  "geometry": { "type": "Point", "coordinates": [-107.7918, 32.5988] } },
                { "type": "Feature", "id": 1,
                  "properties": { "nexrad": "OAX", "storm_id": "Z9", "azimuth": 10, "range": 30, "tvs": "NONE", "meso": "NONE",
                                  "posh": 0, "poh": 20, "max_size": 0.01, "vil": 20, "max_dbz": 59, "max_dbz_height": 10, "top": 30,
                                  "drct": 239, "sknt": 45, "valid": "2026-09-29T15:02:19Z" },
                  "geometry": { "type": "Point", "coordinates": [-96.3879, 41.5444] } }
              ]
            }
            """;

        public const string Warnings = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature", "id": "EPZ.SV.W.0042.2026-09-29T15:03:00Z",
                  "properties": { "phenomena": "SV", "significance": "W", "ps": "Severe Thunderstorm Warning", "wfo": "EPZ",
                                  "issue": "2026-09-29T15:03:00Z", "expire": "2026-09-29T15:30:00Z",
                                  "windtag": 60, "hailtag": 1.0, "tornadotag": "POSSIBLE", "damagetag": null,
                                  "is_emergency": false, "is_pds": false },
                  "geometry": { "type": "MultiPolygon", "coordinates": [[[[-107.9, 32.5], [-107.4, 32.5], [-107.4, 32.9], [-107.9, 32.9], [-107.9, 32.5]]]] } },
                { "type": "Feature", "id": "EPZ.FF.W.0007.2026-09-29T13:00:00Z",
                  "properties": { "phenomena": "FF", "significance": "W", "ps": "Flash Flood Warning", "wfo": "EPZ",
                                  "issue": "2026-09-29T13:00:00Z", "expire": "2026-09-29T15:00:00Z" },
                  "geometry": { "type": "Polygon", "coordinates": [[[-107.9, 32.5], [-107.4, 32.5], [-107.4, 32.9], [-107.9, 32.5]]] } }
              ]
            }
            """;

        public const string OutlookCategorical = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature",
                  "properties": { "DN": 2, "LABEL": "TSTM", "LABEL2": "General Thunderstorms Risk", "ISSUE_ISO": "2026-09-29T12:51:00+00:00", "fill": "#C1E9C1", "stroke": "#55BB55" },
                  "geometry": { "type": "Polygon", "coordinates": [[[-110, 30], [-100, 30], [-100, 36], [-110, 36], [-110, 30]]] } },
                { "type": "Feature",
                  "properties": { "DN": 3, "LABEL": "MRGL", "LABEL2": "Marginal Risk", "ISSUE_ISO": "2026-09-29T12:51:00+00:00", "fill": "#66A366", "stroke": "#005500" },
                  "geometry": { "type": "Polygon", "coordinates": [[[-108, 32], [-107, 32], [-107, 33], [-108, 33], [-108, 32]]] } }
              ]
            }
            """;

        public const string OutlookTornado = """
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature", "properties": { "DN": 2, "LABEL": "0.02", "LABEL2": "2% Tornado Risk" },
                  "geometry": { "type": "Polygon", "coordinates": [[[-110, 30], [-100, 30], [-100, 36], [-110, 36], [-110, 30]]] } },
                { "type": "Feature", "properties": { "DN": 5, "LABEL": "0.05", "LABEL2": "5% Tornado Risk" },
                  "geometry": { "type": "Polygon", "coordinates": [[[-108, 32], [-107, 32], [-107, 33], [-108, 33], [-108, 32]]] } },
                { "type": "Feature", "properties": { "DN": 10, "LABEL": "SIGN", "LABEL2": "Significant Tornado Risk" },
                  "geometry": { "type": "Polygon", "coordinates": [[[-108, 32], [-107, 32], [-107, 33], [-108, 33], [-108, 32]]] } }
              ]
            }
            """;
    }
}
