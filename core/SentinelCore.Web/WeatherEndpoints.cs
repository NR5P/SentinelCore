namespace SentinelCore.Web;

// The Weather tab's two endpoints. Both need a login like the rest of /api, and neither needs an
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
