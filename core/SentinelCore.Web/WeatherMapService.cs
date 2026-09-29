using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SentinelCore.Web;

// The Weather tab's radar map: a zoomable, pannable map of the radar with the house marked on it,
// the storm cells the radars are tracking (with where each is headed and whether it is headed
// here), the warning polygons, and the Storm Prediction Center's severe outlook.
//
// Everything is proxied, like the rest of the tab, so the browser only ever talks to this server:
// - Radar pictures are map tiles cut on demand from the National Weather Service's own map server
//   (opengeo.ncep.noaa.gov, a WMS). The national mosaics (reflectivity, echo tops, precipitation
//   type) come from its "conus" workspace; each radar's own products (super-resolution
//   reflectivity, velocity, hydrometeor classification, rainfall totals) from a workspace named
//   after the radar. Both carry a time dimension, which is what makes the loop possible.
// - The map underneath is Esri's Dark Gray Canvas (free, no key), in its two halves - the land and
//   the place names - so the names can sit on top of the radar instead of under it. (CARTO's dark
//   basemap was the first choice; since 2026 it watermarks every tile served without an API key.)
// - Storm cells and storm-based warning polygons come from the Iowa Environmental Mesonet, which
//   republishes the radars' storm-tracking output and the NWS's warnings as small GeoJSON files.
// - The outlook is the SPC's own GeoJSON.
//
// Tiles are cached in memory under a byte budget, so ten minutes of a storm loop playing on three
// screens is one download per tile, not three. Everything else is cached for about as long as the
// product takes to change.
public sealed class WeatherMapService(
    IHttpClientFactory httpClientFactory,
    AppSettingsFile settings,
    ILogger<WeatherMapService> logger,
    TimeProvider? clock = null)
{
    public const string OpengeoBaseUrl = "https://opengeo.ncep.noaa.gov/geoserver";
    public const string BasemapBaseUrl = "https://server.arcgisonline.com/ArcGIS/rest/services/Canvas";
    public const string StormCellsUrl = "https://mesonet.agron.iastate.edu/geojson/nexrad_attr.geojson";
    public const string StormWarningsUrl = "https://mesonet.agron.iastate.edu/geojson/sbw.geojson";
    public const string SpcOutlookBaseUrl = "https://www.spc.noaa.gov/products/outlook";
    public const string RadarStationsUrl = WeatherService.ApiBaseUrl + "/radar/stations?stationType=WSR-88D";

    // Zoom limits. Radar pictures stop gaining detail past 12 (a super-resolution bin is 250 m),
    // so the map scales the zoom-12 tile up rather than asking for more; the basemap goes to
    // street level so a storm can be placed against the roads.
    public const int MaximumBaseZoom = 16;
    public const int MaximumRadarZoom = 12;

    // A requested frame time must be one of the recent ones. Anything older than this is refused,
    // which is also what stops a script walking the cache full of pictures from last year.
    public static readonly TimeSpan OldestFrame = TimeSpan.FromHours(6);

    // How much of the past the loop covers, and how many frames at most it plays.
    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(90);
    public const int MaximumLoopFrames = 12;

    public static readonly TimeSpan LatestTileTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FrameTileTtl = TimeSpan.FromHours(6);
    public static readonly TimeSpan BaseTileTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan LegendTtl = TimeSpan.FromDays(1);
    public static readonly TimeSpan CapabilitiesTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StormsTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan OutlookTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StationsTtl = TimeSpan.FromDays(7);

    // Tiles in memory, at most. A screen of radar at one zoom is ~20 tiles of 2-60 KB, times the
    // frames of a loop; this holds several loops at several zooms.
    public const long TileCacheBytes = 96L * 1024 * 1024;

    // Storm cells older than this are the radar having stopped tracking them, not storms.
    public static readonly TimeSpan StaleStormCell = TimeSpan.FromMinutes(30);

    // Two cells closer than this, seen by different radars, are one storm seen twice.
    private const double DuplicateCellKm = 6;

    // How far down its track a storm is followed when asking "is it coming here?", and how close
    // it has to pass to count.
    public const double TrackMinutes = 90;
    public const double TowardHomeMiles = 10;

    public const int MaximumStormCells = 600;

    private const double EarthRadiusKm = 6371.0088;
    private const double KmPerMile = 1.609344;

    // The layers the tab can ask for, by the id it uses. National ones live in the "conus"
    // workspace under their own names; per-radar ones are "<station>_<suffix>" in a workspace
    // named after the station.
    public static readonly IReadOnlyList<RadarLayer> Layers =
    [
        new("reflectivity", "Reflectivity", RadarLayerScope.National, "conus_bref_qcd",
            "Rain, snow and hail anywhere in the country - the lowest radar scan from every radar, stitched together. Brighter is heavier."),
        new("precip-type", "Precip type", RadarLayerScope.National, "conus_pcpn_typ",
            "What is falling: rain, snow, freezing rain or sleet, and hail cores. Blue-to-green is rain, blue-to-white snow, pinks freezing rain and sleet."),
        new("echo-tops", "Echo tops", RadarLayerScope.National, "conus_neet_v18",
            "How tall the storms are, in thousands of feet. Tops above 40 kft are strong thunderstorms; above 50 kft usually severe."),
        new("station-reflectivity", "Reflectivity (hi-res)", RadarLayerScope.Station, "sr_bref",
            "One radar's own super-resolution scan: the sharpest picture of a storm's shape - hooks, bows and hail cores show here first."),
        new("velocity", "Velocity", RadarLayerScope.Station, "sr_bvel",
            "Wind toward (green) and away from (red) the radar. Bright green right beside bright red is rotation - the signature forecasters look for in a tornado warning."),
        new("hydrometeor", "Hail / rain / snow", RadarLayerScope.Station, "bdhc",
            "The radar's dual-polarisation guess at what each echo is: rain, heavy rain, hail, graupel, snow, ice crystals, or ground clutter and birds."),
        new("rain-1h", "Rain last hour", RadarLayerScope.Station, "boha",
            "How much rain the radar estimates has fallen in the past hour."),
        new("rain-storm", "Storm total rain", RadarLayerScope.Station, "bdsa",
            "How much rain the radar estimates has fallen since this rain event began - a flooding guide.")
    ];

    private static readonly Regex StationPattern = new("^[A-Za-z]{4}$", RegexOptions.CultureInvariant);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TileCache _tiles = new(TileCacheBytes);
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inFlight = new();
    private readonly SemaphoreSlim _upstream = new(8, 8);

    // The small JSON products share one cache of their own.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (object Value, DateTimeOffset FetchedAt)> _cache = new();

    public static RadarLayer? FindLayer(string id) => Layers.FirstOrDefault(l => l.Id == id);

    // ---------------- Map description ----------------

    // What the tab needs to draw the map: where home is, which radar is nearest to it, every
    // radar to choose from, and the frame times each layer can play.
    public async Task<WeatherMapInfo> GetMapInfoAsync(string? station, CancellationToken ct)
    {
        var (options, home) = await ReadLocationAsync(ct);
        var stations = await GetStationsSafeAsync(ct);

        var homeStation = home is null ? null : stations
            .OrderBy(s => DistanceKm(home.Latitude, home.Longitude, s.Latitude, s.Longitude))
            .FirstOrDefault()?.Id;

        var chosen = station is not null && StationPattern.IsMatch(station) ? station.ToUpperInvariant() : homeStation;

        var times = new Dictionary<string, IReadOnlyList<string>>();
        var problems = new List<string>();
        try
        {
            foreach (var (layer, list) in await GetTimesAsync("conus", ct)) times[layer] = list;
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            problems.Add("The national radar frame list couldn't be read, so the national layers show only the latest picture.");
            logger.LogInformation(ex, "Couldn't read the national radar capabilities");
        }
        if (chosen is not null)
        {
            try
            {
                foreach (var (layer, list) in await GetTimesAsync(chosen.ToLowerInvariant(), ct)) times[layer] = list;
            }
            catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
            {
                problems.Add($"Radar {chosen}'s frame list couldn't be read, so its layers show only the latest picture.");
                logger.LogInformation(ex, "Couldn't read radar {Station}'s capabilities", chosen);
            }
        }

        return new WeatherMapInfo
        {
            Home = home is null ? null : new WeatherMapHome
            {
                Latitude = home.Latitude,
                Longitude = home.Longitude,
                Label = string.IsNullOrWhiteSpace(options.Label) ? "Home" : options.Label.Trim()
            },
            HomeStation = homeStation,
            Station = chosen,
            Stations = stations,
            Layers = Layers.Select(l => new WeatherMapLayer
            {
                Id = l.Id,
                Name = l.Name,
                Scope = l.Scope == RadarLayerScope.National ? "national" : "station",
                Description = l.Description,
                Times = times.TryGetValue(l.Scope == RadarLayerScope.National ? l.WmsName : $"{chosen?.ToLowerInvariant()}_{l.WmsName}", out var list) ? list : []
            }).ToList(),
            MaximumRadarZoom = MaximumRadarZoom,
            MaximumBaseZoom = MaximumBaseZoom,
            Problems = problems
        };
    }

    // Frame times per WMS layer in one workspace, trimmed to the loop window and thinned to at
    // most MaximumLoopFrames (the national mosaic updates every two minutes - forty-five frames
    // an hour is more than a loop needs).
    private async Task<Dictionary<string, IReadOnlyList<string>>> GetTimesAsync(string workspace, CancellationToken ct)
    {
        var url = $"{OpengeoBaseUrl}/{workspace}/ows?service=WMS&version=1.3.0&request=GetCapabilities";
        var xml = await CachedAsync($"caps:{workspace}", CapabilitiesTtl, c => FetchStringAsync(url, c), ct);
        return ParseCapabilityTimes(xml, _clock.GetUtcNow());
    }

    public static Dictionary<string, IReadOnlyList<string>> ParseCapabilityTimes(string xml, DateTimeOffset now)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var document = XDocument.Parse(xml);
        foreach (var layer in document.Descendants().Where(e => e.Name.LocalName == "Layer"))
        {
            var name = layer.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            var dimension = layer.Elements().FirstOrDefault(e => e.Name.LocalName == "Dimension" && (string?)e.Attribute("name") == "time");
            if (string.IsNullOrWhiteSpace(name) || dimension is null) continue;

            var times = dimension.Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null)
                .Where(t => t is not null && t.Value <= now + TimeSpan.FromMinutes(5) && now - t.Value <= LoopWindow)
                .Select(t => t!.Value.ToUniversalTime())
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            result[name] = Thin(times, MaximumLoopFrames).Select(FormatFrameTime).ToList();
        }
        return result;
    }

    // Keeps the newest frame and spaces the rest evenly back through the window.
    public static List<DateTimeOffset> Thin(List<DateTimeOffset> times, int maximum)
    {
        if (times.Count <= maximum) return times;
        var kept = new List<DateTimeOffset>();
        var step = (times.Count - 1) / (double)(maximum - 1);
        for (var i = 0; i < maximum; i++) kept.Add(times[(int)Math.Round(i * step)]);
        return kept.Distinct().ToList();
    }

    public static string FormatFrameTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'.'fff'Z'", CultureInfo.InvariantCulture);

    // ---------------- Tiles ----------------

    // One 256-pixel map tile for a layer: "base" and "labels" are the basemap halves, anything
    // else is a radar layer id. `time` is one of the frame times GetMapInfoAsync listed (or
    // absent for the latest picture).
    public async Task<ProxiedImage> GetTileAsync(string layer, int z, int x, int y, string? station, string? time, CancellationToken ct)
    {
        if (!TileInRange(z, x, y))
            return ProxiedImage.Failed(400, "That tile doesn't exist.");

        string url;
        TimeSpan ttl;
        if (layer is "base" or "labels")
        {
            if (z > MaximumBaseZoom) return ProxiedImage.Failed(400, "The map doesn't zoom in that far.");
            // Esri's tile paths are z/y/x, not z/x/y.
            var service = layer == "base" ? "World_Dark_Gray_Base" : "World_Dark_Gray_Reference";
            url = $"{BasemapBaseUrl}/{service}/MapServer/tile/{z}/{y}/{x}";
            ttl = BaseTileTtl;
        }
        else
        {
            var radar = FindLayer(layer);
            if (radar is null) return ProxiedImage.Failed(400, "Unknown radar layer.");
            if (z > MaximumRadarZoom) return ProxiedImage.Failed(400, "The radar doesn't zoom in that far.");

            var frame = ParseFrameTime(time);
            if (time is not null && frame is null) return ProxiedImage.Failed(400, "Unknown frame time.");

            var workspace = WorkspaceFor(radar, station);
            if (workspace is null) return ProxiedImage.Failed(400, "That layer needs a radar station, like KLVX.");

            url = WmsTileUrl(workspace, WmsLayerFor(radar, workspace), z, x, y, frame);
            ttl = frame is null ? LatestTileTtl : FrameTileTtl;
        }

        var cached = _tiles.Get(url, _clock.GetUtcNow());
        if (cached is not null) return new ProxiedImage(cached, 200, null);

        try
        {
            var bytes = await FetchOnceAsync(url, ct);
            _tiles.Put(url, bytes, _clock.GetUtcNow() + ttl);
            return new ProxiedImage(bytes, 200, null);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Weather map tile failed: {Url}", url);
            return ProxiedImage.Failed(502, "Couldn't fetch that part of the map.");
        }
    }

    // The colour key for a radar layer, straight from the map server (a 500x30 strip).
    public async Task<ProxiedImage> GetLegendAsync(string layer, string? station, CancellationToken ct)
    {
        var radar = FindLayer(layer);
        if (radar is null) return ProxiedImage.Failed(400, "Unknown radar layer.");
        var workspace = WorkspaceFor(radar, station);
        if (workspace is null) return ProxiedImage.Failed(400, "That layer needs a radar station, like KLVX.");

        // The legend is the same for every radar; key the cache on the product, not the station.
        var url = $"{OpengeoBaseUrl}/{workspace}/ows?service=WMS&version=1.3.0&request=GetLegendGraphic&format=image%2Fpng&width=500&height=30&layer={WmsLayerFor(radar, workspace)}";
        var key = $"legend:{radar.Id}";
        var cached = _tiles.Get(key, _clock.GetUtcNow());
        if (cached is not null) return new ProxiedImage(cached, 200, null);

        try
        {
            var bytes = await FetchOnceAsync(url, ct);
            _tiles.Put(key, bytes, _clock.GetUtcNow() + LegendTtl);
            return new ProxiedImage(bytes, 200, null);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Weather legend failed: {Url}", url);
            return ProxiedImage.Failed(502, "Couldn't fetch the legend.");
        }
    }

    public static bool TileInRange(int z, int x, int y) =>
        z is >= 0 and <= 20 && x >= 0 && y >= 0 && x < (1 << z) && y < (1 << z);

    private static string? WorkspaceFor(RadarLayer layer, string? station)
    {
        if (layer.Scope == RadarLayerScope.National) return "conus";
        return station is not null && StationPattern.IsMatch(station) ? station.ToLowerInvariant() : null;
    }

    private static string WmsLayerFor(RadarLayer layer, string workspace) =>
        layer.Scope == RadarLayerScope.National ? layer.WmsName : $"{workspace}_{layer.WmsName}";

    private DateTimeOffset? ParseFrameTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return null;
        var now = _clock.GetUtcNow();
        if (parsed > now + TimeSpan.FromMinutes(10) || now - parsed > OldestFrame) return null;
        return parsed;
    }

    // A WMS GetMap for exactly the square a slippy-map tile covers, in web-mercator metres.
    public static string WmsTileUrl(string workspace, string wmsLayer, int z, int x, int y, DateTimeOffset? time)
    {
        var (minX, minY, maxX, maxY) = TileBounds(z, x, y);
        var bbox = string.Join(",", new[] { minX, minY, maxX, maxY }.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
        var url = $"{OpengeoBaseUrl}/{workspace}/ows?service=WMS&version=1.3.0&request=GetMap&layers={wmsLayer}&styles=&format=image%2Fpng&transparent=true&crs=EPSG:3857&width=256&height=256&bbox={bbox}";
        return time is null ? url : $"{url}&time={Uri.EscapeDataString(FormatFrameTime(time.Value))}";
    }

    public static (double MinX, double MinY, double MaxX, double MaxY) TileBounds(int z, int x, int y)
    {
        const double half = 20037508.342789244;
        var size = 2 * half / (1 << z);
        return (-half + x * size, half - (y + 1) * size, -half + (x + 1) * size, half - y * size);
    }

    // One download per URL however many browsers ask at once, and at most eight downloads under
    // way at a time so a zoom-out across a loop doesn't open a hundred connections to the NWS.
    private async Task<byte[]> FetchOnceAsync(string url, CancellationToken ct)
    {
        var lazy = _inFlight.GetOrAdd(url, u => new Lazy<Task<byte[]>>(() => FetchLimitedAsync(u)));
        return await lazy.Value.WaitAsync(ct);
    }

    // Not tied to the first caller's cancellation: another browser may be waiting on the same
    // download, and a finished tile is worth caching anyway.
    private async Task<byte[]> FetchLimitedAsync(string url)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await _upstream.WaitAsync(timeout.Token);
        }
        catch
        {
            _inFlight.TryRemove(url, out _);
            throw;
        }
        try
        {
            using var response = await SendAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new WeatherUpstreamException($"the map server answered HTTP {(int)response.StatusCode}");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            // GeoServer reports a bad request as a 200 with an XML "service exception" - not a picture.
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new WeatherUpstreamException("the map server sent an error instead of a picture");
            return await response.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        finally
        {
            _upstream.Release();
            _inFlight.TryRemove(url, out _);
        }
    }

    // ---------------- Radar stations ----------------

    private async Task<IReadOnlyList<WeatherRadarSite>> GetStationsSafeAsync(CancellationToken ct)
    {
        try
        {
            return await CachedAsync("stations", StationsTtl, FetchStationsAsync, ct);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't list the NWS radar stations");
            return [];
        }
    }

    private async Task<IReadOnlyList<WeatherRadarSite>> FetchStationsAsync(CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await FetchStringAsync(RadarStationsUrl, ct));
        var sites = new List<WeatherRadarSite>();
        foreach (var feature in Features(document.RootElement))
        {
            if (!TryPoint(feature, out var lat, out var lon)) continue;
            var properties = feature.GetProperty("properties");
            var id = Str(properties, "id");
            if (id is null || !StationPattern.IsMatch(id)) continue;
            sites.Add(new WeatherRadarSite { Id = id.ToUpperInvariant(), Name = Str(properties, "name") ?? id, Latitude = lat, Longitude = lon });
        }
        return sites.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    // ---------------- Storms, warnings and the outlook ----------------

    // Everything the map draws on top of the radar for the area on screen: the storm cells
    // (annotated with distance, bearing and whether they're headed for home), the warning
    // polygons, and today's SPC outlook. Each part fails on its own - no storm feed is still a map.
    public async Task<WeatherStorms> GetStormsAsync(double? west, double? south, double? east, double? north, CancellationToken ct)
    {
        var (_, home) = await ReadLocationAsync(ct);
        var box = BoundingBox.From(west, south, east, north);
        var now = _clock.GetUtcNow();
        var problems = new List<string>();

        IReadOnlyList<WeatherStormCell> cells = [];
        try
        {
            var all = await CachedAsync("cells", StormsTtl, c => FetchStormCellsAsync(c), ct);
            cells = ShapeCells(all, box, home, now);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            problems.Add("Storm tracking is unavailable right now.");
            logger.LogInformation(ex, "Couldn't read the storm cells");
        }

        IReadOnlyList<WeatherWarningArea> warnings = [];
        try
        {
            var all = await CachedAsync("warnings", StormsTtl, c => FetchWarningsAsync(c), ct);
            warnings = all
                .Where(w => w.Expires is null || w.Expires > now)
                .Where(w => box.Intersects(w.Bounds))
                .Select(w => w with { ContainsHome = home is not null && w.Contains(home.Latitude, home.Longitude) })
                .ToList();
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            problems.Add("Warning outlines are unavailable right now.");
            logger.LogInformation(ex, "Couldn't read the storm-based warnings");
        }

        WeatherOutlookMap? outlook = null;
        try
        {
            outlook = await CachedAsync("outlook:day1otlk_cat", OutlookTtl, c => FetchOutlookAsync("day1otlk_cat", c), ct);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
        {
            problems.Add("The severe weather outlook is unavailable right now.");
            logger.LogInformation(ex, "Couldn't read the SPC outlook");
        }

        return new WeatherStorms { Cells = cells, Warnings = warnings, Outlook = outlook, FetchedAt = now, Problems = problems };
    }

    // The severe-weather outlook at home for the next three days, and today's odds of each
    // hazard, for the tab's outlook card. Null categories mean home is outside every outline.
    public async Task<WeatherSevereOutlook?> GetSevereOutlookAsync(CancellationToken ct)
    {
        var (_, home) = await ReadLocationAsync(ct);
        if (home is null) return null;

        async Task<WeatherOutlookMap?> Read(string product)
        {
            try { return await CachedAsync($"outlook:{product}", OutlookTtl, c => FetchOutlookAsync(product, c), ct); }
            catch (Exception ex) when (IsUpstreamFailure(ex) && !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Couldn't read SPC product {Product}", product);
                return null;
            }
        }

        var day1 = await Read("day1otlk_cat");
        if (day1 is null) return null;
        var day2 = await Read("day2otlk_cat");
        var day3 = await Read("day3otlk_cat");
        var tornado = await Read("day1otlk_torn");
        var hail = await Read("day1otlk_hail");
        var wind = await Read("day1otlk_wind");

        return new WeatherSevereOutlook
        {
            Days =
            [
                OutlookDay(1, day1, home),
                .. new[] { (2, day2), (3, day3) }.Where(d => d.Item2 is not null).Select(d => OutlookDay(d.Item1, d.Item2!, home))
            ],
            TornadoPercent = HighestPercent(tornado, home),
            HailPercent = HighestPercent(hail, home),
            WindPercent = HighestPercent(wind, home),
            SignificantTornado = Significant(tornado, home),
            SignificantHail = Significant(hail, home),
            SignificantWind = Significant(wind, home),
            IssuedAt = day1.IssuedAt
        };
    }

    private static WeatherOutlookDay OutlookDay(int day, WeatherOutlookMap map, GeoCoordinate home)
    {
        var area = map.Areas
            .Where(a => a.Contains(home.Latitude, home.Longitude))
            .OrderByDescending(a => a.Rank)
            .FirstOrDefault();
        return new WeatherOutlookDay
        {
            Day = day,
            Category = area?.Label,
            Name = area?.Name ?? "No thunderstorms expected",
            Rank = area?.Rank ?? 0,
            Color = area?.Fill
        };
    }

    private static int? HighestPercent(WeatherOutlookMap? map, GeoCoordinate home)
    {
        if (map is null) return null;
        var percents = map.Areas
            .Where(a => a.Contains(home.Latitude, home.Longitude))
            .Select(a => double.TryParse(a.Label, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) ? (int?)Math.Round(fraction * 100) : null)
            .Where(p => p is not null)
            .ToList();
        return percents.Count == 0 ? 0 : percents.Max();
    }

    // "SIGN" areas are the hatched ones - the SPC expecting the significant kind (EF2+ tornadoes,
    // 2-inch hail, 75 mph gusts).
    private static bool Significant(WeatherOutlookMap? map, GeoCoordinate home) =>
        map is not null && map.Areas.Any(a => a.Label == "SIGN" && a.Contains(home.Latitude, home.Longitude));

    private async Task<List<RawStormCell>> FetchStormCellsAsync(CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await FetchStringAsync(StormCellsUrl, ct));
        var cells = new List<RawStormCell>();
        foreach (var feature in Features(document.RootElement))
        {
            if (!TryPoint(feature, out var lat, out var lon)) continue;
            var p = feature.GetProperty("properties");
            cells.Add(new RawStormCell
            {
                Radar = Str(p, "nexrad") ?? "",
                Id = Str(p, "storm_id") ?? "",
                Latitude = lat,
                Longitude = lon,
                RangeNm = Num(p, "range"),
                Tvs = Str(p, "tvs"),
                Meso = Str(p, "meso"),
                HailProbability = Num(p, "poh"),
                SevereHailProbability = Num(p, "posh"),
                MaxHailInches = Num(p, "max_size"),
                Vil = Num(p, "vil"),
                MaxDbz = Num(p, "max_dbz"),
                MaxDbzHeightKft = Num(p, "max_dbz_height"),
                TopKft = Num(p, "top"),
                MovingFromDegrees = Num(p, "drct"),
                SpeedKnots = Num(p, "sknt"),
                ValidAt = Time(p, "valid")
            });
        }
        return cells;
    }

    public static IReadOnlyList<WeatherStormCell> ShapeCells(IReadOnlyList<RawStormCell> all, BoundingBox box, GeoCoordinate? home, DateTimeOffset now)
    {
        var inView = all
            .Where(c => c.ValidAt is null || now - c.ValidAt.Value <= StaleStormCell)
            .Where(c => box.Contains(c.Latitude, c.Longitude))
            .OrderBy(c => c.RangeNm ?? double.MaxValue)
            .ToList();

        // A storm between two radars is tracked by both; keep the sighting from the nearer radar,
        // whose beam is lower and whose numbers are better.
        var kept = new List<RawStormCell>();
        foreach (var cell in inView)
        {
            if (kept.Any(k => k.Radar != cell.Radar && DistanceKm(k.Latitude, k.Longitude, cell.Latitude, cell.Longitude) < DuplicateCellKm))
                continue;
            kept.Add(cell);
        }

        return kept
            .Select(c => Shape(c, home))
            .OrderByDescending(c => c.Threat)
            .ThenBy(c => c.DistanceMiles ?? double.MaxValue)
            .Take(MaximumStormCells)
            .ToList();
    }

    private static WeatherStormCell Shape(RawStormCell c, GeoCoordinate? home)
    {
        var tvs = !string.IsNullOrWhiteSpace(c.Tvs) && !c.Tvs.Equals("NONE", StringComparison.OrdinalIgnoreCase);
        var circulation = !string.IsNullOrWhiteSpace(c.Meso) && !c.Meso.Equals("NONE", StringComparison.OrdinalIgnoreCase);
        var meso = circulation && IsMesocyclone(c.Meso!);

        // The storm-tracking product reports motion the meteorological way - the direction the
        // storm is coming FROM - so it is heading the opposite way. A storm standing still has no
        // heading worth showing.
        var speedMph = c.SpeedKnots is null ? (double?)null : c.SpeedKnots.Value * 1.150779;
        double? heading = c.MovingFromDegrees is null || speedMph is not > 0 ? null : (c.MovingFromDegrees.Value + 180) % 360;

        var threat = tvs ? 4
            : meso ? 3
            : (c.MaxHailInches ?? 0) >= 1 || (c.SevereHailProbability ?? 0) >= 50 ? 2
            : (c.HailProbability ?? 0) >= 50 || (c.MaxDbz ?? 0) >= 55 ? 1
            : 0;

        var track = new List<WeatherTrackPoint>();
        if (heading is not null && speedMph is > 0)
        {
            foreach (var minutes in new[] { 15, 30, 45, 60 })
            {
                var (lat, lon) = Destination(c.Latitude, c.Longitude, heading.Value, speedMph.Value * KmPerMile * minutes / 60);
                track.Add(new WeatherTrackPoint { Minutes = minutes, Latitude = Math.Round(lat, 4), Longitude = Math.Round(lon, 4) });
            }
        }

        double? distance = null, bearing = null, closest = null, eta = null;
        var towardHome = false;
        if (home is not null)
        {
            distance = DistanceKm(home.Latitude, home.Longitude, c.Latitude, c.Longitude) / KmPerMile;
            bearing = Bearing(home.Latitude, home.Longitude, c.Latitude, c.Longitude);
            if (heading is not null && speedMph is > 0)
            {
                var approach = ClosestApproach(c.Latitude, c.Longitude, heading.Value, speedMph.Value, home.Latitude, home.Longitude, TrackMinutes);
                closest = approach.Miles;
                eta = approach.Minutes;
                towardHome = approach.Minutes > 0 && approach.Miles <= TowardHomeMiles && approach.Miles < distance - 1;
            }
        }

        return new WeatherStormCell
        {
            Id = $"{c.Radar}-{c.Id}",
            Radar = c.Radar,
            StormId = c.Id,
            Latitude = Math.Round(c.Latitude, 4),
            Longitude = Math.Round(c.Longitude, 4),
            Threat = threat,
            Tornadic = tvs,
            Rotation = meso,
            MesoStrength = circulation ? c.Meso : null,
            HailProbability = RoundInt(c.HailProbability),
            SevereHailProbability = RoundInt(c.SevereHailProbability),
            MaxHailInches = c.MaxHailInches is > 0 ? Math.Round(c.MaxHailInches.Value, 2) : null,
            Vil = RoundInt(c.Vil),
            MaxDbz = RoundInt(c.MaxDbz),
            TopKft = c.TopKft is null ? null : Math.Round(c.TopKft.Value, 1),
            HeadingDegrees = heading is null ? null : (int)Math.Round(heading.Value),
            HeadingCompass = WeatherService.Compass(heading),
            SpeedMph = RoundInt(speedMph),
            Track = track,
            DistanceMiles = distance is null ? null : Math.Round(distance.Value, 1),
            BearingCompass = WeatherService.Compass(bearing),
            TowardHome = towardHome,
            ClosestMiles = closest is null ? null : Math.Round(closest.Value, 1),
            EtaMinutes = towardHome && eta is not null ? (int)Math.Round(eta.Value) : null,
            ValidAt = c.ValidAt
        };
    }

    // The mesocyclone detection algorithm ranks every circulation it finds from 1 to 25; ranks 1-4
    // are weak, shallow spins that radars flag constantly, and 5 and up is what forecasters treat
    // as a mesocyclone - the rotating updraft tornadoes come from. Older feeds said "MESO"/"UNCO".
    public static bool IsMesocyclone(string meso) =>
        int.TryParse(meso.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank)
            ? rank >= 5
            : meso.Trim().Equals("MESO", StringComparison.OrdinalIgnoreCase);

    // Where along a straight track the storm passes nearest home, within the first `limitMinutes`,
    // worked in a flat local projection around home (good to a fraction of a mile at these ranges).
    public static (double Miles, double Minutes) ClosestApproach(
        double lat, double lon, double headingDegrees, double speedMph,
        double homeLat, double homeLon, double limitMinutes)
    {
        var kmPerDegLat = Math.PI * EarthRadiusKm / 180;
        var kmPerDegLon = kmPerDegLat * Math.Cos(homeLat * Math.PI / 180);
        var px = (lon - homeLon) * kmPerDegLon;
        var py = (lat - homeLat) * kmPerDegLat;
        var theta = headingDegrees * Math.PI / 180;
        var speedKmPerMin = speedMph * KmPerMile / 60;
        var vx = Math.Sin(theta) * speedKmPerMin;
        var vy = Math.Cos(theta) * speedKmPerMin;

        var v2 = vx * vx + vy * vy;
        var t = v2 == 0 ? 0 : -(px * vx + py * vy) / v2;
        t = Math.Clamp(t, 0, limitMinutes);
        var dx = px + vx * t;
        var dy = py + vy * t;
        return (Math.Sqrt(dx * dx + dy * dy) / KmPerMile, t);
    }

    private async Task<List<WeatherWarningArea>> FetchWarningsAsync(CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await FetchStringAsync(StormWarningsUrl, ct));
        var warnings = new List<WeatherWarningArea>();
        foreach (var feature in Features(document.RootElement))
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object) continue;
            var polygons = Polygons(geometry);
            if (polygons.Count == 0) continue;
            var p = feature.GetProperty("properties");
            var phenomena = Str(p, "phenomena") ?? "";
            var significance = Str(p, "significance") ?? "";
            warnings.Add(new WeatherWarningArea
            {
                Id = Str(feature, "id") ?? Guid.NewGuid().ToString("N"),
                Phenomena = phenomena,
                Significance = significance,
                Name = Str(p, "ps") ?? $"{phenomena}.{significance}",
                Office = Str(p, "wfo"),
                Issued = Time(p, "issue"),
                Expires = Time(p, "expire") ?? Time(p, "polygon_end"),
                HailInches = Num(p, "hailtag") ?? Num(p, "max_hailtag"),
                WindMph = Num(p, "windtag") ?? Num(p, "max_windtag"),
                TornadoTag = Str(p, "tornadotag"),
                DamageTag = Str(p, "damagetag") ?? Str(p, "floodtag_damage"),
                Emergency = Bool(p, "is_emergency"),
                ParticularlyDangerous = Bool(p, "is_pds"),
                Polygons = polygons
            });
        }
        return warnings;
    }

    private async Task<WeatherOutlookMap> FetchOutlookAsync(string product, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await FetchStringAsync($"{SpcOutlookBaseUrl}/{product}.nolyr.geojson", ct));
        var areas = new List<WeatherOutlookArea>();
        DateTimeOffset? issued = null;
        foreach (var feature in Features(document.RootElement))
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object) continue;
            var polygons = Polygons(geometry);
            if (polygons.Count == 0) continue;
            var p = feature.GetProperty("properties");
            issued ??= Time(p, "ISSUE_ISO");
            areas.Add(new WeatherOutlookArea
            {
                Label = Str(p, "LABEL") ?? "",
                Name = Str(p, "LABEL2") ?? "",
                Rank = (int)(Num(p, "DN") ?? 0),
                Fill = Str(p, "fill"),
                Stroke = Str(p, "stroke"),
                Polygons = polygons
            });
        }
        return new WeatherOutlookMap { Areas = areas, IssuedAt = issued };
    }

    // ---------------- Plumbing ----------------

    private async Task<(LocationOptions Options, GeoCoordinate? Coordinate)> ReadLocationAsync(CancellationToken ct)
    {
        var options = await settings.GetSectionAsync<LocationOptions>(LocationOptions.SectionName, ct) ?? new LocationOptions();
        var coordinate = new GeoCoordinate(options.Latitude, options.Longitude);
        return (options, coordinate.IsValid ? coordinate : null);
    }

    // Fresh-enough cached value, or a fetch; a failed fetch falls back to the stale value if
    // there is one, and throws if there isn't.
    private async Task<T> CachedAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> fetch, CancellationToken ct) where T : class
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            if (_cache.TryGetValue(key, out var entry) && now - entry.FetchedAt < ttl) return (T)entry.Value;
            try
            {
                var value = await fetch(ct);
                _cache[key] = (value, now);
                return value;
            }
            catch (Exception ex) when (IsUpstreamFailure(ex) && entry.Value is T stale && !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Weather map fetch failed for {Key}; serving the last answer", key);
                return stale;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> FetchStringAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new WeatherUpstreamException($"{new Uri(url).Host} answered HTTP {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(nameof(WeatherMapService));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", WeatherService.UserAgent);
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    private static bool IsUpstreamFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException
            or System.Xml.XmlException or WeatherUpstreamException or InvalidOperationException or KeyNotFoundException;

    private static IEnumerable<JsonElement> Features(JsonElement root) =>
        root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array ? features.EnumerateArray() : [];

    private static bool TryPoint(JsonElement feature, out double lat, out double lon)
    {
        lat = lon = 0;
        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object) return false;
        if (Str(geometry, "type") != "Point" || !geometry.TryGetProperty("coordinates", out var c) || c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2) return false;
        if (c[0].ValueKind != JsonValueKind.Number || c[1].ValueKind != JsonValueKind.Number) return false;
        lon = c[0].GetDouble();
        lat = c[1].GetDouble();
        return true;
    }

    // Polygon and MultiPolygon to a list of polygons, each a list of rings ([lon, lat] pairs,
    // rounded - four decimals is ten metres, and it halves what goes to the browser).
    public static List<List<double[][]>> Polygons(JsonElement geometry)
    {
        var result = new List<List<double[][]>>();
        if (!geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array) return result;

        List<double[][]> ReadPolygon(JsonElement polygon)
        {
            var rings = new List<double[][]>();
            foreach (var ring in polygon.EnumerateArray())
            {
                if (ring.ValueKind != JsonValueKind.Array) continue;
                var points = ring.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
                    .Select(p => new[] { Math.Round(p[0].GetDouble(), 4), Math.Round(p[1].GetDouble(), 4) })
                    .ToArray();
                if (points.Length >= 3) rings.Add(points);
            }
            return rings;
        }

        switch (Str(geometry, "type"))
        {
            case "Polygon":
                var single = ReadPolygon(coordinates);
                if (single.Count > 0) result.Add(single);
                break;
            case "MultiPolygon":
                foreach (var polygon in coordinates.EnumerateArray())
                {
                    if (polygon.ValueKind != JsonValueKind.Array) continue;
                    var rings = ReadPolygon(polygon);
                    if (rings.Count > 0) result.Add(rings);
                }
                break;
        }
        return result;
    }

    // Inside the outer ring and outside every hole, by ray casting.
    public static bool PolygonsContain(IReadOnlyList<List<double[][]>> polygons, double lat, double lon)
    {
        foreach (var rings in polygons)
        {
            if (rings.Count == 0 || !RingContains(rings[0], lat, lon)) continue;
            if (rings.Skip(1).Any(hole => RingContains(hole, lat, lon))) continue;
            return true;
        }
        return false;
    }

    private static bool RingContains(double[][] ring, double lat, double lon)
    {
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            double xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // The compass bearing from the first point to the second, in degrees.
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var φ1 = lat1 * Math.PI / 180;
        var φ2 = lat2 * Math.PI / 180;
        var Δλ = (lon2 - lon1) * Math.PI / 180;
        var y = Math.Sin(Δλ) * Math.Cos(φ2);
        var x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    public static (double Lat, double Lon) Destination(double lat, double lon, double bearingDegrees, double km)
    {
        var δ = km / EarthRadiusKm;
        var θ = bearingDegrees * Math.PI / 180;
        var φ1 = lat * Math.PI / 180;
        var λ1 = lon * Math.PI / 180;
        var φ2 = Math.Asin(Math.Sin(φ1) * Math.Cos(δ) + Math.Cos(φ1) * Math.Sin(δ) * Math.Cos(θ));
        var λ2 = λ1 + Math.Atan2(Math.Sin(θ) * Math.Sin(δ) * Math.Cos(φ1), Math.Cos(δ) - Math.Sin(φ1) * Math.Sin(φ2));
        return (φ2 * 180 / Math.PI, (λ2 * 180 / Math.PI + 540) % 360 - 180);
    }

    private static int? RoundInt(double? value) => value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        // A few IEM fields ("meso" strength, some tags) arrive as numbers in strings.
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement element, string name)
    {
        var text = Str(element, name);
        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    // A byte-budgeted least-recently-used cache of tiles, each with its own expiry.
    private sealed class TileCache(long capacityBytes)
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes, DateTimeOffset Expires)>> _map = new();
        private readonly LinkedList<(string Key, byte[] Bytes, DateTimeOffset Expires)> _order = new();
        private long _bytes;

        public byte[]? Get(string key, DateTimeOffset now)
        {
            lock (_lock)
            {
                if (!_map.TryGetValue(key, out var node)) return null;
                if (node.Value.Expires <= now)
                {
                    Remove(node);
                    return null;
                }
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Bytes;
            }
        }

        public void Put(string key, byte[] bytes, DateTimeOffset expires)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing)) Remove(existing);
                var node = _order.AddFirst((key, bytes, expires));
                _map[key] = node;
                _bytes += bytes.Length;
                while (_bytes > capacityBytes && _order.Last is { } last) Remove(last);
            }
        }

        private void Remove(LinkedListNode<(string Key, byte[] Bytes, DateTimeOffset Expires)> node)
        {
            _order.Remove(node);
            _map.Remove(node.Value.Key);
            _bytes -= node.Value.Bytes.Length;
        }
    }
}

public enum RadarLayerScope { National, Station }

public sealed record RadarLayer(string Id, string Name, RadarLayerScope Scope, string WmsName, string Description);

public sealed record ProxiedImage(byte[]? Bytes, int Status, string? Problem)
{
    // The basemap's land tiles are JPEG; everything else is PNG. Told apart by the file's magic
    // number rather than remembered alongside it in the cache.
    public string ContentType => Bytes is [0xFF, 0xD8, ..] ? "image/jpeg" : "image/png";

    public static ProxiedImage Failed(int status, string problem) => new(null, status, problem);
}

// The visible part of the map, in degrees. Absent or nonsense means "everywhere".
public readonly record struct BoundingBox(double West, double South, double East, double North)
{
    public static readonly BoundingBox World = new(-180, -90, 180, 90);

    public static BoundingBox From(double? west, double? south, double? east, double? north)
    {
        if (west is null || south is null || east is null || north is null) return World;
        if (!double.IsFinite(west.Value) || !double.IsFinite(south.Value) || !double.IsFinite(east.Value) || !double.IsFinite(north.Value)) return World;
        if (south > north || west > east) return World;
        return new BoundingBox(Math.Max(-180, west.Value), Math.Max(-90, south.Value), Math.Min(180, east.Value), Math.Min(90, north.Value));
    }

    public bool Contains(double lat, double lon) => lat >= South && lat <= North && lon >= West && lon <= East;

    public bool Intersects(BoundingBox other) =>
        other.West <= East && other.East >= West && other.South <= North && other.North >= South;
}

// ---- What the tab receives. ----

public sealed class WeatherMapInfo
{
    public WeatherMapHome? Home { get; init; }
    public string? HomeStation { get; init; }
    public string? Station { get; init; }
    public IReadOnlyList<WeatherRadarSite> Stations { get; init; } = [];
    public IReadOnlyList<WeatherMapLayer> Layers { get; init; } = [];
    public int MaximumRadarZoom { get; init; }
    public int MaximumBaseZoom { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public sealed class WeatherMapHome
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Label { get; init; } = "";
}

public sealed class WeatherRadarSite
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

public sealed class WeatherMapLayer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Scope { get; init; } = "national";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Times { get; init; } = [];
}

public sealed class WeatherStorms
{
    public IReadOnlyList<WeatherStormCell> Cells { get; init; } = [];
    public IReadOnlyList<WeatherWarningArea> Warnings { get; init; } = [];
    public WeatherOutlookMap? Outlook { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public sealed class RawStormCell
{
    public string Radar { get; init; } = "";
    public string Id { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double? RangeNm { get; init; }
    public string? Tvs { get; init; }
    public string? Meso { get; init; }
    public double? HailProbability { get; init; }
    public double? SevereHailProbability { get; init; }
    public double? MaxHailInches { get; init; }
    public double? Vil { get; init; }
    public double? MaxDbz { get; init; }
    public double? MaxDbzHeightKft { get; init; }
    public double? TopKft { get; init; }
    public double? MovingFromDegrees { get; init; }
    public double? SpeedKnots { get; init; }
    public DateTimeOffset? ValidAt { get; init; }
}

public sealed class WeatherStormCell
{
    public string Id { get; init; } = "";
    public string Radar { get; init; } = "";
    public string StormId { get; init; } = "";
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    // 0 ordinary, 1 strong, 2 severe hail, 3 rotating, 4 tornado vortex signature.
    public int Threat { get; init; }
    public bool Tornadic { get; init; }
    public bool Rotation { get; init; }
    public string? MesoStrength { get; init; }
    public int? HailProbability { get; init; }
    public int? SevereHailProbability { get; init; }
    public double? MaxHailInches { get; init; }
    public int? Vil { get; init; }
    public int? MaxDbz { get; init; }
    public double? TopKft { get; init; }
    public int? HeadingDegrees { get; init; }
    public string? HeadingCompass { get; init; }
    public int? SpeedMph { get; init; }
    public IReadOnlyList<WeatherTrackPoint> Track { get; init; } = [];
    public double? DistanceMiles { get; init; }
    public string? BearingCompass { get; init; }
    public bool TowardHome { get; init; }
    public double? ClosestMiles { get; init; }
    public int? EtaMinutes { get; init; }
    public DateTimeOffset? ValidAt { get; init; }
}

public sealed class WeatherTrackPoint
{
    public int Minutes { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

public sealed record WeatherWarningArea
{
    public string Id { get; init; } = "";
    public string Phenomena { get; init; } = "";
    public string Significance { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Office { get; init; }
    public DateTimeOffset? Issued { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public double? HailInches { get; init; }
    public double? WindMph { get; init; }
    public string? TornadoTag { get; init; }
    public string? DamageTag { get; init; }
    public bool Emergency { get; init; }
    public bool ParticularlyDangerous { get; init; }
    public bool ContainsHome { get; init; }

    // Polygons, each a list of rings of [lon, lat] - GeoJSON order.
    public IReadOnlyList<List<double[][]>> Polygons { get; init; } = [];

    internal BoundingBox Bounds
    {
        get
        {
            var points = Polygons.SelectMany(p => p).SelectMany(r => r).ToList();
            return points.Count == 0
                ? default
                : new BoundingBox(points.Min(p => p[0]), points.Min(p => p[1]), points.Max(p => p[0]), points.Max(p => p[1]));
        }
    }

    internal bool Contains(double lat, double lon) => WeatherMapService.PolygonsContain(Polygons, lat, lon);
}

public sealed class WeatherOutlookMap
{
    public IReadOnlyList<WeatherOutlookArea> Areas { get; init; } = [];
    public DateTimeOffset? IssuedAt { get; init; }
}

public sealed class WeatherOutlookArea
{
    public string Label { get; init; } = "";
    public string Name { get; init; } = "";
    public int Rank { get; init; }
    public string? Fill { get; init; }
    public string? Stroke { get; init; }
    public IReadOnlyList<List<double[][]>> Polygons { get; init; } = [];

    internal bool Contains(double lat, double lon) => WeatherMapService.PolygonsContain(Polygons, lat, lon);
}

public sealed class WeatherSevereOutlook
{
    public IReadOnlyList<WeatherOutlookDay> Days { get; init; } = [];
    public int? TornadoPercent { get; init; }
    public int? HailPercent { get; init; }
    public int? WindPercent { get; init; }
    public bool SignificantTornado { get; init; }
    public bool SignificantHail { get; init; }
    public bool SignificantWind { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
}

public sealed class WeatherOutlookDay
{
    public int Day { get; init; }
    public string? Category { get; init; }
    public string Name { get; init; } = "";
    public int Rank { get; init; }
    public string? Color { get; init; }
}
