using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Client.Organizer;

// Board automations: "every morning at 7, put 'pick up eggs' in To do", "when a card is moved into
// Done, mark it done". The rules run on the server (OrganizerAutomationEngine), so they happen with
// every device asleep; this is the PC's side of the editor - the rule's shape, how it reads as a
// sentence, the recipes, and the REST calls. The dashboard's organizer-automations.js is the
// reference, and the wording here is a port of it, word for word.
//
// The same builder, opened from Reminders or Calendar, makes rules with no board (IsPlanner): they
// add reminders and events to a space ("just me", "everyone", "admins"), on a schedule or when the
// weather turns, and can be held back by the weather ("only if it hasn't rained an inch this week").
//
// Rules are not Organizer items and never sync: they are plain REST under /api/organizer/automations
// and need the server. Each part of a rule is a loose bag on the server, so every class here keeps
// whatever fields it doesn't know in Extra and sends them back untouched.

public sealed class BoardAutomation
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    // The board the rule looks after. Empty for a reminders-and-calendar rule, which belongs to
    // SpaceId instead: the reminders and events it makes go there.
    public string BoardId { get; set; } = "";
    public string? SpaceId { get; set; }
    // Null only in a draft that has no WHEN block yet; never saved like that.
    public AutomationTrigger? Trigger { get; set; }
    public List<AutomationCondition> Conditions { get; set; } = new();
    public List<AutomationAction> Actions { get; set; } = new();
    public string? TimeZone { get; set; }

    // Kept by the server; sent back as they came and ignored there.
    public string? RunAs { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public bool? LastOk { get; set; }
    public string? LastResult { get; set; }
    public int RunCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsScheduled => Trigger?.Type == AutomationTypes.Schedule;

    [JsonIgnore]
    public bool IsPlanner => string.IsNullOrEmpty(BoardId);

    /// <summary>A schedule or the weather turning: there's no card of its own, and it can be run by hand.</summary>
    [JsonIgnore]
    public bool StartsWithoutCard => AutomationTypes.StartsWithoutCard(Trigger?.Type);

    public BoardAutomation Clone() => BoardAutomationJson.Read<BoardAutomation>(BoardAutomationJson.Write(this))!;
}

public sealed class AutomationTrigger
{
    public string Type { get; set; } = "";
    public string? ColumnId { get; set; }
    public int? MinutesBefore { get; set; }
    public string? Label { get; set; }
    public AutomationSchedule? Schedule { get; set; }
    // weather: the test that starts the rule when it becomes true - a condition of type "weather".
    public AutomationCondition? Weather { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AutomationSchedule
{
    // once | daily | monthly | hourly
    public string Repeat { get; set; } = "daily";
    public string Time { get; set; } = "07:00";
    // 0 = Sunday .. 6 = Saturday; empty = every day. Never null: the server's is a plain list.
    public List<int> Days { get; set; } = new();
    public int? DayOfMonth { get; set; }
    public int? EveryHours { get; set; }
    public string? Date { get; set; }
    public string? TimeZone { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AutomationCondition
{
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? ColumnId { get; set; }
    public List<int>? Days { get; set; }
    // weather: what's measured (see WeatherMetrics), "atLeast" or "atMost", the number, and how many
    // days back (rainPast) or ahead (the forecasts) to look.
    public string? Metric { get; set; }
    public string? Op { get; set; }
    public double? Value { get; set; }
    public int? Window { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AutomationAction
{
    public string Type { get; set; } = "";
    public string? ColumnId { get; set; }
    public string? ToColumnId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Text { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
    public string? Position { get; set; }
    public List<string>? Items { get; set; }
    public List<string>? Labels { get; set; }
    // Days from today; null on setDue clears the due date, and on createCard means no due date.
    public int? InDays { get; set; }
    public string? Time { get; set; }
    public bool SkipIfExists { get; set; }
    public bool OnlyCompleted { get; set; }
    // The server's default is true too, so a block without it means "done".
    public bool Done { get; set; } = true;
    public string? Username { get; set; }
    public List<string>? People { get; set; }
    public bool Everyone { get; set; }
    public List<string>? Addresses { get; set; }
    // createReminder: 0 none .. 3 high. createReminder / createEvent: a heads-up this many minutes
    // before; where, and how long (no time = an all-day event); and whether InDays counts from the
    // day the weather test was about (the night of the freeze) rather than from today.
    public int? Priority { get; set; }
    public int? AlertMinutes { get; set; }
    public string? Location { get; set; }
    public int? DurationMinutes { get; set; }
    public bool FromWeatherDay { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed record AutomationRun(string AutomationId, DateTimeOffset At, bool Ok, string Message);

/// <summary>What GET /api/organizer/automations answers: the board's rules, and whether the
/// notify / email blocks can reach anyone on this server.</summary>
public sealed record BoardAutomationList(IReadOnlyList<BoardAutomation> Automations, string? TimeZone, bool PushReady, bool EmailReady);

public static class AutomationTypes
{
    public const string Schedule = "schedule";
    public const string Weather = "weather";

    public static readonly string[] Triggers = { Schedule, Weather, "cardAdded", "cardMoved", "cardCompleted", "checklistCompleted", "cardDue", "labelAdded" };
    public static readonly string[] Conditions = { "titleContains", "hasLabel", "lacksLabel", "inColumn", "weather", "onDays" };
    public static readonly string[] Actions =
    {
        "createReminder", "createEvent", "createCard", "moveCard", "addLabel", "removeLabel", "setDue", "complete", "addChecklist",
        "resetChecklist", "addComment", "assign", "deleteCard", "notify", "email", "moveAllCards", "deleteAllCards",
    };

    // What a reminders-and-calendar rule can use: it has no cards to watch or act on.
    public static readonly string[] PlannerTriggers = { Schedule, Weather };
    public static readonly string[] PlannerConditions = { "onDays", "weather" };
    public static readonly string[] PlannerActions = { "createReminder", "createEvent", "notify", "email" };

    public static bool StartsWithoutCard(string? type) => type is Schedule or Weather;
}

public static class BoardAutomationJson
{
    // The server speaks camelCase. Nulls are left out rather than sent: every nullable field on the
    // server means the same thing missing as null, and a few of its fields (createdAt) aren't
    // nullable at all, so a null there would be a 400.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Read<T>(JsonElement json) => json.Deserialize<T>(Options);
}

/// <summary>One block in the palette: what it looks like, and whether it needs a card to act on
/// (or anyone to pick) - `needsCard` / `needsPeople` in the dashboard.</summary>
public sealed record AutomationBlock(string Zone, string Type, string Icon, string Label, string? Hint = null, bool NeedsCard = false, bool NeedsPeople = false);

/// <summary>
/// What the wording and the new blocks need to know about the board: every list's name (other
/// boards' too, since a rule can move a card to one), this board's lists in order, and the people
/// there are to pick.
/// </summary>
public sealed class AutomationContext
{
    public IReadOnlyDictionary<string, string> ColumnTitles { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> BoardColumnIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<OrganizerPerson> People { get; init; } = Array.Empty<OrganizerPerson>();
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;
    public string TimeZone { get; init; } = BoardAutomationRules.DeviceTimeZone();

    public string FirstColumnId => BoardColumnIds.Count > 0 ? BoardColumnIds[0] : "";
    public string LastColumnId => BoardColumnIds.Count > 0 ? BoardColumnIds[^1] : "";

    public string ColumnName(string? id) =>
        !string.IsNullOrEmpty(id) && ColumnTitles.TryGetValue(id, out var title)
            ? (title.Length > 0 ? title : "list")
            : "(pick a list)";
}

public sealed record AutomationRecipe(string Name, string Why, BoardAutomation Rule);

/// <summary>What a weather test can look at: the words around the number, its unit, how far the
/// number box steps, whether it looks back (past) or is a reading right now, the windows that make
/// sense, and the test a new block starts with.</summary>
public sealed record WeatherMetric(
    string Key, string Label, string Unit, double Step, bool Past, bool Now, int[] Windows, string Op, double Value, int Window);

/// <summary>The blocks, their wording, and the small rules the editor follows - all a port of the
/// dashboard's builder.</summary>
public static class BoardAutomationRules
{
    public static readonly string[] DayShort = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    public static readonly (string Token, string What)[] BoardTokens =
    {
        ("{date}", "today's date"), ("{weekday}", "day of the week"), ("{month}", "month"), ("{card}", "the card's title"),
        ("{due}", "the card's due date"), ("{board}", "this board's name"),
    };

    public static readonly (string Token, string What)[] PlannerTokens =
    {
        ("{date}", "today's date"), ("{weekday}", "day of the week"), ("{month}", "month"),
    };

    public static readonly (string Token, string What)[] WeatherTokens =
    {
        ("{weather}", "what the weather test measured, like 28°F or 1.2 inches"), ("{weatherDay}", "the day it's about, like tonight or Tuesday night"),
    };

    /// <summary>The words the palette offers: a board's or the reminders builder's, then the weather ones.</summary>
    public static (string Token, string What)[] Tokens(bool planner) => (planner ? PlannerTokens : BoardTokens).Concat(WeatherTokens).ToArray();

    public static readonly WeatherMetric[] Metrics =
    {
        new("rainPast", "rain so far", "inches", 0.1, true, false, new[] { 1, 2, 3, 5, 7, 10, 14 }, "atMost", 1, 7),
        new("rainNext", "rain forecast", "inches", 0.1, false, false, new[] { 1, 2, 3, 5, 7 }, "atLeast", 0.5, 2),
        new("snowNext", "snow forecast", "inches", 0.5, false, false, new[] { 1, 2, 3, 5, 7 }, "atLeast", 2, 3),
        new("lowNext", "lowest temperature", "°F", 1, false, false, new[] { 1, 2, 3, 5, 7 }, "atMost", 32, 7),
        new("highNext", "highest temperature", "°F", 1, false, false, new[] { 1, 2, 3, 5, 7 }, "atLeast", 95, 3),
        new("rainChanceNext", "chance of rain", "%", 5, false, false, new[] { 1, 2, 3, 5, 7 }, "atLeast", 60, 1),
        new("tempNow", "temperature right now", "°F", 1, false, true, new[] { 1 }, "atMost", 32, 1),
    };

    public static WeatherMetric? Metric(string? key) => Metrics.FirstOrDefault(metric => metric.Key == key);

    // Days from today for a reminder or event; from the weather's day, when it counts from that.
    public static readonly (int Days, string Label)[] WeatherDayChoices =
    {
        (-2, "2 days before"), (-1, "the day before"), (0, "on the day"), (1, "the day after"),
    };

    public static readonly (int? Minutes, string Label)[] AlertChoices =
    {
        (null, "no heads-up"), (0, "at the time"), (15, "15 minutes before"), (60, "1 hour before"), (1440, "1 day before"),
    };

    public static readonly (int Minutes, string Label)[] DurationChoices =
    {
        (15, "15 minutes"), (30, "30 minutes"), (60, "1 hour"), (90, "1½ hours"), (120, "2 hours"), (240, "4 hours"),
    };

    public static readonly (int Priority, string Label)[] PriorityChoices = { (0, "none"), (1, "low"), (2, "medium"), (3, "high") };

    public static readonly (int Minutes, string Label)[] DueOffsets =
    {
        (0, "at the time it's due"), (15, "15 minutes before"), (60, "1 hour before"), (180, "3 hours before"),
        (1440, "1 day before"), (2880, "2 days before"), (10080, "1 week before"),
    };

    public static readonly (string Mode, string Label)[] ScheduleModes =
    {
        ("day", "every day"), ("weekdays", "every weekday"), ("weekends", "every weekend day"), ("days", "on certain days"),
        ("monthly", "every month"), ("hourly", "every few hours"), ("once", "just once"),
    };

    public static readonly int[] HourSteps = { 1, 2, 3, 4, 6, 8, 12 };

    public static readonly (int Days, string Label)[] DueChoices =
    {
        (0, "today"), (1, "tomorrow"), (2, "in 2 days"), (3, "in 3 days"), (7, "in a week"), (14, "in 2 weeks"), (30, "in 30 days"),
    };

    public static readonly AutomationBlock[] TriggerBlocks =
    {
        new("trigger", "schedule", "⏰", "On a schedule", "every morning, every Monday, once…"),
        new("trigger", "weather", "🌦", "When the weather turns", "a freeze, a heat wave, a wet day…"),
        new("trigger", "cardAdded", "＋", "When a card is added", "to a list, or anywhere on the board"),
        new("trigger", "cardMoved", "⇢", "When a card is moved", "into a particular list"),
        new("trigger", "cardCompleted", "✓", "When a card is marked done", "ticked off anywhere on the board"),
        new("trigger", "checklistCompleted", "☑", "When a checklist is finished", "every item on a card ticked"),
        new("trigger", "cardDue", "⏳", "When a card is due", "or a while before - a reminder"),
        new("trigger", "labelAdded", "🏷", "When a label is added", "a particular label, or any"),
    };

    public static readonly AutomationBlock[] ConditionBlocks =
    {
        new("conditions", "titleContains", "Aa", "Title contains…", NeedsCard: true),
        new("conditions", "hasLabel", "🏷", "Has a label", NeedsCard: true),
        new("conditions", "lacksLabel", "⊘", "Doesn't have a label", NeedsCard: true),
        new("conditions", "inColumn", "▤", "Is in a list", NeedsCard: true),
        new("conditions", "weather", "🌦", "The weather…", "it hasn't rained, it'll freeze…"),
        new("conditions", "onDays", "📅", "Only on certain days"),
    };

    public static readonly AutomationBlock[] ActionBlocks =
    {
        new("actions", "createReminder", "🔔", "Add a reminder", "to the Reminders list"),
        new("actions", "createEvent", "📆", "Put an event on the calendar", "all day, or at a time"),
        new("actions", "createCard", "＋", "Create a card", "then the steps below act on it"),
        new("actions", "moveCard", "⇢", "Move the card", NeedsCard: true),
        new("actions", "addLabel", "🏷", "Add a label", NeedsCard: true),
        new("actions", "removeLabel", "⊘", "Remove a label", NeedsCard: true),
        new("actions", "setDue", "🕑", "Set the due date", NeedsCard: true),
        new("actions", "complete", "✓", "Mark done / not done", NeedsCard: true),
        new("actions", "addChecklist", "☑", "Add checklist items", NeedsCard: true),
        new("actions", "resetChecklist", "↺", "Untick the checklist", NeedsCard: true),
        new("actions", "addComment", "💬", "Add a comment", NeedsCard: true),
        new("actions", "assign", "👤", "Assign someone", NeedsCard: true, NeedsPeople: true),
        new("actions", "deleteCard", "🗑", "Delete the card", NeedsCard: true),
        new("actions", "notify", "📣", "Send a notification", "to phones - everyone's, or chosen people"),
        new("actions", "email", "✉", "Send an email", "to people here, or any address"),
        new("actions", "moveAllCards", "⇶", "Move every card in a list"),
        new("actions", "deleteAllCards", "🧹", "Clear out a list"),
    };

    public static AutomationBlock? Block(string zone, string? type) => (zone switch
    {
        "trigger" => TriggerBlocks,
        "conditions" => ConditionBlocks,
        _ => ActionBlocks,
    }).FirstOrDefault(block => block.Type == type);

    /// <summary>The blocks the palette offers: all of them on a board, the no-card ones - in the
    /// dashboard's order - for reminders and calendar.</summary>
    public static IReadOnlyList<AutomationBlock> Offered(string zone, bool planner)
    {
        if (!planner)
        {
            return zone switch
            {
                "trigger" => TriggerBlocks,
                "conditions" => ConditionBlocks,
                _ => ActionBlocks,
            };
        }
        var types = zone switch
        {
            "trigger" => AutomationTypes.PlannerTriggers,
            "conditions" => AutomationTypes.PlannerConditions,
            _ => AutomationTypes.PlannerActions,
        };
        return types.Select(type => Block(zone, type)).OfType<AutomationBlock>().ToList();
    }

    /// <summary>The device's time zone as an IANA name - the server runs on Linux, which knows
    /// "America/Chicago" but not "Central Standard Time".</summary>
    public static string DeviceTimeZone()
    {
        var id = TimeZoneInfo.Local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }

    // ---- new blocks ----------------------------------------------------------------------------

    public static AutomationTrigger MakeTrigger(string type, AutomationContext context) => type switch
    {
        AutomationTypes.Schedule => new AutomationTrigger
        {
            Type = type,
            Schedule = new AutomationSchedule { Repeat = "daily", Time = "07:00", Days = new(), TimeZone = context.TimeZone },
        },
        AutomationTypes.Weather => new AutomationTrigger { Type = type, Weather = NewWeatherTest("lowNext") },
        "cardAdded" => new AutomationTrigger { Type = type, ColumnId = "" },
        "cardMoved" => new AutomationTrigger { Type = type, ColumnId = context.LastColumnId },
        "cardDue" => new AutomationTrigger { Type = type, MinutesBefore = 60, ColumnId = "" },
        "labelAdded" => new AutomationTrigger { Type = type, Label = "" },
        _ => new AutomationTrigger { Type = type },
    };

    public static AutomationCondition MakeCondition(string type, AutomationContext context) => type switch
    {
        "inColumn" => new AutomationCondition { Type = type, ColumnId = context.FirstColumnId },
        "onDays" => new AutomationCondition { Type = type, Days = new() { 1, 2, 3, 4, 5 } },
        "weather" => NewWeatherTest("rainPast"),
        _ => new AutomationCondition { Type = type, Text = "" },
    };

    /// <summary>A weather test with the metric's own sensible numbers - 32°F isn't an amount of
    /// rain - which is also what picking another metric resets it to.</summary>
    public static AutomationCondition NewWeatherTest(string metric)
    {
        var m = Metric(metric) ?? Metric("lowNext")!;
        return new AutomationCondition { Type = "weather", Metric = metric, Op = m.Op, Value = m.Value, Window = m.Window };
    }

    /// <summary>Picking another metric in a weather block.</summary>
    public static void ChangeMetric(AutomationCondition test, string metric)
    {
        var fresh = NewWeatherTest(metric);
        test.Metric = fresh.Metric;
        test.Op = fresh.Op;
        test.Value = fresh.Value;
        test.Window = fresh.Window;
    }

    /// <summary>Whether the rule has a weather test, in WHEN or ONLY IF - which a step can then count its day from.</summary>
    public static bool WeatherInRule(BoardAutomation? draft) =>
        draft is not null && (draft.Trigger?.Type == AutomationTypes.Weather || draft.Conditions.Any(condition => condition.Type == "weather"));

    /// <summary>A new THEN block. <paramref name="draft"/> is the rule it goes into: a reminder in a
    /// weather rule counts from the weather's day to begin with.</summary>
    public static AutomationAction MakeAction(string type, AutomationContext context, BoardAutomation? draft = null) => type switch
    {
        "createReminder" => new AutomationAction
        {
            Type = type, Title = "", InDays = 0, Time = "", SkipIfExists = true, Priority = 0, AlertMinutes = null,
            FromWeatherDay = draft?.Trigger?.Type == AutomationTypes.Weather,
        },
        "createEvent" => new AutomationAction
        {
            Type = type, Title = "", InDays = 0, Time = "", DurationMinutes = 60, SkipIfExists = true, AlertMinutes = null, FromWeatherDay = false,
        },
        "createCard" => new AutomationAction { Type = type, ColumnId = context.FirstColumnId, Title = "", Position = "bottom", SkipIfExists = true },
        "moveCard" => new AutomationAction { Type = type, ColumnId = context.LastColumnId, Position = "top" },
        "addLabel" => new AutomationAction { Type = type, Name = "", Color = "" },
        "removeLabel" => new AutomationAction { Type = type, Name = "" },
        "setDue" => new AutomationAction { Type = type, InDays = 1, Time = "" },
        "complete" => new AutomationAction { Type = type, Done = true },
        "addChecklist" => new AutomationAction { Type = type, Items = new() },
        "addComment" => new AutomationAction { Type = type, Text = "" },
        "assign" => new AutomationAction { Type = type, Username = context.People.Count > 0 ? context.People[0].Username : "" },
        "notify" => new AutomationAction { Type = type, Everyone = context.People.Count == 0, People = new(), Title = "", Text = "" },
        "email" => new AutomationAction { Type = type, People = new(), Addresses = new(), Title = "", Text = "" },
        "moveAllCards" => new AutomationAction { Type = type, ColumnId = context.LastColumnId, ToColumnId = context.FirstColumnId, OnlyCompleted = false },
        "deleteAllCards" => new AutomationAction { Type = type, ColumnId = context.LastColumnId, OnlyCompleted = true },
        _ => new AutomationAction { Type = type },
    };

    // ---- the schedule --------------------------------------------------------------------------

    /// <summary>day | weekdays | weekends | days for a daily schedule, else its repeat.</summary>
    public static string ScheduleMode(AutomationSchedule schedule)
    {
        if (schedule.Repeat != "daily") return schedule.Repeat;
        var days = string.Join(",", schedule.Days.OrderBy(day => day));
        return days switch
        {
            "" => "day",
            "1,2,3,4,5" => "weekdays",
            "0,6" => "weekends",
            _ => "days",
        };
    }

    /// <summary>Picking a mode from the dropdown, as the dashboard does it.</summary>
    public static void ApplyScheduleMode(AutomationSchedule schedule, string mode, DateTime today)
    {
        schedule.Repeat = mode is "monthly" or "hourly" or "once" ? mode : "daily";
        switch (mode)
        {
            case "day":
                schedule.Days = new();
                break;
            case "weekdays":
                schedule.Days = new() { 1, 2, 3, 4, 5 };
                break;
            case "weekends":
                schedule.Days = new() { 0, 6 };
                break;
            case "days":
                if (schedule.Days.Count == 0) schedule.Days = new() { 1 };
                break;
            case "monthly":
                schedule.Days = new();
                if (schedule.DayOfMonth is null or 0) schedule.DayOfMonth = 1;
                break;
            case "hourly":
                schedule.Days = new();
                if (schedule.EveryHours is null or 0) schedule.EveryHours = 3;
                break;
            case "once":
                schedule.Days = new();
                if (string.IsNullOrEmpty(schedule.Date)) schedule.Date = today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
        }
    }

    /// <summary>Ticking a day chip. Unticking the last one would silently mean "every day", so
    /// that's refused (null).</summary>
    public static List<int>? ToggleDay(IReadOnlyList<int>? days, int day)
    {
        var current = days ?? Array.Empty<int>();
        var next = current.Contains(day) ? current.Where(d => d != day).ToList() : current.Append(day).OrderBy(d => d).ToList();
        return next.Count == 0 ? null : next;
    }

    // ---- words ---------------------------------------------------------------------------------

    private static string Q(string? text) => $"“{(string.IsNullOrEmpty(text) ? "…" : text)}”";

    /// <summary>"07:00" as the person reads a time - "7:00 AM" in the US.</summary>
    public static string Time12(string? hhmm, CultureInfo culture)
    {
        var parts = (string.IsNullOrEmpty(hhmm) ? "07:00" : hhmm).Split(':');
        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour);
        var minute = 0;
        if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute);
        var at = new DateTime(2000, 1, 1).AddHours(hour).AddMinutes(minute);
        return Plain(at.ToString(culture.DateTimeFormat.ShortTimePattern.Replace("hh", "h").Replace("HH", "H"), culture));
    }

    // ICU (and so .NET off Windows) puts a narrow no-break space before "AM"; read the same everywhere.
    private static string Plain(string text) => text.Replace('\u202F', ' ').Replace('\u00A0', ' ');

    /// <summary>What someone typed as a time - "14:30", "2:30 PM", "7" - as "HH:mm"; null when it
    /// isn't one.</summary>
    public static string? ParseTime(string? text, CultureInfo culture)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and < 24)
        {
            return $"{hour:00}:00";
        }
        if (DateTime.TryParse(value, culture, DateTimeStyles.NoCurrentDateDefault, out var parsed)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out parsed))
        {
            return parsed.Date == DateTime.MinValue.Date ? parsed.ToString("HH:mm", CultureInfo.InvariantCulture) : null;
        }
        return null;
    }

    public static string DueWords(int? inDays, string? time, CultureInfo culture)
    {
        if (inDays is not { } n) return "no due date";
        var day = n == 0 ? "today" : n == 1 ? "tomorrow" : $"in {n} days";
        return string.IsNullOrEmpty(time) ? day : $"{day} at {Time12(time, culture)}";
    }

    /// <summary>A number as JavaScript prints one: 1 not 1.0, 0.5 as 0.5, whatever the culture.</summary>
    public static string JsNumber(double value) =>
        value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>"the last 7 days", "the next day" - a weather block's window.</summary>
    public static string WindowLabel(WeatherMetric metric, int days) =>
        metric.Past ? (days == 1 ? "today" : $"the last {days} days") : (days == 1 ? "the next day" : $"the next {days} days");

    /// <summary>Where a weather block's number comes from, said under its fields.</summary>
    public static string WeatherSource(WeatherMetric metric) =>
        metric.Past ? "Measured at the nearest airport weather station (Iowa State mesonet daily totals)."
        : metric.Now ? "From the nearest weather station, checked every half hour."
        : "From the National Weather Service forecast for Settings → LOCATION.";

    public const string WeatherTriggerNote =
        "Checked every half hour. It goes off when this becomes true - once, not again until it's been untrue for a while.";

    public static string WeatherWords(AutomationCondition test)
    {
        var m = Metric(test.Metric) ?? Metric("rainPast")!;
        var n = test.Window is { } window and not 0 ? window : 1;
        var span = m.Now ? "" : m.Past ? (n == 1 ? " today" : $" in the last {n} days") : (n == 1 ? " in the next day" : $" in the next {n} days");
        var most = test.Op == "atMost";
        var value = test.Value ?? 0;
        var v = JsNumber(value);
        var inches = $"inch{(value == 1 ? "" : "es")}";
        return test.Metric switch
        {
            "rainPast" => $"{v} {inches} of rain or {(most ? "less" : "more")} has fallen{span}",
            "rainNext" => $"{v} {inches} of rain or {(most ? "less" : "more")} is forecast{span}",
            "snowNext" => $"{v} {inches} of snow or {(most ? "less" : "more")} is forecast{span}",
            "lowNext" => most ? $"it gets down to {v}°F or colder{span}" : $"it stays at {v}°F or warmer{span}",
            "highNext" => most ? $"it stays at {v}°F or cooler{span}" : $"it gets up to {v}°F or hotter{span}",
            "rainChanceNext" => most ? $"the chance of rain stays at {v}% or less{span}" : $"there's a {v}% chance of rain or more{span}",
            "tempNow" => $"it's {v}°F or {(most ? "colder" : "warmer")} right now",
            _ => "the weather…",
        };
    }

    /// <summary>When a new reminder or event is for: "tomorrow at 7:00 AM", or counted from the
    /// weather - "the day before the weather". Nothing for a reminder with no due date.</summary>
    public static string ReminderWhen(AutomationAction action, CultureInfo culture)
    {
        if (action.InDays is not { } n) return "";
        if (action.FromWeatherDay)
        {
            var day = n == 0 ? "on the day of the weather" : n == -1 ? "the day before the weather" : n < 0 ? $"{-n} days before the weather" : $"{n} days after the weather";
            return string.IsNullOrEmpty(action.Time) ? day : $"{day} at {Time12(action.Time, culture)}";
        }
        return DueWords(n, action.Time, culture);
    }

    private static string DayNames(IEnumerable<int>? days) =>
        string.Join(", ", (days ?? Enumerable.Empty<int>()).OrderBy(d => d).Where(d => d is >= 0 and <= 6).Select(d => DayShort[d]));

    public static string ScheduleWords(AutomationSchedule schedule, CultureInfo culture)
    {
        var at = Time12(schedule.Time, culture);
        switch (ScheduleMode(schedule))
        {
            case "day": return $"Every day at {at}";
            case "weekdays": return $"Every weekday at {at}";
            case "weekends": return $"Every Saturday and Sunday at {at}";
            case "days":
                var names = DayNames(schedule.Days);
                return $"Every {(names.Length > 0 ? names : "…")} at {at}";
            case "monthly":
                return $"{(schedule.DayOfMonth is null or 0 or -1 ? "On the last day" : $"On day {schedule.DayOfMonth}")} of every month at {at}";
            case "hourly":
                var every = schedule.EveryHours is null or 0 ? 1 : schedule.EveryHours.Value;
                return $"Every {every} hour{(every == 1 ? "" : "s")} from {at} until midnight";
            case "once":
                var date = DateTime.TryParseExact(schedule.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? day.ToString("ddd, MMM d", culture)
                    : "…";
                return $"Once, on {date} at {at}";
            default:
                return "On a schedule";
        }
    }

    public static string TriggerWords(AutomationTrigger trigger, AutomationContext context)
    {
        switch (trigger.Type)
        {
            case AutomationTypes.Schedule:
                return ScheduleWords(trigger.Schedule ?? new AutomationSchedule(), context.Culture);
            case AutomationTypes.Weather:
                return $"When {WeatherWords(trigger.Weather ?? new AutomationCondition())}";
            case "cardAdded":
                return $"When a card is added to {(string.IsNullOrEmpty(trigger.ColumnId) ? "any list" : context.ColumnName(trigger.ColumnId))}";
            case "cardMoved":
                return $"When a card is moved into {(string.IsNullOrEmpty(trigger.ColumnId) ? "any list" : context.ColumnName(trigger.ColumnId))}";
            case "cardCompleted":
                return "When a card is marked done";
            case "checklistCompleted":
                return "When every checklist item on a card is ticked";
            case "cardDue":
                var minutes = trigger.MinutesBefore ?? 0;
                var offset = DueOffsets.FirstOrDefault(o => o.Minutes == minutes).Label;
                var when = minutes == 0
                    ? "When a card is due"
                    : $"{(offset is not null ? char.ToUpperInvariant(offset[0]) + offset[1..] : $"{minutes} minutes before")} a card is due";
                return when + (string.IsNullOrEmpty(trigger.ColumnId) ? "" : $" in {context.ColumnName(trigger.ColumnId)}");
            case "labelAdded":
                return $"When {(string.IsNullOrEmpty(trigger.Label) ? "any label" : $"the {Q(trigger.Label)} label")} is added to a card";
            default:
                return "When…";
        }
    }

    public static string ConditionWords(AutomationCondition condition, AutomationContext context)
    {
        switch (condition.Type)
        {
            case "titleContains": return $"the title contains {Q(condition.Text)}";
            case "hasLabel": return $"it has the {Q(condition.Text)} label";
            case "lacksLabel": return $"it doesn't have the {Q(condition.Text)} label";
            case "inColumn": return $"it's in {context.ColumnName(condition.ColumnId)}";
            case "onDays":
                var names = DayNames(condition.Days);
                return $"it's {(names.Length > 0 ? names : "…")}";
            case "weather": return WeatherWords(condition);
            default: return condition.Type;
        }
    }

    /// <summary>"Morgan", "Morgan and Pat", "Morgan, Pat and Dylan"; "…" (or nothing, quietly) for nobody.</summary>
    public static string WhoWords(IEnumerable<string>? usernames, AutomationContext context, bool quiet = false)
    {
        var names = (usernames ?? Enumerable.Empty<string>())
            .Select(username => context.People.FirstOrDefault(person => person.Username == username)?.Label ?? username)
            .ToList();
        if (names.Count == 0) return quiet ? "" : "…";
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    public static string ActionWords(AutomationAction action, AutomationContext context)
    {
        var culture = context.Culture;
        switch (action.Type)
        {
            case "createReminder":
                return $"add the reminder {Q(action.Title)}"
                       + (action.InDays is not null ? $", due {ReminderWhen(action, culture)}" : "")
                       + (action.SkipIfExists ? " (unless it's still there)" : "");
            case "createEvent":
                var on = new AutomationAction { InDays = action.InDays ?? 0, Time = action.Time, FromWeatherDay = action.FromWeatherDay };
                return $"put {Q(action.Title)} on the calendar {(string.IsNullOrEmpty(action.Time) ? "all day " : "")}{ReminderWhen(on, culture)}";
            case "createCard":
                return $"create a card {Q(action.Title)} in {context.ColumnName(action.ColumnId)}"
                       + (action.InDays is not null ? $", due {DueWords(action.InDays, action.Time, culture)}" : "")
                       + (action.SkipIfExists ? " (unless one is still there)" : "");
            case "moveCard":
                return $"move it to the {(action.Position == "bottom" ? "bottom" : "top")} of {context.ColumnName(action.ColumnId)}";
            case "addLabel": return $"label it {Q(action.Name)}";
            case "removeLabel": return $"take off the {Q(action.Name)} label";
            case "setDue": return action.InDays is null ? "clear its due date" : $"make it due {DueWords(action.InDays, action.Time, culture)}";
            case "complete": return action.Done ? "mark it done" : "mark it not done";
            case "addChecklist":
                var count = action.Items?.Count ?? 0;
                return $"add {(count == 0 ? "some" : count.ToString(CultureInfo.InvariantCulture))} checklist item{(count == 1 ? "" : "s")}";
            case "resetChecklist": return "untick its checklist";
            case "addComment": return $"comment {Q(action.Text)}";
            case "assign": return $"assign it to {(string.IsNullOrEmpty(action.Username) ? "…" : action.Username)}";
            case "deleteCard": return "delete it";
            case "notify":
                return $"notify {(action.Everyone ? "everyone" : WhoWords(action.People, context))} {Q(action.Text)}";
            case "email":
                var to = new[] { WhoWords(action.People, context, quiet: true) }.Concat(action.Addresses ?? new List<string>())
                    .Where(part => !string.IsNullOrEmpty(part)).ToList();
                return $"email {(to.Count > 0 ? string.Join(", ", to) : "…")} {Q(action.Text)}";
            case "moveAllCards":
                return $"move every {(action.OnlyCompleted ? "finished " : "")}card from {context.ColumnName(action.ColumnId)} to {context.ColumnName(action.ToColumnId)}";
            case "deleteAllCards":
                return $"delete every {(action.OnlyCompleted ? "finished " : "")}card in {context.ColumnName(action.ColumnId)}";
            default:
                return action.Type;
        }
    }

    /// <summary>The whole rule as one sentence: "Every day at 7:00 AM, create a card “eggs” in To do."</summary>
    public static string RuleSentence(BoardAutomation rule, AutomationContext context)
    {
        var when = rule.Trigger is { } trigger ? TriggerWords(trigger, context) : "When…";
        var ifs = rule.Conditions.Select(condition => ConditionWords(condition, context)).ToList();
        var thens = rule.Actions.Select(action => ActionWords(action, context)).ToList();
        return $"{when}{(ifs.Count > 0 ? $", if {string.Join(" and ", ifs)}" : "")}, {(thens.Count > 0 ? string.Join(", then ", thens) : "do…")}.";
    }

    /// <summary>The editor's "In plain words" line, which has a prompt of its own while empty.</summary>
    public static string DraftSentence(BoardAutomation draft, AutomationContext context) =>
        draft.Trigger is not null || draft.Actions.Count > 0
            ? RuleSentence(draft, context)
            : "drag a WHEN block and a THEN block into the rule to get started.";

    /// <summary>"Tue Sep 29, 7:00 AM" - a run time as the list and the preview show it.</summary>
    public static string FormatWhen(DateTimeOffset moment, TimeZoneInfo zone, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTime(moment, zone);
        return Plain($"{local.ToString("ddd, MMM d", culture)}, {local.ToString("t", culture)}");
    }

    // ---- checks --------------------------------------------------------------------------------

    /// <summary>Whether the rule has a card on hand at action <paramref name="index"/>: a card rule
    /// starts with one, a schedule or weather rule only once a "create a card" step has run - and a "delete the
    /// card" step takes it away again.</summary>
    public static bool CardAvailableBefore(BoardAutomation draft, int index)
    {
        var before = draft.Actions.Take(index).Select(action => action.Type).ToList();
        var lastCreate = before.LastIndexOf("createCard");
        var lastDelete = before.LastIndexOf("deleteCard");
        if (draft.StartsWithoutCard) return lastCreate > lastDelete;
        return lastDelete == -1 || lastCreate > lastDelete;
    }

    /// <summary>The warning shown on action <paramref name="index"/>, when it would have no card.</summary>
    public static string? ActionWarning(BoardAutomation draft, int index)
    {
        var action = draft.Actions[index];
        if (Block("actions", action.Type) is not { NeedsCard: true } || CardAvailableBefore(draft, index)) return null;
        return draft.StartsWithoutCard
            ? "A schedule or weather rule has no card of its own - put “Create a card” above this step."
            : "The card was deleted by an earlier step.";
    }

    public static string? ConditionWarning(BoardAutomation draft, int index) =>
        draft.StartsWithoutCard && Block("conditions", draft.Conditions[index].Type) is { NeedsCard: true }
            ? "A schedule or weather rule has no card to check - this would never be true."
            : null;

    /// <summary>What the editor checks before sending; the server checks the rest.</summary>
    public static List<string> CheckBeforeSave(BoardAutomation draft)
    {
        var errors = new List<string>();
        if (draft.Trigger is null) errors.Add("Drag a WHEN block in - what starts the rule?");
        if (draft.IsPlanner && string.IsNullOrEmpty(draft.SpaceId)) errors.Add("Pick who the reminders and events are for.");
        if (draft.Actions.Count == 0) errors.Add("Drag at least one THEN block in - what should happen?");
        if (string.IsNullOrWhiteSpace(draft.Name)) errors.Add("Give the rule a name.");
        return errors;
    }

    /// <summary>Moves action <paramref name="from"/> to insertion point <paramref name="to"/>, which
    /// counts positions in the list as it was, before the action came out.</summary>
    public static void MoveAction(List<AutomationAction> actions, int from, int to)
    {
        if (from < 0 || from >= actions.Count) return;
        var moved = actions[from];
        actions.RemoveAt(from);
        var at = Math.Clamp(to > from ? to - 1 : to, 0, actions.Count);
        actions.Insert(at, moved);
    }

    // ---- text fields ---------------------------------------------------------------------------

    public static List<string> Csv(string text) =>
        text.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();

    public static List<string> Lines(string text) =>
        text.Split('\n').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();

    /// <summary>A {token} put into a text field at the cursor, replacing any selection. Returns the
    /// new text and where the cursor goes.</summary>
    public static (string Text, int Caret) InsertToken(string text, int selectionStart, int selectionLength, string token)
    {
        var start = Math.Clamp(selectionStart, 0, text.Length);
        var end = Math.Clamp(start + Math.Max(0, selectionLength), start, text.Length);
        return (text[..start] + token + text[end..], start + token.Length);
    }

    // ---- who a reminders rule is for --------------------------------------------------------------

    public const string AdminsSpaceId = "group:admins";

    /// <summary>The spaces a reminders rule can be for; just Family when the server has sent none.</summary>
    public static IReadOnlyList<OrganizerSpace> PlannerSpaces(IReadOnlyList<OrganizerSpace>? spaces) =>
        spaces is { Count: > 0 } ? spaces : new[] { OrganizerSpace.Family };

    /// <summary>Who a reminders rule is for, as the builder says it: "just me", "everyone", "the admins".</summary>
    public static string SpaceWords(OrganizerSpace? space) => space switch
    {
        null => "…",
        { Kind: OrganizerSpaceKind.Personal } => "just me",
        { Kind: OrganizerSpaceKind.Family } => "everyone",
        { Id: AdminsSpaceId } => "the admins",
        _ => space.Name,
    };

    /// <summary>The same choice as the Reminders screen says it: "just me", "everyone", "admins".
    /// Everyone and Admins reminders also show across the top of the wall kiosk when they're due.</summary>
    public static string AudienceName(OrganizerSpace space) => space switch
    {
        { Kind: OrganizerSpaceKind.Personal } => "just me",
        { Kind: OrganizerSpaceKind.Family } => "everyone",
        { Id: AdminsSpaceId } => "admins",
        _ => space.Name,
    };

    /// <summary>
    /// The rule the editor works on: a copy of the one being edited, or of a recipe, or a blank one -
    /// going back leaves the original as it was, and fields this app doesn't know ride along. A
    /// board's rule has no space; a reminders rule (<paramref name="boardId"/> null) keeps its space
    /// when it's one this person can see, else it goes to everyone.
    /// </summary>
    public static BoardAutomation Draft(BoardAutomation? editing, BoardAutomation? recipe, string? boardId, IReadOnlyList<OrganizerSpace>? spaces)
    {
        var source = editing ?? recipe;
        var draft = source?.Clone() ?? new BoardAutomation();
        draft.BoardId = boardId ?? "";
        draft.SpaceId = boardId is null
            ? (source?.SpaceId is { Length: > 0 } id && PlannerSpaces(spaces).Any(space => space.Id == id) ? id : OrganizerSpace.Family.Id)
            : null;
        if (editing is null)
        {
            draft.Id = null;
            draft.Enabled = true;
        }
        return draft;
    }

    // ---- recipes -------------------------------------------------------------------------------

    public static List<AutomationRecipe> Recipes(string boardId, AutomationContext context)
    {
        var first = context.FirstColumnId;
        var last = context.LastColumnId;
        var tz = context.TimeZone;
        AutomationSchedule Daily(string time, params int[] days) => new() { Repeat = "daily", Time = time, Days = days.ToList(), TimeZone = tz };
        BoardAutomation Rule(string name, AutomationTrigger trigger, params AutomationAction[] actions) =>
            new() { Name = name, BoardId = boardId, Trigger = trigger, Actions = actions.ToList() };

        return new List<AutomationRecipe>
        {
            new("Daily to-do", "a card every morning", Rule("Pick up eggs every morning",
                new AutomationTrigger { Type = AutomationTypes.Schedule, Schedule = Daily("07:00") },
                new AutomationAction { Type = "createCard", ColumnId = first, Title = "pick up eggs", Position = "bottom", SkipIfExists = true })),
            new("Weekly chore", "every Sunday, with a checklist", Rule("Sunday chores",
                new AutomationTrigger { Type = AutomationTypes.Schedule, Schedule = Daily("09:00", 0) },
                new AutomationAction
                {
                    Type = "createCard", ColumnId = first, Title = "Chores for {date}", Position = "bottom", SkipIfExists = true,
                    Items = new() { "take out the trash", "water the plants", "vacuum" },
                })),
            new("Done means done", "moving to the last list ticks it off", Rule($"Moved to {context.ColumnName(last)} → done",
                new AutomationTrigger { Type = "cardMoved", ColumnId = last },
                new AutomationAction { Type = "complete", Done = true })),
            new("Tidy up weekly", "clear finished cards each Monday", Rule($"Clear {context.ColumnName(last)} on Mondays",
                new AutomationTrigger { Type = AutomationTypes.Schedule, Schedule = Daily("06:00", 1) },
                new AutomationAction { Type = "deleteAllCards", ColumnId = last, OnlyCompleted = true })),
            new("Due tomorrow", "new cards get a due date", Rule("New cards are due tomorrow",
                new AutomationTrigger { Type = "cardAdded", ColumnId = first },
                new AutomationAction { Type = "setDue", InDays = 1, Time = "" })),
            new("Due reminder", "a notification an hour before a card is due", Rule("Remind me before cards are due",
                new AutomationTrigger { Type = "cardDue", MinutesBefore = 60, ColumnId = "" },
                new AutomationAction { Type = "notify", Everyone = true, People = new(), Title = "", Text = "{card} is due {due}" })),
            new("Bin night", "a notification every Sunday evening", Rule("Bins out on Sunday",
                new AutomationTrigger { Type = AutomationTypes.Schedule, Schedule = Daily("18:00", 0) },
                new AutomationAction { Type = "notify", Everyone = true, People = new(), Title = "", Text = "Take the bins out tonight" })),
            new("Urgent to the top", "the urgent label jumps the queue", Rule("Urgent cards to the top",
                new AutomationTrigger { Type = "labelAdded", Label = "urgent" },
                new AutomationAction { Type = "moveCard", ColumnId = first, Position = "top" })),
        };
    }

    /// <summary>The reminders-and-calendar recipes. The freeze one goes to the admins when this
    /// server has that space, else to everyone.</summary>
    public static List<AutomationRecipe> PlannerRecipes(IReadOnlyList<OrganizerSpace>? spaces, string timeZone)
    {
        const string everyone = "family";
        var admins = PlannerSpaces(spaces).Any(space => space.Id == AdminsSpaceId) ? AdminsSpaceId : everyone;
        AutomationTrigger On(string time, params int[] days) =>
            new() { Type = AutomationTypes.Schedule, Schedule = new AutomationSchedule { Repeat = "daily", Time = time, Days = days.ToList(), TimeZone = timeZone } };
        AutomationCondition Weather(string metric, string op, double value, int window) =>
            new() { Type = "weather", Metric = metric, Op = op, Value = value, Window = window };
        AutomationTrigger When(AutomationCondition test) => new() { Type = AutomationTypes.Weather, Weather = test };
        BoardAutomation Rule(string name, string spaceId, AutomationTrigger trigger, AutomationCondition[] conditions, params AutomationAction[] actions) =>
            new() { Name = name, BoardId = "", SpaceId = spaceId, Trigger = trigger, Conditions = conditions.ToList(), Actions = actions.ToList() };
        AutomationAction Reminder(string title, int inDays, int priority, bool fromWeatherDay = false, string time = "") =>
            new() { Type = "createReminder", Title = title, InDays = inDays, FromWeatherDay = fromWeatherDay, Time = time, SkipIfExists = true, Priority = priority };

        return new List<AutomationRecipe>
        {
            new("Water the plants", "Saturdays - unless it's rained an inch this week", Rule("Water the plants (if it's been dry)", everyone,
                On("08:00", 6), new[] { Weather("rainPast", "atMost", 1, 7) },
                Reminder("Water the plants", 0, 0))),
            new("Freeze coming", "check the pipes the day before it freezes", Rule("Freeze coming - check the pipes", admins,
                When(Weather("lowNext", "atMost", 32, 7)), Array.Empty<AutomationCondition>(),
                Reminder("Check the pipes - {weather} {weatherDay}", -1, 3, fromWeatherDay: true),
                new AutomationAction { Type = "notify", Everyone = true, People = new(), Title = "Freeze coming", Text = "Down to {weather} {weatherDay} - drip the taps and cover the plants." })),
            new("Rain tomorrow", "bring the cushions in", Rule("Rain on the way", everyone,
                When(Weather("rainChanceNext", "atLeast", 70, 1)), Array.Empty<AutomationCondition>(),
                Reminder("Bring the patio cushions in", 0, 1))),
            new("Heat wave", "extra watering when it's 95° or hotter", Rule("Hot spell - water the garden", everyone,
                When(Weather("highNext", "atLeast", 95, 3)), Array.Empty<AutomationCondition>(),
                Reminder("Hot {weatherDay} ({weather}) - water the garden early", 0, 2, fromWeatherDay: true, time: "07:00"))),
            new("Snow coming", "get the salt and shovel out", Rule("Snow on the way", everyone,
                When(Weather("snowNext", "atLeast", 2, 3)), Array.Empty<AutomationCondition>(),
                Reminder("Salt the steps - {weather} of snow {weatherDay}", -1, 2, fromWeatherDay: true))),
            new("Bin day", "on the calendar every week", Rule("Bin day", everyone,
                On("06:00", 0), Array.Empty<AutomationCondition>(),
                new AutomationAction { Type = "createEvent", Title = "Bin day", InDays = 1, Time = "", SkipIfExists = true })),
        };
    }
}

/// <summary>Raised for any answer from the automations API that isn't a success; carries the
/// server's list of problems ("Give the rule a name.") for the editor to show.</summary>
public sealed class BoardAutomationException(IReadOnlyList<string> errors)
    : OrganizerException(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// /api/organizer/automations. Online only: <paramref name="send"/> is the engine's way to the
/// server, which throws <see cref="OrganizerOfflineException"/> when there's no connection and
/// otherwise hands back whatever the server answered.
/// </summary>
public sealed class BoardAutomationsApi(
    Func<string, string, IReadOnlyDictionary<string, string>?, byte[]?, Task<OrganizerResponse>> send)
{
    private const string Root = "/api/organizer/automations";

    public Task<BoardAutomationList> ListAsync(string boardId) =>
        ListWhereAsync(new Dictionary<string, string> { ["boardId"] = boardId });

    /// <summary>The reminders-and-calendar rules: the ones with no board, in spaces this person is in.</summary>
    public Task<BoardAutomationList> ListPlannerAsync() =>
        ListWhereAsync(new Dictionary<string, string> { ["planner"] = "true" });

    private async Task<BoardAutomationList> ListWhereAsync(IReadOnlyDictionary<string, string> query)
    {
        using var json = JsonDocument.Parse(await CallAsync("GET", Root, query).ConfigureAwait(false));
        var root = json.RootElement;
        var rules = root.TryGetProperty("automations", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(BoardAutomationJson.Read<BoardAutomation>).OfType<BoardAutomation>().ToList()
            : new List<BoardAutomation>();
        return new BoardAutomationList(
            rules,
            root.TryGetProperty("timeZone", out var zone) && zone.ValueKind == JsonValueKind.String ? zone.GetString() : null,
            // Only a server that says "no" is taken at its word; one that doesn't say is assumed fine.
            !(root.TryGetProperty("pushReady", out var push) && push.ValueKind == JsonValueKind.False),
            !(root.TryGetProperty("emailReady", out var email) && email.ValueKind == JsonValueKind.False));
    }

    /// <summary>Creates the rule, or replaces it when it has an id; stamped with the device's time
    /// zone either way, as the dashboard does. Returns the rule as the server saved it.</summary>
    public async Task<BoardAutomation> SaveAsync(BoardAutomation rule, string timeZone)
    {
        var body = rule.Clone();
        body.Id = null;
        body.TimeZone = timeZone;
        var reply = rule.Id is { Length: > 0 } id
            ? await CallAsync("PUT", $"{Root}/{Uri.EscapeDataString(id)}", null, body).ConfigureAwait(false)
            : await CallAsync("POST", Root, null, body).ConfigureAwait(false);
        return BoardAutomationJson.Read<BoardAutomation>(Encoding.UTF8.GetString(reply)) ?? body;
    }

    /// <summary>The on/off switch: the whole rule goes back with only <c>enabled</c> changed.</summary>
    public async Task<BoardAutomation> SetEnabledAsync(BoardAutomation rule, bool enabled)
    {
        var body = rule.Clone();
        body.Enabled = enabled;
        var reply = await CallAsync("PUT", $"{Root}/{Uri.EscapeDataString(rule.Id ?? "")}", null, body).ConfigureAwait(false);
        return BoardAutomationJson.Read<BoardAutomation>(Encoding.UTF8.GetString(reply)) ?? body;
    }

    public Task DeleteAsync(string id) => CallAsync("DELETE", $"{Root}/{Uri.EscapeDataString(id)}");

    /// <summary>Runs a scheduled or weather rule now, to try it out.</summary>
    public async Task<AutomationRun> RunNowAsync(string id)
    {
        var reply = await CallAsync("POST", $"{Root}/{Uri.EscapeDataString(id)}/run").ConfigureAwait(false);
        return BoardAutomationJson.Read<AutomationRun>(Encoding.UTF8.GetString(reply)) ?? new AutomationRun(id, DateTimeOffset.UtcNow, true, "Done.");
    }

    public async Task<List<AutomationRun>> RunsAsync(string id)
    {
        using var json = JsonDocument.Parse(await CallAsync("GET", $"{Root}/{Uri.EscapeDataString(id)}/runs").ConfigureAwait(false));
        return json.RootElement.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
            ? runs.EnumerateArray().Select(BoardAutomationJson.Read<AutomationRun>).OfType<AutomationRun>().ToList()
            : new List<AutomationRun>();
    }

    /// <summary>The next few times a schedule would run, worked out by the server so they're what
    /// will really happen.</summary>
    public async Task<List<DateTimeOffset>> PreviewAsync(AutomationSchedule schedule)
    {
        using var json = JsonDocument.Parse(await CallAsync("POST", $"{Root}/preview", null, schedule).ConfigureAwait(false));
        return json.RootElement.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
            ? runs.EnumerateArray().Select(run => run.GetDateTimeOffset()).ToList()
            : new List<DateTimeOffset>();
    }

    private async Task<byte[]> CallAsync(string method, string path, IReadOnlyDictionary<string, string>? query = null, object? body = null)
    {
        var bytes = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), BoardAutomationJson.Options);
        var response = await send(method, path, query, bytes).ConfigureAwait(false);
        if (response.Status is >= 200 and < 300) return response.Body.Length == 0 ? "{}"u8.ToArray() : response.Body;
        if (response.Status == 401) throw new OrganizerSignInRequiredException();
        throw new BoardAutomationException(Errors(response));
    }

    internal static List<string> Errors(OrganizerResponse response)
    {
        try
        {
            using var json = JsonDocument.Parse(response.Body);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    var list = errors.EnumerateArray().Select(e => e.ToString()).Where(e => e.Length > 0).ToList();
                    if (list.Count > 0) return list;
                }
                if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    && error.GetString() is { Length: > 0 } message)
                {
                    return new List<string> { message };
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON - a proxy's error page, say.
        }
        return new List<string> { $"The server said {response.Status}." };
    }
}
