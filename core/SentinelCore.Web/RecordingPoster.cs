using System.Collections.Concurrent;
using System.Diagnostics;

namespace SentinelCore.Web;

// One frame out of a recording, as the picture on its Events card. Written next to the video as
// <file>.poster.jpg the first time it is asked for. Cheap on purpose - a single frame a second in,
// scaled to card width - because a page of cards may ask for a dozen of these at once on the day
// this ships to an install with a year of timed recordings.
public static class RecordingPoster
{
    // Where the frame is taken from. Not the very first: the first second of a recording started
    // by hand is usually the moment before the person pressed the button, and camera streams begin
    // on a keyframe that is sometimes still grey while the encoder settles.
    private const string SeekSeconds = "1";

    // Serialised per file, so two cards for the same recording arriving together produce one
    // ffmpeg run and one file rather than two writers racing onto the same path.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static async Task<string?> GetOrCreateAsync(
        string videoPath, string ffmpegPath, ILogger logger, CancellationToken cancellationToken)
    {
        var posterPath = videoPath + ".poster.jpg";
        if (File.Exists(posterPath)) return posterPath;

        var gate = Locks.GetOrAdd(posterPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(posterPath)) return posterPath;

            // Written to a temporary name and renamed, so a half-written file never gets served
            // as a finished poster by a request arriving between the two.
            var tempPath = posterPath + ".tmp";
            var info = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", SeekSeconds, "-i", videoPath,
                "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "4", "-f", "image2", tempPath,
            })
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null) return null;

            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(tempPath))
            {
                // A recording shorter than the seek point has no frame at 1s; try the first frame
                // before giving up, since a two-second clip is still a clip.
                var retry = await ExtractFirstFrameAsync(ffmpegPath, videoPath, tempPath, cancellationToken);
                if (!retry)
                {
                    logger.LogWarning("Could not cut a poster frame from {Video}: {Error}", videoPath, (await stderr).Trim());
                    return null;
                }
            }

            File.Move(tempPath, posterPath, overwrite: true);
            return posterPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not write a poster frame for {Video}", videoPath);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<bool> ExtractFirstFrameAsync(
        string ffmpegPath, string videoPath, string outputPath, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", videoPath,
            "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "4", "-f", "image2", outputPath,
        })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info);
        if (process is null) return false;
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 && File.Exists(outputPath);
    }
}
