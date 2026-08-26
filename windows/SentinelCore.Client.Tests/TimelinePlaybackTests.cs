using SentinelCore.Client;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

// The day is not continuous footage: the server keeps a clip around each trigger and nothing at all
// between them. Every rule below is about that, and they are the rules the Apple client's timeline
// follows - so a day reviewed on a Mac and the same day reviewed here move the same way.
public class TimelinePlaybackTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid FirstEventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondEventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdEventId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// A day with three clips: 09:00, 12:00 and 15:00, each 30 seconds long and each starting five
    /// seconds before its event - the ClipPreSeconds window.
    /// </summary>
    private static RecordingTimelineDay Day(bool available = true) => new()
    {
        Camera = "Front Door",
        Date = "2026-08-25",
        Start = DayStart,
        End = DayStart.AddDays(1),
        Recording = new TimelineRecording
        {
            Configured = true,
            Available = available,
            PreSeconds = 5,
            PostSeconds = 25,
            Segments = new List<TimelineRecordingSegment>
            {
                Segment(FirstEventId, 9),
                Segment(SecondEventId, 12),
                Segment(ThirdEventId, 15),
            },
        },
        Events = new List<TimelineSecurityEvent>
        {
            Event(FirstEventId, 9, "person", "high"),
            Event(SecondEventId, 12, "vehicle", "low"),
            Event(ThirdEventId, 15, "person", "critical"),
        },
    };

    private static TimelineRecordingSegment Segment(Guid eventId, int hour) => new()
    {
        EventId = eventId,
        TriggerEventId = Guid.Parse($"aaaaaaaa-0000-0000-0000-{hour:D12}"),
        Start = DayStart.AddHours(hour).AddSeconds(-5),
        End = DayStart.AddHours(hour).AddSeconds(25),
    };

    private static TimelineSecurityEvent Event(Guid id, int hour, string category, string severity) => new()
    {
        Id = id,
        Camera = "Front Door",
        OccurredAt = DayStart.AddHours(hour),
        Category = category,
        Severity = severity,
        Summary = $"Something at {hour}:00",
        HasClip = true,
    };

    [Fact]
    public void A_cursor_inside_a_clip_finds_it_and_the_offset_into_it()
    {
        var day = Day();
        var cursor = DayStart.AddHours(12).AddSeconds(10);

        var segment = TimelinePlayback.SegmentAt(day, cursor);

        Assert.NotNull(segment);
        Assert.Equal(SecondEventId, segment!.EventId);
        // The clip starts five seconds before the event, so ten seconds past the event is fifteen
        // seconds into the file.
        Assert.Equal(15, TimelinePlayback.OffsetInto(segment, cursor));
    }

    [Fact]
    public void A_cursor_in_a_gap_finds_no_clip()
    {
        // There is genuinely nothing recorded at 10:30, and pretending otherwise is what makes a
        // player sit on a frozen frame.
        Assert.Null(TimelinePlayback.SegmentAt(Day(), DayStart.AddHours(10).AddMinutes(30)));
    }

    [Fact]
    public void A_cursor_before_a_clip_starts_is_not_inside_it()
    {
        Assert.Null(TimelinePlayback.SegmentAt(Day(), DayStart.AddHours(9).AddSeconds(-6)));
        Assert.NotNull(TimelinePlayback.SegmentAt(Day(), DayStart.AddHours(9).AddSeconds(-5)));
    }

    [Fact]
    public void An_offset_can_never_be_negative()
    {
        var segment = Segment(FirstEventId, 9);

        Assert.Equal(0, TimelinePlayback.OffsetInto(segment, segment.Start.AddSeconds(-30)));
    }

    [Fact]
    public void Recordings_reported_unavailable_yield_no_clip_at_all()
    {
        // Configured but unavailable - a disk that has gone away. The screen has to say so rather
        // than try to play something.
        Assert.Null(TimelinePlayback.SegmentAt(Day(available: false), DayStart.AddHours(12)));
    }

    [Fact]
    public void Picking_an_event_lands_at_the_start_of_its_clip_not_at_the_event()
    {
        var day = Day();
        var item = day.Events[1];

        // Five seconds early, so the approach is included rather than starting mid-stride.
        Assert.Equal(DayStart.AddHours(12).AddSeconds(-5), TimelinePlayback.PositionFor(day, item));
    }

    [Fact]
    public void An_event_with_no_clip_falls_back_to_its_own_time()
    {
        var day = Day();
        var orphan = Event(Guid.NewGuid(), 20, "other", "low");

        Assert.Equal(orphan.OccurredAt, TimelinePlayback.PositionFor(day, orphan));
    }

    [Fact]
    public void Jumping_forward_moves_one_event_at_a_time_rather_than_sticking()
    {
        // The bug this guards: land on an event's clip start, which is *earlier* than the event
        // itself. Compare the next press against the event time instead of the clip start and the
        // cursor still reads as "behind" it, so every further press picks the same event forever.
        var day = Day();
        var cursor = DayStart;

        var first = TimelinePlayback.JumpTarget(day, day.Events, cursor, 1);
        Assert.Equal(DayStart.AddHours(9).AddSeconds(-5), first);

        var second = TimelinePlayback.JumpTarget(day, day.Events, first!.Value, 1);
        Assert.Equal(DayStart.AddHours(12).AddSeconds(-5), second);

        var third = TimelinePlayback.JumpTarget(day, day.Events, second!.Value, 1);
        Assert.Equal(DayStart.AddHours(15).AddSeconds(-5), third);
    }

    [Fact]
    public void Jumping_backward_walks_the_other_way()
    {
        var day = Day();

        // From the start of the third clip, back one lands on the start of the second.
        var back = TimelinePlayback.JumpTarget(day, day.Events, DayStart.AddHours(15).AddSeconds(-5), -1);

        Assert.Equal(DayStart.AddHours(12).AddSeconds(-5), back);
    }

    [Fact]
    public void Jumping_backward_from_inside_a_clip_returns_to_that_clips_start()
    {
        // Five seconds into the 15:00 clip, "previous" means the beginning of what is playing -
        // the same thing pressing back does in any other player - not the clip before it.
        var day = Day();

        var back = TimelinePlayback.JumpTarget(day, day.Events, DayStart.AddHours(15), -1);

        Assert.Equal(DayStart.AddHours(15).AddSeconds(-5), back);
    }

    [Fact]
    public void Jumping_past_the_last_event_has_nowhere_to_go()
    {
        var day = Day();

        Assert.Null(TimelinePlayback.JumpTarget(day, day.Events, DayStart.AddHours(23), 1));
        Assert.Null(TimelinePlayback.JumpTarget(day, day.Events, DayStart, -1));
    }

    [Fact]
    public void Jumping_only_considers_the_events_still_showing()
    {
        // With a filter on, the transport has to move between what is on screen - skipping to a row
        // that was filtered out reads as the button being broken.
        var day = Day();
        var onlyCritical = day.Events.Where(item => item.Severity == "critical").ToList();

        var target = TimelinePlayback.JumpTarget(day, onlyCritical, DayStart, 1);

        Assert.Equal(DayStart.AddHours(15).AddSeconds(-5), target);
    }

    [Fact]
    public void Reaching_the_end_of_a_clip_advances_to_the_next_one()
    {
        var day = Day();
        var second = day.Recording.Segments[1];

        var next = TimelinePlayback.SegmentAfter(day, second);

        Assert.NotNull(next);
        Assert.Equal(ThirdEventId, next!.EventId);
    }

    [Fact]
    public void The_last_clip_has_nothing_after_it()
    {
        var day = Day();

        Assert.Null(TimelinePlayback.SegmentAfter(day, day.Recording.Segments[2]));
    }

    [Fact]
    public void A_freshly_loaded_day_starts_at_the_most_recent_clip()
    {
        // The thing most likely to be wanted is the latest footage, not midnight.
        Assert.Equal(DayStart.AddHours(15).AddSeconds(-5), TimelinePlayback.InitialCursor(Day()));
    }

    [Fact]
    public void A_day_with_events_but_no_clips_starts_at_the_last_event()
    {
        var day = Day();
        day.Recording.Segments.Clear();

        Assert.Equal(DayStart.AddHours(15), TimelinePlayback.InitialCursor(day));
    }

    [Fact]
    public void An_empty_day_starts_at_midday_rather_than_midnight()
    {
        var day = Day();
        day.Recording.Segments.Clear();
        day.Events.Clear();

        Assert.Equal(DayStart.AddHours(12), TimelinePlayback.InitialCursor(day));
    }

    [Fact]
    public void Positions_along_the_rail_map_both_ways()
    {
        var day = Day();

        Assert.Equal(0, TimelinePlayback.Position(day, DayStart));
        Assert.Equal(0.5, TimelinePlayback.Position(day, DayStart.AddHours(12)), 3);
        Assert.Equal(1, TimelinePlayback.Position(day, DayStart.AddDays(1)));

        // Outside the day is clamped rather than drawn off the end of the rail.
        Assert.Equal(0, TimelinePlayback.Position(day, DayStart.AddHours(-3)));
        Assert.Equal(1, TimelinePlayback.Position(day, DayStart.AddDays(2)));

        Assert.Equal(DayStart.AddHours(6), TimelinePlayback.MomentAt(day, 0.25));
    }

    [Fact]
    public void A_thirty_second_clip_is_a_sliver_of_a_day()
    {
        var day = Day();

        var fraction = TimelinePlayback.Fraction(day, TimeSpan.FromSeconds(30));

        Assert.True(fraction > 0);
        Assert.True(fraction < 0.001);
    }
}

public class TimelineFilterTests
{
    private static TimelineSecurityEvent Event(string category, string severity, bool reviewed) => new()
    {
        Id = Guid.NewGuid(),
        Category = category,
        Severity = severity,
        Reviewed = reviewed,
    };

    [Fact]
    public void Everything_shows_by_default()
    {
        var filter = new TimelineFilter();

        Assert.True(filter.Includes(Event("person", "critical", reviewed: false)));
        Assert.True(filter.Includes(Event("other", "low", reviewed: true)));
    }

    [Fact]
    public void Turning_off_an_object_hides_only_that_object()
    {
        var filter = new TimelineFilter();
        filter.SelectedCategories.Remove("vehicle");

        Assert.False(filter.Includes(Event("vehicle", "high", reviewed: false)));
        Assert.True(filter.Includes(Event("person", "high", reviewed: false)));
    }

    [Fact]
    public void The_review_filter_has_three_positions()
    {
        var filter = new TimelineFilter();
        var seen = Event("person", "high", reviewed: true);
        var unseen = Event("person", "high", reviewed: false);

        filter.Review = ReviewFilter.Reviewed;
        Assert.True(filter.Includes(seen));
        Assert.False(filter.Includes(unseen));

        filter.Review = ReviewFilter.Unreviewed;
        Assert.False(filter.Includes(seen));
        Assert.True(filter.Includes(unseen));

        filter.Review = ReviewFilter.All;
        Assert.True(filter.Includes(seen));
        Assert.True(filter.Includes(unseen));
    }

    [Fact]
    public void Resetting_puts_everything_back()
    {
        var filter = new TimelineFilter();
        filter.SelectedCategories.Clear();
        filter.SelectedSeverities.Remove("critical");
        filter.Review = ReviewFilter.Unreviewed;

        filter.Reset();

        Assert.True(filter.Includes(Event("package", "critical", reviewed: true)));
        Assert.Equal(ReviewFilter.All, filter.Review);
    }

    [Fact]
    public void An_unrecognised_category_from_a_newer_server_is_hidden_rather_than_crashing()
    {
        // The server could grow a category this build has never heard of. It simply doesn't match
        // the selection, which is a row that doesn't show - not an exception mid-list.
        var filter = new TimelineFilter();

        Assert.False(filter.Includes(Event("drone", "high", reviewed: false)));
    }
}
