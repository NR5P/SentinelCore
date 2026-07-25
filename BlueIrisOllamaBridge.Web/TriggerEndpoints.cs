using BlueIrisOllamaBridge;

namespace BlueIrisOllamaBridge.Web;

public sealed class TriggerConfigDto
{
    public bool Enabled { get; set; }
    public List<TriggerPoint> ZonePolygon { get; set; } = [];
    public double MinTriggerArea { get; set; }
    public int CooldownSeconds { get; set; }
    public int MaxTriggersPerMinute { get; set; }
    public int ShadowSensitivity { get; set; } = 50;
    public bool RecordClips { get; set; }
    public int ClipPreSeconds { get; set; } = 5;
    public int ClipPostSeconds { get; set; } = 10;
    public bool SendToAlerts { get; set; }
    public string AlertProfile { get; set; } = "default";
}

public static class TriggerEndpoints
{
    public static void MapTriggerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cameras/{id:guid}/trigger-config", async (Guid id, ITriggerConfigStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAsync(id, ct) ?? TriggerConfig.Disabled(id)));

        app.MapPut("/api/cameras/{id:guid}/trigger-config", async (Guid id, TriggerConfigDto dto, ICameraStore cameraStore, ITriggerConfigStore store, CancellationToken ct) =>
        {
            var camera = await cameraStore.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var profile = string.IsNullOrWhiteSpace(dto.AlertProfile) ? "default" : dto.AlertProfile.Trim();
            var config = new TriggerConfig(id, dto.Enabled, dto.ZonePolygon, dto.MinTriggerArea, dto.CooldownSeconds, dto.MaxTriggersPerMinute,
                dto.ShadowSensitivity, dto.RecordClips, dto.ClipPreSeconds, dto.ClipPostSeconds, dto.SendToAlerts, profile);
            await store.SaveAsync(config, ct);
            return Results.Ok(config);
        });

        // Just the names, not the full /api/settings payload - the zone editor's profile picker
        // has no business seeing API keys/bot tokens/passwords along with it.
        app.MapGet("/api/prompt-profile-names", async (AppSettingsFile mainAppSettings, CancellationToken ct) =>
        {
            var profiles = await mainAppSettings.GetSectionAsync<Dictionary<string, PromptProfile>>("PromptProfiles", ct);
            var names = (profiles ?? [])
                .Select(kvp => new { key = kvp.Key, displayName = string.IsNullOrWhiteSpace(kvp.Value.DisplayName) ? kvp.Key : kvp.Value.DisplayName })
                .OrderBy(p => p.displayName, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(names);
        });

        app.MapGet("/api/cameras/{id:guid}/reference-frame", async (Guid id, ICameraStore cameraStore, HlsStreamManager streams, CancellationToken ct) =>
        {
            var camera = await cameraStore.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var bytes = await streams.CaptureSnapshotAsync(camera, ct);
            if (bytes is null) return Results.Problem("Could not grab a frame from this camera - check the stream URL/credentials.", statusCode: 502);

            return Results.File(bytes, "image/jpeg");
        });

        app.MapGet("/api/triggers", async (ICameraStore cameraStore, ITriggerEventStore store, CancellationToken ct, int limit = 50, Guid? cameraId = null) =>
        {
            var capped = Math.Clamp(limit, 1, 500);
            var events = await store.GetRecentAsync(capped, cameraId, ct);
            var cameras = await cameraStore.GetAllAsync(ct);
            var names = cameras.ToDictionary(c => c.Id, c => c.Name);

            return Results.Ok(events.Select(e => new
            {
                e.Id,
                e.CameraId,
                cameraName = names.GetValueOrDefault(e.CameraId, "deleted camera"),
                occurredAt = e.OccurredAt,
                triggerAreaFraction = e.TriggerAreaFraction,
                hasClip = !string.IsNullOrWhiteSpace(e.ClipPath) && File.Exists(e.ClipPath)
            }));
        });

        app.MapGet("/api/triggers/{id:guid}/image", async (Guid id, ITriggerEventStore store, CancellationToken ct) =>
        {
            var trigger = await store.GetAsync(id, ct);
            if (trigger is null || !File.Exists(trigger.ImagePath)) return Results.NotFound();

            var bytes = await File.ReadAllBytesAsync(trigger.ImagePath, ct);
            return Results.File(bytes, "image/jpeg");
        });

        // Streamed from disk with range support so the browser can seek without pulling the
        // whole clip first.
        app.MapGet("/api/triggers/{id:guid}/clip", async (Guid id, ITriggerEventStore store, CancellationToken ct) =>
        {
            var trigger = await store.GetAsync(id, ct);
            if (trigger?.ClipPath is null || !File.Exists(trigger.ClipPath)) return Results.NotFound();

            return Results.File(trigger.ClipPath, "video/mp4", enableRangeProcessing: true);
        });
    }

    private static List<string> Validate(TriggerConfigDto dto)
    {
        var errors = new List<string>();

        if (dto.Enabled && dto.ZonePolygon.Count < 3)
            errors.Add("Draw a zone with at least 3 points before enabling.");

        if (dto.ZonePolygon.Any(p => p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
            errors.Add("Zone points must be within the frame.");

        if (dto.MinTriggerArea <= 0 || dto.MinTriggerArea > 1)
            errors.Add("Object size must be greater than 0 and no larger than the whole frame.");

        if (dto.CooldownSeconds < 0)
            errors.Add("Cooldown seconds can't be negative.");

        if (dto.MaxTriggersPerMinute < 1)
            errors.Add("Max triggers per minute must be at least 1.");

        if (dto.ShadowSensitivity < 0 || dto.ShadowSensitivity > 100)
            errors.Add("Shadow sensitivity must be between 0 and 100.");

        // Pre-roll is served out of a rolling on-disk buffer, so a long one costs real disk space.
        if (dto.ClipPreSeconds < 0 || dto.ClipPreSeconds > 60)
            errors.Add("Seconds before the trigger must be between 0 and 60.");

        if (dto.ClipPostSeconds < 0 || dto.ClipPostSeconds > 120)
            errors.Add("Seconds after the trigger must be between 0 and 120.");

        if (dto.RecordClips && dto.ClipPreSeconds + dto.ClipPostSeconds == 0)
            errors.Add("A clip needs at least one second before or after the trigger.");

        if (dto.AlertProfile.Length > 100)
            errors.Add("Profile name is too long.");

        return errors;
    }
}
