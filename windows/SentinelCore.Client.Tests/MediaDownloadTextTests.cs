using System.Globalization;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>The wording of movie downloads, which has to match the web dashboard's Movies tab.</summary>
public class MediaDownloadTextTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "")]
    [InlineData(0L, "")]
    [InlineData(-5L, "")]
    [InlineData(1L, "1 MB")]
    [InlineData(400_000_000L, "400 MB")]
    [InlineData(999_400_000L, "999 MB")]
    [InlineData(1_000_000_000L, "1.0 GB")]
    [InlineData(6_400_000_000L, "6.4 GB")]
    public void Sizes(long? bytes, string expected) =>
        Assert.Equal(expected, MediaDownloadText.SizeLabel(bytes, Invariant));

    [Theory]
    [InlineData(null, "")]
    [InlineData(0L, "")]
    [InlineData(200L, "1 KB/s")]
    [InlineData(250_000L, "250 KB/s")]
    [InlineData(1_000_000L, "1.0 MB/s")]
    [InlineData(3_100_000L, "3.1 MB/s")]
    public void Speeds(long? bytesPerSecond, string expected) =>
        Assert.Equal(expected, MediaDownloadText.SpeedLabel(bytesPerSecond, Invariant));

    [Theory]
    [InlineData(null, "")]
    [InlineData(0L, "")]
    [InlineData(75L, "about a minute left")]
    [InlineData(89L, "about a minute left")]
    [InlineData(90L, "2 min left")]
    [InlineData(720L, "12 min left")]
    [InlineData(3599L, "60 min left")]
    [InlineData(3600L, "1 h 0 min left")]
    [InlineData(7500L, "2 h 5 min left")]
    public void Time_left(long? seconds, string expected) =>
        Assert.Equal(expected, MediaDownloadText.EtaLabel(seconds));

    [Theory]
    [InlineData(null, "0%")]
    [InlineData(0.42, "42%")]
    [InlineData(0.4256, "42.6%")]
    [InlineData(1.0, "100%")]
    [InlineData(1.7, "100%")]
    public void Percentages(double? progress, string expected) =>
        Assert.Equal(expected, MediaDownloadText.PercentLabel(progress, Invariant));

    [Theory]
    [InlineData("queued", true, "waiting")]
    [InlineData("downloading", true, "downloading")]
    [InlineData("seeding", true, "sharing back")]
    [InlineData("importing", true, "adding to library")]
    [InlineData("done", false, "ready")]
    [InlineData("failed", false, "failed")]
    [InlineData("canceled", false, "canceled")]
    [InlineData("paused", false, "paused")]
    [InlineData(null, false, "")]
    public void States(string? state, bool active, string word)
    {
        Assert.Equal(active, MediaDownloadText.IsActiveState(state));
        Assert.Equal(word, MediaDownloadText.StateWord(state));
    }

    [Fact]
    public void Ages_follow_the_web()
    {
        Assert.Equal("", MediaDownloadText.AgeLabel(null, Now));
        Assert.Equal("just now", MediaDownloadText.AgeLabel(Now.AddSeconds(-20), Now));
        Assert.Equal("5 min ago", MediaDownloadText.AgeLabel(Now.AddMinutes(-5), Now));
        Assert.Equal("3 h ago", MediaDownloadText.AgeLabel(Now.AddHours(-3), Now));
        Assert.Equal("2 d ago", MediaDownloadText.AgeLabel(Now.AddDays(-2), Now));
        Assert.Contains("2026", MediaDownloadText.AgeLabel(Now.AddDays(-30), Now, Invariant));
    }

    [Fact]
    public void An_active_download_lists_progress_speed_time_size_and_peers()
    {
        var download = new MediaDownload
        {
            State = "downloading", Progress = 0.42, SpeedBytesPerSecond = 3_100_000, EtaSeconds = 75,
            SizeBytes = 400_000_000, Peers = 14,
        };

        Assert.Equal(
            new[] { "42%", "3.1 MB/s", "about a minute left", "400 MB", "14 peers" },
            MediaDownloadText.DownloadFacts(download, Now, Invariant));
        Assert.Equal(0.42, MediaDownloadText.BarFraction(download));
    }

    [Fact]
    public void A_queued_download_says_waiting_with_an_empty_bar()
    {
        var download = new MediaDownload { State = "queued", SizeBytes = 400_000_000, Peers = 3 };

        Assert.Equal(new[] { "waiting", "400 MB" }, MediaDownloadText.DownloadFacts(download, Now, Invariant));
        Assert.Equal(0, MediaDownloadText.BarFraction(download));
        Assert.Equal(1, MediaDownloadText.BarFraction(new MediaDownload { State = "importing" }));
    }

    [Fact]
    public void A_finished_download_lists_state_size_who_and_when()
    {
        var download = new MediaDownload
        {
            State = "done", SizeBytes = 6_400_000_000, RequestedBy = "pat",
            CreatedAt = Now.AddHours(-5), FinishedAt = Now.AddHours(-3),
        };

        Assert.Equal(new[] { "ready", "6.4 GB", "by pat", "3 h ago" }, MediaDownloadText.DownloadFacts(download, Now, Invariant));
        Assert.Equal(new[] { "failed" }, MediaDownloadText.DownloadFacts(new MediaDownload { State = "failed" }, Now, Invariant));
    }

    [Fact]
    public void Release_chips_in_order()
    {
        var release = new MediaRelease
        {
            Quality = "2160p", Codec = "HEVC", Source = "BluRay", Hdr = true, SeasonPack = true,
        };

        Assert.Equal(new[] { "2160p", "HEVC", "BluRay", "HDR", "whole season" }, MediaDownloadText.ReleaseChips(release));
        Assert.Empty(MediaDownloadText.ReleaseChips(new MediaRelease { Hdr = false, SeasonPack = false }));
    }

    [Fact]
    public void Release_facts_skip_what_is_unknown()
    {
        var release = new MediaRelease
        {
            SizeBytes = 6_400_000_000, Seeders = 812, Site = "TheSite", PublishedAt = Now.AddDays(-2),
        };

        Assert.Equal(new[] { "6.4 GB", "812 sharing", "TheSite", "2 d ago" }, MediaDownloadText.ReleaseFacts(release, Now, Invariant));
        Assert.Equal(new[] { "0 sharing" }, MediaDownloadText.ReleaseFacts(new MediaRelease { Seeders = 0 }, Now, Invariant));
    }

    // ---------------------------------------------------------------- Seasons

    private static MediaDownloadSeason Season(int number, params (bool Aired, bool Owned)[] episodes) => new()
    {
        Number = number,
        EpisodeCount = episodes.Length,
        OwnedCount = episodes.Count(e => e.Owned),
        Episodes = episodes.Select((e, i) => new MediaDownloadEpisode { Number = i + 1, Aired = e.Aired, InLibrary = e.Owned }).ToList(),
    };

    [Fact]
    public void A_show_opens_on_the_first_season_with_an_aired_episode_missing()
    {
        var seasons = new List<MediaDownloadSeason>
        {
            Season(1, (true, true), (true, true)),
            Season(2, (true, true), (false, false)), // the gap is not out yet
            Season(3, (true, true), (true, false)),
            Season(0, (true, false)),                // specials never count
        };

        Assert.Equal(3, MediaDownloadText.FirstSeasonToOffer(seasons));
    }

    [Fact]
    public void A_complete_show_opens_on_its_first_season_and_an_empty_one_on_none()
    {
        var complete = new List<MediaDownloadSeason> { Season(1, (true, true)), Season(0, (true, false)) };

        Assert.Equal(1, MediaDownloadText.FirstSeasonToOffer(complete));
        Assert.Null(MediaDownloadText.FirstSeasonToOffer(new List<MediaDownloadSeason>()));
    }

    [Fact]
    public void Season_tabs_and_summary()
    {
        Assert.Equal("S1 ✓", MediaDownloadText.SeasonTabLabel(Season(1, (true, true))));
        Assert.Equal("S2", MediaDownloadText.SeasonTabLabel(Season(2, (true, false))));
        Assert.Equal("Specials", MediaDownloadText.SeasonTabLabel(Season(0)));

        Assert.Equal("Season 2 · 3 episodes · 1 in library",
            MediaDownloadText.SeasonSummary(Season(2, (true, true), (true, false), (false, false))));
        Assert.Equal("Specials · 1 episode",
            MediaDownloadText.SeasonSummary(new MediaDownloadSeason { Number = 0, Name = "Specials", EpisodeCount = 1 }));
    }

    [Fact]
    public void Episode_detail_line()
    {
        Assert.Equal("1 Oct 2018 · in library", MediaDownloadText.EpisodeDetail(
            new MediaDownloadEpisode { AirDate = "2018-10-01", Aired = true, InLibrary = true }, Invariant));
        Assert.Equal("5 Jan 2030 · not out yet", MediaDownloadText.EpisodeDetail(
            new MediaDownloadEpisode { AirDate = "2030-01-05", Aired = false }, Invariant));
        Assert.Equal("", MediaDownloadText.EpisodeDetail(new MediaDownloadEpisode(), Invariant));
    }

    [Fact]
    public void What_a_search_is_for()
    {
        Assert.Equal("Dune: Part Two (2024)",
            MediaDownloadText.SearchWhat(new MediaDownloadSearchInput { Title = "Dune: Part Two", Year = 2024 }));
        Assert.Equal("Bluey (2018) · S02E05",
            MediaDownloadText.SearchWhat(new MediaDownloadSearchInput { Kind = "show", Title = "Bluey", Year = 2018, Season = 2, Episode = 5 }));
        Assert.Equal("Bluey · season 2",
            MediaDownloadText.SearchWhat(new MediaDownloadSearchInput { Kind = "show", Title = "Bluey", Season = 2 }));
    }

    [Theory]
    [InlineData("2", "5", 2, 5)]
    [InlineData(" 2 ", "", 2, null)]
    [InlineData("2", "x", 2, null)]
    [InlineData("", "5", null, null)]   // an episode needs a season
    [InlineData("two", "5", null, null)]
    [InlineData("-1", "", null, null)]
    [InlineData(null, null, null, null)]
    public void Typed_season_and_episode(string? season, string? episode, int? expectedSeason, int? expectedEpisode)
    {
        var (s, e) = MediaDownloadText.ParseSeasonEpisode(season, episode);
        Assert.Equal(expectedSeason, s);
        Assert.Equal(expectedEpisode, e);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)] // an older server that doesn't say has TMDB
    public void Only_a_lookup_without_tmdb_searches_the_sites_straight_away(bool? tmdbConfigured, bool direct) =>
        Assert.Equal(direct, MediaDownloadText.LookupSearchesSitesDirectly(new MediaDownloadLookup { TmdbConfigured = tmdbConfigured }));

    [Fact]
    public void A_direct_movie_search_is_the_typed_text_and_ignores_the_episode_boxes()
    {
        var input = MediaDownloadText.DirectSearchInput("movie", "  Dune Part Two ", "2", "5")!;

        Assert.Equal("movie", input.Kind);
        Assert.Equal("Dune Part Two", input.Title);
        Assert.Null(input.Season);
        Assert.Null(input.Episode);
        Assert.Null(input.Year);
        Assert.Null(input.TmdbId);
    }

    [Theory]
    [InlineData("2", "5", 2, 5)]
    [InlineData("2", "", 2, null)]   // a whole season
    [InlineData("", "", null, null)] // the whole show, as typed
    [InlineData("", "5", null, null)]
    public void A_direct_show_search_takes_the_season_and_episode_when_given(
        string season, string episode, int? expectedSeason, int? expectedEpisode)
    {
        var input = MediaDownloadText.DirectSearchInput("show", "Bluey", season, episode)!;

        Assert.Equal("show", input.Kind);
        Assert.Equal("Bluey", input.Title);
        Assert.Equal(expectedSeason, input.Season);
        Assert.Equal(expectedEpisode, input.Episode);
    }

    [Fact]
    public void Nothing_typed_is_no_direct_search()
    {
        Assert.Null(MediaDownloadText.DirectSearchInput("movie", "   ", null, null));
        Assert.Null(MediaDownloadText.DirectSearchInput("show", null, "1", "1"));
    }

    [Fact]
    public void No_library_warning_only_when_the_server_says_there_is_none()
    {
        var status = new MediaDownloadStatus { MovieLibrary = true, ShowLibrary = false };

        Assert.Null(MediaDownloadText.NoLibraryWarning(status, show: false));
        Assert.Contains("TV library", MediaDownloadText.NoLibraryWarning(status, show: true));
        Assert.Null(MediaDownloadText.NoLibraryWarning(new MediaDownloadStatus(), show: true)); // unknown: no warning
        Assert.Null(MediaDownloadText.NoLibraryWarning(null, show: false));
    }

    [Fact]
    public void Site_field_values_take_the_servers_types()
    {
        Assert.Equal(true, MediaDownloadText.SiteFieldValue("checkbox", null, isChecked: true));
        Assert.Equal(false, MediaDownloadText.SiteFieldValue("checkbox", "true", isChecked: false));
        Assert.Null(MediaDownloadText.SiteFieldValue("number", " "));
        Assert.Equal(100L, MediaDownloadText.SiteFieldValue("number", "100"));
        Assert.Equal(2.5, MediaDownloadText.SiteFieldValue("number", "2.5"));
        Assert.Equal(1L, MediaDownloadText.SiteFieldValue("select", "1"));
        Assert.Equal(-3L, MediaDownloadText.SiteFieldValue("select", "-3"));
        Assert.Equal("newest", MediaDownloadText.SiteFieldValue("select", "newest"));
        Assert.Equal("", MediaDownloadText.SiteFieldValue("select", ""));
        Assert.Equal("pat", MediaDownloadText.SiteFieldValue("text", "pat"));
        Assert.Equal("", MediaDownloadText.SiteFieldValue("password", null));
    }

    // ---------------------------------------------------------------- The VPN light

    private static MediaDownloadServiceStatus Status(string? services, string? vpnState, bool? verified = null,
        string? ip = null, string? location = null, string? country = null, string? error = null) => new()
    {
        Services = services,
        Vpn = new MediaDownloadVpn
        {
            State = vpnState, Verified = verified, Ip = ip, Location = location, Country = country, Error = error,
        },
    };

    private static MediaDownloadText.VpnLookResult Look(MediaDownloadServiceStatus status) =>
        MediaDownloadText.VpnLook(status)!;

    [Fact]
    public void No_status_no_light() => Assert.Null(MediaDownloadText.VpnLook(null));

    [Fact]
    public void Green_only_when_connected_and_verified()
    {
        var look = Look(Status("running", "connected", verified: true, ip: "185.65.134.1", location: "Stockholm, SE", country: "SE"));

        Assert.Equal(MediaDownloadText.VpnTone.On, look.Tone);
        Assert.Equal("VPN · SE", look.Short);
        Assert.Equal("Connected and verified · Stockholm, SE · 185.65.134.1 · your home address is hidden", look.Text);

        var noCountry = Look(Status("running", "connected", verified: true));
        Assert.Equal("VPN on", noCountry.Short);
        Assert.Equal("Connected and verified · your home address is hidden", noCountry.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)] // an older server that never says: not verified
    public void Connected_but_not_verified_is_checking(bool? verified)
    {
        var look = Look(Status("running", "connected", verified: verified, ip: "185.65.134.1", location: "Stockholm, SE", country: "SE"));

        Assert.Equal(MediaDownloadText.VpnTone.Busy, look.Tone);
        Assert.Equal("VPN checking", look.Short);
        Assert.Equal("Connected · Stockholm, SE · 185.65.134.1 · checking it hides your home address…", look.Text);
    }

    [Theory]
    [InlineData("starting", "off")]
    [InlineData("running", "connecting")]
    public void Starting_or_connecting_is_connecting(string services, string state)
    {
        var look = Look(Status(services, state));

        Assert.Equal(MediaDownloadText.VpnTone.Busy, look.Tone);
        Assert.Equal("VPN connecting", look.Short);
        Assert.Equal("Connecting…", look.Text);
    }

    [Theory]
    [InlineData("stopped", "off")]
    [InlineData("stopped", "unknown")]
    [InlineData(null, null)]
    public void Idle_is_grey(string? services, string? state)
    {
        var look = Look(Status(services, state));

        Assert.Equal(MediaDownloadText.VpnTone.Idle, look.Tone);
        Assert.Equal("VPN idle", look.Short);
        Assert.StartsWith("Off until someone searches or downloads", look.Text);
        Assert.Equal(MediaDownloadText.VpnTone.Idle, Look(new MediaDownloadServiceStatus()).Tone);
    }

    [Fact]
    public void External_services_are_grey()
    {
        var look = Look(Status("external", "unknown"));

        Assert.Equal(MediaDownloadText.VpnTone.Idle, look.Tone);
        Assert.Equal("VPN idle", look.Short);
        Assert.Contains("run outside this server", look.Text);
    }

    [Fact]
    public void Disabled_is_amber_and_wins_over_everything()
    {
        foreach (var services in new[] { "running", "notInstalled", "failed", "external" })
        {
            var look = Look(Status(services, "disabled"));
            Assert.Equal(MediaDownloadText.VpnTone.Disabled, look.Tone);
            Assert.Equal("VPN off", look.Short);
            Assert.Contains("home connection", look.Text);
        }
    }

    [Fact]
    public void Problems_are_red()
    {
        var notInstalled = Look(Status("notInstalled", "connected", verified: true));
        Assert.Equal(MediaDownloadText.VpnTone.Problem, notInstalled.Tone);
        Assert.Equal("VPN not set up", notInstalled.Short);
        Assert.Contains("install-downloads.sh", notInstalled.Text);

        var down = Look(Status("running", "down", error: "Handshake failed."));
        Assert.Equal(MediaDownloadText.VpnTone.Problem, down.Tone);
        Assert.Equal("VPN problem", down.Short);
        Assert.Equal("Handshake failed.", down.Text);

        var failed = Look(Status("failed", "off"));
        Assert.Equal(MediaDownloadText.VpnTone.Problem, failed.Tone);
        Assert.Contains("journalctl -u sentinelcore-vpn", failed.Text);
    }

    // ---------------------------------------------------------------- The VPN account

    private static readonly DateTimeOffset AccountNow = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    private static MediaDownloadVpnAccount Account(int daysLeft, bool runningOut = false, bool expired = false,
        string paidUntil = "2026-10-27T12:02:43+00:00") => new()
    {
        Provider = "mullvad",
        PaidUntil = DateTimeOffset.Parse(paidUntil, System.Globalization.CultureInfo.InvariantCulture),
        DaysLeft = daysLeft,
        RunningOut = runningOut,
        Expired = expired,
    };

    private static MediaDownloadText.AccountLineResult Line(MediaDownloadVpnAccount account) =>
        MediaDownloadText.AccountLine(account, AccountNow, TimeZoneInfo.Utc)!;

    [Fact]
    public void An_expired_account_is_red_after_disabled_and_not_installed_but_before_connected()
    {
        var status = new MediaDownloadServiceStatus
        {
            Services = "running",
            Vpn = new MediaDownloadVpn { State = "connected", Verified = true, Country = "SE", Account = Account(0, expired: true) },
        };

        var look = MediaDownloadText.VpnLook(status, AccountNow, TimeZoneInfo.Utc)!;
        Assert.Equal(MediaDownloadText.VpnTone.Problem, look.Tone);
        Assert.Equal("VPN out of time", look.Short);
        Assert.Equal("Your Mullvad account ran out of time on Oct 27. Add time at mullvad.net and downloads work again.", look.Text);

        var disabled = new MediaDownloadServiceStatus
        {
            Services = "running", Vpn = new MediaDownloadVpn { State = "disabled", Account = Account(0, expired: true) },
        };
        Assert.Equal("VPN off", MediaDownloadText.VpnLook(disabled, AccountNow, TimeZoneInfo.Utc)!.Short);

        var notInstalled = new MediaDownloadServiceStatus
        {
            Services = "notInstalled", Vpn = new MediaDownloadVpn { State = "off", Account = Account(0, expired: true) },
        };
        Assert.Equal("VPN not set up", MediaDownloadText.VpnLook(notInstalled, AccountNow, TimeZoneInfo.Utc)!.Short);
    }

    [Fact]
    public void An_account_with_time_left_leaves_the_light_alone()
    {
        var status = new MediaDownloadServiceStatus
        {
            Services = "running",
            Vpn = new MediaDownloadVpn { State = "connected", Verified = true, Country = "SE", Account = Account(3, runningOut: true) },
        };

        Assert.Equal("VPN · SE", MediaDownloadText.VpnLook(status, AccountNow, TimeZoneInfo.Utc)!.Short);
    }

    [Fact]
    public void No_account_no_line() => Assert.Null(MediaDownloadText.AccountLine(null, AccountNow, TimeZoneInfo.Utc));

    [Fact]
    public void Plenty_of_time_is_a_plain_line()
    {
        var line = Line(Account(26));
        Assert.Equal(MediaDownloadText.AccountTone.Plain, line.Tone);
        Assert.Equal("Mullvad paid until Oct 27 · 26 days left", line.Text);

        Assert.Equal("Mullvad paid until Oct 27 · 1 day left", Line(Account(1)).Text);
        Assert.Equal("Mullvad paid until Oct 27 · less than a day left", Line(Account(0)).Text);
    }

    [Fact]
    public void Running_out_is_amber()
    {
        var line = Line(Account(5, runningOut: true));
        Assert.Equal(MediaDownloadText.AccountTone.RunningOut, line.Tone);
        Assert.Equal("Mullvad runs out in 5 days (Oct 27) · add time at mullvad.net", line.Text);

        Assert.Equal("Mullvad runs out tomorrow (Oct 27) · add time at mullvad.net", Line(Account(1, runningOut: true)).Text);
        Assert.Equal("Mullvad runs out today (Oct 27) · add time at mullvad.net", Line(Account(0, runningOut: true)).Text);
    }

    [Fact]
    public void Expired_is_red()
    {
        var line = Line(Account(0, runningOut: true, expired: true));
        Assert.Equal(MediaDownloadText.AccountTone.Expired, line.Tone);
        Assert.Equal("Mullvad ran out on Oct 27 · add time at mullvad.net", line.Text);
    }

    [Fact]
    public void The_date_is_local_and_says_the_year_when_not_this_one()
    {
        Assert.Equal("Mullvad paid until Oct 27, 2027 · 391 days left",
            Line(Account(391, paidUntil: "2027-10-27T12:00:00+00:00")).Text);

        // 02:00 UTC on Oct 28 is still the evening of Oct 27 in Chicago (UTC-5 in October).
        var central = TimeZoneInfo.CreateCustomTimeZone("Test Central", TimeSpan.FromHours(-5), "Test Central", "Test Central");
        Assert.Equal("Oct 27", MediaDownloadText.AccountDate(
            new DateTimeOffset(2026, 10, 28, 2, 0, 0, TimeSpan.Zero), AccountNow, central));
        Assert.Equal("Oct 28", MediaDownloadText.AccountDate(
            new DateTimeOffset(2026, 10, 28, 2, 0, 0, TimeSpan.Zero), AccountNow, TimeZoneInfo.Utc));
    }

    // ---------------------------------------------------------------- Search offers a download

    private static readonly MediaDownloadStatus CanDownload = new() { CanDownload = true };

    [Theory]
    [InlineData("dune 2", 0, false, true)]
    [InlineData("  du  ", 0, false, true)]  // trimmed: "du" is two characters
    [InlineData(" d ", 0, false, false)]    // one character is not a search yet
    [InlineData("", 0, false, false)]
    [InlineData(null, 0, false, false)]
    [InlineData("dune 2", 1, false, false)] // the library has something
    [InlineData("dune 2", 0, true, false)]  // a genre, Unwatched or My List filter may be hiding it
    public void The_offer_needs_text_an_empty_library_answer_and_no_filters(string? query, int items, bool filters, bool expected) =>
        Assert.Equal(expected, MediaDownloadText.ShowsSearchOffer(query, items, filters, CanDownload));

    [Fact]
    public void The_offer_needs_permission_to_download()
    {
        Assert.False(MediaDownloadText.ShowsSearchOffer("dune 2", 0, false, MediaDownloadStatus.Off));
        Assert.False(MediaDownloadText.ShowsSearchOffer("dune 2", 0, false, null));
        Assert.False(MediaDownloadText.ShowsSearchOffer("dune 2", 0, false, new MediaDownloadStatus { Enabled = true, CanDownload = false }));
    }

    [Fact]
    public void Kind_labels_come_from_one_table()
    {
        Assert.Equal(new[] { "movie", "show" }, MediaDownloadText.DownloadKinds.Select(kind => kind.Kind));
        Assert.Equal("Movie", MediaDownloadText.KindLabel("movie"));
        Assert.Equal("TV show", MediaDownloadText.KindLabel("show"));
        Assert.Null(MediaDownloadText.KindLabel("music"));
        Assert.Null(MediaDownloadText.KindLabel(null));
    }

    [Fact]
    public void Offer_results_skip_unknown_kinds_and_stop_at_six()
    {
        var lookup = new MediaDownloadLookup
        {
            Results = new[] { "movie", "music", "show", null, "movie", "show", "movie", "show", "movie" }
                .Select((kind, i) => new MediaDownloadTitle { Kind = kind, TmdbId = i })
                .ToList(),
        };

        var rows = MediaDownloadText.SearchOfferResults(lookup);

        Assert.Equal(new long?[] { 0, 2, 4, 5, 6, 7 }, rows.Select(row => row.TmdbId));
    }

    [Fact]
    public void Offer_wording()
    {
        Assert.Equal("Not in your library", MediaDownloadText.SearchOfferTitle);
        Assert.Equal("Want to download “dune 2”?", MediaDownloadText.SearchOfferLine(" dune 2 "));
        Assert.Equal("Nothing on TMDB called “dune 2”.", MediaDownloadText.SearchOfferNothingOnTmdb("dune 2"));
        Assert.Equal("Search the sites for “dune 2” as a", MediaDownloadText.SearchOfferSitesLine("dune 2"));
    }
}
