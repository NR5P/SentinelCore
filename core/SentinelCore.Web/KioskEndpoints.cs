namespace SentinelCore.Web;

// What the wall kiosk's screen-sleep script on the Pi reads (scripts/kiosk-pi/kiosk-touch-wake).
// Anonymous on purpose: that script has no login, and two timer lengths say nothing about anyone.
public static class KioskEndpoints
{
    public static void MapKioskEndpoints(this WebApplication app)
    {
        app.MapGet("/api/kiosk/screen-sleep", async (AppSettingsFile settings, CancellationToken ct) =>
        {
            var kiosk = await settings.GetSectionAsync<KioskSettingsDto>(KioskSettingsDto.SectionName, ct)
                ?? new KioskSettingsDto();
            return Results.Ok(new { dimSeconds = kiosk.DimSeconds, offSeconds = kiosk.OffSeconds });
        }).AllowAnonymous();
    }
}
