using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SentinelCore.Web;

// NIGHTLY AUTO EXPORT: writes the full backup bundle (BackupService.WriteBundleAsync) to a folder
// or an SFTP server once a night, keeps the last N, and tells the owner when a night fails.
//
// The file is written somewhere private first, read back in full to prove it restores, and only
// then delivered under a temporary name and renamed into place - so what the destination holds is
// either a complete, checked backup or nothing, never half a file that looks like the latest one.
// Retention runs after a successful delivery only, and only ever deletes files matching this
// feature's own name pattern.

// What the dashboard shows about the last run, and what the worker remembers between restarts.
public sealed record AutoExportState
{
    // When the worker first saw the feature switched on. Occurrences before this don't count, so
    // enabling it at 2pm with a 3am schedule doesn't export on the spot - it waits for 3am, as the
    // settings form says. Cleared when the feature is switched off.
    public DateTimeOffset? ArmedAtUtc { get; init; }

    // The scheduled occurrence the last nightly attempt was for, and how many tries it has had -
    // so a failure is retried hourly a few times, and a machine that was off at 3am catches up
    // when it comes back instead of skipping the night.
    public DateTimeOffset? LastOccurrenceUtc { get; init; }
    public int AttemptsForOccurrence { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }

    // "ok", "incomplete" (written, but something couldn't be included - see Warnings) or "failed".
    public string LastResult { get; init; } = "";
    public string LastMessage { get; init; } = "";
    public string LastTrigger { get; init; } = "";
    public string? LastLocation { get; init; }
    public long? LastBytes { get; init; }
    public IReadOnlyList<string> LastWarnings { get; init; } = [];
    public string? LastHostKeyFingerprint { get; init; }
    public DateTimeOffset? LastProblemNotifiedUtc { get; init; }
}

public sealed class AutoExportStateFile(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<AutoExportState> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return new AutoExportState();
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AutoExportState>(stream, Options, ct) ?? new AutoExportState();
        }
        catch (JsonException)
        {
            // A damaged state file only costs the history shown in the dashboard; the next run
            // rewrites it.
            return new AutoExportState();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AutoExportState state, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, state, Options, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}

public static class AutoExportSchedule
{
    public const int MaxAttemptsPerNight = 6;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    // The most recent HH:mm at or before `nowUtc`, in `zone`, as an instant. A spring-forward gap
    // (a 2:30 that doesn't exist) resolves to the first valid minute after it.
    public static DateTimeOffset LatestOccurrence(int hour, int minute, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var candidate = At(localNow.Date, hour, minute, zone);
        return candidate <= nowUtc ? candidate : At(localNow.Date.AddDays(-1), hour, minute, zone);
    }

    public static DateTimeOffset NextOccurrence(int hour, int minute, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var candidate = At(localNow.Date, hour, minute, zone);
        return candidate > nowUtc ? candidate : At(localNow.Date.AddDays(1), hour, minute, zone);
    }

    private static DateTimeOffset At(DateTime date, int hour, int minute, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    // Null when nothing should run now; otherwise the occurrence to run for.
    public static DateTimeOffset? Due(AutoExportSettingsDto options, AutoExportState state, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (!options.Enabled || state.ArmedAtUtc is not { } armedAt) return null;

        var occurrence = LatestOccurrence(options.Hour, options.Minute, nowUtc, zone);
        if (occurrence <= armedAt) return null;

        if (state.LastOccurrenceUtc != occurrence) return occurrence;

        var retry = state.LastResult == "failed"
            && state.AttemptsForOccurrence < MaxAttemptsPerNight
            && state.LastAttemptUtc is { } last && nowUtc - last >= RetryAfter;
        return retry ? occurrence : null;
    }
}

public sealed class AutoExportRunner(
    BackupService backup,
    AppSettingsFile settings,
    AutoExportStateFile stateFile,
    BackupWorkDirectory workDirectory,
    OwnerNotifier notifier,
    ILogger<AutoExportRunner> logger)
{
    public const string FilePrefix = "sentinelcore-backup-";
    public const string ZipExtension = ".zip";
    public const string EncryptedExtension = ".scbackup";

    private static readonly Regex OwnFileName = new(
        @"^sentinelcore-backup-\d{4}-\d{2}-\d{2}-\d{4}(-\d+)?\.(zip|scbackup)$", RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _running = new(1, 1);

    // Auto-update holds off while this is true rather than restart the server mid-export.
    public bool IsRunning => _running.CurrentCount == 0;

    public static bool IsOwnFile(string name) => OwnFileName.IsMatch(name);

    // `occurrence` is the nightly slot being run for; null for "export now".
    public async Task<AutoExportState> RunAsync(DateTimeOffset? occurrence, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct))
            return (await stateFile.LoadAsync(ct)) with { LastResult = "failed", LastMessage = "An export is already running - try again when it finishes." };

        var started = DateTimeOffset.UtcNow;
        var trigger = occurrence is null ? "manual" : "nightly";
        var temporary = workDirectory.NewTemporaryFile();
        string? hostKey = null;
        AutoExportState outcome;

        try
        {
            var options = await settings.GetSectionAsync<AutoExportSettingsDto>(AutoExportSettingsDto.SectionName, ct)
                ?? new AutoExportSettingsDto();
            options.Destination = options.Destination?.Trim().ToLowerInvariant() ?? AutoExportSettingsDto.DestinationFolder;

            // Judged as though switched on, so "export now" with a half-filled form says what's
            // missing rather than trying and failing somewhere less helpful.
            var problems = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var probe = CloneEnabled(options);
            System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                probe, new System.ComponentModel.DataAnnotations.ValidationContext(probe), problems, validateAllProperties: true);
            if (problems.Count > 0)
                throw new AutoExportException(string.Join(" ", problems.Select(p => p.ErrorMessage)));

            var passphrase = string.IsNullOrEmpty(options.Passphrase) ? null : options.Passphrase;
            var zone = RecordingScheduleWorker.ResolveTimeZone(
                await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, ct));
            var local = TimeZoneInfo.ConvertTime(started, zone);
            var fileName = $"{FilePrefix}{local:yyyy-MM-dd-HHmm}{(passphrase is null ? ZipExtension : EncryptedExtension)}";

            BackupService.BundleSummary summary;
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                summary = await backup.WriteBundleAsync(output, passphrase, ct);

            // Read back in full before it goes anywhere: a file that can't be restored is worse
            // than no file, because it replaces a good one in the rotation.
            var restoreProblems = await backup.VerifyBundleAsync(temporary, passphrase, ct);
            IReadOnlyList<string> warnings = [.. summary.Warnings, .. restoreProblems];
            var bytes = new FileInfo(temporary).Length;

            string location;
            if (options.Destination == AutoExportSettingsDto.DestinationSftp)
            {
                (location, hostKey) = await AutoExportSftp.DeliverAsync(options, temporary, fileName, bytes, ct);
            }
            else
            {
                location = await AutoExportFolder.DeliverAsync(options.FolderPath.Trim(), temporary, fileName, bytes, ct);
            }

            var pruned = options.Destination == AutoExportSettingsDto.DestinationSftp
                ? await AutoExportSftp.PruneAsync(options, options.KeepCount, ct)
                : AutoExportFolder.Prune(options.FolderPath.Trim(), options.KeepCount);

            var incomplete = warnings.Count > 0;
            var message = $"Saved {fileName} ({FormatBytes(bytes)}, {summary.FileCount} attached file(s)) to {location}." +
                (pruned > 0 ? $" Removed {pruned} older export(s)." : "") +
                (incomplete ? " Some things could not be included - see below." : "");

            outcome = (await stateFile.LoadAsync(ct)) with
            {
                LastAttemptUtc = started,
                LastSuccessUtc = started,
                LastResult = incomplete ? "incomplete" : "ok",
                LastMessage = message,
                LastTrigger = trigger,
                LastLocation = location,
                LastBytes = bytes,
                LastWarnings = warnings,
                LastHostKeyFingerprint = hostKey
            };
            logger.LogInformation("Auto export ({Trigger}) wrote {File} ({Bytes} bytes) to {Location}", trigger, fileName, bytes, location);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = ex switch
            {
                AutoExportException or BackupBundleException => ex.Message,
                SshAuthenticationException => "The SFTP server refused the username or password.",
                SshConnectionException or System.Net.Sockets.SocketException => $"Could not connect to the SFTP server: {ex.Message}",
                SftpPermissionDeniedException => $"The SFTP server refused to let this account write there: {ex.Message}",
                UnauthorizedAccessException => $"No permission to write to the export folder: {ex.Message}",
                IOException => $"Could not write the export: {ex.Message}",
                _ => $"The export failed: {ex.Message}"
            };
            logger.LogWarning(ex, "Auto export ({Trigger}) failed", trigger);
            outcome = (await stateFile.LoadAsync(CancellationToken.None)) with
            {
                LastAttemptUtc = started,
                LastResult = "failed",
                LastMessage = message,
                LastTrigger = trigger,
                LastWarnings = [],
                LastHostKeyFingerprint = hostKey ?? (ex as AutoExportHostKeyException)?.Seen
            };
        }
        finally
        {
            TryDelete(temporary);
            _running.Release();
        }

        if (occurrence is { } slot)
        {
            var previous = await stateFile.LoadAsync(CancellationToken.None);
            outcome = outcome with
            {
                LastOccurrenceUtc = slot,
                AttemptsForOccurrence = previous.LastOccurrenceUtc == slot ? previous.AttemptsForOccurrence + 1 : 1
            };
            outcome = await NotifyIfNeededAsync(outcome, CancellationToken.None);
        }

        await stateFile.SaveAsync(outcome, CancellationToken.None);
        return outcome;
    }

    // Once per day at most: a failing night retries hourly, and six identical messages by breakfast
    // would teach the owner to ignore the seventh.
    private async Task<AutoExportState> NotifyIfNeededAsync(AutoExportState state, CancellationToken ct)
    {
        if (state.LastResult == "ok") return state;
        if (state.LastProblemNotifiedUtc is { } notified && DateTimeOffset.UtcNow - notified < TimeSpan.FromHours(20)) return state;

        var (subject, text) = state.LastResult == "failed"
            ? ("Sentinel Core nightly backup failed",
               $"Tonight's automatic backup didn't complete. {state.LastMessage} It will retry hourly a few times; " +
               "check Settings -> Nightly Auto Export and press 'export now' to test once it's fixed.")
            : ("Sentinel Core nightly backup is incomplete",
               $"Tonight's backup was saved, but some things couldn't be included: {string.Join(" ", state.LastWarnings)}");

        try
        {
            if (await notifier.SendAsync(subject, text, ct)) return state with { LastProblemNotifiedUtc = DateTimeOffset.UtcNow };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send the auto export problem notification");
        }
        return state;
    }

    private static AutoExportSettingsDto CloneEnabled(AutoExportSettingsDto options) => new()
    {
        Enabled = true,
        Hour = options.Hour,
        Minute = options.Minute,
        Destination = options.Destination,
        FolderPath = options.FolderPath ?? "",
        SftpHost = options.SftpHost ?? "",
        SftpPort = options.SftpPort,
        SftpUsername = options.SftpUsername ?? "",
        SftpPassword = options.SftpPassword ?? "",
        SftpFolder = options.SftpFolder ?? "",
        SftpHostKeyFingerprint = options.SftpHostKeyFingerprint ?? "",
        KeepCount = options.KeepCount,
        Passphrase = options.Passphrase ?? ""
    };

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class AutoExportFolder
{
    public static async Task<string> DeliverAsync(string folder, string source, string fileName, long bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var target = UniqueName(folder, fileName);
        var partial = target + ".partial";

        // Copied rather than moved: the work directory and the destination are usually on
        // different disks, and a copy-then-rename is what makes the arrival atomic on the far side.
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await input.CopyToAsync(output, ct);
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
        }

        if (new FileInfo(partial).Length != bytes)
        {
            File.Delete(partial);
            throw new AutoExportException($"The copy in {folder} came out a different size from the original - the destination may be full.");
        }

        File.Move(partial, target);
        return target;
    }

    public static int Prune(string folder, int keep)
    {
        var own = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => AutoExportRunner.IsOwnFile(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();

        var removed = 0;
        foreach (var old in own.Skip(Math.Max(keep, 1)))
        {
            try
            {
                old.Delete();
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    // Two exports in the same minute (a nightly run and an "export now") must not overwrite each
    // other; the later one gets a -2, -3 suffix, which still sorts after the first.
    private static string UniqueName(string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{stem}-{n}{extension}");
        return target;
    }
}

public static class AutoExportSftp
{
    private static readonly Regex Fingerprint = new(@"^(SHA256:)?[A-Za-z0-9+/]{43}=?$", RegexOptions.CultureInvariant);

    public static bool IsFingerprint(string value) => Fingerprint.IsMatch(value.Trim());

    private static string Normalise(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("SHA256:", StringComparison.Ordinal)) trimmed = trimmed["SHA256:".Length..];
        return trimmed.TrimEnd('=');
    }

    // Returns where it went and the host key fingerprint the server presented.
    public static async Task<(string Location, string HostKey)> DeliverAsync(
        AutoExportSettingsDto options, string source, string fileName, long bytes, CancellationToken ct)
    {
        using var client = Connect(options, out var seen);
        await ConnectCheckedAsync(client, options, seen, ct);

        var folder = await EnsureFolderAsync(client, options.SftpFolder, ct);
        var remote = Combine(folder, fileName);
        for (var n = 2; await client.ExistsAsync(remote, ct); n++)
            remote = Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)}-{n}{Path.GetExtension(fileName)}");
        var partial = remote + ".partial";

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await client.UploadFileAsync(input, partial, ct);

        var uploaded = await client.GetAttributesAsync(partial, ct);
        if (uploaded.Size != bytes)
        {
            await client.DeleteFileAsync(partial, ct);
            throw new AutoExportException("The file on the SFTP server came out a different size from the original - its disk may be full.");
        }

        await client.RenameFileAsync(partial, remote, ct);
        return ($"sftp://{options.SftpHost}{(options.SftpPort == 22 ? "" : $":{options.SftpPort}")}/{remote.TrimStart('/')}", "SHA256:" + seen.Value);
    }

    public static async Task<int> PruneAsync(AutoExportSettingsDto options, int keep, CancellationToken ct)
    {
        using var client = Connect(options, out var seen);
        await ConnectCheckedAsync(client, options, seen, ct);
        var folder = string.IsNullOrWhiteSpace(options.SftpFolder) ? "." : options.SftpFolder.Trim();

        var own = new List<Renci.SshNet.Sftp.ISftpFile>();
        await foreach (var file in client.ListDirectoryAsync(folder, ct))
            if (file.IsRegularFile && AutoExportRunner.IsOwnFile(file.Name)) own.Add(file);

        var removed = 0;
        foreach (var old in own.OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Math.Max(keep, 1)))
        {
            try
            {
                await client.DeleteFileAsync(old.FullName, ct);
                removed++;
            }
            catch (SshException) { }
        }
        return removed;
    }

    private static SftpClient Connect(AutoExportSettingsDto options, out StrongBox seen)
    {
        var box = new StrongBox();
        seen = box;
        var expected = string.IsNullOrWhiteSpace(options.SftpHostKeyFingerprint) ? null : Normalise(options.SftpHostKeyFingerprint);

        var client = new SftpClient(options.SftpHost.Trim(), options.SftpPort, options.SftpUsername.Trim(), options.SftpPassword)
        {
            OperationTimeout = TimeSpan.FromMinutes(2)
        };
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        client.HostKeyReceived += (_, e) =>
        {
            box.Value = e.FingerPrintSHA256;
            e.CanTrust = expected is null || string.Equals(expected, e.FingerPrintSHA256, StringComparison.Ordinal);
        };
        return client;
    }

    // SSH.NET reports a refused host key as a generic key-exchange failure; this turns it into one
    // that names both fingerprints, which is the only way the owner can tell "somebody else is
    // answering at that address" from "I reinstalled the NAS".
    private static async Task ConnectCheckedAsync(SftpClient client, AutoExportSettingsDto options, StrongBox seen, CancellationToken ct)
    {
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (SshConnectionException) when (
            !string.IsNullOrWhiteSpace(options.SftpHostKeyFingerprint) && seen.Value is { } actual &&
            !string.Equals(Normalise(options.SftpHostKeyFingerprint), actual, StringComparison.Ordinal))
        {
            throw new AutoExportHostKeyException(
                $"The SFTP server's host key is SHA256:{actual}, not the SHA256:{Normalise(options.SftpHostKeyFingerprint)} saved in settings, " +
                "so nothing was sent. If you replaced or reinstalled that computer, paste the new fingerprint into settings; if you didn't, " +
                "something else may be answering at that address.",
                "SHA256:" + actual);
        }
    }

    private static async Task<string> EnsureFolderAsync(SftpClient client, string? folder, CancellationToken ct)
    {
        var trimmed = folder?.Trim().TrimEnd('/') ?? "";
        if (trimmed.Length == 0) return "";

        var built = trimmed.StartsWith('/') ? "/" : "";
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            built = built.Length == 0 || built == "/" ? built + segment : built + "/" + segment;
            if (!await client.ExistsAsync(built, ct)) await client.CreateDirectoryAsync(built, ct);
        }
        return trimmed;
    }

    private static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    public sealed class StrongBox
    {
        public string? Value { get; set; }
    }
}

public class AutoExportException(string message) : Exception(message);

public sealed class AutoExportHostKeyException(string message, string seen) : AutoExportException(message)
{
    public string Seen { get; } = seen;
}

public sealed class AutoExportWorker(
    AppSettingsFile settings,
    AutoExportStateFile stateFile,
    AutoExportRunner runner,
    ILogger<AutoExportWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short pause first so a restart at 03:00 doesn't compete with everything else starting.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Auto export check failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var options = await settings.GetSectionAsync<AutoExportSettingsDto>(AutoExportSettingsDto.SectionName, ct)
            ?? new AutoExportSettingsDto();
        var state = await stateFile.LoadAsync(ct);
        var now = DateTimeOffset.UtcNow;

        if (!options.Enabled)
        {
            if (state.ArmedAtUtc is not null) await stateFile.SaveAsync(state with { ArmedAtUtc = null }, ct);
            return;
        }
        if (state.ArmedAtUtc is null)
        {
            await stateFile.SaveAsync(state with { ArmedAtUtc = now }, ct);
            return;
        }

        var zone = RecordingScheduleWorker.ResolveTimeZone(
            await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, ct));
        if (AutoExportSchedule.Due(options, state, now, zone) is { } occurrence)
            await runner.RunAsync(occurrence, ct);
    }
}

public static class AutoExportEndpoints
{
    public static void MapAutoExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/backup/auto").RequireAuthorization("AdminOnly");

        group.MapGet("/status", async (AppSettingsFile settings, AutoExportStateFile stateFile, CancellationToken ct) =>
        {
            var options = await settings.GetSectionAsync<AutoExportSettingsDto>(AutoExportSettingsDto.SectionName, ct)
                ?? new AutoExportSettingsDto();
            var state = await stateFile.LoadAsync(ct);
            DateTimeOffset? next = null;
            if (options.Enabled)
            {
                var zone = RecordingScheduleWorker.ResolveTimeZone(
                    await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, ct));
                next = AutoExportSchedule.NextOccurrence(options.Hour, options.Minute, DateTimeOffset.UtcNow, zone);
            }
            return Results.Ok(new
            {
                enabled = options.Enabled,
                nextRunUtc = next,
                state.LastAttemptUtc,
                state.LastSuccessUtc,
                state.LastResult,
                state.LastMessage,
                state.LastTrigger,
                state.LastLocation,
                state.LastBytes,
                state.LastWarnings,
                state.LastHostKeyFingerprint
            });
        });

        // The settings section's "export now" button. `detail` is what every client's action
        // handler shows on success; a failure answers 400 with `error`, which they show instead.
        group.MapPost("/run", async (AutoExportRunner runner, CancellationToken ct) =>
        {
            var state = await runner.RunAsync(occurrence: null, ct);
            var fingerprint = state.LastHostKeyFingerprint is { } key ? $" SFTP host key: {key}" : "";
            var warnings = state.LastWarnings.Count > 0 ? " " + string.Join(" ", state.LastWarnings) : "";
            return state.LastResult == "failed"
                ? Results.BadRequest(new { error = state.LastMessage + fingerprint })
                : Results.Ok(new { detail = state.LastMessage + warnings + fingerprint, result = state.LastResult });
        });
    }
}
