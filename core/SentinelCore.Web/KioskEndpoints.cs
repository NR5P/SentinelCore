namespace SentinelCore.Web;

// What the wall kiosk's screen-sleep script on the Pi reads (scripts/kiosk-pi/kiosk-touch-wake).
// Anonymous on purpose: that script has no login, and two timer lengths say nothing about anyone.
public static class KioskEndpoints
{
    // How old the NWS answer behind the warning banner may be. Screens ask every minute.
    public static readonly TimeSpan WeatherAlertsMaxAge = TimeSpan.FromMinutes(1);

    public static void MapKioskEndpoints(this WebApplication app)
    {
        app.MapGet("/api/kiosk/screen-sleep", async (AppSettingsFile settings, CancellationToken ct) =>
        {
            var kiosk = await settings.GetSectionAsync<KioskSettingsDto>(KioskSettingsDto.SectionName, ct)
                ?? new KioskSettingsDto();
            return Results.Ok(new { dimSeconds = kiosk.DimSeconds, offSeconds = kiosk.OffSeconds });
        }).AllowAnonymous();

        // The weather warnings every screen's warning banner shows (the wall kiosk's
        // kiosk-weather-alert.js, the dashboard, and the iPhone, Mac, Apple TV, Android and
        // Windows apps), already filtered by Settings > WEATHER WARNING BANNER. Needs a login like
        // the Weather tab it opens. /api/kiosk/weather-alerts is the name it shipped under.
        async Task<IResult> Warnings(AppSettingsFile settings, WeatherService weather, CancellationToken ct)
        {
            var options = await settings.GetSectionAsync<KioskWeatherSettingsDto>(KioskWeatherSettingsDto.SectionName, ct)
                ?? new KioskWeatherSettingsDto();
            if (!options.Enabled)
                return Results.Ok(KioskWeatherAlerts.Response(options, []));
            var feed = await weather.GetActiveAlertsAsync(WeatherAlertsMaxAge, ct);
            return Results.Ok(KioskWeatherAlerts.Response(options, feed?.Alerts ?? []));
        }

        app.MapGet("/api/weather/warnings", Warnings).RequireAuthorization();
        app.MapGet("/api/kiosk/weather-alerts", Warnings).RequireAuthorization();
    }
}

public sealed record KioskWeatherAlert(
    string Id,
    string Event,
    string Level,
    string Category,
    string Severity,
    string Headline,
    string Instruction,
    DateTimeOffset? Ends,
    IReadOnlyList<string> References);

public sealed record KioskWeatherAlertsResponse(
    bool Enabled,
    int WakeMinutes,
    bool OpenRadar,
    IReadOnlyList<KioskWeatherAlert> Alerts);

// Pure, so the filtering is testable with fixtures: the alerts the kiosk settings ask for, worst
// first. Cancellations are left out - they mean the alert they reference is over.
public static class KioskWeatherAlerts
{
    public static KioskWeatherAlertsResponse Response(KioskWeatherSettingsDto options, IReadOnlyList<WeatherAlert> alerts)
    {
        var filter = options.ToFilter();
        var shown = options.Enabled
            ? alerts
                .Where(a => !string.Equals(a.MessageType, "Cancel", StringComparison.OrdinalIgnoreCase))
                .Where(a => WeatherAlertPolicy.Wants(filter, a))
                .OrderByDescending(a => WeatherAlertPolicy.LevelOf(a.Event))
                .ThenBy(a => WeatherService.SeverityRank(a.Severity))
                .Select(a => new KioskWeatherAlert(
                    a.Id,
                    a.Event,
                    WeatherAlertPolicy.LevelOf(a.Event).ToString().ToLowerInvariant(),
                    WeatherAlertPolicy.CategoryOf(a.Event),
                    a.Severity,
                    a.Headline,
                    a.Instruction,
                    a.Ends ?? a.Expires,
                    a.References))
                .ToList()
            : [];
        return new KioskWeatherAlertsResponse(options.Enabled, options.WakeMinutes, options.OpenRadar, shown);
    }
}
