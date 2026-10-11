using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SentinelCore.Client.Organizer;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The Windows side of board automations: the wording (a port of organizer-automations.js, so it
/// must read the same as the dashboard), the rule's JSON, the "no card here" check, and the REST
/// calls against a scripted server.
/// </summary>
public sealed class BoardAutomationsTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private static AutomationContext Context(bool people = true) => new()
    {
        ColumnTitles = new Dictionary<string, string> { ["todo"] = "To do", ["doing"] = "Doing", ["done"] = "Done", ["blank"] = "" },
        BoardColumnIds = new[] { "todo", "doing", "done" },
        People = people
            ? new[] { new OrganizerPerson("morgan", "Morgan"), new OrganizerPerson("pat", null), new OrganizerPerson("dylan", "Dylan", HasEmail: false) }
            : Array.Empty<OrganizerPerson>(),
        Culture = Us,
        TimeZone = "America/Chicago",
    };

    private static AutomationSchedule Daily(string time, params int[] days) => new() { Repeat = "daily", Time = time, Days = days.ToList() };

    private static string Words(AutomationTrigger trigger) => BoardAutomationRules.TriggerWords(trigger, Context());

    private static string Words(AutomationAction action) => BoardAutomationRules.ActionWords(action, Context());

    private static string Words(AutomationCondition condition) => BoardAutomationRules.ConditionWords(condition, Context());

    [Fact]
    public void Schedules_read_like_the_dashboard()
    {
        AutomationTrigger On(AutomationSchedule schedule) => new() { Type = "schedule", Schedule = schedule };
        Assert.Equal("Every day at 7:00 AM", Words(On(Daily("07:00"))));
        Assert.Equal("Every weekday at 6:30 PM", Words(On(Daily("18:30", 5, 4, 3, 2, 1))));
        Assert.Equal("Every Saturday and Sunday at 9:00 AM", Words(On(Daily("09:00", 6, 0))));
        Assert.Equal("Every Sun, Wed at 9:00 AM", Words(On(Daily("09:00", 3, 0))));
        Assert.Equal("On day 15 of every month at 7:00 AM", Words(On(new AutomationSchedule { Repeat = "monthly", Time = "07:00", DayOfMonth = 15 })));
        Assert.Equal("On the last day of every month at 7:00 AM", Words(On(new AutomationSchedule { Repeat = "monthly", Time = "07:00", DayOfMonth = -1 })));
        Assert.Equal("Every 3 hours from 8:00 AM until midnight", Words(On(new AutomationSchedule { Repeat = "hourly", Time = "08:00", EveryHours = 3 })));
        Assert.Equal("Every 1 hour from 8:00 AM until midnight", Words(On(new AutomationSchedule { Repeat = "hourly", Time = "08:00" })));
        Assert.Equal("Once, on Tue, Sep 29 at 7:00 AM", Words(On(new AutomationSchedule { Repeat = "once", Time = "07:00", Date = "2026-09-29" })));
        Assert.Equal("Once, on … at 7:00 AM", Words(On(new AutomationSchedule { Repeat = "once", Time = "07:00" })));
        Assert.Equal("On a schedule", Words(On(new AutomationSchedule { Repeat = "yearly" })));
    }

    [Fact]
    public void Every_trigger_has_its_words()
    {
        Assert.Equal("When a card is added to any list", Words(new AutomationTrigger { Type = "cardAdded", ColumnId = "" }));
        Assert.Equal("When a card is added to To do", Words(new AutomationTrigger { Type = "cardAdded", ColumnId = "todo" }));
        Assert.Equal("When a card is moved into Done", Words(new AutomationTrigger { Type = "cardMoved", ColumnId = "done" }));
        Assert.Equal("When a card is moved into any list", Words(new AutomationTrigger { Type = "cardMoved" }));
        Assert.Equal("When a card is marked done", Words(new AutomationTrigger { Type = "cardCompleted" }));
        Assert.Equal("When every checklist item on a card is ticked", Words(new AutomationTrigger { Type = "checklistCompleted" }));
        Assert.Equal("When a card is due", Words(new AutomationTrigger { Type = "cardDue", MinutesBefore = 0 }));
        Assert.Equal("1 hour before a card is due in Doing", Words(new AutomationTrigger { Type = "cardDue", MinutesBefore = 60, ColumnId = "doing" }));
        Assert.Equal("45 minutes before a card is due", Words(new AutomationTrigger { Type = "cardDue", MinutesBefore = 45 }));
        Assert.Equal("When any label is added to a card", Words(new AutomationTrigger { Type = "labelAdded", Label = "" }));
        Assert.Equal("When the “urgent” label is added to a card", Words(new AutomationTrigger { Type = "labelAdded", Label = "urgent" }));
        Assert.Equal("When…", Words(new AutomationTrigger { Type = "somethingNew" }));
    }

    [Fact]
    public void Every_condition_has_its_words()
    {
        Assert.Equal("the title contains “asap”", Words(new AutomationCondition { Type = "titleContains", Text = "asap" }));
        Assert.Equal("the title contains “…”", Words(new AutomationCondition { Type = "titleContains", Text = "" }));
        Assert.Equal("it has the “home” label", Words(new AutomationCondition { Type = "hasLabel", Text = "home" }));
        Assert.Equal("it doesn't have the “home” label", Words(new AutomationCondition { Type = "lacksLabel", Text = "home" }));
        Assert.Equal("it's in Doing", Words(new AutomationCondition { Type = "inColumn", ColumnId = "doing" }));
        Assert.Equal("it's in (pick a list)", Words(new AutomationCondition { Type = "inColumn", ColumnId = "gone" }));
        Assert.Equal("it's Mon, Fri", Words(new AutomationCondition { Type = "onDays", Days = new() { 5, 1 } }));
        Assert.Equal("it's …", Words(new AutomationCondition { Type = "onDays", Days = new() }));
    }

    [Fact]
    public void Every_action_has_its_words()
    {
        Assert.Equal("create a card “eggs” in To do (unless one is still there)",
            Words(new AutomationAction { Type = "createCard", ColumnId = "todo", Title = "eggs", SkipIfExists = true }));
        Assert.Equal("create a card “eggs” in list, due tomorrow at 5:00 PM",
            Words(new AutomationAction { Type = "createCard", ColumnId = "blank", Title = "eggs", InDays = 1, Time = "17:00" }));
        Assert.Equal("move it to the top of Done", Words(new AutomationAction { Type = "moveCard", ColumnId = "done", Position = "top" }));
        Assert.Equal("move it to the bottom of Done", Words(new AutomationAction { Type = "moveCard", ColumnId = "done", Position = "bottom" }));
        Assert.Equal("label it “urgent”", Words(new AutomationAction { Type = "addLabel", Name = "urgent" }));
        Assert.Equal("take off the “urgent” label", Words(new AutomationAction { Type = "removeLabel", Name = "urgent" }));
        Assert.Equal("clear its due date", Words(new AutomationAction { Type = "setDue", InDays = null }));
        Assert.Equal("make it due today", Words(new AutomationAction { Type = "setDue", InDays = 0, Time = "" }));
        Assert.Equal("make it due in 7 days at 9:30 AM", Words(new AutomationAction { Type = "setDue", InDays = 7, Time = "09:30" }));
        Assert.Equal("mark it done", Words(new AutomationAction { Type = "complete" }));
        Assert.Equal("mark it not done", Words(new AutomationAction { Type = "complete", Done = false }));
        Assert.Equal("add some checklist items", Words(new AutomationAction { Type = "addChecklist", Items = new() }));
        Assert.Equal("add 1 checklist item", Words(new AutomationAction { Type = "addChecklist", Items = new() { "a" } }));
        Assert.Equal("add 3 checklist items", Words(new AutomationAction { Type = "addChecklist", Items = new() { "a", "b", "c" } }));
        Assert.Equal("untick its checklist", Words(new AutomationAction { Type = "resetChecklist" }));
        Assert.Equal("comment “finished on {date}”", Words(new AutomationAction { Type = "addComment", Text = "finished on {date}" }));
        Assert.Equal("assign it to pat", Words(new AutomationAction { Type = "assign", Username = "pat" }));
        Assert.Equal("assign it to …", Words(new AutomationAction { Type = "assign", Username = "" }));
        Assert.Equal("delete it", Words(new AutomationAction { Type = "deleteCard" }));
        Assert.Equal("notify everyone “bins”", Words(new AutomationAction { Type = "notify", Everyone = true, Text = "bins" }));
        Assert.Equal("notify Morgan, pat and Dylan “…”", Words(new AutomationAction { Type = "notify", People = new() { "morgan", "pat", "dylan" } }));
        Assert.Equal("notify … “hi”", Words(new AutomationAction { Type = "notify", People = new(), Text = "hi" }));
        Assert.Equal("email Morgan, a@b.com “hi”", Words(new AutomationAction { Type = "email", People = new() { "morgan" }, Addresses = new() { "a@b.com" }, Text = "hi" }));
        Assert.Equal("email … “…”", Words(new AutomationAction { Type = "email", People = new(), Addresses = new() }));
        Assert.Equal("move every card from Done to To do", Words(new AutomationAction { Type = "moveAllCards", ColumnId = "done", ToColumnId = "todo" }));
        Assert.Equal("move every finished card from Done to (pick a list)", Words(new AutomationAction { Type = "moveAllCards", ColumnId = "done", OnlyCompleted = true }));
        Assert.Equal("delete every finished card in Done", Words(new AutomationAction { Type = "deleteAllCards", ColumnId = "done", OnlyCompleted = true }));
        Assert.Equal("delete every card in Done", Words(new AutomationAction { Type = "deleteAllCards", ColumnId = "done" }));
        Assert.Equal("teleport", Words(new AutomationAction { Type = "teleport" }));
    }

    [Fact]
    public void A_rule_reads_as_one_sentence()
    {
        var rule = new BoardAutomation
        {
            Trigger = new AutomationTrigger { Type = "cardMoved", ColumnId = "done" },
            Conditions = { new AutomationCondition { Type = "hasLabel", Text = "home" }, new AutomationCondition { Type = "onDays", Days = new() { 1 } } },
            Actions = { new AutomationAction { Type = "complete" }, new AutomationAction { Type = "addComment", Text = "ok" } },
        };
        Assert.Equal("When a card is moved into Done, if it has the “home” label and it's Mon, mark it done, then comment “ok”.",
            BoardAutomationRules.RuleSentence(rule, Context()));
        Assert.Equal("When…, do….", BoardAutomationRules.RuleSentence(new BoardAutomation(), Context()));
        Assert.Equal("drag a WHEN block and a THEN block into the rule to get started.", BoardAutomationRules.DraftSentence(new BoardAutomation(), Context()));
    }

    [Fact]
    public void Schedule_modes_come_and_go_like_the_dashboard()
    {
        Assert.Equal("day", BoardAutomationRules.ScheduleMode(Daily("07:00")));
        Assert.Equal("weekdays", BoardAutomationRules.ScheduleMode(Daily("07:00", 5, 1, 2, 3, 4)));
        Assert.Equal("weekends", BoardAutomationRules.ScheduleMode(Daily("07:00", 6, 0)));
        Assert.Equal("days", BoardAutomationRules.ScheduleMode(Daily("07:00", 2)));
        Assert.Equal("hourly", BoardAutomationRules.ScheduleMode(new AutomationSchedule { Repeat = "hourly" }));

        var schedule = Daily("07:00");
        BoardAutomationRules.ApplyScheduleMode(schedule, "days", new DateTime(2026, 9, 29));
        Assert.Equal(new[] { 1 }, schedule.Days);
        BoardAutomationRules.ApplyScheduleMode(schedule, "monthly", new DateTime(2026, 9, 29));
        Assert.Equal(("monthly", 1), (schedule.Repeat, schedule.DayOfMonth));
        Assert.Empty(schedule.Days);
        BoardAutomationRules.ApplyScheduleMode(schedule, "hourly", new DateTime(2026, 9, 29));
        Assert.Equal(3, schedule.EveryHours);
        BoardAutomationRules.ApplyScheduleMode(schedule, "once", new DateTime(2026, 9, 29));
        Assert.Equal(("once", "2026-09-30"), (schedule.Repeat, schedule.Date));
        BoardAutomationRules.ApplyScheduleMode(schedule, "weekends", new DateTime(2026, 9, 29));
        Assert.Equal("daily", schedule.Repeat);
        Assert.Equal(new List<int> { 0, 6 }, schedule.Days);

        Assert.Equal(new List<int> { 1, 3 }, BoardAutomationRules.ToggleDay(new[] { 3 }, 1));
        Assert.Equal(new List<int> { 3 }, BoardAutomationRules.ToggleDay(new[] { 1, 3 }, 1));
        Assert.Null(BoardAutomationRules.ToggleDay(new[] { 3 }, 3));
    }

    [Fact]
    public void Times_are_typed_loosely_and_stored_as_hh_mm()
    {
        Assert.Equal("14:30", BoardAutomationRules.ParseTime("14:30", Us));
        Assert.Equal("14:30", BoardAutomationRules.ParseTime("2:30 PM", Us));
        Assert.Equal("07:00", BoardAutomationRules.ParseTime("7", Us));
        Assert.Null(BoardAutomationRules.ParseTime("soon", Us));
        Assert.Null(BoardAutomationRules.ParseTime("", Us));
        Assert.Equal("7:05 AM", BoardAutomationRules.Time12("07:05", Us));
        Assert.Equal("7:00 AM", BoardAutomationRules.Time12("", Us));
        Assert.Equal("19:05", BoardAutomationRules.Time12("19:05", CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void Card_steps_need_a_card()
    {
        var scheduled = new BoardAutomation
        {
            Trigger = new AutomationTrigger { Type = "schedule", Schedule = Daily("07:00") },
            Actions =
            {
                new AutomationAction { Type = "moveCard" },
                new AutomationAction { Type = "createCard" },
                new AutomationAction { Type = "addLabel" },
                new AutomationAction { Type = "deleteCard" },
                new AutomationAction { Type = "complete" },
                new AutomationAction { Type = "notify" },
            },
            Conditions = { new AutomationCondition { Type = "hasLabel" }, new AutomationCondition { Type = "onDays" } },
        };
        Assert.False(BoardAutomationRules.CardAvailableBefore(scheduled, 0));
        Assert.True(BoardAutomationRules.CardAvailableBefore(scheduled, 2));
        Assert.True(BoardAutomationRules.CardAvailableBefore(scheduled, 3));
        Assert.False(BoardAutomationRules.CardAvailableBefore(scheduled, 4));
        Assert.Equal("A schedule has no card of its own - put “Create a card” above this step.", BoardAutomationRules.ActionWarning(scheduled, 0));
        Assert.Null(BoardAutomationRules.ActionWarning(scheduled, 2));
        Assert.NotNull(BoardAutomationRules.ActionWarning(scheduled, 4));
        // Sending needs no card.
        Assert.Null(BoardAutomationRules.ActionWarning(scheduled, 5));
        Assert.Equal("A schedule has no card to check - this would never be true.", BoardAutomationRules.ConditionWarning(scheduled, 0));
        Assert.Null(BoardAutomationRules.ConditionWarning(scheduled, 1));

        var moved = new BoardAutomation
        {
            Trigger = new AutomationTrigger { Type = "cardMoved" },
            Actions = { new AutomationAction { Type = "complete" }, new AutomationAction { Type = "deleteCard" }, new AutomationAction { Type = "addComment" } },
        };
        Assert.True(BoardAutomationRules.CardAvailableBefore(moved, 0));
        Assert.True(BoardAutomationRules.CardAvailableBefore(moved, 1));
        Assert.False(BoardAutomationRules.CardAvailableBefore(moved, 2));
        Assert.Equal("The card was deleted by an earlier step.", BoardAutomationRules.ActionWarning(moved, 2));
    }

    [Fact]
    public void Save_checks_and_reordering()
    {
        Assert.Equal(3, BoardAutomationRules.CheckBeforeSave(new BoardAutomation()).Count);
        var actions = new List<AutomationAction> { new() { Type = "a" }, new() { Type = "b" }, new() { Type = "c" } };
        BoardAutomationRules.MoveAction(actions, 0, 3);
        Assert.Equal("bca", string.Concat(actions.Select(a => a.Type)));
        BoardAutomationRules.MoveAction(actions, 2, 0);
        Assert.Equal("abc", string.Concat(actions.Select(a => a.Type)));
        // Down one, as the ▼ button does it: an insertion point two further on.
        BoardAutomationRules.MoveAction(actions, 0, 2);
        Assert.Equal("bac", string.Concat(actions.Select(a => a.Type)));
    }

    [Fact]
    public void Text_helpers()
    {
        Assert.Equal(new List<string> { "a", "b c" }, BoardAutomationRules.Csv(" a, ,b c ,"));
        Assert.Equal(new List<string> { "one", "two" }, BoardAutomationRules.Lines("one\r\n\ntwo\n"));
        Assert.Equal(("eggs {date}!", 11), BoardAutomationRules.InsertToken("eggs !", 5, 0, "{date}"));
        Assert.Equal(("{card} due", 6), BoardAutomationRules.InsertToken("X due", 0, 1, "{card}"));
    }

    [Fact]
    public void New_blocks_and_recipes()
    {
        var context = Context(people: false);
        Assert.True(BoardAutomationRules.MakeAction("notify", context).Everyone);
        Assert.False(BoardAutomationRules.MakeAction("notify", Context()).Everyone);
        Assert.Equal("morgan", BoardAutomationRules.MakeAction("assign", Context()).Username);
        Assert.Equal(("todo", "done"), (BoardAutomationRules.MakeAction("moveAllCards", context).ToColumnId, BoardAutomationRules.MakeAction("moveAllCards", context).ColumnId));
        Assert.Equal("America/Chicago", BoardAutomationRules.MakeTrigger("schedule", context).Schedule!.TimeZone);
        Assert.Equal("done", BoardAutomationRules.MakeTrigger("cardMoved", context).ColumnId);

        var recipes = BoardAutomationRules.Recipes("board-1", context);
        Assert.Equal(8, recipes.Count);
        Assert.All(recipes, recipe => Assert.Equal("board-1", recipe.Rule.BoardId));
        Assert.Equal("Moved to Done → done", recipes[2].Rule.Name);
        Assert.Equal("Every Sun at 9:00 AM, create a card “Chores for {date}” in To do (unless one is still there).",
            BoardAutomationRules.RuleSentence(recipes[1].Rule, context));
        Assert.All(AutomationTypes.Actions, type => Assert.NotNull(BoardAutomationRules.Block("actions", type)));
        Assert.All(AutomationTypes.Triggers, type => Assert.NotNull(BoardAutomationRules.Block("trigger", type)));
        Assert.All(AutomationTypes.Conditions, type => Assert.NotNull(BoardAutomationRules.Block("conditions", type)));
    }

    [Fact]
    public void Json_round_trips_including_nulls_and_fields_it_does_not_know()
    {
        const string json = """
            {"id":"r1","name":"Eggs","enabled":false,"boardId":"b1","timeZone":"America/Chicago","runAs":"morgan",
             "createdAt":"2026-09-24T12:00:00+00:00","nextRunAt":null,"lastOk":true,"runCount":4,"futureThing":{"x":1},
             "trigger":{"type":"schedule","columnId":null,"schedule":{"repeat":"daily","time":"07:00","days":[1,2],"timeZone":"America/Chicago","jitter":5}},
             "conditions":[{"type":"onDays","days":[1],"note":"kept"}],
             "actions":[{"type":"setDue","inDays":null,"time":null},{"type":"createCard","columnId":"c1","title":"eggs","items":["a"],"skipIfExists":true,"sparkle":[1,2]},
                        {"type":"complete"},{"type":"teleport","where":"moon"}]}
            """;
        var rule = BoardAutomationJson.Read<BoardAutomation>(json)!;
        Assert.False(rule.Enabled);
        Assert.Null(rule.Actions[0].InDays);
        Assert.Null(rule.NextRunAt);
        // No "done" means done, as on the server.
        Assert.True(rule.Actions[2].Done);
        Assert.Equal(new List<int> { 1, 2 }, rule.Trigger!.Schedule!.Days);

        var back = JsonNode.Parse(BoardAutomationJson.Write(rule))!.AsObject();
        Assert.Equal(1, back["futureThing"]!["x"]!.GetValue<int>());
        Assert.Equal(5, back["trigger"]!["schedule"]!["jitter"]!.GetValue<int>());
        Assert.Equal("kept", back["conditions"]![0]!["note"]!.GetValue<string>());
        Assert.Equal("[1,2]", back["actions"]![1]!["sparkle"]!.ToJsonString());
        Assert.Equal("moon", back["actions"]![3]!["where"]!.GetValue<string>());
        // Nulls are left out - the server reads missing as null, and a null createdAt would be a 400.
        Assert.False(back["actions"]![0]!.AsObject().ContainsKey("inDays"));
        Assert.False(back.ContainsKey("nextRunAt"));
        Assert.Equal("2026-09-24T12:00:00+00:00", back["createdAt"]!.GetValue<string>());

        var again = BoardAutomationJson.Read<BoardAutomation>(back.ToJsonString())!;
        Assert.Null(again.Actions[0].InDays);
        Assert.Equal(BoardAutomationJson.Write(rule), BoardAutomationJson.Write(again));
        Assert.Equal(BoardAutomationJson.Write(rule), BoardAutomationJson.Write(rule.Clone()));

        var fresh = JsonNode.Parse(BoardAutomationJson.Write(new BoardAutomation { BoardId = "b" }))!.AsObject();
        Assert.False(fresh.ContainsKey("createdAt"));
        Assert.False(fresh.ContainsKey("trigger"));
    }

    [Fact]
    public async Task Api_speaks_the_servers_rest()
    {
        var requests = new List<(string Method, string Path, IReadOnlyDictionary<string, string>? Query, string? Body)>();
        OrganizerResponse Reply(string text, int status = 200) => new(status, Encoding.UTF8.GetBytes(text));
        Func<string, string, OrganizerResponse> handler = (_, _) => Reply("{}");
        var api = new BoardAutomationsApi((method, path, query, body) =>
        {
            requests.Add((method, path, query, body is null ? null : Encoding.UTF8.GetString(body)));
            return Task.FromResult(handler(method, path));
        });

        handler = (_, _) => Reply("""{"automations":[{"id":"r1","name":"Eggs","boardId":"b1","trigger":{"type":"cardCompleted"},"conditions":[],"actions":[]}],"timeZone":"UTC","pushReady":false}""");
        var list = await api.ListAsync("b1");
        Assert.Equal("r1", Assert.Single(list.Automations).Id);
        Assert.False(list.PushReady);
        Assert.True(list.EmailReady);
        Assert.Equal(("GET", "/api/organizer/automations", "b1"), (requests[^1].Method, requests[^1].Path, requests[^1].Query!["boardId"]));

        handler = (_, _) => Reply("""{"id":"new","name":"Eggs","boardId":"b1"}""");
        var saved = await api.SaveAsync(new BoardAutomation { Name = "Eggs", BoardId = "b1", Trigger = new AutomationTrigger { Type = "cardCompleted" } }, "America/Chicago");
        Assert.Equal("new", saved.Id);
        Assert.Equal(("POST", "/api/organizer/automations"), (requests[^1].Method, requests[^1].Path));
        var sent = JsonNode.Parse(requests[^1].Body!)!.AsObject();
        Assert.Equal("America/Chicago", sent["timeZone"]!.GetValue<string>());
        Assert.False(sent.ContainsKey("id"));

        await api.SaveAsync(new BoardAutomation { Id = "r 1", Name = "Eggs", BoardId = "b1" }, "UTC");
        Assert.Equal(("PUT", "/api/organizer/automations/r%201"), (requests[^1].Method, requests[^1].Path));

        await api.SetEnabledAsync(new BoardAutomation { Id = "r1", Name = "Eggs", Enabled = true, RunCount = 3 }, false);
        var toggled = JsonNode.Parse(requests[^1].Body!)!.AsObject();
        Assert.False(toggled["enabled"]!.GetValue<bool>());
        Assert.Equal(3, toggled["runCount"]!.GetValue<int>());

        handler = (_, _) => Reply("""{"automationId":"r1","at":"2026-09-29T12:00:00Z","ok":true,"message":"Made 1 card."}""");
        var run = await api.RunNowAsync("r1");
        Assert.Equal(("Made 1 card.", true), (run.Message, run.Ok));
        Assert.Equal("/api/organizer/automations/r1/run", requests[^1].Path);

        handler = (_, _) => Reply("""{"runs":[{"automationId":"r1","at":"2026-09-29T12:00:00Z","ok":false,"message":"No list."}]}""");
        Assert.False(Assert.Single(await api.RunsAsync("r1")).Ok);

        handler = (_, _) => Reply("""{"runs":["2026-09-30T12:00:00Z","2026-10-01T12:00:00Z"]}""");
        var next = await api.PreviewAsync(new AutomationSchedule { Repeat = "daily", Time = "07:00" });
        Assert.Equal(2, next.Count);
        Assert.Equal("/api/organizer/automations/preview", requests[^1].Path);
        Assert.Equal("07:00", JsonNode.Parse(requests[^1].Body!)!["time"]!.GetValue<string>());

        handler = (_, _) => Reply("""{"deleted":true}""");
        await api.DeleteAsync("r1");
        Assert.Equal(("DELETE", "/api/organizer/automations/r1"), (requests[^1].Method, requests[^1].Path));

        handler = (_, _) => Reply("""{"errors":["Give the rule a name.","Pick a time of day."]}""", 400);
        var error = await Assert.ThrowsAsync<BoardAutomationException>(() => api.SaveAsync(new BoardAutomation(), "UTC"));
        Assert.Equal(new[] { "Give the rule a name.", "Pick a time of day." }, error.Errors);

        handler = (_, _) => Reply("<html>bad gateway</html>", 502);
        Assert.Equal("The server said 502.", Assert.Single((await Assert.ThrowsAsync<BoardAutomationException>(() => api.ListAsync("b1"))).Errors));

        handler = (_, _) => Reply("", 401);
        await Assert.ThrowsAsync<OrganizerSignInRequiredException>(() => api.ListAsync("b1"));
    }

    [Fact]
    public async Task Engine_without_a_connection_says_offline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sc-automations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteOrganizerStore(directory);
            using var engine = new OrganizerEngine(store, "device", OfflinePolicy.FullDefault);
            await Assert.ThrowsAsync<OrganizerOfflineException>(() => engine.Automations.ListAsync("b1"));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
