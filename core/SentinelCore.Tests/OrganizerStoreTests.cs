using System.Text.Json.Nodes;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The Organizer's merge and visibility rules. These are what every one of the six clients relies
// on, and a mistake here doesn't crash anything - it quietly loses somebody's note, or shows one
// person's reminders to another - so each rule gets a test that would catch it going wrong.
public sealed class OrganizerStoreTests : IDisposable
{
    private static readonly OrganizerUser Morgan = new("morgan", null);
    private static readonly OrganizerUser Terry = new("terry", null);
    private static readonly OrganizerUser Kid = new("kid", null);

    private readonly List<string> _directories = [];
    private readonly List<SqliteOrganizerStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        foreach (var directory in _directories)
        {
            try { TempDirectories.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private SqliteOrganizerStore NewStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"organizer-{Guid.NewGuid():N}");
        _directories.Add(directory);
        var store = new SqliteOrganizerStore(directory);
        _stores.Add(store);
        return store;
    }

    // A clock `secondsAgo` in the past from `device`. Real time rather than a fixed date because
    // the store clamps clocks that run ahead of it.
    private static string Clock(int secondsAgo, string device = "phone") =>
        $"{DateTimeOffset.UtcNow.AddSeconds(-secondsAgo).ToUnixTimeMilliseconds():D13}-0000-{device}";

    private static OrganizerChange Change(string id, string kind, string clock, params (string Name, JsonNode? Value)[] fields)
    {
        var change = new OrganizerChange { Id = id, Kind = kind };
        foreach (var (name, value) in fields)
        {
            change.Fields[name] = value;
            change.Clocks[name] = clock;
        }
        return change;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<OrganizerItem> OnlyItemAsync(IOrganizerStore store, OrganizerUser user, string id) =>
        Assert.Single(await store.GetItemsAsync(user, null, null, Ct), item => item.Id == id);

    [Fact]
    public async Task EditsToDifferentFields_OnTwoDevices_BothSurvive()
    {
        var store = NewStore();
        await store.ApplyChangesAsync(Morgan, [Change("card-note", OrganizerKinds.Note, Clock(60), ("spaceId", "family"), ("title", "Groceries"), ("body", "milk"))], null, Ct);

        // Phone renames it; laptop pins it. Neither knew about the other.
        await store.ApplyChangesAsync(Morgan, [Change("card-note", OrganizerKinds.Note, Clock(30, "phone"), ("title", "Shopping"))], null, Ct);
        await store.ApplyChangesAsync(Morgan, [Change("card-note", OrganizerKinds.Note, Clock(40, "laptop"), ("pinned", true))], null, Ct);

        var note = await OnlyItemAsync(store, Morgan, "card-note");
        Assert.Equal("Shopping", note.Fields["title"]!.GetValue<string>());
        Assert.True(note.Fields["pinned"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnOlderEdit_ArrivingLate_DoesNotOverwriteANewerOne()
    {
        var store = NewStore();
        await store.ApplyChangesAsync(Morgan, [Change("r1", OrganizerKinds.Reminder, Clock(60), ("spaceId", "family"), ("title", "Call vet"))], null, Ct);
        await store.ApplyChangesAsync(Morgan, [Change("r1", OrganizerKinds.Reminder, Clock(10, "laptop"), ("title", "Call vet at 3"))], null, Ct);

        // The phone was offline and made its edit before the laptop's; it only gets through now.
        await store.ApplyChangesAsync(Morgan, [Change("r1", OrganizerKinds.Reminder, Clock(30, "phone"), ("title", "Call the vet"))], null, Ct);

        Assert.Equal("Call vet at 3", (await OnlyItemAsync(store, Morgan, "r1")).Fields["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task ABoardMadeOffline_ArrivesChildrenFirst_AndIsStillAccepted()
    {
        var store = NewStore();
        var clock = Clock(5);

        // Deliberately out of order: a client may flush its queue in any order.
        var result = await store.ApplyChangesAsync(Morgan,
        [
            Change("card1", OrganizerKinds.Card, clock, ("parentId", "col1"), ("title", "Paint fence")),
            Change("col1", OrganizerKinds.Column, clock, ("parentId", "board1"), ("title", "To do")),
            Change("board1", OrganizerKinds.Board, clock, ("spaceId", "family"), ("title", "House")),
        ], null, Ct);

        Assert.Empty(result.Rejected);
        var card = await OnlyItemAsync(store, Morgan, "card1");
        Assert.Equal("family", card.SpaceId);
    }

    [Fact]
    public async Task ACardWithNoColumn_OrInTheWrongKindOfParent_IsRejected()
    {
        var store = NewStore();
        var clock = Clock(5);
        await store.ApplyChangesAsync(Morgan, [Change("note1", OrganizerKinds.Note, clock, ("spaceId", "family"), ("title", "x"))], null, Ct);

        var result = await store.ApplyChangesAsync(Morgan,
        [
            Change("orphan", OrganizerKinds.Card, clock, ("parentId", "nope"), ("title", "a")),
            Change("misplaced", OrganizerKinds.Card, clock, ("parentId", "note1"), ("title", "b")),
            Change("loose", OrganizerKinds.Column, clock, ("spaceId", "family"), ("title", "c")),
        ], null, Ct);

        Assert.Equal(["loose", "misplaced", "orphan"], result.Rejected.Select(r => r.Id).Order());
    }

    [Fact]
    public async Task TwoPeopleCommentingOnACardOffline_BothKeepTheirComment()
    {
        var store = NewStore();
        var clock = Clock(30);
        await store.ApplyChangesAsync(Morgan,
        [
            Change("board1", OrganizerKinds.Board, clock, ("spaceId", "family"), ("title", "House")),
            Change("col1", OrganizerKinds.Column, clock, ("parentId", "board1"), ("title", "To do")),
            Change("card1", OrganizerKinds.Card, clock, ("parentId", "col1"), ("title", "Paint fence")),
        ], null, Ct);

        await store.ApplyChangesAsync(Morgan, [Change("c-morgan", OrganizerKinds.Comment, Clock(20, "phone"), ("parentId", "card1"), ("body", "Got the paint"))], null, Ct);
        await store.ApplyChangesAsync(Terry, [Change("c-terry", OrganizerKinds.Comment, Clock(25, "laptop"), ("parentId", "card1"), ("body", "Wrong colour"))], null, Ct);

        var comments = await store.GetItemsAsync(Morgan, OrganizerKinds.Comment, null, Ct);
        Assert.Equal(["c-morgan", "c-terry"], comments.Select(c => c.Id).Order());
        Assert.All(comments, c => Assert.Equal("family", c.SpaceId));
        Assert.Equal("terry", comments.Single(c => c.Id == "c-terry").CreatedBy);
    }

    [Fact]
    public async Task AComment_OnlyGoesOnACard()
    {
        var store = NewStore();
        var clock = Clock(5);
        await store.ApplyChangesAsync(Morgan, [Change("note1", OrganizerKinds.Note, clock, ("spaceId", "family"), ("title", "x"))], null, Ct);

        var result = await store.ApplyChangesAsync(Morgan,
        [
            Change("on-note", OrganizerKinds.Comment, clock, ("parentId", "note1"), ("body", "a")),
            Change("loose", OrganizerKinds.Comment, clock, ("spaceId", "family"), ("body", "b")),
        ], null, Ct);

        Assert.Equal(["loose", "on-note"], result.Rejected.Select(r => r.Id).Order());
    }

    [Fact]
    public async Task OnlyTheAuthor_CanEditAComment_ButAnyoneCanDeleteIt()
    {
        var store = NewStore();
        var clock = Clock(60);
        await store.ApplyChangesAsync(Morgan,
        [
            Change("board1", OrganizerKinds.Board, clock, ("spaceId", "family"), ("title", "House")),
            Change("col1", OrganizerKinds.Column, clock, ("parentId", "board1"), ("title", "To do")),
            Change("card1", OrganizerKinds.Card, clock, ("parentId", "col1"), ("title", "Paint fence")),
            Change("c1", OrganizerKinds.Comment, clock, ("parentId", "card1"), ("body", "Got the paint")),
        ], null, Ct);

        var byTerry = await store.ApplyChangesAsync(Terry, [Change("c1", OrganizerKinds.Comment, Clock(30), ("body", "Morgan forgot the paint"))], null, Ct);
        Assert.Equal("c1", Assert.Single(byTerry.Rejected).Id);

        var byMorgan = await store.ApplyChangesAsync(Morgan, [Change("c1", OrganizerKinds.Comment, Clock(20), ("body", "Got the paint, and brushes"))], null, Ct);
        Assert.Empty(byMorgan.Rejected);
        Assert.Equal("Got the paint, and brushes", (await OnlyItemAsync(store, Morgan, "c1")).Fields["body"]!.GetValue<string>());

        var deleted = await store.ApplyChangesAsync(Terry, [Change("c1", OrganizerKinds.Comment, Clock(10), ("deleted", true))], null, Ct);
        Assert.Empty(deleted.Rejected);
        Assert.Empty(await store.GetItemsAsync(Morgan, OrganizerKinds.Comment, null, Ct));
    }

    [Fact]
    public async Task TwoPeopleRewritingTheSameNote_KeepsBothVersions()
    {
        var store = NewStore();
        var original = Clock(120);
        await store.ApplyChangesAsync(Morgan, [Change("n1", OrganizerKinds.Note, original, ("spaceId", "family"), ("title", "Trip"), ("body", "pack bags"))], null, Ct);

        var laptop = Change("n1", OrganizerKinds.Note, Clock(60, "laptop"), ("body", "pack bags, passports"));
        laptop.BaseClocks = new() { ["body"] = original };
        await store.ApplyChangesAsync(Morgan, [laptop], null, Ct);

        // The phone also started from the original text, and saves later.
        var phone = Change("n1", OrganizerKinds.Note, Clock(30, "phone"), ("body", "pack bags, snacks"));
        phone.BaseClocks = new() { ["body"] = original };
        var result = await store.ApplyChangesAsync(Terry, [phone], null, Ct);

        var notes = await store.GetItemsAsync(Morgan, OrganizerKinds.Note, null, Ct);
        Assert.Equal(2, notes.Count);
        Assert.Equal("pack bags, snacks", notes.Single(n => n.Id == "n1").Fields["body"]!.GetValue<string>());
        var copy = notes.Single(n => n.Id != "n1");
        Assert.Equal("pack bags, passports", copy.Fields["body"]!.GetValue<string>());
        Assert.Equal("Trip (conflicted copy)", copy.Fields["title"]!.GetValue<string>());

        // Resending the same batch - a dropped response - must not make a second copy.
        await store.ApplyChangesAsync(Terry, [phone], null, Ct);
        Assert.Equal(2, (await store.GetItemsAsync(Morgan, OrganizerKinds.Note, null, Ct)).Count);
        Assert.Contains(result.Items, item => item.Id == copy.Id);
    }

    [Fact]
    public async Task AClockFromTheFuture_IsPulledBackToNow()
    {
        var store = NewStore();
        var nextYear = $"{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeMilliseconds():D13}-0000-brokenphone";
        await store.ApplyChangesAsync(Morgan, [Change("e1", OrganizerKinds.Event, nextYear, ("spaceId", "family"), ("title", "Party"))], null, Ct);

        // Otherwise that phone would win every future edit to this event for a year.
        await Task.Delay(5, Ct);
        await store.ApplyChangesAsync(Morgan, [Change("e1", OrganizerKinds.Event, Clock(0, "laptop"), ("title", "Birthday party"))], null, Ct);
        Assert.Equal("Birthday party", (await OnlyItemAsync(store, Morgan, "e1")).Fields["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task PersonalItems_AreOnlyVisibleToTheirOwner()
    {
        var store = NewStore();
        var mine = await store.ApplyChangesAsync(Morgan, [Change("diary", OrganizerKinds.Note, Clock(5), ("spaceId", "personal:morgan"), ("title", "private"))], null, Ct);
        Assert.Empty(mine.Rejected);

        Assert.Empty(await store.GetItemsAsync(Terry, null, null, Ct));
        Assert.Empty((await store.PullAsync(Terry, 0, new HashSet<string>(), 100, Ct)).Items);

        // Nor may she write to it, by editing the item or by making one in that space.
        var edit = await store.ApplyChangesAsync(Terry, [Change("diary", OrganizerKinds.Note, Clock(1), ("title", "hacked"))], null, Ct);
        var create = await store.ApplyChangesAsync(Terry, [Change("plant", OrganizerKinds.Note, Clock(1), ("spaceId", "personal:morgan"), ("title", "x"))], null, Ct);
        Assert.Single(edit.Rejected);
        Assert.Single(create.Rejected);
        Assert.Equal("private", (await OnlyItemAsync(store, Morgan, "diary")).Fields["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithLoginOff_OnlyTheFamilySpaceExists()
    {
        var store = NewStore();
        var spaces = await store.GetVisibleSpacesAsync(OrganizerUser.Shared, Ct);
        Assert.Equal(["family"], spaces.Select(space => space.Id));
    }

    [Fact]
    public async Task GroupItems_AreVisibleToMembersOnly()
    {
        var store = NewStore();
        var parents = await store.CreateGroupAsync(Morgan, "Parents", ["terry"], null, Ct);
        Assert.Equal(["morgan", "terry"], parents.Members);

        await store.ApplyChangesAsync(Terry, [Change("gift", OrganizerKinds.Reminder, Clock(5), ("spaceId", parents.Id), ("title", "Buy bike"))], null, Ct);

        Assert.Single(await store.GetItemsAsync(Morgan, null, null, Ct));
        Assert.Empty(await store.GetItemsAsync(Kid, null, null, Ct));
    }

    [Fact]
    public async Task MovingABoardOutOfAGroup_TellsTheOtherMemberToDropIt_WithItsCards()
    {
        var store = NewStore();
        var group = await store.CreateGroupAsync(Morgan, "Us", ["terry"], null, Ct);
        var clock = Clock(60);
        await store.ApplyChangesAsync(Morgan,
        [
            Change("b", OrganizerKinds.Board, clock, ("spaceId", group.Id), ("title", "Surprise")),
            Change("c", OrganizerKinds.Column, clock, ("parentId", "b"), ("title", "Ideas")),
            Change("k", OrganizerKinds.Card, clock, ("parentId", "c"), ("title", "Party")),
        ], null, Ct);

        var terryView = await store.PullAsync(Terry, 0, new HashSet<string>(), 100, Ct);
        Assert.Equal(3, terryView.Items.Count);

        await store.ApplyChangesAsync(Morgan, [Change("b", OrganizerKinds.Board, Clock(10), ("spaceId", "personal:morgan"))], null, Ct);

        var after = await store.PullAsync(Terry, terryView.Cursor, terryView.Items.Select(i => i.SpaceId).ToHashSet(), 100, Ct);
        Assert.Equal(["b", "c", "k"], after.Removed.Order());
        Assert.Empty(after.Items);

        // And the column and card went with the board.
        Assert.All(await store.GetItemsAsync(Morgan, null, null, Ct), item => Assert.Equal("personal:morgan", item.SpaceId));
    }

    [Fact]
    public async Task SomeoneAddedToAGroup_GetsEverythingAlreadyInIt()
    {
        var store = NewStore();
        var group = await store.CreateGroupAsync(Morgan, "Trip", [], null, Ct);
        await store.ApplyChangesAsync(Morgan, [Change("old", OrganizerKinds.Note, Clock(60), ("spaceId", group.Id), ("title", "Hotel"))], null, Ct);

        var terryFirst = await store.PullAsync(Terry, 0, new HashSet<string>(), 100, Ct);
        var known = (await store.GetVisibleSpacesAsync(Terry, Ct)).Select(space => space.Id).ToHashSet();

        await store.UpdateGroupAsync(Morgan, group.Id, "Trip", ["morgan", "terry"], null, Ct);

        // Her cursor is already past the note - only the new-space backfill can deliver it.
        var terryNext = await store.PullAsync(Terry, terryFirst.Cursor, known, 100, Ct);
        Assert.Contains(terryNext.Items, item => item.Id == "old");
    }

    [Fact]
    public async Task ADeletedItem_StaysDeleted_WhenAStaleDeviceEditsIt()
    {
        var store = NewStore();
        await store.ApplyChangesAsync(Morgan, [Change("r", OrganizerKinds.Reminder, Clock(60), ("spaceId", "family"), ("title", "Old"))], null, Ct);
        await store.ApplyChangesAsync(Morgan, [Change("r", OrganizerKinds.Reminder, Clock(20), ("deleted", true))], null, Ct);

        // A phone that never saw the delete renames it.
        await store.ApplyChangesAsync(Morgan, [Change("r", OrganizerKinds.Reminder, Clock(10, "phone"), ("title", "Renamed"))], null, Ct);

        Assert.Empty(await store.GetItemsAsync(Morgan, null, null, Ct));
        var tombstone = Assert.Single((await store.PullAsync(Morgan, 0, new HashSet<string>(), 100, Ct)).Items);
        Assert.True(tombstone.Deleted);
    }

    [Fact]
    public async Task DeletingAGroup_TombstonesWhatWasInIt()
    {
        var store = NewStore();
        var group = await store.CreateGroupAsync(Morgan, "Temp", [], null, Ct);
        await store.ApplyChangesAsync(Morgan, [Change("x", OrganizerKinds.Note, Clock(5), ("spaceId", group.Id), ("title", "x"))], null, Ct);

        Assert.False(await store.DeleteGroupAsync(Terry, group.Id, Ct));
        Assert.True(await store.DeleteGroupAsync(Morgan, group.Id, Ct));

        Assert.DoesNotContain(await store.GetVisibleSpacesAsync(Morgan, Ct), space => space.Id == group.Id);
        Assert.Empty(await store.GetItemsAsync(Morgan, null, null, Ct));
    }

    // The disaster-recovery path: the server is gone, a new one is installed, and a device that
    // still has everything re-uploads it - personal items, family items and group definitions.
    [Fact]
    public async Task ARebuiltServer_IsRefilledFromADevicesCopy()
    {
        var oldServer = NewStore();
        var group = await oldServer.CreateGroupAsync(Morgan, "Parents", ["terry"], null, Ct);
        await oldServer.ApplyChangesAsync(Morgan,
        [
            Change("mine", OrganizerKinds.Note, Clock(60), ("spaceId", "personal:morgan"), ("title", "Mine")),
            Change("ours", OrganizerKinds.Event, Clock(60), ("spaceId", group.Id), ("title", "Date night")),
            Change("all", OrganizerKinds.Reminder, Clock(60), ("spaceId", "family"), ("title", "Trash day")),
        ], null, Ct);
        var deviceCopy = await oldServer.PullAsync(Morgan, 0, new HashSet<string>(), 100, Ct);
        var deviceSpaces = await oldServer.GetVisibleSpacesAsync(Morgan, Ct);

        var newServer = NewStore();
        Assert.NotEqual(oldServer.ServerId, newServer.ServerId);

        var reupload = deviceCopy.Items.Select(item =>
        {
            var change = new OrganizerChange { Id = item.Id, Kind = item.Kind, Fields = (JsonObject)item.Fields.DeepClone() };
            change.Fields["spaceId"] = item.SpaceId;
            foreach (var (name, clock) in item.Clocks) change.Clocks[name] = clock;
            return change;
        }).ToList();
        var result = await newServer.ApplyChangesAsync(Morgan, reupload, deviceSpaces, Ct);

        Assert.Empty(result.Rejected);
        Assert.Equal(3, (await newServer.GetItemsAsync(Morgan, null, null, Ct)).Count);

        // The group came back with its members, so Terry sees what she saw before.
        var terrySees = await newServer.GetItemsAsync(Terry, null, null, Ct);
        Assert.Equal(["all", "ours"], terrySees.Select(item => item.Id).Order());
    }

    [Fact]
    public async Task ADevice_CannotRestoreAGroupItIsNotIn()
    {
        var store = NewStore();
        var stolen = new OrganizerSpace("group:abc", OrganizerSpaceKind.Group, "Theirs", ["terry"], null, "terry");
        await store.ApplyChangesAsync(Kid, [], [stolen], Ct);
        Assert.DoesNotContain(await store.GetVisibleSpacesAsync(Terry, Ct), space => space.Id == "group:abc");
    }

    [Fact]
    public async Task BackupRestore_IsIdempotent_AndNeverRollsBackNewerEdits()
    {
        var source = NewStore();
        await source.ApplyChangesAsync(Morgan, [Change("n", OrganizerKinds.Note, Clock(60), ("spaceId", "personal:morgan"), ("title", "Backed up"))], null, Ct);
        var backup = await source.ExportAsync(Ct);

        var target = NewStore();
        await target.ApplyChangesAsync(Morgan, [Change("n", OrganizerKinds.Note, Clock(5), ("spaceId", "personal:morgan"), ("title", "Newer"))], null, Ct);

        await target.ImportAsync(backup, Ct);
        await target.ImportAsync(backup, Ct);

        Assert.Equal("Newer", (await OnlyItemAsync(target, Morgan, "n")).Fields["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Paging_WalksTheWholeFeed()
    {
        var store = NewStore();
        var clock = Clock(5);
        await store.ApplyChangesAsync(Morgan,
            [.. Enumerable.Range(0, 25).Select(i => Change($"r{i}", OrganizerKinds.Reminder, clock, ("spaceId", "family"), ("title", $"#{i}")))],
            null, Ct);

        var seen = new HashSet<string>();
        long cursor = 0;
        OrganizerPullResult page;
        do
        {
            page = await store.PullAsync(Morgan, cursor, new HashSet<string> { "family", "personal:morgan" }, 10, Ct);
            foreach (var item in page.Items) seen.Add(item.Id);
            cursor = page.Cursor;
        }
        while (page.HasMore);

        Assert.Equal(25, seen.Count);
        Assert.Equal(store.CurrentCursor, cursor);
    }

    [Fact]
    public async Task WaitForChange_WakesWhenSomethingIsWritten()
    {
        var store = NewStore();
        var cursor = store.CurrentCursor;
        var waiting = store.WaitForChangeAsync(cursor, TimeSpan.FromSeconds(10), Ct);
        Assert.False(waiting.IsCompleted);

        await store.ApplyChangesAsync(Morgan, [Change("x", OrganizerKinds.Note, Clock(1), ("spaceId", "family"), ("title", "x"))], null, Ct);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct) > cursor);
    }

    [Fact]
    public void CalendarFeed_WritesEventsAsICalendar()
    {
        var item = new OrganizerItem
        {
            Id = "e1",
            Kind = OrganizerKinds.Event,
            SpaceId = "family",
            UpdatedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Fields = new JsonObject
            {
                ["title"] = "Soccer, practice",
                ["start"] = "2026-09-23T17:00:00-05:00",
                ["end"] = "2026-09-23T18:00:00-05:00",
                ["recurrence"] = "FREQ=WEEKLY;BYDAY=WE",
                ["alerts"] = new JsonArray(30),
                ["links"] = new JsonArray(new JsonObject { ["url"] = "https://example.com/schedule" }),
            },
        };
        var allDay = new OrganizerItem
        {
            Id = "e2",
            Kind = OrganizerKinds.Event,
            SpaceId = "family",
            Fields = new JsonObject { ["title"] = "Holiday", ["start"] = "2026-12-25", ["end"] = "2026-12-26", ["allDay"] = true },
        };
        var injected = new OrganizerItem
        {
            Id = "e3",
            Kind = OrganizerKinds.Event,
            SpaceId = "family",
            Fields = new JsonObject { ["title"] = "x", ["start"] = "2026-01-01T00:00:00Z", ["recurrence"] = "FREQ=DAILY\r\nATTACH:evil" },
        };

        var ics = OrganizerCalendarFeed.Build([item, allDay, injected],
            [new OrganizerSpace("family", OrganizerSpaceKind.Family, "Family", [], null, null)]);

        Assert.Contains("SUMMARY:Soccer\\, practice\r\n", ics);
        Assert.Contains("DTSTART:20260923T220000Z\r\n", ics);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=WE\r\n", ics);
        Assert.Contains("TRIGGER:-PT30M\r\n", ics);
        Assert.Contains("https://example.com/schedule", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20261225\r\n", ics);
        Assert.DoesNotContain("ATTACH", ics);
    }

    // One edited occurrence travels as an override with RECURRENCE-ID. Listing its date as an
    // EXDATE as well makes Google drop the override, so the edited session would vanish.
    [Fact]
    public void CalendarFeed_DoesNotExcludeAnOccurrenceThatHasAnOverride()
    {
        var series = new OrganizerItem
        {
            Id = "series",
            Kind = OrganizerKinds.Event,
            SpaceId = "family",
            Fields = new JsonObject
            {
                ["title"] = "Practice",
                ["start"] = "2026-09-23T15:00:00Z",
                ["recurrence"] = "FREQ=WEEKLY",
                ["exdates"] = new JsonArray("2026-09-30T15:00:00Z", "2026-10-07T15:00:00Z"),
            },
        };
        var moved = new OrganizerItem
        {
            Id = "moved",
            Kind = OrganizerKinds.Event,
            SpaceId = "family",
            Fields = new JsonObject
            {
                ["title"] = "Away game",
                ["start"] = "2026-10-07T18:00:00Z",
                ["seriesId"] = "series",
                ["originalStart"] = "2026-10-07T15:00:00Z",
            },
        };

        var ics = OrganizerCalendarFeed.Build([series, moved], []);

        Assert.Contains("EXDATE:20260930T150000Z", ics);
        Assert.DoesNotContain("EXDATE:20261007T150000Z", ics);
        Assert.Contains("RECURRENCE-ID:20261007T150000Z", ics);
    }
}
