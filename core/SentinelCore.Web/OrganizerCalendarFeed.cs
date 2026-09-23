using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

// Renders Organizer events as an iCalendar (RFC 5545) feed, for the private subscription link
// people paste into Apple Calendar, Google Calendar or Outlook. Read-only on their side: those
// apps poll the link and show what's here, and edits still happen in SentinelCore.
//
// Event fields (see docs/ORGANIZER_SYNC.md): title, notes, location, start, end, allDay, timeZone,
// recurrence (an RRULE value such as "FREQ=WEEKLY;BYDAY=MO"), exdates, seriesId + originalStart
// for one edited occurrence of a repeating event, alerts (minutes before), and links.
public static class OrganizerCalendarFeed
{
    public static string Build(IEnumerable<OrganizerItem> events, IReadOnlyList<OrganizerSpace> spaces)
    {
        var builder = new StringBuilder();
        Line(builder, "BEGIN:VCALENDAR");
        Line(builder, "VERSION:2.0");
        Line(builder, "PRODID:-//SentinelCore//Organizer//EN");
        Line(builder, "CALSCALE:GREGORIAN");
        Line(builder, "METHOD:PUBLISH");
        Line(builder, "X-WR-CALNAME:SentinelCore");
        Line(builder, "REFRESH-INTERVAL;VALUE=DURATION:PT15M");
        Line(builder, "X-PUBLISHED-TTL:PT15M");

        var spaceNames = spaces.ToDictionary(space => space.Id, space => space.Name, StringComparer.Ordinal);
        var eventList = events.ToList();

        // An edited occurrence is excluded from its series (exdates) AND published as an override
        // (RECURRENCE-ID). Inside SentinelCore the exclusion is what hides the original; in an
        // iCalendar feed the override already does that, and Google drops an override whose date
        // is also an EXDATE - so those dates are left out of the series' EXDATE list here.
        var overridden = eventList
            .Where(item => Text(item.Fields, "seriesId") is not null && Text(item.Fields, "originalStart") is not null)
            .Select(item => (Text(item.Fields, "seriesId")!, Text(item.Fields, "originalStart")!))
            .ToHashSet();

        foreach (var item in eventList)
        {
            var start = Text(item.Fields, "start");
            if (start is null) continue;
            var allDay = item.Fields["allDay"] is JsonValue flag && flag.TryGetValue<bool>(out var isAllDay) && isAllDay;
            var timeZone = Text(item.Fields, "timeZone");
            var seriesId = Text(item.Fields, "seriesId");

            Line(builder, "BEGIN:VEVENT");
            // A detached occurrence shares its series' UID and says which occurrence it replaces -
            // that's how calendar apps know to show it instead of the original.
            Line(builder, $"UID:{seriesId ?? item.Id}@sentinelcore");
            Line(builder, $"DTSTAMP:{Utc(item.UpdatedAt)}");
            Line(builder, $"LAST-MODIFIED:{Utc(item.UpdatedAt)}");
            if (DateProperty("DTSTART", start, allDay, timeZone) is { } dtStart) Line(builder, dtStart);
            if (Text(item.Fields, "end") is { } end && DateProperty("DTEND", end, allDay, timeZone) is { } dtEnd) Line(builder, dtEnd);
            if (seriesId is not null && Text(item.Fields, "originalStart") is { } original && DateProperty("RECURRENCE-ID", original, allDay, timeZone) is { } recurrenceId)
                Line(builder, recurrenceId);

            Line(builder, $"SUMMARY:{Escape(Text(item.Fields, "title") ?? "Untitled")}");
            if (Text(item.Fields, "location") is { Length: > 0 } location) Line(builder, $"LOCATION:{Escape(location)}");

            var description = new StringBuilder(Text(item.Fields, "notes") ?? "");
            foreach (var link in item.Fields["links"] as JsonArray ?? [])
            {
                if (link is JsonObject linkObject && Text(linkObject, "url") is { } url)
                    description.Append(description.Length > 0 ? "\n" : "").Append(Text(linkObject, "title") is { } title ? $"{title}: {url}" : url);
            }
            if (description.Length > 0) Line(builder, $"DESCRIPTION:{Escape(description.ToString())}");
            if (spaceNames.GetValueOrDefault(item.SpaceId) is { } spaceName) Line(builder, $"CATEGORIES:{Escape(spaceName)}");

            if (Text(item.Fields, "recurrence") is { Length: > 0 } rule && IsSafeRule(rule))
            {
                Line(builder, $"RRULE:{rule}");
                foreach (var exdate in item.Fields["exdates"] as JsonArray ?? [])
                {
                    if (exdate is JsonValue value && value.TryGetValue<string>(out var text)
                        && !overridden.Contains((item.Id, text))
                        && DateProperty("EXDATE", text, allDay, timeZone) is { } exdateLine)
                        Line(builder, exdateLine);
                }
            }

            foreach (var alert in item.Fields["alerts"] as JsonArray ?? [])
            {
                if (alert is not JsonValue minutesValue || !minutesValue.TryGetValue<int>(out var minutes)) continue;
                Line(builder, "BEGIN:VALARM");
                Line(builder, "ACTION:DISPLAY");
                Line(builder, $"DESCRIPTION:{Escape(Text(item.Fields, "title") ?? "Event")}");
                Line(builder, $"TRIGGER:-PT{Math.Max(0, minutes)}M");
                Line(builder, "END:VALARM");
            }

            Line(builder, "END:VEVENT");
        }

        Line(builder, "END:VCALENDAR");
        return builder.ToString();
    }

    // "2026-09-23" for an all-day event, an ISO 8601 date-time with offset otherwise. With a time
    // zone the local time is written against it, so a repeating 9am meeting stays at 9am across a
    // daylight-saving change instead of drifting an hour; without one it goes out as UTC.
    private static string? DateProperty(string name, string value, bool allDay, string? timeZone)
    {
        if (allDay)
        {
            return DateOnly.TryParse(value.Length >= 10 ? value[..10] : value, CultureInfo.InvariantCulture, out var date)
                ? $"{name};VALUE=DATE:{date:yyyyMMdd}"
                : null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)) return null;
        if (timeZone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            var local = TimeZoneInfo.ConvertTime(moment, zone);
            return $"{name};TZID={timeZone}:{local:yyyyMMdd'T'HHmmss}";
        }
        return $"{name}:{Utc(moment)}";
    }

    // RRULE goes out verbatim, so it has to stay a single property value - a stray newline in a
    // synced field could otherwise inject arbitrary lines into someone's calendar.
    private static bool IsSafeRule(string rule) =>
        rule.Length < 500 && rule.All(c => char.IsAsciiLetterOrDigit(c) || c is '=' or ';' or ',' or '-' or '+' or ':');

    private static string Utc(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string? Text(JsonObject fields, string name) =>
        fields[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal);

    // Lines longer than 75 octets are folded onto continuation lines starting with a space, as
    // the spec requires; some clients reject unfolded long lines outright.
    private static void Line(StringBuilder builder, string line)
    {
        var bytes = 0;
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var width = Encoding.UTF8.GetByteCount(line.AsSpan(i, char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1));
            if (bytes + width > (start == 0 ? 75 : 74))
            {
                builder.Append(line, start, i - start).Append("\r\n ");
                start = i;
                bytes = 0;
            }
            bytes += width;
            if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length) i++;
        }
        builder.Append(line, start, line.Length - start).Append("\r\n");
    }
}
