using System.Diagnostics;
using System.Globalization;

namespace SentinelCore.Web;

// Fetches the full-resolution frame for a given instant out of the rolling video buffer.
//
// This exists because the frame motion is detected on is useless as a picture. Detection runs on a
// deliberately tiny 320x180 copy - comparing full 5-megapixel frames twice a second would cost more
// CPU than the rest of the system put together - so the moment is known but the image is not.
//
// The established designs all solve that the same way: decode once, keep the frames, and retrieve
// the one the detection fired on rather than taking a fresh photograph afterwards. Frigate keeps
// raw frames in shared memory and hands the tracker a (camera, frame_time, frame_id) reference to
// look up; ZoneMinder keeps a shared-memory ring of the last n images; Blue Iris keeps a pre-trigger
// buffer measured in frames. This does the same thing against the clip-segment buffer already on
// disk, which costs nothing to maintain because those segments are copied, never re-encoded.
//
// The lookup is by timestamp and nothing else. There is no search, no scoring and no heuristic to
// tune, which is what makes it behave identically on a fast machine and a slow one - the frame time
// comes from TriggerZoneMonitor's capture loop, which cannot fall behind (see FrameSlot).
public static class TriggerRewind
{
    // -ss BEFORE -i is input seeking: ffmpeg jumps to the nearest keyframe and decodes forward from
    // there, instead of decoding the whole concat and discarding all but one frame. Since ffmpeg
    // 2.1 that is also frame-accurate, so the speed costs no precision.
    //
    // -threads 1 is deliberate. Measured on this hardware, letting ffmpeg use every core took the
    // same wall time and burned 50% more CPU - a pointless burst on a machine whose spare capacity
    // is what keeps the detection loop live.
    public static List<string> BuildExtractArguments(string concatListPath, double offsetSeconds, string outputPath) =>
    [
        "-v", "error",
        "-threads", "1",
        "-ss", FormatSeconds(offsetSeconds),
        "-f", "concat",
        "-safe", "0",
        "-i", concatListPath,
        "-frames:v", "1",
        "-q:v", "2",
        "-y", outputPath
    ];

    // Seconds in a form ffmpeg parses regardless of the machine's locale. A comma decimal separator
    // is silently truncated to whole seconds, which would be a sub-second error nobody would ever
    // trace back to a culture setting.
    public static string FormatSeconds(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);

    // How far into a concatenated run of segments a given instant falls. The run's first segment is
    // t=0 for the concat demuxer, which is why the run has to be contiguous - see
    // ClipRecorder.ContiguousRunEndingAtNewest.
    public static double OffsetInto(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> run, DateTimeOffset at) =>
        (at - run[0].StartsAt).TotalSeconds;
}

// CaptureDelaySeconds is how long after the frame was captured the fetch completed - the pipeline's
// own latency, not a rewind. Nothing is "rewound" relative to the trigger any more: the trigger's
// OccurredAt IS the frame's capture time, so the image and the row describe the same instant by
// construction. This number exists purely as the diagnostic that was missing when a three-second
// detection lag went unnoticed for months.
public sealed record RewindPick(byte[] ImageBytes, double CaptureDelaySeconds, DateTimeOffset ChosenAt);

// The IO half: processes, files, and waiting for the muxer.
//
// Every failure path returns null. A frame that cannot be fetched means falling back to the live
// snapshot, which is what the trigger would have had anyway - losing the event instead would trade
// a cosmetic problem for a real one.
public sealed class TriggerRewindService(
    ClipRecorder clips,
    IConfiguration configuration,
    ILogger<TriggerRewindService> logger)
{
    private readonly string _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";

    // Below-normal is nice +10 on Linux and needs no privilege. Fetching a frame must always yield
    // to the capture loop; a picture arriving a moment later costs nothing, a stalled capture loop
    // costs the next several seconds of detection.
    private const ProcessPriorityClass RewindPriority = ProcessPriorityClass.BelowNormal;

    private static readonly TimeSpan SegmentPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(15);

    // The frame captured at frameAt, or null if the buffer cannot supply it.
    //
    // lookbackSeconds is not a search window - the instant is known exactly. It only bounds how far
    // into the past a request is allowed to reach, so a wildly wrong timestamp (a clock step, a
    // camera reconnecting) can't pull up footage from minutes ago and present it as now.
    public async Task<RewindPick?> TryFetchAsync(
        Guid cameraId, DateTimeOffset frameAt, int lookbackSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var age = (DateTimeOffset.UtcNow - frameAt).TotalSeconds;
            if (age > lookbackSeconds)
            {
                logger.LogWarning(
                    "Trigger rewind was asked for a frame {Age:0.0}s old on camera {CameraId}, beyond the {Lookback}s " +
                    "limit; using the live snapshot instead", age, cameraId, lookbackSeconds);
                return null;
            }

            await WaitForSegmentCoveringAsync(cameraId, frameAt, cancellationToken);

            var run = ClipRecorder.ContiguousRunEndingAtNewest(UsableSegments(cameraId));
            if (run.Count == 0 || frameAt < run[0].StartsAt)
            {
                logger.LogWarning(
                    "Trigger rewind found no buffered video covering the trigger on camera {CameraId}; " +
                    "using the live snapshot instead", cameraId);
                return null;
            }

            var bytes = await ExtractAsync(run, frameAt, cancellationToken);
            if (bytes is null) return null;

            // Includes the wait for the segment holding the frame to be closed by the muxer, so it
            // is dominated by segment length rather than by anything being wrong.
            var delay = (DateTimeOffset.UtcNow - frameAt).TotalSeconds;
            logger.LogInformation(
                "Trigger rewind for camera {CameraId}: used the frame captured at the moment motion was detected, " +
                "fetched {Delay:0.00}s later", cameraId, delay);

            return new RewindPick(bytes, delay, frameAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Trigger rewind failed for camera {CameraId}; using the live snapshot instead", cameraId);
            return null;
        }
    }

    // Same fetch for a camera group's companions, which have no detection of their own and instead
    // borrow the instant from the camera that did fire.
    public async Task<byte[]?> TryExtractAtAsync(Guid cameraId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        try
        {
            var run = ClipRecorder.ContiguousRunEndingAtNewest(UsableSegments(cameraId));
            if (run.Count == 0 || at < run[0].StartsAt) return null;

            return await ExtractAsync(run, at, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Companion rewind failed for camera {CameraId}; using the live snapshot instead", cameraId);
            return null;
        }
    }

    private async Task<byte[]?> ExtractAsync(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> run, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var listPath = Path.Combine(Path.GetTempPath(), $"rewindlist-{Guid.NewGuid():N}.txt");
        var framePath = Path.Combine(Path.GetTempPath(), $"rewind-{Guid.NewGuid():N}.jpg");
        await File.WriteAllLinesAsync(listPath, run.Select(s => ClipRecorder.ConcatListLine(s.Path)), cancellationToken);

        try
        {
            var offset = Math.Max(0, TriggerRewind.OffsetInto(run, at));
            var ok = await FfmpegProcess.RunAsync(
                _ffmpegPath, TriggerRewind.BuildExtractArguments(listPath, offset, framePath),
                ExtractTimeout, logger, "rewind frame fetch", RewindPriority, cancellationToken);

            if (!ok || !File.Exists(framePath)) return null;

            var bytes = await File.ReadAllBytesAsync(framePath, cancellationToken);
            return TriggerImagePaths.IsCompleteJpeg(bytes) ? bytes : null;
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* best effort */ }
            try { File.Delete(framePath); } catch { /* best effort */ }
        }
    }

    // The segment holding the trigger is still being written when the trigger fires, and the muxer
    // only closes it when it opens the next one - so a segment starting after the trigger is the
    // proof that the one containing it is complete.
    //
    // Polled rather than slept for a fixed time: a camera with a 10-second GOP needs longer than one
    // with a 2-second GOP, and a muxer that happens to be ahead needs no wait at all. Past the
    // deadline, carry on with what exists.
    private async Task WaitForSegmentCoveringAsync(Guid cameraId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(ClipRecorder.SegmentSeconds * 2 + 2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (clips.BufferedSegments(cameraId).Any(s => s.StartsAt > at)) return;
            await Task.Delay(SegmentPollInterval, cancellationToken);
        }

        logger.LogInformation(
            "Trigger rewind stopped waiting for camera {CameraId}'s buffer to close the segment holding the trigger; " +
            "using what is already on disk", cameraId);
    }

    // The newest file is the one ffmpeg is writing into right now; reading it would hand the
    // demuxer a truncated stream and shift every offset after it.
    private IReadOnlyList<(string Path, DateTimeOffset StartsAt)> UsableSegments(Guid cameraId)
    {
        var all = clips.BufferedSegments(cameraId);
        return all.Count <= 1 ? [] : all.Take(all.Count - 1).ToList();
    }
}
