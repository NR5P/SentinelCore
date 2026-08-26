namespace SentinelCore;

/// <summary>
/// Writing a file by putting it somewhere else first and then moving it into place - the pattern
/// this server uses everywhere a half-written file would be worse than an out-of-date one.
/// </summary>
public static class AtomicFile
{
    // Roughly a third of a second in total, backing off as it goes: long enough to outlast a
    // scanner's look at a small file, short enough that the worst case is a pause nobody notices
    // rather than a request that appears to hang.
    private const int Attempts = 6;
    private const int RetryDelayMs = 25;

    /// <summary>
    /// Moves <paramref name="temporaryPath"/> onto <paramref name="finalPath"/>, replacing it.
    /// </summary>
    /// <remarks>
    /// The move is unconditional on Linux and macOS, where rename(2) does not care who else has
    /// the destination open. Windows does care: any handle on the destination fails the move, and
    /// a virus scanner opening the file it has just watched appear holds one for a few
    /// milliseconds, at no predictable moment. That turns an ordinary save into an intermittent
    /// IOException - which, depending on the caller, is either a lost write or a failed request.
    ///
    /// So the move is retried. The first attempt is the only one the platforms that don't need
    /// this ever run, and if every attempt fails the original exception still reaches the caller:
    /// this decides how long to keep trying, never whether to report a failure.
    /// </remarks>
    public static void Replace(string temporaryPath, string finalPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, finalPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < Attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelayMs * attempt);
            }
        }
    }
}
