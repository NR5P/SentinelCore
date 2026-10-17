using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Web;

public sealed class HomeKitVideoLog;

public sealed class HomeKitStreamRequest
{
    public string? Quality { get; set; }
}

// The HomeKit sidecar's way to the cameras when HomeKit:IncludeVideo is on. The cameras and
// go2rtc live in this process, while the sidecar otherwise only talks to the main app, so it
// asks here for the camera roster, a loopback RTSP address to point its ffmpeg at, and a still
// for Home's tiles and notifications.
//
// Not for browsers: no login, the shared Bridge:ApiKey instead (the same arrangement as
// ScheduleWeatherEndpoints), and loopback callers only, because a stream address handed out
// here is a live view of the camera. Fails closed when no key is configured or the setting is off.
public static class HomeKitVideoEndpoints
{
    public static void MapHomeKitVideoEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/internal/homekit");

        group.MapGet("/cameras", async (
            HttpContext http, AppSettingsFile mainAppSettings, ICameraStore cameras, CancellationToken ct) =>
        {
            if (await RejectAsync(http, mainAppSettings, ct) is { } rejected) return rejected;

            var all = await cameras.GetAllAsync(ct);
            return Results.Ok(all.Select(camera => new
            {
                id = camera.Id,
                name = camera.Name,
                audio = camera.IncludeAudio,
            }));
        });

        group.MapPost("/cameras/{id:guid}/stream", async (
            Guid id, HomeKitStreamRequest? request, HttpContext http, AppSettingsFile mainAppSettings,
            ICameraStore cameras, Go2RtcManager go2Rtc, ILogger<HomeKitVideoLog> logger, CancellationToken ct) =>
        {
            if (await RejectAsync(http, mainAppSettings, ct) is { } rejected) return rejected;

            var camera = await cameras.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var quality = string.Equals(request?.Quality, "sub", StringComparison.OrdinalIgnoreCase)
                ? StreamQuality.Sub
                : StreamQuality.Main;
            try
            {
                return Results.Ok(new { url = await go2Rtc.EnsureRtspSourceAsync(camera, quality, ct) });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning(ex, "Could not get a HomeKit stream ready for camera {Camera}", camera.Name);
                return Results.Json(new { error = "The live stream isn't available." }, statusCode: 503);
            }
        });

        group.MapGet("/cameras/{id:guid}/snapshot.jpg", async (
            Guid id, HttpContext http, AppSettingsFile mainAppSettings, ICameraStore cameras,
            Go2RtcManager go2Rtc, CancellationToken ct) =>
        {
            if (await RejectAsync(http, mainAppSettings, ct) is { } rejected) return rejected;

            var camera = await cameras.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            // frame.jpeg reads go2rtc's Main stream by name, so it has to be registered first.
            try
            {
                await go2Rtc.EnsureRecordableAsync(camera, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                return Results.StatusCode(503);
            }

            var frame = await go2Rtc.CaptureFrameAsync(camera.Id, ct);
            return frame is null ? Results.StatusCode(503) : Results.File(frame, "image/jpeg");
        });
    }

    private static async Task<IResult?> RejectAsync(HttpContext http, AppSettingsFile mainAppSettings, CancellationToken ct)
    {
        if (http.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
        if (!KeyMatches(bridge?.ApiKey, http.Request.Headers["X-Api-Key"].ToString()))
            return Results.Json(new { error = "Missing or invalid X-Api-Key." }, statusCode: 401);

        var homeKit = await mainAppSettings.GetSectionAsync<HomeKitSettingsDto>("HomeKit", ct);
        if (homeKit is not { Enabled: true, IncludeVideo: true })
            return Results.Json(new { error = "HomeKit live video is turned off." }, statusCode: 409);

        return null;
    }

    private static bool KeyMatches(string? configured, string supplied)
    {
        if (string.IsNullOrEmpty(configured)) return false;
        var expected = Encoding.UTF8.GetBytes(configured);
        var actual = Encoding.UTF8.GetBytes(supplied);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
