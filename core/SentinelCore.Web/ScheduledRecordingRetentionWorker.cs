namespace SentinelCore.Web;

// Ages out scheduled recordings and the files they own.
//
// A worker of its own rather than another pass inside MotionRetentionWorker, which exists to answer
// a different question - "how long do I need to debug my zones" - and deliberately only ever
// touches motion the AI rejected. This one answers "how long do I keep an hour of full-bitrate
// video", and the numbers involved are an order of magnitude larger: the deployment that prompted
// MotionRetentionWorker had accumulated 36 GB of trigger media over months, and a handful of daily
// hour-long recordings gets there in a fortnight.
//
// So unlike trigger clips, which went unpruned for a long time before anyone noticed, this ships
// with retention from the first commit.
public sealed class ScheduledRecordingRetentionWorker(
    IRecordingStore recordings,
    ScheduledRecorder recorder,
    ICameraStore cameras,
    ILogger<ScheduledRecordingRetentionWorker> logger) : BackgroundService
{
    // Matches MotionRetentionWorker's cadence. The window is measured in days, so a tighter
    // interval would only delete things a few hours sooner.
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    // How long an unreferenced file must have sat there before "no row points at it" is allowed to
    // mean "nothing owns it". Same reasoning as MotionRetentionWorker's OrphanGrace, and the same
    // hazard: a recording in progress is referenced by no path at all until it finalizes, so a
    // sweep without this grace would delete a segment buffer out from under a running ffmpeg.
    // A window can legitimately run for hours, so this is far more generous than that one's hour.
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(26);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The scheduled recording retention sweep failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        var options = await recorder.GetOptionsAsync(stoppingToken);
        if (options.RetentionDays <= 0) return;

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(options.RetentionDays);

        // Rows first, then their files - the same order MotionRetentionWorker uses, because losing
        // the race in this direction leaves a visible gap rather than an invisible one.
        var orphanedByDeletion = await recordings.DeleteBeforeAsync(cutoff, stoppingToken);

        var removed = 0;
        long reclaimed = 0;
        foreach (var path in orphanedByDeletion)
        {
            try
            {
                if (!File.Exists(path)) continue;
                reclaimed += new FileInfo(path).Length;
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not delete the aged-out recording at {Path}", path);
            }
        }

        var (orphanFiles, orphanBytes) = await SweepOrphansAsync(stoppingToken);

        if (removed + orphanFiles == 0) return;

        logger.LogInformation(
            "Scheduled recording retention removed {Aged} recording(s) older than {Days} day(s) and {Orphans} orphaned file(s), reclaiming {Megabytes:0.#} MB",
            removed, options.RetentionDays, orphanFiles, (reclaimed + orphanBytes) / 1024.0 / 1024);
    }

    // Files on disk that no row points at. These accumulate the same way trigger clips do - a
    // process killed between writing the mp4 and updating the row, a database restored from a
    // backup older than the footage - and are otherwise unreclaimable, because nothing left in the
    // system knows they exist.
    private async Task<(int Files, long Bytes)> SweepOrphansAsync(CancellationToken stoppingToken)
    {
        var referenced = (await recordings.GetAllReferencedFilesAsync(stoppingToken))
            .ToHashSet(StringComparer.Ordinal);

        var cameraList = await cameras.GetAllAsync(stoppingToken);
        var cutoff = DateTimeOffset.UtcNow - OrphanGrace;

        var files = 0;
        long bytes = 0;

        foreach (var camera in cameraList)
        {
            var directory = recorder.RecordingsDirectory(camera.Id);

            foreach (var path in Directory.EnumerateFiles(directory, "*.mp4"))
            {
                if (referenced.Contains(path)) continue;

                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc > cutoff.UtcDateTime) continue;

                    bytes += info.Length;
                    info.Delete();
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not delete the orphaned recording at {Path}", path);
                }
            }
        }

        return (files, bytes);
    }
}
