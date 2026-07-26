using System.ComponentModel.DataAnnotations;

namespace BlueIrisOllamaBridge.Web;

public sealed class CameraInputDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string StreamUrl { get; set; } = string.Empty;

    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IncludeAudio { get; set; }
}

public static class CameraEndpoints
{
    private static readonly string[] AllowedSchemes = ["rtsp", "http", "https"];

    public static void MapCameraEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cameras", async (ICameraStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        app.MapPost("/api/cameras", async (CameraInputDto dto, ICameraStore store, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var camera = await store.AddAsync(dto.Name.Trim(), dto.StreamUrl.Trim(), dto.Username, dto.Password, dto.IncludeAudio, ct);
            return Results.Ok(camera);
        });

        app.MapPut("/api/cameras/{id:guid}", async (Guid id, CameraInputDto dto, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var updated = await store.UpdateAsync(id, dto.Name.Trim(), dto.StreamUrl.Trim(), dto.Username, dto.Password, dto.IncludeAudio, ct);
            if (!updated) return Results.NotFound();

            // Source may have changed - restart the stream on next view rather than serving stale settings.
            streams.StopStream(id);
            return Results.Ok(new { saved = true });
        });

        app.MapDelete("/api/cameras/{id:guid}", async (Guid id, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
            streams.StopStream(id);
            var deleted = await store.DeleteAsync(id, ct);
            return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
        });

        app.MapGet("/api/cameras/{id:guid}/stream/{file}", async (Guid id, string file, ICameraStore store, HlsStreamManager streams, CancellationToken ct) =>
        {
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
        });
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
