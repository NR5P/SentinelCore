using SentinelCore;
using System.Globalization;

namespace SentinelCore.Web;

// /api/organizer/automations - board automations, see OrganizerAutomations.cs. Anyone who can
// see a board can see and edit its rules, the same as the board itself. A rule runs as whoever
// saved it last, and every list it names has to be one that person can see.
public static class OrganizerAutomationEndpoints
{
    private const int MaxActions = 20;
    private const int MaxConditions = 10;
    private const int MaxText = 500;

    public static void MapOrganizerAutomationEndpoints(this RouteGroupBuilder organizer)
    {
        organizer.MapGet("/automations", async (string? boardId, HttpContext context, IOrganizerStore store,
            IOrganizerAutomationStore automations, IOrganizerAutomationNotifier notifier, CancellationToken ct) =>
        {
            var boards = await VisibleIdsAsync(store, OrganizerEndpoints.UserOf(context), OrganizerKinds.Board, ct);
            var list = (await automations.GetAllAsync(ct))
                .Where(rule => boards.Contains(rule.BoardId) && (boardId is null || rule.BoardId == boardId));
            // Whether the send blocks can work here, so the editor can say so before anyone relies on one.
            var channels = await notifier.ChannelsAsync(ct);
            return Results.Ok(new { automations = list, timeZone = TimeZoneInfo.Local.Id, pushReady = channels.PushReady, emailReady = channels.EmailReady });
        });

        organizer.MapPost("/automations", async (OrganizerAutomation request, HttpContext context, IOrganizerStore store,
            IAccountStore accounts, OrganizerAutomationEngine engine, TimeProvider time, CancellationToken ct) =>
        {
            var user = OrganizerEndpoints.UserOf(context);
            var errors = await ValidateAsync(request, user, store, accounts, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = time.GetUtcNow();
            var rule = Clean(request);
            rule.Id = Guid.NewGuid().ToString("N");
            rule.CreatedBy = user.Username;
            rule.RunAs = user.Username;
            rule.CreatedAt = now;
            rule.UpdatedAt = now;
            await engine.SaveAsync(rule, ct);
            return Results.Ok(rule);
        });

        organizer.MapPut("/automations/{id}", async (string id, OrganizerAutomation request, HttpContext context,
            IOrganizerStore store, IAccountStore accounts, IOrganizerAutomationStore automations, OrganizerAutomationEngine engine,
            TimeProvider time, CancellationToken ct) =>
        {
            var user = OrganizerEndpoints.UserOf(context);
            if (await FindVisibleAsync(id, user, store, automations, ct) is not { } existing) return Results.NotFound();
            var errors = await ValidateAsync(request, user, store, accounts, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var rule = Clean(request);
            rule.Id = existing.Id;
            rule.CreatedBy = existing.CreatedBy;
            rule.CreatedAt = existing.CreatedAt;
            rule.RunAs = user.Username;
            rule.UpdatedAt = time.GetUtcNow();
            rule.LastRunAt = existing.LastRunAt;
            rule.LastOk = existing.LastOk;
            rule.LastResult = existing.LastResult;
            rule.RunCount = existing.RunCount;
            await engine.SaveAsync(rule, ct);
            return Results.Ok(rule);
        });

        organizer.MapDelete("/automations/{id}", async (string id, HttpContext context, IOrganizerStore store,
            IOrganizerAutomationStore automations, OrganizerAutomationEngine engine, CancellationToken ct) =>
        {
            if (await FindVisibleAsync(id, OrganizerEndpoints.UserOf(context), store, automations, ct) is null) return Results.NotFound();
            await engine.DeleteAsync(id, ct);
            return Results.Ok(new { deleted = true });
        });

        organizer.MapPost("/automations/{id}/run", async (string id, HttpContext context, IOrganizerStore store,
            IOrganizerAutomationStore automations, OrganizerAutomationEngine engine, CancellationToken ct) =>
        {
            if (await FindVisibleAsync(id, OrganizerEndpoints.UserOf(context), store, automations, ct) is not { } rule) return Results.NotFound();
            if (rule.Trigger.Type != OrganizerAutomationTypes.Schedule)
                return Results.BadRequest(new { errors = new[] { "Only a scheduled rule can be run by hand - the others wait for a card to do something." } });
            var run = await engine.RunNowAsync(id, ct);
            return run is null
                ? Results.Json(new { errors = new[] { "The automation engine is still starting - try again in a moment." } }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(run);
        });

        organizer.MapGet("/automations/{id}/runs", async (string id, HttpContext context, IOrganizerStore store,
            IOrganizerAutomationStore automations, CancellationToken ct) =>
        {
            if (await FindVisibleAsync(id, OrganizerEndpoints.UserOf(context), store, automations, ct) is null) return Results.NotFound();
            return Results.Ok(new { runs = await automations.GetRunsAsync(id, ct) });
        });

        // When a schedule would run, for the editor's "next: tomorrow at 7:00 AM" line - worked out
        // here so it matches what the server will actually do.
        organizer.MapPost("/automations/preview", (BoardAutomationSchedule schedule, TimeProvider time) =>
        {
            var runs = new List<DateTimeOffset>();
            var after = time.GetUtcNow();
            while (runs.Count < 3 && OrganizerAutomationSchedule.Next(schedule, after) is { } next)
            {
                runs.Add(next);
                after = next;
                if (schedule.Repeat == "once") break;
            }
            return Results.Ok(new { runs });
        });
    }

    private static async Task<HashSet<string>> VisibleIdsAsync(IOrganizerStore store, OrganizerUser user, string kind, CancellationToken ct) =>
        (await store.GetItemsAsync(user, kind, null, ct)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

    private static async Task<OrganizerAutomation?> FindVisibleAsync(
        string id, OrganizerUser user, IOrganizerStore store, IOrganizerAutomationStore automations, CancellationToken ct)
    {
        if (await automations.GetAsync(id, ct) is not { } rule) return null;
        return (await VisibleIdsAsync(store, user, OrganizerKinds.Board, ct)).Contains(rule.BoardId) ? rule : null;
    }

    // What a client sent, minus the fields only the server may set.
    private static OrganizerAutomation Clean(OrganizerAutomation request) => new()
    {
        Name = request.Name.Trim(),
        Enabled = request.Enabled,
        BoardId = request.BoardId,
        TimeZone = request.TimeZone,
        Trigger = request.Trigger,
        Conditions = request.Conditions,
        Actions = request.Actions,
    };

    public const int MaxRecipients = OrganizerAutomationNotifier.MaxEmailRecipients;

    public static async Task<List<string>> ValidateAsync(
        OrganizerAutomation rule, OrganizerUser user, IOrganizerStore store, IAccountStore accounts, CancellationToken ct)
    {
        var errors = new List<string>();
        rule.Trigger ??= new BoardAutomationTrigger();
        rule.Conditions ??= [];
        rule.Actions ??= [];

        if (string.IsNullOrWhiteSpace(rule.Name)) errors.Add("Give the rule a name.");
        else if (rule.Name.Trim().Length > 80) errors.Add("Rule names can be at most 80 characters.");

        var boards = await store.GetItemsAsync(user, OrganizerKinds.Board, null, ct);
        if (!boards.Any(board => board.Id == rule.BoardId)) errors.Add("That board doesn't exist, or you can't see it.");

        // Lists on any board this person can see - a rule may move a card to another board.
        var columns = (await store.GetItemsAsync(user, OrganizerKinds.Column, null, ct)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var knownPeople = (await accounts.GetAllAsync(ct)).Select(a => a.Username.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        void RequireColumn(string? id, string what)
        {
            if (string.IsNullOrEmpty(id)) errors.Add($"Pick a list for \"{what}\".");
            else if (!columns.Contains(id)) errors.Add($"The list chosen for \"{what}\" doesn't exist any more.");
        }
        void CheckPeople(List<string>? people)
        {
            foreach (var person in people ?? [])
            {
                if (!knownPeople.Contains(person.Trim().ToLowerInvariant())) errors.Add($"There's no account called \"{person}\".");
            }
        }
        void CheckText(string? text, string what)
        {
            if (text is { Length: > MaxText }) errors.Add($"\"{what}\" can be at most {MaxText} characters.");
        }

        var trigger = rule.Trigger;
        if (!OrganizerAutomationTypes.Triggers.Contains(trigger.Type))
        {
            errors.Add("Pick what starts the rule.");
        }
        else if (trigger.Type == OrganizerAutomationTypes.Schedule)
        {
            ValidateSchedule(trigger.Schedule, errors);
        }
        else if (trigger.Type == OrganizerAutomationTypes.CardDue && trigger.MinutesBefore is < 0 or > 60 * 24 * 14)
        {
            errors.Add("A due-date reminder can be at most two weeks before.");
        }
        else if (!string.IsNullOrEmpty(trigger.ColumnId) && !columns.Contains(trigger.ColumnId))
        {
            errors.Add("The list the rule watches doesn't exist any more.");
        }

        if (!string.IsNullOrWhiteSpace(rule.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(rule.TimeZone, out _))
            errors.Add($"The server doesn't know the time zone \"{rule.TimeZone}\".");

        if (rule.Conditions.Count > MaxConditions) errors.Add($"A rule can have at most {MaxConditions} conditions.");
        foreach (var condition in rule.Conditions)
        {
            if (!OrganizerAutomationTypes.Conditions.Contains(condition.Type)) errors.Add($"\"{condition.Type}\" isn't a condition this server knows.");
            else if (condition.Type == "inColumn") RequireColumn(condition.ColumnId, "is in list");
            else if (condition.Type == "onDays" && condition.Days is { } days && days.Any(d => d is < 0 or > 6)) errors.Add("Days of the week go from 0 (Sunday) to 6.");
            else if (condition.Type is "titleContains" or "hasLabel" or "lacksLabel" && string.IsNullOrWhiteSpace(condition.Text))
                errors.Add("A condition is missing its text.");
            CheckText(condition.Text, "condition");
        }

        if (rule.Actions.Count == 0) errors.Add("Add at least one thing for the rule to do.");
        if (rule.Actions.Count > MaxActions) errors.Add($"A rule can do at most {MaxActions} things.");
        // A scheduled rule has no card of its own; card steps only make sense after it makes one.
        var haveCard = trigger.Type != OrganizerAutomationTypes.Schedule;
        foreach (var action in rule.Actions)
        {
            var isBoard = OrganizerAutomationTypes.BoardActions.Contains(action.Type);
            if (!isBoard && !OrganizerAutomationTypes.IsCardAction(action.Type))
            {
                errors.Add($"\"{action.Type}\" isn't something this server knows how to do.");
                continue;
            }
            if (OrganizerAutomationTypes.IsCardAction(action.Type) && !haveCard)
                errors.Add("A scheduled rule has no card to change - put \"Create a card\" before the steps that change it.");

            switch (action.Type)
            {
                case "createCard":
                    RequireColumn(action.ColumnId, "create a card");
                    if (string.IsNullOrWhiteSpace(action.Title)) errors.Add("The new card needs a title.");
                    haveCard = true;
                    break;
                case "moveCard":
                    RequireColumn(action.ColumnId, "move the card");
                    break;
                case "moveAllCards":
                    RequireColumn(action.ColumnId, "move every card from");
                    RequireColumn(action.ToColumnId, "move every card to");
                    break;
                case "deleteAllCards":
                    RequireColumn(action.ColumnId, "delete every card in");
                    break;
                case "addLabel" or "removeLabel" when string.IsNullOrWhiteSpace(action.Name):
                    errors.Add("Say which label.");
                    break;
                case "addComment" when string.IsNullOrWhiteSpace(action.Text):
                    errors.Add("The comment is empty.");
                    break;
                case "addChecklist" when action.Items is not { Count: > 0 }:
                    errors.Add("Add at least one checklist item.");
                    break;
                case "assign" when string.IsNullOrWhiteSpace(action.Username):
                    errors.Add("Pick who to assign.");
                    break;
                case "deleteCard":
                    haveCard = false;
                    break;
                case "notify":
                    if (string.IsNullOrWhiteSpace(action.Text)) errors.Add("The notification has no message.");
                    if (action.Everyone) break;
                    if (user.Username is null) errors.Add("With login turned off every phone is the same person, so a notification can only go to everyone.");
                    else if (action.People is not { Count: > 0 }) errors.Add("Pick who gets the notification, or send it to everyone.");
                    CheckPeople(action.People);
                    break;
                case "email":
                    if (string.IsNullOrWhiteSpace(action.Text)) errors.Add("The email has no message.");
                    if ((action.People?.Count ?? 0) + (action.Addresses?.Count(a => !string.IsNullOrWhiteSpace(a)) ?? 0) == 0)
                        errors.Add("Pick who gets the email, or type an address.");
                    if ((action.People?.Count ?? 0) + (action.Addresses?.Count ?? 0) > MaxRecipients)
                        errors.Add($"An email can go to at most {MaxRecipients} people.");
                    CheckPeople(action.People);
                    foreach (var address in (action.Addresses ?? []).Where(a => !string.IsNullOrWhiteSpace(a)))
                    {
                        if (address.Trim().Length > 254 || !SmtpMailer.LooksLikeEmailAddress(address.Trim()))
                            errors.Add($"\"{address}\" doesn't look like an email address.");
                    }
                    break;
            }
            if (action.InDays is < -365 or > 3650) errors.Add("Due dates can be at most ten years away.");
            if (action.Time is { Length: > 0 } t && !OrganizerAutomationSchedule.TryParseTime(t, out _)) errors.Add($"\"{t}\" isn't a time.");
            if (action.Items is { Count: > 50 }) errors.Add("At most 50 checklist items at once.");
            CheckText(action.Title, "title");
            CheckText(action.Description, "description");
            CheckText(action.Text, "comment");
            CheckText(action.Name, "label");
            foreach (var item in action.Items ?? []) CheckText(item, "checklist item");
        }

        return errors;
    }

    private static void ValidateSchedule(BoardAutomationSchedule? schedule, List<string> errors)
    {
        if (schedule is null)
        {
            errors.Add("Say when the rule should run.");
            return;
        }
        if (!OrganizerAutomationTypes.Repeats.Contains(schedule.Repeat)) errors.Add("Pick how often it repeats.");
        if (!OrganizerAutomationSchedule.TryParseTime(schedule.Time, out _)) errors.Add("Pick a time of day.");
        if (schedule.Days.Any(d => d is < 0 or > 6)) errors.Add("Days of the week go from 0 (Sunday) to 6.");
        if (schedule.Repeat == "monthly" && schedule.DayOfMonth is { } day && day != -1 && day is < 1 or > 31)
            errors.Add("Pick a day of the month from 1 to 31, or the last day.");
        if (schedule.Repeat == "hourly" && schedule.EveryHours is < 1 or > 24) errors.Add("Every 1 to 24 hours.");
        if (!string.IsNullOrWhiteSpace(schedule.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZone, out _))
            errors.Add($"The server doesn't know the time zone \"{schedule.TimeZone}\".");
        if (schedule.Repeat == "once")
        {
            if (!DateOnly.TryParseExact(schedule.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errors.Add("Pick the date it should run.");
            else if (OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.UtcNow) is null) errors.Add("That date and time has already passed.");
        }
    }
}
