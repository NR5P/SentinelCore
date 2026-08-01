using System.Security.Cryptography;
using System.Text;
using SentinelCore;

namespace SentinelCore.Web;

// The one route in this app that authenticates with a key instead of a dashboard session, so
// something that can't log in - a doorbell, an NVR's alert action, an IP relay, a cron line - can
// still say "look at this camera now". Deliberately a GET with the key in the query string: that
// is the only shape a lot of that hardware can send, and a POST-with-header API it can't produce
// is no API at all for the person trying to wire one up.
//
// The cost of that choice is that the key lands in server logs, browser history, and anything
// else that records URLs, so it is scoped to exactly this - triggering a camera - and is not the
// key Blue Iris uses. It is also refused outright on the Internet Exposed port: that port carries
// traffic from the open internet, where a URL-borne credential has no business being.
public static class CameraTriggerEndpoints
{
    public static void MapCameraTriggerEndpoints(this WebApplication app)
    {
        MapTriggerUrl(app);

        // No RequireAuthorization on purpose - the key check below is the gate. AuthGateMiddleware
        // still runs first, but all it can do is hand out a synthetic Admin when login is off,
        // which changes nothing here.
        app.MapGet("/api/cameras/{id:guid}/trigger", async (
            Guid id,
            HttpContext context,
            string? key,
            string? profile,
            ICameraStore cameras,
            AppSettingsFile settings,
            CameraTriggerService triggers,
            InternetExposedSettings internetExposed,
            ILogger<CameraTriggerLog> logger,
            CancellationToken ct) =>
        {
            var ip = ClientIpResolver.Resolve(context, internetExposed);

            if (internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port)
            {
                logger.LogWarning("Refused a camera trigger for {CameraId} from {Ip} - the trigger API is not served on the Internet Exposed port", id, ip);
                return Results.NotFound();
            }

            var options = await settings.GetSectionAsync<TriggerApiOptions>(TriggerApiOptions.SectionName, ct);
            if (options is not { Enabled: true })
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(options.ApiKey) || !KeyMatches(options.ApiKey, key))
            {
                logger.LogWarning("Rejected a camera trigger for {CameraId} from {Ip} - missing or invalid key", id, ip);
                return Results.Json(new { error = "Missing or invalid key." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var camera = await cameras.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            logger.LogInformation("Camera trigger requested for {CameraId} ({Name}) from {Ip}", camera.Id, camera.Name, ip);

            var result = await triggers.TriggerAsync(
                camera,
                new CameraTriggerRequest($"trigger API ({ip})", profile, options.CooldownSeconds),
                ct);

            // 429 for the cooldown specifically, so a device that retries on failure can tell
            // "come back later" apart from "that didn't work" and back off accordingly.
            return result.Status switch
            {
                CameraTriggerStatus.Triggered => Results.Ok(new { triggered = true, camera = camera.Name, detail = result.Detail, triggerId = result.TriggerEventId }),
                CameraTriggerStatus.Cooldown => Results.Json(new { triggered = false, detail = result.Detail }, statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Json(new { triggered = false, detail = result.Detail }, statusCode: StatusCodes.Status502BadGateway)
            };
        });
    }

    private static void MapTriggerUrl(WebApplication app)
    {
        // The ready-to-paste address for one camera, for the trigger zone screen. Admin-only,
        // because it hands back the key itself - the same bar as the settings page that shows it.
        // Returns the path and key rather than a whole URL so the caller can prefix its own
        // origin: the right host to give a device is whatever address the dashboard is actually
        // reachable at on that network, which this process can't know better than the browser.
        app.MapGet("/api/cameras/{id:guid}/trigger-url", async (
            Guid id, ICameraStore cameras, AppSettingsFile settings, CancellationToken ct) =>
        {
            if (await cameras.GetAsync(id, ct) is null) return Results.NotFound();

            var options = await settings.GetSectionAsync<TriggerApiOptions>(TriggerApiOptions.SectionName, ct);
            return Results.Ok(new
            {
                enabled = options is { Enabled: true } && !string.IsNullOrWhiteSpace(options.ApiKey),
                path = $"/api/cameras/{id}/trigger",
                key = options?.ApiKey ?? string.Empty
            });
        }).RequireAuthorization("AdminOnly");
    }

    // Same fixed-time comparison the main app's ApiKeyMiddleware uses - a key checked with an
    // ordinary string compare leaks its length and prefix to anyone timing the responses. Public
    // for testability; this project has no InternalsVisibleTo.
    public static bool KeyMatches(string expected, string? supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

// Marker type only, for a distinctly-named ILogger<T> category - naming the static endpoint class
// itself here isn't legal (CS0718), and ILogger<Program> collides with the SentinelCore project's
// own top-level Program type once both are referenced. Same reason as AutomationRuleProxyLog.
public sealed class CameraTriggerLog;
