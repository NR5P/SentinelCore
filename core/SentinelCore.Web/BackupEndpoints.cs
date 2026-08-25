using System.Security.Claims;
using System.Text.Json;

namespace SentinelCore.Web;

// Everything configurable through this dashboard, in one portable file: the settings that
// SettingsEndpoints already exposes, plus the configuration that lives in the SQLite stores
// instead and so isn't part of that response - cameras, trigger zones, camera groups and
// gate/door checks. Deliberately excludes Setup (one-time onboarding state) and License (just a
// file path - the .lic file has its own upload flow).
//
// The settings half of this cannot drift: it is built and applied through the same
// SettingsEndpoints.BuildSnapshotAsync/ApplySnapshotAsync the settings screen uses, so a new
// settings section is in the backup the day it is added. The SQLite half has to be remembered by
// hand, which is exactly how camera groups and gate checks came to be missing from it for a
// while - if you add a store, add it here and to BackupRoundTripTests.
//
// Still NOT included, deliberately:
//   - Events, captured images, and the "reviewed" marks on them: history, not configuration, and
//     large. An install restored from this file is your setup, not your past.
//   - Login sessions: restoring one onto another machine would be handing over a live login.
//
// This intentionally includes secrets (SMTP password, bot tokens, API keys) and, since accounts
// are here, password hashes - for a genuinely one-file restore. The exported file is as sensitive
// as appsettings.json and accounts.db together, and the UI says so.
public sealed record BackupDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    SettingsSnapshot? Settings,
    IReadOnlyList<Camera>? Cameras,
    IReadOnlyList<TriggerConfig>? TriggerZones,
    // Added in schema 2. Optional with a default so a schema 1 file - which has no such property -
    // still deserializes, and so every one of these simply arrives null and is skipped on import.
    IReadOnlyList<CameraGroup>? CameraGroups = null,
    IReadOnlyList<GateCheckConfig>? GateChecks = null,
    IReadOnlyList<Account>? Accounts = null,
    IReadOnlyList<LiveOrderEntry>? LiveOrder = null);

public static class BackupEndpoints
{
    // 2 added CameraGroups and GateChecks. Bumping this does not invalidate older exports - see
    // Validate, which accepts anything up to this number.
    private const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions ImportOptions = new(JsonSerializerDefaults.Web);

    public static void MapBackupEndpoints(this WebApplication app)
    {
        var backup = app.MapGroup("/api/backup").RequireAuthorization("AdminOnly");

        // Accounts go in, password hashes and all, so that losing the machine doesn't also mean
        // rebuilding everyone's login by hand. That is a deliberate trade: it makes this file
        // enough to sign in as anybody on a restored install, so it needs storing like the
        // password it effectively is - which the dashboard, USER_GUIDE and DEPLOYMENT all say.
        // The hashes are still hashes; the plaintext API keys already in here are worth more.
        //
        // Live grid order rides along because it is keyed by account id and is meaningless
        // without them.
        backup.MapGet("/export", async (
            AppSettingsFile settings, ICameraStore cameras, ICameraGroupStore cameraGroups,
            ITriggerConfigStore triggerConfigs, IGateCheckStore gateChecks,
            IAccountStore accounts, ILiveOrderStore liveOrder, CancellationToken ct) =>
        {
            var document = new BackupDocument(
                CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                await SettingsEndpoints.BuildSnapshotAsync(settings, ct),
                await cameras.GetAllAsync(ct),
                await triggerConfigs.GetAllAsync(ct),
                await cameraGroups.GetAllAsync(ct),
                [.. (await gateChecks.GetAllAsync(ct)).Select(WithoutTransitionMemory)],
                await accounts.GetAllAsync(ct),
                await liveOrder.GetAllAsync(ct));

            return Results.Ok(document);
        });

        // Settings first, then cameras, then everything that points at a camera by Id - trigger
        // zones, camera groups and gate checks all do, so the cameras have to exist first.
        // Cameras and groups are restored via RestoreAsync, which (unlike AddAsync) keeps the
        // original Id instead of minting a new one, so those references still resolve, and so
        // importing the same file twice updates what it created rather than duplicating it.
        // Nothing in the current setup that isn't mentioned in the backup is touched or removed -
        // restore overlays, it never wipes.
        backup.MapPost("/import", async (
            HttpRequest request, HttpContext http, AppSettingsFile settings, ICameraStore cameras,
            ICameraGroupStore cameraGroups, ITriggerConfigStore triggerConfigs,
            IGateCheckStore gateChecks, IAccountStore accounts, ILiveOrderStore liveOrder,
            CancellationToken ct) =>
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

            var cameraGroupsRestored = 0;
            foreach (var group in document.CameraGroups ?? [])
            {
                await cameraGroups.RestoreAsync(group, ct);
                cameraGroupsRestored++;
            }

            var triggerZonesRestored = 0;
            foreach (var zone in document.TriggerZones ?? [])
            {
                await triggerConfigs.SaveAsync(zone, ct);
                triggerZonesRestored++;
            }

            // SaveAsync's upsert deliberately leaves the four transition-memory columns alone on
            // conflict, so importing onto a running install can't make a gate that is currently
            // open look like a fresh transition and re-notify. On a camera this install has never
            // seen, the neutral values the export wrote are what land.
            var gateChecksRestored = 0;
            foreach (var check in document.GateChecks ?? [])
            {
                await gateChecks.SaveAsync(WithoutTransitionMemory(check), ct);
                gateChecksRestored++;
            }

            // The account performing this restore is skipped, always. Everything else here
            // overlays; accounts are the one thing that can lock the person out of the install
            // they are in the middle of restoring - a backup whose admin password nobody
            // remembers any more would otherwise replace the working login with it, and there is
            // no way back from that through the dashboard. Skipping by both id and username
            // covers the two ways "the same person" can appear: a re-import onto this machine
            // (same id) and a restore onto a fresh one where setup minted a new id for the same
            // name. Whoever is signed in keeps their password; every other account comes back.
            var currentAccountId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var currentUsername = http.User.Identity?.Name;

            var accountsRestored = 0;
            var accountsSkipped = 0;
            foreach (var account in document.Accounts ?? [])
            {
                var isCurrent =
                    string.Equals(account.Id.ToString("N"), currentAccountId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(account.Username, currentUsername, StringComparison.OrdinalIgnoreCase);

                if (isCurrent)
                {
                    accountsSkipped++;
                    continue;
                }

                await accounts.RestoreAsync(account, ct);
                accountsRestored++;
            }

            // After the accounts, since every owner key names one - restoring an arrangement for
            // an account that isn't there yet would just be a row nothing ever reads.
            var liveOrderRestored = 0;
            foreach (var entry in document.LiveOrder ?? [])
            {
                await liveOrder.SaveAsync(entry.OwnerKey, entry.CameraIds, ct);
                liveOrderRestored++;
            }

            return Results.Ok(new
            {
                success = true,
                camerasRestored,
                triggerZonesRestored,
                cameraGroupsRestored,
                gateChecksRestored,
                accountsRestored,
                accountsSkipped,
                liveOrderRestored
            });
        });
    }

    // A gate check's LastKnownState/LastStateChangedAt/LastNotifiedAt/LastKnownBlocked are memory
    // of what this install last saw, not configuration - see GateCheckConfig. Carrying them into a
    // backup would export "when your gate was last open" along with the settings, and restoring
    // them onto a different machine would seed it with a transition that never happened there.
    private static GateCheckConfig WithoutTransitionMemory(GateCheckConfig config) =>
        config with
        {
            LastKnownState = "unknown",
            LastStateChangedAt = null,
            LastNotifiedAt = null,
            LastKnownBlocked = false
        };

    private static List<string> Validate(BackupDocument? document)
    {
        var errors = new List<string>();
        if (document is null)
        {
            errors.Add("That file doesn't look like a Sentinel Core backup.");
            return errors;
        }

        // Older files are fine and must stay fine: the whole point of an exported backup is that
        // it is still there when you need it, which may be several versions later. Every section
        // is optional, so a file from an earlier schema simply restores the sections it has. Only
        // a file from a NEWER version is refused - that one may contain configuration this install
        // would silently drop, and quietly restoring nine tenths of a backup is worse than saying
        // so. This used to be an equality check, which rejected old exports the day the schema
        // moved.
        if (document.SchemaVersion < 1 || document.SchemaVersion > CurrentSchemaVersion)
            errors.Add(
                $"Unsupported backup version {document.SchemaVersion} (this install supports up to {CurrentSchemaVersion}). " +
                "A backup from a newer version of Sentinel Core can't be restored here - update this install first.");

        if (document.Settings?.PromptProfiles is { } profiles && !profiles.ContainsKey("default"))
            errors.Add("Backup's prompt profiles are missing a 'default' profile.");

        // Restore writes cameras, trigger zones and gate checks straight into their stores, so
        // until now it was the one way into this install that answered to none of the rules the
        // endpoints enforce. The same trigger zone that PUT /api/cameras/{id}/trigger-config
        // refuses with six separate errors - an object size of zero, a negative cooldown, a
        // polygon outside the frame, a night window running from hour 99 to hour -3 - imported
        // silently and left a camera that either never fires or never stops.
        //
        // None of that announces itself. A trigger zone with an impossible object size does not
        // log anything; it just quietly stops being a camera that watches. The stream URL is the
        // same story from the other direction: the endpoint allows rtsp/http/https and nothing
        // else, and ffmpeg is handed whatever is stored, so a file:// address restored from a
        // backup is a restriction that existed everywhere except here.
        //
        // Held to the endpoints' own validators rather than a second copy of the rules, because a
        // second copy is how the two drift apart - which is the whole shape of this bug.
        // The settings half of the same gap: ApplySnapshotAsync merges each section into
        // appsettings.json without the check that section's own PUT performs.
        if (document.Settings is { } settings)
        foreach (var problem in SettingsEndpoints.ValidateSnapshot(settings))
            errors.Add($"Settings - {problem}");

        foreach (var camera in document.Cameras ?? [])
        foreach (var problem in CameraEndpoints.Validate(ToInput(camera)))
            errors.Add($"Camera \"{camera.Name}\": {problem}");

        foreach (var zone in document.TriggerZones ?? [])
        foreach (var problem in TriggerEndpoints.Validate(
            ToDto(zone), zone.NightWindowEnabled, zone.NightStartHour, zone.NightEndHour))
            errors.Add($"Trigger zone for camera {zone.CameraId}: {problem}");

        foreach (var check in document.GateChecks ?? [])
        foreach (var problem in GateCheckEndpoints.Validate(ToInput(check)))
            errors.Add($"Gate check for camera {check.CameraId}: {problem}");

        return errors;
    }

    // Nothing is written when any of it fails: the file is rejected whole, with every problem
    // named. That is the same call the schema-version check above already makes, and for the same
    // reason - restoring most of a backup and saying nothing about the rest is worse than
    // refusing it, because the part that went missing is the part nobody will think to check.
    private static CameraInputDto ToInput(Camera camera) => new()
    {
        Name = camera.Name,
        StreamUrl = camera.StreamUrl,
        Username = camera.Username,
        Password = camera.Password,
        IncludeAudio = camera.IncludeAudio,
        PtzEnabled = camera.PtzEnabled,
        OnvifPtzServiceUrl = camera.OnvifPtzServiceUrl,
        OnvifProfileToken = camera.OnvifProfileToken,
        IsBrowserPublished = camera.IsBrowserPublished
    };

    private static GateCheckInputDto ToInput(GateCheckConfig check) => new()
    {
        Enabled = check.Enabled,
        CheckLabel = check.CheckLabel,
        CheckBlocked = check.CheckBlocked,
        IntervalMinutes = check.IntervalMinutes,
        RemindIfStillOpenAfterHours = check.RemindIfStillOpenAfterHours
    };

    private static TriggerConfigDto ToDto(TriggerConfig config) => new()
    {
        Enabled = config.Enabled,
        ZonePolygon = [.. config.ZonePolygon],
        MinTriggerArea = config.MinTriggerArea,
        Zones = config.Zones is null
            ? null
            : [.. config.Zones.Select(z => new TriggerZoneDto
            {
                Name = z.Name,
                Polygon = [.. z.Polygon],
                MinTriggerArea = z.MinTriggerArea
            })],
        CooldownSeconds = config.CooldownSeconds,
        MaxTriggersPerMinute = config.MaxTriggersPerMinute,
        ShadowSensitivity = config.ShadowSensitivity,
        RecordClips = config.RecordClips,
        ClipPreSeconds = config.ClipPreSeconds,
        ClipPostSeconds = config.ClipPostSeconds,
        SendToAlerts = config.SendToAlerts,
        AlertProfile = config.AlertProfile,
        SensorDeviceId = config.SensorDeviceId,
        SensorTriggerOn = config.SensorTriggerOn,
        SensorCooldownSeconds = config.SensorCooldownSeconds,
        SensorMeasurement = config.SensorMeasurement,
        SensorThreshold = config.SensorThreshold,
        NightWindowEnabled = config.NightWindowEnabled,
        NightStartHour = config.NightStartHour,
        NightEndHour = config.NightEndHour
    };
}
