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
            "Retention: pruned saved alert images and results older than {Days}d - {Deleted} file(s) removed, {Megabytes:0.#} MB reclaimed",
            options.RetentionDays, outcome.FilesDeleted, outcome.BytesReclaimed / 1024d / 1024d);

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
    public readonly record struct PruneOutcome(int FilesDeleted, long BytesReclaimed, int FilesFailed);

    // Public so the "never touches the database" guarantee is directly testable, following the
    // same pattern as MqttTopics and BuildSnapshotUrl elsewhere in this project.
    public static PruneOutcome PruneExpiredFiles(string dataRoot, DateTime cutoffUtc)
    {
        var deleted = 0;
        var bytes = 0L;
        var failed = 0;

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
        }

        return new PruneOutcome(deleted, bytes, failed);
    }
}
