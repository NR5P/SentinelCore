namespace SentinelCore.Web;

// The Weather tab's endpoints. All need a login like the rest of /api, and neither needs an
// admin: the weather here is something everyone in the house can see, the same as the cameras.
public static class WeatherEndpoints
{
    public static void MapWeatherEndpoints(this WebApplication app)
    {
        // Always 200 with a report - "no location set", "outside the NWS's coverage" and "couldn't
        // reach weather.gov" are states the tab explains, not errors in this server.
        app.MapGet("/api/weather", async (WeatherService weather, CancellationToken ct) =>
            Results.Ok(await weather.GetReportAsync(ct)))
        .RequireAuthorization();

        // The radar picture. Proxied rather than linked so the browser only ever talks to this
        // server (one origin, one login, and no mixed-content trouble on an HTTPS dashboard), and
        // so every open dashboard shares one download per two minutes.
        app.MapGet("/api/weather/radar", async (
            string? scope,
            string? kind,
            WeatherService weather,
            HttpContext http,
            CancellationToken ct) =>
        {
            var image = await weather.GetRadarAsync(scope ?? "local", kind ?? "loop", ct);
            if (image.Bytes is null)
                return Results.Json(new { error = image.Problem }, statusCode: image.Status);

            // Short: the tab refreshes the picture itself every two minutes with a cache-busting
            // query, and this keeps a plain reload from re-downloading half a megabyte.
            http.Response.Headers.CacheControl = "private, max-age=60";
            return Results.File(image.Bytes, "image/gif");
        })
        .RequireAuthorization();

        // The extras under the main report: precipitation totals, hour-by-hour feels-like and
        // gusts, the barometer's trend, the forecasters' discussion and the SPC severe outlook.
        app.MapGet("/api/weather/details", async (WeatherService weather, WeatherMapService map, CancellationToken ct) =>
        {
            var details = await weather.GetDetailsAsync(ct);
            if (details.State != WeatherReportState.Ok) return Results.Ok(details);
            var severe = await map.GetSevereOutlookAsync(ct);
            return Results.Ok(new WeatherDetails
            {
                State = details.State,
                Totals = details.Totals,
                Hours = details.Hours,
                Pressure = details.Pressure,
                Discussion = details.Discussion,
                Severe = severe,
                FetchedAt = details.FetchedAt,
                Problems = details.Problems
            });
        })
        .RequireAuthorization();

        // The radar map: home, the radar sites, the layers and the frame times each can play.
        // `station` picks whose per-radar layers to list (default: the one nearest home).
        app.MapGet("/api/weather/map", async (string? station, WeatherMapService map, CancellationToken ct) =>
            Results.Ok(await map.GetMapInfoAsync(station, ct)))
        .RequireAuthorization();

        // One map tile - basemap ("base", "labels") or a radar layer - proxied and cached.
        app.MapGet("/api/weather/map/tile/{layer}/{z:int}/{x:int}/{y:int}", async (
            string layer, int z, int x, int y, string? station, string? time,
            WeatherMapService map, HttpContext http, CancellationToken ct) =>
        {
            var tile = await map.GetTileAsync(layer, z, x, y, station, time, ct);
            if (tile.Bytes is null) return Results.Json(new { error = tile.Problem }, statusCode: tile.Status);

            // A dated frame never changes; the latest one does every couple of minutes, and the
            // basemap hardly ever.
            http.Response.Headers.CacheControl = layer is "base" or "labels" ? "private, max-age=604800"
                : string.IsNullOrEmpty(time) ? "private, max-age=60"
                : "private, max-age=21600";
            return Results.File(tile.Bytes, tile.ContentType);
        })
        .RequireAuthorization();

        app.MapGet("/api/weather/map/legend/{layer}", async (
            string layer, string? station, WeatherMapService map, HttpContext http, CancellationToken ct) =>
        {
            var legend = await map.GetLegendAsync(layer, station, ct);
            if (legend.Bytes is null) return Results.Json(new { error = legend.Problem }, statusCode: legend.Status);
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(legend.Bytes, "image/png");
        })
        .RequireAuthorization();

        // Storm cells, warning outlines and today's outlook for the part of the map on screen.
        app.MapGet("/api/weather/storms", async (
            double? west, double? south, double? east, double? north,
            WeatherMapService map, CancellationToken ct) =>
            Results.Ok(await map.GetStormsAsync(west, south, east, north, ct)))
        .RequireAuthorization();

        // Lightning flashes inside the box over the loop window. `epoch` and `after` come from the
        // previous answer, so a map left open only gets what arrived since.
        app.MapGet("/api/weather/lightning", (
            double? west, double? south, double? east, double? north, string? epoch, long? after,
            LightningService lightning) =>
            Results.Ok(lightning.GetFlashes(west, south, east, north, epoch, after)))
        .RequireAuthorization();

        // The "send test alert" button on WEATHER ALERTS: a sample tornado warning through every
        // channel that is set up, so the owner learns where a real one will land before one does.
        // Same answer shape as the other test buttons ({ sent, detail } / { sent, error }).
        app.MapPost("/api/settings/weather-alerts/test", async (
            IOwnerNotifier notifier,
            AppSettingsFile settings,
            CancellationToken ct) =>
        {
            var location = await settings.GetSectionAsync<LocationOptions>(LocationOptions.SectionName, ct);
            var place = string.IsNullOrWhiteSpace(location?.Label) ? "your location" : location.Label.Trim();
            var sample = WeatherAlertPolicy.Sample(DateTimeOffset.UtcNow, place);

            var sent = await notifier.SendAsync(
                "TEST " + WeatherAlertPolicy.Subject(sample, place),
                WeatherAlertPolicy.Body(sample, null),
                ct);

            return sent
                ? Results.Ok(new { sent = true, detail = "Sent a sample tornado warning to every channel that is set up - check your email, Telegram and the apps." })
                : Results.Json(new { sent = false, error = "No channel accepted it. Set up Email, Telegram Alerts or Push Notifications first, then try again." }, statusCode: 502);
        })
        .RequireAuthorization("AdminOnly");
    }
}
