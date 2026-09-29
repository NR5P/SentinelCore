using System.Net;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// Movie downloads ("+ Add"), against the contract and sample bodies in core/docs/MOVIE_DOWNLOADS.md:
/// every route, the full objects, and minimal ones from a server that sends less.
/// </summary>
public class MediaDownloadsClientTests
{
    private static SentinelClient ClientFor(FakeServer server) =>
        new(() => server.BaseUrl, NullSentinelLog.Instance);

    private const string StatusJson = """
        { "enabled": true, "canDownload": true, "canManageSites": true, "tmdbConfigured": true,
          "movieLibrary": true, "showLibrary": false,
          "status": { "enabled": true, "services": "running", "idleMinutes": 5,
                      "vpn": { "state": "connected", "required": true, "ip": "185.65.134.1",
                               "location": "Stockholm, SE", "country": "SE", "provider": "AS39351 31173 Services AB",
                               "checkedAt": "2026-09-29T10:00:00Z", "error": null } } }
        """;

    private const string ReleaseJson = """
        { "id": "r1", "title": "Dune.Part.Two.2024.1080p.WEB-DL.DDP5.1.H.264-FLUX", "site": "TheSite",
          "sizeBytes": 6400000000, "seeders": 812, "leechers": 40, "publishedAt": "2026-09-01T10:00:00Z",
          "quality": "1080p", "codec": "H.264", "source": "WEB-DL", "hdr": false,
          "season": null, "episode": null, "seasonPack": false, "matches": true,
          "warning": "Filmed in a cinema - poor picture and sound." }
        """;

    private const string DownloadJson = """
        { "id": "d1", "kind": "episode", "title": "Bluey", "year": 2018, "tmdbId": 82728,
          "season": 2, "episode": 5, "subtitle": "S02E05",
          "posterUrl": "https://image.tmdb.org/t/p/w342/bluey.jpg", "releaseTitle": "Bluey.S02E05.1080p", "site": "TheSite",
          "sizeBytes": 400000000, "state": "downloading",
          "progress": 0.42, "downloadedBytes": 168000000, "speedBytesPerSecond": 3100000, "etaSeconds": 75,
          "peers": 14, "error": "Looking for people sharing it...", "libraryId": "l2", "itemId": null, "requestedBy": "sam",
          "createdAt": "2026-09-29T10:00:00Z", "completedAt": null, "finishedAt": null }
        """;

    // ---------------------------------------------------------------- Status

    [Fact]
    public async Task Status_decodes_the_full_object()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/status", StatusJson);
        using var client = ClientFor(server);

        var status = await client.MediaDownloadStatusAsync();

        Assert.True(status.ShowsAddButton);
        Assert.True(status.ShowsSitesButton);
        Assert.True(status.HasTmdb);
        Assert.True(status.MovieLibrary);
        Assert.False(status.ShowLibrary);
        Assert.Equal("running", status.Status!.Services);
        Assert.Equal(5, status.Status.IdleMinutes);
        var vpn = status.Status.Vpn!;
        Assert.Equal("connected", vpn.State);
        Assert.True(vpn.Required);
        Assert.Equal("185.65.134.1", vpn.Ip);
        Assert.Equal("Stockholm, SE", vpn.Location);
        Assert.Equal("AS39351 31173 Services AB", vpn.Provider);
        Assert.NotNull(vpn.CheckedAt);
        Assert.Null(vpn.Error);
    }

    [Fact]
    public async Task A_minimal_status_decodes_and_hides_add()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/status", """{"enabled":false}""");
        using var client = ClientFor(server);

        var status = await client.MediaDownloadStatusAsync();

        Assert.False(status.ShowsAddButton);
        Assert.False(status.ShowsSitesButton);
        Assert.True(status.HasTmdb); // unknown is assumed present
        Assert.Null(status.Status);
    }

    [Fact]
    public async Task An_older_server_404_means_the_feature_is_off()
    {
        using var server = new FakeServer(); // nothing registered: every route is a 404
        using var client = ClientFor(server);

        var status = await client.MediaDownloadStatusAsync();

        Assert.Same(MediaDownloadStatus.Off, status);
        Assert.False(status.ShowsAddButton);
    }

    [Fact]
    public async Task Other_status_failures_still_throw()
    {
        using var server = new FakeServer().OnGet(
            "/api/media/downloads/status", """{"error":"boom"}""", HttpStatusCode.InternalServerError);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.MediaDownloadStatusAsync());

        Assert.Equal(500, error.Status);
    }

    // ---------------------------------------------------------------- Lookup and show

    [Fact]
    public async Task Lookup_sends_query_and_kind_and_decodes_results()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/lookup", """
            { "tmdbConfigured": true, "results": [
              { "kind": "movie", "tmdbId": 693134, "title": "Dune: Part Two", "year": 2024, "overview": "Paul...",
                "posterPath": "/abc.jpg", "posterUrl": "https://image.tmdb.org/t/p/w342/abc.jpg",
                "inLibrary": true, "itemId": "m9" },
              { "kind": "movie", "tmdbId": 1 } ] }
            """);
        using var client = ClientFor(server);

        var lookup = await client.LookupMediaDownloadAsync(" dune ", "movie");

        Assert.Equal("GET /api/media/downloads/lookup?q=dune&kind=movie", server.Requests.Single());
        Assert.True(lookup.TmdbConfigured);
        Assert.Equal(2, lookup.Results.Count);
        var dune = lookup.Results[0];
        Assert.Equal(693134, dune.TmdbId);
        Assert.Equal(2024, dune.Year);
        Assert.True(dune.IsInLibrary);
        Assert.Equal("m9", dune.ItemId);
        Assert.False(dune.IsShow);
        var bare = lookup.Results[1];
        Assert.Equal("", bare.DisplayTitle);
        Assert.Null(bare.Year);
        Assert.False(bare.IsInLibrary);
    }

    [Fact]
    public async Task Lookup_without_tmdb_is_empty_and_says_so()
    {
        using var server = new FakeServer().OnGet(
            "/api/media/downloads/lookup", """{ "tmdbConfigured": false, "results": null }""");
        using var client = ClientFor(server);

        var lookup = await client.LookupMediaDownloadAsync("dune", "show");

        Assert.False(lookup.TmdbConfigured);
        Assert.Empty(lookup.Results);
    }

    [Fact]
    public async Task Show_decodes_seasons_and_episodes()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/show/82728", """
            { "tmdbId": 82728, "title": "Bluey", "year": 2018, "overview": "…", "posterPath": "/b.jpg",
              "posterUrl": "https://image.tmdb.org/t/p/w342/b.jpg", "itemId": null,
              "seasons": [
                { "number": 1, "name": "Season 1", "episodeCount": 52, "ownedCount": 10,
                  "episodes": [ { "number": 1, "name": "Magic Xylophone", "airDate": "2018-10-01", "aired": true, "inLibrary": true },
                                { "number": 2, "name": "Hospital", "airDate": "2030-01-01", "aired": false, "inLibrary": false } ] },
                { "number": 0, "name": "Specials" } ] }
            """);
        using var client = ClientFor(server);

        var show = await client.MediaDownloadShowAsync(82728);

        Assert.Equal("Bluey", show.Title);
        Assert.Null(show.ItemId);
        Assert.Equal(2, show.Seasons.Count);
        var first = show.Seasons[0];
        Assert.Equal(52, first.EpisodeCount);
        Assert.Equal(10, first.OwnedCount);
        Assert.True(first.Episodes[0].IsAired);
        Assert.True(first.Episodes[0].IsInLibrary);
        Assert.False(first.Episodes[1].IsAired);
        Assert.Empty(show.Seasons[1].Episodes);
        Assert.Equal(0, show.Seasons[1].SeasonNumber);
    }

    // ---------------------------------------------------------------- Search

    [Fact]
    public async Task Search_posts_only_what_is_set_and_polls_by_id()
    {
        using var server = new FakeServer()
            .On("POST", "/api/media/downloads/search",
                """{ "id": "s1", "phase": "connecting", "message": "Connecting to the VPN...", "results": [], "others": [] }""",
                HttpStatusCode.Accepted)
            .OnGet("/api/media/downloads/search/s1", $$"""
                { "id": "s1", "phase": "done", "message": null, "error": null,
                  "kind": "show", "title": "Bluey", "year": 2018, "season": 2, "episode": null,
                  "results": [ {{ReleaseJson}} ], "others": [ { "id": "r2", "title": "Other" } ] }
                """);
        using var client = ClientFor(server);

        var started = await client.StartMediaDownloadSearchAsync(new MediaDownloadSearchInput
        {
            Kind = "show", Title = "Bluey", Year = 2018, TmdbId = 82728, PosterPath = "/b.jpg", Season = 2,
        });
        var done = await client.MediaDownloadSearchAsync("s1");

        Assert.Equal("s1", started.Id);
        Assert.False(started.IsFinished);
        Assert.Equal("Connecting to the VPN...", started.Message);
        var body = server.Bodies[0];
        Assert.Contains("\"kind\":\"show\"", body);
        Assert.Contains("\"title\":\"Bluey\"", body);
        Assert.Contains("\"year\":2018", body);
        Assert.Contains("\"tmdbId\":82728", body);
        Assert.Contains("\"posterPath\":\"/b.jpg\"", body);
        Assert.Contains("\"season\":2", body);
        Assert.DoesNotContain("episode", body); // a whole season: no episode sent

        Assert.True(done.IsDone);
        Assert.Equal(2, done.Season);
        Assert.Null(done.Episode);
        var best = Assert.Single(done.Results);
        Assert.Equal("r1", best.Id);
        Assert.Equal("TheSite", best.Site);
        Assert.Equal(6_400_000_000, best.SizeBytes);
        Assert.Equal(812, best.Seeders);
        Assert.Equal("1080p", best.Quality);
        Assert.Equal("H.264", best.Codec);
        Assert.Equal("WEB-DL", best.Source);
        Assert.False(best.Hdr);
        Assert.True(best.Matches);
        Assert.Equal("Filmed in a cinema - poor picture and sound.", best.Warning);
        var other = Assert.Single(done.Others);
        Assert.Null(other.SizeBytes);
        Assert.Null(other.Seeders);
        Assert.Null(other.Hdr);
    }

    [Fact]
    public async Task A_failed_search_carries_its_message()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/search/s1",
            """{ "id": "s1", "phase": "failed", "message": "The VPN is not connected.", "error": "The VPN is not connected." }""");
        using var client = ClientFor(server);

        var search = await client.MediaDownloadSearchAsync("s1");

        Assert.True(search.IsFailed);
        Assert.True(search.IsFinished);
        Assert.Equal("The VPN is not connected.", search.Message);
        Assert.Empty(search.Results);
        Assert.Empty(search.Others);
    }

    [Fact]
    public async Task An_expired_search_is_a_404_with_the_servers_sentence()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/search/old",
            """{"error":"That search has expired. Search again."}""", HttpStatusCode.NotFound);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.MediaDownloadSearchAsync("old"));

        Assert.Equal(404, error.Status);
        Assert.Equal("That search has expired. Search again.", error.Message);
    }

    // ---------------------------------------------------------------- Queue

    [Fact]
    public async Task The_queue_routes_hit_the_contract()
    {
        using var server = new FakeServer()
            .OnGet("/api/media/downloads", $$"""
                { "status": { "services": "running", "vpn": { "state": "connected" } },
                  "active": [ {{DownloadJson}} ], "recent": [ { "id": "d0", "state": "done", "itemId": "m1" } ] }
                """)
            .On("POST", "/api/media/downloads", DownloadJson, HttpStatusCode.Created)
            .OnGet("/api/media/downloads/d1", DownloadJson)
            .On("DELETE", "/api/media/downloads/d1", "", HttpStatusCode.NoContent)
            .On("POST", "/api/media/downloads/d1/retry", DownloadJson);
        using var client = ClientFor(server);

        var list = await client.MediaDownloadsAsync();
        var grabbed = await client.StartMediaDownloadAsync("s1", "r1");
        var one = await client.MediaDownloadAsync("d1");
        await client.CancelMediaDownloadAsync("d1");
        var retried = await client.RetryMediaDownloadAsync("d1");

        Assert.Equal(
            new[]
            {
                "GET /api/media/downloads",
                "POST /api/media/downloads",
                "GET /api/media/downloads/d1",
                "DELETE /api/media/downloads/d1",
                "POST /api/media/downloads/d1/retry",
            },
            server.Requests);
        Assert.Contains("\"searchId\":\"s1\"", server.Bodies[1]);
        Assert.Contains("\"releaseId\":\"r1\"", server.Bodies[1]);
        Assert.DoesNotContain("libraryId", server.Bodies[1]);

        Assert.Equal("running", list.Status!.Services);
        var active = Assert.Single(list.Active);
        Assert.True(active.IsActive);
        Assert.Equal("S02E05", active.Subtitle);
        Assert.Equal(0.42, active.Progress);
        Assert.Equal(3_100_000, active.SpeedBytesPerSecond);
        Assert.Equal(75, active.EtaSeconds);
        Assert.Equal(14, active.Peers);
        Assert.Equal("Looking for people sharing it...", active.Error);
        Assert.Null(active.ItemId);
        Assert.Null(active.FinishedAt);
        var recent = Assert.Single(list.Recent);
        Assert.True(recent.CanOpen);
        Assert.False(recent.IsActive);
        Assert.Null(recent.Title);
        Assert.Equal("d1", grabbed.Id);
        Assert.Equal("sam", one.RequestedBy);
        Assert.Equal("downloading", retried.State);
    }

    [Fact]
    public async Task A_grab_with_no_library_throws_the_servers_sentence()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads",
            """{"error":"Add one (kind: TV shows) under Movies -> Libraries"}""", HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.StartMediaDownloadAsync("s1", "r1", "l9"));

        Assert.Equal(400, error.Status);
        Assert.Equal("Add one (kind: TV shows) under Movies -> Libraries", error.Message);
        Assert.Contains("\"libraryId\":\"l9\"", server.Bodies[0]);
    }

    [Fact]
    public async Task An_empty_list_decodes_from_nulls()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads", """{ "active": null, "recent": null }""");
        using var client = ClientFor(server);

        var list = await client.MediaDownloadsAsync();

        Assert.Empty(list.Active);
        Assert.Empty(list.Recent);
        Assert.Null(list.Status);
    }

    [Fact]
    public async Task Turned_off_and_not_allowed_surface_the_servers_message()
    {
        using var server = new FakeServer()
            .OnGet("/api/media/downloads", """{"error":"Movie downloads are turned off."}""", HttpStatusCode.NotFound)
            .OnGet("/api/media/downloads/lookup", """{"error":"Only an admin can add movies and shows."}""", HttpStatusCode.Forbidden);
        using var client = ClientFor(server);

        var off = await Assert.ThrowsAsync<SentinelServerException>(() => client.MediaDownloadsAsync());
        var forbidden = await Assert.ThrowsAsync<SentinelServerException>(() => client.LookupMediaDownloadAsync("x", "movie"));

        Assert.Equal(404, off.Status);
        Assert.Equal("Movie downloads are turned off.", off.Message);
        Assert.Equal(403, forbidden.Status);
        Assert.Equal("Only an admin can add movies and shows.", forbidden.Message);
    }

    // ---------------------------------------------------------------- Sites

    [Fact]
    public async Task Sites_list_catalog_add_toggle_and_delete()
    {
        using var server = new FakeServer()
            .OnGet("/api/media/downloads/sites", """
                [ { "id": 3, "name": "TheSite", "enabled": true, "privacy": "public", "language": "en-US",
                    "description": "Movies and TV", "definition": "thesite" }, { "id": 4 } ]
                """)
            .OnGet("/api/media/downloads/sites/catalog", """
                [ { "definition": "private1", "name": "Private One", "description": "…", "privacy": "private", "language": "en-US",
                    "needsSetup": true, "fields": [
                      { "name": "username", "label": "Username", "type": "text", "help": "Your login", "value": null, "options": null },
                      { "name": "freeleech", "label": "Freeleech only", "type": "checkbox", "value": true },
                      { "name": "sort", "label": "Sort", "type": "select", "value": 2,
                        "options": [ { "value": "1", "label": "Newest" }, { "value": "2", "label": "Seeders" } ] },
                      { "name": "limit", "label": "Limit", "type": "number", "value": 100 } ] },
                  { "definition": "public1", "name": "Public One", "needsSetup": false } ]
                """)
            .On("POST", "/api/media/downloads/sites", """{ "id": 5, "name": "Private One", "enabled": true, "privacy": "private" }""",
                HttpStatusCode.Created)
            .On("PUT", "/api/media/downloads/sites/3", """{ "id": 3, "enabled": false }""")
            .On("DELETE", "/api/media/downloads/sites/3", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        var sites = await client.MediaDownloadSitesAsync();
        var catalog = await client.MediaDownloadSiteCatalogAsync(" priv ");
        var added = await client.AddMediaDownloadSiteAsync(new MediaDownloadSiteInput
        {
            Definition = "private1",
            Fields = new Dictionary<string, object?> { ["username"] = "sam", ["freeleech"] = false, ["sort"] = 1L, ["limit"] = null },
        });
        await client.SetMediaDownloadSiteEnabledAsync(3, false);
        await client.DeleteMediaDownloadSiteAsync(3);

        Assert.Equal(
            new[]
            {
                "GET /api/media/downloads/sites",
                "GET /api/media/downloads/sites/catalog?q=priv",
                "POST /api/media/downloads/sites",
                "PUT /api/media/downloads/sites/3",
                "DELETE /api/media/downloads/sites/3",
            },
            server.Requests);

        Assert.Equal(2, sites.Count);
        Assert.Equal(3, sites[0].Id);
        Assert.True(sites[0].IsEnabled);
        Assert.Equal("thesite", sites[0].Definition);
        Assert.False(sites[1].IsEnabled);
        Assert.Null(sites[1].Name);

        Assert.Equal(2, catalog.Count);
        var setup = catalog[0];
        Assert.True(setup.RequiresSetup);
        Assert.Equal(4, setup.Fields.Count);
        Assert.Equal("", setup.Fields[0].ValueText);
        Assert.Empty(setup.Fields[0].Options);
        Assert.Equal("Your login", setup.Fields[0].Help);
        Assert.True(setup.Fields[1].ValueIsTrue);
        Assert.Equal("2", setup.Fields[2].ValueText);
        Assert.Equal("Seeders", setup.Fields[2].Options[1].DisplayLabel);
        Assert.Equal("2", setup.Fields[2].Options[1].ValueText);
        Assert.Equal("100", setup.Fields[3].ValueText);
        Assert.False(catalog[1].RequiresSetup);
        Assert.Empty(catalog[1].Fields);

        Assert.Equal(5, added.Id);
        var body = server.Bodies[2];
        Assert.Contains("\"definition\":\"private1\"", body);
        Assert.Contains("\"username\":\"sam\"", body);
        Assert.Contains("\"freeleech\":false", body);
        Assert.Contains("\"sort\":1", body);
        Assert.Contains("\"limit\":null", body);
        Assert.Contains("\"enabled\":false", server.Bodies[3]);
    }

    [Fact]
    public async Task A_blank_catalog_query_is_the_whole_catalog()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/sites/catalog", "[]");
        using var client = ClientFor(server);

        var catalog = await client.MediaDownloadSiteCatalogAsync(null);

        Assert.Empty(catalog);
        Assert.Equal("GET /api/media/downloads/sites/catalog?q=", server.Requests.Single());
    }

    [Fact]
    public async Task A_vpn_still_starting_is_a_503_flagged_starting()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/sites",
            """{ "error": "Connecting to the VPN... try again in a moment.", "starting": true }""",
            HttpStatusCode.ServiceUnavailable);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.MediaDownloadSitesAsync());

        Assert.Equal(503, error.Status);
        Assert.True(error.Starting);
        Assert.False(error.NeedsLogin);
        Assert.Equal("Connecting to the VPN... try again in a moment.", error.Message);
    }

    [Fact]
    public async Task A_site_that_fails_its_test_is_a_400_not_starting()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads/sites",
            """{ "error": "The site turned down that login." }""", HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() =>
            client.AddMediaDownloadSiteAsync(new MediaDownloadSiteInput { Definition = "x" }));

        Assert.Equal(400, error.Status);
        Assert.False(error.Starting);
        Assert.Equal("The site turned down that login.", error.Message);
    }

    // ---------------------------------------------------------------- Artwork

    [Fact]
    public void Absolute_poster_urls_are_used_as_they_are()
    {
        using var client = new SentinelClient(() => "http://192.168.1.50:3320", NullSentinelLog.Instance);

        Assert.Equal(
            "https://image.tmdb.org/t/p/w342/abc.jpg",
            client.Url("https://image.tmdb.org/t/p/w342/abc.jpg")!.ToString());
        Assert.Equal(
            "http://192.168.1.50:3320/api/media/items/m1/poster",
            client.Url("/api/media/items/m1/poster")!.ToString());
    }
}
