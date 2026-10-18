using System.Text.Json;

namespace SentinelCore.Web;

// The nightly pass over the family file library and the on-demand jobs the dashboard starts:
//
//   1. empty what has sat in the trash longer than the setting allows,
//   2. re-index the library (hash whatever is new or changed),
//   3. find files with identical contents and retire all but one copy to the trash - or only
//      report them, when the setting says so,
//   4. mirror the library to every enabled backup drive.
//
// One job at a time. A job started from the dashboard runs in the background and reports
// progress through FamilyFilesJobs.Current; the nightly pass runs inline in the worker.

public sealed record FamilyDuplicateGroup(string Kept, IReadOnlyList<string> Copies, long Size);

public sealed record FamilyDuplicateReport
{
    public DateTimeOffset RanUtc { get; init; }
    // True when the copies were moved to the trash; false when they were only listed.
    public bool Removed { get; init; }
    public int Groups { get; init; }
    public int Copies { get; init; }
    public long Bytes { get; init; }
    public int FilesChecked { get; init; }
    // The first so-many groups, for the dashboard; the count above is the real total.
    public IReadOnlyList<FamilyDuplicateGroup> Sample { get; init; } = [];
    public string? TrashBatchId { get; init; }
}

public sealed record FamilyFilesState
{
    public DateTimeOffset? ArmedAtUtc { get; init; }
    public DateTimeOffset? LastOccurrenceUtc { get; init; }
    public int AttemptsForOccurrence { get; init; }

    public DateTimeOffset? LastNightlyUtc { get; init; }
    // "ok", "incomplete" (some drive failed) or "failed".
    public string LastNightlyResult { get; init; } = "";
    public string LastNightlyMessage { get; init; } = "";
    public DateTimeOffset? LastProblemNotifiedUtc { get; init; }

    public DateTimeOffset? IndexedUtc { get; init; }
    public int LibraryFiles { get; init; }
    public long LibraryBytes { get; init; }

    public Dictionary<string, FamilyDriveState> Drives { get; init; } = new(StringComparer.Ordinal);
    public FamilyDuplicateReport? Duplicates { get; init; }
}

public sealed class FamilyFilesStateFile(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<FamilyFilesState> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return new FamilyFilesState();
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<FamilyFilesState>(stream, Options, ct) ?? new FamilyFilesState();
        }
        catch (JsonException)
        {
            return new FamilyFilesState();
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(FamilyFilesState state, CancellationToken ct)
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
        finally { _lock.Release(); }
    }

    public async Task<FamilyFilesState> UpdateAsync(Func<FamilyFilesState, FamilyFilesState> change, CancellationToken ct)
    {
        var updated = change(await LoadAsync(ct));
        await SaveAsync(updated, ct);
        return updated;
    }
}

public static class FamilyFilesSchedule
{
    public const int MaxAttemptsPerNight = 4;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    public static DateTimeOffset? Due(FamilyFilesSettingsDto options, FamilyFilesState state, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (!options.Enabled || state.ArmedAtUtc is not { } armedAt) return null;
        var occurrence = AutoExportSchedule.LatestOccurrence(options.Hour, options.Minute, nowUtc, zone);
        if (occurrence <= armedAt) return null;
        if (state.LastOccurrenceUtc != occurrence) return occurrence;

        var retry = state.LastNightlyResult != "ok"
            && state.AttemptsForOccurrence < MaxAttemptsPerNight
            && state.LastNightlyUtc is { } last && nowUtc - last >= RetryAfter;
        return retry ? occurrence : null;
    }
}

// What a running job says about itself.
public sealed record FamilyFilesJobProgress(
    string Kind, string? DriveId, string? DriveName, DateTimeOffset StartedUtc, string Message, int Done, int Total);

public sealed class FamilyFilesJobs(
    FamilyFilesLocations locations,
    FamilyFilesLibrary library,
    FamilyFilesIndex index,
    FamilyFilesTrash trash,
    FamilyDriveStore drives,
    FamilyFilesStateFile stateFile,
    AppSettingsFile settings,
    OwnerNotifier notifier,
    ILogger<FamilyFilesJobs> logger)
{
    public const string KindNightly = "nightly";
    public const string KindSync = "sync";
    public const string KindRestore = "restore";
    public const string KindDuplicates = "duplicates";
    public const string KindIndex = "index";

    private readonly SemaphoreSlim _busy = new(1, 1);
    private volatile FamilyFilesJobProgress? _current;

    public FamilyFilesJobProgress? Current => _current;

    // Starts a job in the background. False when one is already running.
    public bool TryStart(string kind, string? driveId, string? driveName, Func<CancellationToken, Task> work)
    {
        if (!_busy.Wait(0)) return false;
        _current = new FamilyFilesJobProgress(kind, driveId, driveName, DateTimeOffset.UtcNow, "starting", 0, 0);
        _ = Task.Run(async () =>
        {
            try
            {
                await work(CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Family files {Kind} job failed", kind);
            }
            finally
            {
                _current = null;
                _busy.Release();
            }
        });
        return true;
    }

    // Runs `work` now, inline, or returns false if a job is already running.
    public async Task<bool> RunExclusiveAsync(string kind, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return false;
        _current = new FamilyFilesJobProgress(kind, null, null, DateTimeOffset.UtcNow, "starting", 0, 0);
        try
        {
            await work(ct);
            return true;
        }
        finally
        {
            _current = null;
            _busy.Release();
        }
    }

    private void Report(string message, int done = 0, int total = 0)
    {
        if (_current is { } current) _current = current with { Message = message, Done = done, Total = total };
    }

    public async Task<FamilyLibraryScan> IndexAsync(CancellationToken ct)
    {
        var store = await library.StoreAsync(ct);
        Report("checking the library", 0, 0);
        // A library folder that isn't there (a disk not mounted, a share folder renamed) is a
        // problem to report, not an empty library to back up - an empty library would look like
        // everything was deleted. Only the default folder under the data directory is simply made.
        if (await Task.Run(() => store.Stat(""), ct) is not { IsFolder: true })
        {
            if (store.LocalPath("") is { } local && string.Equals(Path.GetFullPath(local), Path.GetFullPath(locations.DefaultLibrary), StringComparison.Ordinal))
                await Task.Run(() => store.CreateFolder(""), ct);
            else
                throw new IOException($"The library folder {store.Root} isn't there - is the disk connected, or the shared folder still shared? Nothing was backed up or removed.");
        }
        var scan = await index.RescanAsync(store, (done, total) => Report("checking the library", done, total), ct);
        await stateFile.UpdateAsync(s => s with
        {
            IndexedUtc = DateTimeOffset.UtcNow,
            LibraryFiles = scan.Files.Count,
            LibraryBytes = scan.Files.Sum(f => f.Size)
        }, ct);
        if (!scan.Complete)
            logger.LogWarning("Family files: {Count} folder(s)/file(s) couldn't be read, first {First}", scan.Unreadable.Count, scan.Unreadable[0]);
        return scan;
    }

    // Groups the library by content. With `remove`, every copy but one goes to the trash; the
    // kept copy is the oldest, and among equals the one with the shortest path (the "original"
    // is almost always the one that was there first and not buried in a "copy of" folder).
    public async Task<FamilyDuplicateReport> CheckDuplicatesAsync(bool remove, string? by, CancellationToken ct)
    {
        var scan = await IndexAsync(ct);
        var files = scan.Files;
        Report("looking for duplicates", 0, 0);

        var groups = files
            .Where(f => f.Sha256 is not null && f.Size > 0)
            .GroupBy(f => (f.Sha256!, f.Size))
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var ordered = g.OrderBy(f => f.ModifiedUtc).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
                return new FamilyDuplicateGroup(ordered[0].Path, ordered.Skip(1).Select(f => f.Path).ToList(), g.Key.Size);
            })
            .OrderByDescending(g => g.Size * g.Copies.Count)
            .ToList();

        var copies = groups.Sum(g => g.Copies.Count);
        var bytes = groups.Sum(g => g.Size * g.Copies.Count);
        string? batchId = null;

        if (remove && copies > 0)
        {
            Report($"moving {copies} duplicate(s) to the trash", 0, copies);
            var store = await library.StoreAsync(ct);
            var items = groups.SelectMany(g => g.Copies.Select(c => (c, (string?)g.Kept))).ToList();
            var batch = await trash.MoveAsync(store, items, FamilyFilesTrash.ReasonDuplicate, by, ct);
            batchId = batch.Id;
            foreach (var (path, _) in items) await index.ForgetAsync(path, ct);
            await index.SaveAsync(ct);
            foreach (var folder in groups.SelectMany(g => g.Copies).Select(FamilyFilePaths.ParentOf).Where(f => f.Length > 0).Distinct())
                await Task.Run(() => store.DeleteEmptyFolders(folder), ct);
        }

        var report = new FamilyDuplicateReport
        {
            RanUtc = DateTimeOffset.UtcNow,
            Removed = remove,
            Groups = groups.Count,
            Copies = copies,
            Bytes = bytes,
            FilesChecked = files.Count,
            Sample = groups.Take(200).ToList(),
            TrashBatchId = batchId
        };
        await stateFile.UpdateAsync(s => s with
        {
            Duplicates = report,
            LibraryFiles = remove ? s.LibraryFiles - copies : s.LibraryFiles,
            LibraryBytes = remove ? s.LibraryBytes - bytes : s.LibraryBytes
        }, ct);
        logger.LogInformation("Family files duplicate check: {Groups} group(s), {Copies} copies, {Bytes} bytes{Removed}",
            groups.Count, copies, bytes, remove ? ", moved to the trash" : "");
        return report;
    }

    // `removals`: Allowed when a person pressed "back up now", Guarded for the nightly pass. An
    // incomplete scan always means Never.
    public async Task<FamilyDriveState> SyncDriveAsync(FamilyDrive drive, FamilyLibraryScan? scan, FamilyDriveRemovals removals, CancellationToken ct)
    {
        scan ??= await IndexAsync(ct);
        var files = scan.Files;
        if (!scan.Complete) removals = FamilyDriveRemovals.Never;
        var store = await library.StoreAsync(ct);
        var started = DateTimeOffset.UtcNow;
        if (_current is { } current) _current = current with { DriveId = drive.Id, DriveName = drive.Name };
        Report($"backing up to {drive.Name}", 0, files.Count);

        FamilyDriveState outcome;
        try
        {
            var result = await FamilyDriveSync.MirrorAsync(drive, store, files, removals,
                (done, total, path) => Report($"backing up to {drive.Name}: {path}", done, total), logger, ct);
            outcome = new FamilyDriveState
            {
                LastAttemptUtc = started,
                LastSuccessUtc = result.Ok ? started : (await stateFile.LoadAsync(ct)).Drives.GetValueOrDefault(drive.Id)?.LastSuccessUtc,
                // "held": backed up, but removals were kept back for a person to look at.
                LastResult = !result.Ok ? "failed" : result.Held > 0 ? "held" : "ok",
                LastMessage = result.Message,
                LastFileCount = result.FileCount,
                LastBytes = result.Bytes,
                LastCopied = result.Copied,
                LastRemoved = result.Removed,
                LastFreeBytes = result.FreeBytes,
                LastHostKeyFingerprint = result.HostKey
            };
            logger.LogInformation("Family files backed up to {Drive}: {Message}", drive.Name, result.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var previous = (await stateFile.LoadAsync(CancellationToken.None)).Drives.GetValueOrDefault(drive.Id);
            outcome = new FamilyDriveState
            {
                LastAttemptUtc = started,
                LastSuccessUtc = previous?.LastSuccessUtc,
                LastResult = "failed",
                LastMessage = FamilyDriveSync.Describe(exception),
                LastFileCount = previous?.LastFileCount,
                LastBytes = previous?.LastBytes,
                LastFreeBytes = previous?.LastFreeBytes,
                LastHostKeyFingerprint = (exception as FamilyDriveHostKeyException)?.Seen ?? previous?.LastHostKeyFingerprint
            };
            logger.LogWarning(exception, "Family files backup to {Drive} failed", drive.Name);
        }

        await stateFile.UpdateAsync(s =>
        {
            var updated = new Dictionary<string, FamilyDriveState>(s.Drives, StringComparer.Ordinal) { [drive.Id] = outcome };
            return s with { Drives = updated };
        }, CancellationToken.None);
        return outcome;
    }

    public async Task<FamilyDriveRestoreResult> RestoreFromDriveAsync(FamilyDrive drive, CancellationToken ct)
    {
        var store = await library.StoreAsync(ct);
        if (_current is { } current) _current = current with { DriveId = drive.Id, DriveName = drive.Name };
        Report($"bringing files back from {drive.Name}", 0, 0);
        try
        {
            var result = await FamilyDriveSync.RestoreAsync(drive, store, locations.Work,
                (done, total, path) => Report($"bringing back {path}", done, total), logger, ct);
            await IndexAsync(ct);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Restore from drive {Drive} failed", drive.Name);
            return new FamilyDriveRestoreResult(false, FamilyDriveSync.Describe(exception), 0, 0, 0, 0);
        }
    }

    // The whole nightly pass. `occurrence` is the slot being run for; null for "run now".
    public async Task<FamilyFilesState> RunNightlyAsync(DateTimeOffset? occurrence, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var options = await locations.OptionsAsync(ct);
        var problems = new List<string>();
        var notes = new List<string>();

        try
        {
            Report("emptying old trash");
            var purged = await trash.PurgeAsync(TimeSpan.FromDays(options.TrashKeepDays), ct);
            if (purged > 0) notes.Add($"emptied {purged} file(s) from the trash after {options.TrashKeepDays} days");

            var report = await CheckDuplicatesAsync(options.RemoveDuplicates, "nightly", ct);
            if (report.Copies > 0)
                notes.Add(options.RemoveDuplicates
                    ? $"moved {report.Copies} duplicate file(s) ({AutoExportRunner.FormatBytes(report.Bytes)}) to the trash"
                    : $"found {report.Copies} duplicate file(s) ({AutoExportRunner.FormatBytes(report.Bytes)}) - see the Files tab");

            var scan = await IndexAsync(ct);
            if (!scan.Complete)
                problems.Add($"{scan.Unreadable.Count} folder(s) or file(s) in the library couldn't be read (first: {scan.Unreadable[0]}), so nothing was removed from the drives tonight");
            var files = scan.Files;
            var enabled = (await drives.GetAllAsync(ct)).Where(d => d.Enabled).ToList();
            var synced = 0;
            foreach (var drive in enabled)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await SyncDriveAsync(drive, scan, FamilyDriveRemovals.Guarded, ct);
                if (outcome.LastResult == "ok") synced++;
                else problems.Add($"{drive.Name}: {outcome.LastMessage}");
            }
            if (enabled.Count == 0) notes.Add("no backup drives are set up yet");
            else notes.Add($"backed up {files.Count} file(s) to {synced} of {enabled.Count} drive(s)");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Family files nightly pass failed");
            problems.Add(exception.Message);
        }

        var result = problems.Count == 0 ? "ok" : notes.Count > 0 ? "incomplete" : "failed";
        var message = string.Join("; ", notes.Concat(problems.Select(p => "problem: " + p)));
        if (message.Length > 0) message = char.ToUpperInvariant(message[0]) + message[1..] + ".";

        var state = await stateFile.UpdateAsync(s => s with
        {
            LastNightlyUtc = started,
            LastNightlyResult = result,
            LastNightlyMessage = message,
            LastOccurrenceUtc = occurrence ?? s.LastOccurrenceUtc,
            AttemptsForOccurrence = occurrence is null ? s.AttemptsForOccurrence
                : s.LastOccurrenceUtc == occurrence ? s.AttemptsForOccurrence + 1 : 1
        }, CancellationToken.None);

        if (occurrence is not null && problems.Count > 0) state = await NotifyAsync(state, problems);
        return state;
    }

    private async Task<FamilyFilesState> NotifyAsync(FamilyFilesState state, IReadOnlyList<string> problems)
    {
        if (state.LastProblemNotifiedUtc is { } notified && DateTimeOffset.UtcNow - notified < TimeSpan.FromHours(20)) return state;
        try
        {
            var sent = await notifier.SendAsync(
                "Sentinel Core family files backup had a problem",
                "Last night's family files backup didn't fully complete. " + string.Join(" ", problems) +
                " It will retry hourly a few times; check the Files tab on the dashboard, and press 'back up now' once it's fixed.",
                CancellationToken.None);
            if (sent) return await stateFile.UpdateAsync(s => s with { LastProblemNotifiedUtc = DateTimeOffset.UtcNow }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not send the family files problem notification");
        }
        return state;
    }

    public async Task<DateTimeOffset?> NextRunAsync(CancellationToken ct)
    {
        var options = await locations.OptionsAsync(ct);
        if (!options.Enabled) return null;
        var zone = RecordingScheduleWorker.ResolveTimeZone(await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, ct));
        return AutoExportSchedule.NextOccurrence(options.Hour, options.Minute, DateTimeOffset.UtcNow, zone);
    }
}

public sealed class FamilyFilesWorker(
    FamilyFilesLocations locations,
    FamilyFilesStateFile stateFile,
    FamilyFilesJobs jobs,
    AppSettingsFile settings,
    ILogger<FamilyFilesWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Family files nightly check failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var options = await locations.OptionsAsync(ct);
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

        var zone = RecordingScheduleWorker.ResolveTimeZone(await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, ct));
        if (FamilyFilesSchedule.Due(options, state, now, zone) is { } occurrence)
        {
            // A job somebody started from the dashboard wins; the next tick tries again.
            await jobs.RunExclusiveAsync(FamilyFilesJobs.KindNightly, token => jobs.RunNightlyAsync(occurrence, token), ct);
        }
    }
}
