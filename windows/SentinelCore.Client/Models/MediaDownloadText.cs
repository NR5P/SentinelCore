using System.Globalization;
using System.Text.Json;
using SentinelCore.Client.Models;

namespace SentinelCore.Client;

/// <summary>
/// The wording and small decisions of movie downloads - sizes, speeds, time left, the VPN line,
/// a release's chips, which season to open on - as pure functions, so they can be checked without
/// a window. They follow the web dashboard's Movies tab (core/SentinelCore.Web/wwwroot/movies.js,
/// "downloads (+ add)") so every app says the same thing.
/// </summary>
public static class MediaDownloadText
{
    /// <summary>The states a download is still moving in.</summary>
    public static bool IsActiveState(string? state) =>
        state is "queued" or "downloading" or "seeding" or "importing";

    /// <summary>A state in words: "sharing back" rather than "seeding".</summary>
    public static string StateWord(string? state) => state switch
    {
        "queued" => "waiting",
        "downloading" => "downloading",
        "seeding" => "sharing back",
        "importing" => "adding to library",
        "done" => "ready",
        "failed" => "failed",
        "canceled" => "canceled",
        null => "",
        _ => state,
    };

    public static string Plural(long count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";

    /// <summary>"6.4 GB", "400 MB"; "" for nothing or an unknown size.</summary>
    public static string SizeLabel(long? bytes, CultureInfo? culture = null)
    {
        var b = bytes ?? 0;
        if (b <= 0) return "";
        culture ??= CultureInfo.CurrentCulture;
        if (b >= 1_000_000_000) return $"{(b / 1e9).ToString("0.0", culture)} GB";
        return $"{Math.Max(1, (long)Math.Round(b / 1e6, MidpointRounding.AwayFromZero))} MB";
    }

    /// <summary>"3.1 MB/s", "250 KB/s"; "" when nothing is moving.</summary>
    public static string SpeedLabel(long? bytesPerSecond, CultureInfo? culture = null)
    {
        var b = bytesPerSecond ?? 0;
        if (b <= 0) return "";
        culture ??= CultureInfo.CurrentCulture;
        if (b >= 1_000_000) return $"{(b / 1e6).ToString("0.0", culture)} MB/s";
        return $"{Math.Max(1, (long)Math.Round(b / 1e3, MidpointRounding.AwayFromZero))} KB/s";
    }

    /// <summary>"about a minute left", "12 min left", "2 h 5 min left"; "" when unknown.</summary>
    public static string EtaLabel(long? seconds)
    {
        var s = seconds ?? 0;
        if (s <= 0) return "";
        if (s < 90) return "about a minute left";
        if (s < 3600) return $"{(long)Math.Round(s / 60d, MidpointRounding.AwayFromZero)} min left";
        return $"{s / 3600} h {(long)Math.Round(s % 3600 / 60d, MidpointRounding.AwayFromZero)} min left";
    }

    /// <summary>Progress to one decimal place: "42%", "42.5%".</summary>
    public static string PercentLabel(double? progress, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var percent = Math.Round(Math.Clamp(progress ?? 0, 0, 1) * 1000, MidpointRounding.AwayFromZero) / 10;
        return $"{percent.ToString("0.#", culture)}%";
    }

    /// <summary>How full the bar is: a download's own progress, empty while queued, full once past downloading.</summary>
    public static double BarFraction(MediaDownload download) => download.State switch
    {
        "downloading" => Math.Clamp(download.Progress ?? 0, 0, 1),
        "queued" => 0,
        _ => 1,
    };

    /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago", then the date - the web's wording.</summary>
    public static string AgeLabel(DateTimeOffset? at, DateTimeOffset now, CultureInfo? culture = null)
    {
        if (at is not { } moment) return "";
        var seconds = (now - moment).TotalSeconds;
        if (seconds < 60) return "just now";
        if (seconds < 3600) return $"{(long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero)} min ago";
        if (seconds < 86400) return $"{(long)Math.Round(seconds / 3600, MidpointRounding.AwayFromZero)} h ago";
        if (seconds < 86400 * 7) return $"{(long)Math.Round(seconds / 86400, MidpointRounding.AwayFromZero)} d ago";
        return moment.ToLocalTime().ToString("d MMM yyyy", culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The facts line under a download: while active the percentage (or the state), speed, time
    /// left, size and peers; once finished the state, size, who asked and when.
    /// </summary>
    public static IReadOnlyList<string> DownloadFacts(MediaDownload d, DateTimeOffset now, CultureInfo? culture = null)
    {
        var downloading = d.State == "downloading";
        var facts = d.IsActive
            ? new[]
            {
                downloading ? PercentLabel(d.Progress, culture) : StateWord(d.State),
                downloading ? SpeedLabel(d.SpeedBytesPerSecond, culture) : "",
                downloading ? EtaLabel(d.EtaSeconds) : "",
                SizeLabel(d.SizeBytes, culture),
                downloading && d.Peers is > 0 ? Plural(d.Peers.Value, "peer") : "",
            }
            : new[]
            {
                StateWord(d.State),
                SizeLabel(d.SizeBytes, culture),
                string.IsNullOrWhiteSpace(d.RequestedBy) ? "" : $"by {d.RequestedBy}",
                AgeLabel(d.FinishedAt ?? d.CreatedAt, now, culture),
            };
        return facts.Where(fact => fact.Length > 0).ToList();
    }

    /// <summary>A release's labels, in order: quality, codec, source, HDR, whole season.</summary>
    public static IReadOnlyList<string> ReleaseChips(MediaRelease release)
    {
        var chips = new List<string>();
        if (!string.IsNullOrWhiteSpace(release.Quality)) chips.Add(release.Quality!);
        if (!string.IsNullOrWhiteSpace(release.Codec)) chips.Add(release.Codec!);
        if (!string.IsNullOrWhiteSpace(release.Source)) chips.Add(release.Source!);
        if (release.Hdr == true) chips.Add("HDR");
        if (release.SeasonPack == true) chips.Add("whole season");
        return chips;
    }

    /// <summary>The line under a release: size, how many are sharing it, the site, how old.</summary>
    public static IReadOnlyList<string> ReleaseFacts(MediaRelease release, DateTimeOffset now, CultureInfo? culture = null)
    {
        var facts = new[]
        {
            SizeLabel(release.SizeBytes, culture),
            release.Seeders is { } seeders ? $"{seeders} sharing" : "",
            release.Site ?? "",
            AgeLabel(release.PublishedAt, now, culture),
        };
        return facts.Where(fact => fact.Length > 0).ToList();
    }

    /// <summary>
    /// The season a show opens on: the first real season (not specials) with an episode that is
    /// out and not in the library yet, else the first season listed, else none.
    /// </summary>
    public static int? FirstSeasonToOffer(IReadOnlyList<MediaDownloadSeason> seasons)
    {
        var gap = seasons.FirstOrDefault(season =>
            season.Number is > 0 && season.Episodes.Any(episode => episode.IsAired && !episode.IsInLibrary));
        return (gap ?? seasons.FirstOrDefault())?.Number;
    }

    /// <summary>"S1", "Specials", with a tick once every episode is in the library.</summary>
    public static string SeasonTabLabel(MediaDownloadSeason season)
    {
        var label = season.SeasonNumber == 0 ? "Specials" : $"S{season.SeasonNumber}";
        var complete = season.EpisodeCount is > 0 && season.OwnedCount == season.EpisodeCount;
        return complete ? $"{label} ✓" : label;
    }

    /// <summary>"Season 2 · 52 episodes · 10 in library".</summary>
    public static string SeasonSummary(MediaDownloadSeason season)
    {
        var name = string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.SeasonNumber}" : season.Name!;
        var parts = new List<string> { name, Plural(season.EpisodeCount ?? season.Episodes.Count, "episode") };
        if (season.OwnedCount is > 0) parts.Add($"{season.OwnedCount} in library");
        return string.Join(" · ", parts);
    }

    /// <summary>"1 Oct 2018 · in library", "· not out yet" - the small line under an episode's name.</summary>
    public static string EpisodeDetail(MediaDownloadEpisode episode, CultureInfo? culture = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(episode.AirDate))
        {
            parts.Add(DateOnly.TryParseExact(episode.AirDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToString("d MMM yyyy", culture ?? CultureInfo.CurrentCulture)
                : episode.AirDate!);
        }
        if (episode.IsInLibrary) parts.Add("in library");
        if (!episode.IsAired) parts.Add("not out yet");
        return string.Join(" · ", parts);
    }

    /// <summary>What a search is for: "Dune: Part Two (2024)", "Bluey (2018) · S02E05", "Bluey · season 2".</summary>
    public static string SearchWhat(MediaDownloadSearchInput input)
    {
        var what = input.Year is { } year ? $"{input.Title} ({year})" : input.Title;
        if (input.Season is not { } season) return what;
        return input.Episode is { } episode
            ? $"{what} · S{season:00}E{episode:00}"
            : $"{what} · season {season}";
    }

    /// <summary>
    /// The season and episode typed into the no-TMDB search: whole numbers only, and an episode
    /// only counts alongside a season (the server refuses an episode on its own).
    /// </summary>
    public static (int? Season, int? Episode) ParseSeasonEpisode(string? seasonText, string? episodeText)
    {
        var season = WholeNumber(seasonText);
        if (season is null) return (null, null);
        return (season, WholeNumber(episodeText));
    }

    /// <summary>
    /// Whether a lookup's answer means "search the sites straight away": the server has no TMDB
    /// key, so there are no titles to pick from and stopping at a second button only made every
    /// search look empty. Only an explicit false counts - an older server that doesn't say has TMDB.
    /// </summary>
    public static bool LookupSearchesSitesDirectly(MediaDownloadLookup lookup) => lookup.TmdbConfigured == false;

    /// <summary>
    /// The search the sites get for exactly what was typed: the kind, the text as the title, and
    /// for TV the season and episode boxes when they hold whole numbers (an episode only with a
    /// season). Null when nothing was typed.
    /// </summary>
    public static MediaDownloadSearchInput? DirectSearchInput(string kind, string? query, string? seasonText, string? episodeText)
    {
        var title = query?.Trim() ?? "";
        if (title.Length == 0) return null;
        var show = kind == "show";
        var (season, episode) = show ? ParseSeasonEpisode(seasonText, episodeText) : (null, null);
        return new MediaDownloadSearchInput
        {
            Kind = show ? "show" : "movie",
            Title = title,
            Season = season,
            Episode = episode,
        };
    }

    private static int? WholeNumber(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit)) return null;
        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>A warning when there is no library on the server's own disks of the kind being added.</summary>
    public static string? NoLibraryWarning(MediaDownloadStatus? status, bool show)
    {
        if (status is null) return null;
        var missing = show ? status.ShowLibrary == false : status.MovieLibrary == false;
        if (!missing) return null;
        return show
            ? "There is no TV library on the server's own disks to save into yet. Add one under Movies & TV libraries (kind: TV Shows) first."
            : "There is no movie library on the server's own disks to save into yet. Add one under Movies & TV libraries first.";
    }

    /// <summary>
    /// A site setup field's typed value as the server wants it: checkboxes true/false, numbers as
    /// numbers (blank is null), select options that are whole numbers as numbers, the rest text.
    /// </summary>
    public static object? SiteFieldValue(string? type, string? text, bool isChecked = false)
    {
        switch (type)
        {
            case "checkbox":
                return isChecked;
            case "number":
            {
                var trimmed = text?.Trim() ?? "";
                if (trimmed.Length == 0) return null;
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var local)) return local;
                return trimmed;
            }
            case "select":
            {
                var value = text ?? "";
                return value.Length > 0 && (value[0] == '-' ? value.Length > 1 && value[1..].All(char.IsAsciiDigit) : value.All(char.IsAsciiDigit))
                       && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var option)
                    ? option
                    : value;
            }
            default:
                return text ?? "";
        }
    }

    /// <summary>A JSON value as the text a box shows: strings bare, numbers as written, nothing for null.</summary>
    public static string JsonText(JsonElement? value) => value switch
    {
        null => "",
        { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "",
        { } element => element.GetRawText(),
    };

    /// <summary>The VPN light's colour (core/docs/MOVIE_DOWNLOADS.md, "The VPN light").</summary>
    public enum VpnTone
    {
        /// <summary>Green: connected, and the sites were checked to see another address than home.</summary>
        On,

        /// <summary>Cyan, pulsing: connected but not yet verified, connecting, or the services starting.</summary>
        Busy,

        /// <summary>Grey: off until someone needs it, or run outside this server.</summary>
        Idle,

        /// <summary>Amber: Use VPN is off in Settings - the home connection is used.</summary>
        Disabled,

        /// <summary>Red: not installed, failed, down, or the VPN account out of time.</summary>
        Problem,
    }

    /// <summary>
    /// How the VPN light looks: its tone, the pill's few words ("VPN · SE") and the Downloads
    /// screen's sentence. One function for both, so they can never disagree.
    /// </summary>
    public sealed record VpnLookResult(VpnTone Tone, string Short, string Text);

    /// <summary>
    /// The look for a status, following the web's <c>vpnLook</c> in the table's order: disabled,
    /// not installed, account out of time, connected (verified or not), connecting, failed or
    /// down, external, idle. Null for no status at all - there is then no light to draw.
    /// <paramref name="now"/> and <paramref name="zone"/> (the device's clock and time zone when
    /// left out) only date the out-of-time sentence.
    /// </summary>
    public static VpnLookResult? VpnLook(MediaDownloadServiceStatus? status, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        if (status is null) return null;
        var vpn = status.Vpn;
        var state = vpn?.State;
        var services = status.Services;

        if (state == "disabled")
        {
            return new VpnLookResult(VpnTone.Disabled, "VPN off",
                "Turned off in Settings · searching and downloading use your home connection, so the sites see your home address.");
        }
        if (services == "notInstalled")
        {
            return new VpnLookResult(VpnTone.Problem, "VPN not set up",
                "The VPN and torrent services are not installed on the server yet. Run core/downloads/install-downloads.sh on it once.");
        }
        if (vpn?.Account is { IsExpired: true } account)
        {
            var on = account.PaidUntil is { } until
                ? $" on {AccountDate(until, now ?? DateTimeOffset.Now, zone ?? TimeZoneInfo.Local)}"
                : "";
            return new VpnLookResult(VpnTone.Problem, "VPN out of time",
                $"Your Mullvad account ran out of time{on}. Add time at mullvad.net and downloads work again.");
        }
        if (state == "connected")
        {
            var where = "";
            if (!string.IsNullOrWhiteSpace(vpn!.Location)) where += $" · {vpn.Location}";
            if (!string.IsNullOrWhiteSpace(vpn.Ip)) where += $" · {vpn.Ip}";
            return vpn.IsVerified
                ? new VpnLookResult(VpnTone.On,
                    string.IsNullOrWhiteSpace(vpn.Country) ? "VPN on" : $"VPN · {vpn.Country}",
                    $"Connected and verified{where} · your home address is hidden")
                : new VpnLookResult(VpnTone.Busy, "VPN checking", $"Connected{where} · checking it hides your home address…");
        }
        if (services == "starting" || state == "connecting") return new VpnLookResult(VpnTone.Busy, "VPN connecting", "Connecting…");
        if (services == "failed" || state == "down")
        {
            return new VpnLookResult(VpnTone.Problem, "VPN problem", string.IsNullOrWhiteSpace(vpn?.Error)
                ? "The VPN or torrent services failed to start. Check the server's log (journalctl -u sentinelcore-vpn)."
                : vpn!.Error!);
        }
        if (services == "external")
        {
            return new VpnLookResult(VpnTone.Idle, "VPN idle",
                "qBittorrent and Prowlarr are run outside this server; the VPN is checked when they are used.");
        }
        return new VpnLookResult(VpnTone.Idle, "VPN idle",
            "Off until someone searches or downloads, then it connects by itself. Press Test to check it now.");
    }

    /// <summary>The account line's colour (core/docs/MOVIE_DOWNLOADS.md, "The account line").</summary>
    public enum AccountTone
    {
        /// <summary>Secondary text: plenty of time left.</summary>
        Plain,

        /// <summary>Amber: 7 days or less left.</summary>
        RunningOut,

        /// <summary>Red: the paid time is used up.</summary>
        Expired,
    }

    /// <summary>The account line under the VPN light: its words and colour.</summary>
    public sealed record AccountLineResult(AccountTone Tone, string Text);

    /// <summary>
    /// The account line for <c>status.vpn.account</c> - "Mullvad paid until Oct 27 · 26 days
    /// left", or a warning to add time when it runs out within the week or has run out. Null when
    /// there is no account to show (not Mullvad, or an older server).
    /// </summary>
    public static AccountLineResult? AccountLine(MediaDownloadVpnAccount? account, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (account is null) return null;
        var date = account.PaidUntil is { } until ? AccountDate(until, now, zone) : null;
        var days = Math.Max(0, account.DaysLeft ?? 0);

        if (account.IsExpired)
        {
            return new AccountLineResult(AccountTone.Expired, date is null
                ? "Mullvad ran out · add time at mullvad.net"
                : $"Mullvad ran out on {date} · add time at mullvad.net");
        }
        if (account.IsRunningOut)
        {
            var when = days switch
            {
                0 => "today",
                1 => "tomorrow",
                _ => $"in {days} days",
            };
            var dated = date is null ? "" : $" ({date})";
            return new AccountLineResult(AccountTone.RunningOut, $"Mullvad runs out {when}{dated} · add time at mullvad.net");
        }
        var left = days switch
        {
            0 => "less than a day left",
            1 => "1 day left",
            _ => $"{days} days left",
        };
        return new AccountLineResult(AccountTone.Plain, date is null
            ? $"Mullvad paid · {left}"
            : $"Mullvad paid until {date} · {left}");
    }

    /// <summary>"Oct 27" on the device's local date, with ", 2027" when it is not this year.</summary>
    public static string AccountDate(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var text = local.ToString("MMM d", CultureInfo.InvariantCulture);
        return local.Year == today.Year ? text : $"{text}, {local.Year.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The note at the top of the Add window while Use VPN is off in Settings.</summary>
    public const string VpnOffWarning =
        "The VPN is turned off in Settings, so the sites and everyone sharing will see your home address.";

    // ---------------------------------------------------------------- Search offers a download

    /// <summary>A kind that can be downloaded, and what the offer calls it.</summary>
    public sealed record DownloadKind(string Kind, string Label);

    /// <summary>
    /// The kinds the library search's offer knows, in the order its "Search the sites for … as a"
    /// buttons show them. Music gets a row here when it can be downloaded.
    /// </summary>
    public static IReadOnlyList<DownloadKind> DownloadKinds { get; } = new[]
    {
        new DownloadKind("movie", "Movie"),
        new DownloadKind("show", "TV show"),
    };

    /// <summary>"Movie", "TV show"; null for a kind this app doesn't know (a newer server's).</summary>
    public static string? KindLabel(string? kind) =>
        DownloadKinds.FirstOrDefault(known => known.Kind == kind)?.Label;

    /// <summary>The most TMDB matches the offer lists; past that, "+ Add" is the place to look.</summary>
    public const int SearchOfferMaxResults = 6;

    /// <summary>
    /// Whether searching the library should offer a download instead of a bare "nothing matches":
    /// at least 2 characters typed, the library found nothing, no genre / unwatched / My List
    /// filter narrowed it (then the title may well be there), and this user may download.
    /// </summary>
    public static bool ShowsSearchOffer(string? query, int libraryItemCount, bool otherFilters, MediaDownloadStatus? status) =>
        (query?.Trim().Length ?? 0) >= 2 && libraryItemCount == 0 && !otherFilters && status?.ShowsAddButton == true;

    /// <summary>A kind=any lookup's rows for the offer: kinds this app knows, at most six, in the server's order.</summary>
    public static IReadOnlyList<MediaDownloadTitle> SearchOfferResults(MediaDownloadLookup lookup) =>
        lookup.Results.Where(result => KindLabel(result.Kind) is not null).Take(SearchOfferMaxResults).ToList();

    public const string SearchOfferTitle = "Not in your library";

    public static string SearchOfferLine(string query) => $"Want to download “{query.Trim()}”?";

    public static string SearchOfferNothingOnTmdb(string query) => $"Nothing on TMDB called “{query.Trim()}”.";

    public static string SearchOfferSitesLine(string query) => $"Search the sites for “{query.Trim()}” as a";
}
