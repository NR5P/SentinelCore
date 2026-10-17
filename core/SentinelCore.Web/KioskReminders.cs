using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

// The household reminders the wall kiosk shows across the top, the way it shows a weather
// warning (kiosk-reminders.js): open reminders for Everyone (the Family space) and for the Admins,
// due today or overdue. Read here rather than from the kiosk's own Organizer copy because the
// kiosk is often signed in as a viewer, who can't see the Admins space - and the wall is where
// "check the pipes tonight" is meant to be seen by whoever walks past.
//
// Ticking one off from the wall goes through here too, applied as a member of that space, exactly
// like an edit from a phone, so it syncs everywhere.
public static class KioskReminders
{
    // What the kiosk is sent: anything due before this far ahead. It decides "today" in its own
    // time zone, so the cut-off here is generous.
    public static readonly TimeSpan Lookahead = TimeSpan.FromHours(36);
    public const string DeviceId = "kiosk";

    public sealed record Reminder(
        string Id, string Title, string? Notes, string Due, bool AllDay, string? Recurrence,
        int Priority, string SpaceId, string Audience);

    public sealed record DoneRequest(string? NextDue);

    public static void MapKioskReminderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/kiosk/reminders", async (IOrganizerStore store, IAccountStore accounts, AppSettingsFile settings,
            TimeProvider time, CancellationToken ct) =>
        {
            if (!(await OrganizerEndpoints.ReadOptionsAsync(settings, ct)).Enabled) return Results.Ok(new { reminders = Array.Empty<Reminder>() });
            var now = time.GetUtcNow();
            var list = new List<Reminder>();
            foreach (var (spaceId, reader) in await ReadersAsync(accounts, ct))
            {
                foreach (var item in await store.GetItemsAsync(reader, OrganizerKinds.Reminder, spaceId, ct))
                {
                    if (Shown(item, now) is { } reminder) list.Add(reminder);
                }
            }
            return Results.Ok(new { reminders = list.OrderBy(r => r.Due, StringComparer.Ordinal) });
        }).RequireAuthorization();

        app.MapPost("/api/kiosk/reminders/{id}/done", async (string id, DoneRequest? request, HttpContext context,
            IOrganizerStore store, IAccountStore accounts, TimeProvider time, OrganizerPushNudger nudger, CancellationToken ct) =>
        {
            foreach (var (spaceId, reader) in await ReadersAsync(accounts, ct))
            {
                var item = (await store.GetItemsAsync(reader, OrganizerKinds.Reminder, spaceId, ct)).FirstOrDefault(r => r.Id == id);
                if (item is null) continue;

                var now = time.GetUtcNow();
                var fields = new JsonObject();
                // A repeating reminder moves on to its next time rather than being finished for
                // good. The kiosk works the next time out (the same RRULE code as the dashboard).
                if (Text(item, "recurrence") is { Length: > 0 } && request?.NextDue is { Length: > 0 } next && IsDue(next))
                {
                    fields["due"] = next;
                    fields["completed"] = false;
                    fields["lastCompletedAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                }
                else
                {
                    fields["completed"] = true;
                    fields["completedAt"] = now.ToString("O", CultureInfo.InvariantCulture);
                }
                var change = new OrganizerChange { Id = item.Id, Kind = item.Kind, Fields = fields };
                var clock = NextClock(item, now);
                foreach (var (name, _) in fields) change.Clocks[name] = clock;

                // As the person tapping when they're in the space (Everyone always is), otherwise
                // as one of its members - a viewer ticking off the admins' reminder on the wall.
                var actor = spaceId == OrganizerSpaceIds.Family ? OrganizerEndpoints.UserOf(context) : reader;
                var result = await store.ApplyChangesAsync(actor, [change], null, ct);
                if (result.Rejected.FirstOrDefault() is { } rejected) return Results.BadRequest(new { errors = new[] { rejected.Error } });
                nudger.Changed();
                return Results.Ok(new { done = true, due = fields["due"]?.GetValue<string>() });
            }
            return Results.NotFound();
        }).RequireAuthorization();
    }

    // Who to read each space as: anyone sees Family; the Admins space needs an admin.
    private static async Task<List<(string SpaceId, OrganizerUser Reader)>> ReadersAsync(IAccountStore accounts, CancellationToken ct)
    {
        var readers = new List<(string, OrganizerUser)> { (OrganizerSpaceIds.Family, OrganizerUser.Shared) };
        var admin = (await accounts.GetAllAsync(ct)).FirstOrDefault(account => account.Role == AccountRole.Admin);
        if (admin is not null) readers.Add((OrganizerSpaceIds.Admins, new OrganizerUser(admin.Username.ToLowerInvariant(), null)));
        return readers;
    }

    public static Reminder? Shown(OrganizerItem item, DateTimeOffset now)
    {
        if (item.Deleted || item.ParentId is not null) return null;
        if (item.Fields["completed"]?.GetValueKind() == JsonValueKind.True) return null;
        if (Text(item, "due") is not { } due || !IsDue(due)) return null;
        var allDay = due.Length == 10;
        // An all-day "2026-10-18" is compared as that date's start in UTC - close enough for a
        // 36-hour window; the kiosk makes the real "is it today" call in its own zone.
        var at = allDay
            ? new DateTimeOffset(DateTime.ParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture), TimeSpan.Zero)
            : DateTimeOffset.Parse(due, CultureInfo.InvariantCulture);
        if (at > now + Lookahead) return null;
        var priority = item.Fields["priority"] is JsonValue value && value.TryGetValue<int>(out var p) ? p : 0;
        return new Reminder(item.Id, Text(item, "title") ?? "reminder", Text(item, "notes"), due, allDay, Text(item, "recurrence"),
            priority, item.SpaceId, item.SpaceId == OrganizerSpaceIds.Admins ? "admins" : "everyone");
    }

    private static bool IsDue(string due) =>
        due.Length == 10
            ? DateOnly.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            : DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string? Text(OrganizerItem item, string name) =>
        item.Fields[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Newer than every clock on the item, so the tap wins over the edit it was looking at.
    private static string NextClock(OrganizerItem item, DateTimeOffset now)
    {
        var ms = now.ToUnixTimeMilliseconds();
        foreach (var clock in item.Clocks.Values)
        {
            if (clock.Length >= 13 && long.TryParse(clock.AsSpan(0, 13), NumberStyles.None, CultureInfo.InvariantCulture, out var seen))
                ms = Math.Max(ms, seen + 1);
        }
        return $"{ms:D13}-0000-{DeviceId}";
    }
}
