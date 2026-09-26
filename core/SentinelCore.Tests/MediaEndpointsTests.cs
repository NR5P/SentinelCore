extern alias WebHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Movies tab end to end, against real video files made by ffmpeg for the test: add a
// library, let it scan, browse it, direct-play an MP4, and play an MKV through the HLS remux the
// Apple TV depends on. Skipped on a machine without ffmpeg, like the other real-ffmpeg tests.
public sealed class MediaEndpointsTests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly string _movies;
    private readonly string _shows;

    public MediaEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"media-web-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "data");
        _movies = Path.Combine(_root, "library", "Movies");
        _shows = Path.Combine(_root, "library", "TV Shows");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_movies);
        Directory.CreateDirectory(_shows);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> FfmpegAvailableAsync() =>
        await FfmpegProcess.RunAsync("ffmpeg", ["-version"], TimeSpan.FromSeconds(10), NullLogger.Instance, "test", null, CancellationToken.None);

    private static async Task MakeVideoAsync(string path, string videoCodec, string audioCodec, int seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ok = await FfmpegProcess.RunAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size=320x180:rate=25",
            "-f", "lavfi", "-i", $"sine=duration={seconds}",
            "-c:v", videoCodec, "-pix_fmt", "yuv420p", "-g", "25", "-c:a", audioCodec, "-shortest", path
        ], TimeSpan.FromMinutes(1), NullLogger.Instance, "test video", null, CancellationToken.None);
        Assert.True(ok, $"ffmpeg could not make {path}");
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task WaitForScanAsync(HttpClient client, int expectedMovies, int expectedEpisodes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var status = await GetJsonAsync(client, "/api/media/status");
            if (!status.GetProperty("scanning").GetBoolean()
                && status.GetProperty("movieCount").GetInt32() >= expectedMovies
                && status.GetProperty("episodeCount").GetInt32() >= expectedEpisodes)
                return;
            await Task.Delay(250, Ct);
        }
        Assert.Fail("The library scan never finished.");
    }

    [Fact]
    public async Task ALibrary_ScansBrowsesAndPlays_DirectAndThroughHls()
    {
        if (!await FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");

        await MakeVideoAsync(Path.Combine(_movies, "Test Movie (2020) [1080p]", "Test.Movie.2020.1080p.BluRay.x264.mp4"), "libx264", "aac", 6);
        var episodePath = Path.Combine(_shows, "Test Show (2019) S01 (1080p)", "Season 1", "Test.Show.S01E01.Pilot.720p.WEB-DL.mkv");
        await MakeVideoAsync(episodePath, "libx264", "ac3", 14);
        await MakeVideoAsync(Path.Combine(_shows, "Test Show (2019) S01 (1080p)", "Season 1", "Test.Show.S01E02.The.Second.One.720p.WEB-DL.mkv"), "libx264", "ac3", 6);
        await File.WriteAllTextAsync(Path.ChangeExtension(episodePath, ".en.srt"),
            "1\n00:00:01,000 --> 00:00:03,000\nHello from a sidecar\n\n", Ct);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        Assert.True((await GetJsonAsync(client, "/api/media/enabled")).GetProperty("enabled").GetBoolean());

        // A folder that is not there is refused with a hint about network folders.
        using (var missing = await client.PostAsJsonAsync("/api/media/libraries", new { name = "Nope", kind = "movies", path = Path.Combine(_root, "not-here") }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains("smb://", await missing.Content.ReadAsStringAsync(Ct));
        }

        using (var created = await client.PostAsJsonAsync("/api/media/libraries", new { name = "Movies", kind = "movies", path = _movies }, Ct))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var created = await client.PostAsJsonAsync("/api/media/libraries", new { name = "TV", kind = "shows", path = _shows }, Ct))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await WaitForScanAsync(client, expectedMovies: 1, expectedEpisodes: 2);

        // Browsing.
        var movies = await GetJsonAsync(client, "/api/media/items?kind=movie");
        Assert.Equal(1, movies.GetProperty("total").GetInt32());
        var movie = movies.GetProperty("items")[0];
        Assert.Equal("Test Movie", movie.GetProperty("title").GetString());
        Assert.Equal(2020, movie.GetProperty("year").GetInt32());
        var movieId = movie.GetProperty("id").GetString();

        var home = await GetJsonAsync(client, "/api/media/home");
        Assert.Equal(1, home.GetProperty("recentMovies").GetArrayLength());
        Assert.Equal(1, home.GetProperty("recentShows").GetArrayLength());
        Assert.Equal(2, home.GetProperty("recentEpisodes").GetArrayLength());

        var movieDetail = await GetJsonAsync(client, $"/api/media/items/{movieId}");
        Assert.Equal("h264", movieDetail.GetProperty("file").GetProperty("videoCodec").GetString());
        Assert.InRange(movieDetail.GetProperty("durationSeconds").GetDouble(), 5, 7);

        var show = (await GetJsonAsync(client, "/api/media/items?kind=show")).GetProperty("items")[0];
        Assert.Equal("Test Show", show.GetProperty("title").GetString());
        Assert.Equal(2, show.GetProperty("episodeCount").GetInt32());
        var showDetail = await GetJsonAsync(client, $"/api/media/items/{show.GetProperty("id").GetString()}");
        var season = showDetail.GetProperty("seasons")[0];
        Assert.Equal(1, season.GetProperty("number").GetInt32());
        var pilot = season.GetProperty("episodes")[0];
        Assert.Equal("Pilot", pilot.GetProperty("title").GetString());
        Assert.Equal(1, pilot.GetProperty("episodeNumber").GetInt32());
        var pilotId = pilot.GetProperty("id").GetString();
        Assert.Equal(pilotId, show.GetProperty("nextEpisodeId").GetString());

        // Direct play: the MP4 itself, with byte ranges.
        var direct = await GetJsonAsync(client, $"/api/media/items/{movieId}/playback?video=h264&audio=aac");
        Assert.Equal("direct", direct.GetProperty("mode").GetString());
        using (var request = new HttpRequestMessage(HttpMethod.Get, direct.GetProperty("url").GetString()))
        {
            request.Headers.Range = new RangeHeaderValue(0, 99);
            using var ranged = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal(100, (await ranged.Content.ReadAsByteArrayAsync(Ct)).Length);
        }

        // HLS: an MKV with AC-3 audio for a client that only takes AAC - video copied, audio converted.
        var hls = await GetJsonAsync(client, $"/api/media/items/{pilotId}/playback?video=h264&audio=aac");
        Assert.Equal("hls", hls.GetProperty("mode").GetString());
        Assert.Equal("copy", hls.GetProperty("video").GetString());
        Assert.Equal("aac", hls.GetProperty("audio").GetString());
        Assert.Equal(pilotId is null ? null : season.GetProperty("episodes")[1].GetProperty("id").GetString(), hls.GetProperty("nextEpisodeId").GetString());
        var master = await client.GetStringAsync(hls.GetProperty("url").GetString(), Ct);
        Assert.Contains("TYPE=SUBTITLES", master);
        Assert.Contains("index.m3u8", master);
        var baseUrl = hls.GetProperty("url").GetString()![..(hls.GetProperty("url").GetString()!.LastIndexOf('/') + 1)];
        var index = await client.GetStringAsync(baseUrl + "index.m3u8", Ct);
        Assert.Contains("#EXT-X-START:TIME-OFFSET=0", index);
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4\"", index);
        var firstSegment = index.Split('\n').First(line => line.EndsWith(".m4s", StringComparison.Ordinal));
        using (var init = await client.GetAsync(baseUrl + "init.mp4", Ct)) Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        using (var segment = await client.GetAsync(baseUrl + firstSegment, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
            Assert.Equal("video/mp4", segment.Content.Headers.ContentType?.MediaType);
        }

        // The sidecar .srt comes out as WebVTT.
        var subtitle = hls.GetProperty("subtitles")[0];
        Assert.Equal("English", subtitle.GetProperty("label").GetString()!.Split(' ')[0]);
        var vtt = await client.GetStringAsync(subtitle.GetProperty("url").GetString(), Ct);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("Hello from a sidecar", vtt);

        // Progress: a peek is not a resume point, most of the way through is watched.
        using (var peek = await client.PostAsJsonAsync($"/api/media/items/{pilotId}/progress", new { positionSeconds = 5, durationSeconds = 14 }, Ct))
            Assert.Equal("null", await peek.Content.ReadAsStringAsync(Ct));
        using (var done = await client.PostAsJsonAsync($"/api/media/items/{pilotId}/progress", new { positionSeconds = 13.5, durationSeconds = 14 }, Ct))
            Assert.True((await done.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("watched").GetBoolean());
        var afterPilot = (await GetJsonAsync(client, "/api/media/items?kind=show")).GetProperty("items")[0];
        Assert.Equal(1, afterPilot.GetProperty("watchedEpisodeCount").GetInt32());
        Assert.Equal(season.GetProperty("episodes")[1].GetProperty("id").GetString(), afterPilot.GetProperty("nextEpisodeId").GetString());
        // "Next up" on Home is the episode after the one just finished.
        var upNext = (await GetJsonAsync(client, "/api/media/home")).GetProperty("continueWatching");
        Assert.Equal(season.GetProperty("episodes")[1].GetProperty("id").GetString(), upNext[0].GetProperty("id").GetString());

        // A backup carries the libraries and the watched state.
        var backup = await GetJsonAsync(client, "/api/backup/export");
        Assert.Equal(2, backup.GetProperty("media").GetProperty("libraries").GetArrayLength());
        Assert.Equal(1, backup.GetProperty("media").GetProperty("progress").GetArrayLength());
    }

    [Fact]
    public async Task TurnedOff_HidesEverythingButTheFlag()
    {
        File.WriteAllText(_settingsPath, """{ "Auth": { "Enabled": false }, "Media": { "Enabled": false } }""");
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        Assert.False((await GetJsonAsync(client, "/api/media/enabled")).GetProperty("enabled").GetBoolean());
        using var home = await client.GetAsync("/api/media/home", Ct);
        Assert.Equal(HttpStatusCode.NotFound, home.StatusCode);
        Assert.Contains("turned off", await home.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ItemIds_SurviveARescan_SoWatchedStateDoes()
    {
        Directory.CreateDirectory(Path.Combine(_movies, "Film (2001)"));
        File.WriteAllBytes(Path.Combine(_movies, "Film (2001)", "Film.2001.mkv"), [1, 2, 3]);
        var library = new MediaLibrary(Guid.NewGuid(), "Movies", MediaLibraryKinds.Movies, _movies, DateTimeOffset.UtcNow);
        var first = MediaScanner.WalkMovies(library).Single().Id;
        var second = MediaScanner.WalkMovies(library).Single().Id;
        Assert.Equal(first, second);

        using var store = new SqliteMediaStore(_dataDirectory);
        await store.SaveLibraryAsync(library, Ct);
        await store.UpsertItemsAsync(MediaScanner.WalkMovies(library), Ct);
        await store.SetProgressAsync(new MediaProgress("sam", first, 600, 6000, false, DateTimeOffset.UtcNow), Ct);
        await store.UpsertItemsAsync(MediaScanner.WalkMovies(library), Ct);
        Assert.Equal(600, Assert.Single(await store.GetProgressAsync("sam", Ct)).PositionSeconds);

        // Newest edit wins on restore: an older backup row does not roll anyone back.
        await store.RestoreProgressAsync([new MediaProgress("sam", first, 10, 6000, false, DateTimeOffset.UtcNow.AddDays(-1))], Ct);
        Assert.Equal(600, Assert.Single(await store.GetProgressAsync("sam", Ct)).PositionSeconds);

        // A library whose share went away keeps its catalog rather than emptying the shelf.
        Assert.Equal(0, await store.ReconcileMissingAsync(library.Id, new HashSet<Guid> { first }, Ct));
    }
}
