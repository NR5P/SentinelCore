using System.IO.Compression;
using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace SentinelCore.Web;

// Where backup bundles are assembled and unpacked. Under the data directory rather than the
// system temp folder on purpose: a bundle with a household's photo attachments can run to
// gigabytes, and /tmp on a small Linux box is often a RAM-backed tmpfs.
public sealed class BackupWorkDirectory(string path)
{
    public string Path { get; } = path;

    public string NewTemporaryFile()
    {
        Directory.CreateDirectory(Path);
        return System.IO.Path.Combine(Path, $"{Guid.NewGuid():N}.tmp");
    }

    // Leftovers from a process that died mid-export. Called at startup.
    public void Sweep()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var file in Directory.EnumerateFiles(Path, "*.tmp"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

// Builds, writes, checks and restores backups. BackupEndpoints (the dashboard's export and
// restore) and AutoExportRunner (the nightly file) both go through here, so the file the night
// writes and the file the button downloads can't come to differ.
public sealed class BackupService(
    AppSettingsFile settings,
    [FromKeyedServices("web")] AppSettingsFile webSettings,
    ICameraStore cameras,
    ICameraGroupStore cameraGroups,
    ITriggerConfigStore triggerConfigs,
    IGateCheckStore gateChecks,
    IAccountStore accounts,
    ILiveOrderStore liveOrder,
    IOrganizerStore organizer,
    OrganizerBlobStore blobs,
    IRecordingScheduleStore recordingSchedules,
    IOrganizerAutomationStore organizerAutomations,
    OrganizerAutomationEngine organizerAutomationEngine,
    IAlarmStore alarmStore,
    AlarmEngine alarmEngine,
    FamilyDriveStore familyDrives,
    MainAppHubBackupClient hub,
    IMediaStore media,
    MediaScanner mediaScanner,
    BackupWorkDirectory workDirectory,
    IOptions<BridgeOptions> bridgeOptions,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<BackupService> logger)
{
    // 2 added CameraGroups and GateChecks; 3 added Organizer; 4 added RecordingSchedules, Hub,
    // InternetExposed, Warnings, the organizer's feed tokens and preferences, and the settings
    // AutoExport section; 5 added OrganizerAutomations; 6 added Alarm, FamilyDrives and the
    // settings FamilyFiles section; 7 added Media (movie libraries and watched progress) and the
    // settings Media section. Bumping this does not invalidate older exports - see Validate,
    // which accepts anything up to this number.
    public const int CurrentSchemaVersion = 7;

    public sealed record BundleSummary(int FileCount, IReadOnlyList<string> Warnings);

    public async Task<BackupDocument> BuildDocumentAsync(CancellationToken ct)
    {
        var warnings = new List<string>();

        var hubExport = await hub.ExportAsync(ct);
        if (hubExport.Error is not null)
            warnings.Add($"Device Hub devices, automation rules and schedules are missing from this backup: {hubExport.Error}.");

        return new BackupDocument(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            await SettingsEndpoints.BuildSnapshotAsync(settings, ct),
            await cameras.GetAllAsync(ct),
            await triggerConfigs.GetAllAsync(ct),
            await cameraGroups.GetAllAsync(ct),
            [.. (await gateChecks.GetAllAsync(ct)).Select(WithoutTransitionMemory)],
            await accounts.GetAllAsync(ct),
            await liveOrder.GetAllAsync(ct),
            await organizer.ExportAsync(ct),
            [.. (await recordingSchedules.GetAllAsync(ct)).Select(WithoutRunMemory)],
            hubExport.Value,
            await webSettings.GetSectionAsync<InternetExposedSettingsDto>("InternetExposed", ct),
            await organizerAutomations.GetAllAsync(ct),
            new AlarmBackup(await alarmStore.GetSensorsAsync(ct), await alarmStore.GetConfigAsync(ct)),
            await familyDrives.GetAllAsync(ct),
            new MediaBackup(await media.GetLibrariesAsync(ct), await media.AllProgressAsync(ct)),
            warnings.Count > 0 ? warnings : null);
    }

    // The full bundle: the document plus every file it points at, optionally encrypted.
    public async Task<BundleSummary> WriteBundleAsync(Stream output, string? passphrase, CancellationToken ct)
    {
        var document = await BuildDocumentAsync(ct);
        var (files, fileWarnings) = await CollectFilesAsync(ct);

        IReadOnlyList<string> warnings = [.. document.Warnings ?? [], .. fileWarnings];
        document = document with { Warnings = warnings.Count > 0 ? warnings : null };

        if (string.IsNullOrEmpty(passphrase))
        {
            await BackupBundle.WriteZipAsync(output, document, files, ct);
        }
        else
        {
            var zipPath = workDirectory.NewTemporaryFile();
            try
            {
                await using (var zip = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    await BackupBundle.WriteZipAsync(zip, document, files, ct);
                    zip.Position = 0;
                    await BackupBundle.EncryptAsync(zip, output, passphrase, ct);
                }
            }
            finally
            {
                File.Delete(zipPath);
            }
        }

        return new BundleSummary(files.Count, warnings);
    }

    // Reads a written bundle back end to end: decrypts it if it is encrypted, parses backup.json,
    // and reads every file entry to the end so the zip's CRCs are checked - a file that fails any
    // of that throws BackupBundleException. It also runs the document through the checks a restore
    // makes, and returns what they'd refuse: data already in this install that today's rules
    // reject. That doesn't stop the file being kept - a backup restore would refuse is still far
    // better than none - but the owner has to hear about it, because it means restoring needs a
    // fix first.
    public async Task<IReadOnlyList<string>> VerifyBundleAsync(string path, string? passphrase, CancellationToken ct)
    {
        string? decrypted = null;
        try
        {
            var zipPath = path;
            if (!string.IsNullOrEmpty(passphrase))
            {
                decrypted = workDirectory.NewTemporaryFile();
                await using var input = File.OpenRead(path);
                await using var output = File.Create(decrypted);
                await BackupBundle.DecryptAsync(input, output, passphrase, ct);
                zipPath = decrypted;
            }

            await using var zipStream = File.OpenRead(zipPath);
            var (document, archive) = await BackupBundle.OpenZipAsync(zipStream, ct);
            using (archive)
            {
                var buffer = new byte[81920];
                foreach (var entry in archive.Entries)
                {
                    await using var stream = entry.Open();
                    while (await stream.ReadAsync(buffer, ct) > 0) { }
                }

                var problems = Validate(document);
                return problems.Count == 0
                    ? []
                    : [$"A restore of this backup would be refused until this is fixed here: {string.Join(" ", problems)}"];
            }
        }
        catch (InvalidDataException ex)
        {
            throw new BackupBundleException($"The export was written but reading it back failed: {ex.Message}");
        }
        finally
        {
            if (decrypted is not null) File.Delete(decrypted);
        }
    }

    // ---- files ------------------------------------------------------------------------------

    private async Task<(List<BackupFile> Files, List<string> Warnings)> CollectFilesAsync(CancellationToken ct)
    {
        var files = new List<BackupFile>();
        var warnings = new List<string>();

        // Organizer attachments. A hash with no bytes here is an attachment a device made but
        // never finished uploading; the devices still hold it and will send it when they next
        // sync, so it's noted rather than treated as a failure.
        var missingAttachments = 0;
        foreach (var hash in (await organizer.GetAllLiveBlobHashesAsync(ct)).Order(StringComparer.Ordinal))
        {
            if (!OrganizerBlobStore.IsValidHash(hash)) continue;
            var path = blobs.PathFor(hash);
            if (File.Exists(path)) files.Add(FromDisk($"{BackupFileKinds.OrganizerBlob}/{hash.ToLowerInvariant()}", path));
            else missingAttachments++;
        }
        if (missingAttachments > 0)
            warnings.Add($"{missingAttachments} Organizer attachment(s) haven't finished uploading from the device they were added on, so only their descriptions are in this backup.");

        // Known-face reference photos. Recognition itself doesn't need them - the embeddings are
        // in the settings - but without them the Known Faces screen shows a row of broken images.
        var dataRoot = Path.GetFullPath(KnownFaceEndpoints.ResolveDataRoot(bridgeOptions.Value, environment));
        foreach (var face in await settings.GetSectionAsync<List<KnownFace>>("KnownFaces", ct) ?? [])
        foreach (var enrollment in face.Enrollments)
        {
            var relative = enrollment.ImagePath.Replace('\\', '/');
            if (!BackupBundle.IsSafeRelativePath(relative)) continue;
            var path = Path.Combine(dataRoot, relative);
            if (File.Exists(path)) files.Add(FromDisk($"{BackupFileKinds.KnownFacePhoto}/{relative}", path));
        }

        var licensePath = await LicenseEndpoints.ResolveLicensePathAsync(settings, configuration, environment, ct);
        if (File.Exists(licensePath)) files.Add(FromDisk($"{BackupFileKinds.License}/license.lic", licensePath));

        // The certificate authority every device was told to trust. Without it a rebuilt server
        // has a new one and every phone, tablet and browser has to be taken through trusting it
        // again. Only an authority this server made itself (the key sits beside it) - a real
        // domain certificate has none, and one set up by hand is left to whoever set it up.
        if (AutoCertificateAuthority() is { } authority)
        {
            files.Add(FromDisk($"{BackupFileKinds.CertificateAuthority}/ca.crt", authority.CertPath));
            files.Add(FromDisk($"{BackupFileKinds.CertificateAuthority}/ca.key", authority.KeyPath));
        }

        // The HomeKit sidecar's pairing identity. Without it the bridge comes back as a new
        // accessory and has to be removed and re-added in the Home app, losing its rooms and
        // automations there.
        var homeKitDirectory = HomeKitPersistDirectory();
        if (Directory.Exists(homeKitDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(homeKitDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(homeKitDirectory, path).Replace('\\', '/');
                if (BackupBundle.IsSafeRelativePath(relative))
                    files.Add(FromDisk($"{BackupFileKinds.HomeKit}/{relative}", path));
            }
        }

        return (files, warnings);
    }

    private static BackupFile FromDisk(string entryName, string path) =>
        new(entryName,
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            new DateTimeOffset(File.GetLastWriteTime(path)));

    private (string CertPath, string KeyPath, string Directory)? AutoCertificateAuthority()
    {
        var certPath = configuration[CertificateAuthorityEndpoints.ConfigurationKey];
        if (string.IsNullOrWhiteSpace(certPath)) return null;

        var directory = Path.GetDirectoryName(Path.GetFullPath(certPath))!;
        var autoDirectory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "certs"));
        var keyPath = Path.Combine(directory, "ca.key");
        if (!string.Equals(directory, autoDirectory, StringComparison.Ordinal) || !File.Exists(certPath) || !File.Exists(keyPath))
            return null;
        return (Path.GetFullPath(certPath), keyPath, directory);
    }

    private string HomeKitPersistDirectory() =>
        configuration["HomeKitPersistDirectory"] is { Length: > 0 } configured
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "homekit-bridge", "persist"));

    // ---- restore ----------------------------------------------------------------------------

    public sealed record RestoreResult(IReadOnlyList<string>? Errors, object? Counts);

    // Settings first, then cameras, then everything that points at a camera by Id - trigger
    // zones, camera groups, gate checks and recording schedules all do, so the cameras have to
    // exist first. Cameras and groups are restored via RestoreAsync, which (unlike AddAsync) keeps
    // the original Id instead of minting a new one, so those references still resolve, and so
    // importing the same file twice updates what it created rather than duplicating it. Nothing
    // in the current setup that isn't mentioned in the backup is touched or removed - restore
    // overlays, it never wipes.
    //
    // `archive` is the bundle's zip when restoring a full backup, null for a plain JSON file.
    // `restoreMachineIdentity` says whether this machine's HTTPS certificate authority and HomeKit
    // pairing may be replaced with the backup's - right when rebuilding a lost server, wrong when
    // rolling back data on one that is running fine.
    public async Task<RestoreResult> RestoreAsync(
        BackupDocument? document, ZipArchive? archive, bool restoreMachineIdentity, ClaimsPrincipal user, CancellationToken ct)
    {
        var errors = Validate(document);
        if (errors.Count > 0) return new RestoreResult(errors, null);

        // Asked before anything is written, so a section the main app would refuse - or a main
        // app that isn't running - fails the whole restore rather than half of it.
        if (document!.Hub is { } hubSection && HasContent(hubSection))
        {
            var hubErrors = await hub.ValidateAsync(hubSection, ct);
            if (hubErrors.Count > 0) return new RestoreResult([.. hubErrors.Select(e => $"Device Hub: {e}")], null);
        }

        var notes = new List<string>();

        if (document.Settings is not null)
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

        // Same shape as gate checks: SaveAsync's upsert leaves the last-run columns alone on a
        // schedule that already exists here.
        var recordingSchedulesRestored = 0;
        foreach (var schedule in document.RecordingSchedules ?? [])
        {
            await recordingSchedules.SaveAsync(WithoutRunMemory(schedule), ct);
            recordingSchedulesRestored++;
        }

        // The account performing this restore is skipped, always. Everything else here
        // overlays; accounts are the one thing that can lock the person out of the install
        // they are in the middle of restoring - a backup whose admin password nobody
        // remembers any more would otherwise replace the working login with it, and there is
        // no way back from that through the dashboard. Skipping by both id and username
        // covers the two ways "the same person" can appear: a re-import onto this machine
        // (same id) and a restore onto a fresh one where setup minted a new id for the same
        // name. Whoever is signed in keeps their password; every other account comes back.
        var currentAccountId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var currentUsername = user.Identity?.Name;

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

        var organizerItemsRestored = document.Organizer is { } organizerBackup
            ? await organizer.ImportAsync(organizerBackup, ct)
            : 0;

        // After the boards they belong to. Same id, so importing twice replaces rather than
        // duplicates; saved through the engine so each one's next run is worked out afresh here.
        var organizerAutomationsRestored = 0;
        foreach (var rule in document.OrganizerAutomations ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) continue;
            await organizerAutomationEngine.SaveAsync(rule, ct);
            organizerAutomationsRestored++;
        }

        // Straight into the store, then the engine is told to re-read: same ids, so a re-import
        // replaces rather than duplicates. The alarm's armed/disarmed state is not in the file -
        // a restored server comes up disarmed.
        var alarmSensorsRestored = 0;
        if (document.Alarm is { } alarmBackup)
        {
            foreach (var sensor in alarmBackup.Sensors ?? [])
            {
                if (sensor.Id == Guid.Empty || sensor.DeviceId == Guid.Empty) continue;
                await alarmStore.SaveSensorAsync(sensor, ct);
                alarmSensorsRestored++;
            }
            if (alarmBackup.Config is { } alarmConfig) await alarmStore.SaveConfigAsync(alarmConfig, ct);
            alarmEngine.Invalidate();
        }

        // Same id, so a second restore replaces rather than adds a twin of every drive.
        var familyDrivesRestored = 0;
        foreach (var drive in document.FamilyDrives ?? [])
        {
            if (string.IsNullOrWhiteSpace(drive.Id)) continue;
            await familyDrives.UpsertAsync(drive.Normalized(), ct);
            familyDrivesRestored++;
        }

        // Movies & TV: the libraries keep their ids, and since item ids are derived from the
        // library id and the file's path, the rescan below lands on the same ids the progress
        // rows point at. Progress merges newest-wins, so an old backup never un-watches anything.
        var mediaLibrariesRestored = 0;
        var mediaProgressRestored = 0;
        if (document.Media is { } mediaBackup)
        {
            var existing = (await media.GetLibrariesAsync(ct)).ToDictionary(l => l.Id);
            foreach (var library in mediaBackup.Libraries ?? [])
            {
                if (library.Id == Guid.Empty || string.IsNullOrWhiteSpace(library.Name) || !MediaLibraryKinds.IsValid(library.Kind)) continue;
                var keep = existing.GetValueOrDefault(library.Id);
                await media.SaveLibraryAsync(library with
                {
                    LastScanStartedAt = keep?.LastScanStartedAt,
                    LastScanFinishedAt = keep?.LastScanFinishedAt,
                    LastScanError = keep?.LastScanError,
                    ItemCount = keep?.ItemCount ?? 0
                }, ct);
                mediaLibrariesRestored++;
            }
            mediaProgressRestored = await media.RestoreProgressAsync(
                (mediaBackup.Progress ?? []).Where(p => p.ItemId != Guid.Empty && p.UserKey is not null), ct);
            if (mediaLibrariesRestored > 0)
            {
                mediaScanner.RequestScan(null);
                if (mediaBackup.Libraries!.Any(l => !Directory.Exists(l.Path)))
                    notes.Add("Some movie library folders don't exist on this machine yet. Mount them at the same paths (or edit the library) and they will be scanned.");
            }
        }

        // Written last among the stores because it's the one part that can still fail after
        // validation passed (the main app stopping mid-restore). Everything above has landed by
        // then, so it's reported as a note on a restore that otherwise worked, not as a failure
        // that would suggest nothing happened.
        HubRestoreCounts? hubCounts = null;
        if (document.Hub is { } hubBackup && HasContent(hubBackup))
        {
            var restored = await hub.RestoreAsync(hubBackup, ct);
            if (restored.Error is not null) notes.Add(restored.Error);
            hubCounts = restored.Value;
        }

        // Only when this machine could actually run it: Internet Exposed Mode depends on this
        // machine's HTTPS certificate, which may not exist on a freshly built one.
        if (document.InternetExposed is { } exposed)
        {
            var exposedErrors = await SettingsEndpoints.ValidateInternetExposedAsync(exposed, configuration, accounts, ct);
            if (exposedErrors.Count == 0)
            {
                var current = await webSettings.GetSectionAsync<InternetExposedSettingsDto>("InternetExposed", ct);
                if (current is null || current.Enabled != exposed.Enabled || current.Port != exposed.Port)
                {
                    await webSettings.MergeSectionAsync("InternetExposed", exposed, ct);
                    notes.Add("Internet Exposed Mode was restored - restart the dashboard for it to take effect.");
                }
            }
            else
            {
                notes.Add("Internet Exposed Mode wasn't restored: " + string.Join(" ", exposedErrors));
            }
        }

        var files = archive is null
            ? null
            : await RestoreFilesAsync(archive, restoreMachineIdentity, notes, ct);

        foreach (var note in notes) logger.LogInformation("Backup restore: {Note}", note);

        return new RestoreResult(null, new
        {
            success = true,
            camerasRestored,
            triggerZonesRestored,
            cameraGroupsRestored,
            gateChecksRestored,
            recordingSchedulesRestored,
            accountsRestored,
            accountsSkipped,
            liveOrderRestored,
            organizerItemsRestored,
            organizerAutomationsRestored,
            alarmSensorsRestored,
            familyDrivesRestored,
            mediaLibrariesRestored,
            mediaProgressRestored,
            devicesRestored = hubCounts?.DevicesRestored ?? 0,
            automationRulesRestored = hubCounts?.RulesRestored ?? 0,
            schedulesRestored = hubCounts?.SchedulesRestored ?? 0,
            attachmentsRestored = files?.Attachments ?? 0,
            knownFacePhotosRestored = files?.FacePhotos ?? 0,
            licenseRestored = files?.License ?? false,
            certificateAuthorityRestored = files?.CertificateAuthority ?? false,
            homeKitRestored = files?.HomeKit ?? false,
            notes
        });
    }

    private sealed record FileCounts(int Attachments, int FacePhotos, bool License, bool CertificateAuthority, bool HomeKit);

    // Files only ever fill gaps, apart from the two machine-identity sets, which replace what is
    // here when - and only when - the caller asked for it.
    private async Task<FileCounts> RestoreFilesAsync(ZipArchive archive, bool restoreMachineIdentity, List<string> notes, CancellationToken ct)
    {
        var attachments = 0;
        var facePhotos = 0;
        var license = false;
        var homeKitFiles = new List<(string Relative, ZipArchiveEntry Entry)>();
        ZipArchiveEntry? caCert = null, caKey = null;

        var dataRoot = Path.GetFullPath(KnownFaceEndpoints.ResolveDataRoot(bridgeOptions.Value, environment));

        foreach (var (kind, relative, entry) in BackupBundle.Files(archive))
        {
            switch (kind)
            {
                case BackupFileKinds.OrganizerBlob:
                    if (!OrganizerBlobStore.IsValidHash(relative) || blobs.Exists(relative)) break;
                    await using (var stream = entry.Open())
                    {
                        // SaveAsync keeps it only if it hashes to its name, so a damaged entry
                        // can't become an attachment that shows the wrong picture.
                        if (await blobs.SaveAsync(relative, stream, long.MaxValue, ct)) attachments++;
                        else notes.Add($"Attachment {relative[..12]}… in the backup was damaged and wasn't restored.");
                    }
                    break;

                case BackupFileKinds.KnownFacePhoto:
                    var photoPath = ContainedPath(dataRoot, relative);
                    if (photoPath is null || File.Exists(photoPath)) break;
                    await WriteEntryAsync(entry, photoPath, ct);
                    facePhotos++;
                    break;

                case BackupFileKinds.License when relative == "license.lic":
                    license = await RestoreLicenseAsync(entry, notes, ct);
                    break;

                case BackupFileKinds.CertificateAuthority when relative == "ca.crt":
                    caCert = entry;
                    break;

                case BackupFileKinds.CertificateAuthority when relative == "ca.key":
                    caKey = entry;
                    break;

                case BackupFileKinds.HomeKit:
                    homeKitFiles.Add((relative, entry));
                    break;
            }
        }

        var certificateAuthority = false;
        var homeKit = false;
        if (restoreMachineIdentity)
        {
            if (caCert is not null && caKey is not null)
                certificateAuthority = await RestoreCertificateAuthorityAsync(caCert, caKey, notes, ct);
            if (homeKitFiles.Count > 0)
                homeKit = await RestoreHomeKitAsync(homeKitFiles, notes, ct);
        }
        else if (caCert is not null || homeKitFiles.Count > 0)
        {
            notes.Add("This backup has the old server's HTTPS certificate authority and HomeKit pairing; they were left alone. " +
                "Restore again with 'this is a replacement server' ticked to bring them back.");
        }

        return new FileCounts(attachments, facePhotos, license, certificateAuthority, homeKit);
    }

    // Only onto an install with no working licence: one that has its own may have a newer one.
    private async Task<bool> RestoreLicenseAsync(ZipArchiveEntry entry, List<string> notes, CancellationToken ct)
    {
        var path = await LicenseEndpoints.ResolveLicensePathAsync(settings, configuration, environment, ct);
        if (LicenseStatus.Check(path) is not null) return false;

        string contents;
        using (var reader = new StreamReader(entry.Open()))
            contents = await reader.ReadToEndAsync(ct);

        if (LicenseStatus.Verify(contents) is null)
        {
            notes.Add("The licence in the backup isn't valid, so it wasn't restored.");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, ct);
        return true;
    }

    private async Task<bool> RestoreCertificateAuthorityAsync(
        ZipArchiveEntry certEntry, ZipArchiveEntry keyEntry, List<string> notes, CancellationToken ct)
    {
        if (AutoCertificateAuthority() is not { } current)
        {
            notes.Add("This server's HTTPS certificate is set up by hand (or HTTPS is off), so the backup's certificate authority wasn't restored.");
            return false;
        }

        var cert = await ReadAllBytesAsync(certEntry, ct);
        var key = await ReadAllBytesAsync(keyEntry, ct);
        var currentCert = await File.ReadAllBytesAsync(current.CertPath, ct);
        var currentKey = await File.ReadAllBytesAsync(current.KeyPath, ct);
        if (cert.AsSpan().SequenceEqual(currentCert) && key.AsSpan().SequenceEqual(currentKey))
            return false;

        // A mismatched pair would leave the server unable to start HTTPS at all, so the two are
        // checked against each other before either is written.
        try
        {
            using var parsed = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                System.Text.Encoding.UTF8.GetString(cert), System.Text.Encoding.UTF8.GetString(key));
            if (!parsed.HasPrivateKey) throw new System.Security.Cryptography.CryptographicException();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            notes.Add("The certificate authority in the backup is damaged, so it wasn't restored.");
            return false;
        }

        await File.WriteAllBytesAsync(current.KeyPath, key, ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(current.KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllBytesAsync(current.CertPath, cert, ct);

        // The server certificate was signed by the authority just replaced. Removing it makes
        // AutoCertificateProvisioner issue a fresh one from the restored authority at the next
        // start; the running process keeps serving the old one until then.
        foreach (var name in new[] { "server.crt", "server.key" })
        {
            var path = Path.Combine(current.Directory, name);
            if (File.Exists(path)) File.Delete(path);
        }

        notes.Add("The old server's HTTPS certificate authority was restored - restart the dashboard, and devices that trusted the old server will trust this one.");
        return true;
    }

    private async Task<bool> RestoreHomeKitAsync(
        List<(string Relative, ZipArchiveEntry Entry)> files, List<string> notes, CancellationToken ct)
    {
        var directory = HomeKitPersistDirectory();
        var changed = false;
        foreach (var (relative, entry) in files)
        {
            var path = ContainedPath(directory, relative);
            if (path is null) continue;
            var bytes = await ReadAllBytesAsync(entry, ct);
            var existing = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
            if (existing is not null && bytes.AsSpan().SequenceEqual(existing)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
            changed = true;
        }

        if (changed)
            notes.Add("The HomeKit pairing was restored - restart the HomeKit service (sentinelcore-homekit) and the Home app will find the bridge it already knows.");
        return changed;
    }

    // Null when `relative` would land outside `root` - belt and braces on top of
    // BackupBundle.IsSafeRelativePath, since this is the line that actually writes.
    private static string? ContainedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine([fullRoot, .. relative.Split('/')]));
        return path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : null;
    }

    private static async Task WriteEntryAsync(ZipArchiveEntry entry, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var input = entry.Open();
        await using var output = File.Create(path);
        await input.CopyToAsync(output, ct);
    }

    private static async Task<byte[]> ReadAllBytesAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var input = entry.Open();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static bool HasContent(HubBackup backup) =>
        backup.Devices is { Count: > 0 } || backup.AutomationRules is { Count: > 0 } || backup.Schedules is { Count: > 0 };

    // ---- validation -------------------------------------------------------------------------

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

    // Likewise for a recording schedule: when it last ran, and how that went, belong to this
    // machine's history.
    private static RecordingSchedule WithoutRunMemory(RecordingSchedule schedule) =>
        schedule with { LastStartedUtc = null, LastStatus = string.Empty, LastDetail = string.Empty };

    public static List<string> Validate(BackupDocument? document)
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
        if (document.Settings is { } settingsSnapshot)
        foreach (var problem in SettingsEndpoints.ValidateSnapshot(settingsSnapshot))
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

        // Judged against the cameras and groups the file itself carries, not the ones already
        // here: a restore onto a rebuilt machine writes all three in one pass, so the cameras a
        // group names are usually arriving in the same document rather than existing yet.
        //
        // The one-group-per-camera rule matters more than it looks. GetGroupForCameraAsync
        // resolves a camera's group with FirstOrDefault, so two groups claiming the same camera
        // means whichever row comes back first wins - and the other group's companion cameras
        // quietly never record. That is the ambiguity the endpoint's check exists to prevent, and
        // restore was the way around it.
        var knownCameraIds = (document.Cameras ?? []).Select(c => c.Id).ToHashSet();
        var groups = document.CameraGroups ?? [];
        foreach (var group in groups)
        foreach (var problem in CameraGroupEndpoints.Validate(
            ToInput(group), group.Id, knownCameraIds, [.. groups.Where(other => other.Id != group.Id)]))
            errors.Add($"Camera group \"{group.Name}\": {problem}");

        // A recording schedule for a camera the file doesn't carry would be a schedule that
        // starts ffmpeg against nothing every night, so it's refused along with the rest.
        foreach (var schedule in document.RecordingSchedules ?? [])
        {
            if (!knownCameraIds.Contains(schedule.CameraId))
                errors.Add($"Recording schedule \"{schedule.Name}\": its camera isn't in this backup.");
            foreach (var problem in RecordingEndpoints.Validate(ToInput(schedule), out _))
                errors.Add($"Recording schedule \"{schedule.Name}\": {problem}");
        }

        // Accounts and live-order rows are small enough to check inline; neither has an endpoint
        // whose validator could be borrowed. An account with no username cannot be signed in to
        // and cannot be told apart in the accounts list, and a role outside the enum renders as a
        // bare number and matches no authorization policy - it is neither an admin nor a viewer.
        foreach (var account in document.Accounts ?? [])
        {
            if (string.IsNullOrWhiteSpace(account.Username))
                errors.Add($"Account {account.Id}: username is required.");
            if (!Enum.IsDefined(account.Role))
                errors.Add($"Account \"{account.Username}\": role {(int)account.Role} is neither Admin nor Viewer.");
        }

        // OwnerKey is the primary key of the row this becomes. It is declared non-nullable, which
        // means nothing to the deserializer, so a file missing the field reached SQLite as a null
        // parameter and threw there - a 500 with a stack trace, raised after the cameras, groups
        // and accounts above had already been written and could not be taken back.
        foreach (var entry in document.LiveOrder ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.OwnerKey))
                errors.Add("Live grid order: an entry has no owner key.");
        }

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

    private static CameraGroupInputDto ToInput(CameraGroup group) => new()
    {
        Name = group.Name,
        CameraIds = [.. group.CameraIds],
        CorrelationWindowSeconds = group.CorrelationWindowSeconds,
        RecordCompanionClips = group.RecordCompanionClips,
        AnalyzeCompanionCaptures = group.AnalyzeCompanionCaptures
    };

    private static GateCheckInputDto ToInput(GateCheckConfig check) => new()
    {
        Enabled = check.Enabled,
        CheckLabel = check.CheckLabel,
        CheckBlocked = check.CheckBlocked,
        IntervalMinutes = check.IntervalMinutes,
        RemindIfStillOpenAfterHours = check.RemindIfStillOpenAfterHours
    };

    private static RecordingScheduleDto ToInput(RecordingSchedule schedule) => new(
        schedule.Id,
        schedule.Name,
        schedule.Enabled,
        schedule.Kind,
        schedule.TimeOfDay,
        StopTime: string.Empty,
        schedule.DaysOfWeek,
        schedule.CronExpression,
        schedule.RunAtUtc,
        schedule.SolarEvent,
        schedule.SolarOffsetMinutes,
        schedule.SolarElevationDegrees,
        schedule.DurationSeconds,
        schedule.IncludeAudio);

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
        NightEndHour = config.NightEndHour,
        BypassFastLocal = config.BypassFastLocal,
        KeepOnlyAlertable = config.KeepOnlyAlertable
    };
}
