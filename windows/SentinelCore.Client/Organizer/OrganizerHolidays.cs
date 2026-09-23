using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Client.Organizer;

// Holiday calendars - US federal holidays, Christian, Jewish, Islamic and other religious
// holidays - that people can add to the calendar. The server works the dates out; this is what
// the PC keeps of them. See ORGANIZER_SYNC.md, "Holiday calendars", and OrganizerKit's
// Holidays.swift. None is shown unless the person turns it on.

public sealed record HolidaySet(string Id, string Name, string Description);

public sealed record Holiday(string Id, string SetId, string Name, string Date, int? Days = null, string? Note = null)
{
    [JsonIgnore]
    public int DayCount => Math.Max(1, Days ?? 1);

    /// <summary>Local midnight on its first day, and on the day after its last.</summary>
    public (DateTimeOffset Start, DateTimeOffset End)? Span(TimeZoneInfo zone)
    {
        if (!DateTime.TryParseExact(Date.Length >= 10 ? Date[..10] : Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
        {
            return null;
        }
        return (OrganizerDates.StartOfDay(day, zone), OrganizerDates.StartOfDay(day.AddDays(DayCount), zone));
    }
}

/// <summary>The chosen sets (and whether that choice has reached the server), the list of sets
/// there are, and the days themselves for last year through two years ahead.</summary>
public sealed record HolidayState
{
    public IReadOnlyList<string> Sets { get; init; } = Array.Empty<string>();
    public bool Dirty { get; init; }
    public IReadOnlyList<HolidaySet>? Catalog { get; init; }
    public string? Key { get; init; }
    public IReadOnlyList<Holiday> List { get; init; } = Array.Empty<Holiday>();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static HolidayState? FromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<HolidayState>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One color per holiday calendar, the same in every app.</summary>
public static class HolidayColors
{
    public static string Hex(string setId) => setId switch
    {
        "us-federal" => "#4a9dff",
        "christian" => "#c98bff",
        "orthodox" => "#ff9a52",
        "jewish" => "#4ae8ff",
        "islamic" => "#7fd67f",
        "hindu" => "#ffd84a",
        "chinese" => "#ff4d5e",
        _ => "#93a2ad",
    };
}
