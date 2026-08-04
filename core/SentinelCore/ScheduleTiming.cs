using Cronos;

namespace SentinelCore;

// Works out when a Schedule fires next. Pure and side-effect free - no clock, no database, no
// network - in the same spirit as InternetOutageReport.Evaluate and StartupDiagnosis, because
// every hard case here (daylight saving, a window that crosses midnight, polar night) is
// impossible to test reliably against a live clock and trivial to test against a given instant.
//
// The time zone is a parameter rather than TimeZoneInfo.Local for the same reason: the DST tests
// have to pin a specific zone regardless of the machine running them.
public static class ScheduleTiming
{
    // Guards the day-walking loops below. Eight days is enough for any day-of-week set to come
    // round; the solar loop needs far more, because a polar winter can put months between one
    // sunrise and the next.
    private const int DayWalkLimit = 8;
    private const int SolarDayWalkLimit = 400;

    public const int MinimumIntervalSeconds = 10;

    // The next instant strictly after `after` at which this schedule should fire, or null if it
    // never will again (a one-shot in the past, a malformed expression, sunset at a latitude
    // where the sun is not currently setting).
    public static DateTimeOffset? NextOccurrence(
        Schedule schedule, DateTimeOffset after, TimeZoneInfo zone, GeoCoordinate? location) =>
        schedule.Kind?.ToLowerInvariant() switch
        {
            Schedule.KindDaily => NextDaily(schedule, after, zone),
            Schedule.KindInterval => NextInterval(schedule, after, zone),
            Schedule.KindCron => NextCron(schedule, after, zone),
            Schedule.KindOnce => schedule.RunAtUtc > after ? schedule.RunAtUtc : null,
            Schedule.KindSolar => NextSolar(schedule, after, zone, location),
            _ => null
        };

    // Backs the "next 5 runs" preview in the editors, which is the only practical way to tell
    // whether a cron expression or a day-of-week set says what someone meant it to say.
    public static IReadOnlyList<DateTimeOffset> NextOccurrences(
        Schedule schedule, DateTimeOffset after, TimeZoneInfo zone, GeoCoordinate? location, int count)
    {
        var occurrences = new List<DateTimeOffset>();
        var cursor = after;

        for (var i = 0; i < count; i++)
        {
            var next = NextOccurrence(schedule, cursor, zone, location);
            if (next is null) break;

            occurrences.Add(next.Value);
            cursor = next.Value;
        }

        return occurrences;
    }

    private static DateTimeOffset? NextDaily(Schedule schedule, DateTimeOffset after, TimeZoneInfo zone)
    {
        if (!TryParseTimeOfDay(schedule.TimeOfDay, out var timeOfDay)) return null;

        var startDate = LocalDate(after, zone);

        for (var offset = 0; offset <= DayWalkLimit; offset++)
        {
            var date = startDate.AddDays(offset);
            var candidate = ResolveLocal(date.ToDateTime(timeOfDay), zone);

            // Checked on the resolved instant rather than the candidate date, so a time that a
            // spring-forward gap pushed over midnight is filtered by the day it actually lands on.
            if (!MatchesDayOfWeek(schedule.DaysOfWeek, TimeZoneInfo.ConvertTime(candidate, zone).DayOfWeek))
                continue;

            if (candidate > after) return candidate;
        }

        return null;
    }

    // Two anchoring rules, because the intuitive answer differs depending on whether a window is
    // set. Without one, ticks align to the Unix epoch, so "every 15 minutes" lands on :00/:15/
    // :30/:45 like a wall clock rather than drifting from whenever the server happened to boot.
    // With one, ticks are anchored to each day's window opening, so "every 30 minutes from 09:00
    // to 17:00" fires at 09:00 - and a window shorter than the interval still fires once, which
    // an epoch grid intersected with a window would not guarantee.
    private static DateTimeOffset? NextInterval(Schedule schedule, DateTimeOffset after, TimeZoneInfo zone)
    {
        if (schedule.IntervalSeconds < MinimumIntervalSeconds) return null;

        var interval = TimeSpan.FromSeconds(schedule.IntervalSeconds);

        // Both bounds or neither - half a window is a half-configured schedule, and confining
        // the ticks to a guessed other half would be worse than ignoring it.
        var hasStart = TryParseTimeOfDay(schedule.WindowStart, out var windowStart);
        var hasEnd = TryParseTimeOfDay(schedule.WindowEnd, out var windowEnd);
        if (!hasStart || !hasEnd) return AlignToEpoch(after, schedule.IntervalSeconds);

        var windowLength = WindowLength(windowStart, windowEnd);
        var startDate = LocalDate(after, zone);

        // Starts a day early: a window that crosses midnight (22:00 -> 06:00) may have opened
        // yesterday and still be open now.
        for (var offset = -1; offset <= DayWalkLimit; offset++)
        {
            var open = ResolveLocal(startDate.AddDays(offset).ToDateTime(windowStart), zone);
            var close = open + windowLength;
            if (close <= after) continue;

            var candidate = open > after ? open : AlignFrom(open, interval, after);
            if (candidate < close) return candidate;
        }

        return null;
    }

    private static DateTimeOffset? NextCron(Schedule schedule, DateTimeOffset after, TimeZoneInfo zone)
    {
        if (!TryParseCron(schedule.CronExpression, out var expression)) return null;

        // Cronos resolves the expression against the zone itself, which is what makes a cron
        // schedule survive daylight saving the same way a "daily" one does.
        return expression.GetNextOccurrence(after, zone, inclusive: false);
    }

    private static DateTimeOffset? NextSolar(
        Schedule schedule, DateTimeOffset after, TimeZoneInfo zone, GeoCoordinate? location)
    {
        if (location is null || !location.IsValid) return null;

        var evening = Schedule.IsEveningSolarEvent(schedule.SolarEvent);

        // sunrise/sunset are pinned to the elevation that defines them; dusk/dawn are whatever
        // darkness the schedule asked for.
        var elevation = Schedule.UsesElevation(schedule.SolarEvent)
            ? schedule.SolarElevationDegrees
            : SolarCalculator.SunriseElevationDegrees;

        var offsetMinutes = TimeSpan.FromMinutes(schedule.SolarOffsetMinutes);

        // Starts a day early because a negative offset ("30 minutes before sunrise") can pull
        // today's event back across the boundary we started from.
        var startDate = LocalDate(after, zone).AddDays(-1);

        for (var offset = 0; offset < SolarDayWalkLimit; offset++)
        {
            var solarEvent = SolarCalculator.Calculate(startDate.AddDays(offset), location, evening, elevation);

            // Null means the sun never reaches that altitude on that date at that latitude -
            // polar night, or a summer where it never gets as dark as was asked for. Skip the
            // day rather than stopping; both conditions end eventually.
            if (solarEvent is null) continue;

            var candidate = solarEvent.Value + offsetMinutes;
            if (candidate <= after) continue;

            if (!MatchesDayOfWeek(schedule.DaysOfWeek, TimeZoneInfo.ConvertTime(candidate, zone).DayOfWeek))
                continue;

            return candidate;
        }

        return null;
    }

    // Turns a wall-clock time into an actual instant, which is only interesting twice a year.
    //
    // Spring forward: the wall clock skips an hour, so a schedule set inside the gap describes a
    // time that does not exist that day. It fires at the first moment that does exist, rather
    // than silently skipping the day - someone whose gate opens at 02:30 wants it open.
    //
    // Fall back: the wall clock repeats an hour, so the time happens twice. The earlier of the
    // two is taken, which - together with every caller requiring strictly-greater-than the last
    // firing - is what stops a 01:30 schedule running twice that night.
    public static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // Walked a minute at a time rather than added as a fixed hour: DST gaps are usually
            // an hour but are not required to be, and a few dozen cheap iterations twice a year
            // is not worth hard-coding an assumption over.
            var probe = local;
            for (var minute = 0; minute < 24 * 60 && zone.IsInvalidTime(probe); minute++)
                probe = probe.AddMinutes(1);

            local = probe;
        }

        if (zone.IsAmbiguousTime(local))
        {
            // The larger offset is the pre-transition one, which is the earlier of the two
            // instants that share this wall-clock time.
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            return new DateTimeOffset(local, offsets.Max());
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static bool TryParseTimeOfDay(string? value, out TimeOnly timeOfDay)
    {
        timeOfDay = default;
        return !string.IsNullOrWhiteSpace(value) &&
               TimeOnly.TryParseExact(value.Trim(), "HH:mm", out timeOfDay);
    }

    // Five fields (minute hour day-of-month month day-of-week) or six with a leading seconds
    // field. Anything else is rejected here rather than throwing out of the timing loop.
    public static bool TryParseCron(string? expression, out CronExpression parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var format = fields.Length switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => (CronFormat?)null
        };

        if (format is null) return false;

        try
        {
            parsed = CronExpression.Parse(expression.Trim(), format.Value);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    private static bool MatchesDayOfWeek(IReadOnlyList<int> daysOfWeek, DayOfWeek dayOfWeek) =>
        daysOfWeek.Count == 0 || daysOfWeek.Contains((int)dayOfWeek);

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // A window whose end is not after its start wraps past midnight; equal start and end is the
    // whole day, since a zero-length window could never fire and is never what someone meant.
    private static TimeSpan WindowLength(TimeOnly start, TimeOnly end) =>
        end > start ? end - start : TimeSpan.FromDays(1) - (start - end);

    private static DateTimeOffset AlignToEpoch(DateTimeOffset after, int intervalSeconds)
    {
        var ticks = after.ToUnixTimeSeconds() / intervalSeconds + 1;
        return DateTimeOffset.FromUnixTimeSeconds(ticks * intervalSeconds);
    }

    private static DateTimeOffset AlignFrom(DateTimeOffset anchor, TimeSpan interval, DateTimeOffset after)
    {
        var elapsed = after - anchor;
        var ticks = (long)(elapsed.Ticks / interval.Ticks) + 1;
        return anchor + TimeSpan.FromTicks(ticks * interval.Ticks);
    }
}

// Whether a firing time that has already passed should still be honoured. Separated from
// ScheduleWorker for the usual reason: it is the part with the interesting rules, and it is
// worth being able to test them without a running host.
public sealed record CatchUpDecision(bool Run, string Status, string Reason);

public static class ScheduleCatchUp
{
    // Below this, a firing is simply on time - the worker wakes a moment late every time, and
    // treating that as a missed run would be absurd.
    public static readonly TimeSpan OnTimeTolerance = TimeSpan.FromSeconds(30);

    public static CatchUpDecision Decide(
        Schedule schedule, DateTimeOffset dueAt, DateTimeOffset now, TimeSpan maximumCatchUp)
    {
        var lateness = now - dueAt;

        if (lateness <= OnTimeTolerance)
            return new CatchUpDecision(true, ScheduleRun.StatusOk, "on time");

        // The same guard the power-cut and internet-outage reports use, for the same reason: a
        // machine whose clock just jumped (flat RTC, NTP finally syncing) would otherwise decide
        // that a year of firings are all due right now.
        if (lateness > StartupDiagnosis.MaximumPlausibleDowntime)
            return new CatchUpDecision(false, ScheduleRun.StatusMissed,
                "the clock appears to have jumped; not running a firing this old");

        if (!string.Equals(schedule.CatchUpPolicy, Schedule.CatchUpRunOnce, StringComparison.OrdinalIgnoreCase))
            return new CatchUpDecision(false, ScheduleRun.StatusMissed,
                $"missed by {Describe(lateness)} while the server was unavailable");

        if (lateness > maximumCatchUp)
            return new CatchUpDecision(false, ScheduleRun.StatusMissed,
                $"missed by {Describe(lateness)}, which is beyond the catch-up limit of {Describe(maximumCatchUp)}");

        return new CatchUpDecision(true, ScheduleRun.StatusOk, $"caught up {Describe(lateness)} late");
    }

    private static string Describe(TimeSpan span) => OutageEmail.Describe(span);
}
