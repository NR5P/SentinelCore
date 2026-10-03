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

    [Fact]
    public async Task Status_decodes_verified_and_treats_missing_as_not_verified()
    {
        using var server = new FakeServer()
            .OnGet("/api/media/downloads/status", """
                { "canDownload": true, "status": { "services": "running",
                  "vpn": { "state": "connected", "verified": true, "verifiedAt": "2026-09-29T10:00:00Z", "country": "SE" } } }
                """);
        using var client = ClientFor(server);

        var status = await client.MediaDownloadStatusAsync();

        Assert.True(status.Status!.Vpn!.IsVerified);
        Assert.Equal("2026-09-29T10:00:00Z", status.Status.Vpn.VerifiedAt);
        Assert.False(status.VpnDisabled);
        Assert.False(StatusJsonVpn().IsVerified); // the older sample above has no "verified"

        static MediaDownloadVpn StatusJsonVpn() =>
            System.Text.Json.JsonSerializer.Deserialize<MediaDownloadStatus>(StatusJson, SentinelClient.JsonOptions)!.Status!.Vpn!;
    }

    [Fact]
    public async Task A_disabled_vpn_decodes_with_null_addresses()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/status", """
            { "enabled": true, "canDownload": true,
              "status": { "enabled": true, "services": "stopped", "idleMinutes": 5,
                          "vpn": { "state": "disabled", "required": false, "verified": false, "verifiedAt": null,
                                   "ip": null, "location": null, "country": null, "provider": null,
                                   "checkedAt": null, "error": null } } }
            """);
        using var client = ClientFor(server);

        var status = await client.MediaDownloadStatusAsync();

        Assert.True(status.VpnDisabled);
        var vpn = status.Status!.Vpn!;
        Assert.True(vpn.IsDisabled);
        Assert.False(vpn.Required);
        Assert.False(vpn.IsVerified);
        Assert.Null(vpn.VerifiedAt);
        Assert.Null(vpn.Ip);
        Assert.Null(vpn.Country);
        Assert.Equal(MediaDownloadText.VpnTone.Disabled, MediaDownloadText.VpnLook(status.Status)!.Tone);
    }

    [Fact]
    public async Task A_mullvad_account_decodes_and_absent_or_null_is_no_account()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/status", """
            { "enabled": true, "canDownload": true,
              "status": { "services": "running",
                          "vpn": { "state": "connected", "verified": true, "error": null,
                                   "account": { "provider": "mullvad", "paidUntil": "2026-10-27T12:02:43+00:00",
                                                "daysLeft": 26, "runningOut": false, "expired": false,
                                                "checkedAt": "2026-10-01T15:00:00+00:00" } } } }
            """);
        using var client = ClientFor(server);

        var account = (await client.MediaDownloadStatusAsync()).Status!.Vpn!.Account!;

        Assert.Equal("mullvad", account.Provider);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 12, 2, 43, TimeSpan.Zero), account.PaidUntil);
        Assert.Equal(26, account.DaysLeft);
        Assert.False(account.IsRunningOut);
        Assert.False(account.IsExpired);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), account.CheckedAt);

        foreach (var vpn in new[] { """{ "state": "connected" }""", """{ "state": "connected", "account": null }""" })
        {
            using var other = new FakeServer().OnGet("/api/media/downloads/status",
                $$"""{ "canDownload": true, "status": { "services": "running", "vpn": {{vpn}} } }""");
            using var otherClient = ClientFor(other);

            var status = await otherClient.MediaDownloadStatusAsync();

            Assert.Null(status.Status!.Vpn!.Account);
            Assert.Null(MediaDownloadText.AccountLine(status.Status.Vpn.Account, DateTimeOffset.Now, TimeZoneInfo.Local));
        }
    }

    [Fact]
    public async Task An_expired_account_in_a_vpn_test_turns_the_light_red()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads/vpn/test", """
            { "ok": false, "message": "The VPN would not connect.",
              "status": { "services": "running",
                          "vpn": { "state": "down", "account": { "provider": "mullvad", "paidUntil": "2026-09-27T12:00:00+00:00",
                                                                  "daysLeft": 0, "runningOut": true, "expired": true } } } }
            """);
        using var client = ClientFor(server);

        var test = await client.TestMediaDownloadVpnAsync();

        Assert.Equal("VPN out of time", MediaDownloadText.VpnLook(test.Status)!.Short);
        Assert.True(test.Status!.Vpn!.Account!.IsExpired);
    }

    [Fact]
    public async Task Vpn_test_posts_and_decodes_success_and_failure()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads/vpn/test", """
            { "ok": true, "message": "Working. The sites see 185.195.233.76 in Stockholm, SE, not your home address.",
              "status": { "enabled": true, "services": "running", "idleMinutes": 5,
                          "vpn": { "state": "connected", "required": true, "verified": true,
                                   "verifiedAt": "2026-09-29T10:00:00Z", "ip": "185.195.233.76",
                                   "location": "Stockholm, SE", "country": "SE" } } }
            """);
        using var client = ClientFor(server);

        var test = await client.TestMediaDownloadVpnAsync();

        Assert.Equal("POST /api/media/downloads/vpn/test", server.Requests.Single());
        Assert.True(test.Succeeded);
        Assert.StartsWith("Working.", test.Message);
        Assert.Equal("VPN · SE", MediaDownloadText.VpnLook(test.Status)!.Short);
    }

    [Fact]
    public async Task A_failed_vpn_test_is_data_not_an_exception()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads/vpn/test", """
            { "ok": false, "message": "The sites would see your home address. Downloads are stopped.",
              "status": { "services": "running", "vpn": { "state": "down", "error": "Same address as home." } } }
            """);
        using var client = ClientFor(server);

        var test = await client.TestMediaDownloadVpnAsync();

        Assert.False(test.Succeeded);
        Assert.Equal("The sites would see your home address. Downloads are stopped.", test.Message);
        Assert.Equal(MediaDownloadText.VpnTone.Problem, MediaDownloadText.VpnLook(test.Status)!.Tone);
    }

    [Fact]
    public async Task A_minimal_vpn_test_answer_decodes()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads/vpn/test", "{}");
        using var client = ClientFor(server);

        var test = await client.TestMediaDownloadVpnAsync();

        Assert.False(test.Succeeded);
        Assert.Null(test.Message);
        Assert.Null(test.Status);
    }

    // The VPN settings come through the schema-driven Settings window; the one field with a list
    // of its own (Mullvad Location) has to decode its optionsSource and fetch it like any other.
    [Fact]
    public async Task The_mullvad_location_field_lists_its_options_through_the_settings_schema()
    {
        using var server = new FakeServer()
            .OnGet("/api/settings/schema", """
                [ { "key": "mediaDownloads", "title": "MOVIE DOWNLOADS", "endpoint": "/api/settings/media-downloads",
                    "basic": false, "description": "", "fields": [
                      { "name": "vpnProvider", "label": "VPN Provider", "type": "select", "tip": "",
                        "options": [ { "value": "installed", "label": "The file installed on the server" },
                                     { "value": "mullvad", "label": "Mullvad (account number)" },
                                     { "value": "wireguard", "label": "Another WireGuard provider" } ] },
                      { "name": "mullvadAccountNumber", "label": "Mullvad Account Number", "type": "password", "tip": "" },
                      { "name": "mullvadLocation", "label": "Mullvad Location", "type": "text", "tip": "",
                        "optionsSource": { "label": "Mullvad locations", "tip": "Lists every country and city...",
                          "endpoint": "/api/settings/media-downloads/mullvad-locations",
                          "emptyMessage": "Mullvad's server list came back empty.", "typeItMessage": "Type a country code." } },
                      { "name": "useVpn", "label": "Use VPN For Movie Downloads", "type": "checkbox", "tip": "" } ] } ]
                """)
            .OnGet("/api/settings/media-downloads/mullvad-locations", """
                { "options": [ { "value": "se", "label": "Sweden (any city)" }, { "value": "se-sto", "label": "Stockholm, Sweden" } ] }
                """);
        using var client = ClientFor(server);

        var section = Assert.Single(await client.SettingsSchemaAsync());
        var location = section.Fields.Single(field => field.Name == "mullvadLocation");
        var options = await client.SettingsOptionsAsync(location.OptionsSource!);

        Assert.Equal("text", location.Type);
        Assert.Equal("/api/settings/media-downloads/mullvad-locations", location.OptionsSource!.Endpoint);
        Assert.Equal(3, section.Fields.Single(field => field.Name == "vpnProvider").Options!.Count);
        Assert.Equal(new[] { "se", "se-sto" }, options.Select(option => option.Value));
        Assert.Equal("Stockholm, Sweden", options[1].Label);
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
    public async Task Lookup_any_sends_kind_any_and_skips_kinds_it_does_not_know()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/lookup", """
            { "tmdbConfigured": true, "results": [
              { "kind": "show", "tmdbId": 1, "title": "Dune: Prophecy", "year": 2024 },
              { "kind": "music", "tmdbId": 2, "title": "Dune (Soundtrack)" },
              { "kind": "movie", "tmdbId": 3, "title": "Dune", "year": 2021, "inLibrary": true, "itemId": "m1" },
              { "tmdbId": 4, "title": "No kind" } ] }
            """);
        using var client = ClientFor(server);

        var lookup = await client.LookupAnyMediaDownloadAsync(" dune ");

        Assert.Equal("GET /api/media/downloads/lookup?q=dune&kind=any", server.Requests.Single());
        Assert.True(lookup.TmdbConfigured);
        Assert.Equal(new long?[] { 1, 3 }, lookup.Results.Select(result => result.TmdbId));
        Assert.True(lookup.Results[0].IsShow);
        Assert.True(lookup.Results[1].IsInLibrary);
        Assert.Equal("m1", lookup.Results[1].ItemId);
    }

    [Fact]
    public async Task Lookup_any_without_tmdb_says_so()
    {
        using var server = new FakeServer().OnGet(
            "/api/media/downloads/lookup", """{ "tmdbConfigured": false, "results": [] }""");
        using var client = ClientFor(server);

        var lookup = await client.LookupAnyMediaDownloadAsync("dune");

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
        Assert.DoesNotContain("replace", server.Bodies[1]);

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
    public async Task A_movie_already_in_the_library_throws_what_the_library_has()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads", """
            { "error": "You already have Dune (2021).", "code": "alreadyInLibrary",
              "existing": { "itemId": "m7", "title": "Dune", "year": 2021, "sizeBytes": 1400000000, "height": 720, "canReplace": true } }
            """, HttpStatusCode.Conflict);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<MediaAlreadyInLibraryException>(() => client.StartMediaDownloadAsync("s1", "r1"));

        Assert.Equal("You already have Dune (2021).", error.Message);
        Assert.Equal("m7", error.Existing.ItemId);
        Assert.Equal("Dune", error.Existing.Title);
        Assert.Equal(2021, error.Existing.Year);
        Assert.Equal(1_400_000_000, error.Existing.SizeBytes);
        Assert.Equal(720, error.Existing.Height);
        Assert.True(error.Existing.IsReplaceable);
        Assert.Equal("720p · 1.4 GB", error.Existing.QualityText(System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("replace", server.Bodies[0]);
    }

    [Fact]
    public async Task Another_409_stays_a_server_error()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads",
            """{"error":"Something else."}""", HttpStatusCode.Conflict);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.StartMediaDownloadAsync("s1", "r1"));

        Assert.Equal(409, error.Status);
        Assert.Null(error.Code);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_replaced_says_so_and_unknown_quality_is_blank()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads", """
            { "error": "You already have Dune (2021).", "code": "alreadyInLibrary",
              "existing": { "itemId": "m7", "canReplace": false } }
            """, HttpStatusCode.Conflict);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<MediaAlreadyInLibraryException>(() => client.StartMediaDownloadAsync("s1", "r1"));

        Assert.False(error.Existing.IsReplaceable);
        Assert.Equal("", error.Existing.QualityText());
    }

    [Theory]
    [InlineData(true, "\"replace\":true")]
    [InlineData(false, "\"replace\":false")]
    public async Task The_answer_is_sent_back_as_replace(bool replace, string expected)
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads",
            """{ "id": "d2", "state": "queued", "replacesItemId": "m7" }""", HttpStatusCode.Created);
        using var client = ClientFor(server);

        var grabbed = await client.StartMediaDownloadAsync("s1", "r1", replace: replace);

        Assert.Contains(expected, server.Bodies[0]);
        Assert.Contains("\"searchId\":\"s1\"", server.Bodies[0]);
        Assert.Equal("m7", grabbed.ReplacesItemId);
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
