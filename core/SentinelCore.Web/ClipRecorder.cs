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

    // The buffer only ever needs to reach back far enough to cover the longest configured
    // pre-roll (plus slack for a clip that is still waiting on its post-roll).
    public void PruneBuffer(Guid cameraId, int preSeconds)
    {
        var keepFor = TimeSpan.FromSeconds(preSeconds + 60);
        var cutoff = DateTimeOffset.UtcNow - keepFor;

        foreach (var file in EnumerateSegments(cameraId))
        {
            if (file.StartsAt >= cutoff) continue;
            try { File.Delete(file.Path); } catch { /* best effort - may be mid-write */ }
        }
    }

    public void DiscardBuffer(Guid cameraId)
    {
        var dir = Path.Combine(_root, "clip-buffer", cameraId.ToString("N"));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    public async Task<string?> BuildClipAsync(Guid cameraId, DateTimeOffset triggeredAt, int preSeconds, int postSeconds, CancellationToken cancellationToken)
    {
        var from = triggeredAt - TimeSpan.FromSeconds(preSeconds);
        var to = triggeredAt + TimeSpan.FromSeconds(postSeconds);

        // A segment named T covers roughly [T, T + SegmentSeconds), so anything that starts
        // before the window ends and ends after it begins overlaps the clip.
        var segments = EnumerateSegments(cameraId)
            .Where(s => s.StartsAt < to && s.StartsAt + TimeSpan.FromSeconds(SegmentSeconds) > from)
            .OrderBy(s => s.StartsAt)
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
