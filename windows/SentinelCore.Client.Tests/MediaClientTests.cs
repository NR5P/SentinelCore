using System.Net;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The Movies & TV routes: what the client asks for, and that the contract in core/docs/MOVIES.md
/// decodes into the models the views read.
/// </summary>
public class MediaClientTests
{
    private static SentinelClient ClientFor(FakeServer server) =>
        new(() => server.BaseUrl, NullSentinelLog.Instance);

    private const string EmptyPage = """{"items":[],"total":0,"genres":[]}""";

    [Fact]
    public async Task The_default_library_page_is_the_bare_route_with_a_limit()
    {
        using var server = new FakeServer().OnGet("/api/media/items", EmptyPage);
        using var client = ClientFor(server);

        await client.MediaItemsAsync();

        Assert.Equal("GET /api/media/items?limit=60", server.Requests.Single());
    }

    [Fact]
    public async Task Every_library_filter_reaches_the_query_string()
    {
        using var server = new FakeServer().OnGet("/api/media/items", EmptyPage);
        using var client = ClientFor(server);

        await client.MediaItemsAsync(
            kind: "movie", query: "  toy story ", genre: "Science Fiction", sort: "rating", order: "desc",
            unwatched: true, offset: 120, limit: 60);

        var request = server.Requests.Single();
        Assert.StartsWith("GET /api/media/items?", request);
        Assert.Contains("kind=movie", request);
        Assert.Contains("q=toy%20story", request);          // trimmed, then escaped
        Assert.Contains("genre=Science%20Fiction", request);
        Assert.Contains("sort=rating", request);
        Assert.Contains("order=desc", request);
        Assert.Contains("unwatched=true", request);
        Assert.Contains("offset=120", request);
        Assert.Contains("limit=60", request);
    }

    [Fact]
    public async Task Most_played_and_My_List_reach_the_query_string_and_My_List_is_off_by_default()
    {
        using var server = new FakeServer().OnGet("/api/media/items", EmptyPage);
        using var client = ClientFor(server);

        await client.MediaItemsAsync(sort: "plays", order: "desc", inList: true);
        await client.MediaItemsAsync(sort: "plays");

        Assert.Contains("sort=plays", server.Requests[0]);
        Assert.Contains("list=true", server.Requests[0]);
        Assert.DoesNotContain("list=", server.Requests[1]);
    }

    [Fact]
    public async Task My_List_posts_the_contract_body_and_decodes_the_answer()
    {
        using var server = new FakeServer()
            .On("POST", "/api/media/items/m1/list", """{"itemId":"m1","inList":true}""")
            .On("POST", "/api/media/items/e1/list", """{"itemId":"s1","inList":false}""");
        using var client = ClientFor(server);

        var added = await client.SetInMyListAsync("m1", true);
        var removed = await client.SetInMyListAsync("e1", false);

        Assert.Equal("POST /api/media/items/m1/list", server.Requests[0]);
        Assert.Contains("\"inList\":true", server.Bodies[0]);
        Assert.Equal("m1", added.ItemId);
        Assert.True(added.InList);
        Assert.Equal("POST /api/media/items/e1/list", server.Requests[1]);
        Assert.Contains("\"inList\":false", server.Bodies[1]);
        Assert.Equal("s1", removed.ItemId);   // an episode's id saves its show
        Assert.False(removed.InList);
    }

    [Fact]
    public async Task Random_sends_only_the_filters_given_and_decodes_one_summary()
    {
        const string pick = """{"id":"m1","kind":"movie","title":"17 Again","genres":["Comedy"],"playCount":2,"inList":true}""";
        using var server = new FakeServer().OnGet("/api/media/random", pick);
        using var client = ClientFor(server);

        var item = await client.GetRandomMediaAsync();
        await client.GetRandomMediaAsync(kind: "movie", libraryId: "l1", genre: "Science Fiction");

        Assert.Equal("GET /api/media/random", server.Requests[0]);
        Assert.Equal("m1", item.Id);
        Assert.Equal(2, item.PlayCount);
        Assert.True(item.InList);
        Assert.Contains("kind=movie", server.Requests[1]);
        Assert.Contains("libraryId=l1", server.Requests[1]);
        Assert.Contains("genre=Science%20Fiction", server.Requests[1]);
    }

    [Fact]
    public async Task Random_with_nothing_to_pick_surfaces_the_servers_sentence()
    {
        using var server = new FakeServer().OnGet("/api/media/random",
            """{"error":"Nothing in the library matches that yet."}""", HttpStatusCode.NotFound);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.GetRandomMediaAsync(kind: "show"));

        Assert.Equal(404, error.Status);
        Assert.Equal("Nothing in the library matches that yet.", error.Message);
    }

    [Fact]
    public async Task Playback_declares_what_Chromium_decodes_and_resumes_by_omitting_start()
    {
        using var server = new FakeServer().OnGet("/api/media/items/m1/playback", PlaybackJson("direct", 0));
        using var client = ClientFor(server);

        await client.MediaPlaybackAsync("m1");

        var request = server.Requests.Single();
        Assert.Contains("video=h264", request);
        Assert.Contains("audio=aac%2Cmp3", request);
        Assert.DoesNotContain("start=", request);   // omitted means "resume from the saved point"
        Assert.DoesNotContain("hevc", request);
    }

    [Fact]
    public async Task Playback_sends_start_over_and_a_position_in_invariant_seconds()
    {
        using var server = new FakeServer().OnGet("/api/media/items/m1/playback", PlaybackJson("direct", 0));
        using var client = ClientFor(server);

        await client.MediaPlaybackAsync("m1", start: 0);
        await client.MediaPlaybackAsync("m1", start: 1234.5, audioTrack: 2);

        Assert.Contains("start=0", server.Requests[0]);
        Assert.Contains("start=1234.5", server.Requests[1]);
        Assert.Contains("audioTrack=2", server.Requests[1]);
    }

    [Fact]
    public async Task A_playback_answer_decodes_mode_clock_and_next_episode()
    {
        using var server = new FakeServer().OnGet("/api/media/items/e1/playback", PlaybackJson("hls", 1200));
        using var client = ClientFor(server);

        var playback = await client.MediaPlaybackAsync("e1");

        Assert.True(playback.IsHls);
        Assert.Equal(1200, playback.StartSeconds);
        Assert.Equal(2646, playback.DurationSeconds);
        Assert.Equal("/api/media/hls/s1/master.m3u8", playback.Url);
        Assert.True(playback.HasNextEpisode);
        Assert.Equal("e2", playback.NextEpisodeId);
        Assert.Single(playback.Subtitles);
        Assert.Equal("English", playback.Subtitles[0].Label);
    }

    [Fact]
    public async Task Progress_and_watched_post_the_contract_bodies()
    {
        const string progress = """{"positionSeconds":1234.5,"durationSeconds":6120,"percent":20.2,"watched":false,"updatedAt":"2026-09-25T10:00:00Z"}""";
        using var server = new FakeServer()
            .On("POST", "/api/media/items/m1/progress", progress)
            .On("POST", "/api/media/items/m1/watched", progress)
            .On("POST", "/api/media/items/s1/seasons/2/watched", progress);
        using var client = ClientFor(server);

        var saved = await client.ReportMediaProgressAsync("m1", 1234.5, 6120);
        await client.SetMediaWatchedAsync("m1", true);
        await client.SetMediaSeasonWatchedAsync("s1", 2, false);

        Assert.Equal(1234.5, saved.PositionSeconds);
        Assert.Equal("POST /api/media/items/m1/progress", server.Requests[0]);
        Assert.Contains("\"positionSeconds\":1234.5", server.Bodies[0]);
        Assert.Contains("\"durationSeconds\":6120", server.Bodies[0]);
        Assert.Equal("POST /api/media/items/m1/watched", server.Requests[1]);
        Assert.Contains("\"watched\":true", server.Bodies[1]);
        Assert.Equal("POST /api/media/items/s1/seasons/2/watched", server.Requests[2]);
        Assert.Contains("\"watched\":false", server.Bodies[2]);
    }

    [Fact]
    public async Task Item_ids_are_escaped_into_the_path()
    {
        using var server = new FakeServer().OnGet("/api/media/items/a%2Fb", "{}");
        using var client = ClientFor(server);

        try { await client.MediaItemAsync("a/b"); } catch (Exception) { /* only the path matters */ }

        Assert.StartsWith("GET /api/media/items/a%2Fb", server.Requests.Single());
    }

    [Fact]
    public async Task Home_decodes_shelves_featured_and_show_counts()
    {
        const string json = """
            {
              "continueWatching": [
                {"id":"e1","kind":"episode","title":"The National Anthem","showId":"s1","showTitle":"Black Mirror",
                 "seasonNumber":1,"episodeNumber":1,"genres":[],"posterUrl":"/api/media/items/s1/poster",
                 "thumbUrl":"/api/media/items/e1/thumb",
                 "progress":{"positionSeconds":600,"durationSeconds":2646,"percent":22.7,"watched":false}}
              ],
              "recentMovies": [
                {"id":"m1","kind":"movie","title":"17 Again","year":2009,"genres":["Comedy","Fantasy"],"rating":6.2,
                 "certification":"PG-13","runtimeMinutes":102,"durationSeconds":6120.4,"posterUrl":null,"progress":null}
              ],
              "recentShows": [
                {"id":"s1","kind":"show","title":"Black Mirror","genres":["Drama"],"seasonCount":6,"episodeCount":27,
                 "watchedEpisodeCount":3,"nextEpisodeId":"e4"}
              ],
              "recentEpisodes": [],
              "featured": {"id":"m1","kind":"movie","title":"17 Again","genres":[],"backdropUrl":"/api/media/items/m1/backdrop"},
              "movieCount": 396, "showCount": 45,
              "libraries": [{"id":"l1","name":"Movies","kind":"movies","path":"/mnt/movies","itemCount":396}]
            }
            """;
        using var server = new FakeServer().OnGet("/api/media/home", json);
        using var client = ClientFor(server);

        var home = await client.MediaHomeAsync();

        var episode = home.ContinueWatching.Single();
        Assert.True(episode.IsEpisode);
        Assert.Equal("S1 · E1 · The National Anthem", episode.EpisodeLabel);
        Assert.Equal(600, episode.Progress!.PositionSeconds);

        var movie = home.RecentMovies.Single();
        Assert.True(movie.IsMovie);
        Assert.Null(movie.PosterUrl);
        Assert.Null(movie.Progress);
        Assert.Equal(new[] { "Comedy", "Fantasy" }, movie.Genres);

        var show = home.RecentShows.Single();
        Assert.True(show.IsShow);
        Assert.Equal(24, show.UnwatchedEpisodeCount);
        Assert.Equal("e4", show.NextEpisodeId);

        Assert.Equal("/api/media/items/m1/backdrop", home.Featured!.BackdropUrl);
        Assert.Equal(396, home.MovieCount);
        Assert.Equal("movies", home.Libraries.Single().Kind);

        // This payload is an older server's: no discovery rows, no play counts, no My List.
        Assert.Empty(home.MyList);
        Assert.Empty(home.MostPlayed);
        Assert.Empty(home.RecentlyWatched);
        Assert.Empty(home.TopRated);
        Assert.Empty(home.GenreShelves);
        Assert.Equal(0, movie.PlayCount);
        Assert.False(movie.InList);
    }

    [Fact]
    public async Task Home_decodes_the_discovery_rows_play_counts_and_My_List()
    {
        const string json = """
            {
              "continueWatching": [], "recentMovies": [], "recentShows": [], "recentEpisodes": [], "featured": null,
              "myList": [{"id":"m1","kind":"movie","title":"17 Again","genres":[],"playCount":3,"inList":true}],
              "mostPlayed": [{"id":"s1","kind":"show","title":"Black Mirror","genres":[],"playCount":12,"inList":false}],
              "recentlyWatched": [{"id":"m2","kind":"movie","title":"Up","genres":[],"playCount":1}],
              "topRated": [{"id":"m3","kind":"movie","title":"Heat","genres":[],"rating":8.3}],
              "genreShelves": [
                {"genre":"Comedy","items":[{"id":"m1","kind":"movie","title":"17 Again","genres":["Comedy"]}]},
                {"genre":"Drama","items":null}
              ],
              "movieCount": 3, "showCount": 1, "libraries": []
            }
            """;
        using var server = new FakeServer().OnGet("/api/media/home", json);
        using var client = ClientFor(server);

        var home = await client.MediaHomeAsync();

        var saved = home.MyList.Single();
        Assert.True(saved.InList);
        Assert.Equal(3, saved.PlayCount);
        Assert.Equal(12, home.MostPlayed.Single().PlayCount);
        Assert.False(home.MostPlayed.Single().InList);
        Assert.Equal("m2", home.RecentlyWatched.Single().Id);
        Assert.Equal(8.3, home.TopRated.Single().Rating);
        Assert.Equal(new[] { "Comedy", "Drama" }, home.GenreShelves.Select(shelf => shelf.Genre));
        Assert.Equal("m1", home.GenreShelves[0].Items.Single().Id);
        Assert.Empty(home.GenreShelves[1].Items);   // a null row is an empty one
    }

    [Fact]
    public async Task Null_discovery_rows_decode_as_empty()
    {
        const string json = """
            {"continueWatching":[],"recentMovies":[],"recentShows":[],"recentEpisodes":[],
             "myList":null,"mostPlayed":null,"recentlyWatched":null,"topRated":null,"genreShelves":null,
             "movieCount":0,"showCount":0,"libraries":[]}
            """;
        using var server = new FakeServer().OnGet("/api/media/home", json);
        using var client = ClientFor(server);

        var home = await client.MediaHomeAsync();

        Assert.Empty(home.MyList);
        Assert.Empty(home.MostPlayed);
        Assert.Empty(home.RecentlyWatched);
        Assert.Empty(home.TopRated);
        Assert.Empty(home.GenreShelves);
    }

    [Fact]
    public async Task A_show_detail_carries_seasons_and_a_movie_detail_its_file()
    {
        const string show = """
            {"id":"s1","kind":"show","title":"Black Mirror","genres":[],"overview":"Anthology.","cast":[],
             "file":null,"audioTracks":null,"subtitles":null,
             "seasons":[{"number":1,"name":"Season 1","episodeCount":3,"watchedCount":1,"posterUrl":null,
               "episodes":[{"id":"e1","kind":"episode","title":"The National Anthem","genres":[],"seasonNumber":1,"episodeNumber":1}]}]}
            """;
        const string movie = """
            {"id":"m1","kind":"movie","title":"17 Again","genres":[],"overview":"…","tagline":"Who says you're only young once?",
             "releaseDate":"2009-04-10","cast":[{"name":"Zac Efron","character":"Mike O'Donnell"}],"tmdbId":17638,
             "file":{"container":"mp4","videoCodec":"h264","width":1920,"height":800,"audioCodec":"aac","audioChannels":2,
                     "sizeBytes":1734000000,"bitrate":2267000,"path":null},
             "audioTracks":[{"index":0,"codec":"eac3","channels":6,"language":"eng","label":"English 5.1","isDefault":true}],
             "subtitles":[{"index":0,"language":"eng","label":"English","url":"/api/media/items/m1/subtitles/0.vtt","forced":false,"external":false}],
             "seasons":null}
            """;
        using var server = new FakeServer()
            .OnGet("/api/media/items/s1", show)
            .OnGet("/api/media/items/m1", movie);
        using var client = ClientFor(server);

        var showDetail = await client.MediaItemAsync("s1");
        var movieDetail = await client.MediaItemAsync("m1");

        Assert.Null(showDetail.File);
        var season = showDetail.Seasons!.Single();
        Assert.Equal("Season 1", season.Label);
        Assert.Equal("S1 · E1", season.Episodes.Single().EpisodeCode);

        Assert.Null(movieDetail.Seasons);
        Assert.Equal(1920, movieDetail.File!.Width);
        Assert.Equal("Mike O'Donnell", movieDetail.Cast.Single().Character);
        Assert.True(movieDetail.AudioTracks!.Single().IsDefault);
        Assert.Equal(17638, movieDetail.TmdbId);
        // Neither body says canDelete, as an older server wouldn't: no Delete button.
        Assert.False(movieDetail.CanDelete);
        Assert.False(showDetail.CanDelete);
    }

    [Fact]
    public async Task CanDelete_decodes_from_the_detail()
    {
        using var server = new FakeServer()
            .OnGet("/api/media/items/m1", """{"id":"m1","kind":"movie","title":"Dune","genres":[],"cast":[],"canDelete":true}""")
            .OnGet("/api/media/items/m2", """{"id":"m2","kind":"movie","title":"Heat","genres":[],"cast":[],"canDelete":false}""");
        using var client = ClientFor(server);

        Assert.True((await client.MediaItemAsync("m1")).CanDelete);
        Assert.False((await client.MediaItemAsync("m2")).CanDelete);
    }

    [Fact]
    public async Task Delete_hits_the_item_path()
    {
        using var server = new FakeServer().On("DELETE", "/api/media/items/m1", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        await client.DeleteMediaItemAsync("m1");

        Assert.Equal("DELETE /api/media/items/m1", server.Requests.Single());
    }

    [Fact]
    public async Task A_refused_delete_surfaces_the_servers_sentence()
    {
        using var server = new FakeServer().On("DELETE", "/api/media/items/m1",
            """{"error":"This movie is on another computer's share, so it can't be deleted from here."}""",
            HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.DeleteMediaItemAsync("m1"));

        Assert.Equal(400, error.Status);
        Assert.Equal("This movie is on another computer's share, so it can't be deleted from here.", error.Message);
    }

    [Fact]
    public async Task Status_decodes_an_idle_scan_as_null()
    {
        const string json = """
            {"enabled":true,"ffmpegAvailable":false,"tmdbConfigured":true,"scanning":false,"scan":null,
             "libraries":[],"movieCount":1,"showCount":2,"episodeCount":3,"lastScanFinishedAt":"2026-09-25T10:00:00Z"}
            """;
        using var server = new FakeServer().OnGet("/api/media/status", json);
        using var client = ClientFor(server);

        var status = await client.MediaStatusAsync();

        Assert.False(status.FfmpegAvailable);
        Assert.Null(status.Scan);
        Assert.Equal(3, status.EpisodeCount);
    }

    private static string PlaybackJson(string mode, double start) => $$"""
        {"itemId":"e1","title":"Black Mirror","subtitle":"S1 · E1 · The National Anthem","durationSeconds":2646,
         "startSeconds":{{start}},"resumeSeconds":{{start}},"mode":"{{mode}}",
         "url":"{{(mode == "hls" ? "/api/media/hls/s1/master.m3u8" : "/api/media/stream/e1/video.mp4")}}",
         "mimeType":"video/mp4","video":"copy","audio":"aac",
         "subtitles":[{"index":0,"language":"eng","label":"English","url":"/api/media/items/e1/subtitles/0.vtt","forced":false}],
         "nextEpisodeId":"e2","nextEpisodeTitle":"S1 · E2 · Fifteen Million Merits","backdropUrl":null,"posterUrl":null}
        """;
}
