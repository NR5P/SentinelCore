using SentinelCore;

namespace SentinelCore.Web;

public sealed class TriggerZoneDto
{
    public string Name { get; set; } = "";
    public List<TriggerPoint> Polygon { get; set; } = [];
    public double MinTriggerArea { get; set; }
}

// The wire contract here is deliberately ADDITIVE, and has to stay that way.
//
// The shipped iOS/tvOS builds decode this with Swift's synthesized Codable and the Android build
// hand-parses it with org.json. Both ignore keys they don't know about, so adding `zones` is
// invisible to them - but Swift THROWS on a missing key it declares as non-optional, so dropping
// `zonePolygon` or `minTriggerArea` would hard-break every installed copy of the app. They stay,
// mirroring the primary zone. See TriggerConfig.ResolvedZones.
//
// Those clients also WRITE this object, and an old build sends no `zones` key at all. That is why
// Zones is nullable rather than defaulting to an empty list: absent (edit the primary zone, leave
// the rest alone) has to be distinguishable from empty (the new editor really did delete them).
// Get that wrong and someone editing the zone from their phone silently wipes every extra zone.
public sealed class TriggerConfigDto
{
    public bool Enabled { get; set; }
    public List<TriggerPoint> ZonePolygon { get; set; } = [];
    public double MinTriggerArea { get; set; }

    // Null means "this client doesn't know about zones" - NOT "no zones". Never give this a
    // default; see the note above.
    public List<TriggerZoneDto>? Zones { get; set; }
    public int CooldownSeconds { get; set; }
    public int MaxTriggersPerMinute { get; set; }
    public int ShadowSensitivity { get; set; } = 50;
    public bool RecordClips { get; set; }
    public int ClipPreSeconds { get; set; } = 5;
    public int ClipPostSeconds { get; set; } = 10;
    public bool SendToAlerts { get; set; }
    public string AlertProfile { get; set; } = "default";
    public string SensorDeviceId { get; set; } = "";
    public string SensorTriggerOn { get; set; } = "on";
    public int SensorCooldownSeconds { get; set; } = 30;
    public string SensorMeasurement { get; set; } = "";
    public double SensorThreshold { get; set; }

    // Nullable for the same reason Zones is, and it is the same trap: the shipped mobile builds
    // send none of these three keys, so a plain `bool` would read as false on every zone edit made
    // from a phone and silently switch the night window back off. Null means "this client doesn't
    // know about the night window" - NOT "turn it off". Resolved against the stored row in the PUT
    // handler. See TriggerNightWindow.
    public bool? NightWindowEnabled { get; set; }
    public int? NightStartHour { get; set; }
    public int? NightEndHour { get; set; }

    // Nullable for exactly the reason the three above are: no shipped mobile build sends this key,
    // and a plain `bool` would read as false and quietly route the coop back through the detector
    // the first time someone nudged its zone from a phone. Null means "this client doesn't know
    // about the setting", not "turn it off". See TriggerConfig.BypassFastLocal.
    public bool? BypassFastLocal { get; set; }

    public bool HasSensorBinding => !string.IsNullOrWhiteSpace(SensorDeviceId);

    public bool UsesThreshold =>
        SensorTriggerOn.Trim().ToLowerInvariant() is "below" or "above";
}

public static class TriggerEndpoints
{
    // "below"/"above" compare a harvested number against SensorThreshold; the first three read the
    // device's on/off state. Kept as one field rather than a separate mode flag because from the
    // user's side it is one question - what about this sensor should fire the camera.
    internal static readonly string[] SensorTriggerOnValues = ["on", "off", "any", "below", "above"];

    // Turns whatever the client sent into the zone list to persist, and is where the
    // old-client-must-not-destroy-data rule actually lives.
    //
    // dto.Zones populated -> the caller understands zones, so it is authoritative.
    // dto.Zones null      -> an older mobile build that only knows about the single legacy
    //                        polygon. Update the primary zone in place from what it sent and keep
    //                        every other zone exactly as it was.
    // Public so the old-client merge rule can be tested directly - this project has no
    // InternalsVisibleTo, same as CameraTriggerEndpoints.KeyMatches and SensorTriggerWatcher.
    public static async Task<List<TriggerZone>> ResolveZonesAsync(
        Guid cameraId, TriggerConfigDto dto, ITriggerConfigStore store, CancellationToken cancellationToken)
    {
        if (dto.Zones is { Count: > 0 })
            return [.. dto.Zones.Select(z => new TriggerZone(
                string.IsNullOrWhiteSpace(z.Name) ? TriggerConfig.DefaultZoneName : z.Name.Trim(),
                z.Polygon,
                z.MinTriggerArea))];

        var existing = await store.GetAsync(cameraId, cancellationToken);
        var primary = new TriggerZone(
            existing?.ResolvedZones[0].Name ?? TriggerConfig.DefaultZoneName, dto.ZonePolygon, dto.MinTriggerArea);

        // An explicitly empty list (as opposed to a missing one) still means "just the primary" -
        // a camera always has at least one zone, so there is nothing sensible to save otherwise.
        if (existing is null || dto.Zones is not null) return [primary];

        return [primary, .. existing.ResolvedZones.Skip(1)];
    }

    // The night window's version of the old-client merge rule, and public for the same reason
    // ResolveZonesAsync is: getting it wrong is silent. A phone sends no night-window keys at all,
    // so treating absent as false would switch the window off every time someone nudged a zone
    // from the app, and nothing anywhere would say so - the camera would just start flooding again
    // the following night.
    //
    // existing is null for a camera that has never been configured, which falls back to the
    // record's own defaults (off, 21:00-07:00).
    public static (bool Enabled, int StartHour, int EndHour) ResolveNightWindow(
        TriggerConfigDto dto, TriggerConfig? existing) =>
        (dto.NightWindowEnabled ?? existing?.NightWindowEnabled ?? false,
         dto.NightStartHour ?? existing?.NightStartHour ?? 21,
         dto.NightEndHour ?? existing?.NightEndHour ?? 7);

    public static void MapTriggerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cameras/{id:guid}/trigger-config", async (Guid id, ITriggerConfigStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAsync(id, ct) ?? TriggerConfig.Disabled(id))).RequireAuthorization("AdminOnly");

        app.MapPut("/api/cameras/{id:guid}/trigger-config", async (Guid id, TriggerConfigDto dto, ICameraStore cameraStore, ITriggerConfigStore store, CancellationToken ct) =>
        {
            var camera = await cameraStore.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var existing = await store.GetAsync(id, ct);

            // Whatever the row already said, for any of the three night-window keys this client
            // didn't send - see ResolveNightWindow.
            var (nightEnabled, nightStart, nightEnd) = ResolveNightWindow(dto, existing);

            // Validated against the RESOLVED window, not the raw DTO. The zero-width check needs
            // the merged values or it can be walked straight past: a client that sends
            // NightWindowEnabled with only one of the two hours leaves the other null, the DTO-only
            // check skips itself for want of a pair, and an enabled zero-width window gets stored -
            // exactly the "turned it on and it did nothing" state the check exists to refuse.
            var errors = Validate(dto, nightEnabled, nightStart, nightEnd);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var zones = await ResolveZonesAsync(id, dto, store, ct);

            var profile = string.IsNullOrWhiteSpace(dto.AlertProfile) ? "default" : dto.AlertProfile.Trim();
            var config = new TriggerConfig(id, dto.Enabled, zones[0].Polygon, zones[0].MinTriggerArea, dto.CooldownSeconds, dto.MaxTriggersPerMinute,
                dto.ShadowSensitivity, dto.RecordClips, dto.ClipPreSeconds, dto.ClipPostSeconds, dto.SendToAlerts, profile,
                dto.SensorDeviceId.Trim(), dto.SensorTriggerOn.Trim().ToLowerInvariant(), dto.SensorCooldownSeconds, zones,
                dto.SensorMeasurement.Trim(), dto.SensorThreshold,
                nightEnabled, nightStart, nightEnd,
                // Same absent-key merge as the night window, for the same reason.
                dto.BypassFastLocal ?? existing?.BypassFastLocal ?? false);
            await store.SaveAsync(config, ct);
            return Results.Ok(config);
        }).RequireAuthorization("AdminOnly");

        // Just the names, not the full /api/settings payload - the zone editor's profile picker
        // has no business seeing API keys/bot tokens/passwords along with it.
        app.MapGet("/api/prompt-profile-names", async (AppSettingsFile mainAppSettings, CancellationToken ct) =>
        {
            var profiles = await mainAppSettings.GetSectionAsync<Dictionary<string, PromptProfile>>("PromptProfiles", ct);
            var names = (profiles ?? [])
                .Select(kvp => new { key = kvp.Key, displayName = string.IsNullOrWhiteSpace(kvp.Value.DisplayName) ? kvp.Key : kvp.Value.DisplayName })
                .OrderBy(p => p.displayName, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(names);
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/cameras/{id:guid}/reference-frame", async (Guid id, ICameraStore cameraStore, HlsStreamManager streams, CancellationToken ct) =>
        {
            var camera = await cameraStore.GetAsync(id, ct);
            if (camera is null) return Results.NotFound();

            var bytes = await streams.CaptureSnapshotAsync(camera, ct);
            if (bytes is null) return Results.Problem("Could not grab a frame from this camera - check the stream URL/credentials.", statusCode: 502);

            return Results.File(bytes, "image/jpeg");
        }).RequireAuthorization("AdminOnly");

        // includeUnconfirmed is what the Triggers tab's "show hidden" toggle sets - triggers the
        // AI rejected, plus any still waiting on a verdict. Off by default, so the tab shows only
        // what was confirmed (or never needed confirming) - see TriggerConfirmationService.
        app.MapGet("/api/triggers", async (ICameraStore cameraStore, ITriggerEventStore store, CancellationToken ct,
            int limit = 50, Guid? cameraId = null, bool includeUnconfirmed = false) =>
        {
            var capped = Math.Clamp(limit, 1, 500);
            var events = await store.GetRecentAsync(capped, cameraId, includeUnconfirmed, ct);
            var cameras = await cameraStore.GetAllAsync(ct);
            var names = cameras.ToDictionary(c => c.Id, c => c.Name);

            return Results.Ok(events.Select(e => new
            {
                e.Id,
                e.CameraId,
                cameraName = names.GetValueOrDefault(e.CameraId, "deleted camera"),
                occurredAt = e.OccurredAt,
                triggerAreaFraction = e.TriggerAreaFraction,
                hasClip = !string.IsNullOrWhiteSpace(e.ClipPath) && File.Exists(e.ClipPath),
                incidentId = e.IncidentId,
                confirmation = e.Confirmation.ToString().ToLowerInvariant(),
                zoneName = e.ZoneName
            }));
        }).RequireAuthorization();

        // size=card serves the downscaled copy the Events list displays - see EventImageResults,
        // which this shares with the alert-side endpoint so the two cannot disagree.
        app.MapGet("/api/triggers/{id:guid}/image", async (
            Guid id, string? size, ITriggerEventStore store, EventImageThumbnails thumbnails,
            HttpContext http, CancellationToken ct) =>
        {
            var trigger = await store.GetAsync(id, ct);
            if (trigger is null || !File.Exists(trigger.ImagePath)) return Results.NotFound();

            return await EventImageResults.ServeAsync(trigger.ImagePath, size, thumbnails, http, ct);
        }).RequireAuthorization();

        // Streamed from disk with range support so the browser can seek without pulling the
        // whole clip first.
        app.MapGet("/api/triggers/{id:guid}/clip", async (Guid id, ITriggerEventStore store, CancellationToken ct) =>
        {
            var trigger = await store.GetAsync(id, ct);
            if (trigger?.ClipPath is null || !File.Exists(trigger.ClipPath)) return Results.NotFound();

            return Results.File(trigger.ClipPath, "video/mp4", enableRangeProcessing: true);
        }).RequireAuthorization();
    }

    // Takes the already-merged night window alongside the DTO, because those three are the only
    // fields whose stored value can differ from what the request carried - see ResolveNightWindow.
    // Public for the same reason ResolveNightWindow is: it's the gate in front of a setting whose
    // failure mode is silence, so it needs to be assertable directly.
    public static List<string> Validate(TriggerConfigDto dto, bool nightEnabled, int nightStartHour, int nightEndHour)
    {
        var errors = new List<string>();

        if (dto.Enabled && dto.ZonePolygon.Count < 3)
            errors.Add("Draw a zone with at least 3 points before enabling.");

        if (dto.ZonePolygon.Any(p => p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
            errors.Add("Zone points must be within the frame.");

        if (dto.MinTriggerArea <= 0 || dto.MinTriggerArea > 1)
            errors.Add("Object size must be greater than 0 and no larger than the whole frame.");

        // Same rules again per zone, reported with the zone's name so a message about the fourth
        // zone doesn't read as though it were about the one on screen. Only runs for a client that
        // sent zones at all - the legacy fields above already cover the older ones.
        foreach (var (zone, index) in (dto.Zones ?? []).Select((z, i) => (z, i)))
        {
            var label = string.IsNullOrWhiteSpace(zone.Name) ? $"Zone {index + 1}" : zone.Name.Trim();

            if (dto.Enabled && zone.Polygon.Count < 3)
                errors.Add($"\"{label}\" needs at least 3 points before enabling.");

            if (zone.Polygon.Any(p => p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
                errors.Add($"\"{label}\" has points outside the frame.");

            if (zone.MinTriggerArea <= 0 || zone.MinTriggerArea > 1)
                errors.Add($"\"{label}\" object size must be greater than 0 and no larger than the whole frame.");

            if (zone.Name.Trim().Length > 50)
                errors.Add($"\"{label}\" name is too long (50 characters max).");
        }

        if (dto.CooldownSeconds < 0)
            errors.Add("Cooldown seconds can't be negative.");

        if (dto.MaxTriggersPerMinute < 1)
            errors.Add("Max triggers per minute must be at least 1.");

        if (dto.ShadowSensitivity < 0 || dto.ShadowSensitivity > 100)
            errors.Add("Shadow sensitivity must be between 0 and 100.");

        // Not checked against the Device Hub's device list on purpose: the devices live in the
        // main app's database, and refusing to save a camera because that app happens to be
        // restarting would be worse than storing an id that SensorTriggerWatcher then reports as
        // missing. A binding whose device is gone simply never fires.
        if (dto.HasSensorBinding && !Guid.TryParse(dto.SensorDeviceId, out _))
            errors.Add("The sensor device id isn't a valid device.");

        if (dto.HasSensorBinding && !SensorTriggerOnValues.Contains(dto.SensorTriggerOn, StringComparer.OrdinalIgnoreCase))
            errors.Add("Sensor trigger state must be 'on', 'off', 'any', 'below', or 'above'.");

        // The one combination that would save happily and then never fire: a comparison with
        // nothing named to compare. Worth refusing at the door, because the failure is silent
        // afterwards - no state ever satisfies an empty key.
        if (dto.HasSensorBinding && dto.UsesThreshold && string.IsNullOrWhiteSpace(dto.SensorMeasurement))
            errors.Add("Choose which reading to compare when firing on a threshold.");

        if (dto.HasSensorBinding && dto.UsesThreshold && !double.IsFinite(dto.SensorThreshold))
            errors.Add("The sensor threshold must be a number.");

        if (dto.SensorCooldownSeconds is < 0 or > 3600)
            errors.Add("Sensor cooldown must be between 0 and 3600 seconds.");

        // Pre-roll is served out of a rolling on-disk buffer, so a long one costs real disk space.
        if (dto.ClipPreSeconds < 0 || dto.ClipPreSeconds > 60)
            errors.Add("Seconds before the trigger must be between 0 and 60.");

        if (dto.ClipPostSeconds < 0 || dto.ClipPostSeconds > 120)
            errors.Add("Seconds after the trigger must be between 0 and 120.");

        if (dto.RecordClips && dto.ClipPreSeconds + dto.ClipPostSeconds == 0)
            errors.Add("A clip needs at least one second before or after the trigger.");

        if (dto.AlertProfile.Length > 100)
            errors.Add("Profile name is too long.");

        if (dto.NightStartHour is < 0 or > 23 || dto.NightEndHour is < 0 or > 23)
            errors.Add("Night window hours must be between 0 and 23.");

        // Equal hours is a zero-width window that suppresses nothing (see TriggerNightWindow), so
        // saving it while the feature is on would look like it had been turned on and done nothing.
        // Refuse at the door rather than let that be discovered at 3am.
        //
        // Judged on the resolved window rather than the DTO's own fields, because the two can
        // differ: a client that sends the toggle and only one hour inherits the other from the
        // stored row, and a check that needed both keys present in the request would wave that
        // through. The web dashboard always sends all three, so today this only differs for a
        // partial client - which is precisely the client nobody would think to test.
        if (nightEnabled && nightStartHour == nightEndHour)
            errors.Add("The night window's start and end hours can't be the same - that window is zero hours long.");

        return errors;
    }
}
