namespace SentinelCore.Client.Models;

/// <summary>
/// What to tell somebody who has just stopped a recording.
/// </summary>
/// <remarks>
/// Lives here rather than in the WPF app so it can be tested on any machine, and so the phrasing
/// cannot drift between the camera grid and the opened camera - both call this.
/// </remarks>
public static class RecordingText
{
    /// <summary>
    /// Says what was actually kept. The old wording ("Recording saved") was never quite true - it
    /// appeared while the clip was still being cut - so this reports the length the server came back
    /// with, and names the two outcomes that are not a clean save.
    /// </summary>
    public static string StopMessage(ManualRecordingStopped stopped) => stopped.Status switch
    {
        "failed" => string.IsNullOrWhiteSpace(stopped.Detail) ? "Nothing was recorded." : stopped.Detail,
        "complete" or "partial" => Saved(stopped.Seconds),

        // No status means the stop found nothing running - another device had already stopped it.
        // Not an error, and not a claim that anything was saved here.
        _ => "Recording stopped."
    };

    private static string Saved(int? seconds) =>
        seconds is > 0 ? $"Saved {Describe(seconds.Value)} to Recordings." : "Recording saved.";

    private static string Describe(int seconds) =>
        seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
}
