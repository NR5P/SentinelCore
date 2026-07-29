namespace SentinelCore;

// The alert queue lives in memory, so anything still waiting when the process goes down - a
// deploy, a crash, a reboot - used to disappear, leaving a row stuck at "queued" with nothing
// behind it. Since the image is now written before the alert is enqueued, those alerts can be
// picked back up on the next start instead.
//
// Runs once at startup. Anything that can't be resumed (image saving turned off, file deleted
// by retention) is marked failed rather than left pending forever.
public sealed class AlertRecoveryService(
    IEventStore eventStore,
    IResultStore resultStore,
    IAlertQueue queue,
    ILogger<AlertRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        List<PendingAlertRecord> pending;
        try
        {
            pending = [.. await eventStore.GetPendingAlertsAsync(cancellationToken)];
        }
        catch (Exception ex)
        {
            // Recovery is a convenience, not a prerequisite - never block startup over it.
            logger.LogError(ex, "Could not read interrupted alerts; skipping recovery");
            return;
        }

        if (pending.Count == 0) return;
        logger.LogInformation("Found {Count} alert(s) interrupted by a restart", pending.Count);

        var resumed = 0;
        var abandoned = 0;
        foreach (var record in pending)
        {
            try
            {
                if (await TryResumeAsync(record, cancellationToken)) resumed++;
                else { await AbandonAsync(record, cancellationToken); abandoned++; }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not recover interrupted alert {AlertId}", record.Id);
            }
        }

        logger.LogInformation("Alert recovery complete: {Resumed} resumed, {Abandoned} abandoned", resumed, abandoned);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> TryResumeAsync(PendingAlertRecord record, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.ImagePath) || !File.Exists(record.ImagePath)) return false;

        var bytes = await File.ReadAllBytesAsync(record.ImagePath, cancellationToken);
        var alert = new IncomingAlert(
            record.Id,
            record.Camera,
            record.BlueIrisAlertId,
            record.OccurredAt,
            record.ReceivedAt,
            record.Profile,
            ContentTypeFor(record.ImagePath),
            Path.GetFileName(record.ImagePath),
            bytes,
            record.ImagePath);

        // A full queue here means we'd only lose it again on the next restart, so treat it the
        // same as any other unrecoverable alert rather than silently dropping it.
        if (!await queue.TryEnqueueAsync(alert, cancellationToken)) return false;

        logger.LogInformation("Resumed interrupted alert {AlertId} from {Camera}", record.Id, record.Camera);
        return true;
    }

    private async Task AbandonAsync(PendingAlertRecord record, CancellationToken cancellationToken)
    {
        await resultStore.SaveAsync(new AlertResult
        {
            Id = record.Id,
            Camera = record.Camera,
            BlueIrisAlertId = record.BlueIrisAlertId,
            OccurredAt = record.OccurredAt,
            ReceivedAt = record.ReceivedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Profile = record.Profile,
            Model = record.Model,
            Status = "failed",
            ImagePath = record.ImagePath,
            Error = "Interrupted by a restart before analysis, and the image was no longer available to retry with."
        }, cancellationToken);

        logger.LogWarning("Abandoned interrupted alert {AlertId} from {Camera} - no image to retry with", record.Id, record.Camera);
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };
}
