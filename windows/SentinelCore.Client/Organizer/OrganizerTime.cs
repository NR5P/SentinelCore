using System.Globalization;
using System.Text;

namespace SentinelCore.Client.Organizer;

/// <summary>
/// Field clocks: "&lt;13-digit unix ms&gt;-&lt;4-digit counter&gt;-&lt;device id&gt;", compared as plain strings.
/// Hybrid logical: never runs backwards and never falls behind a clock this device has seen, so an
/// edit made here always sorts after the version of the field it replaced.
/// </summary>
public sealed class HybridClock
{
    private readonly object _gate = new();
    private long _lastMillis;
    private int _lastCounter;

    public HybridClock(string deviceId)
    {
        var cleaned = new string(deviceId.Where(c => (c < 128 && char.IsLetterOrDigit(c)) || "_.:-".Contains(c)).ToArray());
        DeviceId = cleaned.Length == 0 ? "windows" : cleaned[..Math.Min(64, cleaned.Length)];
    }

    public string DeviceId { get; }

    public string Next(long? nowMillis = null)
    {
        var now = nowMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (now > _lastMillis)
            {
                _lastMillis = now;
                _lastCounter = 0;
            }
            else
            {
                _lastCounter = (_lastCounter + 1) % 10_000;
            }
            return Format(_lastMillis, _lastCounter, DeviceId);
        }
    }

    public void Observe(string clock)
    {
        var parts = clock.Split('-', 3);
        if (parts.Length != 3 || parts[0].Length != 13 || parts[1].Length != 4) return;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var millis)) return;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var counter)) return;
        lock (_gate)
        {
            if (millis > _lastMillis || (millis == _lastMillis && counter > _lastCounter))
            {
                _lastMillis = millis;
                _lastCounter = counter;
            }
        }
    }

    public static string Format(long millis, int counter, string device) =>
        string.Create(CultureInfo.InvariantCulture, $"{millis:D13}-{counter:D4}-{device}");
}

/// <summary>
/// The date formats inside Organizer fields: a timed moment is ISO 8601 with its offset
/// ("2026-09-23T17:00:00-05:00"), an all-day one is a bare "2026-09-23". Times are handled as a
/// DateTimeOffset whose offset is the one the given time zone has at that moment - the .NET
/// equivalent of the ZonedDateTime the Android port walks.
/// </summary>
public static class OrganizerDates
{
    /// <summary>A wall-clock time in a zone. A time skipped by a daylight-saving jump moves forward
    /// past the gap, and a time that happens twice takes the first - both as java.time does.</summary>
    public static DateTimeOffset At(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var before = zone.GetUtcOffset(local.AddHours(-3));
            var after = zone.GetUtcOffset(local.AddHours(3));
            local = local + (after - before);
        }
        if (zone.IsAmbiguousTime(local))
        {
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max());
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The same instant, expressed with the zone's offset at that moment.</summary>
    public static DateTimeOffset InZone(DateTimeOffset moment, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(moment, zone);

    public static DateTimeOffset StartOfDay(DateTime date, TimeZoneInfo zone) => At(date.Date, zone);

    public static DateTimeOffset? Parse(string? value, bool allDay, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (allDay || value.Length == 10)
        {
            return DateTime.TryParseExact(value[..Math.Min(10, value.Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
                ? StartOfDay(date, zone)
                : null;
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? InZone(parsed, zone)
            : null;
    }

    public static string Ymd(DateTimeOffset date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string IsoLocal(DateTimeOffset date) =>
        date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + OffsetText(date.Offset);

    private static string OffsetText(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var magnitude = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{magnitude.Hours:D2}:{magnitude.Minutes:D2}");
    }

    public static string FieldValue(DateTimeOffset date, bool allDay) => allDay ? Ymd(date) : IsoLocal(date);

    public static string Timestamp(DateTimeOffset? moment = null) =>
        (moment ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Repeating events and reminders: FREQ DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, BYDAY,
/// BYMONTHDAY, COUNT and UNTIL - the subset every client supports (see ORGANIZER_SYNC.md). A port
/// of OrganizerKit's Recurrence, walking local calendar fields so a weekly 9am meeting stays at 9am
/// across a daylight-saving change.
/// </summary>
public sealed record RecurrenceRule(
    string Frequency,
    int Interval = 1,
    int? Count = null,
    DateTimeOffset? Until = null,
    IReadOnlyList<int>? ByWeekday = null, // 0 = Sunday ... 6 = Saturday
    IReadOnlyList<int>? ByMonthDay = null)
{
    private static readonly string[] Codes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    public string RRuleValue
    {
        get
        {
            var parts = new List<string> { $"FREQ={Frequency}" };
            if (Interval > 1) parts.Add(string.Create(CultureInfo.InvariantCulture, $"INTERVAL={Interval}"));
            if (ByWeekday is { Count: > 0 } days) parts.Add("BYDAY=" + string.Join(",", days.Select(day => Codes[day])));
            if (ByMonthDay is { Count: > 0 } monthDays) parts.Add("BYMONTHDAY=" + string.Join(",", monthDays.Select(d => d.ToString(CultureInfo.InvariantCulture))));
            if (Count is { } count) parts.Add(string.Create(CultureInfo.InvariantCulture, $"COUNT={count}"));
            if (Until is { } until) parts.Add("UNTIL=" + until.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            return string.Join(";", parts);
        }
    }

    // Records compare lists by reference; a rule is the same rule if it says the same thing.
    public bool Equals(RecurrenceRule? other) => other is not null && RRuleValue == other.RRuleValue;

    public override int GetHashCode() => RRuleValue.GetHashCode();

    /// <summary>Every start of the series from <paramref name="start"/> on, in order.</summary>
    public IEnumerable<DateTimeOffset> Starts(DateTimeOffset start, TimeZoneInfo zone, int limit = 3000)
    {
        var produced = 0;
        var localStart = start.DateTime;
        var time = localStart.TimeOfDay;
        for (var step = 0; step < limit; step++)
        {
            IEnumerable<DateTimeOffset> candidates;
            switch (Frequency)
            {
                case "DAILY":
                    candidates = new[] { OrganizerDates.At(localStart.AddDays((double)step * Interval), zone) };
                    break;
                case "WEEKLY":
                {
                    var weekday = (int)localStart.DayOfWeek;
                    var weekStart = localStart.Date.AddDays(-weekday).AddDays(7.0 * step * Interval);
                    var days = ByWeekday is { Count: > 0 } chosen ? chosen : new[] { weekday };
                    candidates = days.OrderBy(day => day).Select(day => OrganizerDates.At(weekStart.AddDays(day) + time, zone)).ToList();
                    break;
                }
                case "MONTHLY":
                {
                    var month = new DateTime(localStart.Year, localStart.Month, 1).AddMonths(step * Interval);
                    var last = DateTime.DaysInMonth(month.Year, month.Month);
                    var days = ByMonthDay is { Count: > 0 } chosen ? chosen : new[] { localStart.Day };
                    candidates = days
                        .Select(day => day < 0 ? last + day + 1 : day)
                        .Where(day => day >= 1 && day <= last)
                        .OrderBy(day => day)
                        .Select(day => OrganizerDates.At(month.AddDays(day - 1) + time, zone))
                        .ToList();
                    break;
                }
                case "YEARLY":
                {
                    var year = localStart.Year + step * Interval;
                    if (year > 9999) yield break;
                    // 29 February only happens in leap years; other years skip it.
                    candidates = localStart.Month == 2 && localStart.Day == 29 && !DateTime.IsLeapYear(year)
                        ? Array.Empty<DateTimeOffset>()
                        : new[] { OrganizerDates.At(new DateTime(year, localStart.Month, localStart.Day) + time, zone) };
                    break;
                }
                default:
                    yield break;
            }
            foreach (var date in candidates)
            {
                if (date < start) continue;
                if (Until is { } until && date > until) yield break;
                produced++;
                if (Count is { } count && produced > count) yield break;
                yield return date;
            }
        }
    }

    public static RecurrenceRule? Parse(string? text, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = new Dictionary<string, string>();
        foreach (var part in text.Split(';'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2) parts[pair[0].ToUpperInvariant()] = pair[1];
        }
        if (!parts.TryGetValue("FREQ", out var frequency) || frequency is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY")) return null;
        return new RecurrenceRule(
            frequency,
            Interval: Math.Max(1, parts.TryGetValue("INTERVAL", out var interval) && int.TryParse(interval, out var i) ? i : 1),
            Count: parts.TryGetValue("COUNT", out var count) && int.TryParse(count, out var c) ? c : null,
            Until: parts.TryGetValue("UNTIL", out var until) ? ParseUntil(until, zone) : null,
            ByWeekday: parts.TryGetValue("BYDAY", out var byDay)
                ? byDay.Split(',').Select(code => Array.IndexOf(Codes, code.Length >= 2 ? code[^2..] : code)).Where(index => index >= 0).ToList()
                : null,
            ByMonthDay: parts.TryGetValue("BYMONTHDAY", out var byMonthDay)
                ? byMonthDay.Split(',').Select(value => int.TryParse(value, out var d) ? (int?)d : null).OfType<int>().ToList()
                : null);
    }

    // "20261231" means the end of that local day; "20261231T000000Z" is an exact UTC instant.
    private static DateTimeOffset? ParseUntil(string text, TimeZoneInfo zone)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        if (!DateTime.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        if (digits.Length >= 14)
        {
            var time = new TimeSpan(int.Parse(digits[8..10], CultureInfo.InvariantCulture), int.Parse(digits[10..12], CultureInfo.InvariantCulture),
                int.Parse(digits[12..14], CultureInfo.InvariantCulture));
            return text.EndsWith('Z')
                ? OrganizerDates.InZone(new DateTimeOffset(date + time, TimeSpan.Zero), zone)
                : OrganizerDates.At(date + time, zone);
        }
        return OrganizerDates.At(date + new TimeSpan(23, 59, 59), zone);
    }
}

/// <param name="OriginalStart">The start this occurrence has in its series, in field format - what
/// goes in exdates or a detached copy's originalStart. Null for an event that doesn't repeat.</param>
public sealed record Occurrence(OrganizerItem Item, DateTimeOffset Start, DateTimeOffset End, string? OriginalStart);

public static class Recurrence
{
    public static (DateTimeOffset Start, DateTimeOffset End)? Span(OrganizerItem @event, TimeZoneInfo zone)
    {
        var allDay = @event.Bool("allDay");
        if (OrganizerDates.Parse(@event.String("start"), allDay, zone) is not { } start) return null;
        var end = OrganizerDates.Parse(@event.String("end"), allDay, zone);
        if (end is null || end <= start)
        {
            end = allDay ? OrganizerDates.At(start.DateTime.AddDays(1), zone) : start.AddHours(1);
        }
        return (start, end.Value);
    }

    public static List<Occurrence> Occurrences(OrganizerItem @event, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        if (Span(@event, zone) is not var (start, end)) return new List<Occurrence>();
        var duration = end - start;
        var allDay = @event.Bool("allDay");
        var rule = RecurrenceRule.Parse(@event.String("recurrence"), zone);
        if (rule is null)
        {
            return start < to && end > from
                ? new List<Occurrence> { new(@event, start, end, null) }
                : new List<Occurrence>();
        }

        var skipped = @event.List("exdates")
            .Select(value => OrganizerDates.Parse(value as string, allDay, zone))
            .OfType<DateTimeOffset>()
            .Select(date => Key(date, allDay))
            .ToHashSet();
        var result = new List<Occurrence>();
        foreach (var occurrenceStart in rule.Starts(start, zone))
        {
            if (occurrenceStart >= to) break;
            var occurrenceEnd = allDay
                ? OrganizerDates.At(occurrenceStart.DateTime + duration, zone)
                : occurrenceStart + duration;
            if (occurrenceEnd <= from || skipped.Contains(Key(occurrenceStart, allDay))) continue;
            result.Add(new Occurrence(@event, occurrenceStart, occurrenceEnd, OrganizerDates.FieldValue(occurrenceStart, allDay)));
        }
        return result;
    }

    /// <summary>When the last occurrence ends: <see cref="DateTimeOffset.MaxValue"/> when it repeats
    /// forever, null when it has no usable start.</summary>
    public static DateTimeOffset? LastEnd(OrganizerItem @event, TimeZoneInfo zone)
    {
        if (Span(@event, zone) is not var (start, end)) return null;
        var rule = RecurrenceRule.Parse(@event.String("recurrence"), zone);
        if (rule is null) return end;
        if (rule.Until is null && rule.Count is null) return DateTimeOffset.MaxValue;
        var last = rule.Starts(start, zone).DefaultIfEmpty(start).Last();
        return last + (end - start);
    }

    /// <summary>
    /// The occurrence after the one just completed - the same rule as the dashboard and the other
    /// apps, so a weekly chore ticked off late doesn't silently skip a week.
    /// </summary>
    public static string? NextDue(OrganizerItem reminder, TimeZoneInfo zone)
    {
        var allDay = reminder.Bool("allDay");
        if (OrganizerDates.Parse(reminder.String("due"), allDay, zone) is not { } due) return null;
        if (RecurrenceRule.Parse(reminder.String("recurrence"), zone) is not { } rule) return null;
        foreach (var next in rule.Starts(due, zone))
        {
            if (next > due) return OrganizerDates.FieldValue(next, allDay);
        }
        return null;
    }

    private static string Key(DateTimeOffset date, bool allDay) =>
        allDay ? OrganizerDates.Ymd(date) : date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
