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
// Since schema 4 it also carries the main app's Device Hub devices, automation rules and
// schedules (fetched from it over HTTP - see MainAppHubBackupClient), recording schedules,
// Internet Exposed Mode, and the organizer's calendar-feed tokens and preferences.
//
// The files configuration only points at - organizer attachment bytes, known-face photos, the
// licence, the HTTPS certificate authority and the HomeKit pairing - travel alongside this
// document in the full backup bundle (BackupBundle), which is what the nightly auto export
// writes. The plain JSON export stays as it was for the apps, which save and restore it as-is;
// after a JSON-only restore the server lists the attachments it's missing so the devices
// upload them again - see OrganizerStore.
//
// Still NOT included, deliberately:
//   - Events, captured images, recordings, and the "reviewed" marks on them: history, not
//     configuration, and large. An install restored from this file is your setup, not your past.
//   - Login sessions: restoring one onto another machine would be handing over a live login.
//   - Push device registrations: every app re-registers each time it connects, and a token
//     restored onto a different server is one that server may never have been told about.
//   - Runtime memory (a schedule's next-due time, a gate's last seen state, open package
//     watches): a restored install works these out afresh rather than inheriting another
//     machine's idea of what just happened.
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
    IReadOnlyList<LiveOrderEntry>? LiveOrder = null,
    // Added in schema 3: every Organizer space and item, clocks included, so a restore merges
    // by the same newest-edit-wins rule as a device sync and can't roll anything back.
    OrganizerBackup? Organizer = null,
    // Added in schema 4.
    IReadOnlyList<RecordingSchedule>? RecordingSchedules = null,
    HubBackup? Hub = null,
    InternetExposedSettingsDto? InternetExposed = null,
    // Added in schema 5: board automations (scheduled cards and card rules). They live in their
    // own file on the server and no device holds a copy, so this is their only way back.
    IReadOnlyList<OrganizerAutomation>? OrganizerAutomations = null,
    // Added in schema 6: the alarm's sensors and its settings (PIN hash included, like the
    // account password hashes above - this file is already that sensitive).
    AlarmBackup? Alarm = null,
    // What couldn't be included when this file was written, in words - e.g. the main app wasn't
    // running. Informational; restore ignores it.
    IReadOnlyList<string>? Warnings = null);

public sealed record AlarmBackup(IReadOnlyList<AlarmSensor> Sensors, AlarmConfig Config);

public static class BackupEndpoints
{
    private static readonly System.Text.Json.JsonSerializerOptions ImportOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

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
        backup.MapGet("/export", async (BackupService service, CancellationToken ct) =>
            Results.Ok(await service.BuildDocumentAsync(ct)));

        // The full bundle - the document plus attachments, photos, licence and certificates - as
        // a download. Assembled in the work directory first so a failure halfway is an error
        // response rather than a truncated file the browser saves as though it were fine.
        backup.MapPost("/bundle", async (
            BundleRequest? body, BackupService service, BackupWorkDirectory work, CancellationToken ct) =>
        {
            var passphrase = body?.Passphrase;
            if (!string.IsNullOrEmpty(passphrase) && passphrase.Length < 8)
                return Results.BadRequest(new { error = "Use a passphrase of at least 8 characters, or leave it blank." });

            var path = work.NewTemporaryFile();
            try
            {
                BackupService.BundleSummary summary;
                await using (var output = File.Create(path))
                    summary = await service.WriteBundleAsync(output, passphrase, ct);

                var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmm");
                var name = $"{AutoExportRunner.FilePrefix}{stamp}{(string.IsNullOrEmpty(passphrase) ? AutoExportRunner.ZipExtension : AutoExportRunner.EncryptedExtension)}";
                if (summary.Warnings.Count > 0)
                    app.Logger.LogWarning("Backup bundle written with warnings: {Warnings}", string.Join(" ", summary.Warnings));

                // DeleteOnClose: the file goes away when the response has been sent, or when the
                // client disconnects partway.
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                return Results.File(stream, "application/octet-stream", name);
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        });

        // Accepts any of the three shapes a backup comes in - the JSON export, the zip bundle, or
        // an encrypted bundle (passphrase in the X-Backup-Passphrase header, never the URL) - told
        // apart by their first bytes rather than the file name or content type, so a renamed
        // file still restores. Spooled to disk first: a bundle can be far larger than memory.
        backup.MapPost("/import", async (
            HttpRequest request, HttpContext http, BackupService service, BackupWorkDirectory work,
            bool? machineIdentity, CancellationToken ct) =>
        {
            var spooled = work.NewTemporaryFile();
            string? decrypted = null;
            try
            {
                await using (var output = File.Create(spooled))
                    await request.Body.CopyToAsync(output, ct);

                var head = new byte[16];
                int headLength;
                await using (var probe = File.OpenRead(spooled))
                    headLength = await probe.ReadAsync(head, ct);

                var format = BackupBundle.Detect(head.AsSpan(0, headLength));
                var zipPath = spooled;
                switch (format)
                {
                    case BackupBundle.Format.Json:
                    {
                        BackupDocument? document;
                        try
                        {
                            await using var input = File.OpenRead(spooled);
                            document = await System.Text.Json.JsonSerializer.DeserializeAsync<BackupDocument>(input, ImportOptions, ct);
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            return Results.BadRequest(new { errors = new[] { "That file doesn't look like a Sentinel Core backup - it isn't valid JSON." } });
                        }
                        return ToResult(await service.RestoreAsync(document, null, false, http.User, ct));
                    }

                    case BackupBundle.Format.Encrypted:
                    {
                        var passphrase = request.Headers["X-Backup-Passphrase"].ToString();
                        if (string.IsNullOrEmpty(passphrase))
                            return Results.BadRequest(new { errors = new[] { "That backup is encrypted - enter its passphrase to restore it." }, passphraseRequired = true });
                        decrypted = work.NewTemporaryFile();
                        await using (var input = File.OpenRead(spooled))
                        await using (var output = File.Create(decrypted))
                            await BackupBundle.DecryptAsync(input, output, passphrase, ct);
                        zipPath = decrypted;
                        goto case BackupBundle.Format.Zip;
                    }

                    case BackupBundle.Format.Zip:
                    {
                        await using var zipStream = File.OpenRead(zipPath);
                        var (document, archive) = await BackupBundle.OpenZipAsync(zipStream, ct);
                        using (archive)
                            return ToResult(await service.RestoreAsync(document, archive, machineIdentity ?? false, http.User, ct));
                    }

                    default:
                        return Results.BadRequest(new { errors = new[] { "That file isn't a Sentinel Core backup - expected a .json, .zip or .scbackup file." } });
                }
            }
            catch (BackupBundleException ex)
            {
                return Results.BadRequest(new { errors = new[] { ex.Message } });
            }
            finally
            {
                File.Delete(spooled);
                if (decrypted is not null) File.Delete(decrypted);
            }
        })
        // A full bundle with a household's attachments is well past Kestrel's 30 MB default. Admin
        // only, and spooled straight to disk, so lifting the cap costs nothing but disk.
        .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
    }

    public sealed record BundleRequest(string? Passphrase);

    private static IResult ToResult(BackupService.RestoreResult result) =>
        result.Errors is { Count: > 0 } errors ? Results.BadRequest(new { errors }) : Results.Ok(result.Counts);
}
