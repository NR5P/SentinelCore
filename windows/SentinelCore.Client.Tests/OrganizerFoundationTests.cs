using System.Text.Json.Nodes;
using SentinelCore.Client.Organizer;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The building blocks every sync decision rests on - the same cases OrganizerKit pins on Apple and
/// OrganizerFoundationTest pins on Android, so a repeating event lands on the same days on this PC
/// as on a phone.
/// </summary>
public class OrganizerFoundationTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    private static OrganizerItem Event(string start, string? end = null, string? rule = null, bool allDay = false, params string[] exdates)
    {
        var fields = new Dictionary<string, object?> { ["title"] = "x", ["start"] = start, ["allDay"] = allDay };
        if (end is not null) fields["end"] = end;
        if (rule is not null) fields["recurrence"] = rule;
        if (exdates.Length > 0) fields["exdates"] = exdates.Cast<object?>().ToList();
        return new OrganizerItem("e", OrganizerKinds.Event, "family", Fields: fields);
    }

    private static DateTimeOffset Day(string text) => OrganizerDates.StartOfDay(DateTime.Parse(text), Chicago);

    private static List<string> Days(IEnumerable<Occurrence> list) => list.Select(o => OrganizerDates.Ymd(o.Start)).ToList();

    [Fact]
    public void JsonRoundTripsEveryShapeAndKeepsUnknownFields()
    {
        const string text = """{"title":"x","n":2,"f":1.5,"flag":true,"none":null,"list":[1,"a",{"k":false}],"future":{"deep":[null]}}""";
        var map = OrganizerJson.Map((JsonObject)JsonNode.Parse(text)!);
        var written = OrganizerJson.ToNode(map)!.ToJsonString();
        var again = OrganizerJson.Map((JsonObject)JsonNode.Parse(written)!);
        Assert.Equal(OrganizerJson.ToNode(map)!.ToJsonString(), OrganizerJson.ToNode(again)!.ToJsonString());
        Assert.Contains("\"future\":{\"deep\":[null]}", written);
        Assert.Contains("\"none\":null", written);
        // Whole numbers go out without a fraction.
        Assert.Contains("\"n\":2,", written);
        Assert.Contains("\"f\":1.5", written);
    }

    [Fact]
    public void ItemsRoundTripThroughTheirStoredForm()
    {
        var item = new OrganizerItem("i", OrganizerKinds.Card, "group:ab", "col", false,
            new Dictionary<string, object?> { ["title"] = "Paint", ["labels"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "red" } } },
            new Dictionary<string, string> { ["title"] = "1790000000000-0000-pc" },
            Seq: 7, CreatedAt: DateTimeOffset.Parse("2026-09-23T19:08:19.8631234+00:00"));
        var back = OrganizerJson.Item(OrganizerJson.ItemJson(item));
        Assert.Equal(OrganizerJson.ItemJson(item).ToJsonString(), OrganizerJson.ItemJson(back).ToJsonString());
        Assert.Equal("col", back.ParentId);
        Assert.Equal(7, back.Seq);
    }

    [Fact]
    public void ClocksAreFixedWidthOrderedAndNeverFallBehindOneAlreadySeen()
    {
        var clock = new HybridClock("pc");
        Assert.Equal("1790000000000-0000-pc", clock.Next(1_790_000_000_000));
        Assert.Equal("1790000000000-0001-pc", clock.Next(1_790_000_000_000));
        clock.Observe("1790000060000-0003-laptop");
        Assert.True(string.CompareOrdinal(clock.Next(1_790_000_000_000), "1790000060000-0003-laptop") > 0);
        Assert.Equal("MorgansPC2", new HybridClock("Morgan’s PC (2)").DeviceId);
    }

    [Fact]
    public void FieldValuesMatchTheDashboardFormat()
    {
        var date = OrganizerDates.At(new DateTime(2026, 9, 23, 17, 0, 0), Chicago);
        Assert.Equal("2026-09-23T17:00:00-05:00", OrganizerDates.FieldValue(date, allDay: false));
        Assert.Equal("2026-09-23", OrganizerDates.FieldValue(date, allDay: true));
        Assert.Equal(date, OrganizerDates.Parse("2026-09-23T22:00:00Z", false, Chicago));
        Assert.NotNull(OrganizerJson.Instant("2026-09-23T19:08:19.8631234+00:00"));
        Assert.Equal("2026-11-01T01:30:00-05:00", OrganizerDates.IsoLocal(OrganizerDates.At(new DateTime(2026, 11, 1, 1, 30, 0), Chicago)));
        // 2:30am doesn't exist on the spring-forward night; it moves past the gap, as java.time does.
        Assert.Equal("2027-03-14T03:30:00-05:00", OrganizerDates.IsoLocal(OrganizerDates.At(new DateTime(2027, 3, 14, 2, 30, 0), Chicago)));
    }

    [Fact]
    public void WeeklyKeepsItsWallClockTimeAcrossDaylightSaving()
    {
        var starts = Recurrence.Occurrences(Event("2026-10-21T09:00:00-05:00", rule: "FREQ=WEEKLY"), Day("2026-10-20"), Day("2026-11-12"), Chicago);
        Assert.Equal(new[] { 9, 9, 9, 9 }, starts.Select(o => o.Start.Hour));
        Assert.Equal(TimeSpan.FromHours(-6), starts.Last().Start.Offset);
    }

    [Fact]
    public void ByDayUntilAndExdates()
    {
        var series = Event("2026-09-21T18:00:00-05:00", rule: "FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261007", exdates: "2026-09-30T18:00:00-05:00");
        Assert.Equal(new[] { "2026-09-21", "2026-09-23", "2026-09-28", "2026-10-05", "2026-10-07" },
            Days(Recurrence.Occurrences(series, Day("2026-09-01"), Day("2026-12-01"), Chicago)));
    }

    [Fact]
    public void MonthlyOnThe31stSkipsShortMonthsAndLeapDayOnlyHappensInLeapYears()
    {
        Assert.Equal(new[] { "2026-01-31", "2026-03-31", "2026-05-31" },
            Days(Recurrence.Occurrences(Event("2026-01-31", rule: "FREQ=MONTHLY", allDay: true), Day("2026-01-01"), Day("2026-06-01"), Chicago)));
        Assert.Equal(new[] { "2028-02-29", "2032-02-29" },
            Days(Recurrence.Occurrences(Event("2028-02-29", rule: "FREQ=YEARLY", allDay: true), Day("2028-01-01"), Day("2033-01-01"), Chicago)));
    }

    [Fact]
    public void CountLimitsTheSeriesAndIntervalSkips()
    {
        Assert.Equal(new[] { "2026-09-01", "2026-09-03", "2026-09-05" },
            Days(Recurrence.Occurrences(Event("2026-09-01", rule: "FREQ=DAILY;INTERVAL=2;COUNT=3", allDay: true), Day("2026-08-01"), Day("2026-12-01"), Chicago)));
    }

    [Fact]
    public void OccurrenceOriginalStartIsWhatExdatesUse()
    {
        var only = Recurrence.Occurrences(Event("2026-09-23T15:00:00-05:00", rule: "FREQ=WEEKLY"), Day("2026-10-13"), Day("2026-10-15"), Chicago);
        Assert.Equal(new[] { "2026-10-14T15:00:00-05:00" }, only.Select(o => o.OriginalStart));
    }

    [Fact]
    public void AllDayEventsLastWholeDaysAndOverlapTheirDays()
    {
        var trip = Event("2026-09-25", end: "2026-09-28", allDay: true);
        var hits = Recurrence.Occurrences(trip, Day("2026-09-27"), Day("2026-09-28"), Chicago);
        Assert.Single(hits);
        Assert.Empty(Recurrence.Occurrences(trip, Day("2026-09-28"), Day("2026-09-29"), Chicago));
    }

    [Fact]
    public void NextDueIsTheOccurrenceAfterTheOneJustDone()
    {
        var reminder = new OrganizerItem("r", OrganizerKinds.Reminder, "family",
            Fields: new Dictionary<string, object?> { ["due"] = "2026-09-22", ["allDay"] = true, ["recurrence"] = "FREQ=WEEKLY" });
        Assert.Equal("2026-09-29", Recurrence.NextDue(reminder, Chicago));
        var timed = reminder with
        {
            Fields = new Dictionary<string, object?> { ["due"] = "2026-09-22T19:30:00-05:00", ["allDay"] = false, ["recurrence"] = "FREQ=WEEKLY" },
        };
        Assert.Equal("2026-09-29T19:30:00-05:00", Recurrence.NextDue(timed, Chicago));
    }

    [Fact]
    public void RuleRoundTripsItsOwnOutput()
    {
        var rule = new RecurrenceRule("WEEKLY", Interval: 2, ByWeekday: new[] { 1, 3 });
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE", rule.RRuleValue);
        Assert.Equal(rule, RecurrenceRule.Parse(rule.RRuleValue, Chicago));
        Assert.Null(RecurrenceRule.Parse("FREQ=HOURLY", Chicago));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T12:00:00Z");

    private static OrganizerItem Item(string id, string kind, string? parent = null, int updatedDaysAgo = 0, Dictionary<string, object?>? fields = null) =>
        new(id, kind, "family", parent, Fields: fields, UpdatedAt: Now.AddDays(-updatedDaysAgo));

    [Fact]
    public void ALightCopyDropsOldThingsButNeverUpcomingOrOpenEndedOnes()
    {
        var items = new[]
        {
            Item("old-note", OrganizerKinds.Note, updatedDaysAgo: 200),
            Item("old-note-photo", OrganizerKinds.Attachment, parent: "old-note", fields: new() { ["hash"] = "a" }),
            Item("pinned-note", OrganizerKinds.Note, updatedDaysAgo: 400, fields: new() { ["pinned"] = true }),
            Item("recent-note", OrganizerKinds.Note, updatedDaysAgo: 3),
            Item("past-event", OrganizerKinds.Event, fields: new() { ["start"] = "2026-01-10", ["allDay"] = true }),
            Item("weekly-since-2020", OrganizerKinds.Event, fields: new() { ["start"] = "2020-01-06", ["allDay"] = true, ["recurrence"] = "FREQ=WEEKLY" }),
            Item("next-month", OrganizerKinds.Event, fields: new() { ["start"] = "2026-10-10T10:00:00Z" }),
            Item("open-reminder", OrganizerKinds.Reminder, updatedDaysAgo: 900),
            Item("done-long-ago", OrganizerKinds.Reminder, fields: new() { ["completed"] = true, ["completedAt"] = "2025-01-01T00:00:00Z" }),
            Item("board", OrganizerKinds.Board, updatedDaysAgo: 900),
            Item("column", OrganizerKinds.Column, parent: "board", updatedDaysAgo: 900),
            Item("stale-open-card", OrganizerKinds.Card, parent: "column", updatedDaysAgo: 900),
            Item("finished-card", OrganizerKinds.Card, parent: "column", updatedDaysAgo: 300, fields: new() { ["completed"] = true }),
        }.ToDictionary(item => item.Id);
        var pruned = OfflinePolicy.PhoneDefault.ItemsToPrune(items, new HashSet<string>(), Now, Chicago);
        Assert.Equal(new[] { "done-long-ago", "finished-card", "old-note", "old-note-photo", "past-event" }, pruned.Order());
    }

    [Fact]
    public void PendingEditsAndPinsAreNeverDroppedAndKeepTheirParents()
    {
        var items = new[]
        {
            Item("old-note", OrganizerKinds.Note, updatedDaysAgo: 500),
            Item("photo", OrganizerKinds.Attachment, parent: "old-note", fields: new() { ["hash"] = "a" }),
            Item("other", OrganizerKinds.Note, updatedDaysAgo: 500),
        }.ToDictionary(item => item.Id);
        Assert.Equal(new[] { "other" }, OfflinePolicy.PhoneDefault.ItemsToPrune(items, new HashSet<string> { "photo" }, Now, Chicago));
        Assert.Empty(OfflinePolicy.FullDefault.ItemsToPrune(items, new HashSet<string>(), Now, Chicago));
        Assert.True(OfflinePolicy.FullDefault.Widens(OfflinePolicy.PhoneDefault));
        Assert.False(OfflinePolicy.PhoneDefault.Widens(OfflinePolicy.FullDefault));
        Assert.Equal(OfflinePolicy.PhoneDefault, OfflinePolicy.FromJson(OfflinePolicy.PhoneDefault.ToJson()));
        Assert.Null(OfflinePolicy.FromJson("{not json"));
    }

    [Fact]
    public void PlansEventAlertsRemindersAtTheirDueTimeAndSkipsFinishedOnes()
    {
        var now = OrganizerDates.Parse("2026-09-23T08:00:00-05:00", false, Chicago)!.Value;
        var items = new[]
        {
            new OrganizerItem("practice", OrganizerKinds.Event, "family", Fields: new Dictionary<string, object?>
            {
                ["title"] = "Practice", ["start"] = "2026-09-23T15:00:00-05:00", ["recurrence"] = "FREQ=WEEKLY",
                ["alerts"] = new List<object?> { 30.0 }, ["location"] = "Field 3",
            }),
            new OrganizerItem("dentist", OrganizerKinds.Reminder, "family",
                Fields: new Dictionary<string, object?> { ["title"] = "Call dentist", ["due"] = "2026-09-23T17:30:00-05:00" }),
            new OrganizerItem("trash", OrganizerKinds.Reminder, "family",
                Fields: new Dictionary<string, object?> { ["title"] = "Trash", ["due"] = "2026-09-24", ["allDay"] = true }),
            new OrganizerItem("done", OrganizerKinds.Reminder, "family",
                Fields: new Dictionary<string, object?> { ["title"] = "Done", ["due"] = "2026-09-23T18:00:00-05:00", ["completed"] = true }),
            new OrganizerItem("silent", OrganizerKinds.Event, "family",
                Fields: new Dictionary<string, object?> { ["title"] = "No alert", ["start"] = "2026-09-23T12:00:00-05:00" }),
        };
        var plan = AlertPlanner.Plan(items, now, Chicago, horizonDays: 8, formatTime: date => date.ToString("HH:mm"));
        Assert.Equal(
            new[]
            {
                "Practice 2026-09-23T14:30:00-05:00", "Call dentist 2026-09-23T17:30:00-05:00",
                "Trash 2026-09-24T09:00:00-05:00", "Practice 2026-09-30T14:30:00-05:00",
            },
            plan.Select(alert => $"{alert.Title} {OrganizerDates.IsoLocal(OrganizerDates.InZone(alert.FireAt, Chicago))}"));
        Assert.Equal("15:00 · Field 3", plan[0].Body);
        Assert.Equal(plan.Count, plan.Select(alert => alert.Id).Distinct().Count());
    }

    [Fact]
    public void AnAlertThatCameDueIsShownOnceEvenIfTheNextPlanNoLongerHasIt()
    {
        var queue = new OrganizerAlertQueue();
        var now = DateTimeOffset.Parse("2026-09-23T14:29:50-05:00");
        PlannedAlert Alert(string id, string at) => new(id, id, DateTimeOffset.Parse(at), id, "");
        queue.Update(new[] { Alert("practice", "2026-09-23T14:30:00-05:00"), Alert("dentist", "2026-09-23T17:30:00-05:00"), Alert("bins", "2026-09-23T18:00:00-05:00") }, now);

        // A sync at 14:30:05 replans: practice is now in the past so the plan leaves it out, and the
        // bins reminder was ticked off.
        var later = now.AddSeconds(15);
        queue.Update(new[] { Alert("dentist", "2026-09-23T17:30:00-05:00") }, later);
        Assert.Equal(new[] { "practice" }, queue.TakeDue(later.AddSeconds(5)).Select(alert => alert.Id));
        Assert.Empty(queue.TakeDue(later.AddSeconds(20)));
        Assert.Empty(queue.TakeDue(DateTimeOffset.Parse("2026-09-23T17:59:00-05:00")).Where(alert => alert.Id == "bins"));
    }

    [Fact]
    public void AnAlertMissedByHoursIsDroppedNotShown()
    {
        var queue = new OrganizerAlertQueue();
        var at = DateTimeOffset.Parse("2026-09-23T09:00:00-05:00");
        queue.Update(new[] { new PlannedAlert("a", "a", at, "Standup", "") }, at.AddMinutes(-5));
        // The PC slept through it and woke at lunchtime.
        Assert.Empty(queue.TakeDue(at.AddHours(3)));
        Assert.Empty(queue.TakeDue(at.AddHours(3)));
    }

    [Fact]
    public void TheSqliteStoreKeepsItemsPendingEditsAndBlobsAcrossReopening()
    {
        var root = Path.Combine(Path.GetTempPath(), "organizer-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var item = new OrganizerItem("n1", OrganizerKinds.Note, "family",
                Fields: new Dictionary<string, object?> { ["title"] = "Keep", ["tags"] = new List<object?> { "a" } },
                Clocks: new Dictionary<string, string> { ["title"] = "1790000000000-0000-pc" });
            var change = new OrganizerChange("n1", OrganizerKinds.Note,
                new Dictionary<string, object?> { ["title"] = "Keep" },
                new Dictionary<string, string> { ["title"] = "1790000000000-0000-pc" },
                new Dictionary<string, string> { ["body"] = "1780000000000-0000-web" });
            using (var store = new SqliteOrganizerStore(root))
            {
                store.SaveItems(new[] { item, item with { Id = "n2" } });
                store.DeleteItems(new[] { "n2" });
                store.SavePending("n1", change);
                store.SetMeta("cursor", "42");
                store.SaveBlob(new BlobRecord("abc", true, true, 10, 5));
            }
            using (var store = new SqliteOrganizerStore(root))
            {
                Assert.Equal("Keep", Assert.Single(store.LoadItems()).Title);
                Assert.Equal("1780000000000-0000-web", store.LoadPending()["n1"].BaseClocks!["body"]);
                Assert.Equal("42", store.Meta("cursor"));
                Assert.Equal(new BlobRecord("abc", true, true, 10, 5), store.Blob("abc"));
                store.Wipe(keepPending: true);
                Assert.Empty(store.LoadItems());
                Assert.Single(store.LoadPending());
                store.Wipe(keepPending: false);
                Assert.Empty(store.LoadPending());
                Assert.Empty(store.AllBlobs());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
