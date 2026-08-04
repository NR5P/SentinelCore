using System.Diagnostics;
using System.Globalization;

namespace SentinelCore.Web;

// Saving video from *before* a trigger means the footage has to already be on disk when the
// trigger fires, so each recording camera continuously writes short .ts segments into a rolling
// buffer (see TriggerZoneMonitor, which adds the segment output to the detection ffmpeg so one
// RTSP pull feeds both). When a trigger fires we wait out the post-roll, then concatenate the
// segments spanning [trigger - pre, trigger + post] into an mp4 with -c copy.
public sealed class ClipRecorder(IConfiguration configuration, IHostEnvironment environment, ILogger<ClipRecorder> logger)
{
    // Segments are cut on keyframe boundaries, so the real segment length is the camera's GOP
    // rounded up to this. Short segments keep the clip edges tight; too short and the buffer
    // directory churns badly.
    public const int SegmentSeconds = 2;

    private const string SegmentPrefix = "seg_";
    private const string SegmentTimeFormat = "yyyyMMdd-HHmmss";

    private readonly string _root = ResolveRoot(configuration, environment);
    private readonly string _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";

    public string BufferDirectory(Guid cameraId)
    {
        var dir = Path.Combine(_root, "clip-buffer", cameraId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ClipsDirectory(Guid cameraId)
    {
        var dir = Path.Combine(_root, "trigger-clips", cameraId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ffmpeg writes these with -strftime, so the name is the segment's start time in local time.
    public string SegmentPattern(Guid cameraId) =>
        Path.Combine(BufferDirectory(cameraId), $"{SegmentPrefix}%Y%m%d-%H%M%S.ts");

    // The buffer has to reach back far enough to still hold the pre-roll of a clip that is only
    // now being assembled - which is a clip whose trigger fired a whole post-roll ago, because
    // BuildClipAsync waits that long before it touches the buffer at all (see
    // TriggerZoneMonitor.BuildClipAsync).
    //
    // This used to keep `preSeconds + 60`, a flat minute of slack, and prune on every reconcile
    // pass throughout that wait. That covered a post-roll up to about 55 seconds; the trigger
    // editor accepts up to 120. Set a two-minute post-roll and the oldest surviving segment at
    // build time started roughly a minute AFTER the trigger, so the saved clip began after the
    // person had left - and silently, because segments covering the tail of the window still
    // existed, so the "no buffered video" warning never fired. Deriving the window from the
    // post-roll instead of guessing at it makes the retention correct for any setting.
    public void PruneBuffer(Guid cameraId, int preSeconds, int postSeconds)
    {
        var keepFor = TimeSpan.FromSeconds(preSeconds + postSeconds + RetentionSlackSeconds);
        var cutoff = DateTimeOffset.UtcNow - keepFor;

        foreach (var file in EnumerateSegments(cameraId))
        {
            if (file.StartsAt >= cutoff) continue;
            try { File.Delete(file.Path); } catch { /* best effort - may be mid-write */ }
        }
    }

    // Covers the segment the trigger moment itself landed in, the extra segment BuildClipAsync
    // waits for so the tail is closed off, and the concatenation. Deliberately roomy: over-keeping
    // costs a few seconds of low-bitrate .ts on disk, under-keeping costs the footage.
    private const int RetentionSlackSeconds = 60;

    public void DiscardBuffer(Guid cameraId)
    {
        var dir = Path.Combine(_root, "clip-buffer", cameraId.ToString("N"));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    public async Task<string?> BuildClipAsync(Guid cameraId, DateTimeOffset triggeredAt, int preSeconds, int postSeconds, CancellationToken cancellationToken)
    {
        var from = triggeredAt - TimeSpan.FromSeconds(preSeconds);
        var to = triggeredAt + TimeSpan.FromSeconds(postSeconds);

        // A segment runs until the next one starts. That matters more than it looks: the muxer can
        // only cut on a keyframe, so with -c copy the real segment length is the camera's GOP
        // rounded up to SegmentSeconds, and 4- and 10-second GOPs are ordinary on the cameras this
        // talks to. Assuming a flat SegmentSeconds of coverage therefore under-measured every
        // segment, and the one holding the pre-roll - the segment that STARTS before the window and
        // runs into it - was the one that got excluded. The clip then began at the trigger instead
        // of five seconds before it, with nothing logged, because the rest of the window was still
        // covered. The last segment has no successor to measure against, so it falls back to the
        // nominal length.
        var ordered = EnumerateSegments(cameraId).OrderBy(s => s.StartsAt).ToList();
        var segments = ordered
            .Where((s, i) => s.StartsAt < to && SegmentEnd(ordered, i) > from)
            .ToList();

        if (segments.Count == 0)
        {
            logger.LogWarning("No buffered video covering the trigger window for camera {CameraId}", cameraId);
            return null;
        }

        var clipPath = Path.Combine(ClipsDirectory(cameraId), $"{triggeredAt:yyyyMMdd-HHmmss-fff}.mp4");
        var listPath = Path.Combine(Path.GetTempPath(), $"cliplist-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(listPath, segments.Select(s => $"file '{s.Path.Replace("'", @"'\''")}'"), cancellationToken);

        try
        {
            var args = new[]
            {
                "-f", "concat",
                "-safe", "0",
                "-i", listPath,
                "-c", "copy",
                "-movflags", "+faststart", // lets the browser start playing before the whole file lands
                "-y", clipPath
            };

            if (!await RunFfmpegAsync(args, cancellationToken) || !File.Exists(clipPath))
            {
                logger.LogWarning("Failed to build trigger clip for camera {CameraId}", cameraId);
                return null;
            }

            return clipPath;
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* best effort */ }
        }
    }

    // When a segment really ends: where the next one begins, since the muxer writes them back to
    // back. The clamp matters because the buffer isn't guaranteed contiguous - a restarted or
    // stalled detection session leaves a hole in it, and without the clamp the last segment before
    // the hole would be credited with covering the entire outage, splicing footage from before it
    // onto the front of a clip. Beyond a plausible GOP, fall back to the nominal length; the last
    // segment has no successor to measure against and does the same.
    private static DateTimeOffset SegmentEnd(IReadOnlyList<(string Path, DateTimeOffset StartsAt)> ordered, int index)
    {
        var nominalEnd = ordered[index].StartsAt + TimeSpan.FromSeconds(SegmentSeconds);
        if (index + 1 >= ordered.Count) return nominalEnd;

        var nextStart = ordered[index + 1].StartsAt;
        return nextStart - ordered[index].StartsAt <= TimeSpan.FromSeconds(MaxSegmentSpanSeconds)
            ? nextStart
            : nominalEnd;
    }

    // The longest a single segment can plausibly be: SegmentSeconds rounded up to the camera's GOP,
    // and long GOPs on these cameras top out around 10 seconds. Anything longer is a gap, not a
    // segment.
    private const int MaxSegmentSpanSeconds = 15;

    private IEnumerable<(string Path, DateTimeOffset StartsAt)> EnumerateSegments(Guid cameraId)
    {
        var dir = Path.Combine(_root, "clip-buffer", cameraId.ToString("N"));
        if (!Directory.Exists(dir)) yield break;

        foreach (var path in Directory.EnumerateFiles(dir, $"{SegmentPrefix}*.ts"))
        {
            var stamp = Path.GetFileNameWithoutExtension(path)[SegmentPrefix.Length..];
            DateTimeOffset startsAt;

            if (DateTime.TryParseExact(stamp, SegmentTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                startsAt = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local));
            }
            else
            {
                // Shouldn't happen, but never let one odd filename break clip building.
                startsAt = File.GetLastWriteTimeUtc(path) - TimeSpan.FromSeconds(SegmentSeconds);
            }

            yield return (path, startsAt);
        }
    }

    private async Task<bool> RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return false;

        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return false;
        }

        if (process.ExitCode == 0) return true;

        logger.LogWarning("ffmpeg clip build exited {Code}: {Error}", process.ExitCode, Tail(await stderr));
        return false;
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(10));
    }

    private static string ResolveRoot(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["CamerasDataDirectory"] ?? "data";
        return Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
    }
}
