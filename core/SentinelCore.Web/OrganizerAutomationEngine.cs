using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

// Runs board automations (see OrganizerAutomations.cs). It keeps its own picture of every board,
// column and card, follows the Organizer's change feed, and compares each changed card with how
// it looked before - that comparison is what turns "this card's parentId changed" into "a card
// was moved into Done". Schedules are checked on the same loop (OrganizerAutomationWorker).
//
// Everything a rule does goes through IOrganizerStore.ApplyChangesAsync as the rule's RunAs
// person, exactly like an edit from a device: the same permission checks, the same merge, the
// same sync to every device, plus a push nudge so phones' widgets catch up.
//
// A rule's own changes never fire rules. The engine records the result of each change it makes
// as the new "before", so when that change comes back round the feed there's nothing to compare.
// That rules out loops ("moved to A -> move to B", "moved to B -> move to A") by construction.
public sealed class OrganizerAutomationEngine
{
    // How late a scheduled run may still happen - the server was restarting at 7:00, say. Later
    // than this and it's skipped rather than putting "pick up eggs" on the board at 3pm.
    public static readonly TimeSpan MissedRunGrace = TimeSpan.FromHours(6);

    // A change that reaches the server more than this long after it was made - a device that was
    // offline for a day, or every card being re-uploaded to a rebuilt server - doesn't fire rules.
    // Otherwise refilling a server would "add" every card on every board at once.
    public static readonly TimeSpan StaleChange = TimeSpan.FromHours(12);

    // A rule sends at most this many notifications and emails an hour. A badly built rule ("when a
    // card is added, email everyone") meeting a pasted list of fifty cards sends twenty, not fifty.
    public const int MaxSendsPerRulePerHour = 20;

    public const string DeviceId = "automation";
    private const int PageSize = 1000;
    private static readonly string[] Palette = ["#39ff9c", "#4be7ff", "#ffb545", "#c792ea", "#ff7eb6", "#82aaff", "#f78c6c"];
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private readonly IOrganizerStore _store;
    private readonly IOrganizerAutomationStore _automations;
    private readonly IOrganizerAutomationNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly Action _changed;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recentSends = new(StringComparer.Ordinal);

    // Live boards, columns and cards, by id.
    private readonly Dictionary<string, OrganizerItem> _items = new(StringComparer.Ordinal);
    private long _cursor;
    private bool _initialized;
    private long _lastClockMs;
    private int _lastClockCounter;

    public OrganizerAutomationEngine(
        IOrganizerStore store, IOrganizerAutomationStore automations, IOrganizerAutomationNotifier notifier,
        TimeProvider time, Action changed, ILogger logger)
    {
        _store = store;
        _automations = automations;
        _notifier = notifier;
        _time = time;
        _changed = changed;
        _logger = logger;
    }

    public long Cursor => Interlocked.Read(ref _cursor);

    private sealed record CardEvent(string Type, OrganizerItem Card, string? Label);

    private sealed class RunContext(OrganizerAutomation automation, OrganizerUser user, TimeZoneInfo zone, DateTimeOffset now, OrganizerItem? card)
    {
        public OrganizerAutomation Automation { get; } = automation;
        public OrganizerUser User { get; } = user;
        public TimeZoneInfo Zone { get; } = zone;
        public DateTimeOffset Now { get; } = now;
        public string? TriggerTitle { get; } = card is null ? null : Text(card, "title");
        public string? BoardTitle { get; init; }
        public OrganizerItem? Card { get; set; } = card;
        public bool ChangedAnything { get; set; }
        // A step that didn't fully work but shouldn't stop the rest - a notification that
        // couldn't be delivered mustn't stop the card being moved.
        public bool Problem { get; set; }
    }

    private sealed class RuleFailure(string message) : Exception(message);

    // ---- the loop's steps ---------------------------------------------------------------------

    // Reads the whole Organizer once. Nothing that already exists fires anything.
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            _items.Clear();
            long cursor = 0;
            while (true)
            {
                var page = await _store.PullEverythingAsync(cursor, PageSize, cancellationToken);
                foreach (var item in page.Items) Remember(item);
                cursor = page.Cursor;
                if (!page.HasMore) break;
            }
            Interlocked.Exchange(ref _cursor, cursor);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Everything that changed since last time: fire the card rules it calls for.
    public async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized) return;
            while (true)
            {
                var page = await _store.PullEverythingAsync(_cursor, PageSize, cancellationToken);
                var events = new List<CardEvent>();
                foreach (var item in page.Items)
                {
                    var before = _items.GetValueOrDefault(item.Id);
                    events.AddRange(Compare(before, item));
                    Remember(item);
                }
                Interlocked.Exchange(ref _cursor, page.Cursor);

                if (events.Count > 0)
                {
                    var rules = (await _automations.GetAllAsync(cancellationToken))
                        .Where(rule => rule.Enabled && rule.Trigger.Type != OrganizerAutomationTypes.Schedule)
                        .ToList();
                    foreach (var cardEvent in events)
                    {
                        foreach (var rule in rules.Where(rule => Matches(rule, cardEvent)))
                        {
                            // The card as it is now - an earlier rule may have just changed it.
                            var card = _items.GetValueOrDefault(cardEvent.Card.Id);
                            if (card is null) break;
                            await RunAndRecordAsync(rule, card, cancellationToken);
                        }
                    }
                }

                if (!page.HasMore) break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunDueSchedulesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized) return;
            var now = _time.GetUtcNow();
            foreach (var rule in await _automations.GetAllAsync(cancellationToken))
            {
                if (!rule.Enabled || rule.Trigger is not { Type: OrganizerAutomationTypes.Schedule, Schedule: { } schedule }) continue;

                if (rule.NextRunAt is null)
                {
                    rule.NextRunAt = OrganizerAutomationSchedule.Next(schedule, now);
                    if (rule.NextRunAt is not null) await _automations.SaveAsync(rule, cancellationToken);
                    continue;
                }
                if (rule.NextRunAt > now) continue;

                if (now - rule.NextRunAt.Value <= MissedRunGrace)
                {
                    await RunAndRecordAsync(rule, null, cancellationToken, save: false);
                }
                else
                {
                    var message = $"Skipped the {Local(rule.NextRunAt.Value, schedule):MMM d h:mm tt} run - the server was off then.";
                    await _automations.AddRunAsync(new OrganizerAutomationRun(rule.Id, now, false, message), cancellationToken);
                    rule.LastOk = false;
                    rule.LastResult = message;
                }

                rule.NextRunAt = OrganizerAutomationSchedule.Next(schedule, now);
                if (schedule.Repeat == "once") rule.Enabled = false;
                await _automations.SaveAsync(rule, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // "When a card is due": every card on the rule's board whose moment has come, once each.
    public async Task RunDueCardsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized) return;
            var now = _time.GetUtcNow();
            foreach (var rule in await _automations.GetAllAsync(cancellationToken))
            {
                if (!rule.Enabled || rule.Trigger.Type != OrganizerAutomationTypes.CardDue) continue;
                foreach (var (card, due, fireAt) in DueCards(rule).ToList())
                {
                    // Not yet, or so long ago (the server was off) that it would be news to nobody.
                    if (fireAt > now || now - fireAt > MissedRunGrace) continue;
                    if (!await _automations.TryMarkDueFiredAsync(rule.Id, card.Id, due, cancellationToken)) continue;
                    // The card as it is now - an earlier rule may have just changed or deleted it.
                    if (_items.GetValueOrDefault(card.Id) is not { } current) continue;
                    await RunAndRecordAsync(rule, current, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<(OrganizerItem Card, string Due, DateTimeOffset FireAt)> DueCards(OrganizerAutomation rule)
    {
        var zone = OrganizerAutomationSchedule.ZoneOf(rule);
        var before = TimeSpan.FromMinutes(Math.Max(0, rule.Trigger.MinutesBefore ?? 0));
        foreach (var card in _items.Values)
        {
            if (card.Kind != OrganizerKinds.Card || Bool(card, "completed")) continue;
            if (BoardOf(card) != rule.BoardId) continue;
            if (!string.IsNullOrEmpty(rule.Trigger.ColumnId) && card.ParentId != rule.Trigger.ColumnId) continue;
            if (Text(card, "due") is not { } due || OrganizerAutomationSchedule.DueInstant(due, zone) is not { } at) continue;
            yield return (card, due, at - before);
        }
    }

    // How long until the next schedule or due card, so the loop can wake for it on the second.
    public async Task<TimeSpan?> TimeUntilNextScheduleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            var rules = await _automations.GetAllAsync(cancellationToken);
            var next = rules
                .Where(rule => rule.Enabled && rule.NextRunAt is not null)
                .Select(rule => rule.NextRunAt!.Value)
                .Concat(rules
                    .Where(rule => rule.Enabled && rule.Trigger.Type == OrganizerAutomationTypes.CardDue)
                    .SelectMany(DueCards)
                    .Select(due => due.FireAt)
                    .Where(at => at > now))
                .DefaultIfEmpty(DateTimeOffset.MaxValue)
                .Min();
            return next == DateTimeOffset.MaxValue ? null : next - now;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- called by the endpoints ---------------------------------------------------------------

    // Saves a rule and works out when it next runs. Through here rather than straight to the
    // store so an edit can't be overwritten by a run finishing at the same moment.
    public async Task SaveAsync(OrganizerAutomation rule, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            rule.NextRunAt = rule.Enabled && rule.Trigger is { Type: OrganizerAutomationTypes.Schedule, Schedule: { } schedule }
                ? OrganizerAutomationSchedule.Next(schedule, _time.GetUtcNow())
                : null;
            await _automations.SaveAsync(rule, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _automations.DeleteAsync(id, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // "Run now", for trying a rule out. Only a scheduled rule can: a card rule needs a card.
    public async Task<OrganizerAutomationRun?> RunNowAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized) return null;
            if (await _automations.GetAsync(id, cancellationToken) is not { } rule) return null;
            // Catch up first, so the rule sees the board as it is right now.
            await CatchUpQuietlyAsync(cancellationToken);
            return await RunAndRecordAsync(rule, null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- change detection ---------------------------------------------------------------------

    private void Remember(OrganizerItem item)
    {
        if (item.Kind is not (OrganizerKinds.Board or OrganizerKinds.Column or OrganizerKinds.Card)) return;
        if (item.Deleted) _items.Remove(item.Id);
        else _items[item.Id] = item;
    }

    private async Task CatchUpQuietlyAsync(CancellationToken cancellationToken)
    {
        // Used by Run now: anything newer than the loop has seen is taken in without firing - the
        // loop will not see it again, but a card rule missing one change beats a doubled one.
        while (true)
        {
            var page = await _store.PullEverythingAsync(_cursor, PageSize, cancellationToken);
            foreach (var item in page.Items) Remember(item);
            Interlocked.Exchange(ref _cursor, page.Cursor);
            if (!page.HasMore) return;
        }
    }

    private IEnumerable<CardEvent> Compare(OrganizerItem? before, OrganizerItem after)
    {
        if (after.Kind != OrganizerKinds.Card || after.Deleted) yield break;
        var now = _time.GetUtcNow();

        if (before is null)
        {
            if (Fresh(after, "parentId", now)) yield return new CardEvent(OrganizerAutomationTypes.CardAdded, after, null);
            yield break;
        }

        if (before.ParentId != after.ParentId && Fresh(after, "parentId", now))
            yield return new CardEvent(OrganizerAutomationTypes.CardMoved, after, null);

        if (!Bool(before, "completed") && Bool(after, "completed") && Fresh(after, "completed", now))
            yield return new CardEvent(OrganizerAutomationTypes.CardCompleted, after, null);

        if (!ChecklistDone(before) && ChecklistDone(after) && Fresh(after, "checklist", now))
            yield return new CardEvent(OrganizerAutomationTypes.ChecklistCompleted, after, null);

        if (Fresh(after, "labels", now))
        {
            var had = LabelNames(before).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var label in LabelNames(after).Where(name => !had.Contains(name)))
                yield return new CardEvent(OrganizerAutomationTypes.LabelAdded, after, label);
        }
    }

    // Was this field changed recently, by a person rather than a rule?
    private static bool Fresh(OrganizerItem item, string field, DateTimeOffset now)
    {
        if (!item.Clocks.TryGetValue(field, out var clock) || clock.Length < 13) return false;
        if (clock.EndsWith("-" + DeviceId, StringComparison.Ordinal)) return false;
        if (!long.TryParse(clock.AsSpan(0, 13), NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) return false;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms) >= now - StaleChange;
    }

    private string? BoardOf(OrganizerItem card) =>
        card.ParentId is { } columnId && _items.GetValueOrDefault(columnId) is { Kind: OrganizerKinds.Column } column ? column.ParentId : null;

    private bool Matches(OrganizerAutomation rule, CardEvent cardEvent)
    {
        if (rule.Trigger.Type != cardEvent.Type) return false;
        if (rule.BoardId != BoardOf(cardEvent.Card)) return false;
        return cardEvent.Type switch
        {
            OrganizerAutomationTypes.CardAdded or OrganizerAutomationTypes.CardMoved =>
                string.IsNullOrEmpty(rule.Trigger.ColumnId) || rule.Trigger.ColumnId == cardEvent.Card.ParentId,
            OrganizerAutomationTypes.LabelAdded =>
                string.IsNullOrWhiteSpace(rule.Trigger.Label) || string.Equals(rule.Trigger.Label.Trim(), cardEvent.Label, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    // ---- running a rule -----------------------------------------------------------------------

    private async Task<OrganizerAutomationRun> RunAndRecordAsync(
        OrganizerAutomation rule, OrganizerItem? card, CancellationToken cancellationToken, bool save = true)
    {
        var now = _time.GetUtcNow();
        var zone = OrganizerAutomationSchedule.ZoneOf(rule);
        var context = new RunContext(rule, rule.RunAs is { Length: > 0 } name ? new OrganizerUser(name, null) : OrganizerUser.Shared, zone, now, card)
        {
            BoardTitle = _items.GetValueOrDefault(rule.BoardId) is { } board ? Text(board, "title") : null,
        };

        OrganizerAutomationRun run;
        if (!rule.Conditions.All(condition => Holds(condition, context)))
        {
            // Not worth a log line each time - "only if it has the urgent label" would fill the
            // history with every card that didn't.
            return new OrganizerAutomationRun(rule.Id, now, true, "Conditions not met - nothing done.");
        }

        var notes = new List<string>();
        try
        {
            foreach (var action in rule.Actions)
                notes.Add(await RunActionAsync(action, context, cancellationToken));
            run = new OrganizerAutomationRun(rule.Id, now, !context.Problem, string.Join(" ", notes.Where(note => note.Length > 0)));
        }
        catch (RuleFailure failure)
        {
            notes.Add(failure.Message);
            run = new OrganizerAutomationRun(rule.Id, now, false, string.Join(" ", notes.Where(note => note.Length > 0)));
        }

        if (context.ChangedAnything) _changed();
        _logger.LogInformation("Board automation \"{Name}\" ran: {Result}", rule.Name, run.Message);

        rule.LastRunAt = now;
        rule.LastOk = run.Ok;
        rule.LastResult = run.Message;
        rule.RunCount++;
        await _automations.AddRunAsync(run, cancellationToken);
        if (save) await _automations.SaveAsync(rule, cancellationToken);
        return run;
    }

    private bool Holds(BoardAutomationCondition condition, RunContext context)
    {
        var card = context.Card;
        switch (condition.Type)
        {
            case "onDays":
                var today = (int)TimeZoneInfo.ConvertTime(context.Now, context.Zone).DayOfWeek;
                return condition.Days is not { Count: > 0 } days || days.Contains(today);
            case "titleContains":
                return card is not null && (Text(card, "title") ?? "").Contains(condition.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
            case "hasLabel":
                return card is not null && LabelNames(card).Contains(condition.Text?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            case "lacksLabel":
                return card is not null && !LabelNames(card).Contains(condition.Text?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            case "inColumn":
                return card is not null && card.ParentId == condition.ColumnId;
            default:
                return false;
        }
    }

    private async Task<string> RunActionAsync(BoardAutomationAction action, RunContext context, CancellationToken cancellationToken)
    {
        if (OrganizerAutomationTypes.IsCardAction(action.Type) && context.Card is null)
            throw new RuleFailure("There was no card to act on - add \"Create a card\" first.");

        var card = context.Card!;
        switch (action.Type)
        {
            case "createCard":
            {
                var column = ColumnOrFail(action.ColumnId);
                var title = Render(action.Title, context);
                if (string.IsNullOrWhiteSpace(title)) title = "card";
                if (action.SkipIfExists && CardsIn(column.Id).FirstOrDefault(c => !Bool(c, "completed")
                        && string.Equals(Text(c, "title")?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)) is { } existing)
                {
                    context.Card = existing;
                    return $"\"{title}\" was already in {Title(column)}, so no new card.";
                }

                var fields = new JsonObject
                {
                    ["parentId"] = column.Id,
                    ["title"] = title,
                    ["position"] = PositionIn(column.Id, action.Position),
                    ["completed"] = false,
                };
                if (Render(action.Description, context) is { Length: > 0 } description) fields["description"] = description;
                if (action.Labels is { Count: > 0 } labels)
                    fields["labels"] = new JsonArray([.. labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase).Select(l => (JsonNode)Label(l.Trim(), null))]);
                if (action.Items is { Count: > 0 } items) fields["checklist"] = Checklist(items.Select(i => Render(i, context)));
                if (action.InDays is { } days) fields["due"] = Due(days, action.Time, context);
                if (!string.IsNullOrWhiteSpace(action.Username)) fields["assignees"] = new JsonArray(action.Username.Trim().ToLowerInvariant());

                context.Card = await ApplyAsync(context, Guid.NewGuid().ToString("N"), OrganizerKinds.Card, null, fields, cancellationToken);
                return $"Made \"{title}\" in {Title(column)}.";
            }

            case "moveCard":
            {
                var column = ColumnOrFail(action.ColumnId);
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card,
                    new JsonObject { ["parentId"] = column.Id, ["position"] = PositionIn(column.Id, action.Position, card.Id) }, cancellationToken);
                return $"Moved \"{Text(card, "title")}\" to {Title(column)}.";
            }

            case "addLabel":
            {
                var name = Render(action.Name, context).Trim();
                if (name.Length == 0) throw new RuleFailure("The label to add has no name.");
                var labels = Labels(card);
                if (labels.Any(l => string.Equals(Text(l, "name"), name, StringComparison.OrdinalIgnoreCase))) return "";
                labels.Add(Label(name, action.Color));
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["labels"] = labels }, cancellationToken);
                return $"Labelled it \"{name}\".";
            }

            case "removeLabel":
            {
                var name = Render(action.Name, context).Trim();
                var labels = Labels(card);
                var kept = new JsonArray([.. labels.Where(l => !string.Equals(Text(l, "name"), name, StringComparison.OrdinalIgnoreCase)).Select(l => l?.DeepClone())]);
                if (kept.Count == labels.Count) return "";
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["labels"] = kept }, cancellationToken);
                return $"Took off the \"{name}\" label.";
            }

            case "setDue":
            {
                JsonNode? due = action.InDays is { } days ? Due(days, action.Time, context) : null;
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["due"] = due }, cancellationToken);
                return due is null ? "Cleared the due date." : $"Set it due {due}.";
            }

            case "complete":
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["completed"] = action.Done }, cancellationToken);
                return action.Done ? "Marked it done." : "Marked it not done.";

            case "addChecklist":
            {
                var checklist = card.Fields["checklist"] as JsonArray is { } existing ? (JsonArray)existing.DeepClone() : [];
                foreach (var item in Checklist((action.Items ?? []).Select(i => Render(i, context)))) checklist.Add(item?.DeepClone());
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["checklist"] = checklist }, cancellationToken);
                return "Added checklist items.";
            }

            case "resetChecklist":
            {
                if (card.Fields["checklist"] is not JsonArray { Count: > 0 } existing) return "";
                var checklist = (JsonArray)existing.DeepClone();
                foreach (var item in checklist.OfType<JsonObject>()) item["done"] = false;
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["checklist"] = checklist }, cancellationToken);
                return "Unticked the checklist.";
            }

            case "addComment":
            {
                var body = Render(action.Text, context).Trim();
                if (body.Length == 0) return "";
                await ApplyAsync(context, Guid.NewGuid().ToString("N"), OrganizerKinds.Comment, null,
                    new JsonObject { ["parentId"] = card.Id, ["body"] = body }, cancellationToken);
                return "Added a comment.";
            }

            case "assign":
            {
                var username = action.Username?.Trim().ToLowerInvariant() ?? "";
                if (username.Length == 0) throw new RuleFailure("Nobody was chosen to assign.");
                var assignees = card.Fields["assignees"] as JsonArray is { } list ? (JsonArray)list.DeepClone() : [];
                if (assignees.Any(a => a?.GetValueKind() == JsonValueKind.String && a.GetValue<string>() == username)) return "";
                assignees.Add(username);
                context.Card = await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["assignees"] = assignees }, cancellationToken);
                return $"Assigned it to {username}.";
            }

            case "deleteCard":
                await ApplyAsync(context, card.Id, card.Kind, card, new JsonObject { ["deleted"] = true }, cancellationToken);
                context.Card = null;
                return $"Deleted \"{Text(card, "title")}\".";

            case "moveAllCards":
            {
                var from = ColumnOrFail(action.ColumnId);
                var to = ColumnOrFail(action.ToColumnId);
                if (from.Id == to.Id) return "";
                var cards = CardsIn(from.Id).Where(c => !action.OnlyCompleted || Bool(c, "completed")).OrderBy(Position).ToList();
                var position = PositionIn(to.Id, "bottom");
                foreach (var moving in cards)
                {
                    await ApplyAsync(context, moving.Id, moving.Kind, moving, new JsonObject { ["parentId"] = to.Id, ["position"] = position }, cancellationToken);
                    position += 1;
                }
                return cards.Count == 0 ? $"{Title(from)} was already empty." : $"Moved {Count(cards.Count)} from {Title(from)} to {Title(to)}.";
            }

            case "deleteAllCards":
            {
                var from = ColumnOrFail(action.ColumnId);
                var cards = CardsIn(from.Id).Where(c => !action.OnlyCompleted || Bool(c, "completed")).ToList();
                foreach (var doomed in cards)
                    await ApplyAsync(context, doomed.Id, doomed.Kind, doomed, new JsonObject { ["deleted"] = true }, cancellationToken);
                return cards.Count == 0 ? $"{Title(from)} was already empty." : $"Deleted {Count(cards.Count)} from {Title(from)}.";
            }

            case "notify":
            {
                var body = Render(action.Text, context).Trim();
                if (body.Length == 0) return "";
                var title = Render(action.Title, context).Trim();
                if (title.Length == 0) title = context.BoardTitle ?? "Organizer";
                if (!TryCountSend(context)) return TooManySends(context);
                var result = await _notifier.PushAsync(
                    action.Everyone ? null : action.People ?? [], title, body, context.Card?.Id ?? context.Automation.BoardId, cancellationToken);
                context.Problem |= !result.Ok;
                return result.Message;
            }

            case "email":
            {
                var body = Render(action.Text, context).Trim();
                if (body.Length == 0) return "";
                var subject = Render(action.Title, context).Trim();
                if (subject.Length == 0) subject = $"{context.BoardTitle ?? "Organizer"}: {context.Automation.Name}";
                if (!TryCountSend(context)) return TooManySends(context);
                var footer = $"\n\n-- \nSent by the board automation \"{context.Automation.Name}\" on {context.BoardTitle ?? "a board"}. " +
                    "Change or turn it off in the Organizer: open the board, then automations.";
                var result = await _notifier.EmailAsync(action.People ?? [], action.Addresses ?? [], subject, body + footer, cancellationToken);
                context.Problem |= !result.Ok;
                return result.Message;
            }

            default:
                throw new RuleFailure($"This server doesn't know how to \"{action.Type}\".");
        }
    }

    // One change, applied as the rule's person. The result becomes the engine's "before" for that
    // item, which is what stops the change firing rules when it comes back round the feed.
    private async Task<OrganizerItem> ApplyAsync(
        RunContext context, string id, string kind, OrganizerItem? existing, JsonObject fields, CancellationToken cancellationToken)
    {
        var clock = NextClock(existing);
        var change = new OrganizerChange { Id = id, Kind = kind, Fields = fields };
        foreach (var (name, _) in fields) change.Clocks[name] = clock;

        var result = await _store.ApplyChangesAsync(context.User, [change], null, cancellationToken);
        if (result.Rejected.FirstOrDefault() is { } rejection)
            throw new RuleFailure($"The server refused: {rejection.Error}");

        context.ChangedAnything = true;
        foreach (var item in result.Items) Remember(item);
        return result.Items.First(item => item.Id == id);
    }

    // A clock newer than anything on the item, so a rule's change wins over the edit that
    // triggered it even if that device's clock runs a little fast.
    private string NextClock(OrganizerItem? item)
    {
        var ms = _time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var clock in item?.Clocks.Values ?? Enumerable.Empty<string>())
        {
            if (clock.Length >= 13 && long.TryParse(clock.AsSpan(0, 13), NumberStyles.None, CultureInfo.InvariantCulture, out var seen))
                ms = Math.Max(ms, seen + 1);
        }
        if (ms <= _lastClockMs)
        {
            ms = _lastClockMs;
            _lastClockCounter++;
        }
        else
        {
            _lastClockCounter = 0;
        }
        _lastClockMs = ms;
        return $"{ms:D13}-{_lastClockCounter % 10000:D4}-{DeviceId}";
    }

    // ---- helpers ------------------------------------------------------------------------------

    // Counts a send against the rule's hourly allowance; false when it's used up.
    private bool TryCountSend(RunContext context)
    {
        if (!_recentSends.TryGetValue(context.Automation.Id, out var sends))
            _recentSends[context.Automation.Id] = sends = new Queue<DateTimeOffset>();
        while (sends.Count > 0 && context.Now - sends.Peek() >= TimeSpan.FromHours(1)) sends.Dequeue();
        if (sends.Count >= MaxSendsPerRulePerHour) return false;
        sends.Enqueue(context.Now);
        return true;
    }

    private static string TooManySends(RunContext context)
    {
        context.Problem = true;
        return $"Didn't send - this rule has already sent {MaxSendsPerRulePerHour} notifications or emails in the last hour.";
    }

    private OrganizerItem ColumnOrFail(string? id) =>
        id is not null && _items.GetValueOrDefault(id) is { Kind: OrganizerKinds.Column } column
            ? column
            : throw new RuleFailure("One of the lists this rule uses has been deleted - edit the rule to pick another.");

    private IEnumerable<OrganizerItem> CardsIn(string columnId) =>
        _items.Values.Where(item => item.Kind == OrganizerKinds.Card && item.ParentId == columnId);

    private double PositionIn(string columnId, string? where, string? except = null)
    {
        var positions = CardsIn(columnId).Where(c => c.Id != except).Select(Position).ToList();
        if (positions.Count == 0) return 1;
        return where == "top" ? positions.Min() - 1 : positions.Max() + 1;
    }

    private static double Position(OrganizerItem item) =>
        item.Fields["position"] is JsonValue value && value.TryGetValue<double>(out var position) ? position : 0;

    private static string Title(OrganizerItem item) => $"\"{Text(item, "title") ?? item.Kind}\"";

    private static string Count(int n) => n == 1 ? "1 card" : $"{n} cards";

    private static string? Text(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? Text(OrganizerItem item, string name) => Text(item.Fields, name);

    private static bool Bool(OrganizerItem item, string name) => item.Fields[name]?.GetValueKind() == JsonValueKind.True;

    private static JsonArray Labels(OrganizerItem item) =>
        item.Fields["labels"] is JsonArray labels ? (JsonArray)labels.DeepClone() : [];

    private static IEnumerable<string> LabelNames(OrganizerItem item) =>
        (item.Fields["labels"] as JsonArray ?? []).Select(label => Text(label, "name")).OfType<string>();

    private static bool ChecklistDone(OrganizerItem item) =>
        item.Fields["checklist"] is JsonArray { Count: > 0 } list
        && list.All(entry => entry is JsonObject obj && obj["done"]?.GetValueKind() == JsonValueKind.True);

    // The same colour the dashboard picks for a label with no colour of its own.
    private static JsonObject Label(string name, string? color)
    {
        uint hash = 0;
        foreach (var ch in name) hash = unchecked(hash * 31 + ch);
        return new JsonObject { ["name"] = name, ["color"] = string.IsNullOrWhiteSpace(color) ? Palette[hash % Palette.Length] : color };
    }

    private static JsonArray Checklist(IEnumerable<string> items) =>
        new([.. items.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => (JsonNode)new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["text"] = text.Trim(),
            ["done"] = false,
        })]);

    // A due date `days` from today in the rule's time zone: a date alone, or with a time.
    private static string Due(int days, string? time, RunContext context)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Now, context.Zone).DateTime).AddDays(days);
        if (!OrganizerAutomationSchedule.TryParseTime(time, out var at))
            return today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var local = today.ToDateTime(TimeOnly.FromTimeSpan(at), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, context.Zone.GetUtcOffset(local)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static DateTime Local(DateTimeOffset at, BoardAutomationSchedule schedule) =>
        TimeZoneInfo.ConvertTime(at, OrganizerAutomationSchedule.ZoneOf(schedule)).DateTime;

    // The card's due date the way a person would say it: "Sep 25" or "Sep 25 at 5:00 PM".
    private static string DueWords(RunContext context)
    {
        var due = context.Card is { } card ? Text(card, "due") : null;
        if (due is null || OrganizerAutomationSchedule.DueInstant(due, context.Zone) is not { } at) return "";
        var local = TimeZoneInfo.ConvertTime(at, context.Zone);
        return due.Length == 10 ? local.ToString("MMM d", English) : local.ToString("MMM d 'at' h:mm tt", English);
    }

    // Fills in {date}, {weekday}, {month}, {year}, {time}, {card}, {board} and {due} - the words
    // people can drop into a card title, comment or message, like "Groceries for {weekday}".
    private static string Render(string? template, RunContext context)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var local = TimeZoneInfo.ConvertTime(context.Now, context.Zone);
        return template
            .Replace("{date}", local.ToString("MMM d", English), StringComparison.OrdinalIgnoreCase)
            .Replace("{weekday}", local.ToString("dddd", English), StringComparison.OrdinalIgnoreCase)
            .Replace("{month}", local.ToString("MMMM", English), StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", local.ToString("yyyy", English), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", local.ToString("h:mm tt", English), StringComparison.OrdinalIgnoreCase)
            .Replace("{card}", context.TriggerTitle ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{board}", context.BoardTitle ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{due}", DueWords(context), StringComparison.OrdinalIgnoreCase);
    }
}

// Drives the engine: wakes whenever the Organizer changes, and in time for the next schedule.
public sealed class OrganizerAutomationWorker(
    OrganizerAutomationEngine engine, IOrganizerStore store, AppSettingsFile settings, ILogger<OrganizerAutomationWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan LongestSleep = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.InitializeAsync(stoppingToken);
                var options = await OrganizerEndpoints.ReadOptionsAsync(settings, stoppingToken);
                if (options.Enabled)
                {
                    await engine.ProcessChangesAsync(stoppingToken);
                    await engine.RunDueSchedulesAsync(stoppingToken);
                    await engine.RunDueCardsAsync(stoppingToken);
                }

                var untilSchedule = await engine.TimeUntilNextScheduleAsync(stoppingToken);
                var sleep = untilSchedule is { } due && due < LongestSleep
                    ? (due < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : due)
                    : LongestSleep;
                await store.WaitForChangeAsync(engine.Cursor, sleep, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Board automations hit an error; trying again shortly");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
