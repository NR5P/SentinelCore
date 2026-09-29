extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// Movie downloads end to end through the real web host, with qBittorrent, Prowlarr, the VPN and
// the home-address lookup replaced by fakes: search, pick, download, file into the library.
public sealed class MediaDownloadEndpointsTests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly string _moviesPath;
    private readonly string _showsPath;
    private readonly FakeStack _stack = new();
    private readonly FakeQbittorrent _qbittorrent = new();
    private readonly FakeProwlarr _prowlarr = new();

    public MediaDownloadEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"media-downloads-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "data");
        _moviesPath = Path.Combine(_root, "Movies");
        _showsPath = Path.Combine(_root, "TV");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_moviesPath);
        Directory.CreateDirectory(_showsPath);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        WriteSettings(enabled: true);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void WriteSettings(bool enabled, bool requireVpn = true) => File.WriteAllText(_settingsPath, $$"""
        {
          "Auth": { "Enabled": false },
          "Media": { "Enabled": true },
          "MediaDownloads": { "Enabled": {{(enabled ? "true" : "false")}}, "RequireVpn": {{(requireVpn ? "true" : "false")}}, "ProwlarrApiKey": "test-key", "IdleMinutes": 1 }
        }
        """);

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseTestSettings(_settingsPath, _dataDirectory)
            .UseSetting("MediaDownloads:StartTimeoutSeconds", "2")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IMediaDownloadStack>(_stack);
                services.AddHttpClient(QbittorrentClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _qbittorrent);
                services.AddHttpClient(ProwlarrClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _prowlarr);
                services.AddHttpClient(MediaDownloadManager.HomeIpClientName).ConfigurePrimaryHttpMessageHandler(() => new StaticHandler("203.0.113.9"));
                // The test drives the worker itself, one pass at a time.
                var worker = services.First(s => s.ImplementationType == typeof(MediaDownloadWorker));
                services.Remove(worker);
            }));

    private async Task<Guid> AddLibraryAsync(WebApplicationFactory<WebProgram> factory, string kind, string path)
    {
        var library = new MediaLibrary(Guid.NewGuid(), kind == MediaLibraryKinds.Movies ? "Movies" : "TV", kind, path, DateTimeOffset.UtcNow);
        await factory.Services.GetRequiredService<IMediaStore>().SaveLibraryAsync(library, Ct);
        return library.Id;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, object input)
    {
        var started = await JsonAsync(await client.PostAsJsonAsync("/api/media/downloads/search", input, Ct), HttpStatusCode.Accepted);
        var id = started.GetProperty("id").GetString();
        for (var i = 0; i < 100; i++)
        {
            var search = await JsonAsync(await client.GetAsync($"/api/media/downloads/search/{id}", Ct), HttpStatusCode.OK);
            if (search.GetProperty("phase").GetString() is "done" or "failed") return search;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("search never finished");
    }

    [Fact]
    public async Task Movie_IsSearchedPickedDownloadedAndFiledIntoTheLibrary()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var libraryId = await AddLibraryAsync(factory, MediaLibraryKinds.Movies, _moviesPath);
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.65.134.1", "Stockholm", null, "SE", "Mullvad");
        _prowlarr.Releases =
        [
            Release("Big.Buck.Bunny.2008.CAM.x264", 400, "magnet:?xt=urn:btih:cccc"),
            Release("Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP", 50, "magnet:?xt=urn:btih:aaaa", "aaaa"),
            Release("Elephants.Dream.2006.1080p.BluRay.x264", 900, "magnet:?xt=urn:btih:bbbb"),
        ];

        var status = await JsonAsync(await client.GetAsync("/api/media/downloads/status", Ct), HttpStatusCode.OK);
        Assert.True(status.GetProperty("canDownload").GetBoolean());
        Assert.True(status.GetProperty("movieLibrary").GetBoolean());
        Assert.False(status.GetProperty("showLibrary").GetBoolean());

        var search = await SearchAsync(client, new { kind = "movie", title = "Big Buck Bunny", year = 2008 });
        Assert.Equal("done", search.GetProperty("phase").GetString());
        var results = search.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(["Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP", "Big.Buck.Bunny.2008.CAM.x264"], results.Select(r => r.GetProperty("title").GetString()));
        Assert.Equal("1080p", results[0].GetProperty("quality").GetString());
        Assert.NotNull(results[1].GetProperty("warning").GetString());
        Assert.Equal("Elephants.Dream.2006.1080p.BluRay.x264", Assert.Single(search.GetProperty("others").EnumerateArray()).GetProperty("title").GetString());
        Assert.Contains("Big Buck Bunny 2008", _prowlarr.Queries);
        // The magnet link never goes to the app.
        Assert.False(results[0].TryGetProperty("downloadSource", out _));

        var queued = await JsonAsync(await client.PostAsJsonAsync("/api/media/downloads",
            new { searchId = search.GetProperty("id").GetString(), releaseId = results[0].GetProperty("id").GetString() }, Ct), HttpStatusCode.Created);
        Assert.Equal("queued", queued.GetProperty("state").GetString());
        var id = Guid.Parse(queued.GetProperty("id").GetString()!);

        var manager = factory.Services.GetRequiredService<MediaDownloadManager>();
        await manager.TickAsync(Ct);
        var added = Assert.Single(_qbittorrent.Added);
        Assert.Equal("magnet:?xt=urn:btih:aaaa", added.Magnet);
        var staging = Path.Combine(_moviesPath, ".downloads", id.ToString("N"));
        Assert.Equal(staging, added.SavePath);
        Assert.Equal("downloading", (await manager.GetAsync(id, Ct))!.State);

        // Halfway.
        _qbittorrent.Torrents.Add(new FakeTorrent("aaaa", added.Tag, 0.5, 1000, 500, 2_000_000, 120, "downloading"));
        await manager.TickAsync(Ct);
        var halfway = await JsonAsync(await client.GetAsync("/api/media/downloads", Ct), HttpStatusCode.OK);
        var active = Assert.Single(halfway.GetProperty("active").EnumerateArray());
        Assert.Equal(0.5, active.GetProperty("progress").GetDouble());
        Assert.Equal(2_000_000, active.GetProperty("speedBytesPerSecond").GetInt64());
        Assert.Equal("connected", halfway.GetProperty("status").GetProperty("vpn").GetProperty("state").GetString());
        Assert.Equal("Stockholm, SE", halfway.GetProperty("status").GetProperty("vpn").GetProperty("location").GetString());

        // Finished: qBittorrent wrote the release's files into the staging folder.
        var release = Path.Combine(staging, "Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP");
        Directory.CreateDirectory(Path.Combine(release, "Sample"));
        File.WriteAllText(Path.Combine(release, "Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP.mkv"), "the film");
        File.WriteAllText(Path.Combine(release, "Sample", "sample.mkv"), "a sample");
        File.WriteAllText(Path.Combine(release, "English.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        _qbittorrent.Torrents[0] = _qbittorrent.Torrents[0] with { Progress = 1, State = "stalledUP" };
        _qbittorrent.Files["aaaa"] =
        [
            ("Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP/Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP.mkv", 8),
            ("Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP/Sample/sample.mkv", 8),
            ("Big.Buck.Bunny.2008.1080p.BluRay.x264-GRP/English.srt", 30),
        ];
        await manager.TickAsync(Ct);

        var done = (await manager.GetAsync(id, Ct))!;
        Assert.Equal(MediaDownloadStates.Done, done.State);
        var film = Path.Combine(_moviesPath, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv");
        Assert.Equal([film], done.ImportedPaths);
        Assert.Equal("the film", File.ReadAllText(film));
        Assert.True(File.Exists(Path.Combine(_moviesPath, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).en.srt")));
        Assert.False(Directory.Exists(Path.Combine(_moviesPath, ".downloads")), "the staging folder is tidied away");
        Assert.Equal([("aaaa", false)], _qbittorrent.Deleted);

        // The id the download links to is the one the scanner gives the new file.
        var scanner = factory.Services.GetRequiredService<MediaScanner>();
        while (!await scanner.ScanAsync(libraryId, Ct)) await Task.Delay(100, Ct);
        var item = await factory.Services.GetRequiredService<IMediaStore>().GetItemAsync(done.ItemId!.Value, Ct);
        Assert.NotNull(item);
        Assert.Equal("Big Buck Bunny", item!.Title);

        var history = await JsonAsync(await client.GetAsync("/api/media/downloads", Ct), HttpStatusCode.OK);
        Assert.Empty(history.GetProperty("active").EnumerateArray());
        Assert.Equal(done.ItemId.ToString(), Assert.Single(history.GetProperty("recent").EnumerateArray()).GetProperty("itemId").GetString());
    }

    [Fact]
    public async Task Nothing_IsSearchedWithoutTheVpn_AndALeakingVpnStopsEverything()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AddLibraryAsync(factory, MediaLibraryKinds.Movies, _moviesPath);

        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Down, Error: "The VPN is not reaching the internet (timed out)");
        var down = await SearchAsync(client, new { kind = "movie", title = "Big Buck Bunny" });
        Assert.Equal("failed", down.GetProperty("phase").GetString());
        Assert.Contains("VPN", down.GetProperty("message").GetString());
        Assert.Empty(_prowlarr.Queries);
        Assert.True(_stack.Starts > 0, "a stopped stack is started by a search");

        // The tunnel is "up" but the sites would see the home address.
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "203.0.113.9");
        var leak = await SearchAsync(client, new { kind = "movie", title = "Big Buck Bunny" });
        Assert.Equal("failed", leak.GetProperty("phase").GetString());
        Assert.Contains("not hiding", leak.GetProperty("message").GetString());
        Assert.True(_stack.Stops > 0);
        Assert.Empty(_prowlarr.Queries);
    }

    [Fact]
    public async Task EpisodeSearch_AsksForTheEpisode_AndGrabNeedsATvLibrary()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AddLibraryAsync(factory, MediaLibraryKinds.Movies, _moviesPath);
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.65.134.1");
        _prowlarr.Releases = [Release("Bluey.2018.S02E05.1080p.WEB-DL.H.264", 20, "magnet:?xt=urn:btih:dddd")];

        var search = await SearchAsync(client, new { kind = "show", title = "Bluey", year = 2018, season = 2, episode = 5 });
        Assert.Equal(["Bluey S02E05"], _prowlarr.Queries);
        Assert.Equal(5000, _prowlarr.Categories.Single());
        var release = Assert.Single(search.GetProperty("results").EnumerateArray());

        var refused = await client.PostAsJsonAsync("/api/media/downloads",
            new { searchId = search.GetProperty("id").GetString(), releaseId = release.GetProperty("id").GetString() }, Ct);
        Assert.Contains("TV library", (await JsonAsync(refused, HttpStatusCode.BadRequest)).GetProperty("error").GetString());

        await AddLibraryAsync(factory, MediaLibraryKinds.Shows, _showsPath);
        var queued = await JsonAsync(await client.PostAsJsonAsync("/api/media/downloads",
            new { searchId = search.GetProperty("id").GetString(), releaseId = release.GetProperty("id").GetString() }, Ct), HttpStatusCode.Created);
        Assert.Equal("S02E05", queued.GetProperty("subtitle").GetString());
        Assert.StartsWith(_showsPath, (await factory.Services.GetRequiredService<MediaDownloadManager>().GetAsync(Guid.Parse(queued.GetProperty("id").GetString()!), Ct))!.StagingPath);
    }

    [Fact]
    public async Task Cancel_RemovesTheTorrentAndItsFiles_AndIdleServicesAreStopped()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AddLibraryAsync(factory, MediaLibraryKinds.Movies, _moviesPath);
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.65.134.1");
        _prowlarr.Releases = [Release("Big.Buck.Bunny.2008.1080p.BluRay.x264", 20, "http://prowlarr/1/download?link=x")];
        _prowlarr.TorrentFile = Encoding.ASCII.GetBytes("d8:announce3:urle");

        var search = await SearchAsync(client, new { kind = "movie", title = "Big Buck Bunny", year = 2008 });
        var queued = await JsonAsync(await client.PostAsJsonAsync("/api/media/downloads",
            new { searchId = search.GetProperty("id").GetString(), releaseId = search.GetProperty("results")[0].GetProperty("id").GetString() }, Ct), HttpStatusCode.Created);
        var manager = factory.Services.GetRequiredService<MediaDownloadManager>();
        await manager.TickAsync(Ct);
        var added = Assert.Single(_qbittorrent.Added);
        Assert.Null(added.Magnet);
        Assert.True(added.HadFile, "a .torrent fetched through Prowlarr is uploaded to qBittorrent");
        _qbittorrent.Torrents.Add(new FakeTorrent("eeee", added.Tag, 0.1, 1000, 100, 10, 900, "downloading"));

        var cancel = await client.DeleteAsync($"/api/media/downloads/{queued.GetProperty("id").GetString()}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal([("eeee", true)], _qbittorrent.Deleted);
        Assert.Equal(MediaDownloadStates.Canceled, (await manager.GetAsync(Guid.Parse(queued.GetProperty("id").GetString()!), Ct))!.State);

        // Nothing left to do: once the idle time has passed the services are switched off.
        _stack.State = MediaDownloadServiceStates.Running;
        var stopsBefore = _stack.Stops;
        await manager.TickAsync(Ct);
        Assert.Equal(stopsBefore, _stack.Stops);
        typeof(MediaDownloadManager).GetField("_lastActivity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(manager, DateTimeOffset.UtcNow.AddMinutes(-2));
        await manager.TickAsync(Ct);
        Assert.Equal(stopsBefore + 1, _stack.Stops);
    }

    [Fact]
    public async Task TurnedOff_EverythingButStatusIs404()
    {
        WriteSettings(enabled: false);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var status = await JsonAsync(await client.GetAsync("/api/media/downloads/status", Ct), HttpStatusCode.OK);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("canDownload").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/media/downloads", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/media/downloads/search", new { kind = "movie", title = "x" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Sites_AreListedAndAddedThroughProwlarr()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.65.134.1");

        var catalog = await JsonAsync(await client.GetAsync("/api/media/downloads/sites/catalog?q=public", Ct), HttpStatusCode.OK);
        var site = Assert.Single(catalog.EnumerateArray());
        Assert.Equal("publicsite", site.GetProperty("definition").GetString());
        Assert.False(site.GetProperty("needsSetup").GetBoolean());

        var added = await JsonAsync(await client.PostAsJsonAsync("/api/media/downloads/sites", new { definition = "publicsite" }, Ct), HttpStatusCode.Created);
        Assert.Equal("Public Site", added.GetProperty("name").GetString());
        var posted = JsonDocument.Parse(_prowlarr.PostedIndexer!).RootElement;
        Assert.True(posted.GetProperty("enable").GetBoolean());
        Assert.Equal(7, posted.GetProperty("appProfileId").GetInt32());

        var list = await JsonAsync(await client.GetAsync("/api/media/downloads/sites", Ct), HttpStatusCode.OK);
        Assert.Equal("Public Site", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Settings_AreValidated_AndTheKeysMasked()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var bad = await client.PutAsJsonAsync("/api/settings/media-downloads", new { enabled = true, allowedUsers = "kids", preferredQuality = "8k", prowlarrUrl = "not a url", idleMinutes = 5 }, Ct);
        var errors = (await JsonAsync(bad, HttpStatusCode.BadRequest)).GetRawText();
        Assert.Contains("Who Can Add", errors);
        Assert.Contains("Preferred Quality", errors);
        Assert.Contains("Prowlarr Address", errors);

        var snapshot = await JsonAsync(await client.GetAsync("/api/settings", Ct), HttpStatusCode.OK);
        var section = snapshot.GetProperty("mediaDownloads");
        Assert.NotEqual("test-key", section.GetProperty("prowlarrApiKey").GetString());
        Assert.Equal("1080p", section.GetProperty("preferredQuality").GetString());
    }

    // MARK: - Fakes

    private static ProwlarrClient.Release Release(string title, int seeders, string source, string? hash = null) =>
        new(Guid.NewGuid().ToString("N"), title, "TestSite", 2_000_000_000, seeders, 1, DateTimeOffset.UtcNow,
            source.StartsWith("magnet:") ? null : source, source.StartsWith("magnet:") ? source : null, hash, "torrent");

    private sealed class FakeStack : IMediaDownloadStack
    {
        public string State = MediaDownloadServiceStates.Stopped;
        public MediaVpnStatus Vpn = new(MediaVpnStatus.Off);
        public int Starts;
        public int Stops;

        public Task<string> StateAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; State = MediaDownloadServiceStates.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; State = MediaDownloadServiceStates.Stopped; return Task.CompletedTask; }
        public Task<MediaVpnStatus> VpnStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Vpn);
    }

    private sealed record FakeTorrent(string Hash, string Tag, double Progress, long Size, long Downloaded, long Speed, long Eta, string State);

    private sealed class FakeQbittorrent : HttpMessageHandler
    {
        public readonly List<(string? Magnet, bool HadFile, string SavePath, string Tag)> Added = [];
        public readonly List<FakeTorrent> Torrents = [];
        public readonly Dictionary<string, List<(string Name, long Size)>> Files = [];
        public readonly List<(string Hash, bool DeleteFiles)> Deleted = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            switch (path)
            {
                case "/api/v2/app/version": return Text("v5.1.4");
                case "/api/v2/torrents/add":
                {
                    var form = (MultipartFormDataContent)request.Content!;
                    string? magnet = null, save = null, tag = null;
                    var hadFile = false;
                    foreach (var part in form)
                    {
                        var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                        if (name == "torrents") { hadFile = true; continue; }
                        var value = await part.ReadAsStringAsync(cancellationToken);
                        if (name == "urls") magnet = value;
                        if (name == "savepath") save = value;
                        if (name == "tags") tag = value;
                    }
                    Added.Add((magnet, hadFile, save!, tag!));
                    return Text("Ok.");
                }
                case "/api/v2/torrents/info":
                    return Json(JsonSerializer.Serialize(Torrents.Select(t => new Dictionary<string, object>
                    {
                        ["hash"] = t.Hash, ["name"] = "release", ["state"] = t.State, ["progress"] = t.Progress, ["total_size"] = t.Size,
                        ["downloaded"] = t.Downloaded, ["dlspeed"] = t.Speed, ["eta"] = t.Eta, ["num_seeds"] = 5, ["num_leechs"] = 2,
                        ["completion_on"] = t.Progress >= 1 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : -1, ["save_path"] = "/x", ["tags"] = t.Tag
                    })));
                case "/api/v2/torrents/files":
                {
                    var hash = request.RequestUri.Query.Split("hash=")[1];
                    return Json(JsonSerializer.Serialize(Files[hash].Select(f => new { name = f.Name, size = f.Size, progress = 1.0 })));
                }
                case "/api/v2/torrents/delete":
                {
                    var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var values = form.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    Deleted.Add((values["hashes"], values["deleteFiles"] == "true"));
                    Torrents.RemoveAll(t => t.Hash == values["hashes"]);
                    return Text("");
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class FakeProwlarr : HttpMessageHandler
    {
        public List<ProwlarrClient.Release> Releases = [];
        public readonly List<string> Queries = [];
        public readonly List<int> Categories = [];
        public byte[]? TorrentFile;
        public string? PostedIndexer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/1/download") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TorrentFile!) };
            if (request.Headers.TryGetValues("X-Api-Key", out var keys) is false || keys.Single() != "test-key")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            switch (uri.AbsolutePath)
            {
                case "/api/v1/system/status": return Json("""{ "version": "2.0.0" }""");
                case "/api/v1/search":
                {
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    Queries.Add(query["query"]!);
                    Categories.Add(int.Parse(query["categories"]!));
                    return Json(JsonSerializer.Serialize(Releases.Select(r => new
                    {
                        guid = r.Guid, title = r.Title, indexer = r.Indexer, size = r.Size, seeders = r.Seeders, leechers = r.Leechers,
                        publishDate = r.PublishDate, downloadUrl = r.DownloadUrl, magnetUrl = r.MagnetUrl, infoHash = r.InfoHash, protocol = r.Protocol
                    })));
                }
                case "/api/v1/indexer/schema":
                    return Json("""
                        [ { "name": "Public Site", "definitionName": "publicsite", "protocol": "torrent", "privacy": "public", "description": "A public site",
                            "fields": [ { "name": "definitionFile", "value": "publicsite", "type": "hidden" }, { "name": "baseUrl", "label": "Base Url", "type": "select", "value": "https://a", "selectOptions": [] } ] },
                          { "name": "Private Place", "definitionName": "privateplace", "protocol": "torrent", "privacy": "private",
                            "fields": [ { "name": "username", "label": "Username", "type": "textbox" } ] } ]
                        """);
                case "/api/v1/appprofile": return Json("""[ { "id": 7, "name": "Standard" } ]""");
                case "/api/v1/indexer" when request.Method == HttpMethod.Post:
                    PostedIndexer = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return Json("""{ "id": 3, "name": "Public Site", "enable": true, "privacy": "public", "definitionName": "publicsite" }""", HttpStatusCode.Created);
                case "/api/v1/indexer":
                    return Json(PostedIndexer is null ? "[]" : """[ { "id": 3, "name": "Public Site", "enable": true, "privacy": "public", "definitionName": "publicsite" } ]""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Text(body));
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
