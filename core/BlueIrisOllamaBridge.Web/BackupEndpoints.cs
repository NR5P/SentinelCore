using System.Text.Json;

namespace BlueIrisOllamaBridge.Web;

// Everything configurable through this dashboard, in one portable file: the settings that
// SettingsEndpoints already exposes, plus cameras and trigger zones, which live in their own
// SQLite stores and aren't part of that response. Deliberately excludes Setup (one-time
// onboarding state) and License (just a file path - the .lic file has its own upload flow).
//
// This intentionally includes secrets (SMTP password, bot tokens, API keys, the login
// password hash) for a genuinely one-file restore - the exported file is as sensitive as
// appsettings.json itself, and the UI says so.
public sealed record BackupDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    SettingsSnapshot? Settings,
    IReadOnlyList<Camera>? Cameras,
    IReadOnlyList<TriggerConfig>? TriggerZones);

public static class BackupEndpoints
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions ImportOptions = new(JsonSerializerDefaults.Web);

    public static void MapBackupEndpoints(this WebApplication app)
    {
        var backup = app.MapGroup("/api/backup").RequireAuthorization("AdminOnly");

        backup.MapGet("/export", async (AppSettingsFile settings, ICameraStore cameras, ITriggerConfigStore triggerConfigs, CancellationToken ct) =>
        {
            var document = new BackupDocument(
                CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                await SettingsEndpoints.BuildSnapshotAsync(settings, ct),
                await cameras.GetAllAsync(ct),
                await triggerConfigs.GetAllAsync(ct));

            return Results.Ok(document);
        });

        // Applies settings, then cameras, then trigger zones, in that order - trigger zones
        // reference cameras by Id, so the cameras they point at have to exist first. Cameras
        // are restored via ICameraStore.RestoreAsync, which (unlike AddAsync) keeps the
        // camera's original Id instead of minting a new one, so the trigger zones in the same
        // backup still resolve correctly. Nothing in the current setup that isn't mentioned in
        // the backup is touched or removed - restore overlays, it never wipes.
        backup.MapPost("/import", async (HttpRequest request, AppSettingsFile settings, ICameraStore cameras, ITriggerConfigStore triggerConfigs, CancellationToken ct) =>
        {
            BackupDocument? document;
            try
            {
                document = await JsonSerializer.DeserializeAsync<BackupDocument>(request.Body, ImportOptions, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { errors = new[] { "That file doesn't look like a Sentinel Core backup - it isn't valid JSON." } });
            }

            var errors = Validate(document);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            if (document!.Settings is not null)
                await SettingsEndpoints.ApplySnapshotAsync(document.Settings, settings, ct);

            var camerasRestored = 0;
            foreach (var camera in document.Cameras ?? [])
            {
                await cameras.RestoreAsync(camera, ct);
                camerasRestored++;
            }

            var triggerZonesRestored = 0;
            foreach (var zone in document.TriggerZones ?? [])
            {
                await triggerConfigs.SaveAsync(zone, ct);
                triggerZonesRestored++;
            }

            return Results.Ok(new { success = true, camerasRestored, triggerZonesRestored });
        });
    }

    private static List<string> Validate(BackupDocument? document)
    {
        var errors = new List<string>();
        if (document is null)
        {
            errors.Add("That file doesn't look like a Sentinel Core backup.");
            return errors;
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Unsupported backup version {document.SchemaVersion} (this install supports {CurrentSchemaVersion}).");

        if (document.Settings?.PromptProfiles is { } profiles && !profiles.ContainsKey("default"))
            errors.Add("Backup's prompt profiles are missing a 'default' profile.");

        return errors;
    }
}
