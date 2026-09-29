using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

// The media library behind the Movies tab: what a library is, what the scanner finds in it, and
// what each person has watched. Storage is SqliteMediaStore (media.db), scanning is MediaScanner,
// playback is MediaPlaybackPlanner + MediaHlsManager, and the HTTP surface is MediaEndpoints.
// The wire contract every client is built against is core/docs/MOVIES.md.

public static class MediaKinds
{
    public const string Movie = "movie";
    public const string Show = "show";
    public const string Episode = "episode";
}

public static class MediaLibraryKinds
{
    public const string Movies = "movies";
    public const string Shows = "shows";

    public static bool IsValid(string? kind) => kind is Movies or Shows;
}

public sealed record MediaLibrary(
    Guid Id,
    string Name,
    string Kind,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastScanStartedAt = null,
    DateTimeOffset? LastScanFinishedAt = null,
    string? LastScanError = null,
    int ItemCount = 0);

public sealed record MediaCastMember(string Name, string? Character);

public sealed record MediaAudioTrack(int Index, string Codec, int Channels, string? Language, string? Title, bool IsDefault);

// `External` is the sidecar file's path when the track is a .srt beside the video rather than a
// stream inside it; `StreamIndex` is ffmpeg's index among the file's subtitle streams otherwise.
public sealed record MediaSubtitleTrack(int Index, string Codec, string? Language, string? Title, bool Forced, bool IsDefault, string? External, int? StreamIndex);

// One row of the catalog: a movie, a show, or one episode of a show. Mutable because the scanner
// fills it in over three passes (files, metadata, probe) that each touch a different part.
public sealed class MediaItem
{
    public Guid Id { get; set; }
    public Guid LibraryId { get; set; }
    public string Kind { get; set; } = MediaKinds.Movie;
    public Guid? ParentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    // The video file for a movie or episode, the folder for a show.
    public string Path { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public DateTimeOffset? FileModifiedAt { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public bool Missing { get; set; }

    // Metadata (TMDB, or whatever the file name gave us).
    public int? TmdbId { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public List<string> Genres { get; set; } = [];
    public double? Rating { get; set; }
    public string? Certification { get; set; }
    public int? RuntimeMinutes { get; set; }
    public string? ReleaseDate { get; set; }
    public List<MediaCastMember> Cast { get; set; } = [];
    public DateTimeOffset? MetadataFetchedAt { get; set; }
    public bool MetadataLocked { get; set; }
    public string? MetadataError { get; set; }

    // Artwork: a local file beside the video wins; otherwise the TMDB path, fetched into the art
    // cache the first time anyone asks for it.
    public string? PosterLocal { get; set; }
    public string? PosterRemote { get; set; }
    public string? BackdropLocal { get; set; }
    public string? BackdropRemote { get; set; }
    public string? ThumbLocal { get; set; }
    public string? ThumbRemote { get; set; }

    // What ffprobe said about the file.
    public double? DurationSeconds { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? VideoProfile { get; set; }
    public string? VideoTag { get; set; }
    public string? PixelFormat { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? Bitrate { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public List<MediaAudioTrack> AudioTracks { get; set; } = [];
    public List<MediaSubtitleTrack> SubtitleTracks { get; set; } = [];
    public DateTimeOffset? ProbedAt { get; set; }
    public string? ProbeError { get; set; }

    public bool IsPlayable => Kind is MediaKinds.Movie or MediaKinds.Episode;
    public bool HasPoster => PosterLocal is not null || PosterRemote is not null;
    public bool HasBackdrop => BackdropLocal is not null || BackdropRemote is not null;
    public bool HasThumb => ThumbLocal is not null || ThumbRemote is not null;
}

// Where one person is in one movie or episode. Keyed by the lower-cased username (the empty
// string when login is off and the household shares one view), for the same reason the Organizer
// is: a rebuilt server recreates accounts with new ids, and this has to survive that.
public sealed record MediaProgress(
    string UserKey,
    Guid ItemId,
    double PositionSeconds,
    double? DurationSeconds,
    bool Watched,
    DateTimeOffset UpdatedAt)
{
    public const double WatchedFraction = 0.92;
    public const double MinimumSavedSeconds = 30;

    public double Percent =>
        Watched ? 100 : DurationSeconds is > 0 ? Math.Clamp(PositionSeconds / DurationSeconds.Value * 100, 0, 100) : 0;
}

// One press of Play by one person. Keyed like MediaProgress. The play history is what "Most
// played" counts.
public sealed record MediaPlay(string UserKey, Guid ItemId, DateTimeOffset PlayedAt)
{
    // Asking for playback again this soon (a seek in an HLS stream, an audio-track change, a
    // dropped connection) is the same sitting, not another play.
    public static readonly TimeSpan SameSitting = TimeSpan.FromHours(4);
}

public sealed record MediaPlayStats(int Count, DateTimeOffset LastPlayedAt);

// A movie or show someone saved to My List. Per person, like progress.
public sealed record MediaWatchlistEntry(string UserKey, Guid ItemId, DateTimeOffset AddedAt);

// What goes in the backup: the libraries (configuration) and everyone's progress (the one thing a
// rescan cannot bring back). The catalog itself is rebuilt from the files.
// NetworkComputers (added without a schema bump - older files simply lack it) are the saved
// sign-ins for libraries on other computers, passwords included like FamilyDrives' SFTP ones.
// Plays and Watchlist were added the same way.
public sealed record MediaBackup(
    IReadOnlyList<MediaLibrary> Libraries,
    IReadOnlyList<MediaProgress> Progress,
    IReadOnlyList<MediaNetworkComputer>? NetworkComputers = null,
    IReadOnlyList<MediaPlay>? Plays = null,
    IReadOnlyList<MediaWatchlistEntry>? Watchlist = null);

public sealed class MediaSettingsDto
{
    public const string SectionName = "Media";

    public bool Enabled { get; set; } = true;
    public string TmdbApiKey { get; set; } = string.Empty;
    [Range(0, 168)]
    public int RescanHours { get; set; } = 12;
}

public sealed class MediaLibraryInput
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = MediaLibraryKinds.Movies;
    public string Path { get; set; } = string.Empty;
}

public sealed class MediaProgressInput
{
    public double PositionSeconds { get; set; }
    public double? DurationSeconds { get; set; }
}

public sealed class MediaWatchedInput
{
    public bool Watched { get; set; }
}

public sealed class MediaListInput
{
    public bool InList { get; set; }
}

public sealed class MediaMatchInput
{
    public int TmdbId { get; set; }
}

public sealed class MediaRefreshInput
{
    public bool Unlock { get; set; }
}
