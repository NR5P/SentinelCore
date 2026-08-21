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
        MapManualRecord(app);

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

    // How much video either side of the press the clip covers. The look-back is the point: you
    // press this *after* seeing something, so a clip that starts at the press has already missed it.
    // Both sit comfortably inside the live stream's rolling segment window.
    private const int ManualClipPreRollSeconds = 8;
    private const int ManualClipPostRollSeconds = 8;

    // Long enough to swallow a double-tap, short enough that deliberately recording twice in a row
    // works. Deliberately not the camera's configured motion cooldown - that number is tuned for a
    // detector that fires repeatedly at one event, and a person pressing a button is not that.
    private const int ManualTriggerCooldownSeconds = 5;

    // "Record this now" from the live view, for any camera - the thing you press when you are
    // looking at something and want it kept.
    //
    // It goes through CameraTriggerService rather than doing something of its own, which is what
    // makes it land where the user expects: the same TriggerEvent row, the same Feed card, the same
    // analysis and notification path a zone trigger takes. The one thing that service does not do
    // is cut video, because its other callers (the HTTP trigger API, a Device Hub sensor) fire at
    // cameras nobody is watching and have no buffer to cut from. Here there is one - the live HLS
    // stream the viewer is already pulling - so the clip is attached afterwards.
    private static void MapManualRecord(WebApplication app)
    {
        app.MapPost("/api/cameras/{id:guid}/record", async (
            Guid id,
            ICameraStore store,
            CameraTriggerService triggers,
            ClipRecorder clips,
            HlsStreamManager streams,
            ITriggerEventStore events,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var pressedAt = DateTimeOffset.UtcNow;
            var result = await triggers.TriggerAsync(
                camera,
                new CameraTriggerRequest("the app's record button", AlertProfile: null, ManualTriggerCooldownSeconds),
                ct);

            if (result.Status == CameraTriggerStatus.Cooldown)
                return Results.Problem(result.Detail, statusCode: 429);
            if (!result.Triggered)
                return Results.Problem(result.Detail, statusCode: 502);

            // Deliberately not awaited, and deliberately not on the request's token: the clip cannot
            // be built until the post-roll has actually been recorded, and nobody should be holding
            // an HTTP connection open for that. The event and its still image already exist, so the
            // press has been honoured whatever happens next.
            if (result.TriggerEventId is { } triggerId)
            {
                var logger = loggerFactory.CreateLogger("SentinelCore.Web.ManualRecord");
                _ = AttachManualClipAsync(camera, triggerId, pressedAt, clips, streams, events, logger);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();
    }

    private static async Task AttachManualClipAsync(
        Camera camera, Guid triggerId, DateTimeOffset pressedAt,
        ClipRecorder clips, HlsStreamManager streams, ITriggerEventStore events, ILogger logger)
    {
        try
        {
            // Wait for the far side of the window to exist on disk, plus a segment's grace so the
            // last one has been closed rather than still being written.
            await Task.Delay(TimeSpan.FromSeconds(ManualClipPostRollSeconds + 2));

            var segments = streams.BufferedSegments(camera.Id);
            if (segments.Count == 0)
            {
                logger.LogInformation(
                    "Recorded a manual trigger for {Name}, but no live video buffer was on disk to cut a clip from - " +
                    "the still image is on the event either way.",
                    camera.Name);
                return;
            }

            var clipPath = await clips.BuildClipAsync(
                camera.Id,
                segments,
                pressedAt,
                pressedAt.AddSeconds(-ManualClipPreRollSeconds),
                pressedAt.AddSeconds(ManualClipPostRollSeconds),
                CancellationToken.None);

            if (clipPath is null) return;

            await events.SetClipPathAsync(triggerId, clipPath, CancellationToken.None);
            logger.LogInformation(
                "Saved a {Seconds}s clip for {Name} from the record button.",
                ManualClipPreRollSeconds + ManualClipPostRollSeconds, camera.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not attach a clip to the manual trigger for camera {CameraId}", camera.Id);
        }
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
