using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// Movie downloads - "+ Add" on the Movies tab, /api/media/downloads. The contract is
// core/docs/MOVIE_DOWNLOADS.md. The feature is newer than every other Movies route, so every field
// here is nullable (and every list survives an explicit null): a server a version behind or ahead
// must never make the Movies section fail to decode. The [JsonIgnore] helpers are what the views
// read instead of the raw nullables.

/// <summary>GET /api/media/downloads/status - whether "+ Add" belongs on the Movies screen.</summary>
public sealed class MediaDownloadStatus
{
    /// <summary>What an older server (404 on /status) or a failed check means: the feature is off.</summary>
    public static MediaDownloadStatus Off { get; } = new();

    public bool? Enabled { get; init; }
    public bool? CanDownload { get; init; }
    public bool? CanManageSites { get; init; }
    public bool? TmdbConfigured { get; init; }

    /// <summary>Whether a movie library on the server's own disks exists to download into.</summary>
    public bool? MovieLibrary { get; init; }
    public bool? ShowLibrary { get; init; }
    public MediaDownloadServiceStatus? Status { get; init; }

    [JsonIgnore] public bool ShowsAddButton => CanDownload == true;
    [JsonIgnore] public bool ShowsSitesButton => CanManageSites == true;

    /// <summary>False only when the server says so; an older server that doesn't say is assumed to have TMDB.</summary>
    [JsonIgnore] public bool HasTmdb => TmdbConfigured != false;
}

/// <summary>The VPN and services half of the status: what the Downloads screen's VPN line reads.</summary>
public sealed class MediaDownloadServiceStatus
{
    public bool? Enabled { get; init; }

    /// <summary>running | starting | stopped | failed | notInstalled | external</summary>
    public string? Services { get; init; }
    public int? IdleMinutes { get; init; }
    public MediaDownloadVpn? Vpn { get; init; }
}

public sealed class MediaDownloadVpn
{
    /// <summary>connected | connecting | down | off | unknown</summary>
    public string? State { get; init; }
    public bool? Required { get; init; }
    public string? Ip { get; init; }
    public string? Location { get; init; }
    public string? Country { get; init; }
    public string? Provider { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public string? Error { get; init; }
}

// ---------------------------------------------------------------- Finding the title (TMDB)

/// <summary>GET /api/media/downloads/lookup. With no TMDB key the results are empty and TmdbConfigured is false.</summary>
public sealed class MediaDownloadLookup
{
    private List<MediaDownloadTitle> _results = new();

    public bool? TmdbConfigured { get; init; }
    public List<MediaDownloadTitle> Results { get => _results; init => _results = value ?? new(); }
}

/// <summary>One TMDB match for what was typed.</summary>
public sealed class MediaDownloadTitle
{
    public string? Kind { get; init; } // movie | show
    public long? TmdbId { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? PosterUrl { get; init; }
    public bool? InLibrary { get; init; }
    public string? ItemId { get; init; }

    [JsonIgnore] public bool IsShow => Kind == "show";
    [JsonIgnore] public bool IsInLibrary => InLibrary == true;
    [JsonIgnore] public string DisplayTitle => Title ?? "";
}

/// <summary>GET /api/media/downloads/show/{tmdbId}: a show's seasons, for picking episodes.</summary>
public sealed class MediaDownloadShow
{
    private List<MediaDownloadSeason> _seasons = new();

    public long? TmdbId { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? PosterUrl { get; init; }
    public string? ItemId { get; init; }

    /// <summary>In the order to show them; season 0 (specials) is last.</summary>
    public List<MediaDownloadSeason> Seasons { get => _seasons; init => _seasons = value ?? new(); }
}

public sealed class MediaDownloadSeason
{
    private List<MediaDownloadEpisode> _episodes = new();

    public int? Number { get; init; }
    public string? Name { get; init; }
    public int? EpisodeCount { get; init; }
    public int? OwnedCount { get; init; }
    public List<MediaDownloadEpisode> Episodes { get => _episodes; init => _episodes = value ?? new(); }

    [JsonIgnore] public int SeasonNumber => Number ?? 0;
}

public sealed class MediaDownloadEpisode
{
    public int? Number { get; init; }
    public string? Name { get; init; }
    public string? AirDate { get; init; }

    /// <summary>False when it is not out yet. Missing counts as aired - the server only ever withholds.</summary>
    public bool? Aired { get; init; }
    public bool? InLibrary { get; init; }

    [JsonIgnore] public bool IsAired => Aired != false;
    [JsonIgnore] public bool IsInLibrary => InLibrary == true;
}

// ---------------------------------------------------------------- Searching the sites

/// <summary>
/// The body of POST /api/media/downloads/search. <see cref="Season"/> alone is a whole season;
/// with <see cref="Episode"/> it is one episode. Nulls are left out of the body.
/// </summary>
public sealed class MediaDownloadSearchInput
{
    public string Kind { get; init; } = "movie"; // movie | show
    public string Title { get; init; } = "";
    public int? Year { get; init; }
    public long? TmdbId { get; init; }
    public string? PosterPath { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
}

/// <summary>A search of the household's sites, polled every second until it is done or failed.</summary>
public sealed class MediaDownloadSearch
{
    private List<MediaRelease> _results = new();
    private List<MediaRelease> _others = new();

    public string? Id { get; init; }

    /// <summary>connecting | searching | done | failed</summary>
    public string? Phase { get; init; }

    /// <summary>The line to show under the spinner, or the failure.</summary>
    public string? Message { get; init; }
    public string? Error { get; init; }
    public string? Kind { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// <summary>What was asked for, with people sharing it, best first.</summary>
    public List<MediaRelease> Results { get => _results; init => _results = value ?? new(); }

    /// <summary>Everything else: another title, year or episode, or nobody sharing. Shown collapsed.</summary>
    public List<MediaRelease> Others { get => _others; init => _others = value ?? new(); }

    [JsonIgnore] public bool IsDone => Phase == "done";
    [JsonIgnore] public bool IsFailed => Phase == "failed";
    [JsonIgnore] public bool IsFinished => IsDone || IsFailed;
}

/// <summary>One torrent a site offered.</summary>
public sealed class MediaRelease
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Site { get; init; }
    public long? SizeBytes { get; init; }
    public int? Seeders { get; init; }
    public int? Leechers { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Quality { get; init; } // 1080p | 2160p | 720p | 480p
    public string? Codec { get; init; }
    public string? Source { get; init; }
    public bool? Hdr { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public bool? SeasonPack { get; init; }
    public bool? Matches { get; init; }

    /// <summary>Something to know before choosing it ("Filmed in a cinema..."), shown in amber.</summary>
    public string? Warning { get; init; }
}

// ---------------------------------------------------------------- The queue

/// <summary>The body of POST /api/media/downloads. No library means the first local one of the right kind.</summary>
public sealed class MediaDownloadGrabInput
{
    public string SearchId { get; init; } = "";
    public string ReleaseId { get; init; } = "";
    public string? LibraryId { get; init; }
}

public sealed class MediaDownload
{
    public string? Id { get; init; }
    public string? Kind { get; init; } // movie | episode
    public string? Title { get; init; }
    public int? Year { get; init; }
    public long? TmdbId { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// <summary>"S02E05", "Season 2", or null for a film: the line under the title.</summary>
    public string? Subtitle { get; init; }
    public string? PosterUrl { get; init; }
    public string? ReleaseTitle { get; init; }
    public string? Site { get; init; }
    public long? SizeBytes { get; init; }

    /// <summary>queued | downloading | seeding | importing | done | failed | canceled</summary>
    public string? State { get; init; }

    /// <summary>0-1.</summary>
    public double? Progress { get; init; }
    public long? DownloadedBytes { get; init; }
    public long? SpeedBytesPerSecond { get; init; }
    public long? EtaSeconds { get; init; }
    public int? Peers { get; init; }

    /// <summary>On an active download a note ("Waiting for the VPN to connect."), on a failed one the reason.</summary>
    public string? Error { get; init; }
    public string? LibraryId { get; init; }

    /// <summary>Once done: the new movie, or the show, in the library.</summary>
    public string? ItemId { get; init; }
    public string? RequestedBy { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonIgnore] public bool IsActive => MediaDownloadText.IsActiveState(State);
    [JsonIgnore] public bool IsDone => State == "done";
    [JsonIgnore] public bool IsFailed => State == "failed";
    [JsonIgnore] public bool CanOpen => IsDone && !string.IsNullOrEmpty(ItemId);
}

/// <summary>GET /api/media/downloads.</summary>
public sealed class MediaDownloadList
{
    private List<MediaDownload> _active = new();
    private List<MediaDownload> _recent = new();

    public MediaDownloadServiceStatus? Status { get; init; }
    public List<MediaDownload> Active { get => _active; init => _active = value ?? new(); }

    /// <summary>The last 30 finished, newest first.</summary>
    public List<MediaDownload> Recent { get => _recent; init => _recent = value ?? new(); }
}

// ---------------------------------------------------------------- Sites (admin)

/// <summary>One of the sites Prowlarr searches.</summary>
public sealed class MediaDownloadSite
{
    public int? Id { get; init; }
    public string? Name { get; init; }
    public bool? Enabled { get; init; }
    public string? Privacy { get; init; } // public | semiPrivate | private
    public string? Language { get; init; }
    public string? Description { get; init; }
    public string? Definition { get; init; }

    [JsonIgnore] public bool IsEnabled => Enabled == true;
}

/// <summary>A site that can be added, from GET /api/media/downloads/sites/catalog.</summary>
public sealed class MediaDownloadSiteDefinition
{
    private List<MediaDownloadSiteField> _fields = new();

    public string? Definition { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Privacy { get; init; }
    public string? Language { get; init; }

    /// <summary>False: added with one press. True: <see cref="Fields"/> need filling in first.</summary>
    public bool? NeedsSetup { get; init; }
    public List<MediaDownloadSiteField> Fields { get => _fields; init => _fields = value ?? new(); }

    [JsonIgnore] public bool RequiresSetup => NeedsSetup == true;
}

public sealed class MediaDownloadSiteField
{
    private List<MediaDownloadSiteOption> _options = new();

    public string? Name { get; init; }
    public string? Label { get; init; }

    /// <summary>text | password | checkbox | select | number</summary>
    public string? Type { get; init; }
    public string? Help { get; init; }

    /// <summary>The site's default, of whatever JSON type the site uses (text, number, true/false).</summary>
    public JsonElement? Value { get; init; }
    public List<MediaDownloadSiteOption> Options { get => _options; init => _options = value ?? new(); }

    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Name ?? "" : Label!;

    /// <summary>The default as text for a text box or a select, "" when there is none.</summary>
    [JsonIgnore] public string ValueText => MediaDownloadText.JsonText(Value);

    [JsonIgnore] public bool ValueIsTrue => Value is { ValueKind: JsonValueKind.True };
}

public sealed class MediaDownloadSiteOption
{
    public JsonElement? Value { get; init; }
    public string? Label { get; init; }

    [JsonIgnore] public string ValueText => MediaDownloadText.JsonText(Value);
    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? ValueText : Label!;

    public override string ToString() => DisplayLabel;
}

/// <summary>The body of POST /api/media/downloads/sites.</summary>
public sealed class MediaDownloadSiteInput
{
    public string Definition { get; init; } = "";
    public string? Name { get; init; }

    /// <summary>Field name to value: a string, a number, true/false, or null.</summary>
    public Dictionary<string, object?> Fields { get; init; } = new();
}

/// <summary>The body of PUT /api/media/downloads/sites/{id}.</summary>
public sealed class MediaDownloadSiteUpdate
{
    public bool Enabled { get; init; }
}
