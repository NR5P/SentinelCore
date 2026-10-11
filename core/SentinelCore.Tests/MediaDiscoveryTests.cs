extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Movies Home shelves beyond "recently added": play history (Most played), My List, Watch
// again, Top rated, genre rows and Surprise me. No ffmpeg needed: the catalog is written straight
// into the store, already matched and probed so the scanner leaves it alone.
public sealed class MediaDiscoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly Guid _library = Guid.NewGuid();

    public MediaDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"media-discovery-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

    private MediaItem Movie(string title, double? rating, params string[] genres) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = _library,
        Kind = MediaKinds.Movie,
        Title = title,
        SortTitle = title.ToLowerInvariant(),
        Path = Path.Combine(_root, title + ".mp4"),
        AddedAt = DateTimeOffset.UtcNow.AddDays(-10),
        Rating = rating,
        Genres = [.. genres],
        TmdbId = 1,
        MetadataFetchedAt = DateTimeOffset.UtcNow,
        MetadataLocked = true,
        ProbedAt = DateTimeOffset.UtcNow,
        DurationSeconds = 6000
    };

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static List<string> Titles(JsonElement array) => [.. array.EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    [Fact]
    public async Task Plays_AreCountedOncePerSitting_AndSeededFromExistingProgress()
    {
        var item = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var before = new SqliteMediaStore(_dataDirectory))
            await before.SetProgressAsync(new MediaProgress("pat", item, 600, 6000, false, DateTimeOffset.UtcNow.AddDays(-3)), Ct);
        // A media.db from before play history existed.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, "media.db")};Pooling=False"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE MediaPlays";
            drop.ExecuteNonQuery();
        }

        // Progress that predates play history counts as one play each; the seeding happens once.
        using var store = new SqliteMediaStore(_dataDirectory);
        Assert.Equal(1, (await store.PlayStatsAsync(Ct))[item].Count);

        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.RecordPlayAsync(new MediaPlay("pat", item, now), MediaPlay.SameSitting, Ct));
        // A seek or an audio-track change an hour later is the same sitting...
        Assert.False(await store.RecordPlayAsync(new MediaPlay("pat", item, now.AddHours(1)), MediaPlay.SameSitting, Ct));
        // ...but someone else pressing play is another play, and so is tomorrow.
        Assert.True(await store.RecordPlayAsync(new MediaPlay("alex", item, now.AddHours(1)), MediaPlay.SameSitting, Ct));
        Assert.True(await store.RecordPlayAsync(new MediaPlay("pat", item, now.AddDays(1)), MediaPlay.SameSitting, Ct));
        Assert.True(await store.RecordPlayAsync(new MediaPlay("pat", other, now), MediaPlay.SameSitting, Ct));

        var stats = await store.PlayStatsAsync(Ct);
        Assert.Equal(4, stats[item].Count);
        Assert.Equal(1, stats[other].Count);

        // Restoring a backup adds only what is missing.
        var all = await store.AllPlaysAsync(Ct);
        Assert.Equal(0, await store.RestorePlaysAsync(all, Ct));
        using var fresh = new SqliteMediaStore(Path.Combine(_root, "restored"));
        Assert.Equal(all.Count, await fresh.RestorePlaysAsync(all, Ct));
        Assert.Equal(4, (await fresh.PlayStatsAsync(Ct))[item].Count);

        // My List is per person and adding twice keeps the first date.
        await store.SetInWatchlistAsync("pat", item, true, Ct);
        var added = Assert.Single(await store.GetWatchlistAsync("pat", Ct)).AddedAt;
        await store.SetInWatchlistAsync("pat", item, true, Ct);
        Assert.Equal(added, Assert.Single(await store.GetWatchlistAsync("pat", Ct)).AddedAt);
        Assert.Empty(await store.GetWatchlistAsync("alex", Ct));
        await store.SetInWatchlistAsync("pat", item, false, Ct);
        Assert.Empty(await store.GetWatchlistAsync("pat", Ct));
    }

    [Fact]
    public async Task Home_HasMostPlayedMyListWatchAgainTopRatedAndGenreShelves()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IMediaStore>();

        var popular = Movie("Popular", 6.0, "Comedy");
        var favourite = Movie("Favourite", 8.5, "Comedy");
        var finished = Movie("Finished", 9.0, "Comedy");
        var acclaimed = Movie("Acclaimed", 8.0, "Drama");
        var comedies = Enumerable.Range(1, 5).Select(n => Movie($"Comedy {n}", 5.0, "Comedy")).ToList();
        await store.UpsertItemsAsync([popular, favourite, finished, acclaimed, .. comedies], Ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var who in new[] { "a", "b", "c" }) await store.RecordPlayAsync(new MediaPlay(who, popular.Id, now), MediaPlay.SameSitting, Ct);
        await store.RecordPlayAsync(new MediaPlay("a", favourite.Id, now), MediaPlay.SameSitting, Ct);
        await store.SetProgressAsync(new MediaProgress("", finished.Id, 0, 6000, true, now), Ct);

        using (var add = await client.PostAsJsonAsync($"/api/media/items/{acclaimed.Id}/list", new { inList = true }, Ct))
            Assert.True((await add.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("inList").GetBoolean());

        var home = await GetJsonAsync(client, "/api/media/home");
        Assert.Equal(["Popular", "Favourite"], Titles(home.GetProperty("mostPlayed")));
        Assert.Equal(3, home.GetProperty("mostPlayed")[0].GetProperty("playCount").GetInt32());
        Assert.Equal(["Acclaimed"], Titles(home.GetProperty("myList")));
        Assert.True(home.GetProperty("myList")[0].GetProperty("inList").GetBoolean());
        Assert.Equal(["Finished"], Titles(home.GetProperty("recentlyWatched")));
        // Top rated leaves out what this person already watched.
        Assert.Equal(["Favourite", "Acclaimed"], Titles(home.GetProperty("topRated")));
        // Comedy has enough titles for a shelf, Drama does not; unwatched titles come first.
        var shelf = Assert.Single(home.GetProperty("genreShelves").EnumerateArray());
        Assert.Equal("Comedy", shelf.GetProperty("genre").GetString());
        Assert.Equal(8, shelf.GetProperty("items").GetArrayLength());
        Assert.Equal("Finished", Titles(shelf.GetProperty("items")).Last());

        var byPlays = await GetJsonAsync(client, "/api/media/items?kind=movie&sort=plays&limit=2");
        Assert.Equal(["Popular", "Favourite"], Titles(byPlays.GetProperty("items")));
        var listed = await GetJsonAsync(client, "/api/media/items?list=true");
        Assert.Equal(["Acclaimed"], Titles(listed.GetProperty("items")));

        // Surprise me skips what has been watched while there is anything else.
        for (var i = 0; i < 20; i++)
            Assert.NotEqual("Finished", (await GetJsonAsync(client, "/api/media/random?genre=Comedy")).GetProperty("title").GetString());
        using (var none = await client.GetAsync("/api/media/random?genre=Western", Ct))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        // Backups carry the play history and My List.
        var backup = (await GetJsonAsync(client, "/api/backup/export")).GetProperty("media");
        Assert.Equal(4, backup.GetProperty("plays").GetArrayLength());
        Assert.Equal(1, backup.GetProperty("watchlist").GetArrayLength());

        using (var remove = await client.PostAsJsonAsync($"/api/media/items/{acclaimed.Id}/list", new { inList = false }, Ct))
            remove.EnsureSuccessStatusCode();
        Assert.Empty((await GetJsonAsync(client, "/api/media/home")).GetProperty("myList").EnumerateArray());
    }
}
