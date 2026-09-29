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
    private readonly FakeMullvad _mullvad = new();

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

    private void WriteSettings(bool enabled, bool useVpn = true) => File.WriteAllText(_settingsPath, $$"""
        {
          "Auth": { "Enabled": false },
          "Media": { "Enabled": true },
          "MediaDownloads": { "Enabled": {{(enabled ? "true" : "false")}}, "UseVpn": {{(useVpn ? "true" : "false")}}, "ProwlarrApiKey": "test-key", "IdleMinutes": 1 }
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
                services.AddHttpClient(MediaVpnConfig.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _mullvad);
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
    public async Task TestVpn_ProvesTheTunnelHidesTheHomeAddress_AndTheStatusSaysSo()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.195.233.76", "Stockholm", null, "SE", "Mullvad");

        var before = await JsonAsync(await client.GetAsync("/api/media/downloads/status", Ct), HttpStatusCode.OK);
        Assert.False(before.GetProperty("status").GetProperty("vpn").GetProperty("verified").GetBoolean());

        var test = await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Equal("Working. The sites see 185.195.233.76 in Stockholm, SE, not your home address.", test.GetProperty("message").GetString());
        var vpn = test.GetProperty("status").GetProperty("vpn");
        Assert.Equal("connected", vpn.GetProperty("state").GetString());
        Assert.True(vpn.GetProperty("verified").GetBoolean());
        Assert.True(_stack.Starts > 0);

        // The tunnel drops: no longer proven, and the test says why.
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Down, Error: "The VPN is not reaching the internet (timed out)");
        var down = await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.False(down.GetProperty("ok").GetBoolean());
        Assert.Contains("timed out", down.GetProperty("message").GetString());
        Assert.False(down.GetProperty("status").GetProperty("vpn").GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task UseVpnOff_SearchesOverTheHomeConnection_RestartsTheServicesAndSaysSo()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AddLibraryAsync(factory, MediaLibraryKinds.Movies, _moviesPath);
        _prowlarr.Releases = [Release("Big.Buck.Bunny.2008.1080p.BluRay.x264", 20, "magnet:?xt=urn:btih:ffff")];
        // Running with the VPN, as it was before the setting changed.
        _stack.State = MediaDownloadServiceStates.Running;
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "203.0.113.9");
        WriteSettings(enabled: true, useVpn: false);

        var search = await SearchAsync(client, new { kind = "movie", title = "Big Buck Bunny", year = 2008 });

        // The home address is not a leak when the VPN is meant to be off.
        Assert.Equal("done", search.GetProperty("phase").GetString());
        Assert.False(_stack.UseVpn);
        Assert.Equal((1, 1), (_stack.Stops, _stack.Starts));
        var vpn = (await JsonAsync(await client.GetAsync("/api/media/downloads/status", Ct), HttpStatusCode.OK)).GetProperty("status").GetProperty("vpn");
        Assert.Equal("disabled", vpn.GetProperty("state").GetString());
        Assert.False(vpn.GetProperty("required").GetBoolean());
        Assert.Equal(JsonValueKind.Null, vpn.GetProperty("ip").ValueKind);

        var test = await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Contains("turned off", test.GetProperty("message").GetString());

        // Back on: restarted again, and the home address is refused once more.
        WriteSettings(enabled: true, useVpn: true);
        var refused = await SearchAsync(client, new { kind = "movie", title = "Big Buck Bunny", year = 2008 });
        Assert.Equal("failed", refused.GetProperty("phase").GetString());
        Assert.True(_stack.UseVpn);
    }

    [Fact]
    public async Task ATitleStartingWithThe_IsAlsoSearchedWithoutIt_ButStillMatchedWhole()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.65.134.1");
        _prowlarr.EmptyForLeadingThe = true;
        _prowlarr.Releases =
        [
            Release("The Addams Family 1991 Extended 1080P BluRay x264", 60, "magnet:?xt=urn:btih:a1"),
            Release("The Addams Family Values 1993 1080p BluRay", 40, "magnet:?xt=urn:btih:a2"),
        ];

        var search = await SearchAsync(client, new { kind = "movie", title = "The Addams Family" });

        Assert.Equal(["The Addams Family", "Addams Family"], _prowlarr.Queries);
        Assert.Equal("The Addams Family 1991 Extended 1080P BluRay x264", Assert.Single(search.GetProperty("results").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal("The Addams Family Values 1993 1080p BluRay", Assert.Single(search.GetProperty("others").EnumerateArray()).GetProperty("title").GetString());
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
        // Prowlarr's schema leaves the address blank; the site's first address is sent.
        var baseUrl = posted.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "baseUrl");
        Assert.Equal("https://public.example/", baseUrl.GetProperty("value").GetString());

        // The catalog offers the addresses to pick from, starting on the first; an optional
        // uploader filter does not make it a "set up" site.
        var field = site.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "baseUrl");
        Assert.Equal("https://public.example/", field.GetProperty("value").GetString());
        Assert.Equal(2, field.GetProperty("options").GetArrayLength());
        var privateSite = (await JsonAsync(await client.GetAsync("/api/media/downloads/sites/catalog?q=private", Ct), HttpStatusCode.OK))[0];
        Assert.True(privateSite.GetProperty("needsSetup").GetBoolean());

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
        public bool UseVpn = true;
        public string? WireGuard;
        public Task<bool> ConfigureAsync(bool useVpn, string? wireGuardConfig, CancellationToken cancellationToken)
        {
            var changed = UseVpn != useVpn || WireGuard != wireGuardConfig;
            WireGuard = wireGuardConfig;
            UseVpn = useVpn;
            return Task.FromResult(changed);
        }
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
        public bool EmptyForLeadingThe;

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
                    // The Pirate Bay: a query starting with "The" finds nothing at all.
                    if (EmptyForLeadingThe && query["query"]!.StartsWith("The ", StringComparison.Ordinal)) return Json("[]");
                    return Json(JsonSerializer.Serialize(Releases.Select(r => new
                    {
                        guid = r.Guid, title = r.Title, indexer = r.Indexer, size = r.Size, seeders = r.Seeders, leechers = r.Leechers,
                        publishDate = r.PublishDate, downloadUrl = r.DownloadUrl, magnetUrl = r.MagnetUrl, infoHash = r.InfoHash, protocol = r.Protocol
                    })));
                }
                case "/api/v1/indexer/schema":
                    return Json("""
                        [ { "name": "Public Site", "definitionName": "publicsite", "protocol": "torrent", "privacy": "public", "description": "A public site",
                            "indexerUrls": [ "https://public.example/", "https://mirror.public.example/" ],
                            "fields": [ { "name": "definitionFile", "value": "publicsite", "type": "hidden" },
                                        { "name": "baseUrl", "label": "Base Url", "type": "select", "value": null, "selectOptions": null, "selectOptionsProviderAction": "getUrls" },
                                        { "name": "uploader", "label": "Filter by Uploader", "type": "textbox", "value": null } ] },
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

    // Mullvad's API as far as MediaVpnConfig uses it: sign in, register a device, the server list.
    private sealed class FakeMullvad : HttpMessageHandler
    {
        public const string Account = "1234567812345678";
        public readonly List<string> RegisteredKeys = [];
        public bool Full;
        public readonly HashSet<string> Removed = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/token")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return body.Contains(Account) ? Json("""{ "access_token": "tok", "expiry": "2030-01-01T00:00:00Z" }""") : Json("""{ "code": "INVALID_ACCOUNT" }""", HttpStatusCode.BadRequest);
            }
            if (path == "/accounts/v1/devices" && request.Method == HttpMethod.Post)
            {
                if (Full) return Json("""{ "code": "MAX_DEVICES_REACHED" }""", HttpStatusCode.BadRequest);
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                RegisteredKeys.Add(doc.RootElement.GetProperty("pubkey").GetString()!);
                var n = RegisteredKeys.Count;
                return Json($$"""{ "id": "dev{{n}}", "name": "happy otter {{n}}", "ipv4_address": "10.64.0.{{n}}/32", "ipv6_address": "fc00:bbbb::{{n}}/128" }""", HttpStatusCode.Created);
            }
            if (path.StartsWith("/accounts/v1/devices/", StringComparison.Ordinal))
                return Removed.Contains(path.Split('/').Last()) ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json("{}");
            if (path == "/public/relays/wireguard/v2")
                return Json("""
                    { "locations": { "se-sto": { "country": "Sweden", "city": "Stockholm" }, "se-got": { "country": "Sweden", "city": "Gothenburg" }, "nl-ams": { "country": "Netherlands", "city": "Amsterdam" } },
                      "wireguard": { "relays": [
                        { "hostname": "se-sto-wg-001", "location": "se-sto", "active": true, "owned": true, "ipv4_addr_in": "185.195.233.76", "public_key": "MkP/Jytkg51/Y/EostONjIN6YaFRpsAYiNKMX27/CAY=" },
                        { "hostname": "se-got-wg-001", "location": "se-got", "active": false, "owned": true, "ipv4_addr_in": "185.213.154.1", "public_key": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" },
                        { "hostname": "nl-ams-wg-001", "location": "nl-ams", "active": true, "owned": false, "ipv4_addr_in": "193.32.249.66", "public_key": "UrQiI9ISdPPzd4ARw1NHOPKKvKvxUhjwRjaI0JpJFgM=" } ] } }
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private void WriteVpnSettings(string extra) => File.WriteAllText(_settingsPath, $$"""
        {
          "Auth": { "Enabled": false },
          "Media": { "Enabled": true },
          "MediaDownloads": { "Enabled": true, "UseVpn": true, "ProwlarrApiKey": "test-key", "IdleMinutes": 1, {{extra}} }
        }
        """);

    [Fact]
    public async Task Mullvad_AccountNumberAlone_RegistersOneDevice_AndWritesTheTunnelsWireGuardFile()
    {
        WriteVpnSettings($$"""
            "VpnProvider": "mullvad", "MullvadAccountNumber": "1234 5678 1234 5678", "MullvadLocation": "se"
            """);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.195.233.76", "Stockholm", null, "SE");

        var test = await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.True(test.GetProperty("ok").GetBoolean(), test.GetRawText());

        // Only the active Swedish server qualifies; the key in the file is the one registered.
        var config = _stack.WireGuard!;
        Assert.Contains("Endpoint = 185.195.233.76:51820", config);
        Assert.Contains("PublicKey = MkP/Jytkg51/Y/EostONjIN6YaFRpsAYiNKMX27/CAY=", config);
        Assert.Contains("Address = 10.64.0.1/32,fc00:bbbb::1/128", config);
        Assert.Contains("DNS = 10.64.0.1", config);
        var privateKey = System.Text.RegularExpressions.Regex.Match(config, @"PrivateKey = (\S+)").Groups[1].Value;
        var derived = Convert.ToBase64String(new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(Convert.FromBase64String(privateKey)).GeneratePublicKey().GetEncoded());
        Assert.Equal(Assert.Single(_mullvad.RegisteredKeys), derived);

        // The services stop and start again later: same device, no new slot used.
        _stack.State = MediaDownloadServiceStates.Stopped;
        await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Single(_mullvad.RegisteredKeys);

        // A new location picks a server there and restarts the tunnel, still with the same device.
        WriteVpnSettings($$"""
            "VpnProvider": "mullvad", "MullvadAccountNumber": "1234567812345678", "MullvadLocation": "nl-ams"
            """);
        var stops = _stack.Stops;
        await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Contains("Endpoint = 193.32.249.66:51820", _stack.WireGuard);
        Assert.Contains($"PrivateKey = {privateKey}", _stack.WireGuard);
        Assert.Single(_mullvad.RegisteredKeys);
        Assert.Equal(stops + 1, _stack.Stops);

        // Removed on mullvad.net: noticed when the tunnel will not come up, and a new one is made.
        _mullvad.Removed.Add("dev1");
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Down, Error: "The VPN is not reaching the internet (timed out)");
        var failed = await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Contains("removed from your Mullvad account", failed.GetProperty("message").GetString());
        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "193.32.249.66");
        await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Equal(2, _mullvad.RegisteredKeys.Count);
    }

    [Fact]
    public async Task Mullvad_AFullAccountOrAWrongNumber_IsSaidPlainly()
    {
        WriteVpnSettings($$"""
            "VpnProvider": "mullvad", "MullvadAccountNumber": "1234567812345678", "MullvadLocation": "se"
            """);
        _mullvad.Full = true;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var full = await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.False(full.GetProperty("ok").GetBoolean());
        Assert.Contains("maximum of 5 devices", full.GetProperty("message").GetString());
        Assert.Equal(0, _stack.Starts);

        WriteVpnSettings($$"""
            "VpnProvider": "mullvad", "MullvadAccountNumber": "9999888877776666", "MullvadLocation": "se"
            """);
        var wrong = await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Contains("did not recognise that account number", wrong.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OtherWireGuardProvider_FieldsBecomeTheTunnelsFile_AndBadValuesAreRefusedOnSave()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var bad = await client.PutAsJsonAsync("/api/settings/media-downloads", new
        {
            enabled = true, useVpn = true, vpnProvider = "wireguard", idleMinutes = 5, preferredQuality = "1080p", allowedUsers = "admins",
            wireGuardPrivateKey = "not-a-key", wireGuardAddress = "10.2.0.2", wireGuardServerPublicKey = "", wireGuardServerEndpoint = "vpn.example.net"
        }, Ct);
        var errors = (await JsonAsync(bad, HttpStatusCode.BadRequest)).GetRawText();
        Assert.Contains("WireGuard Private Key", errors);
        Assert.Contains("WireGuard Address", errors);
        Assert.Contains("Server Public Key", errors);
        Assert.Contains("Server Endpoint", errors);

        var good = await client.PutAsJsonAsync("/api/settings/media-downloads", new
        {
            enabled = true, useVpn = true, vpnProvider = "wireguard", idleMinutes = 5, preferredQuality = "1080p", allowedUsers = "admins",
            prowlarrApiKey = "test-key", wireGuardPrivateKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=", wireGuardAddress = "10.2.0.2/32",
            wireGuardDns = "10.2.0.1", wireGuardServerPublicKey = "xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=", wireGuardServerEndpoint = "185.159.157.1:51820"
        }, Ct);
        await JsonAsync(good, HttpStatusCode.OK);

        _stack.Vpn = new MediaVpnStatus(MediaVpnStatus.Connected, "185.159.157.1");
        await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Equal("""
            [Interface]
            PrivateKey = AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=
            Address = 10.2.0.2/32
            DNS = 10.2.0.1

            [Peer]
            PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
            AllowedIPs = 0.0.0.0/0,::/0
            Endpoint = 185.159.157.1:51820

            """.ReplaceLineEndings("\n"), _stack.WireGuard);
        Assert.Empty(_mullvad.RegisteredKeys);

        // Back to the installed file: the tunnel is told to forget the typed-in one.
        WriteSettings(enabled: true);
        await JsonAsync(await client.PostAsync("/api/media/downloads/vpn/test", null, Ct), HttpStatusCode.OK);
        Assert.Null(_stack.WireGuard);
    }

    [Fact]
    public async Task MullvadLocations_ListCountriesAndCitiesWithAnActiveServer()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var options = (await JsonAsync(await client.GetAsync("/api/settings/media-downloads/mullvad-locations", Ct), HttpStatusCode.OK)).GetProperty("options");
        Assert.Equal(
            [("nl", "Netherlands - any city"), ("nl-ams", "Netherlands - Amsterdam"), ("se", "Sweden - any city"), ("se-sto", "Sweden - Stockholm")],
            options.EnumerateArray().Select(o => (o.GetProperty("value").GetString(), o.GetProperty("label").GetString())));
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Text(body));
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
