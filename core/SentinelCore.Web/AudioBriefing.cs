using System.Globalization;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

public sealed record AudioBriefingResult(string Greeting, IReadOnlyList<string> Speech, IReadOnlyList<AudioEntry> Episodes)
{
    // Everything spoken, as one announcement: what a speaker says before the episodes start.
    public string SpokenText => string.Join(" ", new[] { Greeting }.Concat(Speech));
}

// The Morning Briefing: today's date, the weather from the Weather tab, the person's events and
// reminders from the Organizer, then the newest episode of each show they put in the briefing.
// Plain sentences, written to be read aloud by a phone's or a TV's speech voice.
public sealed class AudioBriefingBuilder(
    WeatherService weather,
    IOrganizerStore organizer,
    AppSettingsFile settings,
    IAccountStore accounts,
    IPodcastAudioSource podcasts,
    TimeProvider clock,
    ILogger<AudioBriefingBuilder> logger)
{
    public const int MaxEpisodes = 5;
    private const int MaxListed = 5;

    // The server's own clock zone: it is the household's, the same one wake-up alarms use.
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    public async Task<AudioBriefingResult> BuildAsync(string userKey, CancellationToken cancellationToken)
    {
        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZone);
        var name = await DisplayNameAsync(userKey, cancellationToken);
        var greeting = (now.Hour switch { < 12 => "Good morning", < 17 => "Good afternoon", _ => "Good evening" })
            + (name is null ? "." : $", {name}.");

        var speech = new List<string> { $"It's {now:dddd}, {now:MMMM} {Ordinal(now.Day)}." };
        try
        {
            speech.AddRange(await WeatherLinesAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The briefing could not read the weather");
        }

        try
        {
            if ((await OrganizerEndpoints.ReadOptionsAsync(settings, cancellationToken)).Enabled)
            {
                var user = userKey.Length == 0 ? OrganizerUser.Shared : new OrganizerUser(userKey, null);
                var events = await organizer.GetItemsAsync(user, "event", null, cancellationToken);
                var reminders = await organizer.GetItemsAsync(user, "reminder", null, cancellationToken);
                speech.AddRange(EventLines(events, now));
                speech.AddRange(ReminderLines(reminders, now));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The briefing could not read the Organizer");
        }

        IReadOnlyList<AudioEntry> episodes = [];
        try
        {
            if (await podcasts.EnabledAsync(cancellationToken))
                episodes = await podcasts.BriefingEpisodesAsync(userKey, MaxEpisodes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The briefing could not read the podcasts");
        }
        if (episodes.Count > 0)
            speech.Add(episodes.Count == 1 ? "Here's your news." : $"Here are {episodes.Count} new episodes.");
        else
            speech.Add("Have a great day.");

        return new AudioBriefingResult(greeting, speech, episodes);
    }

    private async Task<string?> DisplayNameAsync(string userKey, CancellationToken cancellationToken)
    {
        if (userKey.Length == 0) return null;
        var account = await accounts.GetByUsernameAsync(userKey, cancellationToken);
        var name = string.IsNullOrWhiteSpace(account?.DisplayName) ? account?.Username : account.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return null;
        // "Nathan Rigg" -> "Nathan": a briefing says hello the way a person would.
        var first = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return char.ToUpper(first[0], CultureInfo.InvariantCulture) + first[1..];
    }

    private async Task<IEnumerable<string>> WeatherLinesAsync(CancellationToken cancellationToken)
    {
        var report = await weather.GetReportAsync(cancellationToken);
        if (report.State != WeatherReportState.Ok) return [];
        var lines = new List<string>();
        var place = report.Location?.City;
        if (report.Current is { } current && !string.IsNullOrWhiteSpace(current.Description))
            lines.Add($"Right now it's {current.TemperatureF} degrees and {current.Description.ToLowerInvariant()}{(place is null ? "" : $" in {place}")}.");
        else if (report.Current is { } bare)
            lines.Add($"Right now it's {bare.TemperatureF} degrees{(place is null ? "" : $" in {place}")}.");

        var today = report.Daily.FirstOrDefault();
        if (today is not null && !string.IsNullOrWhiteSpace(today.ShortForecast))
        {
            var sentence = $"{today.Name}: {today.ShortForecast.ToLowerInvariant()}";
            if (today.TemperatureF is { } temperature) sentence += today.IsDaytime ? $", with a high of {temperature}" : $", with a low of {temperature}";
            if (today.PrecipitationChance is > 0 and var chance) sentence += $" and a {chance} percent chance of rain";
            lines.Add(sentence + ".");
        }

        var warnings = report.Alerts
            .Where(a => a.MessageType != "Cancel" && !string.IsNullOrWhiteSpace(a.Event))
            .Select(a => a.Event)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (warnings.Count == 1) lines.Add($"There is a {warnings[0]} in effect.");
        else if (warnings.Count > 1) lines.Add($"There are {warnings.Count} weather alerts in effect, including a {warnings[0]}.");
        return lines;
    }

    // Public for the tests.
    public IEnumerable<string> EventLines(IEnumerable<OrganizerItem> events, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var todays = new List<(DateTimeOffset? At, string Text)>();
        foreach (var item in events)
        {
            var title = Text(item.Fields, "title");
            var start = Text(item.Fields, "start");
            if (string.IsNullOrWhiteSpace(title) || start is null) continue;
            var allDay = item.Fields["allDay"] is JsonValue flag && flag.TryGetValue<bool>(out var isAllDay) && isAllDay;
            if (!TryParseStart(start, allDay, out var startDate, out var startAt)) continue;

            var recurrence = Text(item.Fields, "recurrence");
            bool happensToday;
            if (string.IsNullOrWhiteSpace(recurrence))
            {
                happensToday = startDate == today;
                // A multi-day event that is still going counts too.
                if (!happensToday && Text(item.Fields, "end") is { } end && TryParseStart(end, allDay, out var endDate, out _))
                    happensToday = startDate < today && (allDay ? endDate > today : endDate >= today);
            }
            else
            {
                var exdates = (item.Fields["exdates"] as JsonArray ?? [])
                    .Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) && TryParseStart(text, allDay, out var d, out _) ? d : (DateOnly?)null)
                    .Where(d => d is not null).Select(d => d!.Value).ToHashSet();
                happensToday = !exdates.Contains(today) && AudioRecurrence.OccursOn(recurrence, startDate, today);
            }
            if (!happensToday) continue;

            if (allDay || startAt is null) todays.Add((null, $"{title}, all day"));
            else
            {
                var local = TimeZoneInfo.ConvertTime(startAt.Value, TimeZone);
                var at = new DateTimeOffset(now.Date + local.TimeOfDay, now.Offset);
                todays.Add((at, $"{title} at {SpokenTime(local)}"));
            }
        }

        if (todays.Count == 0) return ["Your calendar is clear today."];
        var ordered = todays.OrderBy(e => e.At ?? DateTimeOffset.MinValue).Select(e => e.Text).ToList();
        return [$"You have {Count(ordered.Count, "event")} today: {List(ordered)}."];
    }

    public IEnumerable<string> ReminderLines(IEnumerable<OrganizerItem> reminders, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var dueToday = new List<string>();
        var overdue = 0;
        foreach (var item in reminders)
        {
            if (item.Fields["completed"] is JsonValue done && done.TryGetValue<bool>(out var completed) && completed) continue;
            // Sub-tasks are read out with nothing to say whose they are; their parent covers them.
            if (item.ParentId is not null) continue;
            var title = Text(item.Fields, "title");
            var due = Text(item.Fields, "due");
            if (string.IsNullOrWhiteSpace(title) || due is null) continue;
            var allDay = item.Fields["allDay"] is JsonValue flag && flag.TryGetValue<bool>(out var isAllDay) && isAllDay;
            if (!TryParseStart(due, allDay, out var dueDate, out _)) continue;
            if (dueDate == today) dueToday.Add(title);
            else if (dueDate < today) overdue++;
        }

        var lines = new List<string>();
        if (dueToday.Count > 0)
        {
            var count = Count(dueToday.Count, "reminder");
            lines.Add($"{char.ToUpperInvariant(count[0])}{count[1..]} {(dueToday.Count == 1 ? "is" : "are")} due today: {List(dueToday)}.");
        }
        if (overdue > 0)
            lines.Add(overdue == 1 ? "One reminder is overdue." : $"{overdue} reminders are overdue.");
        return lines;
    }

    private bool TryParseStart(string text, bool allDay, out DateOnly date, out DateTimeOffset? at)
    {
        at = null;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            var local = TimeZoneInfo.ConvertTime(parsed, TimeZone);
            // An all-day date written as midnight UTC is that calendar date, not the evening before.
            date = allDay ? DateOnly.FromDateTime(parsed.UtcDateTime) : DateOnly.FromDateTime(local.DateTime);
            at = allDay ? null : parsed;
            return true;
        }
        return false;
    }

    private static string SpokenTime(DateTimeOffset time) =>
        time.Minute == 0 ? time.ToString("h tt", CultureInfo.InvariantCulture) : time.ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string Count(int count, string noun) =>
        count switch { 1 => $"one {noun}", 2 => $"two {noun}s", 3 => $"three {noun}s", _ => $"{count} {noun}s" };

    private static string List(IReadOnlyList<string> items)
    {
        var shown = items.Take(MaxListed).ToList();
        var more = items.Count - shown.Count;
        if (more > 0) shown.Add($"and {more} more");
        return shown.Count switch
        {
            1 => shown[0],
            2 => more > 0 ? $"{shown[0]}, {shown[1]}" : $"{shown[0]} and {shown[1]}",
            _ => more > 0 ? string.Join(", ", shown) : $"{string.Join(", ", shown.Take(shown.Count - 1))}, and {shown[^1]}"
        };
    }

    private static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static string? Text(JsonObject fields, string key) =>
        fields[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}

// Whether a repeating Organizer event (an iCalendar RRULE such as "FREQ=WEEKLY;BYDAY=MO,WE")
// has an occurrence on a given day. Covers what the apps' event editors write: DAILY, WEEKLY
// (with BYDAY), MONTHLY (on the start's day of the month, or BYDAY like 2TU / -1FR) and YEARLY,
// with INTERVAL, COUNT and UNTIL.
public static class AudioRecurrence
{
    private const int MaxSteps = 20000;

    public static bool OccursOn(string rule, DateOnly start, DateOnly day)
    {
        if (day < start) return false;
        var parts = rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim().ToUpperInvariant());
        if (!parts.TryGetValue("FREQ", out var freq)) return false;
        var interval = parts.TryGetValue("INTERVAL", out var i) && int.TryParse(i, out var n) && n > 0 ? n : 1;
        int? count = parts.TryGetValue("COUNT", out var c) && int.TryParse(c, out var cn) && cn > 0 ? cn : null;
        DateOnly? until = parts.TryGetValue("UNTIL", out var u) && u.Length >= 8
            && DateOnly.TryParseExact(u[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ud) ? ud : null;
        if (until is { } last && day > last) return false;
        var byDay = parts.TryGetValue("BYDAY", out var b) ? b.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];

        // Walk the occurrences in order: with COUNT the day only counts if it comes early enough.
        var seen = 0;
        foreach (var occurrence in Occurrences(freq, interval, byDay, start))
        {
            if (occurrence > day || (until is { } end && occurrence > end)) return false;
            seen++;
            if (count is { } limit && seen > limit) return false;
            if (occurrence == day) return true;
            if (seen > MaxSteps) return false;
        }
        return false;
    }

    private static IEnumerable<DateOnly> Occurrences(string freq, int interval, string[] byDay, DateOnly start)
    {
        switch (freq)
        {
            case "DAILY":
                for (var d = start; ; d = d.AddDays(interval)) yield return d;
            case "WEEKLY":
            {
                var days = byDay.Select(Weekday).Where(w => w is not null).Select(w => w!.Value).ToHashSet();
                if (days.Count == 0) days.Add(start.DayOfWeek);
                // Weeks start on Monday, as iCalendar's default WKST does.
                var weekStart = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                for (var week = weekStart; ; week = week.AddDays(7 * interval))
                    for (var offset = 0; offset < 7; offset++)
                    {
                        var d = week.AddDays(offset);
                        if (d >= start && days.Contains(d.DayOfWeek)) yield return d;
                    }
            }
            case "MONTHLY":
                for (var month = new DateOnly(start.Year, start.Month, 1); ; month = month.AddMonths(interval))
                {
                    if (byDay.Length == 0)
                    {
                        if (start.Day <= DateTime.DaysInMonth(month.Year, month.Month))
                        {
                            var d = new DateOnly(month.Year, month.Month, start.Day);
                            if (d >= start) yield return d;
                        }
                        continue;
                    }
                    foreach (var d in byDay.Select(spec => NthWeekday(month, spec)).Where(d => d is not null).Select(d => d!.Value).Order())
                        if (d >= start) yield return d;
                }
            case "YEARLY":
                for (var year = start.Year; ; year += interval)
                {
                    if (start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(year)) continue;
                    yield return new DateOnly(year, start.Month, start.Day);
                }
        }
    }

    // "2TU" = the second Tuesday, "-1FR" = the last Friday, "MO" = every Monday (first here).
    private static DateOnly? NthWeekday(DateOnly month, string spec)
    {
        var weekday = Weekday(spec[^2..]);
        if (weekday is null) return null;
        var nth = spec.Length > 2 && int.TryParse(spec[..^2], out var parsed) ? parsed : 1;
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        if (nth > 0)
        {
            var first = month;
            while (first.DayOfWeek != weekday) first = first.AddDays(1);
            var d = first.AddDays(7 * (nth - 1));
            return d.Month == month.Month ? d : null;
        }
        var lastDay = new DateOnly(month.Year, month.Month, daysInMonth);
        while (lastDay.DayOfWeek != weekday) lastDay = lastDay.AddDays(-1);
        var back = lastDay.AddDays(7 * (nth + 1));
        return back.Month == month.Month ? back : null;
    }

    private static DayOfWeek? Weekday(string code) => code.Length < 2 ? null : code[^2..] switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null
    };
}
