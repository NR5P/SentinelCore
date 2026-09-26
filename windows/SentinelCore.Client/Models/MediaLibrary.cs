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
    public int ItemCount { get; init; }
    public DateTimeOffset? LastScanFinishedAt { get; init; }
    public string? LastScanError { get; init; }
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
