extern alias WebHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Web;
using Xunit;
using AccountRole = SentinelCore.Web.AccountRole;
using IAccountStore = SentinelCore.Web.IAccountStore;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Music tab end to end through the dashboard's API, against real audio files made by ffmpeg:
// the on/off gate, adding and scanning a library, browsing and search, art, lyrics, streaming
// with and without conversion, counting plays, loves and ratings, mixes, playlists and who may
// change them, and the backup carrying all of it to another install.
public sealed class MusicEndpointsTests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly string _music;

    public MusicEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"music-web-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "data");
        _music = Path.Combine(_root, "library", "Music");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_music);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(_settingsPath, """{ "Auth": { "Enabled": false }, "Music": { "FetchArtworkOnline": false, "FetchLyricsOnline": false } }""");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory(string? settingsPath = null, string? dataDirectory = null) => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath ?? _settingsPath, dataDirectory ?? _dataDirectory));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> SendJsonAsync(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{method} {url} -> {(int)response.StatusCode} (wanted {(int)expected}): {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task WaitForScanAsync(HttpClient client, int tracks)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var status = await GetJsonAsync(client, "/api/music/status");
            if (!status.GetProperty("scanning").GetBoolean() && status.GetProperty("trackCount").GetInt32() >= tracks) return;
            await Task.Delay(200, Ct);
        }
        Assert.Fail("The music scan never finished.");
    }

    private async Task MakeLibraryAsync()
    {
        var cover = Path.Combine(_root, "cover.png");
        await MusicTestAudio.MakeImageAsync(cover, "red", 640);
        var rumours = Path.Combine(_music, "Fleetwood Mac", "Rumours");
        await MusicTestAudio.MakeAsync(Path.Combine(rumours, "01 Second Hand News.mp3"),
            MusicTestAudio.Tags("Second Hand News", "Fleetwood Mac", "Rumours", 1, "Fleetwood Mac", "Rock", 1977), coverImage: cover);
        await MusicTestAudio.MakeAsync(Path.Combine(rumours, "02 Dreams.flac"),
            MusicTestAudio.Tags("Dreams", "Fleetwood Mac", "Rumours", 2, "Fleetwood Mac", "Rock", 1977), frequency: 550);
        await File.WriteAllTextAsync(Path.Combine(rumours, "02 Dreams.lrc"), "[00:01.00][00:04.00]Thunder only happens\n[00:02.50]when it's raining", Ct);
        await MusicTestAudio.MakeAsync(Path.Combine(_music, "Beyoncé", "Lemonade", "01 Formation.m4a"),
            MusicTestAudio.Tags("Formation", "Beyoncé", "Lemonade", 1, "Beyoncé", "Pop", 2016));
        await MusicTestAudio.MakeAsync(Path.Combine(_music, "Hits", "01.mp3"), MusicTestAudio.Tags("Hit One", "Alpha", "Hits", 1, genre: "Pop", year: 1995));
        await MusicTestAudio.MakeAsync(Path.Combine(_music, "Hits", "02.mp3"), MusicTestAudio.Tags("Hit Two", "Beta", "Hits", 2, genre: "Pop", year: 1995));
        await MusicTestAudio.MakeAsync(Path.Combine(_music, "Hits", "03.mp3"), MusicTestAudio.Tags("Hit Three", "Gamma feat. Alpha", "Hits", 3, genre: "Pop", year: 1995));
    }

    private static async Task AddLibraryAsync(HttpClient client, string path, int tracks)
    {
        await SendJsonAsync(client, HttpMethod.Post, "/api/music/libraries", new { name = "Music", path }, HttpStatusCode.Created);
        await WaitForScanAsync(client, tracks);
    }

    private static async Task<string> TrackIdAsync(HttpClient client, string title)
    {
        var tracks = await GetJsonAsync(client, $"/api/music/tracks?q={Uri.EscapeDataString(title)}");
        return tracks.GetProperty("items").EnumerateArray().First(t => t.GetProperty("title").GetString() == title).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task MusicTurnedOff_AnswersEveryRouteWithTheSameNotFound()
    {
        File.WriteAllText(_settingsPath, """{ "Auth": { "Enabled": false }, "Music": { "Enabled": false } }""");
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        foreach (var url in new[] { "/api/music/status", "/api/music/home", "/api/music/albums", $"/api/music/tracks/{Guid.NewGuid()}/stream" })
        {
            using var response = await client.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Music is turned off in Settings.", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }
        using var scan = await client.PostAsync("/api/music/scan", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, scan.StatusCode);

        // Turned back on from Settings, it answers again.
        await SendJsonAsync(client, HttpMethod.Put, "/api/settings/music", new
        {
            enabled = true, rescanHours = 12, fetchArtworkOnline = false, fetchLyricsOnline = false, transcodeBitrate = 256,
            pauseSpeakersOnAlarm = true, announceOnSpeakers = false
        }, HttpStatusCode.OK);
        Assert.True((await GetJsonAsync(client, "/api/music/status")).GetProperty("enabled").GetBoolean());
        var settings = await GetJsonAsync(client, "/api/settings");
        Assert.Equal(256, settings.GetProperty("music").GetProperty("transcodeBitrate").GetInt32());
        await SendJsonAsync(client, HttpMethod.Put, "/api/settings/music", new { enabled = true, transcodeBitrate = 999, rescanHours = 12 }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ALibrary_ScansBrowsesStreamsAndCounts()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        await MakeLibraryAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var missing = await SendJsonAsync(client, HttpMethod.Post, "/api/music/libraries", new { name = "", path = Path.Combine(_root, "nope") }, HttpStatusCode.BadRequest);
        Assert.Equal(2, missing.GetProperty("errors").GetArrayLength());
        await AddLibraryAsync(client, _music, 6);
        var duplicate = await SendJsonAsync(client, HttpMethod.Post, "/api/music/libraries", new { name = "Again", path = _music }, HttpStatusCode.BadRequest);
        Assert.Contains("already uses that folder", duplicate.GetProperty("errors")[0].GetString());

        var status = await GetJsonAsync(client, "/api/music/status");
        Assert.Equal((6, 3), (status.GetProperty("trackCount").GetInt32(), status.GetProperty("albumCount").GetInt32()));
        var library = status.GetProperty("libraries")[0];
        Assert.Equal(6, library.GetProperty("trackCount").GetInt32());
        Assert.False(library.GetProperty("network").GetBoolean());

        // Albums, the compilation and their details.
        var albums = await GetJsonAsync(client, "/api/music/albums?sort=year&order=asc");
        Assert.Equal(["Rumours", "Hits", "Lemonade"], albums.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        var hits = albums.GetProperty("items")[1];
        Assert.True(hits.GetProperty("compilation").GetBoolean());
        Assert.Equal("Various Artists", hits.GetProperty("artist").GetString());
        var rumours = albums.GetProperty("items")[0];
        Assert.Equal("/api/music/albums/" + rumours.GetProperty("id").GetString() + "/art", rumours.GetProperty("artUrl").GetString());
        var detail = await GetJsonAsync(client, $"/api/music/albums/{rumours.GetProperty("id").GetString()}");
        Assert.Equal(["Second Hand News", "Dreams"], detail.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("title").GetString()));
        Assert.Contains("flac 16/44.1", detail.GetProperty("formats").EnumerateArray().Select(f => f.GetString()));
        Assert.True(detail.GetProperty("totalBytes").GetInt64() > 0);
        var dreams = detail.GetProperty("tracks")[1];
        Assert.True(dreams.GetProperty("hasLyrics").GetBoolean());
        Assert.Equal("Fleetwood Mac/Rumours/02 Dreams.flac", dreams.GetProperty("path").GetString());   // login off: everyone is admin
        Assert.Equal(JsonValueKind.Null, dreams.GetProperty("replayGain").ValueKind);
        var dreamsId = dreams.GetProperty("id").GetString()!;

        // Artists: Alpha only appears on the compilation.
        var alpha = (await GetJsonAsync(client, "/api/music/artists?q=alp")).GetProperty("items")[0];
        Assert.Equal(0, alpha.GetProperty("albumCount").GetInt32());
        Assert.Equal(2, alpha.GetProperty("trackCount").GetInt32());
        var onlyAlbumArtists = await GetJsonAsync(client, "/api/music/artists?albumArtistsOnly=true");
        Assert.DoesNotContain("Alpha", onlyAlbumArtists.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        var alphaDetail = await GetJsonAsync(client, $"/api/music/artists/{alpha.GetProperty("id").GetString()}");
        Assert.Equal("Hits", alphaDetail.GetProperty("appearsOn")[0].GetProperty("title").GetString());

        // Search: start of words, case and accents ignored.
        var search = await GetJsonAsync(client, "/api/music/search?q=beyonce");
        Assert.Equal("Beyoncé", search.GetProperty("artists")[0].GetProperty("name").GetString());
        Assert.Equal("Lemonade", search.GetProperty("albums")[0].GetProperty("title").GetString());
        Assert.Empty((await GetJsonAsync(client, "/api/music/search?q=eyonce")).GetProperty("artists").EnumerateArray());
        Assert.Contains("Pop", (await GetJsonAsync(client, "/api/music/search?q=po")).GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
        var genres = await GetJsonAsync(client, "/api/music/genres");
        Assert.Equal(4, genres.EnumerateArray().First(g => g.GetProperty("name").GetString() == "Pop").GetProperty("trackCount").GetInt32());

        // Folders.
        var top = await GetJsonAsync(client, "/api/music/folders");
        var libraryId = top.GetProperty("folders")[0].GetProperty("libraryId").GetString();
        var root = await GetJsonAsync(client, $"/api/music/folders?libraryId={libraryId}");
        Assert.Equal(["Beyoncé", "Fleetwood Mac", "Hits"], root.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        var fm = await GetJsonAsync(client, $"/api/music/folders?libraryId={libraryId}&path=Fleetwood%20Mac/Rumours");
        Assert.Equal("Fleetwood Mac", fm.GetProperty("parent").GetString());
        Assert.Equal(2, fm.GetProperty("tracks").GetArrayLength());

        // Art: the embedded cover, downscaled on request.
        using (var art = await client.GetAsync($"/api/music/albums/{rumours.GetProperty("id").GetString()}/art?size=128", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, art.StatusCode);
            Assert.Equal("image/jpeg", art.Content.Headers.ContentType?.MediaType);
            using var image = SixLabors.ImageSharp.Image.Load(await art.Content.ReadAsByteArrayAsync(Ct));
            Assert.Equal(128, image.Width);
        }
        using (var none = await client.GetAsync($"/api/music/albums/{hits.GetProperty("id").GetString()}/art", Ct))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        // Lyrics from the sidecar, with one line sung twice.
        var lyrics = await GetJsonAsync(client, $"/api/music/tracks/{dreamsId}/lyrics");
        Assert.True(lyrics.GetProperty("synced").GetBoolean());
        Assert.Equal("sidecar", lyrics.GetProperty("source").GetString());
        Assert.Equal([1.0, 2.5, 4.0], lyrics.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("time").GetDouble()));
        using (var noLyrics = await client.GetAsync($"/api/music/tracks/{await TrackIdAsync(client, "Formation")}/lyrics", Ct))
            Assert.Equal(HttpStatusCode.NotFound, noLyrics.StatusCode);

        // Streaming: the FLAC itself to a device that plays FLAC, with ranges...
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/music/tracks/{dreamsId}/stream?codecs=mp3,aac,alac,flac,wav,aiff"))
        {
            request.Headers.Range = new RangeHeaderValue(0, 99);
            using var original = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.PartialContent, original.StatusCode);
            Assert.Equal("flac", original.Headers.GetValues("X-Audio-Codec").Single());
            Assert.Equal("false", original.Headers.GetValues("X-Audio-Transcoded").Single());
            Assert.Equal("audio/flac", original.Content.Headers.ContentType?.MediaType);
            Assert.Equal(100, (await original.Content.ReadAsByteArrayAsync(Ct)).Length);
        }
        // ...and converted to AAC for one that does not, as a download with a name.
        using (var converted = await client.GetAsync($"/api/music/tracks/{dreamsId}/download?codecs=mp3,aac", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
            Assert.Equal("aac", converted.Headers.GetValues("X-Audio-Codec").Single());
            Assert.Equal("true", converted.Headers.GetValues("X-Audio-Transcoded").Single());
            Assert.Equal("audio/mp4", converted.Content.Headers.ContentType?.MediaType);
            Assert.Equal("Fleetwood Mac - Dreams.m4a", converted.Content.Headers.ContentDisposition?.FileNameStar ?? converted.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_dataDirectory, "audio-cache"), "*.m4a"));

        // Counting: too short counts as nothing, an early skip is a skip, a repeat within 5 s is
        // the same play.
        var playedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreamsId}/played", new { secondsListened = 3, completed = false, skipped = false }, HttpStatusCode.NoContent);
        await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreamsId}/played", new { secondsListened = 31, completed = true, skipped = false, playedAt, deviceName = "Kitchen" }, HttpStatusCode.NoContent);
        await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreamsId}/played", new { secondsListened = 31, completed = true, skipped = false, playedAt = playedAt.AddSeconds(3) }, HttpStatusCode.NoContent);
        var formationId = await TrackIdAsync(client, "Formation");
        await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{formationId}/played", new { secondsListened = 4, completed = false, skipped = true }, HttpStatusCode.NoContent);
        await SendJsonAsync(client, HttpMethod.Post, "/api/music/now-playing-track", new { trackId = dreamsId }, HttpStatusCode.NoContent);
        var dreamsNow = await GetJsonAsync(client, $"/api/music/tracks/{dreamsId}");
        Assert.Equal(1, dreamsNow.GetProperty("playCount").GetInt32());
        Assert.Equal(0, (await GetJsonAsync(client, $"/api/music/tracks/{formationId}")).GetProperty("playCount").GetInt32());

        // Loves and ratings are the caller's own, and show on every object.
        Assert.True((await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreamsId}/love", new { loved = true }, HttpStatusCode.OK)).GetProperty("loved").GetBoolean());
        Assert.Equal(4, (await SendJsonAsync(client, HttpMethod.Post, $"/api/music/albums/{rumours.GetProperty("id").GetString()}/rating", new { rating = 4 }, HttpStatusCode.OK)).GetProperty("rating").GetInt32());
        await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreamsId}/rating", new { rating = 9 }, HttpStatusCode.BadRequest);
        Assert.True((await GetJsonAsync(client, $"/api/music/tracks/{dreamsId}")).GetProperty("loved").GetBoolean());
        Assert.Single((await GetJsonAsync(client, "/api/music/tracks?loved=true")).GetProperty("items").EnumerateArray());

        // Home.
        var home = await GetJsonAsync(client, "/api/music/home");
        Assert.Equal(6, home.GetProperty("trackCount").GetInt32());
        Assert.Equal("Rumours", home.GetProperty("recentlyPlayed")[0].GetProperty("album").GetProperty("title").GetString());
        Assert.Equal("album", home.GetProperty("recentlyPlayed")[0].GetProperty("kind").GetString());
        Assert.Equal("Dreams", home.GetProperty("lovedTracks")[0].GetProperty("title").GetString());
        Assert.Equal(3, home.GetProperty("recentlyAdded").GetArrayLength());
        Assert.Contains(home.GetProperty("decades").EnumerateArray(), d => d.GetProperty("label").GetString() == "70s" && d.GetProperty("trackCount").GetInt32() == 2);
        Assert.Contains(home.GetProperty("mixes").EnumerateArray(), m => m.GetProperty("id").GetString() == "daily-1");
        Assert.Contains(home.GetProperty("mixes").EnumerateArray(), m => m.GetProperty("id").GetString() == "loved");
        Assert.Equal(JsonValueKind.Null, home.GetProperty("onThisDay").ValueKind);

        // Radio from a track starts with it; mixes and shuffle-all play.
        var radio = await GetJsonAsync(client, $"/api/music/mix?seedType=track&seedId={formationId}&limit=10");
        Assert.Equal("Formation Radio", radio.GetProperty("name").GetString());
        Assert.Equal(formationId, radio.GetProperty("tracks")[0].GetProperty("id").GetString());
        Assert.NotEmpty((await GetJsonAsync(client, "/api/music/mix?mix=loved")).GetProperty("tracks").EnumerateArray());
        Assert.Equal(6, (await GetJsonAsync(client, "/api/music/shuffle")).GetProperty("tracks").GetArrayLength());
        await SendJsonAsync(client, HttpMethod.Get, "/api/music/mix?seedType=weather&seedId=x", null, HttpStatusCode.BadRequest);
        await SendJsonAsync(client, HttpMethod.Get, "/api/music/mix?mix=daily-9", null, HttpStatusCode.NotFound);

        // The shared audio code's view of the same thing.
        var source = factory.Services.GetRequiredService<IMusicAudioSource>();
        var stats = JsonSerializer.SerializeToElement(await source.StatsAsync("", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1), Ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal((1, 1, 1), (stats.GetProperty("plays").GetInt32(), stats.GetProperty("skips").GetInt32(), stats.GetProperty("longestStreakDays").GetInt32()));
        Assert.Equal("Fleetwood Mac", stats.GetProperty("topArtists")[0].GetProperty("artist").GetProperty("name").GetString());
        Assert.Equal(24, stats.GetProperty("byHour").GetArrayLength());
        Assert.Equal(7, stats.GetProperty("byWeekday").GetArrayLength());
        Assert.Equal(1, stats.GetProperty("newArtists").GetInt32());
        Assert.Equal("Dreams", stats.GetProperty("firstPlay").GetProperty("track").GetProperty("title").GetString());
        var albumQueue = await source.QueueForSourceAsync("", AudioSourceTypes.Album, rumours.GetProperty("id").GetString(), Ct);
        Assert.Equal(2, albumQueue.Count);
        var resolved = await source.ResolveTracksAsync("", false, [Guid.Parse(dreamsId), Guid.NewGuid()], Ct);
        Assert.Null(((MusicTrackDto)Assert.Single(resolved).Dto).Path);
        Assert.Equal(6, (await source.QueueForSourceAsync("", AudioSourceTypes.Shuffle, null, Ct)).Count);
        Assert.True(await source.HasAlbumAsync("fleetwood mac", "RUMOURS", Ct));
        Assert.InRange(await source.LosslessShareAsync(Ct), 0.16, 0.17);
    }

    [Fact]
    public async Task Playlists_BelongToTheirOwner_UnlessSharedOrCollaborative()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        await MakeLibraryAsync();
        using var factory = CreateFactory();
        using (var admin = factory.CreateClient()) await AddLibraryAsync(admin, _music, 6);
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            foreach (var name in new[] { "nathan", "terry" })
                await accounts.CreateAsync(name, PasswordHasher.Hash("correct horse battery"), AccountRole.Viewer, null, Ct);
        }
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, Ct))!.AsObject();
        settings["Auth"] = new JsonObject { ["Enabled"] = true };
        await File.WriteAllTextAsync(_settingsPath, settings.ToJsonString(), Ct);

        using var nathan = factory.CreateClient();
        using var terry = factory.CreateClient();
        foreach (var (client, name) in new[] { (nathan, "nathan"), (terry, "terry") })
            await SendJsonAsync(client, HttpMethod.Post, "/api/auth/login", new { Username = name, Password = "correct horse battery" }, HttpStatusCode.OK);

        // Viewers cannot manage libraries, and never see paths.
        await SendJsonAsync(nathan, HttpMethod.Post, "/api/music/libraries", new { name = "Mine", path = _music }, HttpStatusCode.Forbidden);
        var dreams = await TrackIdAsync(nathan, "Dreams");
        var formation = await TrackIdAsync(nathan, "Formation");
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync(nathan, $"/api/music/tracks/{dreams}")).GetProperty("path").ValueKind);

        await SendJsonAsync(nathan, HttpMethod.Post, "/api/music/playlists", new { name = "" }, HttpStatusCode.BadRequest);
        var secret = await SendJsonAsync(nathan, HttpMethod.Post, "/api/music/playlists",
            new { name = "Secret", trackIds = new[] { dreams, dreams, Guid.NewGuid().ToString() } }, HttpStatusCode.Created);
        Assert.Equal(2, secret.GetProperty("trackCount").GetInt32());
        Assert.True(secret.GetProperty("mine").GetBoolean());
        Assert.Equal("nathan", secret.GetProperty("ownerName").GetString());
        var secretId = secret.GetProperty("id").GetString();
        await SendJsonAsync(terry, HttpMethod.Get, $"/api/music/playlists/{secretId}", null, HttpStatusCode.NotFound);
        Assert.Empty((await GetJsonAsync(terry, "/api/music/playlists")).EnumerateArray());

        // Shared: Terry can play it but not change it.
        await SendJsonAsync(nathan, HttpMethod.Put, $"/api/music/playlists/{secretId}", new { name = "Road Trip", shared = true }, HttpStatusCode.OK);
        var shared = await GetJsonAsync(terry, $"/api/music/playlists/{secretId}");
        Assert.Equal("Road Trip", shared.GetProperty("name").GetString());
        Assert.False(shared.GetProperty("mine").GetBoolean());
        var entries = shared.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("entryId").GetString()).ToList();
        Assert.Equal(2, entries.Distinct().Count());
        await SendJsonAsync(terry, HttpMethod.Post, $"/api/music/playlists/{secretId}/tracks", new { trackIds = new[] { formation } }, HttpStatusCode.Forbidden);
        await SendJsonAsync(terry, HttpMethod.Put, $"/api/music/playlists/{secretId}", new { name = "Mine now" }, HttpStatusCode.Forbidden);
        await SendJsonAsync(terry, HttpMethod.Delete, $"/api/music/playlists/{secretId}", null, HttpStatusCode.Forbidden);

        // Collaborative: she can add, reorder and remove tracks, but still not rename or delete it.
        await SendJsonAsync(nathan, HttpMethod.Put, $"/api/music/playlists/{secretId}", new { collaborative = true }, HttpStatusCode.OK);
        var added = await SendJsonAsync(terry, HttpMethod.Post, $"/api/music/playlists/{secretId}/tracks", new { trackIds = new[] { formation }, position = 0 }, HttpStatusCode.OK);
        Assert.Equal(3, added.GetProperty("trackCount").GetInt32());
        var full = await GetJsonAsync(terry, $"/api/music/playlists/{secretId}");
        Assert.Equal("Formation", full.GetProperty("tracks")[0].GetProperty("title").GetString());
        var order = full.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("entryId").GetString()!).Reverse().ToList();
        await SendJsonAsync(terry, HttpMethod.Put, $"/api/music/playlists/{secretId}/order", new { entryIds = order.Take(2) }, HttpStatusCode.BadRequest);
        await SendJsonAsync(terry, HttpMethod.Put, $"/api/music/playlists/{secretId}/order", new { entryIds = order }, HttpStatusCode.NoContent);
        await SendJsonAsync(terry, HttpMethod.Delete, $"/api/music/playlists/{secretId}/tracks/{order[0]}", null, HttpStatusCode.NoContent);
        var after = await GetJsonAsync(nathan, $"/api/music/playlists/{secretId}");
        Assert.Equal(["Dreams", "Formation"], after.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("title").GetString()));
        await SendJsonAsync(terry, HttpMethod.Put, $"/api/music/playlists/{secretId}", new { name = "Ours" }, HttpStatusCode.Forbidden);

        // Pins are per person; a copy belongs to whoever made it.
        Assert.True((await SendJsonAsync(terry, HttpMethod.Post, $"/api/music/playlists/{secretId}/pin", new { pinned = true }, HttpStatusCode.OK)).GetProperty("pinned").GetBoolean());
        Assert.Single((await GetJsonAsync(terry, "/api/music/home")).GetProperty("pinned").EnumerateArray());
        Assert.Empty((await GetJsonAsync(nathan, "/api/music/home")).GetProperty("pinned").EnumerateArray());
        var copy = await SendJsonAsync(terry, HttpMethod.Post, $"/api/music/playlists/{secretId}/duplicate", null, HttpStatusCode.Created);
        Assert.True(copy.GetProperty("mine").GetBoolean());
        Assert.False(copy.GetProperty("shared").GetBoolean());
        Assert.Equal(2, copy.GetProperty("trackCount").GetInt32());

        // A smart playlist works its tracks out each time; its copy is a plain list of today's.
        var smart = await SendJsonAsync(nathan, HttpMethod.Post, "/api/music/playlists", new
        {
            name = "Pop",
            rules = new { match = "all", rules = new object[] { new { field = "genre", op = "is", value = "pop" } }, sort = "title", order = "asc", limit = 0 }
        }, HttpStatusCode.Created);
        Assert.True(smart.GetProperty("smart").GetBoolean());
        Assert.Equal(4, smart.GetProperty("trackCount").GetInt32());
        var smartId = smart.GetProperty("id").GetString();
        await SendJsonAsync(nathan, HttpMethod.Post, $"/api/music/playlists/{smartId}/tracks", new { trackIds = new[] { dreams } }, HttpStatusCode.BadRequest);
        var smartCopy = await SendJsonAsync(nathan, HttpMethod.Post, $"/api/music/playlists/{smartId}/duplicate", null, HttpStatusCode.Created);
        Assert.False(smartCopy.GetProperty("smart").GetBoolean());
        Assert.Equal(4, smartCopy.GetProperty("trackCount").GetInt32());
        var bad = await SendJsonAsync(nathan, HttpMethod.Post, "/api/music/playlists", new
        {
            name = "Bad", rules = new { rules = new object[] { new { field = "genre", op = "gte", value = 3 } } }
        }, HttpStatusCode.BadRequest);
        Assert.Contains("genre can be matched with", bad.GetProperty("errors")[0].GetString());

        // The .m3u8 export lists paths inside the library.
        using var m3u = await nathan.GetAsync($"/api/music/playlists/{secretId}/m3u", Ct);
        var text = await m3u.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith("#EXTM3U", text);
        Assert.Contains("\nFleetwood Mac/Rumours/02 Dreams.flac\n", text);
        Assert.Contains("#EXTINF:8,Fleetwood Mac - Dreams", text);

        await SendJsonAsync(nathan, HttpMethod.Delete, $"/api/music/playlists/{secretId}", null, HttpStatusCode.NoContent);
        await SendJsonAsync(terry, HttpMethod.Get, $"/api/music/playlists/{secretId}", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheBackup_CarriesLibrariesPlaysLovesRatingsAndPlaylists_ToAnotherInstall()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        await MakeLibraryAsync();
        string exported;
        string dreams;
        using (var source = CreateFactory())
        using (var client = source.CreateClient())
        {
            await AddLibraryAsync(client, _music, 6);
            dreams = await TrackIdAsync(client, "Dreams");
            await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreams}/played", new { secondsListened = 200, completed = true, skipped = false }, HttpStatusCode.NoContent);
            await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreams}/love", new { loved = true }, HttpStatusCode.OK);
            await SendJsonAsync(client, HttpMethod.Post, $"/api/music/tracks/{dreams}/rating", new { rating = 5 }, HttpStatusCode.OK);
            var playlist = await SendJsonAsync(client, HttpMethod.Post, "/api/music/playlists", new { name = "Keep", trackIds = new[] { dreams } }, HttpStatusCode.Created);
            await SendJsonAsync(client, HttpMethod.Post, $"/api/music/playlists/{playlist.GetProperty("id").GetString()}/pin", new { pinned = true }, HttpStatusCode.OK);
            using var response = await client.GetAsync("/api/backup/export", Ct);
            exported = await response.Content.ReadAsStringAsync(Ct);
        }
        var music = JsonDocument.Parse(exported).RootElement.GetProperty("music");
        Assert.Equal(1, music.GetProperty("libraries").GetArrayLength());
        Assert.Equal(1, music.GetProperty("plays").GetArrayLength());
        Assert.Equal(1, music.GetProperty("loves").GetArrayLength());
        Assert.Equal(1, music.GetProperty("ratings").GetArrayLength());
        Assert.Equal(1, music.GetProperty("playlists").GetArrayLength());
        Assert.Equal(1, music.GetProperty("pins").GetArrayLength());

        var otherSettings = Path.Combine(_root, "other.json");
        File.Copy(_settingsPath, otherSettings);
        using var destination = CreateFactory(otherSettings, Path.Combine(_root, "other-data"));
        using var other = destination.CreateClient();
        for (var pass = 0; pass < 2; pass++)
            await SendJsonAsync(other, HttpMethod.Post, "/api/backup/import", JsonDocument.Parse(exported).RootElement, HttpStatusCode.OK);
        await WaitForScanAsync(other, 6);

        // The rescan lands on the same track ids, so everything points at the right track again.
        var track = await GetJsonAsync(other, $"/api/music/tracks/{dreams}");
        Assert.Equal((1, true, 5), (track.GetProperty("playCount").GetInt32(), track.GetProperty("loved").GetBoolean(), track.GetProperty("rating").GetInt32()));
        var playlists = await GetJsonAsync(other, "/api/music/playlists");
        var only = Assert.Single(playlists.EnumerateArray());
        Assert.Equal(("Keep", 1, true), (only.GetProperty("name").GetString(), only.GetProperty("trackCount").GetInt32(), only.GetProperty("pinned").GetBoolean()));

        using var reExport = await other.GetAsync("/api/backup/export", Ct);
        var again = JsonDocument.Parse(await reExport.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("music");
        Assert.Equal(music.GetProperty("plays").GetRawText(), again.GetProperty("plays").GetRawText());
        Assert.Equal(music.GetProperty("playlists").GetRawText(), again.GetProperty("playlists").GetRawText());
    }
}
