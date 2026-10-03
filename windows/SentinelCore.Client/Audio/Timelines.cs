using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Audio;

/// <summary>
/// Synced lyrics. The server already hands lines with times (GET .../lyrics), but a lyric text
/// can still arrive as raw LRC - an unsynced "plain" that is really LRC, or a sidecar a server
/// passed through untouched - so this parses LRC too, and finds the line that is singing now.
/// </summary>
public static partial class LyricsTimeline
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    [GeneratedRegex(@"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    /// <summary>
    /// Parses LRC: one or more [mm:ss.xx] stamps per line, an [offset:+/-ms] tag, and the metadata
    /// tags ([ar:], [ti:] …) ignored. Lines come back sorted by time. A text with no stamps at all
    /// comes back as unsynced lines (time null), blank lines dropped.
    /// </summary>
    public static List<LyricsLine> ParseLrc(string? text)
    {
        var timed = new List<LyricsLine>();
        var plain = new List<LyricsLine>();
        if (string.IsNullOrWhiteSpace(text)) return timed;

        var offsetSeconds = 0.0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (Offset().Match(line) is { Success: true } offset)
            {
                // Positive offset shifts lyrics earlier (they appear sooner), per the LRC convention.
                offsetSeconds = int.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
                continue;
            }

            var stamps = Stamp().Matches(line);
            if (stamps.Count == 0)
            {
                if (!MetaTag().IsMatch(line)) plain.Add(new LyricsLine { Time = null, Text = line });
                continue;
            }

            var words = Stamp().Replace(line, "").Trim();
            foreach (Match stamp in stamps)
            {
                var minutes = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = stamp.Groups[3].Success
                    ? double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture)
                    : 0;
                timed.Add(new LyricsLine { Time = minutes * 60 + seconds + fraction, Text = words });
            }
        }

        if (timed.Count == 0) return plain;
        return timed
            .Select(line => new LyricsLine { Time = Math.Max(0, line.Time!.Value - offsetSeconds), Text = line.Text })
            .OrderBy(line => line.Time)
            .ToList();
    }

    /// <summary>
    /// The lines to show for an answer from the server: its own lines when it sent any, otherwise
    /// the plain text parsed (which may itself turn out to be LRC).
    /// </summary>
    public static List<LyricsLine> LinesFor(MusicLyrics lyrics)
    {
        if (lyrics.Lines.Count > 0)
        {
            return lyrics.Synced
                ? lyrics.Lines.Where(line => line.Time is not null).OrderBy(line => line.Time).ToList()
                : lyrics.Lines.ToList();
        }
        return ParseLrc(lyrics.Plain);
    }

    public static bool IsSynced(IReadOnlyList<LyricsLine> lines) => lines.Count > 0 && lines.All(line => line.Time is not null);

    /// <summary>The line being sung at <paramref name="seconds"/>: the last one that has started, or -1 before the first.</summary>
    public static int ActiveIndex(IReadOnlyList<LyricsLine> lines, double seconds)
    {
        if (!IsSynced(lines)) return -1;
        int low = 0, high = lines.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lines[mid].Time!.Value <= seconds + 0.05)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found;
    }
}

/// <summary>A podcast transcript following playback, and searched from inside the Now Playing screen.</summary>
public static class TranscriptTimeline
{
    /// <summary>
    /// The segment being spoken: the one whose span holds the time, or failing that the last one to
    /// have started (gaps between segments keep the previous one lit rather than flickering off).
    /// -1 before the first.
    /// </summary>
    public static int ActiveIndex(IReadOnlyList<TranscriptSegment> segments, double seconds)
    {
        int low = 0, high = segments.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (segments[mid].Start <= seconds)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found;
    }

    /// <summary>The segments that mention every word of the query, ignoring case and accents.</summary>
    public static List<int> Search(IReadOnlyList<TranscriptSegment> segments, string query)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<int>();
        if (words.Length == 0) return hits;
        for (var i = 0; i < segments.Count; i++)
        {
            var text = Fold(segments[i].Text);
            if (words.All(word => text.Contains(word, StringComparison.Ordinal))) hits.Add(i);
        }
        return hits;
    }

    /// <summary>Lower case with accents stripped, for matching "cafe" against "Café".</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>Podcast chapters: which one is playing, and where the next and previous start.</summary>
public static class ChapterTimeline
{
    public static int ActiveIndex(IReadOnlyList<PodcastChapter> chapters, double seconds)
    {
        var found = -1;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].StartSeconds <= seconds + 0.05) found = i;
            else break;
        }
        return found;
    }

    /// <summary>The start of the next chapter, or null in the last one.</summary>
    public static double? NextStart(IReadOnlyList<PodcastChapter> chapters, double seconds)
    {
        var index = ActiveIndex(chapters, seconds);
        return index + 1 < chapters.Count ? chapters[index + 1].StartSeconds : null;
    }

    /// <summary>
    /// Previous chapter, with the same three-second rule as Previous track: well into a chapter goes
    /// back to its start; near its start goes to the one before.
    /// </summary>
    public static double PreviousStart(IReadOnlyList<PodcastChapter> chapters, double seconds)
    {
        var index = ActiveIndex(chapters, seconds);
        if (index < 0) return 0;
        if (seconds - chapters[index].StartSeconds > 3 || index == 0) return chapters[index].StartSeconds;
        return chapters[index - 1].StartSeconds;
    }
}

/// <summary>How times read on screen.</summary>
public static class AudioTime
{
    /// <summary>"3:05", "1:02:03". Negative reads as zero.</summary>
    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var total = (long)Math.Floor(seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{secs:00}"
            : $"{minutes}:{secs:00}";
    }

    /// <summary>"-2:10" - the time left, for the right-hand end of a scrubber.</summary>
    public static string Remaining(double position, double duration) => "-" + Clock(Math.Max(0, duration - position));

    /// <summary>"45 min", "1 hr 5 min", "30 sec" - an episode's or album's length.</summary>
    public static string Length(double seconds)
    {
        if (seconds <= 0) return "";
        if (seconds < 60) return $"{Math.Round(seconds)} sec";
        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 60) return $"{minutes} min";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} hr" : $"{hours} hr {rest} min";
    }

    /// <summary>"1 hr 5 min left" for an episode part-way through.</summary>
    public static string Left(double seconds) => seconds <= 0 ? "" : $"{Length(seconds)} left";

    /// <summary>Minutes as hours where that reads better: "12,034 minutes" becomes "200 hours".</summary>
    public static string Minutes(double minutes)
    {
        if (minutes < 120) return $"{Math.Round(minutes):N0} minutes";
        return $"{Math.Round(minutes / 60):N0} hours";
    }
}
