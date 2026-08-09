using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The two pieces of the recording feature that are pure and therefore worth pinning: deciding
// which window (if any) is due right now, and turning two clock times into a duration.
//
// Everything the timing itself does - day-of-week sets, cron, DST - is ScheduleTiming's, and is
// already covered by ScheduleTimingTests. What is tested here is only what this feature adds on
// top: that a window already ended is skipped rather than caught up, that one underway is joined
// late, and that a stop time before its start means "crosses midnight" rather than a negative
// length.
public sealed class ScheduledRecordingTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static RecordingSchedule Daily(string startTime, int durationSeconds, DateTimeOffset? lastStarted = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Evening driveway",
            Enabled: true,
            RecordingSchedule.KindDaily,
            startTime,
            DaysOfWeek: [],
            CronExpression: string.Empty,
            RunAtUtc: null,
            SolarEvent: string.Empty,
            SolarOffsetMinutes: 0,
            SolarElevationDegrees: 0,
            durationSeconds,
            IncludeAudio: false,
            lastStarted,
            LastStatus: string.Empty,
            LastDetail: string.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    // 20:00 America/New_York on a summer evening (EDT, UTC-4) is 00:00 UTC the next day.
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void AWindowThatHasNotStartedYetIsNotDue()
    {
        var schedule = Daily("20:00", 1800);

        // 19:30 local - half an hour early.
        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 1, 23, 30), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void AWindowUnderwayIsDueSoARestartRejoinsIt()
    {
        var schedule = Daily("20:00", 1800);

        // 20:05 local: five minutes in. The remaining twenty-five are still worth having, which is
        // the whole point of resuming rather than writing the window off.
        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 2, 0, 5), NewYork, null);

        Assert.NotNull(due);
        Assert.Equal(Utc(2026, 7, 2, 0, 0), due!.Value.Start);
        Assert.Equal(Utc(2026, 7, 2, 0, 30), due.Value.End);
    }

    [Fact]
    public void AWindowThatAlreadyEndedIsSkippedRatherThanCaughtUp()
    {
        var schedule = Daily("20:00", 1800);

        // 21:00 local, an hour after the window opened and half an hour after it shut. Recording
        // now would capture the wrong half hour - the footage of 20:00 is simply gone.
        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 2, 1, 0), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void AScheduleThatAlreadyRanTodayDoesNotFireAgain()
    {
        // LastStartedUtc is set to today's window, which is what the worker writes the moment it
        // starts one. Without this the next pass, thirty seconds later, would start a second
        // recording of the same window on top of the first.
        var start = Utc(2026, 7, 2, 0, 0);
        var schedule = Daily("20:00", 1800, lastStarted: start);

        var due = RecordingScheduleWorker.FindDueWindow(schedule, start.AddMinutes(5), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void ADormantScheduleDoesNotOweAWeekOfMissedWindows()
    {
        // Last ran a fortnight ago - the machine was off. Every window in between has ended, so
        // none of them is due, and the walk must terminate rather than run to its limit.
        var schedule = Daily("20:00", 1800, lastStarted: Utc(2026, 6, 18, 0, 0));

        var due = RecordingScheduleWorker.FindDueWindow(schedule, Utc(2026, 7, 2, 1, 0), NewYork, null);

        Assert.Null(due);
    }

    [Fact]
    public void StopTimeBeforeStartTimeIsAWindowCrossingMidnight()
    {
        var dto = Dto(startTime: "23:00", stopTime: "01:00");

        var errors = RecordingEndpoints.Validate(dto, out var duration);

        Assert.Empty(errors);
        Assert.Equal((int)TimeSpan.FromHours(2).TotalSeconds, duration);
    }

    [Fact]
    public void StopTimeAfterStartTimeIsTheObviousDifference()
    {
        var errors = RecordingEndpoints.Validate(Dto("20:00", "20:30"), out var duration);

        Assert.Empty(errors);
        Assert.Equal(1800, duration);
    }

    [Fact]
    public void AWindowShorterThanTheMinimumIsRefused()
    {
        var errors = RecordingEndpoints.Validate(Dto("20:00", "20:00", durationSeconds: 30), out _);

        Assert.Contains(errors, error => error.Contains("at least", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AWindowLongerThanADayIsRefused()
    {
        var errors = RecordingEndpoints.Validate(
            Dto("20:00", "20:00", durationSeconds: RecordingSchedule.MaximumDurationSeconds + 1), out _);

        Assert.Contains(errors, error => error.Contains("longer than", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AWindowWithNeitherStopTimeNorDurationIsRefused()
    {
        var errors = RecordingEndpoints.Validate(Dto("20:00", stopTime: string.Empty), out _);

        Assert.Contains(errors, error => error.Contains("how long", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUnparseableCronExpressionIsRefused()
    {
        var dto = Dto("", "", durationSeconds: 1800) with { Kind = RecordingSchedule.KindCron, CronExpression = "not a cron" };

        var errors = RecordingEndpoints.Validate(dto, out _);

        Assert.Contains(errors, error => error.Contains("cron", StringComparison.OrdinalIgnoreCase));
    }

    private static RecordingScheduleDto Dto(string startTime, string stopTime, int? durationSeconds = null) =>
        new(
            Id: null,
            "Evening driveway",
            Enabled: true,
            RecordingSchedule.KindDaily,
            startTime,
            stopTime,
            DaysOfWeek: [],
            CronExpression: string.Empty,
            RunAtUtc: null,
            SolarEvent: string.Empty,
            SolarOffsetMinutes: 0,
            SolarElevationDegrees: 0,
            durationSeconds,
            IncludeAudio: false);
}
