using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// The Movies & TV library - /api/media. The contract is core/docs/MOVIES.md: camelCase JSON,
// string enums, ISO-8601 UTC dates. Everything optional on the wire is nullable here so a summary
// of a movie (no show fields) and a summary of an episode (no show-count fields) decode from the
// same shape.

public sealed class MediaEnabled
{
    public bool Enabled { get; init; }
}

public sealed class MediaLibrary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = ""; // "movies" | "shows"
    public string Path { get; init; } = "";

    /// <summary>True when <see cref="Path"/> is a shared folder on another computer (smb://…).</summary>
    public bool Network { get; init; }
    public int ItemCount { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastScanStartedAt { get; init; }
    public DateTimeOffset? LastScanFinishedAt { get; init; }
    public string? LastScanError { get; init; }

    public bool IsShows => Kind == "shows";
}

/// <summary>The body of POST and PUT /api/media/libraries.</summary>
public sealed class MediaLibraryInput
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "movies"; // movies | shows
    public string Path { get; init; } = "";
}

// ---------------------------------------------------------------- Folder picker (admin)

/// <summary>One folder in a <see cref="MediaBrowseListing"/>.</summary>
public sealed class MediaBrowseFolder
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";

    /// <summary>Videos directly inside this folder. Null for network folders (a round trip each).</summary>
    public int? VideoCount { get; init; }
}

/// <summary>
/// GET /api/media/browse. Always a 200: a folder that cannot be opened carries
/// <see cref="Error"/>, and <see cref="NeedsLogin"/> when signing in to <see cref="Host"/> would fix it.
/// </summary>
public sealed class MediaBrowseListing
{
    public string Path { get; init; } = "";

    /// <summary>Where "up" goes. "" is the starting page; null only on the starting page itself.</summary>
    public string? Parent { get; init; }
    public string Kind { get; init; } = ""; // roots | local | computer | network
    public string? Host { get; init; }
    public List<MediaBrowseFolder> Folders { get; init; } = new();

    /// <summary>Only on the starting page: the network computers already signed in to.</summary>
    public List<MediaNetworkComputer> Computers { get; init; } = new();

    /// <summary>Videos directly in this folder. May be null.</summary>
    public int? VideoCount { get; init; }
    public string? Hint { get; init; }
    public string? Error { get; init; }
    public bool NeedsLogin { get; init; }

    public bool IsRoots => Kind == "roots";

    /// <summary>A computer itself lists its shared folders but cannot be a library.</summary>
    public bool IsComputer => Kind == "computer";

    public bool CanGoUp => Parent is not null && !IsRoots;

    /// <summary>Whether "Use this folder" makes sense here.</summary>
    public bool CanPick => Path.Length > 0 && !IsRoots && !IsComputer && string.IsNullOrEmpty(Error);
}

/// <summary>A computer whose shared folders the server has a saved sign-in for. Never a password.</summary>
public sealed class MediaNetworkComputer
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public string Username { get; init; } = "";
    public string Domain { get; init; } = "";
    public bool HasPassword { get; init; }

    /// <summary>smb://host - browsing this lists the computer's shared folders.</summary>
    public string Path { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public string SignInSummary =>
        Username.Length == 0
            ? "Guest"
            : Domain.Length > 0 ? $"{Domain}\\{Username}" : Username;
}

/// <summary>The body of POST and PUT /api/media/network. A blank password on PUT keeps the saved one.</summary>
public sealed class MediaNetworkComputerInput
{
    public string Host { get; init; } = "";
    public string? Name { get; init; }
    public string Username { get; init; } = "";
    public string? Password { get; init; }
    public string? Domain { get; init; }
}

public sealed class MediaNetworkShare
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}

/// <summary>What a successful sign-in answers: the saved computer and the shares it lists.</summary>
public sealed class MediaNetworkSignInResult
{
    public MediaNetworkComputer Computer { get; init; } = new();
    public List<MediaNetworkShare> Shares { get; init; } = new();
}

/// <summary>
/// A computer found by GET /api/media/network/discover (mDNS _smb._tcp): Macs, NASes, Samba.
/// Windows PCs do not advertise themselves this way.
/// </summary>
public sealed class MediaDiscoveredComputer
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public string? Address { get; init; }
    public string Path { get; init; } = "";

    /// <summary>The saved computer this is, when there is one.</summary>
    public string? SavedId { get; init; }
}

public sealed class MediaScanState
{
    public string Phase { get; init; } = ""; // idle | files | metadata | probing
    public string? LibraryName { get; init; }
    public int Processed { get; init; }
    public int Total { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}

public sealed class MediaStatus
{
    public bool Enabled { get; init; }
    public bool FfmpegAvailable { get; init; }
    public bool TmdbConfigured { get; init; }
    public bool Scanning { get; init; }
    public MediaScanState? Scan { get; init; }
    public List<MediaLibrary> Libraries { get; init; } = new();
    public int MovieCount { get; init; }
    public int ShowCount { get; init; }
    public int EpisodeCount { get; init; }
    public DateTimeOffset? LastScanFinishedAt { get; init; }
}

// How far the signed-in user got. Null on an item they never played.
public sealed class MediaProgress
{
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double Percent { get; init; } // 0-100
    public bool Watched { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

// A movie, a show or an episode as the browsing routes return it. Not sealed: ItemDetail is this
// plus the long-form fields, and the two are one JSON shape on the wire.
public class MediaItemSummary
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = ""; // movie | show | episode
    public string? LibraryId { get; init; }
    public string Title { get; init; } = "";
    public string? SortTitle { get; init; }
    public int? Year { get; init; }
    public List<string> Genres { get; init; } = new();
    public double? Rating { get; init; }
    public string? Certification { get; init; }
    public int? RuntimeMinutes { get; init; }
    public double? DurationSeconds { get; init; }

    // Null when there is no artwork - render a text card. Episodes carry the still in ThumbUrl
    // and their show's poster in PosterUrl.
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public string? ThumbUrl { get; init; }

    public DateTimeOffset? AddedAt { get; init; }
    public MediaProgress? Progress { get; init; }

    // Episodes only.
    public string? ShowId { get; init; }
    public string? ShowTitle { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string? AirDate { get; init; }

    // Shows only.
    public int? SeasonCount { get; init; }
    public int? EpisodeCount { get; init; }
    public int? WatchedEpisodeCount { get; init; }
    public string? NextEpisodeId { get; init; }

    // How many times anyone in the household pressed Play on it (a show adds up its episodes), and
    // whether the signed-in user saved it to My List (never on episodes). Both are missing on
    // servers older than those features, which is 0 and false.
    public int PlayCount { get; init; }
    public bool InList { get; init; }

    [JsonIgnore] public bool IsMovie => Kind == "movie";
    [JsonIgnore] public bool IsShow => Kind == "show";
    [JsonIgnore] public bool IsEpisode => Kind == "episode";

    [JsonIgnore] public bool IsWatched => Progress?.Watched == true;

    // "S1 · E3" for an episode, empty for anything else.
    [JsonIgnore]
    public string EpisodeCode =>
        IsEpisode && SeasonNumber is { } season && EpisodeNumber is { } episode ? $"S{season} · E{episode}" : "";

    // "S1 · E3 · The National Anthem" - what a shelf card says under an episode.
    [JsonIgnore]
    public string EpisodeLabel => EpisodeCode.Length > 0 ? $"{EpisodeCode} · {Title}" : Title;

    // The number left to watch on a show, or null when the counts are not known.
    [JsonIgnore]
    public int? UnwatchedEpisodeCount =>
        EpisodeCount is { } total && WatchedEpisodeCount is { } watched ? Math.Max(0, total - watched) : null;
}

public sealed class MediaCastMember
{
    public string Name { get; init; } = "";
    public string? Character { get; init; }
}

public sealed class MediaFileInfo
{
    public string? Container { get; init; }
    public string? VideoCodec { get; init; }
    public string? VideoProfile { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? AudioCodec { get; init; }
    public int? AudioChannels { get; init; }
    public long? SizeBytes { get; init; }
    public long? Bitrate { get; init; }

    // Admin only; null for everyone else.
    public string? Path { get; init; }
}

public sealed class MediaAudioTrack
{
    public int Index { get; init; }
    public string? Codec { get; init; }
    public int? Channels { get; init; }
    public string? Language { get; init; }
    public string? Label { get; init; }
    public bool IsDefault { get; init; }
}

// Text subtitles only - bitmap ones are never offered. The same shape comes back from both the
// item detail and the playback answer.
public sealed class MediaSubtitle
{
    public int Index { get; init; }
    public string? Language { get; init; }
    public string? Label { get; init; }
    public string Url { get; init; } = "";
    public bool Forced { get; init; }
    public bool External { get; init; }
}

public sealed class MediaSeason
{
    public int Number { get; init; }
    public string? Name { get; init; }
    public int EpisodeCount { get; init; }
    public int WatchedCount { get; init; }
    public string? PosterUrl { get; init; }
    public List<MediaItemSummary> Episodes { get; init; } = new();

    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Name) ? $"Season {Number}" : Name;
}

public sealed class MediaItemDetail : MediaItemSummary
{
    public string? Overview { get; init; }
    public string? Tagline { get; init; }
    public string? ReleaseDate { get; init; }
    public List<MediaCastMember> Cast { get; init; } = new();
    public long? TmdbId { get; init; }
    public string? TmdbUrl { get; init; }

    // Null for shows.
    public MediaFileInfo? File { get; init; }
    public List<MediaAudioTrack>? AudioTracks { get; init; }
    public List<MediaSubtitle>? Subtitles { get; init; }

    // Null for movies and episodes.
    public List<MediaSeason>? Seasons { get; init; }

    /// <summary>
    /// Whether the signed-in user may delete this title: admins, and only movies on the server's
    /// own disks. Missing from older servers, which reads as false.
    /// </summary>
    public bool CanDelete { get; init; }
}

public sealed class MediaHome
{
    public List<MediaItemSummary> ContinueWatching { get; init; } = new();
    public List<MediaItemSummary> RecentMovies { get; init; } = new();
    public List<MediaItemSummary> RecentShows { get; init; } = new();
    public List<MediaItemSummary> RecentEpisodes { get; init; } = new();
    public MediaItemSummary? Featured { get; init; }
    public int MovieCount { get; init; }
    public int ShowCount { get; init; }
    public List<MediaLibrary> Libraries { get; init; } = new();

    // The discovery rows. All five are missing from older servers; a null is treated the same, so
    // the view never has to ask.
    private List<MediaItemSummary> _myList = new();
    private List<MediaItemSummary> _mostPlayed = new();
    private List<MediaItemSummary> _recentlyWatched = new();
    private List<MediaItemSummary> _topRated = new();
    private List<MediaGenreShelf> _genreShelves = new();

    public List<MediaItemSummary> MyList { get => _myList; init => _myList = value ?? new(); }
    public List<MediaItemSummary> MostPlayed { get => _mostPlayed; init => _mostPlayed = value ?? new(); }
    public List<MediaItemSummary> RecentlyWatched { get => _recentlyWatched; init => _recentlyWatched = value ?? new(); } // "Watch again"
    public List<MediaItemSummary> TopRated { get => _topRated; init => _topRated = value ?? new(); }
    public List<MediaGenreShelf> GenreShelves { get => _genreShelves; init => _genreShelves = value ?? new(); }
}

// One of Home's genre rows: a different pick of genres each day.
public sealed class MediaGenreShelf
{
    private List<MediaItemSummary> _items = new();

    public string Genre { get; init; } = "";
    public List<MediaItemSummary> Items { get => _items; init => _items = value ?? new(); }
}

public sealed class MediaItemsPage
{
    public List<MediaItemSummary> Items { get; init; } = new();
    public int Total { get; init; }
    public List<string> Genres { get; init; } = new();
}

// What /playback hands back: where the bytes are, how to play them, and where the clock starts.
public sealed class MediaPlayback
{
    public string ItemId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public double DurationSeconds { get; init; }

    // For HLS the playlist's clock starts here: the player sees 0 where the movie is at
    // StartSeconds. For direct play the player seeks itself here.
    public double StartSeconds { get; init; }
    public double ResumeSeconds { get; init; }

    public string Mode { get; init; } = ""; // direct | hls
    public string Url { get; init; } = "";
    public string? MimeType { get; init; }
    public string? Video { get; init; } // copy | h264
    public string? Audio { get; init; } // copy | aac
    public List<MediaSubtitle> Subtitles { get; init; } = new();
    public string? NextEpisodeId { get; init; }
    public string? NextEpisodeTitle { get; init; }
    public string? BackdropUrl { get; init; }
    public string? PosterUrl { get; init; }

    [JsonIgnore] public bool IsHls => Mode == "hls";
    [JsonIgnore] public bool HasNextEpisode => !string.IsNullOrEmpty(NextEpisodeId);
}

// POST /api/media/items/{id}/progress
public sealed class MediaProgressInput
{
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
}

// POST /api/media/items/{id}/watched
public sealed class MediaWatchedInput
{
    public bool Watched { get; init; }
}

// POST /api/media/items/{id}/list
public sealed class MediaListInput
{
    public bool InList { get; init; }
}

// What the list route answers. ItemId is the show when an episode's id was posted.
public sealed class MediaListResult
{
    public string ItemId { get; init; } = "";
    public bool InList { get; init; }
}
