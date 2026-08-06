namespace SentinelCore.Web;

// Ages out motion the AI rejected, along with the images and clips those rows owned.
//
// Nothing used to prune any of this. The main app's RetentionWorker deliberately restricts itself
// to two subdirectories of ITS OWN data root (see the note there about sweeping the root and
// unlinking events.db), and this app's trigger-images and trigger-clips live somewhere else
// entirely - so they grew without bound. On the deployment that prompted this they had reached
// 36 GB, more than half of it motion the AI had already judged to be nothing.
//
// Rejected motion is kept for a window rather than dropped at the moment of rejection, because the
// rejects are the only evidence for whether a zone is tuned correctly. A threshold that fires on a
// spider web looks identical to one that is set perfectly until you can see what it fired ON. The
// window is what makes both true at once: the false triggers are reviewable while they still tell
// you something about the current settings, and gone before they cost real disk.
//
// Confirmed events are not touched here. Those age out under the main app's RetentionDays, which
// is a much longer window and a different question - one is "how much history do I keep", the
// other is "how long do I need to debug my zones".
public sealed class MotionRetentionWorker(
    ITriggerEventStore triggers,
    AppSettingsFile appSettings,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<MotionRetentionWorker> logger) : BackgroundService
{
    // Matches RetentionWorker's cadence in the main app. The window is measured in days, so the
    // only thing a tighter interval would buy is deleting things a few hours sooner.
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    // How long a file has to have been sitting there before "no row points at it" is allowed to
    // mean "nothing owns it". This is the whole safety margin of the orphan sweep, and it exists
    // because an unreferenced file is the NORMAL state of a clip that is still being made: the row
    // is written the moment motion fires, with ClipPath null, and patched only once the post-roll
    // has elapsed and the segments have been concatenated (see TriggerZoneMonitor and
    // SetClipPathAsync). Sweeping without this would delete clips out from under the recorder that
    // was still writing them - turning a garbage collector into a corruption source.
    //
    // An hour is far longer than any clip takes to build. The cost of being generous is that a
    // genuine orphan survives one extra sweep; the cost of being tight is deleting live data.
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(1);

    // Written continuously by each camera's ffmpeg session and referenced by no row, ever - so it
    // looks exactly like an orphan and never is.
    private const string LiveSnapshotName = "_latest.jpg";

    // Sweeps immediately on startup and every Interval after, matching RetentionWorker in the main
    // app. No startup delay: the work is a DELETE against an indexed column and a metadata-only
    // directory walk, which is not worth staging around the camera monitors, and a worker whose
    // first run is minutes away is a worker nobody can watch actually work.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Two independent sweeps, caught independently. The orphan sweep runs whatever the
            // retention window is set to - including 0, "keep everything" - because a file with no
            // row is not history anyone chose to keep, it's a leak. Ordered second so it also picks
            // up anything the row sweep above failed to unlink on this same pass.
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Motion retention sweep failed"); }

            try { await SweepOrphansAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Orphaned trigger file sweep failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        // Read live from the settings file for the same reason the endpoints do - this project's
        // configuration doesn't include the main app's appsettings.json, so a bound IOptionsMonitor
        // would never see an edit made in the Settings UI.
        var bridge = await appSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        var days = bridge?.UnverifiedMotionRetentionDays ?? 0;

        // Zero (or a missing section) means "keep everything", which is what the setting looked
        // like before it existed. Deleting on a value we failed to read would be the one mistake
        // here that can't be undone.
        if (days <= 0) return;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var files = await triggers.DeleteUnconfirmedBeforeAsync(cutoff, cancellationToken);
        if (files.Count == 0) return;

        var bytes = 0L;
        var deleted = 0;
        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var size = info.Length;
                info.Delete();
                bytes += size;
                deleted++;
            }
            // A file that won't delete is a file that gets picked up on a later sweep only if its
            // row still exists - and the row is already gone. That leaks one file rather than
            // failing the whole sweep, which is the right trade for a cleanup task.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        logger.LogInformation(
            "Motion retention: pruned unconfirmed motion older than {Days}d - {Deleted} of {Referenced} referenced files removed, {Megabytes:0.0} MB reclaimed",
            days, deleted, files.Count, bytes / 1024d / 1024d);
    }

    // Deletes files under the trigger directories that no row points at any more.
    //
    // These accumulate through every path that separates a row from its file: the sweep above
    // deletes the row first and can fail on the file, a clip finishes building after its row was
    // already pruned, a crash lands between the two writes. None of it is recoverable by looking at
    // rows alone - the row is precisely what's missing - so without this the only way to reclaim
    // the space is to diff the directory against the database by hand. On the deployment this was
    // written for there were 510 such files, 4.7 GB, and nothing that would ever have found them.
    private async Task SweepOrphansAsync(CancellationToken cancellationToken)
    {
        // Both directories hang off the same CamerasDataDirectory root, resolved identically by
        // TriggerImagePaths.ResolveRoot and ClipRecorder.ResolveRoot. Walking back up from the
        // images root rather than re-deriving it keeps this pinned to whatever those decide, since
        // pointing the sweep at the wrong directory is how it would come to see every file as
        // unowned. clip-buffer is deliberately NOT included: the recorder rewrites it constantly
        // and no row ever references it, so it is 100% "orphan" by this definition and 0% garbage.
        var imagesRoot = TriggerImagePaths.ResolveRoot(configuration, environment);
        var clipsRoot = Path.Combine(Path.GetDirectoryName(imagesRoot)!, "trigger-clips");

        var referenced = (await triggers.GetAllReferencedFilesAsync(cancellationToken))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.Ordinal);

        var onDisk = new List<(string Path, DateTimeOffset LastWrite)>();
        foreach (var root in new[] { imagesRoot, clipsRoot })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { onDisk.Add((file, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero))); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var orphans = SelectOrphans(onDisk, referenced, DateTimeOffset.UtcNow - OrphanGrace);
        if (orphans.Count == 0) return;

        var bytes = 0L;
        var deleted = 0;
        foreach (var path in orphans)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var size = info.Length;
                info.Delete();
                bytes += size;
                deleted++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        logger.LogInformation(
            "Motion retention: removed {Deleted} orphaned files with no database row, {Megabytes:0.0} MB reclaimed",
            deleted, bytes / 1024d / 1024d);
    }

    // Pure, and public for the tests, for the same reason RetentionWorker.PruneExpiredFiles is:
    // this decides what gets deleted, and every way of getting it wrong destroys live data rather
    // than throwing. `referenced` must already be normalized through NormalizePath - the database
    // stores absolute paths and a directory walk can produce a different spelling of the same file,
    // and a comparison that misses would classify every referenced file as an orphan.
    public static IReadOnlyList<string> SelectOrphans(
        IReadOnlyList<(string Path, DateTimeOffset LastWrite)> onDisk,
        IReadOnlySet<string> referenced,
        DateTimeOffset cutoff)
    {
        var orphans = new List<string>();
        foreach (var (path, lastWrite) in onDisk)
        {
            if (Path.GetFileName(path) == LiveSnapshotName) continue;
            if (lastWrite >= cutoff) continue;
            if (referenced.Contains(NormalizePath(path))) continue;
            orphans.Add(path);
        }

        return orphans;
    }

    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        // A row holding something that isn't a usable path can't match a real file anyway; keeping
        // it verbatim leaves it in the referenced set, which errs toward not deleting.
        catch (ArgumentException) { return path; }
        catch (NotSupportedException) { return path; }
        catch (PathTooLongException) { return path; }
    }
}
