using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed class RetentionWorker : BackgroundService
{
    private readonly IOptionsMonitor<BridgeOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(IOptionsMonitor<BridgeOptions> options, IHostEnvironment environment, ILogger<RetentionWorker> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { DeleteExpiredFiles(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Retention cleanup failed"); }
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }

    // Only these two, never the data root itself. events.db lives in the root next to them, and
    // sweeping the root would eventually delete it: the file's timestamp only advances while
    // alerts are arriving, so any quiet spell longer than RetentionDays makes the database look
    // expired. On Linux the delete would succeed silently - the running service keeps writing
    // through its existing handle to a now-unlinked inode, and the loss only surfaces as an
    // empty history after the next restart. The transient events.db-journal is at equal risk,
    // where deletion means corruption rather than just loss.
    //
    // These are the only directories retention has any business in anyway: both hold
    // per-alert files that FileResultStore writes and nothing else reads long-term.
    private static readonly string[] PrunableSubdirectories = ["images", "results"];

    private void DeleteExpiredFiles()
    {
        var options = _options.CurrentValue;
        var configured = options.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(_environment.ContentRootPath, configured);
        var outcome = PruneExpiredFiles(root, DateTime.UtcNow.AddDays(-options.RetentionDays));

        // Every sweep, including the ones that removed nothing. This pass was previously silent in
        // both directions - it logged only on an exception that aborted the whole run - so "is
        // retention running at all?" could not be answered from the journal, only inferred by
        // noticing that the oldest surviving directory happened to sit at the cutoff. That is a
        // poor way to learn the answer for the one worker whose whole job is to stop the disk
        // filling. At twice a day this is two lines, which is not the noise a per-alert line would
        // be, and its sibling MotionRetentionWorker already reports each sweep the same way.
        _logger.LogInformation(
            "Retention: pruned saved alert images and results older than {Days}d - {Deleted} file(s) removed, " +
            "{Megabytes:0.#} MB reclaimed, {Directories} empty day folder(s) removed",
            options.RetentionDays, outcome.FilesDeleted, outcome.BytesReclaimed / 1024d / 1024d,
            outcome.DirectoriesDeleted);

        // Separate, and a warning, because this is the silent failure that matters: a file the
        // service cannot delete is skipped and retried forever, so a permissions or lock problem
        // does not stop retention loudly - it just quietly stops reclaiming while the sweep goes on
        // reporting success. Without this the disk fills with no signal anywhere.
        if (outcome.FilesFailed > 0)
        {
            _logger.LogWarning(
                "Retention: {Failed} expired file(s) could not be deleted and will be retried on the next sweep - " +
                "if this count is not falling, retention has stopped reclaiming space",
                outcome.FilesFailed);
        }
    }

    // What one sweep did. Returned rather than logged in here so the pruning stays pure and
    // directly testable - see the note on PruneExpiredFiles.
    public readonly record struct PruneOutcome(int FilesDeleted, long BytesReclaimed, int FilesFailed, int DirectoriesDeleted);

    // Public so the "never touches the database" guarantee is directly testable, following the
    // same pattern as MqttTopics and BuildSnapshotUrl elsewhere in this project.
    public static PruneOutcome PruneExpiredFiles(string dataRoot, DateTime cutoffUtc)
    {
        var deleted = 0;
        var bytes = 0L;
        var failed = 0;
        var directories = 0;

        foreach (var subdirectory in PrunableSubdirectories)
        {
            var directory = Path.Combine(dataRoot, subdirectory);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    // FileInfo rather than File.GetLastWriteTimeUtc because the size has to be read
                    // before the delete, and reading both off one handle avoids a second stat.
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc >= cutoffUtc) continue;

                    var size = info.Length;
                    File.Delete(file);
                    deleted++;
                    bytes += size;
                }
                // A file that vanished under us is not a failure - the sweep wanted it gone and it
                // is gone. Both of these derive from IOException, so they have to be caught first
                // or they would inflate the failure count the warning above is judged by.
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { failed++; }
                catch (UnauthorizedAccessException) { failed++; }
            }

            directories += RemoveEmptyDirectories(directory);
        }

        return new PruneOutcome(deleted, bytes, failed, directories);
    }

    // ResultStore files images under images/yyyy-MM-dd/, creating the day's folder on the first
    // alert of that day. The file sweep above empties those folders and leaves every one of them
    // behind, so images/ accumulated one permanently-empty entry per day the service had ever run -
    // eleven of them on the install this was written for, and one more every day forever. It is
    // 4 KB apiece, so this is about the directory staying readable rather than about disk: the
    // useful listing is "which days do I still have images for", and it was being buried under a
    // month of answers that are all "none".
    //
    // Only empty ones, only below images/ and results/, and never those roots themselves - the same
    // boundary the file sweep keeps, for the same reason. Today's folder is skipped outright: it is
    // the only one a running service still writes into, and ResultStore's CreateDirectory sits a
    // statement before its write, so deleting it in that window would cost one alert its image.
    // Nothing else is ever written to a past day, which is what makes those safe to take.
    private static int RemoveEmptyDirectories(string subdirectoryRoot)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var removed = 0;

        string[] candidates;
        try { candidates = Directory.GetDirectories(subdirectoryRoot, "*", SearchOption.AllDirectories); }
        catch (DirectoryNotFoundException) { return 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        // Deepest first, so a folder whose only contents were themselves empty folders has already
        // been emptied by the time it is considered. Any child is strictly deeper than its parent,
        // which is what makes separator count a sufficient ordering here.
        foreach (var directory in candidates
            .OrderByDescending(path => path.Count(character => character == Path.DirectorySeparatorChar)))
        {
            if (string.Equals(Path.GetFileName(directory), today, StringComparison.Ordinal)) continue;

            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;

                // Non-recursive on purpose: it throws rather than deletes if anything appeared
                // between the check above and this call, so the race cannot cost a file.
                Directory.Delete(directory, recursive: false);
                removed++;
            }
            // Not counted as a failure the way an undeletable file is. An empty folder that will
            // not go is worth nothing to reclaim, and folding it into FilesFailed would trip a
            // warning whose text is about the disk no longer being reclaimable.
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return removed;
    }
}
