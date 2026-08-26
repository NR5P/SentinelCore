using SentinelCore.Client.Models;

namespace SentinelCore.Client;

/// <summary>
/// Where a cursor sitting somewhere in a day maps to in the recorded clips, and where it should go
/// next.
/// </summary>
/// <remarks>
/// The day is not continuous footage. The server keeps a clip around each trigger - a few seconds
/// before and after - and nothing at all in between, so "play the day" really means "play this
/// clip, then jump to the start of the next one". These are the rules for that, kept as pure
/// functions so they can be checked without a video surface: the arithmetic is where this screen
/// goes wrong, and it goes wrong subtly - a cursor a second short of the event it just jumped to
/// still counts as behind it, so every further press picks the same event again.
/// </remarks>
public static class TimelinePlayback
{
    /// <summary>The clip covering this moment, or null if the cursor is in a gap.</summary>
    public static TimelineRecordingSegment? SegmentAt(RecordingTimelineDay? day, DateTimeOffset cursor)
    {
        if (day is null || !day.Recording.Available) return null;
        return day.Recording.Segments.FirstOrDefault(
            segment => segment.Start <= cursor && segment.End >= cursor);
    }

    /// <summary>How far into its clip the cursor is. Never negative.</summary>
    public static double OffsetInto(TimelineRecordingSegment segment, DateTimeOffset cursor) =>
        Math.Max(0, (cursor - segment.Start).TotalSeconds);

    /// <summary>
    /// Where the cursor should land to watch one event: the start of the clip recorded for it,
    /// which is ClipPreSeconds earlier than the event itself, so the approach is included.
    /// </summary>
    public static DateTimeOffset PositionFor(RecordingTimelineDay? day, TimelineSecurityEvent item)
    {
        var segment = day?.Recording.Segments.FirstOrDefault(candidate => candidate.EventId == item.Id);
        return segment?.Start ?? item.OccurredAt;
    }

    /// <summary>
    /// The next clip after this one, for when playback reaches the end of the current window.
    /// There is no footage in the gap, so playing "through" it would just be a frozen frame.
    /// </summary>
    public static TimelineRecordingSegment? SegmentAfter(
        RecordingTimelineDay? day, TimelineRecordingSegment segment)
    {
        if (day is null) return null;
        return day.Recording.Segments
            .Where(candidate => candidate.Start > segment.End)
            .OrderBy(candidate => candidate.Start)
            .FirstOrDefault();
    }

    /// <summary>
    /// Where the previous/next buttons should move to, or null when there is nothing that way.
    /// </summary>
    /// <remarks>
    /// Compared against where the cursor actually lands - the start of the event's clip - rather
    /// than against the event's own time. Comparing one and seeking to the other leaves the cursor
    /// a few seconds short of the event just moved to, so it still counts as being ahead of it and
    /// every further press picks the same one again.
    /// </remarks>
    public static DateTimeOffset? JumpTarget(
        RecordingTimelineDay? day,
        IEnumerable<TimelineSecurityEvent> events,
        DateTimeOffset cursor,
        int direction)
    {
        var positions = events.Select(item => PositionFor(day, item)).OrderBy(position => position).ToList();
        // Half a second of slack, so a cursor sitting exactly on a position doesn't pick itself.
        var slack = TimeSpan.FromSeconds(0.5);

        return direction > 0
            ? positions.Where(position => position > cursor + slack).Cast<DateTimeOffset?>().FirstOrDefault()
            : positions.Where(position => position < cursor - slack).Cast<DateTimeOffset?>().LastOrDefault();
    }

    /// <summary>
    /// Where the cursor should start when a day is first loaded: the last clip if there is one,
    /// otherwise the last event, otherwise the middle of the day.
    /// </summary>
    public static DateTimeOffset InitialCursor(RecordingTimelineDay day)
    {
        var lastSegment = day.Recording.Segments.OrderBy(segment => segment.Start).LastOrDefault();
        if (lastSegment is not null) return lastSegment.Start;

        var lastEvent = day.Events.OrderBy(item => item.OccurredAt).LastOrDefault();
        if (lastEvent is not null) return lastEvent.OccurredAt;

        return day.Start.AddHours(12);
    }

    /// <summary>Where a moment sits along the day, 0 at the start and 1 at the end.</summary>
    public static double Position(RecordingTimelineDay day, DateTimeOffset moment)
    {
        var span = (day.End - day.Start).TotalSeconds;
        if (span <= 0) return 0;
        return Math.Clamp((moment - day.Start).TotalSeconds / span, 0, 1);
    }

    /// <summary>How much of the day a duration covers, as a fraction of its width.</summary>
    public static double Fraction(RecordingTimelineDay day, TimeSpan duration)
    {
        var span = (day.End - day.Start).TotalSeconds;
        if (span <= 0) return 0;
        return Math.Clamp(duration.TotalSeconds / span, 0, 1);
    }

    /// <summary>The moment a fraction along the day's width refers to.</summary>
    public static DateTimeOffset MomentAt(RecordingTimelineDay day, double fraction) =>
        day.Start.AddSeconds((day.End - day.Start).TotalSeconds * Math.Clamp(fraction, 0, 1));
}

/// <summary>Which events the timeline is showing, by object, severity and whether they're reviewed.</summary>
public sealed class TimelineFilter
{
    // The vocabularies the server uses - see TimelineEndpoints.Category and .Severity. Kept here
    // rather than in the view so the reset button and the filter both work from one list.
    //
    // "none" is the server's answer for a frame the AI looked at and found no subject in, and it is
    // easily the most common outcome on a busy camera. It is offered here, and off by default: a day
    // that is mostly "nothing there" is worth being able to see, but it is not what anyone opens the
    // timeline to review. The count beside the filters always says how many are being held back, so
    // it is never a mystery where the rest of the day went.
    public static IReadOnlyList<string> Categories { get; } =
        new[] { "person", "vehicle", "animal", "package", "plate", "other", "none" };

    /// <summary>The categories a fresh filter starts with - everything the AI actually found.</summary>
    public static IReadOnlyList<string> DefaultCategories { get; } =
        Categories.Where(value => value != "none").ToList();

    public static IReadOnlyList<string> Severities { get; } =
        new[] { "critical", "high", "medium", "low" };

    /// <summary>How a category reads on screen. Only "none" needs more than capitalising.</summary>
    public static string Label(string category) => category switch
    {
        "none" => "Nothing detected",
        _ => char.ToUpperInvariant(category[0]) + category[1..],
    };

    public HashSet<string> SelectedCategories { get; } = new(DefaultCategories, StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SelectedSeverities { get; } = new(Severities, StringComparer.OrdinalIgnoreCase);

    public ReviewFilter Review { get; set; } = ReviewFilter.All;

    public void Reset()
    {
        SelectedCategories.Clear();
        foreach (var value in DefaultCategories) SelectedCategories.Add(value);
        SelectedSeverities.Clear();
        foreach (var value in Severities) SelectedSeverities.Add(value);
        Review = ReviewFilter.All;
    }

    public bool Includes(TimelineSecurityEvent item) =>
        SelectedCategories.Contains(item.Category) &&
        SelectedSeverities.Contains(item.Severity) &&
        Review switch
        {
            ReviewFilter.Reviewed => item.Reviewed,
            ReviewFilter.Unreviewed => !item.Reviewed,
            _ => true,
        };

    public IEnumerable<TimelineSecurityEvent> Apply(IEnumerable<TimelineSecurityEvent> events) =>
        events.Where(Includes);
}

public enum ReviewFilter
{
    All,
    Unreviewed,
    Reviewed,
}
