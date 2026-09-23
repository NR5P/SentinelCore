using System.Text;
using System.Text.Json.Nodes;
using SentinelCore.Client.Organizer;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// A server that answers what each test tells it to, and remembers what it was asked - for the
/// engine behaviours that don't depend on the real merge rules. Those run against the real server
/// in OrganizerLiveServerTests.
/// </summary>
internal sealed class ScriptedTransport(Func<string, string, byte[]?, OrganizerResponse> handler) : IOrganizerTransport
{
    private readonly List<(string Path, byte[]? Body)> _requests = new();

    public Func<string, string, byte[]?, OrganizerResponse> Handler { get; set; } = handler;

    public int SyncDelayMilliseconds { get; set; }

    public async Task<OrganizerResponse> SendAsync(string method, string path, IReadOnlyDictionary<string, string>? query = null,
        byte[]? body = null, string? contentType = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        lock (_requests) _requests.Add((path, body));
        if (path == "/api/organizer/wait") await Task.Delay(Timeout.Infinite, ct);
        if (path == "/api/organizer/sync" && SyncDelayMilliseconds > 0) await Task.Delay(SyncDelayMilliseconds, ct);
        return Handler(method, path, body);
    }

    public List<JsonObject> SyncBodies()
    {
        lock (_requests)
        {
            return _requests.Where(r => r.Path == "/api/organizer/sync")
                .Select(r => (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(r.Body!))!).ToList();
        }
    }

    public static OrganizerResponse Json(string text, int status = 200) => new(status, Encoding.UTF8.GetBytes(text));

    public static OrganizerResponse Status(string? username = null, string serverId = "server-a") => Json(new JsonObject
    {
        ["enabled"] = true, ["serverId"] = serverId, ["username"] = username, ["personalSpaces"] = username is not null,
        ["itemCount"] = 0, ["maxAttachmentBytes"] = 52_428_800,
    }.ToJsonString());

    public const string FamilySpace = """{"id":"family","kind":"Family","name":"Family","members":[]}""";

    public static OrganizerResponse Reply(string serverId = "server-a", bool reset = false, int cursor = 1, string? spaces = null,
        string items = "[]", string rejected = "[]") =>
        Json($$"""
            {"serverId":"{{serverId}}","reset":{{(reset ? "true" : "false")}},"cursor":{{cursor}},"hasMore":false,
             "spaces":{{spaces ?? $"[{FamilySpace}]"}},"items":{{items}},"removed":[],"rejected":{{rejected}},"missingBlobs":[]}
            """);
}

public sealed class OrganizerEngineTests : IDisposable
{
    private readonly List<string> _directories = new();
    private readonly List<OrganizerEngine> _engines = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "organizer-engine-" + Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    private OrganizerEngine Engine(IOrganizerLocalStore store, OfflinePolicy? policy = null)
    {
        var engine = new OrganizerEngine(store, "pc", policy ?? OfflinePolicy.FullDefault);
        _engines.Add(engine);
        return engine;
    }

    private static ScriptedTransport OnlyStatus(Func<string, string, byte[]?, OrganizerResponse> handler) =>
        new((method, path, body) => path == "/api/organizer/status" ? ScriptedTransport.Status() : handler(method, path, body));

    private static async Task Settle(OrganizerEngine engine)
    {
        await Task.Delay(100);
        await engine.SyncAsync();
    }

    private static Dictionary<string, object?> F(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    [Fact]
    public async Task EditsMadeOfflineSurviveARestartAndGoUpAsOneChange()
    {
        var store = new InMemoryOrganizerStore(NewDirectory());
        var first = Engine(store);
        var note = first.Create(OrganizerKinds.Note, F(("title", "Groceries"), ("body", "milk")), spaceId: "family");
        first.Update(note.Id, "body", "milk, eggs");
        first.Update(note.Id, "body", "milk, eggs, bread");
        Assert.Equal(1, first.SyncState.WaitingChanges);

        // The app was closed with no network. The next launch still has the note and the edit.
        var second = Engine(store);
        Assert.Equal("milk, eggs, bread", second.Item(note.Id)?.String("body"));
        var transport = OnlyStatus((_, _, _) => ScriptedTransport.Reply());
        second.Connect(transport);
        await Settle(second);

        var sent = Assert.Single(transport.SyncBodies()[0]["changes"]!.AsArray())!.AsObject();
        Assert.Equal("milk, eggs, bread", sent["fields"]!["body"]!.GetValue<string>());
        Assert.Equal("family", sent["fields"]!["spaceId"]!.GetValue<string>());
        Assert.Equal(new[] { "body", "spaceId", "title" }, sent["clocks"]!.AsObject().Select(pair => pair.Key).Order());
    }

    [Fact]
    public async Task AFailedSyncKeepsEverythingQueued()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        engine.Connect(OnlyStatus((_, _, _) => throw new OrganizerOfflineException()));
        await Settle(engine);
        engine.Create(OrganizerKinds.Reminder, F(("title", "Call vet")), spaceId: "family");
        await engine.SyncAsync();
        Assert.False(engine.SyncState.Online);
        Assert.Equal(1, engine.SyncState.WaitingChanges);
        Assert.Equal(new[] { "Call vet" }, engine.Items(OrganizerKinds.Reminder).Select(item => item.Title));
    }

    [Fact]
    public async Task ASyncCalledAfterAnEditMadeMidSyncSendsThatEdit()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var transport = OnlyStatus((_, _, _) => ScriptedTransport.Reply());
        engine.Connect(transport);
        await Settle(engine);

        transport.SyncDelayMilliseconds = 200;
        engine.Create(OrganizerKinds.Note, F(("title", "first")), spaceId: "family");
        var first = Task.Run(engine.SyncAsync);
        await Task.Delay(50);
        var second = engine.Create(OrganizerKinds.Note, F(("title", "second")), spaceId: "family");
        await engine.SyncAsync();
        var sentIds = transport.SyncBodies()
            .SelectMany(body => body["changes"]!.AsArray().Select(change => change!["id"]!.GetValue<string>()));
        Assert.Contains(second.Id, sentIds);
        await first;
    }

    /// <summary>A server that is unreachable for requests begun while <see cref="Down"/> is set,
    /// however long they take to fail - like a long poll left over from before the network dropped.</summary>
    private sealed class FlakyTransport : IOrganizerTransport
    {
        public volatile bool Down;
        public readonly List<string> SentIds = new();

        public async Task<OrganizerResponse> SendAsync(string method, string path, IReadOnlyDictionary<string, string>? query = null,
            byte[]? body = null, string? contentType = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var down = Down;
            if (path == "/api/organizer/wait") await Task.Delay(Timeout.Infinite, ct);
            if (path == "/api/organizer/status") return ScriptedTransport.Status();
            await Task.Delay(200, ct);
            if (down) throw new OrganizerOfflineException();
            var changes = JsonNode.Parse(Encoding.UTF8.GetString(body!))!["changes"]!.AsArray();
            lock (SentIds) SentIds.AddRange(changes.Select(change => change!["id"]!.GetValue<string>()));
            return ScriptedTransport.Reply();
        }
    }

    [Fact]
    public async Task ASyncAskedForAfterReconnectingIsNotAnsweredByAPassThatFailedWhileOffline()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var transport = new FlakyTransport();
        engine.Connect(transport);
        await Settle(engine);

        transport.Down = true;
        var failing = Task.Run(engine.SyncAsync);
        await Task.Delay(50);
        transport.Down = false;
        var note = engine.Create(OrganizerKinds.Note, F(("title", "written as the network came back")), spaceId: "family");
        await engine.SyncAsync();
        lock (transport.SentIds) Assert.Contains(note.Id, transport.SentIds);
        await failing;
    }

    [Fact]
    public async Task ANoteBodyEditCarriesTheClockItStartedFrom()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var transport = OnlyStatus((_, _, _) => ScriptedTransport.Reply(items: """
            [{"id":"n1","kind":"note","spaceId":"family","parentId":null,"deleted":false,"fields":{"title":"Trip","body":"pack"},
              "clocks":{"title":"1790000000000-0000-web","body":"1790000000000-0000-web","spaceId":"1790000000000-0000-web"},"seq":1}]
            """));
        engine.Connect(transport);
        await Settle(engine);
        transport.Handler = (_, _, _) => ScriptedTransport.Reply(cursor: 2);
        engine.Update("n1", "body", "pack bags");
        engine.Update("n1", "body", "pack bags, snacks");
        await engine.SyncAsync();
        var change = transport.SyncBodies().Last()["changes"]![0]!;
        Assert.Equal("1790000000000-0000-web", change["baseClocks"]!["body"]!.GetValue<string>());
        Assert.Equal("pack bags, snacks", change["fields"]!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARebuiltServerAsksBeforeUploadingAnything()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var transport = OnlyStatus((_, _, _) => ScriptedTransport.Reply("server-a"));
        engine.Connect(transport);
        await Settle(engine);
        engine.Create(OrganizerKinds.Note, F(("title", "Keep me")), spaceId: "family");
        await engine.SyncAsync();

        transport.Handler = (_, path, _) => path == "/api/organizer/status"
            ? ScriptedTransport.Status(serverId: "server-b")
            : ScriptedTransport.Reply("server-b", reset: true, cursor: 0);
        engine.Create(OrganizerKinds.Note, F(("title", "Written after")), spaceId: "family");
        await engine.SyncAsync();
        Assert.Equal("server-b", engine.ResetPrompt?.ServerId);
        Assert.Equal(2, engine.ResetPrompt?.LocalCount);
        Assert.Equal(1, engine.SyncState.WaitingChanges);
        Assert.Equal(2, engine.Items(OrganizerKinds.Note).Count);
    }

    [Fact]
    public async Task SigningInAsSomeoneElseStartsFromAnEmptyCopy()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var transport = new ScriptedTransport((_, path, _) => path == "/api/organizer/status"
            ? ScriptedTransport.Status("nathan")
            : ScriptedTransport.Reply(items: """[{"id":"diary","kind":"note","spaceId":"family","deleted":false,"fields":{"title":"Mine"},"clocks":{},"seq":1}]"""));
        engine.Connect(transport);
        await Settle(engine);
        Assert.Single(engine.Items(OrganizerKinds.Note));
        transport.Handler = (_, path, _) => path == "/api/organizer/status" ? ScriptedTransport.Status("terry") : ScriptedTransport.Reply(cursor: 0);
        await engine.RefreshStatusAsync();
        Assert.Empty(engine.Items(OrganizerKinds.Note));
    }

    [Fact]
    public async Task LosingAccessToAGroupDropsWhatWasInIt()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        const string group = """{"id":"group:abc","kind":"Group","name":"Parents","members":["nathan","terry"]}""";
        var transport = OnlyStatus((_, _, _) => ScriptedTransport.Reply(
            spaces: $"[{ScriptedTransport.FamilySpace},{group}]",
            items: """[{"id":"gift","kind":"reminder","spaceId":"group:abc","deleted":false,"fields":{"title":"Bike"},"clocks":{},"seq":1}]"""));
        engine.Connect(transport);
        await Settle(engine);
        Assert.Equal(new[] { "Bike" }, engine.Items(OrganizerKinds.Reminder).Select(item => item.Title));
        Assert.Equal(OrganizerSpaceKind.Group, engine.Space("group:abc")?.Kind);
        transport.Handler = (_, _, _) => ScriptedTransport.Reply(cursor: 2);
        await engine.SyncAsync();
        Assert.Empty(engine.Items(OrganizerKinds.Reminder));
    }

    [Fact]
    public async Task ANewItemTheServerRefusesIsRemovedAndExplained()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var transport = OnlyStatus((_, _, _) => ScriptedTransport.Reply());
        engine.Connect(transport);
        await Settle(engine);
        var doomed = engine.Create(OrganizerKinds.Note, F(("title", "x")), spaceId: "personal:someone");
        transport.Handler = (_, _, _) => ScriptedTransport.Reply(cursor: 2,
            rejected: new JsonArray(new JsonObject { ["id"] = doomed.Id, ["error"] = "You don't have access to that space." }).ToJsonString());
        await engine.SyncAsync();
        Assert.Null(engine.Item(doomed.Id));
        Assert.Equal("You don't have access to that space.", engine.SyncState.LastError);
        Assert.Equal(0, engine.SyncState.WaitingChanges);
    }

    [Fact]
    public void DeletingABoardDeletesItsColumnsAndCardsAndChildrenTakeTheirParentsSpace()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var board = engine.Create(OrganizerKinds.Board, F(("title", "House")), spaceId: "family");
        var column = engine.Create(OrganizerKinds.Column, F(("title", "To do")), parentId: board.Id);
        var card = engine.Create(OrganizerKinds.Card, F(("title", "Paint")), parentId: column.Id);
        Assert.Equal("family", card.SpaceId);
        Assert.Single(engine.Children(board.Id, OrganizerKinds.Column));

        engine.Update(board.Id, "spaceId", "group:x");
        Assert.Equal("group:x", engine.Item(card.Id)?.SpaceId);

        engine.DeleteTree(board.Id);
        Assert.Empty(engine.Items(OrganizerKinds.Board));
        Assert.Empty(engine.Items(OrganizerKinds.Card));
    }

    [Fact]
    public void AttachingWorksOfflineAndKeepsTheFile()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        var note = engine.Create(OrganizerKinds.Note, F(("title", "Photo")), spaceId: "family");
        var attachment = engine.Attach(Encoding.UTF8.GetBytes("not really a photo"), "photo.jpg", "image/jpeg", note.Id);
        var hash = attachment.String("hash")!;
        Assert.Equal(64, hash.Length);
        Assert.True(engine.HasOriginal(hash));
        Assert.Equal(1, engine.StorageSummary().WaitingUploads);
        Assert.Equal(note.Id, attachment.ParentId);
    }

    [Fact]
    public async Task OffModeKeepsNothingBetweenLaunchesExceptUnsentEdits()
    {
        var store = new InMemoryOrganizerStore(NewDirectory());
        var off = OfflinePolicy.PhoneDefault with { Mode = OfflineMode.Off };
        var first = Engine(store, off);
        first.Connect(OnlyStatus((_, _, _) => ScriptedTransport.Reply(
            items: """[{"id":"synced","kind":"note","spaceId":"family","deleted":false,"fields":{"title":"From server"},"clocks":{},"seq":1}]""")));
        await Settle(first);
        first.Disconnect();
        first.Create(OrganizerKinds.Note, F(("title", "Unsent")), spaceId: "family");
        Assert.Equal(2, first.Items(OrganizerKinds.Note).Count);

        var second = Engine(store, off);
        Assert.Equal(new[] { "Unsent" }, second.Items(OrganizerKinds.Note).Select(item => item.Title));
        Assert.Equal(1, second.SyncState.WaitingChanges);
    }

    [Fact]
    public async Task AlertsAreReplannedAfterEveryEdit()
    {
        var engine = Engine(new InMemoryOrganizerStore(NewDirectory()));
        IReadOnlyList<PlannedAlert>? latest = null;
        engine.AlertsChanged += alerts => latest = alerts;
        var due = OrganizerDates.FieldValue(DateTimeOffset.Now.AddHours(2), allDay: false);
        var reminder = engine.Create(OrganizerKinds.Reminder, F(("title", "Take bins out"), ("due", due)), spaceId: "family");
        Assert.Equal("Take bins out", Assert.Single(latest!).Title);
        engine.Update(reminder.Id, "completed", true);
        Assert.Empty(latest!);
        await Task.CompletedTask;
    }
}
