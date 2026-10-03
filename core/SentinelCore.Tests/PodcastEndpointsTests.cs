extern alias WebHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Podcasts tab through the real host (MUSIC_AND_PODCASTS.md §5-6), against a publisher on
// loopback. Login is off, so every request is the household ("" user key) and an admin.
public sealed class PodcastEndpointsTests : IDisposable
{
    private static readonly DateTimeOffset Day = DateTimeOffset.UtcNow.Date.AddDays(-3);
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly PodcastTestServer _server = new();
    private readonly RecordingPodcastNotifier _notifier = new();
    private readonly List<WebApplicationFactory<WebProgram>> _factories = [];

    public PodcastEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"podcast-web-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        WriteSettings(podcastsEnabled: true, organizerEnabled: true);
    }

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
        _server.Dispose();
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WriteSettings(bool podcastsEnabled, bool organizerEnabled, string extra = "") =>
        File.WriteAllText(_settingsPath, $$"""
            { "Auth": { "Enabled": false },
              "Podcasts": { "Enabled": {{(podcastsEnabled ? "true" : "false")}}, "AutoDownloadNew": false {{extra}} },
              "Organizer": { "Enabled": {{(organizerEnabled ? "true" : "false")}} } }
            """);

    private WebApplicationFactory<WebProgram> CreateFactory(string? dataDirectory = null, HttpMessageHandler? directory = null)
    {
        var factory = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(builder => builder
            .UseTestSettings(_settingsPath, dataDirectory ?? _dataDirectory)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IPodcastNotifier>(_notifier);
                if (directory is not null) services.AddHttpClient(PodcastDirectory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => directory);
            }));
        _factories.Add(factory);
        return factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) => await JsonAsync(await client.GetAsync(url, Ct));

    private string Feed(string title, int count, string extraPerItem = "") =>
        PodcastFeeds.Rss(title, string.Join("\n", Enumerable.Range(1, count).Select(n =>
            PodcastFeeds.Item($"{title}-{n}", $"{title} {n}", Day.AddHours(n), _server.Url($"/audio/{title}-{n}.mp3"),
                extraPerItem.Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture)), duration: "600"))));

    [Fact]
    public async Task EveryRoute_Answers404WithTheMessage_WhenPodcastsAreOff()
    {
        WriteSettings(podcastsEnabled: false, organizerEnabled: true);
        using var client = CreateFactory().CreateClient();
        foreach (var request in new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/api/podcasts/home"),
            new HttpRequestMessage(HttpMethod.Get, "/api/podcasts/status"),
            new HttpRequestMessage(HttpMethod.Get, "/api/podcasts"),
            new HttpRequestMessage(HttpMethod.Post, "/api/podcasts/subscriptions") { Content = JsonContent.Create(new { feedUrl = "https://x.example/feed" }) },
            new HttpRequestMessage(HttpMethod.Get, $"/api/podcasts/episodes/{Guid.NewGuid()}/audio")
        })
        {
            using var response = await client.SendAsync(request, Ct);
            var body = await JsonAsync(response, HttpStatusCode.NotFound);
            Assert.Equal("Podcasts are turned off in Settings.", body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Subscribe_Browse_Queue_Listen_Bookmark_Clip_AndUnfollow()
    {
        var audio = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();
        for (var n = 1; n <= 3; n++) _server.Bytes($"/audio/Show-{n}.mp3", audio, "audio/mpeg");
        _server.Bytes("/transcripts/1.vtt", Encoding.UTF8.GetBytes("WEBVTT\n\n00:00:00.000 --> 00:00:10.000\n<v Host>Welcome to the show about sourdough.\n\n00:00:10.000 --> 00:00:20.000\n<v Host>Starters need feeding every day.\n"), "text/vtt");
        _server.Bytes("/chapters/1.json", Encoding.UTF8.GetBytes("""{ "version": "1.2.0", "chapters": [ { "startTime": 0, "title": "Intro" }, { "startTime": 95, "title": "Interview" } ] }"""), "application/json+chapters");
        _server.Feed("/show.xml", Feed("Show", 3,
            $"""<itunes:season>1</itunes:season><podcast:transcript url="{_server.Url("/transcripts/{n}.vtt")}" type="text/vtt"/><podcast:chapters url="{_server.Url("/chapters/{n}.json")}" type="application/json+chapters"/>"""));

        var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Follow.
        using (var bad = await client.PostAsJsonAsync("/api/podcasts/subscriptions", new { feedUrl = _server.Url("/missing.xml") }, Ct))
            Assert.Contains("404", (await JsonAsync(bad, HttpStatusCode.BadRequest)).GetProperty("errors")[0].GetString());
        var podcast = await JsonAsync(await client.PostAsJsonAsync("/api/podcasts/subscriptions", new { feedUrl = _server.Url("/show.xml") }, Ct), HttpStatusCode.Created);
        var podcastId = podcast.GetProperty("id").GetString();
        Assert.Equal("Show", podcast.GetProperty("title").GetString());
        Assert.True(podcast.GetProperty("subscribed").GetBoolean());
        Assert.Equal(1, podcast.GetProperty("subscriberCount").GetInt32());
        Assert.Equal(3, podcast.GetProperty("episodeCount").GetInt32());
        Assert.Equal(3, podcast.GetProperty("unplayedCount").GetInt32());
        Assert.StartsWith($"/api/podcasts/{podcastId}/art", podcast.GetProperty("artUrl").GetString());
        Assert.False(podcast.GetProperty("settings").GetProperty("autoDownload").GetBoolean());
        Assert.True(podcast.GetProperty("mySettings").GetProperty("notify").GetBoolean());
        Assert.Equal(1, (await GetAsync(client, "/api/podcasts")).GetArrayLength());

        // Browse.
        var page = await GetAsync(client, $"/api/podcasts/{podcastId}/episodes?sort=oldest");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal([1], page.GetProperty("seasons").EnumerateArray().Select(s => s.GetInt32()));
        var episodes = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Show 1", "Show 2", "Show 3"], episodes.Select(e => e.GetProperty("title").GetString()));
        var first = episodes[0];
        var id1 = first.GetProperty("id").GetString();
        var id2 = episodes[1].GetProperty("id").GetString();
        var id3 = episodes[2].GetProperty("id").GetString();
        Assert.Equal($"/api/podcasts/episodes/{id1}/audio", first.GetProperty("audioUrl").GetString());
        Assert.Equal("none", first.GetProperty("download").GetProperty("state").GetString());
        Assert.True(first.GetProperty("hasTranscript").GetBoolean());
        Assert.Equal("feed", first.GetProperty("transcriptSource").GetString());
        Assert.True(first.GetProperty("hasChapters").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("progress").ValueKind);
        var detail = await GetAsync(client, $"/api/podcasts/episodes/{id1}");
        Assert.Equal("<p>About Show 1.</p>", detail.GetProperty("description").GetString());
        Assert.Equal("About Show 1.", detail.GetProperty("descriptionText").GetString());

        // Up Next.
        await client.PostAsJsonAsync("/api/podcasts/queue", new { episodeId = id1, position = "last" }, Ct);
        await client.PostAsJsonAsync("/api/podcasts/queue", new { episodeId = id2, position = "last" }, Ct);
        var queue = await JsonAsync(await client.PostAsJsonAsync("/api/podcasts/queue", new { episodeId = id3, position = "next" }, Ct));
        Assert.Equal([id3, id1, id2], queue.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.True(queue[0].GetProperty("inQueue").GetBoolean());
        queue = await JsonAsync(await client.PutAsJsonAsync("/api/podcasts/queue", new { episodeIds = new[] { id2, id1, Guid.NewGuid().ToString() } }, Ct));
        Assert.Equal([id2, id1], queue.EnumerateArray().Select(e => e.GetProperty("id").GetString()));

        // Listening: under 10 s saves nothing; 95 % is played and leaves Up Next.
        using (var tooSoon = await client.PostAsJsonAsync($"/api/podcasts/episodes/{id1}/progress", new { positionSeconds = 5, durationSeconds = 600 }, Ct))
            Assert.Equal("null", await tooSoon.Content.ReadAsStringAsync(Ct));
        var progress = await JsonAsync(await client.PostAsJsonAsync($"/api/podcasts/episodes/{id1}/progress", new { positionSeconds = 300, durationSeconds = 600, speed = 1.0, deviceName = "Test" }, Ct));
        Assert.False(progress.GetProperty("played").GetBoolean());
        Assert.Equal(50, progress.GetProperty("percent").GetDouble());
        var home = await GetAsync(client, "/api/podcasts/home");
        Assert.Equal(id1, home.GetProperty("continueListening")[0].GetProperty("id").GetString());
        Assert.Equal(3, home.GetProperty("newEpisodes").GetArrayLength());
        Assert.Equal(2, home.GetProperty("upNext").GetArrayLength());
        Assert.Equal(1, home.GetProperty("subscriptions").GetArrayLength());

        var fill = await GetAsync(client, "/api/podcasts/fill?minutes=15");
        // Up Next first: episode 2 (600 s) fits; episode 1 has 300 s left, which also fits.
        Assert.Equal([id2, id1], fill.GetProperty("episodes").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal(900, fill.GetProperty("totalSeconds").GetDouble());

        progress = await JsonAsync(await client.PostAsJsonAsync($"/api/podcasts/episodes/{id1}/progress", new { positionSeconds = 571, durationSeconds = 600 }, Ct));
        Assert.True(progress.GetProperty("played").GetBoolean());
        Assert.Equal([id2], (await GetAsync(client, "/api/podcasts/queue")).EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        var unplayed = await GetAsync(client, $"/api/podcasts/{podcastId}/episodes?filter=unplayed");
        Assert.Equal(2, unplayed.GetProperty("total").GetInt32());
        using (var unplay = await client.PostAsJsonAsync($"/api/podcasts/episodes/{id1}/played", new { played = false }, Ct))
            Assert.Equal("null", await unplay.Content.ReadAsStringAsync(Ct));
        using (var all = await client.PostAsJsonAsync($"/api/podcasts/{podcastId}/played", new { played = true }, Ct))
            Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
        Assert.Equal(0, (await GetAsync(client, $"/api/podcasts/{podcastId}")).GetProperty("unplayedCount").GetInt32());
        Assert.Empty((await GetAsync(client, "/api/podcasts/queue")).EnumerateArray());

        // The audio relays the publisher's file with the Range passed through.
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/podcasts/episodes/{id2}/audio?download=1"))
        {
            request.Headers.Range = new RangeHeaderValue(0, 99);
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync(Ct)).Length);
            Assert.Equal("bytes 0-99/50000", response.Content.Headers.ContentRange?.ToString());
            Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        }

        // Downloading puts it on the server, and then the file itself is served.
        await client.PostAsync($"/api/podcasts/episodes/{id2}/download", null, Ct);
        await PodcastTestKit.WaitForAsync(async () =>
            (await GetAsync(client, $"/api/podcasts/episodes/{id2}")).GetProperty("download").GetProperty("state").GetString() == "done", "the download");
        var hitsBefore = _server.Hits.GetValueOrDefault("/audio/Show-2.mp3");
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/podcasts/episodes/{id2}/audio"))
        {
            request.Headers.Range = new RangeHeaderValue(100, 199);
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(audio[100..200], await response.Content.ReadAsByteArrayAsync(Ct));
        }
        Assert.Equal(hitsBefore, _server.Hits.GetValueOrDefault("/audio/Show-2.mp3"));
        var status = await GetAsync(client, "/api/podcasts/status");
        Assert.Equal(50_000, status.GetProperty("storageBytes").GetInt64());
        Assert.Equal(53687091200, status.GetProperty("maxStorageBytes").GetInt64());
        Assert.False(status.GetProperty("transcripts").GetProperty("available").GetBoolean());

        // Chapters and the feed's transcript.
        var chapters = await GetAsync(client, $"/api/podcasts/episodes/{id1}/chapters");
        Assert.Equal(["Intro", "Interview"], chapters.EnumerateArray().Select(c => c.GetProperty("title").GetString()));
        Assert.Equal(95, chapters[0].GetProperty("endSeconds").GetDouble());
        Assert.Equal(600, chapters[1].GetProperty("endSeconds").GetDouble());
        var transcript = await GetAsync(client, $"/api/podcasts/episodes/{id1}/transcript");
        Assert.Equal("feed", transcript.GetProperty("source").GetString());
        Assert.Equal("Host", transcript.GetProperty("segments")[0].GetProperty("speaker").GetString());
        using (var none = await client.GetAsync($"/api/podcasts/episodes/{Guid.NewGuid()}/transcript", Ct))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        var hits = await GetAsync(client, "/api/podcasts/transcripts/search?q=sourdough");
        Assert.Equal(id1, hits[0].GetProperty("episodeId").GetString());
        Assert.Equal("Show", hits[0].GetProperty("podcastTitle").GetString());
        // Both lines share one search block that starts at 0; a hit plays from the line with the words.
        var feeding = await GetAsync(client, "/api/podcasts/transcripts/search?q=feeding%20ever");
        Assert.Equal(10, feeding[0].GetProperty("startSeconds").GetDouble());
        using (var transcribe = await client.PostAsync($"/api/podcasts/episodes/{id1}/transcribe", null, Ct))
            Assert.Contains("turned off", (await JsonAsync(transcribe, HttpStatusCode.BadRequest)).GetProperty("error").GetString());

        // Bookmarks carry the transcript around them, and become Organizer notes.
        var bookmark = await JsonAsync(await client.PostAsJsonAsync($"/api/podcasts/episodes/{id1}/bookmarks", new { positionSeconds = 12, note = "feeding schedule" }, Ct), HttpStatusCode.Created);
        Assert.Equal("Welcome to the show about sourdough. Starters need feeding every day.", bookmark.GetProperty("transcriptSnippet").GetString());
        Assert.Equal("Show", bookmark.GetProperty("podcastTitle").GetString());
        var bookmarkId = bookmark.GetProperty("id").GetString();
        bookmark = await JsonAsync(await client.PutAsJsonAsync($"/api/podcasts/bookmarks/{bookmarkId}", new { note = "feed it daily" }, Ct));
        Assert.Equal("feed it daily", bookmark.GetProperty("note").GetString());
        Assert.Equal(1, (await GetAsync(client, $"/api/podcasts/bookmarks?episodeId={id1}")).GetArrayLength());
        Assert.Equal(1, (await GetAsync(client, $"/api/podcasts/episodes/{id1}")).GetProperty("bookmarkCount").GetInt32());

        var note = await JsonAsync(await client.PostAsync($"/api/podcasts/bookmarks/{bookmarkId}/to-note", null, Ct));
        var noteId = note.GetProperty("noteId").GetString();
        var organizer = factory.Services.GetRequiredService<IOrganizerStore>();
        var made = (await organizer.PullEverythingAsync(0, 500, Ct)).Items.Single(i => i.Id == noteId);
        Assert.Equal(OrganizerSpaceIds.Family, made.SpaceId);
        Assert.Equal("Show — Show 1", made.Fields["title"]!.GetValue<string>());
        Assert.Contains("**At 0:12**", made.Fields["body"]!.GetValue<string>());
        Assert.Contains("feed it daily", made.Fields["body"]!.GetValue<string>());
        WriteSettings(podcastsEnabled: true, organizerEnabled: false);
        using (var off = await client.PostAsync($"/api/podcasts/bookmarks/{bookmarkId}/to-note", null, Ct))
            Assert.Equal("The Organizer is turned off in Settings.", (await JsonAsync(off, HttpStatusCode.NotFound)).GetProperty("error").GetString());
        WriteSettings(podcastsEnabled: true, organizerEnabled: true);

        // Clips are 5 s to 3 min.
        foreach (var length in new[] { 4.9, 180.1 })
            using (var refused = await client.PostAsJsonAsync($"/api/podcasts/episodes/{id1}/clips", new { startSeconds = 0, durationSeconds = length }, Ct))
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Settings, per person and for the household.
        using (var bad = await client.PutAsJsonAsync($"/api/podcasts/{podcastId}/my-settings", new { playbackSpeed = 4.0 }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var mine = await JsonAsync(await client.PutAsJsonAsync($"/api/podcasts/{podcastId}/my-settings",
            new { playbackSpeed = 1.5, skipIntroSeconds = 30, notify = false, autoQueue = true, inBriefing = true, sort = "oldest" }, Ct));
        Assert.Equal(1.5, mine.GetProperty("mySettings").GetProperty("playbackSpeed").GetDouble());
        Assert.Equal("oldest", mine.GetProperty("mySettings").GetProperty("sort").GetString());
        var household = await JsonAsync(await client.PutAsJsonAsync($"/api/podcasts/{podcastId}/settings",
            new { autoDownload = true, keepEpisodes = 3, voiceBoost = true, trimSilence = false, transcribe = false }, Ct));
        Assert.Equal(3, household.GetProperty("settings").GetProperty("keepEpisodes").GetInt32());
        Assert.True(household.GetProperty("settings").GetProperty("voiceBoost").GetBoolean());

        // The shared audio code's view of it.
        var source = factory.Services.GetRequiredService<IPodcastAudioSource>();
        Assert.True(await source.EnabledAsync(Ct));
        var resolved = await source.ResolveEpisodesAsync("", true, [Guid.Parse(id3!), Guid.NewGuid(), Guid.Parse(id1!)], Ct);
        Assert.Equal([Guid.Parse(id3!), Guid.Parse(id1!)], resolved.Select(r => r.Id));
        Assert.Equal([Guid.Parse(id3!)], await source.QueueForSourceAsync("", AudioSourceTypes.Podcast, podcastId, Ct));

        // Unfollow keeps the show in ?all=true until an admin deletes it.
        using (var unfollow = await client.DeleteAsync($"/api/podcasts/{podcastId}/subscription", Ct))
            Assert.Equal(HttpStatusCode.NoContent, unfollow.StatusCode);
        Assert.Equal(0, (await GetAsync(client, "/api/podcasts")).GetArrayLength());
        Assert.Equal(1, (await GetAsync(client, "/api/podcasts?all=true")).GetArrayLength());
        using (var delete = await client.DeleteAsync($"/api/podcasts/{podcastId}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0, (await GetAsync(client, "/api/podcasts?all=true")).GetArrayLength());
        using (var gone = await client.GetAsync($"/api/podcasts/episodes/{id2}", Ct))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task AClip_IsCutByFfmpeg_AndServed()
    {
        if (!await PodcastTestKit.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        using var kit = new PodcastTestKit();
        _server.Bytes("/audio/Clip-1.mp3", await kit.MakeEpisodeAudioAsync(), "audio/mpeg");
        _server.Feed("/clip.xml", Feed("Clip", 1));
        using var client = CreateFactory().CreateClient();
        await JsonAsync(await client.PostAsJsonAsync("/api/podcasts/subscriptions", new { feedUrl = _server.Url("/clip.xml") }, Ct), HttpStatusCode.Created);
        var episodeId = (await GetAsync(client, "/api/podcasts/home")).GetProperty("newEpisodes")[0].GetProperty("id").GetString();

        var clip = await JsonAsync(await client.PostAsJsonAsync($"/api/podcasts/episodes/{episodeId}/clips", new { startSeconds = 8, durationSeconds = 6, title = "The good bit" }, Ct), HttpStatusCode.Created);
        var url = clip.GetProperty("url").GetString()!;
        Assert.Equal(6, clip.GetProperty("durationSeconds").GetDouble());
        using (var file = await client.GetAsync(url, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("audio/mp4", file.Content.Headers.ContentType?.MediaType);
            var path = Path.Combine(_root, "clip.m4a");
            await File.WriteAllBytesAsync(path, await file.Content.ReadAsByteArrayAsync(Ct), Ct);
            var probe = await kit.Audio.ProbeAsync(path, Ct);
            Assert.InRange(probe.DurationSeconds!.Value, 5.8, 6.2);
        }
        var clips = await GetAsync(client, "/api/podcasts/clips");
        Assert.Equal("The good bit", clips[0].GetProperty("title").GetString());
        using (var delete = await client.DeleteAsync($"/api/podcasts/clips/{clip.GetProperty("id").GetString()}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using (var gone = await client.GetAsync(url, Ct))
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // A clip asked to run past the end stops at the end and says how long it really is
        // (the episode is about 49 s, so 30 s from 40 s is about 9 s, not 30).
        var tail = await JsonAsync(await client.PostAsJsonAsync($"/api/podcasts/episodes/{episodeId}/clips", new { startSeconds = 40, durationSeconds = 30 }, Ct), HttpStatusCode.Created);
        Assert.InRange(tail.GetProperty("durationSeconds").GetDouble(), 8, 10);
        Assert.InRange((await GetAsync(client, "/api/podcasts/clips"))[0].GetProperty("durationSeconds").GetDouble(), 8, 10);
    }

    [Fact]
    public async Task Opml_ExportsAndImports_InTheBackground()
    {
        _server.Feed("/one.xml", Feed("One", 1));
        _server.Feed("/two.xml", Feed("Two", 2));
        using var client = CreateFactory().CreateClient();
        await JsonAsync(await client.PostAsJsonAsync("/api/podcasts/subscriptions", new { feedUrl = _server.Url("/one.xml") }, Ct), HttpStatusCode.Created);

        using (var export = await client.GetAsync("/api/podcasts/opml", Ct))
        {
            Assert.Equal("text/x-opml", export.Content.Headers.ContentType?.MediaType);
            Assert.Contains(_server.Url("/one.xml"), await export.Content.ReadAsStringAsync(Ct));
        }

        var opml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="2.0"><head><title>From my old app</title></head><body>
              <outline text="One" type="rss" xmlUrl="{_server.Url("/one.xml")}"/>
              <outline text="Folder"><outline text="Two (from OPML)" type="rss" xmlUrl="{_server.Url("/two.xml")}"/></outline>
              <outline text="Broken" type="rss" xmlUrl="not a feed"/>
            </body></opml>
            """;
        using var content = new StringContent(opml, Encoding.UTF8, "text/xml");
        var result = await JsonAsync(await client.PostAsync("/api/podcasts/opml", content, Ct));
        Assert.Equal(1, result.GetProperty("added").GetInt32());
        Assert.Equal(1, result.GetProperty("existing").GetInt32());
        Assert.Equal("not a feed", result.GetProperty("failed")[0].GetProperty("feedUrl").GetString());

        // Listed straight away, filled in by the background refresh.
        await PodcastTestKit.WaitForAsync(async () =>
            (await GetAsync(client, "/api/podcasts")).EnumerateArray().Any(p => p.GetProperty("title").GetString() == "Two" && p.GetProperty("episodeCount").GetInt32() == 2),
            "the imported feed to be fetched");
        Assert.Empty(_notifier.Sent);

        using var junk = new StringContent("<html>nope</html>", Encoding.UTF8, "text/xml");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/podcasts/opml", junk, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheDirectory_MarksShowsTheHouseholdHas()
    {
        _server.Feed("/daily.xml", Feed("Daily", 1));
        var feedUrl = _server.Url("/daily.xml");
        var directory = new DirectoryHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://itunes.apple.com/search", StringComparison.Ordinal))
            {
                Assert.Contains("country=gb", url);
                Assert.Contains("term=the%20daily", url);
                return $$"""
                    { "resultCount": 2, "results": [
                      { "kind": "podcast", "collectionId": 1200361736, "collectionName": "The Daily", "artistName": "NYT", "feedUrl": "{{feedUrl}}", "artworkUrl600": "https://img.example/600.jpg", "primaryGenreName": "Daily News", "trackCount": 2737 },
                      { "kind": "podcast", "collectionId": 42, "collectionName": "Other", "artistName": "Someone", "feedUrl": "https://other.example/feed", "primaryGenreName": "Comedy" } ] }
                    """;
            }
            if (url.StartsWith("https://itunes.apple.com/gb/rss/toppodcasts/limit=50/genre=", StringComparison.Ordinal))
                return """
                    { "feed": { "entry": [ { "im:name": { "label": "Funny Show" }, "im:artist": { "label": "Comic" }, "id": { "attributes": { "im:id": "77" } },
                      "im:image": [ { "label": "https://img.example/55x55bb.png" }, { "label": "https://img.example/170x170bb.png" } ], "category": { "attributes": { "label": "Comedy" } } } ] } }
                    """;
            if (url.StartsWith("https://itunes.apple.com/lookup", StringComparison.Ordinal))
            {
                Assert.Contains("id=77", url);
                return """{ "results": [ { "kind": "podcast", "collectionId": 77, "collectionName": "Funny Show", "feedUrl": "https://funny.example/feed", "trackCount": 12 } ] }""";
            }
            return null;
        });
        WriteSettings(podcastsEnabled: true, organizerEnabled: true, extra: """, "DirectoryCountry": "gb" """);
        using var client = CreateFactory(directory: directory).CreateClient();
        await JsonAsync(await client.PostAsJsonAsync("/api/podcasts/subscriptions", new { feedUrl }, Ct), HttpStatusCode.Created);

        var results = await GetAsync(client, "/api/podcasts/directory/search?q=the%20daily");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal(1200361736, results[0].GetProperty("itunesId").GetInt64());
        Assert.True(results[0].GetProperty("subscribed").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, results[0].GetProperty("podcastId").ValueKind);
        Assert.Equal(JsonValueKind.Null, results[1].GetProperty("podcastId").ValueKind);
        Assert.False(results[1].GetProperty("subscribed").GetBoolean());

        var top = await GetAsync(client, "/api/podcasts/directory/top?genre=1303");
        Assert.Equal("https://funny.example/feed", top[0].GetProperty("feedUrl").GetString());
        Assert.Equal("Comedy", top[0].GetProperty("genre").GetString());
        Assert.Equal(12, top[0].GetProperty("episodeCount").GetInt32());

        // The household follows a Technology show; the Technology chart (minus what it has) is
        // what it is recommended.
        var recommended = await GetAsync(client, "/api/podcasts/recommended");
        Assert.Equal(["Funny Show"], recommended.EnumerateArray().Select(r => r.GetProperty("title").GetString()));

        // Look before you follow.
        var preview = await GetAsync(client, $"/api/podcasts/preview?feedUrl={Uri.EscapeDataString(feedUrl)}");
        Assert.Equal("Daily", preview.GetProperty("podcast").GetProperty("title").GetString());
        Assert.True(preview.GetProperty("podcast").GetProperty("subscribed").GetBoolean());
        Assert.Equal("Daily 1", preview.GetProperty("episodes")[0].GetProperty("title").GetString());
        using (var notAFeed = await client.GetAsync($"/api/podcasts/preview?feedUrl={Uri.EscapeDataString(_server.Url("/none.xml"))}", Ct))
            Assert.Equal(HttpStatusCode.BadRequest, notAFeed.StatusCode);

        var genres = await GetAsync(client, "/api/podcasts/directory/genres");
        Assert.Contains(genres.EnumerateArray(), g => g.GetProperty("id").GetInt32() == 1489 && g.GetProperty("name").GetString() == "News");
        using (var empty = await client.GetAsync("/api/podcasts/directory/search?q=", Ct))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task TheSettingsSection_RefusesANetworkFolderAndABadCountry()
    {
        using var client = CreateFactory().CreateClient();
        using (var share = await client.PutAsJsonAsync("/api/settings/podcasts", new { enabled = true, downloadFolder = "smb://nas/podcasts", refreshMinutes = 60, directoryCountry = "us", whisperThreads = 4 }, Ct))
            Assert.Contains("this server's own disks", (await JsonAsync(share, HttpStatusCode.BadRequest)).GetProperty("errors")[0].GetString());
        using (var country = await client.PutAsJsonAsync("/api/settings/podcasts", new { enabled = true, refreshMinutes = 60, directoryCountry = "usa", whisperThreads = 4 }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, country.StatusCode);
        using (var range = await client.PutAsJsonAsync("/api/settings/podcasts", new { enabled = true, refreshMinutes = 5, directoryCountry = "us", whisperThreads = 4 }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, range.StatusCode);
        var folder = Path.Combine(_root, "podcast-downloads");
        using (var ok = await client.PutAsJsonAsync("/api/settings/podcasts", new { enabled = true, downloadFolder = folder, refreshMinutes = 30, directoryCountry = "GB", whisperThreads = 2, maxStorageGb = 10, keepEpisodes = 5 }, Ct))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var settings = (await GetAsync(client, "/api/settings")).GetProperty("podcasts");
        Assert.Equal(folder, settings.GetProperty("downloadFolder").GetString());
        Assert.Equal("gb", settings.GetProperty("directoryCountry").GetString());
        Assert.Equal(folder, (await GetAsync(client, "/api/podcasts/status")).GetProperty("downloadFolder").GetString());

        var schema = await GetAsync(client, "/api/settings/schema");
        var section = schema.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "podcasts");
        Assert.Equal("PODCASTS", section.GetProperty("title").GetString());
        Assert.Equal("/api/podcasts/refresh", section.GetProperty("extraAction").GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task TheBackup_CarriesPodcasts_AndRestoresThemOntoAnotherServer()
    {
        _server.Feed("/kept.xml", Feed("Kept", 2));
        string exported;
        using (var client = CreateFactory().CreateClient())
        {
            var podcast = await JsonAsync(await client.PostAsJsonAsync("/api/podcasts/subscriptions", new { feedUrl = _server.Url("/kept.xml") }, Ct), HttpStatusCode.Created);
            var episodeId = (await GetAsync(client, $"/api/podcasts/{podcast.GetProperty("id").GetString()}/episodes")).GetProperty("items")[0].GetProperty("id").GetString();
            await client.PostAsJsonAsync($"/api/podcasts/episodes/{episodeId}/progress", new { positionSeconds = 120, durationSeconds = 600 }, Ct);
            await client.PostAsJsonAsync("/api/podcasts/queue", new { episodeId, position = "last" }, Ct);
            await client.PostAsJsonAsync($"/api/podcasts/episodes/{episodeId}/bookmarks", new { positionSeconds = 30, note = "kept" }, Ct);
            using var export = await client.GetAsync("/api/backup/export", Ct);
            exported = await export.Content.ReadAsStringAsync(Ct);
            var podcasts = JsonDocument.Parse(exported).RootElement.GetProperty("podcasts");
            Assert.Equal(1, podcasts.GetProperty("shows").GetArrayLength());
            Assert.Equal(1, podcasts.GetProperty("progress").GetArrayLength());
            Assert.Equal(1, podcasts.GetProperty("queue").GetArrayLength());
            Assert.Equal(1, podcasts.GetProperty("bookmarks").GetArrayLength());
        }

        var elsewhere = Path.Combine(_root, "rebuilt");
        Directory.CreateDirectory(elsewhere);
        using var rebuilt = CreateFactory(elsewhere).CreateClient();
        using var content = new StringContent(exported, Encoding.UTF8, "application/json");
        var restored = await JsonAsync(await rebuilt.PostAsync("/api/backup/import", content, Ct));
        Assert.True(restored.GetProperty("podcastItemsRestored").GetInt32() >= 4);

        var shows = await GetAsync(rebuilt, "/api/podcasts");
        Assert.Equal("Kept", shows[0].GetProperty("title").GetString());
        // The first refresh brings the episodes back with the ids the progress points at.
        await rebuilt.PostAsync($"/api/podcasts/{shows[0].GetProperty("id").GetString()}/refresh", null, Ct);
        var queue = await GetAsync(rebuilt, "/api/podcasts/queue");
        Assert.Equal(120, queue[0].GetProperty("progress").GetProperty("positionSeconds").GetDouble());
        Assert.Equal(1, queue[0].GetProperty("bookmarkCount").GetInt32());
        Assert.Empty(_notifier.Sent);
    }

    private sealed class DirectoryHandler(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = answer(request);
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
