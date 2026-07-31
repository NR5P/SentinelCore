using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

public sealed class CameraInputDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string StreamUrl { get; set; } = string.Empty;

    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IncludeAudio { get; set; }

    // OnvifPtzServiceUrl/OnvifProfileToken come from a discovery probe (see
    // CameraDiscoveryService/CameraProbeResult) and are just passed through here on save, the
    // same way ProbeResult.StreamUrl already flows into StreamUrl above - the client never makes
    // these up itself. PtzEnabled defaults to whatever the probe detected but can be flipped
    // independently: turning it on without a detected service URL/token just means PTZ calls for
    // this camera will fail with a clear error rather than silently doing nothing.
    public bool PtzEnabled { get; set; }
    public string? OnvifPtzServiceUrl { get; set; }
    public string? OnvifProfileToken { get; set; }
}

public static class CameraEndpoints
{
    private static readonly string[] AllowedSchemes = ["rtsp", "http", "https"];

    public static void MapCameraEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cameras", async (ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            var cameras = await store.GetAllAsync(ct);

            // Fire-and-forget: warm up each camera's stream as soon as something asks for the
            // camera list - app launch, pull-to-refresh, loading the web dashboard - rather than
            // waiting for the Live tab to actually be opened, so ffmpeg has usually already
            // produced segments by the time a viewer gets there. A no-op for cameras already
            // warm (EnsureStreamStartedAsync reuses the running session). CancellationToken.None
            // because this outlives the request that triggered it; failures are swallowed here
            // since the real stream request surfaces them normally.
            foreach (var camera in cameras)
            {
                _ = streams.EnsureStreamStartedAsync(camera, CancellationToken.None)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }

            return Results.Ok(cameras);
        }).RequireAuthorization();

        app.MapPost("/api/cameras", async (CameraInputDto dto, ICameraStore store, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var camera = await store.AddAsync(
                dto.Name.Trim(), dto.StreamUrl.Trim(), dto.Username, dto.Password, dto.IncludeAudio,
                dto.PtzEnabled, dto.OnvifPtzServiceUrl, dto.OnvifProfileToken, ct);
            return Results.Ok(camera);
        }).RequireAuthorization("AdminOnly");

        app.MapPut("/api/cameras/{id:guid}", async (Guid id, CameraInputDto dto, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var updated = await store.UpdateAsync(
                id, dto.Name.Trim(), dto.StreamUrl.Trim(), dto.Username, dto.Password, dto.IncludeAudio,
                dto.PtzEnabled, dto.OnvifPtzServiceUrl, dto.OnvifProfileToken, ct);
            if (!updated) return Results.NotFound();

            // Source may have changed - restart the stream on next view rather than serving stale settings.
            streams.StopStream(id);
            return Results.Ok(new { saved = true });
        }).RequireAuthorization("AdminOnly");

        app.MapDelete("/api/cameras/{id:guid}", async (Guid id, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            streams.StopStream(id);
            var deleted = await store.DeleteAsync(id, ct);
            return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
        }).RequireAuthorization("AdminOnly");

        // Nothing here may be cached, by the client or by anything in front of the server. The
        // playlist is rewritten every segment, so a cached copy hands the player an out-of-date
        // segment list - it either plays that far behind live or asks for segments that have
        // already rolled off. The segments are worse: their names restart at seg_000.ts every time
        // a session does, so a cache holding one from a previous session would feed a viewer video
        // from a completely different point in time. Relevant well beyond the browser, since
        // Internet Exposed Mode puts a Cloudflare Tunnel in this path.
        app.MapGet("/api/cameras/{id:guid}/stream/{file}", async (Guid id, string file, HttpContext http, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, private";

            if (file == "stream.m3u8")
            {
                var camera = await store.GetAsync(id, ct);
                if (camera is null) return Results.NotFound();

                string playlistPath;
                try
                {
                    playlistPath = await streams.EnsureStreamStartedAsync(camera, ct);
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message, statusCode: 502);
                }

                return Results.File(playlistPath, "application/vnd.apple.mpegurl");
            }

            streams.Touch(id);
            var segmentPath = streams.GetSegmentPath(id, file);
            if (segmentPath is null || !File.Exists(segmentPath)) return Results.NotFound();

            return Results.File(segmentPath, "video/mp2t");
        }).RequireAuthorization();

        // Instant poster frame for a camera tile: if there's a warm stream, this is just a file
        // read of the JPEG ffmpeg has been overwriting in the background (see
        // HlsStreamManager.GetThumbnailPath). Only a cold camera nobody has looked at recently
        // falls back to a one-off snapshot grab.
        app.MapGet("/api/cameras/{id:guid}/thumbnail.jpg", async (Guid id, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            var thumbnailPath = streams.GetThumbnailPath(id);
            if (thumbnailPath is not null) return Results.File(thumbnailPath, "image/jpeg");

            var camera = await store.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var bytes = await streams.CaptureSnapshotAsync(camera, ct);
            return bytes is null
                ? Results.Problem("Could not grab a frame from this camera.", statusCode: 502)
                : Results.File(bytes, "image/jpeg");
        }).RequireAuthorization();
    }

    private static List<string> Validate(CameraInputDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Name is required.");
        if (string.IsNullOrWhiteSpace(dto.StreamUrl))
        {
            errors.Add("Stream URL is required.");
        }
        else if (!Uri.TryCreate(dto.StreamUrl.Trim(), UriKind.Absolute, out var uri) ||
                 !AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("Stream URL must be an absolute rtsp://, http://, or https:// URL.");
        }

        return errors;
    }
}
