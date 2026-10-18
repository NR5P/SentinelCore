using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// "Suggested for you" (MusicSuggestions.cs): who the seeds are, how similar artists are ranked and
// filtered, and what is read out of ListenBrainz' and MusicBrainz' answers.
public sealed class MusicSuggestionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"music-suggestions-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MusicTrack Track(string path, string title, string artist, string album) => new()
    {
        Id = MusicIds.Track(Guid.Empty, path),
        Path = "/music/" + path,
        RelativePath = path,
        AddedAt = DateTimeOffset.UtcNow.AddDays(-5),
        TagsReadAt = DateTimeOffset.UtcNow,
        Title = title,
        ArtistText = artist,
        Album = album,
        Codec = "mp3",
        DurationSeconds = 200
    };

    [Fact]
    public async Task Seeds_WeighLovesPlaysPlaylistsAndImportedLists()
    {
        var dreams = Track("fm/1.mp3", "Dreams", "Fleetwood Mac", "Rumours");
        var hotel = Track("ea/1.mp3", "Hotel California", "Eagles", "Hotel California");
        var other = Track("x/1.mp3", "Song", "Someone Else", "Thing");
        var tracks = new List<MusicTrack> { dreams, hotel, other };
        MusicAlbumRules.Resolve(tracks);
        using var store = new SqliteMusicStore(_directory);
        var now = DateTimeOffset.UtcNow;
        await store.SetLoveAsync(new MusicLove("morgan", MusicLoveKinds.Artist, MusicIds.Artist("Fleetwood Mac"), true, now), Ct);
        for (var i = 0; i < 6; i++)
            await store.RecordPlayAsync(new MusicPlay("morgan", hotel.Id, now.AddHours(-i), 200, false), TimeSpan.Zero, Ct);
        var catalog = new MusicCatalog(1, tracks, new Dictionary<string, MusicArtRecord>(), []);
        var view = new MusicView(catalog, await MusicUserState.LoadAsync(store, "morgan", Ct), isAdmin: false);
        var playlist = new MusicPlaylist(Guid.NewGuid(), "morgan", "Mine", "", false, false, null, now, now,
            [new MusicPlaylistEntry(Guid.NewGuid(), other.Id, now)]);

        var seeds = MusicSuggestionSeeds.Build(view, [playlist], ["Phoebe Bridgers", "Phoebe Bridgers", "Various Artists"]);

        Assert.Equal(["Fleetwood Mac", "Eagles", "Phoebe Bridgers", "Someone Else"], seeds.Select(s => s.Artist));
        Assert.Equal("loved", seeds[0].Reason);
        Assert.Equal("played", seeds[1].Reason);
        Assert.Equal("imported", seeds[2].Reason);
        Assert.Equal("playlists", seeds[3].Reason);
    }

    [Fact]
    public async Task Seeds_FallBackToTheLibrary_WhenNothingIsLovedOrPlayed()
    {
        var tracks = new List<MusicTrack> { Track("a/1.mp3", "One", "Band A", "First"), Track("a/2.mp3", "Two", "Band A", "First") };
        MusicAlbumRules.Resolve(tracks);
        using var store = new SqliteMusicStore(_directory);
        var view = new MusicView(new MusicCatalog(1, tracks, new Dictionary<string, MusicArtRecord>(), []),
            await MusicUserState.LoadAsync(store, "morgan", Ct), isAdmin: false);

        var seed = Assert.Single(MusicSuggestionSeeds.Build(view, [], []));
        Assert.Equal(("Band A", "library"), (seed.Artist, seed.Reason));
    }

    private sealed class FakeSimilarity : IMusicSimilarity
    {
        public Dictionary<string, string> Mbids = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<MusicSimilarArtist>> Similar = [];
        public Dictionary<string, List<MusicPopularAlbum>> Albums = [];
        public bool Offline;

        public Task<string?> ArtistMbidAsync(string name, CancellationToken cancellationToken) =>
            Offline ? throw new MediaDownloadException("Couldn't reach MusicBrainz.") : Task.FromResult(Mbids.GetValueOrDefault(name));

        public Task<IReadOnlyList<MusicSimilarArtist>> SimilarAsync(string artistMbid, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicSimilarArtist>>(Similar.GetValueOrDefault(artistMbid) ?? []);

        public Task<IReadOnlyList<MusicPopularAlbum>> TopAlbumsAsync(string artistMbid, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MusicPopularAlbum>>(Albums.GetValueOrDefault(artistMbid) ?? []);
    }

    [Fact]
    public async Task Ranker_SuggestsTopStudioAlbumsOfSimilarArtistsNotInTheLibrary()
    {
        var similarity = new FakeSimilarity
        {
            Mbids = { ["Fleetwood Mac"] = "fm", ["Eagles"] = "ea" },
            Similar =
            {
                ["fm"] = [new("ea", "Eagles", 100), new("st", "Steely Dan", 90), new("lr", "Linda Ronstadt", 40), new("own", "Owned Band", 80)],
                ["ea"] = [new("fm", "Fleetwood Mac", 100), new("lr", "Linda Ronstadt", 95)],
            },
            Albums =
            {
                ["st"] = [new("aja", "Aja", 1977, 900, "Album"), new("gh", "Greatest Hits", 1978, 800, "Album"),
                          new("s1", "Do It Again", 1972, 700, "Single"), new("cbg", "Can't Buy a Thrill", 1972, 500, "Album"),
                          new("gaucho", "Gaucho", 1980, 400, "Album")],
                ["lr"] = [new("hwh", "Heart Like a Wheel", 1974, 600, "Album"), new("live", "Live in Hollywood", 2019, 500, "Album")],
            }
        };
        var seeds = new List<MusicSuggestionSeed> { new("Fleetwood Mac", 6, "loved"), new("Eagles", 4, "played") };
        var library = new HashSet<string> { MusicText.Key("Owned Band") };
        var dismissed = new HashSet<string> { MusicSuggestionRanker.AlbumDismissal("Steely Dan", "Can't Buy a Thrill") };

        var albums = await MusicSuggestionRanker.RankAsync(seeds, similarity, library,
            (artist, title) => Task.FromResult(title == "Gaucho" && artist == "Steely Dan"), dismissed, Ct);

        // Each seed takes turns with its own closest match (Fleetwood Mac, the heavier seed, first:
        // Steely Dan; then Eagles: Linda Ronstadt, who is close to both); seeds and artists already
        // in the library are never suggested; hits, singles, live records, a dismissed album and an
        // album already owned are skipped; each artist's best album comes first.
        Assert.Equal([("Steely Dan", "Aja"), ("Linda Ronstadt", "Heart Like a Wheel")], albums.Select(a => (a.Artist, a.Title)));
        Assert.Equal(["Fleetwood Mac"], albums[0].Because);
        Assert.Equal(["Eagles", "Fleetwood Mac"], albums[1].Because);
        Assert.Equal("aja", albums[0].Mbid);
        Assert.Equal(1977, albums[0].Year);
    }

    [Fact]
    public async Task Ranker_SkipsDismissedArtists_AndReportsBeingOffline()
    {
        var similarity = new FakeSimilarity
        {
            Mbids = { ["Fleetwood Mac"] = "fm" },
            Similar = { ["fm"] = [new("st", "Steely Dan", 90)] },
            Albums = { ["st"] = [new("aja", "Aja", 1977, 900, "Album")] }
        };
        var seeds = new List<MusicSuggestionSeed> { new("Fleetwood Mac", 6, "loved") };
        var none = await MusicSuggestionRanker.RankAsync(seeds, similarity, new HashSet<string>(),
            (_, _) => Task.FromResult(false), new HashSet<string> { MusicSuggestionRanker.ArtistDismissal("Steely Dan") }, Ct);
        Assert.Empty(none);

        similarity.Offline = true;
        await Assert.ThrowsAsync<MediaDownloadException>(() => MusicSuggestionRanker.RankAsync(seeds, similarity,
            new HashSet<string>(), (_, _) => Task.FromResult(false), new HashSet<string>(), Ct));
    }

    private sealed class RoutedHandler(Func<Uri, (HttpStatusCode, string)> route) : HttpMessageHandler
    {
        public readonly List<Uri> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var (status, body) = route(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task ListenBrainz_AnswersAreReadAndCached()
    {
        // Trimmed copies of real answers (2026-10-18).
        var handler = new RoutedHandler(uri => uri.Host switch
        {
            "musicbrainz.org" => (HttpStatusCode.OK, """
                {"artists":[{"id":"wrong","name":"Fleetwood Mac Tribute","score":100},
                            {"id":"bd13909f-1c29-4c27-a874-d4aaf27c5b1a","name":"Fleetwood Mac","score":98}]}
                """),
            "labs.api.listenbrainz.org" => (HttpStatusCode.OK, """
                [{"artist_mbid":"5441c29d-3602-4898-b1a1-b77fa23b8e50","name":"David Bowie","score":6996,"reference_mbid":"bd13909f-1c29-4c27-a874-d4aaf27c5b1a"},
                 {"artist_mbid":"b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d","name":"The Beatles","score":6839,"reference_mbid":"bd13909f-1c29-4c27-a874-d4aaf27c5b1a"}]
                """),
            _ when uri.AbsolutePath.Contains("/nobody") => (HttpStatusCode.NotFound, """{"error":"none"}"""),
            _ => (HttpStatusCode.OK, """
                [{"release_group_mbid":"416bb5e5-c7d1-3977-8fd7-7c9daf6c2be6","release_group":{"name":"Tusk","date":"1979-10-12","type":"Album"},"total_listen_count":1197365},
                 {"release_group_mbid":"r","release_group":{"name":"Rumours","date":"1977-02-04","type":"Album"},"total_listen_count":5471367},
                 {"release_group_mbid":"s","release_group":{"name":"Dreams","date":null,"type":null},"total_listen_count":10}]
                """)
        });
        using var store = new MediaDownloadStore(_directory);
        var similarity = new ListenBrainzMusicSimilarity(new SingleClientFactory(handler), store, TimeProvider.System,
            NullLogger<ListenBrainzMusicSimilarity>.Instance);

        Assert.Equal("bd13909f-1c29-4c27-a874-d4aaf27c5b1a", await similarity.ArtistMbidAsync("Fleetwood Mac", Ct));
        var similar = await similarity.SimilarAsync("bd13909f-1c29-4c27-a874-d4aaf27c5b1a", Ct);
        Assert.Equal(["David Bowie", "The Beatles"], similar.Select(s => s.Name));
        var albums = await similarity.TopAlbumsAsync("bd13909f-1c29-4c27-a874-d4aaf27c5b1a", Ct);
        Assert.Equal(["Rumours", "Tusk", "Dreams"], albums.Select(a => a.Title));
        Assert.Equal((1977, "Album"), (albums[0].Year, albums[0].Type));
        Assert.Null(albums[2].Year);
        Assert.Empty(await similarity.TopAlbumsAsync("nobody", Ct));

        var asked = handler.Requests.Count;
        await similarity.ArtistMbidAsync("fleetwood mac", Ct);
        await similarity.SimilarAsync("bd13909f-1c29-4c27-a874-d4aaf27c5b1a", Ct);
        await similarity.TopAlbumsAsync("bd13909f-1c29-4c27-a874-d4aaf27c5b1a", Ct);
        Assert.Equal(asked, handler.Requests.Count);
    }
}
