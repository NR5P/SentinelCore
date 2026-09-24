using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Board automations: when schedules fire, and what the engine does to a real Organizer store when
// they do - or when a card moves. The part most worth guarding is what must NOT happen: a rule
// setting itself off, a card appearing twice, a restored server "adding" every card at once.
public sealed class OrganizerAutomationTests : IDisposable
{
    private const string Chicago = "America/Chicago";
    private readonly List<string> _directories = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        foreach (var directory in _directories)
        {
            try { TempDirectories.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // What a rule would have sent, and whether sending "works".
    private sealed class FakeNotifier : IOrganizerAutomationNotifier
    {
        public sealed record Push(IReadOnlyCollection<string>? To, string Title, string Body, string? ItemId);
        public sealed record Email(IReadOnlyCollection<string> People, IReadOnlyCollection<string> Addresses, string Subject, string Body);

        public List<Push> Pushes { get; } = [];
        public List<Email> Emails { get; } = [];
        public bool Works { get; set; } = true;

        public Task<AutomationChannels> ChannelsAsync(CancellationToken cancellationToken) => Task.FromResult(new AutomationChannels(Works, Works));

        public Task<AutomationSendResult> PushAsync(IReadOnlyCollection<string>? usernames, string title, string body, string? organizerItemId, CancellationToken cancellationToken)
        {
            Pushes.Add(new Push(usernames is null ? null : [.. usernames], title, body, organizerItemId));
            return Task.FromResult(Works ? new AutomationSendResult(true, "Notified.") : new AutomationSendResult(false, "Push isn't set up."));
        }

        public Task<AutomationSendResult> EmailAsync(IReadOnlyCollection<string> usernames, IReadOnlyCollection<string> addresses, string subject, string body, CancellationToken cancellationToken)
        {
            Emails.Add(new Email([.. usernames], [.. addresses], subject, body));
            return Task.FromResult(Works ? new AutomationSendResult(true, "Emailed.") : new AutomationSendResult(false, "Email isn't set up."));
        }
    }

    private sealed class Bench
    {
        public required SqliteOrganizerStore Store { get; init; }
        public required FakeNotifier Notifier { get; init; }
        public required SqliteOrganizerAutomationStore Automations { get; init; }
        public required OrganizerAutomationEngine Engine { get; init; }
        public required ManualTime Time { get; init; }
        public int Nudges;

        public List<OrganizerItem> Cards(string columnId) =>
            [.. Store.GetItemsAsync(OrganizerUser.Shared, OrganizerKinds.Card, null, CancellationToken.None).Result.Where(c => c.ParentId == columnId)];

        public OrganizerItem Get(string id) =>
            Store.PullEverythingAsync(0, 10000, CancellationToken.None).Result.Items.Single(item => item.Id == id);
    }

    private async Task<Bench> NewBenchAsync(DateTimeOffset? now = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"organizer-automation-{Guid.NewGuid():N}");
        _directories.Add(directory);
        var store = new SqliteOrganizerStore(directory);
        var automations = new SqliteOrganizerAutomationStore(directory);
        _disposables.Add(store);
        _disposables.Add(automations);
        var time = new ManualTime(now ?? DateTimeOffset.UtcNow);
        var notifier = new FakeNotifier();
        Bench? bench = null;
        var engine = new OrganizerAutomationEngine(store, automations, notifier, time, () => bench!.Nudges++, NullLogger.Instance);
        bench = new Bench { Store = store, Automations = automations, Engine = engine, Time = time, Notifier = notifier };

        // A board with three lists, made by a person before the engine starts.
        await PushAsync(store, Change("board", "board", ("spaceId", "family"), ("title", "Home")));
        await PushAsync(store,
            Change("new", "column", ("parentId", "board"), ("title", "New items"), ("position", 1)),
            Change("doing", "column", ("parentId", "board"), ("title", "Doing"), ("position", 2)),
            Change("done", "column", ("parentId", "board"), ("title", "Done"), ("position", 3)));
        await engine.InitializeAsync(Ct);
        return bench;
    }

    // The counter keeps two edits made in the same millisecond in order, as a real device's would.
    private static int _counter;
    private static string Clock(DateTimeOffset? at = null) =>
        $"{(at ?? DateTimeOffset.UtcNow.AddSeconds(-1)).ToUnixTimeMilliseconds():D13}-{Interlocked.Increment(ref _counter) % 10000:D4}-phone";

    private static OrganizerChange Change(string id, string kind, params (string Name, JsonNode? Value)[] fields) =>
        ChangeAt(null, id, kind, fields);

    private static OrganizerChange ChangeAt(DateTimeOffset? at, string id, string kind, params (string Name, JsonNode? Value)[] fields)
    {
        var change = new OrganizerChange { Id = id, Kind = kind };
        var clock = Clock(at);
        foreach (var (name, value) in fields)
        {
            change.Fields[name] = value;
            change.Clocks[name] = clock;
        }
        return change;
    }

    private static async Task PushAsync(SqliteOrganizerStore store, params OrganizerChange[] changes)
    {
        var result = await store.ApplyChangesAsync(OrganizerUser.Shared, changes, null, CancellationToken.None);
        Assert.Empty(result.Rejected);
    }

    private static OrganizerAutomation EggsEveryMorning() => new()
    {
        Id = "eggs",
        Name = "Eggs",
        BoardId = "board",
        Trigger = new BoardAutomationTrigger
        {
            Type = OrganizerAutomationTypes.Schedule,
            Schedule = new BoardAutomationSchedule { Repeat = "daily", Time = "07:00", TimeZone = Chicago },
        },
        Actions = [new BoardAutomationAction { Type = "createCard", ColumnId = "new", Title = "pick up eggs", SkipIfExists = true }],
    };

    // ---- schedules ----------------------------------------------------------------------------

    [Fact]
    public void Daily_RunsAtSevenLocal_TodayIfNotYetPast_OtherwiseTomorrow()
    {
        var schedule = new BoardAutomationSchedule { Repeat = "daily", Time = "07:00", TimeZone = Chicago };
        // 6:30 CDT on 24 Sep 2026 = 11:30 UTC.
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T12:00:00Z"), OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.Parse("2026-09-24T11:30:00Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T12:00:00Z"), OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.Parse("2026-09-24T12:00:00Z")));
    }

    [Fact]
    public void Daily_StaysAtSevenAcrossTheEndOfDaylightSaving()
    {
        // US DST ends 1 Nov 2026: 7:00 CDT is 12:00 UTC, 7:00 CST is 13:00 UTC.
        var schedule = new BoardAutomationSchedule { Repeat = "daily", Time = "07:00", TimeZone = Chicago };
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T13:00:00Z"), OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.Parse("2026-10-31T12:30:00Z")));
    }

    [Fact]
    public void Daily_WithDays_SkipsToTheNextChosenDay()
    {
        // Weekdays only; 25 Sep 2026 is a Friday, so after Friday's run comes Monday the 28th.
        var schedule = new BoardAutomationSchedule { Repeat = "daily", Time = "07:00", TimeZone = Chicago, Days = [1, 2, 3, 4, 5] };
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z"), OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.Parse("2026-09-25T12:00:00Z")));
    }

    [Fact]
    public void Monthly_LastDay_And_The31st_InAShortMonth()
    {
        var last = new BoardAutomationSchedule { Repeat = "monthly", Time = "09:00", TimeZone = "UTC", DayOfMonth = -1 };
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T09:00:00Z"), OrganizerAutomationSchedule.Next(last, DateTimeOffset.Parse("2026-09-24T00:00:00Z")));
        var thirtyFirst = new BoardAutomationSchedule { Repeat = "monthly", Time = "09:00", TimeZone = "UTC", DayOfMonth = 31 };
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T09:00:00Z"), OrganizerAutomationSchedule.Next(thirtyFirst, DateTimeOffset.Parse("2026-09-24T00:00:00Z")));
    }

    [Fact]
    public void Hourly_EveryThreeHoursFromEight()
    {
        var schedule = new BoardAutomationSchedule { Repeat = "hourly", Time = "08:00", EveryHours = 3, TimeZone = "UTC" };
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T11:00:00Z"), OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.Parse("2026-09-24T08:00:00Z")));
        // After 23:00 the next is 8:00 the following day, not 2:00.
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T08:00:00Z"), OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.Parse("2026-09-24T23:00:00Z")));
    }

    [Fact]
    public void Once_InThePast_NeverRuns()
    {
        var schedule = new BoardAutomationSchedule { Repeat = "once", Date = "2026-09-20", Time = "07:00", TimeZone = "UTC" };
        Assert.Null(OrganizerAutomationSchedule.Next(schedule, DateTimeOffset.Parse("2026-09-24T00:00:00Z")));
    }

    // ---- the engine: schedules ----------------------------------------------------------------

    [Fact]
    public async Task ScheduledCard_AppearsWhenDue_AndOnlyOnce()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(EggsEveryMorning(), Ct);
        var due = (await bench.Automations.GetAsync("eggs", Ct))!.NextRunAt!.Value;

        bench.Time.Now = due.AddSeconds(-5);
        await bench.Engine.RunDueSchedulesAsync(Ct);
        Assert.Empty(bench.Cards("new"));

        bench.Time.Now = due.AddSeconds(1);
        await bench.Engine.RunDueSchedulesAsync(Ct);
        await bench.Engine.RunDueSchedulesAsync(Ct);
        var card = Assert.Single(bench.Cards("new"));
        Assert.Equal("pick up eggs", card.Fields["title"]!.GetValue<string>());
        Assert.Equal(1, bench.Nudges);

        var rule = (await bench.Automations.GetAsync("eggs", Ct))!;
        Assert.Equal(due.AddDays(1), rule.NextRunAt);
        Assert.True(rule.LastOk);
    }

    [Fact]
    public async Task SkipIfExists_DoesNotPileUpUnfinishedCards_ButDoesOnceTheOldOneIsDone()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(EggsEveryMorning(), Ct);

        Assert.True((await bench.Engine.RunNowAsync("eggs", Ct))!.Ok);
        Assert.True((await bench.Engine.RunNowAsync("eggs", Ct))!.Ok);
        var first = Assert.Single(bench.Cards("new"));

        // A person ticks it off afterwards - later than the rule made it.
        await PushAsync(bench.Store, ChangeAt(DateTimeOffset.UtcNow.AddSeconds(2), first.Id, "card", ("completed", true)));
        await bench.Engine.RunNowAsync("eggs", Ct);
        Assert.Equal(2, bench.Cards("new").Count);
    }

    [Fact]
    public async Task ARunMissedByMoreThanTheGrace_IsSkipped_NotDoneLate()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(EggsEveryMorning(), Ct);
        var due = (await bench.Automations.GetAsync("eggs", Ct))!.NextRunAt!.Value;

        bench.Time.Now = due + OrganizerAutomationEngine.MissedRunGrace + TimeSpan.FromMinutes(1);
        await bench.Engine.RunDueSchedulesAsync(Ct);

        Assert.Empty(bench.Cards("new"));
        var rule = (await bench.Automations.GetAsync("eggs", Ct))!;
        Assert.False(rule.LastOk);
        Assert.True(rule.NextRunAt > bench.Time.Now);
    }

    [Fact]
    public async Task CreateThenLabel_StepsAfterCreateActOnTheNewCard_WithTheDateFilledIn()
    {
        var bench = await NewBenchAsync(DateTimeOffset.Parse("2026-09-24T15:00:00Z"));
        var rule = EggsEveryMorning();
        rule.Actions =
        [
            new BoardAutomationAction { Type = "createCard", ColumnId = "new", Title = "Groceries for {weekday}", InDays = 1, Time = "18:00" },
            new BoardAutomationAction { Type = "addLabel", Name = "errand" },
            new BoardAutomationAction { Type = "addChecklist", Items = ["eggs", "milk"] },
        ];
        await bench.Engine.SaveAsync(rule, Ct);
        var run = (await bench.Engine.RunNowAsync("eggs", Ct))!;
        Assert.True(run.Ok, run.Message);

        var card = Assert.Single(bench.Cards("new"));
        Assert.Equal("Groceries for Thursday", card.Fields["title"]!.GetValue<string>());
        Assert.Equal("2026-09-25T18:00:00-05:00", card.Fields["due"]!.GetValue<string>());
        Assert.Equal("errand", card.Fields["labels"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(2, card.Fields["checklist"]!.AsArray().Count);
    }

    [Fact]
    public async Task ClearingDone_EverySunday_DeletesOnlyFinishedCards()
    {
        var bench = await NewBenchAsync();
        await PushAsync(bench.Store,
            Change("c1", "card", ("parentId", "done"), ("title", "a"), ("completed", true)),
            Change("c2", "card", ("parentId", "done"), ("title", "b"), ("completed", false)));
        await bench.Engine.ProcessChangesAsync(Ct);

        var rule = EggsEveryMorning();
        rule.Actions = [new BoardAutomationAction { Type = "deleteAllCards", ColumnId = "done", OnlyCompleted = true }];
        await bench.Engine.SaveAsync(rule, Ct);
        await bench.Engine.RunNowAsync("eggs", Ct);

        Assert.Equal(["c2"], bench.Cards("done").Select(c => c.Id));
    }

    // ---- the engine: card rules ---------------------------------------------------------------

    private static OrganizerAutomation MovedToDone() => new()
    {
        Id = "finish",
        Name = "Finish",
        BoardId = "board",
        Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardMoved, ColumnId = "done" },
        Actions =
        [
            new BoardAutomationAction { Type = "complete", Done = true },
            new BoardAutomationAction { Type = "moveCard", ColumnId = "done", Position = "top" },
        ],
    };

    [Fact]
    public async Task MovingACardIntoDone_MarksItComplete()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(MovedToDone(), Ct);
        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "paint"), ("position", 1)));
        await bench.Engine.ProcessChangesAsync(Ct);
        Assert.False(bench.Get("c1").Fields["completed"]?.GetValue<bool>() ?? false);

        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "done")));
        await bench.Engine.ProcessChangesAsync(Ct);

        Assert.True(bench.Get("c1").Fields["completed"]!.GetValue<bool>());
        Assert.Equal(1, (await bench.Automations.GetAsync("finish", Ct))!.RunCount);
    }

    [Fact]
    public async Task ARulesOwnChanges_NeverSetOffRules()
    {
        var bench = await NewBenchAsync();
        // Ping-pong: into Doing -> move to Done, into Done -> move to Doing. Only the person's
        // move may fire anything, so each rule runs at most once.
        await bench.Engine.SaveAsync(new OrganizerAutomation
        {
            Id = "a", Name = "a", BoardId = "board",
            Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardMoved, ColumnId = "doing" },
            Actions = [new BoardAutomationAction { Type = "moveCard", ColumnId = "done" }],
        }, Ct);
        await bench.Engine.SaveAsync(new OrganizerAutomation
        {
            Id = "b", Name = "b", BoardId = "board",
            Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardMoved, ColumnId = "done" },
            Actions = [new BoardAutomationAction { Type = "moveCard", ColumnId = "doing" }],
        }, Ct);

        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "x")));
        await bench.Engine.ProcessChangesAsync(Ct);
        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "doing")));
        for (var i = 0; i < 5; i++) await bench.Engine.ProcessChangesAsync(Ct);

        Assert.Equal("done", bench.Get("c1").ParentId);
        Assert.Equal(1, (await bench.Automations.GetAsync("a", Ct))!.RunCount);
        Assert.Equal(0, (await bench.Automations.GetAsync("b", Ct))!.RunCount);
    }

    [Fact]
    public async Task ARuleChangeWins_EvenOverADeviceWhoseClockRunsAhead()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(MovedToDone(), Ct);
        var ahead = DateTimeOffset.UtcNow.AddMinutes(2);
        await PushAsync(bench.Store, ChangeAt(ahead, "c1", "card", ("parentId", "new"), ("title", "x"), ("completed", false)));
        await bench.Engine.ProcessChangesAsync(Ct);
        await PushAsync(bench.Store, ChangeAt(ahead.AddSeconds(1), "c1", "card", ("parentId", "done")));
        await bench.Engine.ProcessChangesAsync(Ct);

        Assert.True(bench.Get("c1").Fields["completed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CardAdded_WithACondition_FiresOnlyForMatchingCards()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(new OrganizerAutomation
        {
            Id = "urgent", Name = "Urgent", BoardId = "board",
            Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardAdded, ColumnId = "new" },
            Conditions = [new BoardAutomationCondition { Type = "titleContains", Text = "asap" }],
            Actions = [new BoardAutomationAction { Type = "addLabel", Name = "urgent", Color = "#ff4d5e" }, new BoardAutomationAction { Type = "setDue", InDays = 0 }],
        }, Ct);

        await PushAsync(bench.Store,
            Change("c1", "card", ("parentId", "new"), ("title", "call plumber ASAP")),
            Change("c2", "card", ("parentId", "new"), ("title", "someday")));
        await bench.Engine.ProcessChangesAsync(Ct);

        Assert.Equal("urgent", bench.Get("c1").Fields["labels"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(bench.Get("c1").Fields["due"]);
        Assert.Null(bench.Get("c2").Fields["labels"]);
    }

    [Fact]
    public async Task CardsArrivingLongAfterTheyWereMade_DontFireRules()
    {
        // A rebuilt server being refilled from a phone: every card is "new" to it.
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(new OrganizerAutomation
        {
            Id = "any", Name = "any", BoardId = "board",
            Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardAdded },
            Actions = [new BoardAutomationAction { Type = "addComment", Text = "hello" }],
        }, Ct);

        var old = DateTimeOffset.UtcNow - OrganizerAutomationEngine.StaleChange - TimeSpan.FromHours(1);
        await PushAsync(bench.Store, ChangeAt(old, "c1", "card", ("parentId", "new"), ("title", "old")));
        await bench.Engine.ProcessChangesAsync(Ct);

        Assert.Equal(0, (await bench.Automations.GetAsync("any", Ct))!.RunCount);
    }

    [Fact]
    public async Task ARuleOnOneBoard_IgnoresCardsOnAnother()
    {
        var bench = await NewBenchAsync();
        await PushAsync(bench.Store, Change("board2", "board", ("spaceId", "family"), ("title", "Work")));
        await PushAsync(bench.Store, Change("other", "column", ("parentId", "board2"), ("title", "Inbox")));
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.SaveAsync(new OrganizerAutomation
        {
            Id = "any", Name = "any", BoardId = "board",
            Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardAdded },
            Actions = [new BoardAutomationAction { Type = "complete" }],
        }, Ct);

        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "other"), ("title", "x")));
        await bench.Engine.ProcessChangesAsync(Ct);
        Assert.Equal(0, (await bench.Automations.GetAsync("any", Ct))!.RunCount);
    }

    [Fact]
    public async Task ARuleWhoseListWasDeleted_FailsWithAMessage_InsteadOfThrowing()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(EggsEveryMorning(), Ct);
        await PushAsync(bench.Store, Change("new", "column", ("deleted", true)));

        var run = (await bench.Engine.RunNowAsync("eggs", Ct))!;
        Assert.False(run.Ok);
        Assert.Contains("deleted", run.Message);
        Assert.Single(await bench.Automations.GetRunsAsync("eggs", Ct));
    }

    // ---- sending: notifications and email -----------------------------------------------------

    private static OrganizerAutomation WhenAddedTo(string id, string columnId, params BoardAutomationAction[] actions) => new()
    {
        Id = id,
        Name = id,
        BoardId = "board",
        RunAs = "nathan",
        TimeZone = Chicago,
        Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardAdded, ColumnId = columnId },
        Actions = [.. actions],
    };

    [Fact]
    public async Task Notify_GoesToTheChosenPeople_WithTheWordsFilledIn_AndTheCardToOpen()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(WhenAddedTo("tell", "new", new BoardAutomationAction
        {
            Type = "notify", People = ["terry", "kid"], Title = "{board}", Text = "New: {card}",
        }), Ct);

        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "buy milk")));
        await bench.Engine.ProcessChangesAsync(Ct);

        var push = Assert.Single(bench.Notifier.Pushes);
        Assert.Equal(["terry", "kid"], push.To);
        Assert.Equal("Home", push.Title);
        Assert.Equal("New: buy milk", push.Body);
        Assert.Equal("c1", push.ItemId);
        Assert.True((await bench.Automations.GetAsync("tell", Ct))!.LastOk);
    }

    [Fact]
    public async Task Notify_Everyone_SendsToAllPhones_AndWithNoCard_OpensTheBoard()
    {
        var bench = await NewBenchAsync();
        var rule = EggsEveryMorning();
        rule.Actions = [new BoardAutomationAction { Type = "notify", Everyone = true, Text = "Bins out tonight" }];
        await bench.Engine.SaveAsync(rule, Ct);

        Assert.True((await bench.Engine.RunNowAsync("eggs", Ct))!.Ok);

        var push = Assert.Single(bench.Notifier.Pushes);
        Assert.Null(push.To);
        Assert.Equal("Home", push.Title); // no title given: the board's name
        Assert.Equal("board", push.ItemId);
    }

    [Fact]
    public async Task Email_HasASubjectEvenIfNoneWasGiven_AndSaysWhichRuleSentIt()
    {
        var bench = await NewBenchAsync();
        var rule = EggsEveryMorning();
        rule.Actions = [new BoardAutomationAction { Type = "email", People = ["terry"], Addresses = ["grandma@example.com"], Text = "Eggs on {weekday}" }];
        await bench.Engine.SaveAsync(rule, Ct);

        await bench.Engine.RunNowAsync("eggs", Ct);

        var email = Assert.Single(bench.Notifier.Emails);
        Assert.Equal(["terry"], email.People);
        Assert.Equal(["grandma@example.com"], email.Addresses);
        Assert.Equal("Home: Eggs", email.Subject);
        Assert.StartsWith("Eggs on ", email.Body);
        Assert.Contains("Sent by the board automation \"Eggs\" on Home", email.Body);
    }

    [Fact]
    public async Task ASendThatFails_MarksTheRunAsAProblem_ButTheOtherStepsStillHappen()
    {
        var bench = await NewBenchAsync();
        bench.Notifier.Works = false;
        await bench.Engine.SaveAsync(WhenAddedTo("both", "new",
            new BoardAutomationAction { Type = "notify", Everyone = true, Text = "hi" },
            new BoardAutomationAction { Type = "addLabel", Name = "seen" }), Ct);

        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "x")));
        await bench.Engine.ProcessChangesAsync(Ct);

        Assert.Equal("seen", bench.Get("c1").Fields["labels"]![0]!["name"]!.GetValue<string>());
        var rule = (await bench.Automations.GetAsync("both", Ct))!;
        Assert.False(rule.LastOk);
        Assert.Contains("Push isn't set up.", rule.LastResult);
    }

    [Fact]
    public async Task ARuleSendsAtMostTwentyAnHour_EvenWhenFiftyCardsArriveAtOnce()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(WhenAddedTo("chatty", "new", new BoardAutomationAction { Type = "notify", Everyone = true, Text = "{card}" }), Ct);

        await PushAsync(bench.Store, [.. Enumerable.Range(0, 50).Select(i => Change($"c{i}", "card", ("parentId", "new"), ("title", $"card {i}")))]);
        await bench.Engine.ProcessChangesAsync(Ct);

        Assert.Equal(OrganizerAutomationEngine.MaxSendsPerRulePerHour, bench.Notifier.Pushes.Count);
        var rule = (await bench.Automations.GetAsync("chatty", Ct))!;
        Assert.False(rule.LastOk);
        Assert.Contains("already sent 20", rule.LastResult);

        // An hour later it may send again.
        bench.Time.Now += TimeSpan.FromHours(1);
        await PushAsync(bench.Store, ChangeAt(DateTimeOffset.UtcNow.AddSeconds(2), "late", "card", ("parentId", "new"), ("title", "late")));
        await bench.Engine.ProcessChangesAsync(Ct);
        Assert.Equal(OrganizerAutomationEngine.MaxSendsPerRulePerHour + 1, bench.Notifier.Pushes.Count);
    }

    [Fact]
    public async Task TheSendLimitIsPerRule()
    {
        var bench = await NewBenchAsync();
        await bench.Engine.SaveAsync(WhenAddedTo("one", "new", new BoardAutomationAction { Type = "notify", Everyone = true, Text = "a" }), Ct);
        await bench.Engine.SaveAsync(WhenAddedTo("two", "new", new BoardAutomationAction { Type = "email", Addresses = ["x@example.com"], Text = "b" }), Ct);

        await PushAsync(bench.Store, [.. Enumerable.Range(0, 25).Select(i => Change($"c{i}", "card", ("parentId", "new"), ("title", $"card {i}")))]);
        await bench.Engine.ProcessChangesAsync(Ct);

        Assert.Equal(20, bench.Notifier.Pushes.Count);
        Assert.Equal(20, bench.Notifier.Emails.Count);
    }

    // ---- when a card is due -------------------------------------------------------------------

    private static OrganizerAutomation WhenDue(int minutesBefore, string? columnId = null) => new()
    {
        Id = "due",
        Name = "Due",
        BoardId = "board",
        RunAs = "nathan",
        TimeZone = Chicago,
        Trigger = new BoardAutomationTrigger { Type = OrganizerAutomationTypes.CardDue, MinutesBefore = minutesBefore, ColumnId = columnId },
        Actions = [new BoardAutomationAction { Type = "notify", Everyone = true, Text = "{card} is due {due}" }],
    };

    [Fact]
    public async Task DueTrigger_FiresAtTheRightMoment_AndOnlyOnce()
    {
        var start = DateTimeOffset.UtcNow;
        var bench = await NewBenchAsync(start);
        var due = start.AddHours(3);
        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "Dentist"),
            ("due", due.ToOffset(TimeSpan.FromHours(-5)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"))));
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.SaveAsync(WhenDue(60), Ct);

        bench.Time.Now = due.AddHours(-1).AddSeconds(-5);
        await bench.Engine.RunDueCardsAsync(Ct);
        Assert.Empty(bench.Notifier.Pushes);
        var wait = await bench.Engine.TimeUntilNextScheduleAsync(Ct);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalSeconds, 4, 6); // the loop wakes for it on time

        bench.Time.Now = due.AddHours(-1).AddSeconds(1);
        await bench.Engine.RunDueCardsAsync(Ct);
        await bench.Engine.RunDueCardsAsync(Ct);
        bench.Time.Now = due.AddMinutes(1);
        await bench.Engine.RunDueCardsAsync(Ct);

        var push = Assert.Single(bench.Notifier.Pushes);
        Assert.StartsWith("Dentist is due ", push.Body);
        Assert.Contains(" at ", push.Body);
    }

    [Fact]
    public async Task DueTrigger_FiresAgain_WhenTheDueDateMoves()
    {
        var start = DateTimeOffset.UtcNow;
        var bench = await NewBenchAsync(start);
        await bench.Engine.SaveAsync(WhenDue(0), Ct);
        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "Pay bill"), ("due", start.AddMinutes(-1).ToString("O"))));
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.RunDueCardsAsync(Ct);
        Assert.Single(bench.Notifier.Pushes);

        // Snoozed to later; fires again when that comes.
        var later = start.AddMinutes(30);
        await PushAsync(bench.Store, ChangeAt(DateTimeOffset.UtcNow.AddSeconds(2), "c1", "card", ("due", later.ToString("O"))));
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.RunDueCardsAsync(Ct);
        Assert.Single(bench.Notifier.Pushes);
        bench.Time.Now = later.AddSeconds(1);
        await bench.Engine.RunDueCardsAsync(Ct);
        Assert.Equal(2, bench.Notifier.Pushes.Count);
    }

    [Fact]
    public async Task DueTrigger_SkipsFinishedCards_OtherLists_AndOnesLongOverdue()
    {
        var start = DateTimeOffset.UtcNow;
        var bench = await NewBenchAsync(start);
        await bench.Engine.SaveAsync(WhenDue(0, columnId: "new"), Ct);
        await PushAsync(bench.Store,
            Change("done-card", "card", ("parentId", "new"), ("title", "a"), ("completed", true), ("due", start.AddMinutes(-1).ToString("O"))),
            Change("other-list", "card", ("parentId", "doing"), ("title", "b"), ("due", start.AddMinutes(-1).ToString("O"))),
            Change("ancient", "card", ("parentId", "new"), ("title", "c"), ("due", start.AddDays(-3).ToString("O"))),
            Change("no-due", "card", ("parentId", "new"), ("title", "d")),
            Change("fires", "card", ("parentId", "new"), ("title", "e"), ("due", start.AddMinutes(-1).ToString("O"))));
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.RunDueCardsAsync(Ct);

        var push = Assert.Single(bench.Notifier.Pushes);
        Assert.Equal("fires", push.ItemId);
    }

    [Fact]
    public async Task DueTrigger_AnAllDayCard_IsDueAtNineThatMorning_InTheRulesTimeZone()
    {
        // 25 Sep 2026, 9:00 CDT = 14:00 UTC. A day before is 24 Sep 14:00 UTC.
        var bench = await NewBenchAsync(DateTimeOffset.Parse("2026-09-24T13:59:00Z"));
        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "Trash day"), ("due", "2026-09-25")));
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.SaveAsync(WhenDue(24 * 60), Ct);

        await bench.Engine.RunDueCardsAsync(Ct);
        Assert.Empty(bench.Notifier.Pushes);

        bench.Time.Now = DateTimeOffset.Parse("2026-09-24T14:00:01Z");
        await bench.Engine.RunDueCardsAsync(Ct);
        Assert.Equal("Trash day is due Sep 25", Assert.Single(bench.Notifier.Pushes).Body);
    }

    [Fact]
    public void DueInstant_ReadsBothShapesOfDueDate()
    {
        var chicago = OrganizerAutomationSchedule.ZoneNamed(Chicago);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T14:00:00Z"), OrganizerAutomationSchedule.DueInstant("2026-09-25", chicago));
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T22:00:00Z"), OrganizerAutomationSchedule.DueInstant("2026-09-25T17:00:00-05:00", chicago));
        Assert.Null(OrganizerAutomationSchedule.DueInstant("next tuesday", chicago));
        Assert.Null(OrganizerAutomationSchedule.DueInstant(null, chicago));
    }

    [Fact]
    public async Task DueTrigger_AStepThatMovesTheCard_Works_AndItDoesNotFireTwice()
    {
        var start = DateTimeOffset.UtcNow;
        var bench = await NewBenchAsync(start);
        var rule = WhenDue(0);
        rule.Actions = [new BoardAutomationAction { Type = "moveCard", ColumnId = "doing", Position = "top" }];
        await bench.Engine.SaveAsync(rule, Ct);
        await PushAsync(bench.Store, Change("c1", "card", ("parentId", "new"), ("title", "x"), ("due", start.AddMinutes(-1).ToString("O"))));
        await bench.Engine.ProcessChangesAsync(Ct);

        await bench.Engine.RunDueCardsAsync(Ct);
        await bench.Engine.ProcessChangesAsync(Ct);
        await bench.Engine.RunDueCardsAsync(Ct);

        Assert.Equal("doing", bench.Get("c1").ParentId);
        Assert.Equal(1, (await bench.Automations.GetAsync("due", Ct))!.RunCount);
    }
}
