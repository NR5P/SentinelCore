using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Client.Organizer;

/// <summary>
/// How much of the Organizer this PC keeps for use with no connection, chosen in Settings. A
/// computer keeps everything by default - it is what refills a rebuilt server - where phones
/// default light. Matches OrganizerKit's and Android's OfflinePolicy rule for rule.
/// </summary>
/// <param name="WindowDays">Light mode only: how far back to keep. Null keeps everything.</param>
/// <param name="StorageCapBytes">Attachment files only - items are tiny. Null is no limit.</param>
public sealed record OfflinePolicy(
    OfflineMode Mode,
    int? WindowDays,
    OfflineAttachments Attachments,
    long? StorageCapBytes)
{
    public static readonly OfflinePolicy PhoneDefault = new(OfflineMode.Light, 90, OfflineAttachments.Thumbnails, 500L * 1024 * 1024);
    public static readonly OfflinePolicy FullDefault = new(OfflineMode.Full, null, OfflineAttachments.Full, null);

    public bool Widens(OfflinePolicy old)
    {
        static long Rank(OfflinePolicy policy) => policy.Mode switch
        {
            OfflineMode.Off => 0,
            OfflineMode.Light => policy.WindowDays is { } days ? 1L + days : long.MaxValue - 1,
            _ => long.MaxValue,
        };
        return Rank(this) > Rank(old);
    }

    /// <summary>
    /// Item ids a light copy doesn't keep. Never an item with edits waiting to go up, never
    /// anything pinned to stay offline, never the parents of something kept, and children always
    /// go with their parent.
    /// </summary>
    public HashSet<string> ItemsToPrune(
        IReadOnlyDictionary<string, OrganizerItem> items,
        IReadOnlySet<string> protectedIds,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        if (Mode != OfflineMode.Light || WindowDays is not { } days) return new HashSet<string>();
        var cutoff = OrganizerDates.At(OrganizerDates.InZone(now, zone).DateTime.AddDays(-days), zone);

        OrganizerItem Root(OrganizerItem item)
        {
            var current = item;
            var seen = new HashSet<string> { item.Id };
            while (true)
            {
                if (current.ParentId is not { } parentId || !items.TryGetValue(parentId, out var parent)) return current;
                if (!seen.Add(parent.Id)) return current;
                current = parent;
            }
        }

        bool KeepsOnItsOwn(OrganizerItem item)
        {
            if (item.Deleted) return true;
            var updated = item.UpdatedAt ?? now;
            switch (item.Kind)
            {
                case OrganizerKinds.Event:
                    return Recurrence.LastEnd(item, zone) is not { } last || last >= cutoff;
                case OrganizerKinds.Reminder:
                    if (!item.Bool("completed")) return true;
                    return (OrganizerJson.Instant(item.String("completedAt")) ?? updated) >= cutoff;
                case OrganizerKinds.Note:
                    return item.Bool("pinned") || updated >= cutoff;
                case OrganizerKinds.Card:
                    // A board is a working surface; only finished cards age out.
                    return !item.Bool("completed") || updated >= cutoff;
                default:
                    return true;
            }
        }

        var keep = new HashSet<string>();
        foreach (var item in items.Values)
        {
            var top = Root(item);
            var kept = top.Id == item.Id ? KeepsOnItsOwn(item) : KeepsOnItsOwn(top) && KeepsOnItsOwn(item);
            if (kept || protectedIds.Contains(item.Id) || protectedIds.Contains(top.Id)) keep.Add(item.Id);
        }
        foreach (var id in keep.ToList())
        {
            var parentId = items.TryGetValue(id, out var item) ? item.ParentId : null;
            var guard = 0;
            while (parentId is not null && guard++ < 64)
            {
                keep.Add(parentId);
                parentId = items.TryGetValue(parentId, out var parent) ? parent.ParentId : null;
            }
        }
        return items.Keys.Where(id => !keep.Contains(id)).ToHashSet();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static OfflinePolicy? FromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<OfflinePolicy>(text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public enum OfflineMode { Off, Light, Full }

public enum OfflineAttachments { None, Thumbnails, Full }

/// <summary>
/// A reminder or event alert this PC should raise itself - local, so it fires with the server
/// down or unreachable.
/// </summary>
public sealed record PlannedAlert(string Id, string ItemId, DateTimeOffset FireAt, string Title, string Body);

public static class AlertPlanner
{
    // All-day things alert relative to this hour on their day, as Apple Calendar and Reminders do.
    public const int AllDayHour = 9;

    public static List<PlannedAlert> Plan(
        IEnumerable<OrganizerItem> items,
        DateTimeOffset now,
        TimeZoneInfo zone,
        int horizonDays = 14,
        int limit = 48,
        Func<DateTimeOffset, string>? formatTime = null)
    {
        formatTime ??= date => date.ToString("t", CultureInfo.CurrentCulture);
        var nowZoned = OrganizerDates.InZone(now, zone);
        var end = nowZoned.AddDays(horizonDays);
        var alerts = new List<PlannedAlert>();

        DateTimeOffset Base(DateTimeOffset date, bool allDay) =>
            allDay ? OrganizerDates.At(date.DateTime.Date.AddHours(AllDayHour), zone) : date;

        static List<int> Minutes(OrganizerItem item) =>
            item.List("alerts").Select(value => value switch
            {
                double d => (int?)d,
                int i => i,
                long l => (int)l,
                _ => null,
            }).OfType<int>().ToList();

        string Id(OrganizerItem item, DateTimeOffset fire, int offset) =>
            string.Create(CultureInfo.InvariantCulture, $"organizer.{item.Id}.{fire.ToUnixTimeSeconds()}.{offset}");

        foreach (var item in items)
        {
            if (item.Deleted) continue;
            switch (item.Kind)
            {
                case OrganizerKinds.Event:
                {
                    var offsets = Minutes(item);
                    if (offsets.Count == 0) continue;
                    var latest = end.AddMinutes(offsets.Max());
                    var allDay = item.Bool("allDay");
                    foreach (var occurrence in Recurrence.Occurrences(item, nowZoned, latest, zone))
                    {
                        foreach (var offset in offsets)
                        {
                            var fire = Base(occurrence.Start, allDay).AddMinutes(-offset);
                            if (fire <= nowZoned || fire > end) continue;
                            var body = allDay ? "All day" : formatTime(occurrence.Start);
                            if (item.String("location") is { } location && !string.IsNullOrWhiteSpace(location)) body += $" · {location}";
                            alerts.Add(new PlannedAlert(Id(item, fire, offset), item.Id, fire,
                                item.Title.Length == 0 ? "Event" : item.Title, body));
                        }
                    }
                    break;
                }
                case OrganizerKinds.Reminder:
                {
                    if (item.Bool("completed")) continue;
                    var allDay = item.Bool("allDay");
                    if (OrganizerDates.Parse(item.String("due"), allDay, zone) is not { } due) continue;
                    // A due date on a reminder is a request to be reminded then.
                    var offsets = Minutes(item);
                    if (offsets.Count == 0) offsets.Add(0);
                    foreach (var offset in offsets)
                    {
                        var fire = Base(due, allDay).AddMinutes(-offset);
                        if (fire <= nowZoned || fire > end) continue;
                        var firstLine = item.String("notes")?.Split('\n').FirstOrDefault();
                        alerts.Add(new PlannedAlert(Id(item, fire, offset), item.Id, fire,
                            item.Title.Length == 0 ? "Reminder" : item.Title,
                            string.IsNullOrWhiteSpace(firstLine) ? "Reminder" : firstLine));
                    }
                    break;
                }
            }
        }
        return alerts.OrderBy(alert => alert.FireAt).Take(limit).ToList();
    }
}
