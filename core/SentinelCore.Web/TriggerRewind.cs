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
    // One segment in, -ss AFTER it. Both halves of that were wrong before, and each was wrong on
    // its own:
    //
    // The input used to be a concat list of the whole contiguous run - up to 40 files, ~80 seconds -
    // with -ss placed BEFORE it on the reasoning that input seeking would jump straight to the right
    // keyframe. The concat demuxer does not work that way: it walks the segments. Measured on this
    // hardware against a live buffer, a seek near the end of the run took 20.2 SECONDS and 22.5s of
    // CPU, against 0.8s for the same frame out of the one segment holding it. The cost scaled with
    // how much buffer happened to be on disk, on the machine whose spare capacity is the whole
    // reason the detection loop stays live.
    //
    // -ss then has to move after -i, because input seeking on these segments does not just cost
    // precision, it silently returns NOTHING. A segment is commonly a single GOP with one keyframe
    // at its head, so an input seek past that keyframe finds no later one and hits EOF with no
    // frames written - ffmpeg exits 0 and the file never appears. Measured on a 2.017s segment:
    // input seeking returned no frame at all at offsets 1.2s and beyond, and returned the SAME
    // frame for 0.4s and 0.8s (snapped to the keyframe) where it did work. Output seeking returned
    // a distinct, correct frame at every offset - verified against the cameras' burned-in clock,
    // which advanced by two seconds across the segment as it should.
    //
    // Decoding a whole ~2s segment to drop most of it is exactly the waste input seeking was meant
    // to avoid. At this size that waste is 0.8s and bounded; it does not grow with the buffer.
    //
    // -threads 1 is deliberate: fetching a frame must always yield to the capture loop.
    public static List<string> BuildExtractArguments(string segmentPath, double offsetSeconds, string outputPath) =>
    [
        "-v", "error",
        "-threads", "1",
        "-i", segmentPath,
        "-ss", FormatSeconds(offsetSeconds),
        "-frames:v", "1",
        "-q:v", "2",
        "-y", outputPath
    ];

    // Seconds in a form ffmpeg parses regardless of the machine's locale. A comma decimal separator
    // is silently truncated to whole seconds, which would be a sub-second error nobody would ever
    // trace back to a culture setting.
    public static string FormatSeconds(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);

    // How far into its own segment a given instant falls. Measured against that one file, so a hole
    // anywhere else in the buffer cannot shift it - which is what made the old concat offset fragile
    // enough to need a contiguity check in front of it.
    public static double OffsetInto((string Path, DateTimeOffset StartsAt) segment, DateTimeOffset at) =>
        (at - segment.StartsAt).TotalSeconds;

    // Which segment holds `at`, or -1 if the buffer has no cover for that instant.
    //
    // `ordered` is every segment on disk, oldest first. The newest is never selectable - ffmpeg is
    // still writing it, and reading a half-written file hands the decoder a truncated stream - but
    // it is still passed in, because SegmentEnd measures a segment's real length from where the
    // NEXT one begins. Excluding it from the list outright would leave the last selectable segment
    // measured at its nominal length instead, which is the case most likely to be asked for: the
    // trigger that just fired lands at the live end of the buffer.
    //
    // Searching newest-first because that is where triggers land, and returning -1 rather than the
    // nearest segment on a miss. A frame from the wrong side of an outage is worse than no frame -
    // the caller falls back to the live snapshot, which at least does not claim to be the moment
    // that fired.
    public static int IndexOfSegmentContaining(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, DateTimeOffset at)
    {
        for (var i = ordered.Count - 2; i >= 0; i--)
        {
            if (at < ordered[i].StartsAt) continue;
            return at < ClipRecorder.SegmentEnd(ordered, i) ? i : -1;
        }

        return -1;
    }
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

            var ordered = clips.BufferedSegments(cameraId);
            var index = TriggerRewind.IndexOfSegmentContaining(ordered, frameAt);
            if (index < 0)
            {
                logger.LogWarning(
                    "Trigger rewind found no buffered video covering the trigger on camera {CameraId}; " +
                    "using the live snapshot instead", cameraId);
                return null;
            }

            var bytes = await ExtractAsync(ordered[index], frameAt, cancellationToken);
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
            var ordered = clips.BufferedSegments(cameraId);
            var index = TriggerRewind.IndexOfSegmentContaining(ordered, at);
            if (index < 0) return null;

            return await ExtractAsync(ordered[index], at, cancellationToken);
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
        (string Path, DateTimeOffset StartsAt) segment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var framePath = Path.Combine(Path.GetTempPath(), $"rewind-{Guid.NewGuid():N}.jpg");

        try
        {
            var offset = Math.Max(0, TriggerRewind.OffsetInto(segment, at));
            var ok = await FfmpegProcess.RunAsync(
                _ffmpegPath, TriggerRewind.BuildExtractArguments(segment.Path, offset, framePath),
                ExtractTimeout, logger, "rewind frame fetch", RewindPriority, cancellationToken);

            // ffmpeg exits 0 having written nothing when a seek finds no frame - the failure that
            // used to arrive as a corrupt-looking encoder error rather than a missing file. Treat
            // the absent file as the real signal and say so, because silently falling back to the
            // live snapshot is what put a photograph of a different moment on the event in the
            // first place.
            if (!ok || !File.Exists(framePath))
            {
                logger.LogWarning(
                    "Trigger rewind found the segment for {At:HH:mm:ss.fff} on camera stream {Segment} but ffmpeg " +
                    "produced no frame at {Offset:0.###}s into it; using the live snapshot instead",
                    at, Path.GetFileName(segment.Path), offset);
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(framePath, cancellationToken);
            return TriggerImagePaths.IsCompleteJpeg(bytes) ? bytes : null;
        }
        finally
        {
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
}
