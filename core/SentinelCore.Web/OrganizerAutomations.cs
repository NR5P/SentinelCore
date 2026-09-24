using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// Board automations: "every morning at 7, put 'pick up eggs' in New items", "when a card is moved
// into Done, mark it complete". Built in the dashboard with drag-and-drop blocks, no code; run
// here, on the server, so a scheduled card appears on every device at 7 even when nobody has an
// app open. What the engine does with these lives in OrganizerAutomationEngine.
//
// A rule is WHEN (one trigger) + ONLY IF (any number of conditions, all must hold) + THEN (a list
// of actions, run in order). Every part is a small loose bag of properties rather than a class
// per block type, so the editor can round-trip a block it doesn't fully understand yet.
//
// They are not Organizer items and never sync to devices: they are server behaviour, not data a
// phone needs offline. The cards they make are ordinary cards and sync like any other.
public sealed class OrganizerAutomation
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string BoardId { get; set; } = "";
    public BoardAutomationTrigger Trigger { get; set; } = new();
    public List<BoardAutomationCondition> Conditions { get; set; } = [];
    public List<BoardAutomationAction> Actions { get; set; } = [];

    // IANA zone for everything local about the rule: {date}, due dates it sets, and when an
    // all-day card counts as due. The dashboard sends the browser's; empty means the server's.
    // A schedule carries its own (older rules only have that one).
    public string? TimeZone { get; set; }

    // Changes are made as the last person to save the rule, so a rule can never reach anything
    // that person couldn't reach by hand. Null when login is off.
    public string? RunAs { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Kept by the engine, ignored when a client sends them.
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public bool? LastOk { get; set; }
    public string? LastResult { get; set; }
    public int RunCount { get; set; }
}

public sealed class BoardAutomationTrigger
{
    // schedule | cardAdded | cardMoved | cardCompleted | checklistCompleted | labelAdded | cardDue
    public string Type { get; set; } = OrganizerAutomationTypes.Schedule;
    // cardAdded/cardMoved: only cards landing in this column. cardDue: only cards in it.
    // Null = any column on the board.
    public string? ColumnId { get; set; }
    // cardDue: how long before the due time to fire; 0 = at it. A card due on a date with no
    // time counts as due at 9:00 that morning.
    public int? MinutesBefore { get; set; }
    // labelAdded: only this label (null = any).
    public string? Label { get; set; }
    public BoardAutomationSchedule? Schedule { get; set; }
}

public sealed class BoardAutomationSchedule
{
    // once | daily | monthly | hourly
    public string Repeat { get; set; } = "daily";
    // "HH:mm", local to TimeZone. For hourly, the first run of the day.
    public string Time { get; set; } = "07:00";
    // 0 = Sunday .. 6 = Saturday. Empty = every day. Used by daily and hourly.
    public List<int> Days { get; set; } = [];
    // monthly: 1-31, or -1 for the last day. A month too short for the day uses its last day.
    public int? DayOfMonth { get; set; }
    // hourly: every N hours from Time until midnight.
    public int? EveryHours { get; set; }
    // once: "yyyy-MM-dd".
    public string? Date { get; set; }
    // IANA name. The dashboard sends the browser's; empty means the server's own.
    public string? TimeZone { get; set; }
}

public sealed class BoardAutomationCondition
{
    // titleContains | hasLabel | lacksLabel | inColumn | onDays
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? ColumnId { get; set; }
    public List<int>? Days { get; set; }
}

public sealed class BoardAutomationAction
{
    // Card-making: createCard. Acting on "the card" (the one that fired the rule, or the one the
    // last createCard made): moveCard, addLabel, removeLabel, setDue, complete, addChecklist,
    // resetChecklist, addComment, assign, deleteCard. Board-wide: moveAllCards, deleteAllCards.
    public string Type { get; set; } = "";
    public string? ColumnId { get; set; }
    public string? ToColumnId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Text { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
    // "top" | "bottom"
    public string? Position { get; set; }
    public List<string>? Items { get; set; }
    public List<string>? Labels { get; set; }
    // setDue / createCard: days from today (0 = today); null on setDue clears the due date.
    public int? InDays { get; set; }
    public string? Time { get; set; }
    public bool SkipIfExists { get; set; }
    public bool OnlyCompleted { get; set; }
    public bool Done { get; set; } = true;
    public string? Username { get; set; }
    // notify / email: who to send to, by username. notify may instead go to everyone's phones.
    public List<string>? People { get; set; }
    public bool Everyone { get; set; }
    // email: addresses that aren't anyone's account.
    public List<string>? Addresses { get; set; }
}

public sealed record OrganizerAutomationRun(string AutomationId, DateTimeOffset At, bool Ok, string Message);

public static class OrganizerAutomationTypes
{
    public const string Schedule = "schedule";
    public const string CardAdded = "cardAdded";
    public const string CardMoved = "cardMoved";
    public const string CardCompleted = "cardCompleted";
    public const string ChecklistCompleted = "checklistCompleted";
    public const string LabelAdded = "labelAdded";
    public const string CardDue = "cardDue";

    public static readonly string[] Triggers = [Schedule, CardAdded, CardMoved, CardCompleted, ChecklistCompleted, LabelAdded, CardDue];
    public static readonly string[] Conditions = ["titleContains", "hasLabel", "lacksLabel", "inColumn", "onDays"];
    // Send steps work with or without a card: "every Sunday, text everyone 'bins out'" has none.
    public static readonly string[] BoardActions = ["createCard", "moveAllCards", "deleteAllCards", "notify", "email"];
    public static readonly string[] SendActions = ["notify", "email"];
    public static readonly string[] CardActions =
        ["moveCard", "addLabel", "removeLabel", "setDue", "complete", "addChecklist", "resetChecklist", "addComment", "assign", "deleteCard"];
    public static readonly string[] Repeats = ["once", "daily", "monthly", "hourly"];

    public static bool IsCardAction(string type) => CardActions.Contains(type);
}

// When a schedule next fires. Worked out in the schedule's own time zone, so "7:00" stays 7:00
// across daylight saving; a time that doesn't exist that day (the spring-forward hour) runs an
// hour later, and one that happens twice runs the first time.
public static class OrganizerAutomationSchedule
{
    public static TimeZoneInfo ZoneOf(BoardAutomationSchedule schedule) => ZoneNamed(schedule.TimeZone);

    public static TimeZoneInfo ZoneOf(OrganizerAutomation rule) =>
        ZoneNamed(rule.Trigger.Schedule?.TimeZone is { Length: > 0 } scheduleZone ? scheduleZone : rule.TimeZone);

    public static TimeZoneInfo ZoneNamed(string? name) =>
        !string.IsNullOrWhiteSpace(name) && TimeZoneInfo.TryFindSystemTimeZoneById(name, out var zone) ? zone : TimeZoneInfo.Local;

    // When an all-day card counts as due: the morning of its day, not midnight.
    public static readonly TimeSpan AllDayDueTime = TimeSpan.FromHours(9);

    // A card's `due` as an instant: "2026-09-25T17:00:00-05:00", or "2026-09-25" meaning 9:00 that
    // day in the rule's zone. Null for anything else.
    public static DateTimeOffset? DueInstant(string? due, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(due)) return null;
        if (due.Length == 10)
        {
            return DateOnly.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? ToUtc(date, AllDayDueTime, zone)
                : null;
        }
        return DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToUniversalTime() : null;
    }

    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text ?? "", ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out time)
        && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    public static DateTimeOffset? Next(BoardAutomationSchedule schedule, DateTimeOffset after)
    {
        if (!TryParseTime(schedule.Time, out var time)) return null;
        var zone = ZoneOf(schedule);
        var startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);

        if (schedule.Repeat == "once")
        {
            if (!DateOnly.TryParseExact(schedule.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
            var at = ToUtc(date, time, zone);
            return at > after ? at : null;
        }

        // A day at a time from today. 400 days covers "the 31st" and "Feb 29 on a Monday"-ish
        // gaps; nothing sensible needs longer.
        for (var offset = 0; offset < 400; offset++)
        {
            var date = startDate.AddDays(offset);
            foreach (var candidate in TimesOn(schedule, date, time))
            {
                var at = ToUtc(date, candidate, zone);
                if (at > after) return at;
            }
        }
        return null;
    }

    private static IEnumerable<TimeSpan> TimesOn(BoardAutomationSchedule schedule, DateOnly date, TimeSpan time)
    {
        switch (schedule.Repeat)
        {
            case "daily":
                if (DayAllowed(schedule, date)) yield return time;
                break;
            case "hourly":
                if (!DayAllowed(schedule, date)) yield break;
                var every = TimeSpan.FromHours(Math.Clamp(schedule.EveryHours ?? 1, 1, 24));
                for (var t = time; t < TimeSpan.FromDays(1); t += every) yield return t;
                break;
            case "monthly":
                var lastDay = DateTime.DaysInMonth(date.Year, date.Month);
                var wanted = schedule.DayOfMonth is null or -1 ? lastDay : Math.Min(schedule.DayOfMonth.Value, lastDay);
                if (date.Day == wanted) yield return time;
                break;
        }
    }

    private static bool DayAllowed(BoardAutomationSchedule schedule, DateOnly date) =>
        schedule.Days.Count == 0 || schedule.Days.Contains((int)date.DayOfWeek);

    public static DateTimeOffset ToUtc(DateOnly date, TimeSpan time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.FromTimeSpan(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

public interface IOrganizerAutomationStore
{
    Task<IReadOnlyList<OrganizerAutomation>> GetAllAsync(CancellationToken cancellationToken);
    Task<OrganizerAutomation?> GetAsync(string id, CancellationToken cancellationToken);
    Task SaveAsync(OrganizerAutomation automation, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
    Task AddRunAsync(OrganizerAutomationRun run, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrganizerAutomationRun>> GetRunsAsync(string automationId, CancellationToken cancellationToken);

    // "When a card is due" fires once per card per due date: true the first time this rule is
    // told about this card at this due value, false ever after. A new due date fires again.
    Task<bool> TryMarkDueFiredAsync(string automationId, string cardId, string due, CancellationToken cancellationToken);
}

// organizer-automations.db, beside organizer.db. A file of its own so the Organizer's sync store
// stays exactly what the six clients depend on; the backup carries these separately.
public sealed class SqliteOrganizerAutomationStore : IOrganizerAutomationStore, IDisposable
{
    private const int RunsKeptPerAutomation = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteOrganizerAutomationStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "organizer-automations.db")}");
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS Automations (Id TEXT PRIMARY KEY, Definition TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Runs (AutomationId TEXT NOT NULL, At TEXT NOT NULL, Ok INTEGER NOT NULL, Message TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Runs_Automation ON Runs(AutomationId, At);
            CREATE TABLE IF NOT EXISTS DueFired (
                AutomationId TEXT NOT NULL, CardId TEXT NOT NULL, Due TEXT NOT NULL, FiredAt TEXT NOT NULL,
                PRIMARY KEY (AutomationId, CardId, Due));
            """;
        command.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<OrganizerAutomation>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var list = new List<OrganizerAutomation>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Definition FROM Automations;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Read(reader.GetString(0)) is { } automation) list.Add(automation);
            }
            return [.. list.OrderBy(a => a.CreatedAt)];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OrganizerAutomation?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Definition FROM Automations WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() is string json ? Read(json) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(OrganizerAutomation automation, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Automations (Id, Definition) VALUES ($id, $json)
                ON CONFLICT(Id) DO UPDATE SET Definition = excluded.Definition;
                """;
            command.Parameters.AddWithValue("$id", automation.Id);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(automation, Json));
            command.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Runs WHERE AutomationId = $id; DELETE FROM DueFired WHERE AutomationId = $id; DELETE FROM Automations WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddRunAsync(OrganizerAutomationRun run, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO Runs (AutomationId, At, Ok, Message) VALUES ($id, $at, $ok, $message);
                DELETE FROM Runs WHERE AutomationId = $id AND rowid NOT IN (
                    SELECT rowid FROM Runs WHERE AutomationId = $id ORDER BY At DESC LIMIT {RunsKeptPerAutomation});
                """;
            command.Parameters.AddWithValue("$id", run.AutomationId);
            command.Parameters.AddWithValue("$at", run.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$ok", run.Ok ? 1 : 0);
            command.Parameters.AddWithValue("$message", run.Message);
            command.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<OrganizerAutomationRun>> GetRunsAsync(string automationId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var runs = new List<OrganizerAutomationRun>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT At, Ok, Message FROM Runs WHERE AutomationId = $id ORDER BY At DESC;";
            command.Parameters.AddWithValue("$id", automationId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                runs.Add(new OrganizerAutomationRun(
                    automationId,
                    DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    reader.GetInt32(1) == 1,
                    reader.GetString(2)));
            }
            return runs;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> TryMarkDueFiredAsync(string automationId, string cardId, string due, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO DueFired (AutomationId, CardId, Due, FiredAt) VALUES ($id, $card, $due, $at);
                """;
            command.Parameters.AddWithValue("$id", automationId);
            command.Parameters.AddWithValue("$card", cardId);
            command.Parameters.AddWithValue("$due", due);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery() > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static OrganizerAutomation? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OrganizerAutomation>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
