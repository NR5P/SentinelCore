using System.Diagnostics;

namespace SentinelCore.Web;

// One place to run ffmpeg to completion and get an answer back - as opposed to the long-lived
// supervised sessions in HlsStreamManager and TriggerZoneMonitor, which own their process for the
// life of a camera. Lifted out of ClipRecorder when TriggerRewind needed the same "run it, kill it
// if it hangs, tell me why it failed" behaviour; the shape is that original's, unchanged.
//
// Both pipes are always redirected and always drained. A child whose stdout or stderr fills its
// pipe buffer blocks forever in write(), so an undrained pipe is a hang, not an inefficiency.
public static class FfmpegProcess
{
    public static async Task<bool> RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        ILogger logger,
        string what,
        ProcessPriorityClass? priority,
        CancellationToken cancellationToken)
    {
        using var process = Start(ffmpegPath, args, priority);
        if (process is null) return false;

        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        if (!await WaitOrKillAsync(process, timeout, cancellationToken)) return false;
        if (process.ExitCode == 0) return true;

        logger.LogWarning("ffmpeg {What} exited {Code}: {Error}", what, process.ExitCode, Tail(await stderr));
        return false;
    }

    // Returns what the child wrote to stdout, or null if it failed. Used for the rewind's scoring
    // pass, which pipes a couple of megabytes of raw frames rather than writing them to disk.
    //
    // The copy MUST run concurrently with the wait: at 172,800 bytes per frame the payload dwarfs
    // the 64KB pipe buffer many times over, so reading only after the process exits deadlocks -
    // ffmpeg blocks writing frame two while we block waiting for an exit that can never come.
    public static async Task<byte[]?> RunCapturingStdoutAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        ILogger logger,
        string what,
        ProcessPriorityClass? priority,
        CancellationToken cancellationToken)
    {
        using var process = Start(ffmpegPath, args, priority);
        if (process is null) return null;

        var buffer = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        if (!await WaitOrKillAsync(process, timeout, cancellationToken)) return null;

        // The exit only means ffmpeg stopped writing; the last of what it wrote may still be in
        // flight through the pipe.
        try { await stdout; }
        catch (IOException) { /* pipe torn down early - use whatever arrived */ }

        if (process.ExitCode != 0)
        {
            logger.LogWarning("ffmpeg {What} exited {Code}: {Error}", what, process.ExitCode, Tail(await stderr));
            return null;
        }

        return buffer.ToArray();
    }

    private static Process? Start(string ffmpegPath, IReadOnlyList<string> args, ProcessPriorityClass? priority)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi);
        if (process is null || priority is null) return process;

        // Lowering a child's own priority needs no privilege, but the call can still fail if the
        // process exited between Start and here - which is not a reason to abandon the run.
        try { process.PriorityClass = priority.Value; } catch { /* best effort */ }
        return process;
    }

    private static async Task<bool> WaitOrKillAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return false;
        }
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(10));
    }
}
