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

// Libraries on another computer's shared folder, read over SMB by the server itself. The share
// is TestSmbServer: a real SMB2 server on a loopback port, so the sign-in, the listings, the
// seeking reads, the relay ffmpeg reads through and the HTTP endpoints are all exercised for real.
public sealed class MediaNetworkTests : IDisposable
{
    private readonly string _root;
    private readonly string _shared;
    private readonly string _dataDirectory;
    private readonly string _settingsPath;

    public MediaNetworkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"media-net-{Guid.NewGuid():N}");
        _shared = Path.Combine(_root, "shared");
        _dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // MARK: - Paths

    [Theory]
    [InlineData("smb://nas/Movies/Films", "nas", null, "Movies", "Films", "smb://nas/Movies/Films")]
    [InlineData(@"\\DESKTOP-4F2\Movies\Kids Films\", "DESKTOP-4F2", null, "Movies", "Kids Films", "smb://DESKTOP-4F2/Movies/Kids Films")]
    [InlineData("//192.168.1.20/Media", "192.168.1.20", null, "Media", "", "smb://192.168.1.20/Media")]
    [InlineData("smb://me@nas.local:4455/TV/", "nas.local", 4455, "TV", "", "smb://nas.local:4455/TV")]
    [InlineData("smb://nas:445", "nas", null, "", "", "smb://nas")]
    [InlineData("SMB://Nas/a/b/c", "Nas", null, "a", "b/c", "smb://Nas/a/b/c")]
    public void NetworkPaths_ParseWhatPeoplePaste(string text, string host, int? port, string share, string rest, string normalized)
    {
        Assert.True(MediaNetworkPath.TryParse(text, out var path));
        Assert.Equal(host, path.Host);
        Assert.Equal(port, path.Port);
        Assert.Equal(share, path.Share);
        Assert.Equal(rest, path.Rest);
        Assert.Equal(normalized, path.ToString());
    }

    [Theory]
    [InlineData("/mnt/movies")]
    [InlineData(@"C:\Movies")]
    [InlineData("smb://")]
    [InlineData("smb://nas:notaport/x")]
    public void NetworkPaths_RejectEverythingElse(string text) => Assert.False(MediaNetworkPath.TryParse(text, out _));

    [Fact]
    public void NetworkPaths_WalkUpAndDown()
    {
        Assert.True(MediaNetworkPath.TryParse("smb://nas/Movies/A/B", out var path));
        Assert.Equal("smb://nas/Movies/A", path.Parent()!.ToString());
        Assert.Equal("smb://nas", path.Parent()!.Parent()!.Parent()!.ToString());
        Assert.Null(path.Parent()!.Parent()!.Parent()!.Parent());
        Assert.Equal("A\\B", path.SmbPath);
        Assert.Equal("smb://nas/Movies/A/B/C.mkv", path.Child("C.mkv").ToString());
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=500-", 1000, 500, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=900-5000", 1000, 900, 999)]
    public void RelayRanges_Parse(string header, long length, long start, long end)
    {
        Assert.True(MediaFileRelay.TryParseRange(header, length, out var s, out var e));
        Assert.Equal((start, end), (s, e));
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=5-2", 1000)]
    [InlineData("bytes=0-1,5-9", 1000)]
    [InlineData("items=0-1", 1000)]
    public void RelayRanges_RefuseWhatCannotBeServed(string header, long length) =>
        Assert.False(MediaFileRelay.TryParseRange(header, length, out _, out _));

    // MARK: - The SMB file system

    private (MediaNetworkFileSystem Fs, MediaNetworkComputerStore Computers) NewFileSystem()
    {
        var computers = new MediaNetworkComputerStore(_dataDirectory);
        return (new MediaNetworkFileSystem(computers, new MediaNetworkResolver(NullLogger<MediaNetworkResolver>.Instance), NullLogger<MediaNetworkFileSystem>.Instance), computers);
    }

    private static MediaNetworkComputer SignIn(TestSmbServer server, string password = TestSmbServer.Password) =>
        new(Guid.NewGuid(), server.Host, TestSmbServer.Username, password, "", DateTimeOffset.UtcNow);

    [Fact]
    public void Share_ListsReadsAndSeeks()
    {
        Directory.CreateDirectory(Path.Combine(_shared, "Films", "Big Film (2004)"));
        var bytes = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(_shared, "Films", "Big Film (2004)", "Big.Film.2004.mkv"), bytes);
        using var server = new TestSmbServer(new Dictionary<string, string> { ["Movies"] = _shared, ["Hidden$"] = _shared });
        var (fs, computers) = NewFileSystem();
        using var _ = fs;
        computers.Save(SignIn(server));

        Assert.True(MediaNetworkPath.TryParse($"smb://{server.Host}", out var computer));
        Assert.Equal(["Movies"], fs.ListShares(computer));

        var films = $"smb://{server.Host}/Movies/Films";
        Assert.True(fs.DirectoryExists(films));
        Assert.False(fs.DirectoryExists(films + "/Nope"));
        var folder = Assert.Single(fs.List(films));
        Assert.True(folder.IsDirectory);
        Assert.Equal("Big Film (2004)", folder.Name);
        Assert.Equal(films + "/Big Film (2004)", folder.Path);

        var file = Assert.Single(fs.List(folder.Path));
        Assert.False(file.IsDirectory);
        Assert.Equal(bytes.Length, file.Length);
        Assert.Equal(file.Length, fs.Stat(file.Path)!.Length);
        Assert.Null(fs.Stat(folder.Path + "/missing.mkv"));

        using (var stream = fs.OpenRead(file.Path))
        {
            Assert.Equal(bytes.Length, stream.Length);
            var all = new MemoryStream();
            stream.CopyTo(all);
            Assert.Equal(bytes, all.ToArray());

            stream.Position = 2 * 1024 * 1024 + 7;
            var middle = new byte[4000];
            stream.ReadExactly(middle);
            Assert.Equal(bytes.AsSpan(2 * 1024 * 1024 + 7, 4000).ToArray(), middle);
        }
        // The connection went back to the pool and serves the next open.
        using (var again = fs.OpenRead(file.Path)) Assert.Equal(bytes[0], (byte)again.ReadByte());
    }

    [Fact]
    public void Share_ExplainsWhatWentWrong()
    {
        using var server = new TestSmbServer(new Dictionary<string, string> { ["Movies"] = _shared });
        var (fs, computers) = NewFileSystem();
        using var _ = fs;
        Assert.True(MediaNetworkPath.TryParse($"smb://{server.Host}", out var computer));

        // No saved sign-in and no guests: the client is told to ask for one.
        var noLogin = Assert.Throws<MediaNetworkException>(() => fs.ListShares(computer));
        Assert.True(noLogin.NeedsLogin);

        var wrong = Assert.Throws<MediaNetworkException>(() => fs.ListShares(computer, SignIn(server, "nope")));
        Assert.True(wrong.NeedsLogin);
        Assert.Contains("turned down", wrong.Message);

        computers.Save(SignIn(server));
        var noShare = Assert.Throws<MediaNetworkException>(() => fs.List($"smb://{server.Host}/Films"));
        Assert.Contains("no shared folder called \"Films\"", noShare.Message);

        Assert.True(MediaNetworkPath.TryParse("smb://127.0.0.1:1/Movies", out var closed));
        var unreachable = Assert.Throws<MediaNetworkException>(() => fs.List(closed.ToString()));
        Assert.Contains("Could not reach", unreachable.Message);
        Assert.False(unreachable.NeedsLogin);
    }

    [Fact]
    public void Share_LetsGuestsInWhenTheComputerDoes()
    {
        File.WriteAllBytes(Path.Combine(_shared, "Cartoon.1930.mp4"), [1, 2, 3]);
        using var server = new TestSmbServer(new Dictionary<string, string> { ["Public"] = _shared }, allowGuests: true);
        var (fs, _) = NewFileSystem();
        using var __ = fs;
        Assert.Equal("Cartoon.1930.mp4", Assert.Single(fs.List($"smb://{server.Host}/Public")).Name);
    }

    [Fact]
    public void Scanner_WalksAShareLikeALocalFolder()
    {
        Directory.CreateDirectory(Path.Combine(_shared, "Movies", "Some Film (1999)"));
        File.WriteAllBytes(Path.Combine(_shared, "Movies", "Some Film (1999)", "some.film.1999.1080p.mkv"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_shared, "Movies", "Some Film (1999)", "Poster.JPG"), [9]);
        Directory.CreateDirectory(Path.Combine(_shared, "TV", "A Show", "Season 2"));
        File.WriteAllBytes(Path.Combine(_shared, "TV", "A Show", "Season 2", "A.Show.S02E03.Third.mkv"), [1]);
        using var server = new TestSmbServer(new Dictionary<string, string> { ["Media"] = _shared });
        var (fs, computers) = NewFileSystem();
        using var _ = fs;
        computers.Save(SignIn(server));

        var movies = new MediaLibrary(Guid.NewGuid(), "Movies", MediaLibraryKinds.Movies, $"smb://{server.Host}/Media/Movies", DateTimeOffset.UtcNow);
        var movie = Assert.Single(MediaScanner.WalkMovies(movies, fs));
        Assert.Equal("Some Film", movie.Title);
        Assert.Equal(1999, movie.Year);
        Assert.Equal(3, movie.FileSize);
        Assert.Equal($"smb://{server.Host}/Media/Movies/Some Film (1999)/some.film.1999.1080p.mkv", movie.Path);
        Assert.EndsWith("/Poster.JPG", movie.PosterLocal);
        // The id is the same hash of the library-relative path a local library uses.
        Assert.Equal(MediaNaming.StableId(movies.Id, "Some Film (1999)/some.film.1999.1080p.mkv"), movie.Id);

        var tv = new MediaLibrary(Guid.NewGuid(), "TV", MediaLibraryKinds.Shows, $"smb://{server.Host}/Media/TV", DateTimeOffset.UtcNow);
        var items = MediaScanner.WalkShows(tv, fs);
        Assert.Equal("A Show", items.Single(i => i.Kind == MediaKinds.Show).Title);
        var episode = items.Single(i => i.Kind == MediaKinds.Episode);
        Assert.Equal((2, 3), (episode.SeasonNumber, episode.EpisodeNumber));
    }

    [Fact]
    public async Task Relay_ServesShareFilesToLoopbackWithRanges()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(_shared, "clip.mkv"), bytes);
        using var server = new TestSmbServer(new Dictionary<string, string> { ["Movies"] = _shared });
        var (fs, computers) = NewFileSystem();
        using var _ = fs;
        computers.Save(SignIn(server));
        using var relay = new MediaFileRelay(fs, NullLogger<MediaFileRelay>.Instance);

        var url = relay.UrlFor($"smb://{server.Host}/Movies/clip.mkv");
        Assert.StartsWith("http://127.0.0.1:", url);
        Assert.EndsWith("/clip.mkv", url);
        using var http = new HttpClient();
        Assert.Equal(bytes, await http.GetByteArrayAsync(url, Ct));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(100_000, 100_009);
        using var ranged = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(bytes.AsSpan(100_000, 10).ToArray(), await ranged.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("bytes 100000-100009/300000", ranged.Content.Headers.ContentRange?.ToString());

        // The secret is the key: a guessed URL, or a local path smuggled in, gets nothing.
        var port = new Uri(url).Port;
        using var guessed = await http.GetAsync($"http://127.0.0.1:{port}/0000/{url.Split('/')[4]}/clip.mkv", Ct);
        Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);
        var local = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("/etc/passwd")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var smuggled = await http.GetAsync($"http://127.0.0.1:{port}/{url.Split('/')[3]}/{local}/passwd", Ct);
        Assert.Equal(HttpStatusCode.NotFound, smuggled.StatusCode);
    }

    // MARK: - End to end through the dashboard's API

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<bool> FfmpegAvailableAsync() =>
        await FfmpegProcess.RunAsync("ffmpeg", ["-version"], TimeSpan.FromSeconds(10), NullLogger.Instance, "test", null, CancellationToken.None);

    [Fact]
    public async Task ALibraryOnAnotherComputer_SignsInBrowsesScansAndPlays()
    {
        var ffmpeg = await FfmpegAvailableAsync();
        var moviePath = Path.Combine(_shared, "Films", "Share Film (2011)", "Share.Film.2011.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(moviePath)!);
        if (ffmpeg)
        {
            Assert.True(await FfmpegProcess.RunAsync("ffmpeg",
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=duration=6:size=320x180:rate=25",
                "-f", "lavfi", "-i", "sine=duration=6",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "ac3", "-shortest",
                Path.ChangeExtension(moviePath, ".mkv")
            ], TimeSpan.FromMinutes(1), NullLogger.Instance, "test video", null, CancellationToken.None));
            moviePath = Path.ChangeExtension(moviePath, ".mkv");
            await File.WriteAllTextAsync(Path.ChangeExtension(moviePath, ".en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nOver the network\n\n", Ct);
        }
        else
        {
            await File.WriteAllBytesAsync(moviePath, new byte[5000], Ct);
        }
        using var server = new TestSmbServer(new Dictionary<string, string> { ["Movies"] = _shared });
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // The folder picker's first page lists the server's own places and no computers yet.
        var roots = await GetJsonAsync(client, "/api/media/browse");
        Assert.Equal("roots", roots.GetProperty("kind").GetString());
        Assert.True(roots.GetProperty("folders").GetArrayLength() > 0);
        Assert.Equal(0, roots.GetProperty("computers").GetArrayLength());

        // Browsing a computer that wants a password says so, with a 200 the picker can show.
        var locked = await GetJsonAsync(client, $"/api/media/browse?path={Uri.EscapeDataString($"smb://{server.Host}")}");
        Assert.True(locked.GetProperty("needsLogin").GetBoolean());

        using (var wrong = await client.PostAsJsonAsync("/api/media/network",
                   new { host = server.Host, username = TestSmbServer.Username, password = "nope" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            var body = await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.True(body.GetProperty("needsLogin").GetBoolean());
        }
        Guid computerId;
        using (var signedIn = await client.PostAsJsonAsync("/api/media/network",
                   new { host = server.Host, name = "Den PC", username = TestSmbServer.Username, password = TestSmbServer.Password }, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, signedIn.StatusCode);
            var body = await signedIn.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("Den PC", body.GetProperty("computer").GetProperty("name").GetString());
            Assert.True(body.GetProperty("computer").GetProperty("hasPassword").GetBoolean());
            Assert.False(body.GetProperty("computer").TryGetProperty("password", out _));
            Assert.Equal("Movies", body.GetProperty("shares")[0].GetProperty("name").GetString());
            computerId = body.GetProperty("computer").GetProperty("id").GetGuid();
        }
        // Signing in again with a blank password keeps the saved one.
        using (var again = await client.PutAsJsonAsync($"/api/media/network/{computerId}",
                   new { host = server.Host, name = "Den PC", username = TestSmbServer.Username, password = "" }, Ct))
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        roots = await GetJsonAsync(client, "/api/media/browse");
        var computer = Assert.Single(roots.GetProperty("computers").EnumerateArray());
        Assert.Equal($"smb://{server.Host}", computer.GetProperty("path").GetString());

        var shares = await GetJsonAsync(client, $"/api/media/browse?path={Uri.EscapeDataString(computer.GetProperty("path").GetString()!)}");
        Assert.Equal("computer", shares.GetProperty("kind").GetString());
        var share = Assert.Single(shares.GetProperty("folders").EnumerateArray());
        var films = await GetJsonAsync(client, $"/api/media/browse?path={Uri.EscapeDataString(share.GetProperty("path").GetString()! + "/Films")}");
        Assert.Equal("network", films.GetProperty("kind").GetString());
        Assert.Equal($"smb://{server.Host}/Movies", films.GetProperty("parent").GetString());
        Assert.Equal("Share Film (2011)", films.GetProperty("folders")[0].GetProperty("name").GetString());

        // "New folder" on a share makes it there and answers with its (empty) page.
        using (var made = await client.PostAsJsonAsync("/api/media/browse/folder", new { parent = films.GetProperty("path").GetString(), name = "TV Shows" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, made.StatusCode);
            var page = await made.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal($"smb://{server.Host}/Movies/Films/TV Shows", page.GetProperty("path").GetString());
            Assert.Equal("network", page.GetProperty("kind").GetString());
            Assert.True(Directory.Exists(Path.Combine(_shared, "Films", "TV Shows")));
        }
        // Not on the computer itself: folders go inside a shared folder.
        using (var top = await client.PostAsJsonAsync("/api/media/browse/folder", new { parent = $"smb://{server.Host}", name = "Nope" }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, top.StatusCode);
        Directory.Delete(Path.Combine(_shared, "Films", "TV Shows"));

        // A share that is not there is refused with the computer's own words.
        using (var missing = await client.PostAsJsonAsync("/api/media/libraries", new { name = "Nope", kind = "movies", path = $"smb://{server.Host}/Nope" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains("no shared folder", await missing.Content.ReadAsStringAsync(Ct));
        }
        // Pasted the Windows way, stored the smb:// way.
        using (var created = await client.PostAsJsonAsync("/api/media/libraries",
                   new { name = "Den Movies", kind = "movies", path = $@"\\127.0.0.1:{server.Port}\Movies\Films\" }, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var library = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal($"smb://{server.Host}/Movies/Films", library.GetProperty("path").GetString());
            Assert.True(library.GetProperty("network").GetBoolean());
        }

        var deadline = DateTime.UtcNow.AddSeconds(90);
        JsonElement status;
        do
        {
            await Task.Delay(250, Ct);
            status = await GetJsonAsync(client, "/api/media/status");
        } while ((status.GetProperty("scanning").GetBoolean() || status.GetProperty("movieCount").GetInt32() < 1) && DateTime.UtcNow < deadline);
        Assert.Equal(1, status.GetProperty("movieCount").GetInt32());
        Assert.True(status.GetProperty("libraries")[0].GetProperty("lastScanError").ValueKind == JsonValueKind.Null,
            status.GetProperty("libraries")[0].GetProperty("lastScanError").ToString());

        var movie = (await GetJsonAsync(client, "/api/media/items?kind=movie")).GetProperty("items")[0];
        Assert.Equal("Share Film", movie.GetProperty("title").GetString());
        var id = movie.GetProperty("id").GetString();

        if (!ffmpeg)
        {
            // Without ffmpeg the file can still be streamed straight off the share.
            using var direct = await client.GetAsync($"/api/media/stream/{id}/video.mp4", Ct);
            Assert.Equal(5000, (await direct.Content.ReadAsByteArrayAsync(Ct)).Length);
            return;
        }

        // ffprobe read the MKV through the relay.
        var detail = await GetJsonAsync(client, $"/api/media/items/{id}");
        Assert.Equal("h264", detail.GetProperty("file").GetProperty("videoCodec").GetString());
        Assert.InRange(detail.GetProperty("durationSeconds").GetDouble(), 5, 7);

        // A byte range straight off the share.
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media/stream/{id}/video.mkv"))
        {
            request.Headers.Range = new RangeHeaderValue(10, 109);
            using var ranged = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal(File.ReadAllBytes(moviePath).AsSpan(10, 100).ToArray(), await ranged.Content.ReadAsByteArrayAsync(Ct));
        }

        // HLS: ffmpeg remuxes the MKV (AC-3 to AAC) reading it through the relay.
        var hls = await GetJsonAsync(client, $"/api/media/items/{id}/playback?video=h264&audio=aac");
        Assert.Equal("hls", hls.GetProperty("mode").GetString());
        var url = hls.GetProperty("url").GetString()!;
        var baseUrl = url[..(url.LastIndexOf('/') + 1)];
        var index = await client.GetStringAsync(baseUrl + "index.m3u8", Ct);
        var firstSegment = index.Split('\n').First(line => line.EndsWith(".m4s", StringComparison.Ordinal));
        using (var segment = await client.GetAsync(baseUrl + firstSegment, Ct)) Assert.Equal(HttpStatusCode.OK, segment.StatusCode);

        // And the sidecar subtitles next to it on the share.
        var vtt = await client.GetStringAsync(hls.GetProperty("subtitles")[0].GetProperty("url").GetString(), Ct);
        Assert.Contains("Over the network", vtt);

        // The sign-in goes into backups, so a restored server can still reach the share.
        var backup = await GetJsonAsync(client, "/api/backup/export");
        Assert.Equal(server.Host, backup.GetProperty("media").GetProperty("networkComputers")[0].GetProperty("host").GetString());

        using (var removed = await client.DeleteAsync($"/api/media/network/{computerId}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, (await GetJsonAsync(client, "/api/media/network")).GetArrayLength());
    }
}
