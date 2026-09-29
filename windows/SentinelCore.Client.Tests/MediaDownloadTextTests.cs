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
            State = "done", SizeBytes = 6_400_000_000, RequestedBy = "sam",
            CreatedAt = Now.AddHours(-5), FinishedAt = Now.AddHours(-3),
        };

        Assert.Equal(new[] { "ready", "6.4 GB", "by sam", "3 h ago" }, MediaDownloadText.DownloadFacts(download, Now, Invariant));
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
        Assert.Equal("sam", MediaDownloadText.SiteFieldValue("text", "sam"));
        Assert.Equal("", MediaDownloadText.SiteFieldValue("password", null));
    }

    // ---------------------------------------------------------------- VPN line

    private static MediaDownloadServiceStatus Status(string? services, string? vpnState, bool required = true, string? ip = null,
        string? location = null, string? error = null) => new()
    {
        Services = services,
        Vpn = new MediaDownloadVpn { State = vpnState, Required = required, Ip = ip, Location = location, Error = error },
    };

    [Fact]
    public void Vpn_line_wording()
    {
        Assert.Null(MediaDownloadText.VpnLine(null));

        Assert.Equal((MediaDownloadText.VpnTone.Off, "Off - starts by itself when someone searches or downloads."),
            MediaDownloadText.VpnLine(Status("stopped", "off")));
        Assert.Equal((MediaDownloadText.VpnTone.On, "Connected · Stockholm, SE · 185.65.134.1"),
            MediaDownloadText.VpnLine(Status("running", "connected", ip: "185.65.134.1", location: "Stockholm, SE")));
        Assert.Equal((MediaDownloadText.VpnTone.On, "Connected"),
            MediaDownloadText.VpnLine(Status("running", "connected")));
        Assert.Equal((MediaDownloadText.VpnTone.Busy, "Connecting…"),
            MediaDownloadText.VpnLine(Status("starting", "off")));
        Assert.Equal((MediaDownloadText.VpnTone.Busy, "Connecting…"),
            MediaDownloadText.VpnLine(Status("running", "connecting")));
        Assert.Equal((MediaDownloadText.VpnTone.Warn, "Handshake failed."),
            MediaDownloadText.VpnLine(Status("running", "down", error: "Handshake failed.")));
        Assert.Equal(MediaDownloadText.VpnTone.Warn, MediaDownloadText.VpnLine(Status("failed", "off"))!.Value.Tone);
        Assert.Contains("journalctl -u sentinelcore-vpn", MediaDownloadText.VpnLine(Status("failed", "off"))!.Value.Text);
        Assert.Equal(MediaDownloadText.VpnTone.Warn, MediaDownloadText.VpnLine(Status("notInstalled", "connected"))!.Value.Tone);
        Assert.Contains("install-downloads.sh", MediaDownloadText.VpnLine(Status("notInstalled", "off"))!.Value.Text);
        Assert.Equal((MediaDownloadText.VpnTone.Off, "qBittorrent and Prowlarr are run outside this server."),
            MediaDownloadText.VpnLine(Status("external", "unknown", required: true)));
        Assert.Equal((MediaDownloadText.VpnTone.Off, "qBittorrent and Prowlarr are run outside this server; the VPN is not checked."),
            MediaDownloadText.VpnLine(Status("external", "off", required: false)));
        Assert.Equal((MediaDownloadText.VpnTone.Off, "Off - starts by itself when someone searches or downloads."),
            MediaDownloadText.VpnLine(new MediaDownloadServiceStatus()));
    }
}
