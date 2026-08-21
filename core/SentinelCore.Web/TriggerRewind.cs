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
    // -pix_fmt yuvj420p is what stops ~1% of fetches returning nothing at all.
    //
    // mjpeg refuses to encode a frame that is not full-range: "Non full-range YUV is non-standard,
    // set strict_std_compliance to at most unofficial to use it", after which the encoder never
    // opens, ffmpeg exits 234, and the caller falls back to a live snapshot taken seconds after the
    // moment that actually mattered - precisely the failure this whole file exists to prevent.
    //
    // Left to itself ffmpeg usually avoids that: yuv420p and yuvj420p are both on mjpeg's list, and
    // the negotiation normally lands on yuvj420p and converts. "Usually" is the problem. The choice
    // follows the range the DECODED FRAME carries, which comes from the SPS the camera happened to
    // emit, so a stream that starts signalling limited range - after a day/night switch, or any
    // other time the camera re-initialises its encoder - hands the filtergraph a frame that keeps
    // yuv420p, and mjpeg then rejects it. Nothing in this process's own state predicts it, which is
    // why it reads as intermittent.
    //
    // Naming the format pins the one variable the encoder refuses on: a yuvj pixel format carries
    // full range by definition, so the conversion always happens and the refusal has nothing to
    // fire on. Reproduced exactly - an encoder handed yuv420p at limited range fails with the
    // production error text verbatim, and the same run with this flag succeeds - and verified inert
    // on the healthy path: byte-identical output across 90 fetches (15 real segments x 6 offsets),
    // because yuvj420p is already what the negotiation was picking on every one of them.
    //
    // This supersedes an earlier reading of the same failure that blamed multi-threaded encoding
    // cloning the encoder context. Capping the encoder's threads did change the error - the clone
    // path is gone - but the failures continued at the same rate afterwards (5 more in the 16h
    // after that shipped), so the clone was where it surfaced, not why.
    //
    // -threads 1 stays, on its own merits: fetching a frame must always yield to the capture loop.
    // It has to appear on BOTH sides of -i, because -threads is a per-context option and which
    // context it lands in depends entirely on where it sits: before -i it configures the h264
    // DECODER, after it the mjpeg ENCODER. With it only in front, the encoder kept ffmpeg's default
    // of one thread per core, so writing a single 2560x1920 JPEG spun up nine mjpeg contexts on this
    // eight-core box - the opposite of yielding, on the machine whose spare capacity is the whole
    // reason the detection loop stays live. User CPU over 20 runs: 10.12s to 8.57s.
    public static List<string> BuildExtractArguments(string segmentPath, double offsetSeconds, string outputPath) =>
    [
        "-v", "error",
        "-threads", "1",
        "-i", segmentPath,
        "-ss", FormatSeconds(offsetSeconds),
        "-threads", "1",
        "-frames:v", "1",
        "-q:v", "2",
        "-pix_fmt", "yuvj420p",
        "-y", outputPath
    ];

    // Seconds in a form ffmpeg parses regardless of the machine's locale. A comma decimal separator
    // is silently truncated to whole seconds, which would be a sub-second error nobody would ever
    // trace back to a culture setting.
    public static string FormatSeconds(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);

    // A segment's boundaries to sub-second precision, which its filename does not carry.
    //
    // ffmpeg names each segment with strftime, and strftime has no sub-second field, so the name is
    // the true start TRUNCATED to the whole second - up to a second early. An offset measured from
    // the name therefore seeks that much too far into the file. The error is not a fixed bias that
    // would quietly cancel: these segments run ~1.94s against a nominal 2s, so the missing fraction
    // drifts through the entire 0-1s range and back about every 25 seconds.
    //
    // Measured against the cameras' burned-in clock across one full drift cycle - 18 consecutive
    // segments, frame taken at the same stream position in each - the frame fetched for an instant
    // ran a whole second late on exactly the six segments whose fraction had drifted past ~0.6, and
    // matched on the rest. Where the overshoot ran past the last frame in the file ffmpeg wrote
    // nothing at all and exited 0, which is the "produced no frame" fallback to a live snapshot: 4
    // of 155 fetches on the last buffer.
    //
    // The muxer closes a segment when it opens the next, so the PREVIOUS file's last-write time is
    // this one's true start. On this buffer that agrees with the ext4 birth timestamp to within a
    // millisecond. File.GetCreationTimeUtc is no help - on Linux .NET hands back the write time,
    // not the birth time - so the previous file's close is the measurement actually available.
    //
    // `closedAt` is null in tests that are only exercising the nominal arithmetic, and null is also
    // the honest answer on a filesystem whose timestamps cannot be trusted: refinement is skipped
    // and every boundary is the filename's, exactly as before.
    public static DateTimeOffset RefinedStart(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, int index,
        Func<string, DateTimeOffset>? closedAt)
    {
        var nominal = ordered[index].StartsAt;
        if (closedAt is null || index <= 0) return nominal;

        return Believable(closedAt(ordered[index - 1].Path), nominal);
    }

    // A segment ends where the next one begins, so its refined end is that segment's refined start.
    public static DateTimeOffset RefinedEnd(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, int index,
        Func<string, DateTimeOffset>? closedAt)
    {
        var nominalEnd = ClipRecorder.SegmentEnd(ordered, index);
        if (closedAt is null || index + 1 >= ordered.Count) return nominalEnd;

        // SegmentEnd returning anything other than the next start means its clamp fired: it judged
        // the gap an outage rather than one long segment. Sharpening that boundary would be
        // claiming coverage across a hole, which is the one thing this lookup must never do.
        return nominalEnd == ordered[index + 1].StartsAt
            ? RefinedStart(ordered, index + 1, closedAt)
            : nominalEnd;
    }

    // A measured boundary is only believable if it still truncates to the second it is named after,
    // which is precisely what strftime guaranteed when it wrote that name. A hole in the buffer
    // leaves the previous file closing long before the next one opens, the check fails, and the
    // nominal boundary stands - so an outage still reads as an outage.
    private static DateTimeOffset Believable(DateTimeOffset measured, DateTimeOffset nominal) =>
        measured >= nominal && measured < nominal + TimeSpan.FromSeconds(1) ? measured : nominal;

    // How far into its own segment a given instant falls. Measured against that one file, so a hole
    // anywhere else in the buffer cannot shift it - which is what made the old concat offset fragile
    // enough to need a contiguity check in front of it.
    public static double OffsetInto(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, int index, DateTimeOffset at,
        Func<string, DateTimeOffset>? closedAt) =>
        (at - RefinedStart(ordered, index, closedAt)).TotalSeconds;

    // The segment whose first frame stands in for an instant that overshot the end of its own
    // segment, or -1 when nothing may stand in for it.
    //
    // A segment's refined span is measured from when the muxer closed the files either side of it,
    // and that wall-clock span runs slightly longer than the media actually in the file - these
    // segments hold ~1.954s against a refined span that reaches ~1.99s. An instant landing in that
    // sliver is inside the segment by the boundary math and past its last frame in the file, so the
    // output seek runs off the end and ffmpeg writes nothing. RefinedStart already narrows this
    // (it is what moved most instants into the right file at all), but it cannot close it: the two
    // measurements are of different things.
    //
    // Measured on the live buffer over the 36 hours to 2026-08-12, 57 of 67 "produced no frame"
    // failures asked for an offset between 1.84s and 2.02s of a ~1.95s file - and every one of them
    // fell back to a live snapshot, a photograph of a moment seconds later, which is the exact
    // substitution this lookup exists to prevent.
    //
    // The nearest real frame to an instant in that sliver is the FIRST frame of the next segment,
    // milliseconds away on the other side of the boundary. That is only true across a boundary,
    // though: a next segment starting more than one segment-length after the instant means a hole,
    // and a frame from the far side of an outage is worse than no frame at all, so that returns -1
    // and the caller keeps its honest live-snapshot fallback. In an intact buffer the instant is
    // always within a segment-length of the next start, so the test only ever rejects real holes.
    public static int FallForwardIndex(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, int index, DateTimeOffset at,
        Func<string, DateTimeOffset>? closedAt = null)
    {
        var next = index + 1;
        if (index < 0 || next >= ordered.Count) return -1;

        var gap = GapToNextStart(ordered, index, at, closedAt);
        return gap >= 0 && gap <= ClipRecorder.SegmentSeconds ? next : -1;
    }

    // How far after `at` the next segment opens - which is how stale the stand-in frame would be.
    public static double GapToNextStart(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, int index, DateTimeOffset at,
        Func<string, DateTimeOffset>? closedAt = null) =>
        (RefinedStart(ordered, index + 1, closedAt) - at).TotalSeconds;

    // Which segment holds `at`, or -1 if the buffer has no cover for that instant.
    //
    // `ordered` is every segment on disk, oldest first. The newest is never selectable - ffmpeg is
    // still writing it, and reading a half-written file hands the decoder a truncated stream - but
    // it is still passed in, because the end of the last selectable segment is measured from where
    // the NEXT one begins. Excluding it from the list outright would leave that segment measured at
    // its nominal length instead, which is the case most likely to be asked for: the trigger that
    // just fired lands at the live end of the buffer.
    //
    // Searching newest-first because that is where triggers land, and returning -1 rather than the
    // nearest segment on a miss. A frame from the wrong side of an outage is worse than no frame -
    // the caller falls back to the live snapshot, which at least does not claim to be the moment
    // that fired.
    public static int IndexOfSegmentContaining(
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, DateTimeOffset at,
        Func<string, DateTimeOffset>? closedAt = null)
    {
        for (var i = ordered.Count - 2; i >= 0; i--)
        {
            // Against the refined start, not the filename's: an instant in the fraction of a second
            // between the two belongs to the segment BEFORE this one, and the loop finds it there
            // on the next pass. Selecting on the filename put roughly a quarter of all instants in
            // the wrong file, at the near edge where the overshoot was largest.
            if (at < RefinedStart(ordered, i, closedAt)) continue;
            return at < RefinedEnd(ordered, i, closedAt) ? i : -1;
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

    // When the muxer closed a segment, which is the same instant it opened the next one. This is
    // the sub-second start the filename could not record; see RefinedStart. A file that has gone
    // (retention sweeping the tail of the buffer mid-lookup) reports a close far in the past, which
    // the believability check rejects, leaving the nominal boundary in place.
    private static DateTimeOffset ClosedAt(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTimeOffset.MinValue; }
        catch (UnauthorizedAccessException) { return DateTimeOffset.MinValue; }
    }

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
            var index = TriggerRewind.IndexOfSegmentContaining(ordered, frameAt, ClosedAt);
            if (index < 0)
            {
                logger.LogWarning(
                    "Trigger rewind found no buffered video covering the trigger on camera {CameraId}; " +
                    "using the live snapshot instead", cameraId);
                return null;
            }

            var bytes = await ExtractAsync(ordered, index, frameAt, cancellationToken);
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
            var index = TriggerRewind.IndexOfSegmentContaining(ordered, at, ClosedAt);
            if (index < 0) return null;

            return await ExtractAsync(ordered, index, at, cancellationToken);
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
        IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, int index, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var segment = ordered[index];
        var offset = Math.Max(0, TriggerRewind.OffsetInto(ordered, index, at, ClosedAt));

        var bytes = await ExtractFrameAsync(segment.Path, offset, cancellationToken);
        if (bytes is not null) return bytes;

        // ffmpeg exits 0 having written nothing when a seek finds no frame - the failure that used
        // to arrive as a corrupt-looking encoder error rather than a missing file. The usual cause
        // is an instant in the sliver between this file's last frame and where the next segment
        // opens; see TriggerRewind.FallForwardIndex, which decides whether the next segment's first
        // frame may stand in for it.
        var fallForward = TriggerRewind.FallForwardIndex(ordered, index, at, ClosedAt);
        if (fallForward < 0)
        {
            logger.LogWarning(
                "Trigger rewind found the segment for {At:HH:mm:ss.fff} on camera stream {Segment} but ffmpeg " +
                "produced no frame at {Offset:0.###}s into it, and no adjoining segment can stand in for it; " +
                "using the live snapshot instead",
                at, Path.GetFileName(segment.Path), offset);
            return null;
        }

        // Offset 0 - the head of the file, which is complete from the muxer's first write, so this
        // holds even when the next segment is the one still being written.
        var next = ordered[fallForward];
        bytes = await ExtractFrameAsync(next.Path, 0, cancellationToken);
        if (bytes is null)
        {
            logger.LogWarning(
                "Trigger rewind found no frame for {At:HH:mm:ss.fff} at {Offset:0.###}s into {Segment} nor at the " +
                "head of {Next}; using the live snapshot instead",
                at, offset, Path.GetFileName(segment.Path), Path.GetFileName(next.Path));
            return null;
        }

        logger.LogInformation(
            "Trigger rewind for {At:HH:mm:ss.fff} ran {Offset:0.###}s past the last frame of {Segment}; used the " +
            "first frame of {Next} instead, {Gap:0.###}s away",
            at, offset, Path.GetFileName(segment.Path), Path.GetFileName(next.Path),
            TriggerRewind.GapToNextStart(ordered, index, at, ClosedAt));

        return bytes;
    }

    // One ffmpeg run for one frame, or null if it did not produce a usable one. Null covers every
    // way that can happen - a non-zero exit, the exit-0-wrote-nothing case a seek past the last
    // frame produces, and a truncated JPEG - because the caller's next move is the same for all
    // three.
    private async Task<byte[]?> ExtractFrameAsync(
        string segmentPath, double offsetSeconds, CancellationToken cancellationToken)
    {
        var framePath = Path.Combine(Path.GetTempPath(), $"rewind-{Guid.NewGuid():N}.jpg");

        try
        {
            var ok = await FfmpegProcess.RunAsync(
                _ffmpegPath, TriggerRewind.BuildExtractArguments(segmentPath, offsetSeconds, framePath),
                ExtractTimeout, logger, "rewind frame fetch", RewindPriority, cancellationToken);

            if (!ok || !File.Exists(framePath)) return null;

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
