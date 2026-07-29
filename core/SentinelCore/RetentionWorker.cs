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
        PruneExpiredFiles(root, DateTime.UtcNow.AddDays(-options.RetentionDays));
    }

    // Public so the "never touches the database" guarantee is directly testable, following the
    // same pattern as MqttTopics and BuildSnapshotUrl elsewhere in this project.
    public static void PruneExpiredFiles(string dataRoot, DateTime cutoffUtc)
    {
        foreach (var subdirectory in PrunableSubdirectories)
        {
            var directory = Path.Combine(dataRoot, subdirectory);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { if (File.GetLastWriteTimeUtc(file) < cutoffUtc) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
