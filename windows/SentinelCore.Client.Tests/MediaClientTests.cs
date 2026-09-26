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
