using Xunit;

namespace SentinelCore.Tests;

// The timing rules are the part of the schedules feature most likely to be quietly wrong, and
// the part hardest to notice being wrong in production - a schedule that runs twice on the night
// the clocks go back looks fine for 363 days a year. All of it is pure, so all of it is tested
// here against fixed instants rather than a live clock.
public sealed class ScheduleTimingTests
{
    // A zone with daylight saving in the northern hemisphere, pinned so these tests mean the
    // same thing on a machine set to UTC as on one in New York.
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Southern hemisphere, so the DST transitions run the opposite way round the calendar - a
    // cheap guard against a rule that only happens to work for the northern order.
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static readonly GeoCoordinate NewYorkCity = new(40.7128, -74.0060);

    // --- daily -------------------------------------------------------------------------------

    [Fact]
    public void Daily_FiresAtTheNextLocalWallClockTime()
    {
        var schedule = Daily("18:00");
        var after = Instant(2026, 8, 4, 12, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 4, 18, 0, NewYork), next);
    }

    [Fact]
    public void Daily_PastTodaysTime_RollsToTomorrow()
    {
        var schedule = Daily("06:30");
        var after = Instant(2026, 8, 4, 9, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 5, 6, 30, NewYork), next);
    }

    [Fact]
    public void Daily_WithDaysOfWeek_SkipsToTheNextSelectedDay()
    {
        // Saturday and Sunday only. 2026-08-04 is a Tuesday.
        var schedule = Daily("07:00") with { DaysOfWeek = [0, 6] };
        var after = Instant(2026, 8, 4, 9, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 8, 7, 0, NewYork), next);
        Assert.Equal(DayOfWeek.Saturday, TimeZoneInfo.ConvertTime(next!.Value, NewYork).DayOfWeek);
    }

    [Fact]
    public void Daily_WithDaysOfWeek_WrapsFromSundayToTheStartOfTheWeek()
    {
        // Mondays only. 2026-08-09 is a Sunday.
        var schedule = Daily("07:00") with { DaysOfWeek = [1] };
        var after = Instant(2026, 8, 9, 23, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 10, 7, 0, NewYork), next);
    }

    // --- daylight saving ---------------------------------------------------------------------

    [Fact]
    public void Daily_AcrossSpringForward_KeepsTheSameWallClockTime()
    {
        // US DST starts 2026-03-08. A 07:00 schedule is 07:00 local on both sides, which is a
        // different number of hours apart than a naive 24 hours.
        var schedule = Daily("07:00");
        var after = Instant(2026, 3, 7, 8, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        var local = TimeZoneInfo.ConvertTime(next!.Value, NewYork);
        Assert.Equal(new DateTime(2026, 3, 8, 7, 0, 0), local.DateTime);

        // 23 real hours, not 24, because an hour of that night did not happen.
        Assert.Equal(TimeSpan.FromHours(23), next.Value - Instant(2026, 3, 7, 7, 0, NewYork));
    }

    [Fact]
    public void Daily_InTheSpringForwardGap_FiresAtTheFirstInstantThatExists()
    {
        // 02:30 does not exist on 2026-03-08 in New York; the clock jumps 02:00 -> 03:00.
        var schedule = Daily("02:30");
        var after = Instant(2026, 3, 8, 0, 30, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        var local = TimeZoneInfo.ConvertTime(next!.Value, NewYork);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), local.DateTime);
    }

    [Fact]
    public void Daily_InTheFallBackRepeatedHour_FiresOnceNotTwice()
    {
        // US DST ends 2026-11-01: 01:30 happens twice, once at -04:00 and again at -05:00.
        var schedule = Daily("01:30");
        var after = Instant(2026, 10, 31, 12, 0, NewYork);

        var first = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);
        Assert.Equal(TimeSpan.FromHours(-4), first!.Value.Offset);

        // Asking again from immediately after that firing must skip the repeat of the same wall
        // clock time and land on the next day - otherwise the schedule runs twice that night.
        var second = ScheduleTiming.NextOccurrence(schedule, first.Value, NewYork, null);

        var secondLocal = TimeZoneInfo.ConvertTime(second!.Value, NewYork);
        Assert.Equal(new DateTime(2026, 11, 2, 1, 30, 0), secondLocal.DateTime);
    }

    [Fact]
    public void Daily_AcrossSouthernHemisphereDaylightSaving_KeepsTheSameWallClockTime()
    {
        // Sydney DST starts 2026-10-04 - spring forward in October, the opposite way round the
        // calendar from New York.
        var schedule = Daily("09:00");
        var after = Instant(2026, 10, 3, 12, 0, Sydney);

        var next = ScheduleTiming.NextOccurrence(schedule, after, Sydney, null);

        var local = TimeZoneInfo.ConvertTime(next!.Value, Sydney);
        Assert.Equal(new DateTime(2026, 10, 4, 9, 0, 0), local.DateTime);
    }

    // --- interval ----------------------------------------------------------------------------

    [Fact]
    public void Interval_WithoutAWindow_AlignsToWallClockBoundaries()
    {
        var schedule = Interval(900);
        var after = Instant(2026, 8, 4, 12, 7, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 4, 12, 15, NewYork), next);
    }

    [Fact]
    public void Interval_ExactlyOnABoundary_ReturnsTheFollowingTickNotTheSameOne()
    {
        var schedule = Interval(900);
        var after = Instant(2026, 8, 4, 12, 15, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 4, 12, 30, NewYork), next);
    }

    [Fact]
    public void Interval_BeforeTheWindowOpens_FiresExactlyAtTheOpening()
    {
        var schedule = Interval(1800) with { WindowStart = "09:00", WindowEnd = "17:00" };
        var after = Instant(2026, 8, 4, 6, 12, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 4, 9, 0, NewYork), next);
    }

    [Fact]
    public void Interval_InsideTheWindow_TicksFromTheOpening()
    {
        var schedule = Interval(1800) with { WindowStart = "09:00", WindowEnd = "17:00" };
        var after = Instant(2026, 8, 4, 10, 5, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 4, 10, 30, NewYork), next);
    }

    [Fact]
    public void Interval_AfterTheWindowCloses_WaitsForTomorrowsOpening()
    {
        var schedule = Interval(1800) with { WindowStart = "09:00", WindowEnd = "17:00" };
        var after = Instant(2026, 8, 4, 18, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 5, 9, 0, NewYork), next);
    }

    [Fact]
    public void Interval_WithAWindowCrossingMidnight_StaysInsideItAfterMidnight()
    {
        var schedule = Interval(3600) with { WindowStart = "22:00", WindowEnd = "06:00" };
        var after = Instant(2026, 8, 5, 1, 10, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        // The window opened at 22:00 yesterday, so the hourly ticks land on the hour.
        Assert.Equal(Instant(2026, 8, 5, 2, 0, NewYork), next);
    }

    // A window that runs past midnight spans one of the two nights a year when the clocks move,
    // and "22:00 to 06:00" is eight hours on the wall clock but seven real hours in spring and
    // nine in autumn. Measuring it as a duration and adding it to the opening shut the window at
    // 05:00 every autumn - the last hour of the night silently stopped firing - and held it open
    // until 07:00 every spring. The ordinary-night rows are the control: this only ever went
    // wrong on two nights, which is exactly why it could go unnoticed.
    [Theory]
    [InlineData(2026, 8, 5)]    // an ordinary night
    [InlineData(2026, 11, 1)]   // fall back, 02:00 -> 01:00
    [InlineData(2026, 3, 8)]    // spring forward, 02:00 -> 03:00
    public void Interval_WithAWindowCrossingMidnight_KeepsTickingUntilItsEndHour(int year, int month, int day)
    {
        var schedule = Interval(1800) with { WindowStart = "22:00", WindowEnd = "06:00" };
        var after = Instant(year, month, day, 5, 10, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(year, month, day, 5, 30, NewYork), next);
    }

    [Theory]
    [InlineData(2026, 8, 5)]
    [InlineData(2026, 11, 1)]
    [InlineData(2026, 3, 8)]
    public void Interval_WithAWindowCrossingMidnight_IsShutOnceItsEndHourPasses(int year, int month, int day)
    {
        var schedule = Interval(1800) with { WindowStart = "22:00", WindowEnd = "06:00" };
        var after = Instant(year, month, day, 6, 10, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        // Nothing more until tonight's opening.
        Assert.Equal(Instant(year, month, day, 22, 0, NewYork), next);
    }

    // The same rule for a window that does not cross midnight but does contain a transition:
    // 01:00 -> 05:00 on the night the clocks go forward is three real hours, not four.
    [Fact]
    public void Interval_WithAWindowContainingASpringForward_StillEndsAtItsEndHour()
    {
        var schedule = Interval(1800) with { WindowStart = "01:00", WindowEnd = "05:00" };

        Assert.Equal(
            Instant(2026, 3, 8, 4, 30, NewYork),
            ScheduleTiming.NextOccurrence(schedule, Instant(2026, 3, 8, 4, 10, NewYork), NewYork, null));

        Assert.Equal(
            Instant(2026, 3, 9, 1, 0, NewYork),
            ScheduleTiming.NextOccurrence(schedule, Instant(2026, 3, 8, 5, 10, NewYork), NewYork, null));
    }

    [Fact]
    public void Interval_WithAWindowShorterThanTheInterval_StillFiresOncePerWindow()
    {
        var schedule = Interval(3600) with { WindowStart = "09:00", WindowEnd = "09:15" };
        var after = Instant(2026, 8, 4, 12, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 5, 9, 0, NewYork), next);
    }

    [Fact]
    public void Interval_BelowTheMinimum_NeverFires()
    {
        var schedule = Interval(1);

        Assert.Null(ScheduleTiming.NextOccurrence(schedule, DateTimeOffset.UtcNow, NewYork, null));
    }

    // --- cron --------------------------------------------------------------------------------

    [Fact]
    public void Cron_FiresAtTheNextMatchingLocalTime()
    {
        var schedule = Cron("0 6 * * 1-5");
        var after = Instant(2026, 8, 4, 9, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 5, 6, 0, NewYork), next);
    }

    [Fact]
    public void Cron_SkipsTheWeekendWhenTheExpressionSaysWeekdaysOnly()
    {
        // 2026-08-07 is a Friday, so the next weekday firing is the following Monday.
        var schedule = Cron("30 7 * * 1-5");
        var after = Instant(2026, 8, 7, 12, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 10, 7, 30, NewYork), next);
    }

    [Fact]
    public void Cron_AcceptsASixFieldExpressionWithSeconds()
    {
        var schedule = Cron("30 0 6 * * *");
        var after = Instant(2026, 8, 4, 9, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, null);

        Assert.Equal(Instant(2026, 8, 5, 6, 0, NewYork).AddSeconds(30), next);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a cron expression")]
    [InlineData("* * *")]
    [InlineData("99 * * * *")]
    public void Cron_WithAnUnusableExpression_NeverFires(string expression)
    {
        Assert.False(ScheduleTiming.TryParseCron(expression, out _));
        Assert.Null(ScheduleTiming.NextOccurrence(Cron(expression), DateTimeOffset.UtcNow, NewYork, null));
    }

    // --- once --------------------------------------------------------------------------------

    [Fact]
    public void Once_InTheFuture_FiresAtThatInstant()
    {
        var runAt = Instant(2026, 8, 4, 15, 0, NewYork);
        var schedule = Base() with { Kind = Schedule.KindOnce, RunAtUtc = runAt };

        var next = ScheduleTiming.NextOccurrence(schedule, Instant(2026, 8, 4, 12, 0, NewYork), NewYork, null);

        Assert.Equal(runAt, next);
    }

    [Fact]
    public void Once_AlreadyPast_NeverFiresAgain()
    {
        var schedule = Base() with
        {
            Kind = Schedule.KindOnce,
            RunAtUtc = Instant(2026, 8, 4, 9, 0, NewYork)
        };

        Assert.Null(ScheduleTiming.NextOccurrence(schedule, Instant(2026, 8, 4, 12, 0, NewYork), NewYork, null));
    }

    // --- solar -------------------------------------------------------------------------------

    [Fact]
    public void Solar_WithoutALocation_NeverFires()
    {
        var schedule = Solar(Schedule.SolarSunset, 0);

        Assert.Null(ScheduleTiming.NextOccurrence(schedule, DateTimeOffset.UtcNow, NewYork, null));
        Assert.Null(ScheduleTiming.NextOccurrence(schedule, DateTimeOffset.UtcNow, NewYork, new GeoCoordinate(0, 0)));
    }

    [Fact]
    public void Solar_Sunset_FiresThisEveningWhenAskedThisMorning()
    {
        var schedule = Solar(Schedule.SolarSunset, 0);
        var after = Instant(2026, 6, 21, 8, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, NewYorkCity);

        var local = TimeZoneInfo.ConvertTime(next!.Value, NewYork);
        Assert.Equal(new DateOnly(2026, 6, 21), DateOnly.FromDateTime(local.DateTime));

        // New York sunset on the summer solstice is 20:31 local; the algorithm is good to a
        // couple of minutes, so this asserts the published time with a small tolerance.
        AssertCloseTo(new TimeOnly(20, 31), TimeOnly.FromDateTime(local.DateTime), TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void Solar_WithANegativeOffset_FiresBeforeTheEvent()
    {
        var atSunrise = Solar(Schedule.SolarSunrise, 0);
        var thirtyBefore = Solar(Schedule.SolarSunrise, -30);
        var after = Instant(2026, 6, 20, 22, 0, NewYork);

        var plain = ScheduleTiming.NextOccurrence(atSunrise, after, NewYork, NewYorkCity);
        var early = ScheduleTiming.NextOccurrence(thirtyBefore, after, NewYork, NewYorkCity);

        Assert.Equal(TimeSpan.FromMinutes(-30), early!.Value - plain!.Value);
    }

    [Fact]
    public void Solar_WithDaysOfWeek_OnlyFiresOnThoseDays()
    {
        // Saturdays only. 2026-08-04 is a Tuesday.
        var schedule = Solar(Schedule.SolarSunset, 0) with { DaysOfWeek = [6] };
        var after = Instant(2026, 8, 4, 12, 0, NewYork);

        var next = ScheduleTiming.NextOccurrence(schedule, after, NewYork, NewYorkCity);

        Assert.Equal(DayOfWeek.Saturday, TimeZoneInfo.ConvertTime(next!.Value, NewYork).DayOfWeek);
        Assert.Equal(new DateOnly(2026, 8, 8), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(next.Value, NewYork).DateTime));
    }

    [Fact]
    public void Solar_DuringPolarNight_SkipsForwardToTheNextRealSunrise()
    {
        // Longyearbyen, Svalbard: the sun does not rise at all between late October and mid
        // February, so a sunrise schedule there has to skip months rather than fail.
        var svalbard = new GeoCoordinate(78.2232, 15.6267);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
        var schedule = Solar(Schedule.SolarSunrise, 0);

        var next = ScheduleTiming.NextOccurrence(schedule, Instant(2026, 12, 1, 12, 0, zone), zone, svalbard);

        Assert.NotNull(next);
        var local = TimeZoneInfo.ConvertTime(next.Value, zone);
        Assert.Equal(2027, local.Year);
        Assert.InRange(local.Month, 2, 3);
    }

    // --- darkness levels ---------------------------------------------------------------------

    [Fact]
    public void Dusk_FallsAfterSunset_AndGetsLaterTheDarkerItIsAskedFor()
    {
        var after = Instant(2026, 8, 4, 12, 0, NewYork);

        var sunset = ScheduleTiming.NextOccurrence(Solar(Schedule.SolarSunset, 0), after, NewYork, NewYorkCity);
        var civil = ScheduleTiming.NextOccurrence(
            Darkness(Schedule.SolarDusk, SolarCalculator.CivilTwilightDegrees), after, NewYork, NewYorkCity);
        var nautical = ScheduleTiming.NextOccurrence(
            Darkness(Schedule.SolarDusk, SolarCalculator.NauticalTwilightDegrees), after, NewYork, NewYorkCity);
        var astronomical = ScheduleTiming.NextOccurrence(
            Darkness(Schedule.SolarDusk, SolarCalculator.AstronomicalTwilightDegrees), after, NewYork, NewYorkCity);

        Assert.True(sunset < civil);
        Assert.True(civil < nautical);
        Assert.True(nautical < astronomical);

        // Civil twilight at this latitude runs roughly half an hour past sunset. The exact figure
        // varies through the year; the point of the bound is that the level is being applied at
        // all rather than silently collapsing back to sunset.
        var gap = civil!.Value - sunset!.Value;
        Assert.InRange(gap, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void Dawn_FallsBeforeSunrise()
    {
        var after = Instant(2026, 8, 4, 0, 30, NewYork);

        var sunrise = ScheduleTiming.NextOccurrence(Solar(Schedule.SolarSunrise, 0), after, NewYork, NewYorkCity);
        var dawn = ScheduleTiming.NextOccurrence(
            Darkness(Schedule.SolarDawn, SolarCalculator.CivilTwilightDegrees), after, NewYork, NewYorkCity);

        Assert.True(dawn < sunrise);
    }

    [Fact]
    public void Dusk_TracksTheTimeOfYearWithoutAnythingBeingChanged()
    {
        // The whole reason to ask for a darkness level instead of a clock time: the same
        // unchanged schedule fires hours apart in summer and winter, following the season by
        // itself. A coop door set to "getting dark" needs no seasonal adjustment.
        var schedule = Darkness(Schedule.SolarDusk, SolarCalculator.CivilTwilightDegrees);

        var midsummer = ScheduleTiming.NextOccurrence(
            schedule, Instant(2026, 6, 21, 12, 0, NewYork), NewYork, NewYorkCity);
        var midwinter = ScheduleTiming.NextOccurrence(
            schedule, Instant(2026, 12, 21, 12, 0, NewYork), NewYork, NewYorkCity);

        var summerLocal = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(midsummer!.Value, NewYork).DateTime);
        var winterLocal = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(midwinter!.Value, NewYork).DateTime);

        Assert.True(
            summerLocal - winterLocal > TimeSpan.FromHours(2),
            $"Expected midsummer dusk ({summerLocal:HH:mm}) to be hours later than midwinter dusk ({winterLocal:HH:mm}).");
    }

    [Fact]
    public void Dusk_WhereItNeverGetsThatDarkInSummer_SkipsToANightThatDoes()
    {
        // Above roughly 60 degrees the sun never drops 18 degrees below the horizon around
        // midsummer, so "fully dark" simply does not happen for weeks. That must skip nights
        // rather than invent a time.
        var stockholm = new GeoCoordinate(59.3293, 18.0686);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        var schedule = Darkness(Schedule.SolarDusk, SolarCalculator.AstronomicalTwilightDegrees);

        var next = ScheduleTiming.NextOccurrence(schedule, Instant(2026, 6, 21, 12, 0, zone), zone, stockholm);

        Assert.NotNull(next);
        var local = TimeZoneInfo.ConvertTime(next.Value, zone);
        Assert.True(
            local.Month >= 7,
            $"Expected the next fully-dark night to be weeks away, but got {local:yyyy-MM-dd}.");
    }

    [Fact]
    public void Dusk_WithAnOffset_FiresAfterTheDarknessLevelIsReached()
    {
        // "Twenty minutes after it starts getting dark" - the way someone actually wants a coop
        // door timed, giving the birds a moment to finish going in.
        var plain = Darkness(Schedule.SolarDusk, SolarCalculator.CivilTwilightDegrees);
        var delayed = plain with { SolarOffsetMinutes = 20 };
        var after = Instant(2026, 8, 4, 12, 0, NewYork);

        var atDusk = ScheduleTiming.NextOccurrence(plain, after, NewYork, NewYorkCity);
        var afterDusk = ScheduleTiming.NextOccurrence(delayed, after, NewYork, NewYorkCity);

        Assert.Equal(TimeSpan.FromMinutes(20), afterDusk!.Value - atDusk!.Value);
    }

    // --- previews ----------------------------------------------------------------------------

    [Fact]
    public void NextOccurrences_ReturnsConsecutiveDistinctFiringTimes()
    {
        var schedule = Daily("18:00");
        var after = Instant(2026, 8, 4, 12, 0, NewYork);

        var occurrences = ScheduleTiming.NextOccurrences(schedule, after, NewYork, null, 5);

        Assert.Equal(5, occurrences.Count);
        Assert.Equal(occurrences, occurrences.Distinct().OrderBy(o => o).ToList());
        Assert.Equal(Instant(2026, 8, 8, 18, 0, NewYork), occurrences[^1]);
    }

    [Fact]
    public void NextOccurrences_ForAOneShot_StopsAfterItsSingleFiring()
    {
        var schedule = Base() with
        {
            Kind = Schedule.KindOnce,
            RunAtUtc = Instant(2026, 8, 4, 15, 0, NewYork)
        };

        var occurrences = ScheduleTiming.NextOccurrences(
            schedule, Instant(2026, 8, 4, 12, 0, NewYork), NewYork, null, 5);

        Assert.Single(occurrences);
    }

    // --- catch-up ----------------------------------------------------------------------------

    [Fact]
    public void CatchUp_WhenBarelyLate_CountsAsOnTime()
    {
        var due = DateTimeOffset.UtcNow;
        var decision = ScheduleCatchUp.Decide(
            Daily("18:00"), due, due.AddSeconds(5), TimeSpan.FromHours(1));

        Assert.True(decision.Run);
        Assert.Equal(ScheduleRun.StatusOk, decision.Status);
    }

    [Fact]
    public void CatchUp_WithTheDefaultPolicy_SkipsAFiringMissedWhileTheServerWasDown()
    {
        var due = DateTimeOffset.UtcNow.AddMinutes(-20);
        var decision = ScheduleCatchUp.Decide(
            Daily("18:00"), due, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.False(decision.Run);
        Assert.Equal(ScheduleRun.StatusMissed, decision.Status);
    }

    [Fact]
    public void CatchUp_WithRunOnce_RunsAFiringMissedInsideTheLimit()
    {
        var schedule = Daily("22:00") with { CatchUpPolicy = Schedule.CatchUpRunOnce };
        var due = DateTimeOffset.UtcNow.AddMinutes(-20);

        var decision = ScheduleCatchUp.Decide(schedule, due, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.True(decision.Run);
        Assert.Equal(ScheduleRun.StatusOk, decision.Status);
    }

    [Fact]
    public void CatchUp_WithRunOnce_StillSkipsAFiringBeyondTheLimit()
    {
        var schedule = Daily("22:00") with { CatchUpPolicy = Schedule.CatchUpRunOnce };
        var due = DateTimeOffset.UtcNow.AddHours(-9);

        var decision = ScheduleCatchUp.Decide(schedule, due, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        Assert.False(decision.Run);
        Assert.Equal(ScheduleRun.StatusMissed, decision.Status);
    }

    [Fact]
    public void CatchUp_WhenTheClockAppearsToHaveJumped_RunsNothing()
    {
        // Same guard as the power-cut and internet-outage reports: an NTP sync after a flat RTC
        // battery must not look like a year of schedules all coming due at once.
        var schedule = Daily("22:00") with { CatchUpPolicy = Schedule.CatchUpRunOnce };
        var due = DateTimeOffset.UtcNow.AddDays(-800);

        var decision = ScheduleCatchUp.Decide(schedule, due, DateTimeOffset.UtcNow, TimeSpan.FromDays(900));

        Assert.False(decision.Run);
        Assert.Equal(ScheduleRun.StatusMissed, decision.Status);
    }

    // --- helpers -----------------------------------------------------------------------------

    private static void AssertCloseTo(TimeOnly expected, TimeOnly actual, TimeSpan tolerance)
    {
        var difference = (actual - expected).Duration();
        Assert.True(
            difference <= tolerance,
            $"Expected {actual:HH:mm} to be within {tolerance.TotalMinutes:0} minutes of {expected:HH:mm}.");
    }

    private static DateTimeOffset Instant(int year, int month, int day, int hour, int minute, TimeZoneInfo zone) =>
        ScheduleTiming.ResolveLocal(new DateTime(year, month, day, hour, minute, 0), zone);

    private static Schedule Daily(string timeOfDay) =>
        Base() with { Kind = Schedule.KindDaily, TimeOfDay = timeOfDay };

    private static Schedule Interval(int seconds) =>
        Base() with { Kind = Schedule.KindInterval, IntervalSeconds = seconds };

    private static Schedule Cron(string expression) =>
        Base() with { Kind = Schedule.KindCron, CronExpression = expression };

    private static Schedule Solar(string solarEvent, int offsetMinutes) =>
        Base() with { Kind = Schedule.KindSolar, SolarEvent = solarEvent, SolarOffsetMinutes = offsetMinutes };

    private static Schedule Darkness(string solarEvent, double elevationDegrees) =>
        Base() with { Kind = Schedule.KindSolar, SolarEvent = solarEvent, SolarElevationDegrees = elevationDegrees };

    private static Schedule Base() => new(
        Guid.NewGuid(), "Test schedule", true, Schedule.KindDaily, "", [], 0, "", "", "", null,
        Schedule.SolarSunset, 0, SolarCalculator.CivilTwilightDegrees,
        new AutomationAction(AutomationAction.TypeMqtt, null, "", null, "test/topic", "on", false, "", "POST", "", "", "application/json"),
        Schedule.CatchUpSkip, null, "", "", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
