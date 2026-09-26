using System.Globalization;
using SentinelCore.Client.Models;

namespace SentinelCore.Client;

/// <summary>
/// The rules of playing something from the library: when Resume is offered, what to ask the server
/// for, and how a player's own clock maps onto the movie's. Pure functions, so the arithmetic - which
/// is where a player goes subtly wrong - can be checked without a video surface.
/// </summary>
/// <remarks>
/// The HLS case is the one that bites. The server's playlist starts its clock at the requested start
/// point, so a player sitting at 0:00 is really at <c>startSeconds</c>; report that raw position and
/// every resume point in the library slides back to the beginning.
/// </remarks>
public static class MediaPlaybackRules
{
    /// <summary>The server saves nothing under this - see MOVIES.md - so nothing under it is worth resuming.</summary>
    public const double MinimumResumeSeconds = 30;

    /// <summary>Past this fraction the server marks the item watched and clears the resume point.</summary>
    public const double WatchedFraction = 0.92;

    /// <summary>The <c>start</c> that means "resume from wherever I was" - the parameter is simply omitted.</summary>
    public const double ResumeStart = -1;

    /// <summary>The <c>start</c> that means "from the top".</summary>
    public const double StartOver = 0;

    /// <summary>
    /// Whether a Resume button belongs on this item: there is a saved point, it is far enough in
    /// to matter, and the item has not already been finished.
    /// </summary>
    public static bool CanResume(MediaProgress? progress) =>
        progress is { Watched: false } saved
        && saved.PositionSeconds >= MinimumResumeSeconds
        && (saved.DurationSeconds <= 0 || saved.PositionSeconds < saved.DurationSeconds * WatchedFraction);

    /// <summary>
    /// What <c>start</c> to send: null (resume from the server's own record) when the item can be
    /// resumed and the viewer did not ask to start over, else 0.
    /// </summary>
    public static double? StartFor(MediaProgress? progress, bool startOver) =>
        !startOver && CanResume(progress) ? null : StartOver;

    /// <summary>
    /// Where the movie actually is, given where the player thinks it is. For HLS the playlist
    /// clock began at <c>StartSeconds</c>; for direct play the player was seeked there itself and
    /// reports the real position.
    /// </summary>
    public static double AbsolutePosition(MediaPlayback playback, double playerSeconds) =>
        Math.Max(0, playback.IsHls ? playback.StartSeconds + playerSeconds : playerSeconds);

    /// <summary>The player's own position for a movie position - the inverse of <see cref="AbsolutePosition"/>.</summary>
    public static double PlayerPosition(MediaPlayback playback, double absoluteSeconds) =>
        Math.Max(0, playback.IsHls ? absoluteSeconds - playback.StartSeconds : absoluteSeconds);

    /// <summary>Where the player should seek before it starts: direct play must jump itself, HLS is already there.</summary>
    public static double InitialSeek(MediaPlayback playback) => playback.IsHls ? 0 : playback.StartSeconds;

    /// <summary>
    /// Whether a seek needs a new playback session. An HLS playlist has nothing before its own
    /// start, so jumping earlier than that means asking the server for a new one.
    /// </summary>
    public static bool SeekNeedsNewSession(MediaPlayback playback, double absoluteSeconds) =>
        playback.IsHls && absoluteSeconds < playback.StartSeconds;

    /// <summary>
    /// Whether a position is worth sending to the server at all. Below the minimum the server
    /// ignores it anyway, and a burst of reports in the first seconds is noise.
    /// </summary>
    public static bool IsWorthReporting(double absoluteSeconds) => absoluteSeconds >= MinimumResumeSeconds;

    /// <summary>What a progress bar under a card should show, 0-1, or null when there is nothing to show.</summary>
    public static double? Fraction(MediaProgress? progress)
    {
        if (progress is null || progress.Watched) return null;
        if (progress.DurationSeconds > 0)
        {
            return Math.Clamp(progress.PositionSeconds / progress.DurationSeconds, 0, 1);
        }
        return progress.Percent > 0 ? Math.Clamp(progress.Percent / 100, 0, 1) : null;
    }

    /// <summary>"1h 42m", "42m", or "" when the runtime is unknown.</summary>
    public static string FormatRuntime(int? minutes)
    {
        if (minutes is not { } total || total <= 0) return "";
        var hours = total / 60;
        var remainder = total % 60;
        if (hours == 0) return $"{remainder}m";
        return remainder == 0 ? $"{hours}h" : $"{hours}h {remainder}m";
    }

    /// <summary>The runtime of an item, from TMDB when it said, else from the file, else "".</summary>
    public static string RuntimeText(MediaItemSummary item)
    {
        if (item.RuntimeMinutes is { } minutes && minutes > 0) return FormatRuntime(minutes);
        if (item.DurationSeconds is { } seconds && seconds > 0) return FormatRuntime((int)Math.Round(seconds / 60));
        return "";
    }

    /// <summary>"1:02:15" or "23:45" - a clock readout for the player.</summary>
    public static string FormatClock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(Math.Floor(seconds));
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"Resume from 20:00" or "Resume" when the saved point is unknown.</summary>
    public static string ResumeLabel(MediaProgress? progress) =>
        progress is { PositionSeconds: > 0 } saved ? $"Resume from {FormatClock(saved.PositionSeconds)}" : "Resume";

    /// <summary>
    /// The year, runtime and rating of an item on one line: "2009 · 1h 42m · ★ 6.2". Whatever is
    /// missing is left out rather than shown as a blank.
    /// </summary>
    public static string MetaLine(MediaItemSummary item)
    {
        var parts = new List<string>();
        if (item.Year is { } year) parts.Add(year.ToString(CultureInfo.InvariantCulture));
        if (item.IsShow)
        {
            if (item.SeasonCount is { } seasons && seasons > 0) parts.Add(seasons == 1 ? "1 season" : $"{seasons} seasons");
            if (item.EpisodeCount is { } episodes && episodes > 0) parts.Add(episodes == 1 ? "1 episode" : $"{episodes} episodes");
        }
        else if (RuntimeText(item) is { Length: > 0 } runtime)
        {
            parts.Add(runtime);
        }
        if (item.Rating is { } rating && rating > 0) parts.Add($"★ {rating.ToString("0.0", CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }

    /// <summary>The <c>start</c> query value the client sends, in the invariant format the server parses.</summary>
    public static string StartQueryValue(double start) => start.ToString("0.###", CultureInfo.InvariantCulture);
}
