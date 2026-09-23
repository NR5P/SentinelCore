using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SentinelCore.Client.Organizer;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// Skipped unless the two throwaway servers from scripts/with-organizer-servers.sh are up:
///   scripts/with-organizer-servers.sh dotnet test windows/SentinelCore.Client.Tests
/// </summary>
public sealed class LiveServerFactAttribute : FactAttribute
{
    public LiveServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORGANIZER_TEST_SERVER_A") is null
            || Environment.GetEnvironmentVariable("ORGANIZER_TEST_SERVER_B") is null)
        {
            Skip = "Run under scripts/with-organizer-servers.sh to exercise the real server.";
        }
    }
}

/// <summary>A transport straight at a test server, which a test can cut off or point elsewhere.</summary>
internal sealed class SwitchableTransport(string baseAddress) : IOrganizerTransport, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { CookieContainer = new CookieContainer() })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public volatile string Base = baseAddress;
    public volatile bool Offline;

    public async Task<OrganizerResponse> SendAsync(string method, string path, IReadOnlyDictionary<string, string>? query = null,
        byte[]? body = null, string? contentType = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (Offline) throw new OrganizerOfflineException();
        var queryString = query is null ? "" : string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        using var request = new HttpRequestMessage(new HttpMethod(method), Base + path + (queryString.Length > 0 ? "?" + queryString : ""));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType ?? "application/octet-stream");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            using var response = await _http.SendAsync(request, limit.Token);
            return new OrganizerResponse((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(limit.Token));
        }
        catch (HttpRequestException)
        {
            throw new OrganizerOfflineException();
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Two devices and the real SentinelCore server - the same end-to-end checks OrganizerKit runs on
/// Apple and Android runs in OrganizerLiveServerTest, so every platform is held to the one server.
/// </summary>
public sealed class OrganizerLiveServerTests : IDisposable
{
    private readonly string _serverA = Environment.GetEnvironmentVariable("ORGANIZER_TEST_SERVER_A") ?? "";
    private readonly string _serverB = Environment.GetEnvironmentVariable("ORGANIZER_TEST_SERVER_B") ?? "";
    private readonly List<OrganizerEngine> _engines = new();
    private readonly List<SwitchableTransport> _transports = new();
    private readonly List<string> _directories = new();

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        foreach (var transport in _transports) transport.Dispose();
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private (OrganizerEngine Engine, SwitchableTransport Link) Device(string name, OfflinePolicy? policy = null, string? server = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"organizer-live-{name}-{Guid.NewGuid():N}");
        _directories.Add(folder);
        var engine = new OrganizerEngine(new InMemoryOrganizerStore(folder), name, policy ?? OfflinePolicy.FullDefault);
        var link = new SwitchableTransport(server ?? _serverA);
        _engines.Add(engine);
        _transports.Add(link);
        return (engine, link);
    }

    private static async Task Start(OrganizerEngine engine, SwitchableTransport link)
    {
        engine.Connect(link);
        await engine.RefreshStatusAsync();
        await engine.SyncAsync();
    }

    private static string Unique(string label) => $"{label} {Guid.NewGuid().ToString("N")[..8]}";

    private static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("timed out waiting");
            await Task.Delay(100);
        }
    }

    private static Dictionary<string, object?> F(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    [LiveServerFact]
    public async Task AnEditOnOneDeviceAppearsOnTheOther()
    {
        var (pc, pcLink) = Device("pc");
        var (laptop, laptopLink) = Device("laptop");
        await Start(pc, pcLink);
        await Start(laptop, laptopLink);
        var title = Unique("Soccer practice");
        var links = new List<object?> { new Dictionary<string, object?> { ["url"] = "https://example.com/schedule", ["title"] = "Schedule" } };
        var created = pc.Create(OrganizerKinds.Event,
            F(("title", title), ("start", "2026-09-23T15:00:00-05:00"), ("recurrence", "FREQ=WEEKLY"), ("links", links)), spaceId: "family");
        await pc.SyncAsync();
        await laptop.SyncAsync();
        var seen = laptop.Item(created.Id);
        Assert.NotNull(seen);
        Assert.Equal(title, seen.Title);
        Assert.Equal("Schedule", ((IReadOnlyDictionary<string, object?>)seen.List("links")[0]!)["title"]);
    }

    [LiveServerFact]
    public async Task OfflineEditsToDifferentFieldsBothSurvive()
    {
        var (pc, pcLink) = Device("pc");
        var (laptop, laptopLink) = Device("laptop");
        await Start(pc, pcLink);
        await Start(laptop, laptopLink);
        var reminder = pc.Create(OrganizerKinds.Reminder, F(("title", Unique("Call vet"))), spaceId: "family");
        await pc.SyncAsync();
        await laptop.SyncAsync();

        pcLink.Offline = true;
        pc.Update(reminder.Id, "title", "Call the vet about Max");
        await pc.SyncAsync();
        Assert.Equal(1, pc.SyncState.WaitingChanges);
        laptop.Update(reminder.Id, F(("due", "2026-09-30"), ("allDay", true)));
        await laptop.SyncAsync();
        pcLink.Offline = false;
        await pc.SyncAsync();
        await laptop.SyncAsync();
        foreach (var device in new[] { pc, laptop })
        {
            Assert.Equal("Call the vet about Max", device.Item(reminder.Id)!.Title);
            Assert.Equal("2026-09-30", device.Item(reminder.Id)!.String("due"));
        }
    }

    [LiveServerFact]
    public async Task TwoPeopleRewritingOneNoteKeepBothVersions()
    {
        var (pc, pcLink) = Device("pc");
        var (laptop, laptopLink) = Device("laptop");
        await Start(pc, pcLink);
        await Start(laptop, laptopLink);
        var title = Unique("Trip");
        var note = pc.Create(OrganizerKinds.Note, F(("title", title), ("body", "pack bags")), spaceId: "family");
        await pc.SyncAsync();
        await laptop.SyncAsync();
        pcLink.Offline = true;
        pc.Update(note.Id, "body", "pack bags, snacks");
        laptop.Update(note.Id, "body", "pack bags, passports");
        await laptop.SyncAsync();
        pcLink.Offline = false;
        await pc.SyncAsync();
        await laptop.SyncAsync();
        foreach (var device in new[] { pc, laptop })
        {
            var notes = device.Items(OrganizerKinds.Note).Where(item => item.Title.StartsWith(title, StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { "pack bags, passports", "pack bags, snacks" }, notes.Select(item => item.String("body")).Order());
            Assert.Contains(notes, item => item.Title == $"{title} (conflicted copy)");
        }
    }

    [LiveServerFact]
    public async Task DeletesReachEveryDeviceAndAStaleEditCantBringItBack()
    {
        var (pc, pcLink) = Device("pc");
        var (laptop, laptopLink) = Device("laptop");
        await Start(pc, pcLink);
        await Start(laptop, laptopLink);
        var note = pc.Create(OrganizerKinds.Note, F(("title", Unique("Old list"))), spaceId: "family");
        await pc.SyncAsync();
        await laptop.SyncAsync();
        pcLink.Offline = true;
        pc.Update(note.Id, "title", "Old list (edited offline)");
        laptop.Delete(note.Id);
        await laptop.SyncAsync();
        pcLink.Offline = false;
        await pc.SyncAsync();
        Assert.Null(pc.Item(note.Id));
        Assert.Null(laptop.Item(note.Id));
    }

    [LiveServerFact]
    public async Task ABoardMadeOfflineArrivesWhole()
    {
        var (pc, pcLink) = Device("pc");
        var (laptop, laptopLink) = Device("laptop");
        await Start(pc, pcLink);
        await Start(laptop, laptopLink);
        pcLink.Offline = true;
        var board = pc.Create(OrganizerKinds.Board, F(("title", Unique("House"))), spaceId: "family");
        var todo = pc.Create(OrganizerKinds.Column, F(("title", "To do"), ("position", 1.0)), parentId: board.Id);
        var done = pc.Create(OrganizerKinds.Column, F(("title", "Done"), ("position", 2.0)), parentId: board.Id);
        var checklist = new List<object?> { new Dictionary<string, object?> { ["id"] = "a", ["text"] = "buy paint", ["done"] = false } };
        var card = pc.Create(OrganizerKinds.Card, F(("title", "Paint fence"), ("checklist", checklist)), parentId: todo.Id);
        pc.Update(card.Id, F(("parentId", done.Id), ("position", 1.5)));
        pcLink.Offline = false;
        await pc.SyncAsync();
        await laptop.SyncAsync();
        Assert.Equal(2, laptop.Children(board.Id, OrganizerKinds.Column).Count);
        Assert.Equal(done.Id, laptop.Item(card.Id)!.ParentId);
        Assert.Single(laptop.Item(card.Id)!.List("checklist"));
        Assert.Equal(0, pc.SyncState.WaitingChanges);
    }

    [LiveServerFact]
    public async Task AttachmentsTravelAndALightDeviceKeepsOnlyAThumbnail()
    {
        var (pc, pcLink) = Device("pc");
        var (laptop, laptopLink) = Device("laptop");
        var (light, lightLink) = Device("light", OfflinePolicy.PhoneDefault);
        await Start(pc, pcLink);
        await Start(laptop, laptopLink);
        await Start(light, lightLink);

        var jpeg = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Resources", "organizer-test.jpg"));
        var note = pc.Create(OrganizerKinds.Note, F(("title", Unique("Field map"))), spaceId: "family");
        pcLink.Offline = true;
        var hash = pc.Attach(jpeg, "map.jpg", "image/jpeg", note.Id).String("hash")!;
        pcLink.Offline = false;
        await pc.SyncAsync();
        await WaitUntil(() => pc.StorageSummary().WaitingUploads == 0);

        await laptop.SyncAsync();
        await WaitUntil(() => laptop.HasOriginal(hash));
        Assert.Equal(jpeg, await laptop.ImageBytesAsync(hash, thumbnail: false));

        await light.SyncAsync();
        await WaitUntil(() => light.StorageSummary().BytesOnDevice > 0);
        Assert.False(light.HasOriginal(hash), "a light copy keeps thumbnails, not originals");
        var thumbnail = await light.ImageBytesAsync(hash, thumbnail: true);
        Assert.True(thumbnail!.Length < jpeg.Length);
    }

    // The point of a full copy: the server dies, a new one is installed, this PC refills it.
    [LiveServerFact]
    public async Task ARebuiltServerIsRefilledFromADevice()
    {
        var (pc, link) = Device("pc");
        await Start(pc, link);
        var title = Unique("Survives the server");
        var note = pc.Create(OrganizerKinds.Note, F(("title", title), ("body", "important")), spaceId: "family");
        var bytes = Encoding.UTF8.GetBytes("a picture that has to survive " + Guid.NewGuid());
        var hash = pc.Attach(bytes, "photo.jpg", "image/jpeg", note.Id).String("hash")!;
        await pc.SyncAsync();
        await WaitUntil(() => pc.StorageSummary().WaitingUploads == 0);

        link.Base = _serverB;
        pc.Update(note.Id, "body", "important - edited after the server died");
        await pc.SyncAsync();
        Assert.True((pc.ResetPrompt?.LocalCount ?? 0) > 0);
        await pc.ResolveResetAsync(upload: true);
        await WaitUntil(() => pc.StorageSummary().WaitingUploads == 0 && !pc.SyncState.Syncing);

        var (fresh, freshLink) = Device("fresh", server: _serverB);
        await Start(fresh, freshLink);
        var restored = fresh.Items(OrganizerKinds.Note).FirstOrDefault(item => item.Title == title);
        Assert.Equal("important - edited after the server died", restored?.String("body"));
        await WaitUntil(() => fresh.HasOriginal(hash));
        Assert.Equal(bytes, await fresh.ImageBytesAsync(hash, thumbnail: false));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [LiveServerFact]
    public async Task TheSubscriptionFeedListsEvents()
    {
        var (pc, link) = Device("pc");
        await Start(pc, link);
        var title = Unique("Dentist");
        pc.Create(OrganizerKinds.Event, F(("title", title), ("start", "2026-10-01T09:00:00Z")), spaceId: "family");
        await pc.SyncAsync();
        var path = await pc.CalendarFeedPathAsync();
        using var http = new HttpClient();
        var ics = await http.GetStringAsync(_serverA + path);
        Assert.Contains($"SUMMARY:{title}", ics);
    }

    [LiveServerFact]
    public async Task TheSqliteCopySurvivesARestartAndStillSyncs()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"organizer-live-sqlite-{Guid.NewGuid():N}");
        _directories.Add(folder);
        var link = new SwitchableTransport(_serverA);
        _transports.Add(link);
        var title = Unique("Written before a restart");
        string id;
        using (var store = new SqliteOrganizerStore(folder))
        using (var first = new OrganizerEngine(store, "pc-sqlite", OfflinePolicy.FullDefault))
        {
            await Start(first, link);
            link.Offline = true;
            id = first.Create(OrganizerKinds.Reminder, F(("title", title)), spaceId: "family").Id;
            first.Disconnect();
        }
        link.Offline = false;
        using (var store = new SqliteOrganizerStore(folder))
        using (var second = new OrganizerEngine(store, "pc-sqlite", OfflinePolicy.FullDefault))
        {
            Assert.Equal(title, second.Item(id)?.Title);
            Assert.Equal(1, second.SyncState.WaitingChanges);
            await Start(second, link);
            Assert.Equal(0, second.SyncState.WaitingChanges);
            second.Disconnect();
        }
        var (other, otherLink) = Device("other");
        await Start(other, otherLink);
        Assert.Equal(title, other.Item(id)?.Title);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

/// <summary>
/// The app's own path to the Organizer - AppModel's connect, SentinelClient's cookie session, the
/// session deciding when the engine runs - against the real server.
/// </summary>
public sealed class OrganizerSessionLiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "organizer-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private AppModel NewModel(string server)
    {
        Directory.CreateDirectory(_directory);
        return new AppModel(
            new PreferenceStore(Path.Combine(_directory, "settings.json")),
            new SentinelCore.Client.Networking.CredentialStore(Path.Combine(_directory, "credentials")))
        {
            ServerUrl = server,
        };
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("timed out waiting");
            await Task.Delay(100);
        }
    }

    [LiveServerFact]
    public async Task TheSessionConnectsOnceTheAppIsOnlineAndSyncsThroughItsClient()
    {
        using var model = NewModel(Environment.GetEnvironmentVariable("ORGANIZER_TEST_SERVER_A")!);
        using var session = new OrganizerSession(model, Path.Combine(_directory, "organizer"));
        var title = "Through the app " + Guid.NewGuid().ToString("N")[..8];
        // Written before the server has been reached at all: this PC's copy works offline.
        var reminder = session.Engine.Create(OrganizerKinds.Reminder,
            new Dictionary<string, object?> { ["title"] = title }, spaceId: "family");
        Assert.False(session.Engine.IsConnected);

        await model.ConnectAsync();
        await WaitUntil(() => session.Engine.IsConnected && session.Enabled);
        await session.Engine.SyncAsync();
        Assert.Equal(0, session.Engine.SyncState.WaitingChanges);
        Assert.True(OrganizerDevice.LastKnownEnabled(model.Preferences));

        // Another device sees it.
        var other = new OrganizerEngine(new InMemoryOrganizerStore(Path.Combine(_directory, "other")), "other", OfflinePolicy.FullDefault);
        using var link = new SwitchableTransport(Environment.GetEnvironmentVariable("ORGANIZER_TEST_SERVER_A")!);
        other.Connect(link);
        await other.SyncAsync();
        Assert.Equal(title, other.Item(reminder.Id)?.Title);
        other.Dispose();

        // The policy is this PC's own and outlives the session.
        session.SetPolicy(OfflinePolicy.PhoneDefault);
        Assert.Equal(OfflinePolicy.PhoneDefault, OrganizerDevice.Policy(model.Preferences));
    }

    [Fact]
    public async Task AServerWithoutTheOrganizerIsLeftAlone()
    {
        // Answers 404 to everything, like a server from before the Organizer existed.
        using var listener = new System.Net.HttpListener();
        var port = System.Net.Sockets.TcpListener.Create(0) is var probe ? Port(probe) : 0;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requests = new List<string>();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                lock (requests) requests.Add(context.Request.Url!.AbsolutePath);
                var path = context.Request.Url!.AbsolutePath;
                // Everything else works; there is just no Organizer.
                var organizer = path.StartsWith("/api/organizer", StringComparison.Ordinal);
                context.Response.StatusCode = organizer ? 404 : 200;
                var body = Encoding.UTF8.GetBytes(path switch
                {
                    _ when organizer => "{}",
                    "/api/auth/status" => """{"enabled":false,"authenticated":true}""",
                    "/api/activity" or "/api/cameras" => "[]",
                    _ => "{}",
                });
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });

        using var model = NewModel($"http://127.0.0.1:{port}");
        using var session = new OrganizerSession(model, Path.Combine(_directory, "organizer"));
        await model.ConnectAsync();
        await session.ReconcileAsync();
        await Task.Delay(300);
        Assert.False(session.Enabled);
        Assert.False(session.Engine.IsConnected);
        Assert.Equal(ConnectionState.Online, model.ConnectionState);
        lock (requests)
        {
            // It asked, heard "no such thing", and left it there.
            Assert.Contains("/api/organizer/status", requests);
            Assert.DoesNotContain("/api/organizer/sync", requests);
            Assert.DoesNotContain("/api/organizer/wait", requests);
        }
        listener.Stop();
    }

    private static int Port(System.Net.Sockets.TcpListener listener)
    {
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
