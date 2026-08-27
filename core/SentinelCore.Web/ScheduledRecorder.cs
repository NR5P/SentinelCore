using System.Globalization;

namespace SentinelCore.Web;

// Read live from the shared appsettings.json rather than bound once at startup, like every other
// settings section here, so changing the retention window doesn't need a restart.
public sealed class ScheduledRecordingOptions
{
    public const string SectionName = "ScheduledRecording";

    // How long a finished recording is kept. Deliberately much shorter than the alert history's
    // retention: these are main-stream video at full bitrate, and an hour of one is roughly what a
    // month of trigger clips costs. Nothing else in this app can produce files this large, so
    // nothing else has learned to be careful about them.
    public int RetentionDays { get; init; } = 14;

    // The free-space floor a recording will not cross. See ScheduledRecorder.HasRoomToRecord.
    public double MinimumFreeGigabytes { get; init; } = 5;

    // How long a recording started with a camera's record button may run before it stops itself.
    // A hand-started recording has no window to end it - that is the point of the button - so
    // without a cap the only thing standing between a forgotten press and a full disk is
    // MinimumFreeGigabytes, which is a last resort rather than a plan. Ten minutes of main-stream
    // video is roughly 300 MB, so forgetting costs an annoyance instead of an outage.
    public int ManualMaxMinutes { get; init; } = 10;
}

// Captures one recording window end to end: pull the camera through go2rtc, write it to disk as it
// arrives, and concatenate the result into a playable mp4 when the window closes.
//
// Writes short .ts segments rather than one long mp4, for the same reason ClipRecorder does even
// though nothing here needs a rolling buffer. An mp4 only becomes playable when its moov atom is
// written at the end, so a process killed 55 minutes into an hour leaves a file nobody can open -
// which is precisely the hour somebody wanted. Segments are complete the moment they close: a
// crash costs the few seconds in flight, and everything before it survives to be concatenated.
// That is also what makes resuming after a restart work at all (see RecordingScheduleWorker) -
// a second ffmpeg simply adds more segments to the same directory, and the concatenation at the
// end neither knows nor cares that there were two of them.
//
// Deliberately does NOT reuse TriggerZoneMonitor's segment buffer, even though a camera with clips
// enabled is already writing one. That buffer is pruned to `pre + post + 60s` (see
// ClipRecorder.PruneBuffer), so holding a half-hour window in it would mean teaching the prune
// logic about recordings - and a mistake there breaks trigger clips, which are the thing this
// system exists to produce. A directory of its own cannot.
public sealed class ScheduledRecorder(
    Go2RtcManager go2Rtc,
    CameraSource cameraSource,
    IRecordingStore recordings,
    AppSettingsFile mainAppSettings,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ScheduledRecorder> logger)
{
    // Long enough that an eight-hour window doesn't produce tens of thousands of files, short
    // enough that a crash loses an unremarkable amount. ClipRecorder's 2s exists to keep clip edges
    // tight against a trigger instant; nothing here is trying to hit an instant.
    private const int SegmentSeconds = 10;

    private const string SegmentPrefix = "rec_";

    // How long after the window's nominal end ffmpeg is given before it is killed. It should have
    // stopped itself by then - `-t` is what actually ends the recording - so this only covers a
    // stream that has stalled without disconnecting.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(20);

    // How often the disk is re-checked while recording. A window is minutes to hours long and the
    // floor is measured in gigabytes, so there is nothing to be gained by checking harder.
    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromSeconds(30);

    private readonly string _root = ResolveRoot(configuration, environment);
    private readonly string _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";

    // Below this much free space a recording will not start, and a running one stops itself. An
    // hour of a 4 Mbit/s main stream is about 1.8 GB and nothing here can know how many windows are
    // configured, so the guard is a floor to stay above rather than a prediction of what this
    // recording will use. The point is that the machine keeps running: the databases, the trigger
    // clips and the alert pipeline all need somewhere to write, and they fail in far less obvious
    // ways than a recording that says it stopped.
    private static long MinimumFreeBytes(ScheduledRecordingOptions options) =>
        options.MinimumFreeGigabytes > 0 ? (long)(options.MinimumFreeGigabytes * 1024 * 1024 * 1024) : 0;

    public async Task<ScheduledRecordingOptions> GetOptionsAsync(CancellationToken cancellationToken) =>
        await mainAppSettings.GetSectionAsync<ScheduledRecordingOptions>(
            ScheduledRecordingOptions.SectionName, cancellationToken) ?? new ScheduledRecordingOptions();

    public string RecordingsDirectory(Guid cameraId)
    {
        var dir = Path.Combine(_root, "scheduled-recordings", cameraId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string SegmentDirectory(Guid recordingId)
    {
        var dir = Path.Combine(_root, "recording-buffer", recordingId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Runs a window to completion and writes the outcome to the store. Never throws for an ordinary
    // failure - a camera that won't answer, a disk that's too full, ffmpeg dying - because every one
    // of those is a result the row should carry rather than an exception the worker has to catch to
    // stay alive. Cancellation is the exception, and means shutdown: whatever has been captured is
    // finalized as partial on the way out.
    // stopRequested ends the recording early and cleanly, exactly the way the disk watch below
    // does: ffmpeg is killed, and whatever it had already written is finalized into a playable
    // file. That is what the record button's second press pulls.
    public async Task RunAsync(
        Camera camera, Recording recording, CancellationToken cancellationToken, CancellationToken stopRequested = default)
    {
        var segmentDirectory = SegmentDirectory(recording.Id);

        try
        {
            var options = await GetOptionsAsync(cancellationToken);

            if (!HasRoomToRecord(options, out var freeBytes))
            {
                var refusal = $"Not enough free disk space to record - {Describe(freeBytes)} left, {Describe(MinimumFreeBytes(options))} needed.";
                logger.LogWarning(
                    "Refusing scheduled recording {RecordingId} for camera {CameraId}: {Detail}",
                    recording.Id, camera.Id, refusal);
                await FinishAsync(recording, segmentDirectory, Recording.StatusFailed, refusal, cancellationToken);
                return;
            }

            // Whatever is left of the window right now. On a first start that is essentially the
            // whole thing; after a restart it is only the remainder, which is what stops a resumed
            // recording from running past the end its owner asked for.
            var remaining = recording.RequestedEndUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                await FinalizeFromSegmentsAsync(recording, segmentDirectory, "The window had already ended.", cancellationToken);
                return;
            }

            if (recording.ActualStartUtc is null)
                await recordings.SetStartedAsync(recording.Id, DateTimeOffset.UtcNow, cancellationToken);

            await go2Rtc.EnsureRecordableAsync(camera, cancellationToken);

            // The sidecar's own address for this stream, which is what makes the recording share a
            // connection instead of spending one. Deliberately not CameraSource.ResolveUrl: that
            // hands back the camera's *direct* address for an ordinary camera, which is exactly the
            // second dial this path exists to avoid. For a browser-published camera the two are the
            // same address anyway - there is nowhere else its video exists.
            var source = cameraSource.PublishedUrl(camera.Id);

            logger.LogInformation(
                "Recording {Name} on camera {CameraId} for {Minutes:0.#} more minute(s) into {Directory}",
                recording.Name, camera.Id, remaining.TotalMinutes, segmentDirectory);

            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopRequested);
            var diskWatch = WatchDiskAsync(recording, options, stopping);

            var ran = await FfmpegProcess.RunAsync(
                _ffmpegPath,
                BuildArguments(source, camera, recording, remaining),
                remaining + StopGrace,
                logger,
                "scheduled recording",
                priority: null,
                stopping.Token);

            await stopping.CancelAsync();
            await diskWatch;

            // Stopped by hand is neither a failure nor a short recording. The window this row was
            // created with was only the cap on how long the button was allowed to hold the camera,
            // so measuring the footage against it would file every hand-stopped recording as
            // partial - "captured 0.5 of 10 minutes" - for doing exactly what was asked. Moving the
            // requested end to the moment of the second press makes the row describe what somebody
            // actually wanted, and the completeness check below then agrees with them.
            if (stopRequested.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var stoppedAt = DateTimeOffset.UtcNow;
                await recordings.SetRequestedEndAsync(recording.Id, stoppedAt, cancellationToken);
                recording = recording with { RequestedEndUtc = stoppedAt };
                await FinalizeFromSegmentsAsync(recording, segmentDirectory, string.Empty, cancellationToken);
                return;
            }

            // `ran` false means ffmpeg was killed or exited non-zero - it does not mean there is
            // nothing on disk, and the segments already written are exactly as watchable as they
            // would have been had it finished. So the outcome is decided by what was captured, not
            // by how ffmpeg felt about it.
            var detail = ran ? string.Empty : "The stream ended before the window did.";
            await FinalizeFromSegmentsAsync(recording, segmentDirectory, detail, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown. Finalize with a token that isn't already cancelled, or the row stays stuck
            // in "recording" and the next boot has to work out what happened to it.
            await FinalizeFromSegmentsAsync(
                recording, segmentDirectory, "The server shut down during this recording.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled recording {RecordingId} for camera {CameraId} failed", recording.Id, camera.Id);
            await FinalizeFromSegmentsAsync(recording, segmentDirectory, ex.Message, cancellationToken);
        }
    }

    // Closes out a recording that a restart found still in progress, without running any more of
    // it. The segments already on disk become the file. Used for hand-started recordings, which are
    // never resumed - see RecordingScheduleWorker.ResumeInterruptedAsync.
    //
    // Deliberately leaves the requested end where it is, so this lands as `partial` rather than
    // `complete`. Nobody chose to stop this one - the process died under it - and "captured 2 of
    // the 10 minutes it was allowed" is the honest description of a file that stops mid-scene.
    // Moving the end to make the arithmetic say complete would only hide that.
    public Task FinalizeInterruptedAsync(Recording recording, CancellationToken cancellationToken) =>
        FinalizeFromSegmentsAsync(
            recording, SegmentDirectory(recording.Id),
            "The server restarted while this was recording.", cancellationToken);

    private List<string> BuildArguments(string source, Camera camera, Recording recording, TimeSpan remaining)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            // TCP because a long recording over UDP accumulates exactly the packet loss that makes
            // the concatenation stutter, and there is no interactivity here to pay latency for.
            "-rtsp_transport", "tcp",
            "-i", source,
            "-map", "0:v:0"
        };

        // Audio is the camera's setting and the schedule's, both - a camera with no audio has none
        // to offer, and a window recorded for one specific purpose may not want it. Optional either
        // way (`?`), so a stream that turns out to have no audio track records video rather than
        // failing outright.
        if (camera.IncludeAudio) args.AddRange(["-map", "0:a?"]);

        args.AddRange([
            // Never re-encode. The whole feature is affordable only because this is a copy: an hour
            // of transcoded 4K would cost more CPU than everything else this server does combined.
            "-c", "copy",
            "-f", "segment",
            "-segment_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-segment_format", "mpegts",
            "-reset_timestamps", "1",
            "-strftime", "1",
            // What actually ends the recording. Everything else - the grace period, the worker's
            // own clock - is a backstop for this not happening.
            "-t", remaining.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            Path.Combine(SegmentDirectory(recording.Id), $"{SegmentPrefix}%Y%m%d-%H%M%S.ts")
        ]);

        return args;
    }

    // Stops a recording that is filling the disk. Cancelling the token kills ffmpeg, and everything
    // captured up to that point is still finalized - a partial recording and a working machine,
    // rather than a complete one and a server that can no longer write anything.
    private async Task WatchDiskAsync(
        Recording recording, ScheduledRecordingOptions options, CancellationTokenSource stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(DiskCheckInterval, stopping.Token);

                if (HasRoomToRecord(options, out var freeBytes)) continue;

                logger.LogWarning(
                    "Stopping recording {RecordingId} early - only {Free} of free disk space left",
                    recording.Id, Describe(freeBytes));
                await stopping.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the recording finished and cancelled this watch on its way out.
        }
    }

    private bool HasRoomToRecord(ScheduledRecordingOptions options, out long freeBytes)
    {
        freeBytes = long.MaxValue;
        var minimum = MinimumFreeBytes(options);
        if (minimum <= 0) return true;

        try
        {
            freeBytes = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root)) ?? "/").AvailableFreeSpace;
            return freeBytes >= minimum;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A drive this can't measure is not a reason to refuse to record. Failing open matches
            // how the rest of this app treats a check it can't perform, and the alternative is a
            // feature that silently never runs on some filesystem nobody tested against.
            logger.LogDebug(ex, "Could not measure free disk space for {Root} - recording anyway", _root);
            return true;
        }
    }

    // Turns whatever segments exist into the final mp4 and closes the row out. The single exit
    // path: every way a recording can end - finished, killed, crashed, shut down - comes through
    // here, so there is one definition of what the resulting file and status are.
    private async Task FinalizeFromSegmentsAsync(
        Recording recording, string segmentDirectory, string detail, CancellationToken cancellationToken)
    {
        var segments = OrderedSegments(segmentDirectory);
        if (segments.Count == 0)
        {
            await FinishAsync(
                recording, segmentDirectory, Recording.StatusFailed,
                string.IsNullOrEmpty(detail) ? "No video was captured." : detail, cancellationToken);
            return;
        }

        var path = Path.Combine(
            RecordingsDirectory(recording.CameraId),
            $"{recording.RequestedStartUtc.ToLocalTime():yyyyMMdd-HHmmss}-{recording.Id:N}.mp4");

        var listPath = Path.Combine(Path.GetTempPath(), $"recording-{recording.Id:N}.txt");
        await File.WriteAllLinesAsync(listPath, segments.Select(ClipRecorder.ConcatListLine), cancellationToken);

        try
        {
            var args = new[]
            {
                "-hide_banner",
                "-loglevel", "warning",
                "-f", "concat",
                "-safe", "0",
                "-i", listPath,
                "-c", "copy",
                "-movflags", "+faststart",
                "-y", path
            };

            var built = await FfmpegProcess.RunAsync(
                _ffmpegPath, args, ConcatTimeout(recording), logger, "recording concat", priority: null, cancellationToken);

            if (!built || !File.Exists(path))
            {
                await FinishAsync(recording, segmentDirectory, Recording.StatusFailed, "The recording could not be assembled.", cancellationToken);
                return;
            }

            var size = new FileInfo(path).Length;

            // Complete means the footage actually spans the window that was asked for, measured
            // against the segments rather than against ffmpeg's exit code - a recording can end
            // cleanly and still be short if the camera dropped out near the end. A tolerance of one
            // segment absorbs the fact that the last one is cut on a keyframe.
            var covered = SegmentSpan(segments);
            var requested = recording.RequestedEndUtc - recording.RequestedStartUtc;
            var complete = covered + TimeSpan.FromSeconds(SegmentSeconds * 2) >= requested;

            var status = complete ? Recording.StatusComplete : Recording.StatusPartial;
            var summary = complete
                ? detail
                : $"Captured {covered.TotalMinutes:0.#} of {requested.TotalMinutes:0.#} minutes. {detail}".Trim();

            logger.LogInformation(
                "Recording {Name} finished {Status} - {Size} at {Path}",
                recording.Name, status, Describe(size), path);

            await recordings.FinishAsync(
                recording.Id, DateTimeOffset.UtcNow, path, size, status, summary, cancellationToken);

            DeleteSegments(segmentDirectory);
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* best effort */ }
        }
    }

    // Concatenation is a copy, so it is bound by how fast the disk can move the bytes rather than
    // by anything about the video. ClipRecorder's flat 60 seconds is right for a fifteen-second
    // clip and nowhere near enough for an eight-hour one, which is tens of gigabytes.
    private static TimeSpan ConcatTimeout(Recording recording)
    {
        var window = recording.RequestedEndUtc - recording.RequestedStartUtc;
        return TimeSpan.FromSeconds(Math.Max(120, window.TotalSeconds / 4));
    }

    private async Task FinishAsync(
        Recording recording, string segmentDirectory, string status, string detail, CancellationToken cancellationToken)
    {
        await recordings.FinishAsync(recording.Id, DateTimeOffset.UtcNow, null, 0, status, detail, cancellationToken);
        DeleteSegments(segmentDirectory);
    }

    private void DeleteSegments(string segmentDirectory)
    {
        try
        {
            if (Directory.Exists(segmentDirectory)) Directory.Delete(segmentDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The mp4 is already written and the row already points at it, so a buffer directory
            // that won't delete is litter rather than a failure. The retention sweep will find it.
            logger.LogDebug(ex, "Could not remove the segment buffer at {Directory}", segmentDirectory);
        }
    }

    // Segments for one recording, oldest first. Ordered by the timestamp in the name rather than by
    // mtime: ffmpeg holds a segment open while it writes it, so mtime orders by when each one
    // *closed*, which is the same order right up until it isn't.
    private static List<string> OrderedSegments(string segmentDirectory)
    {
        if (!Directory.Exists(segmentDirectory)) return [];

        return Directory.EnumerateFiles(segmentDirectory, $"{SegmentPrefix}*.ts")
            .Select(path => (Path: path, StartsAt: ParseSegmentStart(path)))
            .Where(segment => segment.StartsAt is not null)
            .OrderBy(segment => segment.StartsAt!.Value)
            .Select(segment => segment.Path)
            .ToList();
    }

    // How much wall-clock time the segments actually cover, from the first one's name to the last
    // one's name plus its nominal length. Deliberately spans any gap in the middle rather than
    // summing the parts: a recording that dropped out for ten minutes and came back did cover the
    // window, just imperfectly, and calling that "partial" on the strength of the gap would be a
    // more useful signal than calling it complete. Summing instead would report the same number for
    // a recording that simply stopped early, which is a different problem.
    private static TimeSpan SegmentSpan(IReadOnlyList<string> segments)
    {
        var first = ParseSegmentStart(segments[0]);
        var last = ParseSegmentStart(segments[^1]);
        if (first is null || last is null) return TimeSpan.Zero;

        return last.Value - first.Value + TimeSpan.FromSeconds(SegmentSeconds);
    }

    // ffmpeg writes these with -strftime, so the name is the segment's start in local time - the
    // same convention (and the same caveat about local time) as the trigger clip buffer.
    private static DateTimeOffset? ParseSegmentStart(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(SegmentPrefix, StringComparison.Ordinal)) return null;

        return DateTimeOffset.TryParseExact(
            name[SegmentPrefix.Length..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static string Describe(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0:0.#} KB"
    };

    private static string ResolveRoot(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["CamerasDataDirectory"] ?? "data";
        return Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
    }
}
