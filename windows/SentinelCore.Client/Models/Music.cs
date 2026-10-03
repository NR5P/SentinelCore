using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// Music - /api/music and /api/audio/enabled. The contract is core/docs/MUSIC_AND_PODCASTS.md
// (§1-§4): camelCase JSON, lower-case string enums, ISO-8601 UTC dates. Everything that can be
// missing or null on the wire is nullable or defaulted here, so a server a little older or newer
// than this app still decodes. Lists are never null after decoding (a null on the wire becomes
// empty), so screens never have to ask.

/// <summary>GET /api/audio/enabled. A 404 from an older server means both off.</summary>
public sealed class AudioEnabled
{
    public bool Music { get; init; }
    public bool Podcasts { get; init; }

    public static AudioEnabled Off { get; } = new();
}

/// <summary>{ "items": [...], "total": N } - every Music list route.</summary>
public sealed class AudioPage<T>
{
    private List<T> _items = new();

    public List<T> Items { get => _items; init => _items = value ?? new(); }
    public int Total { get; init; }
}

public sealed class ReplayGainInfo
{
    public double? TrackGain { get; init; }
    public double? TrackPeak { get; init; }
    public double? AlbumGain { get; init; }
    public double? AlbumPeak { get; init; }
}

public sealed class MusicTrack
{
    private List<string> _artists = new();
    private List<string> _genres = new();

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public List<string> Artists { get => _artists; init => _artists = value ?? new(); }
    public string? ArtistId { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? AlbumId { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int? Year { get; init; }
    public List<string> Genres { get => _genres; init => _genres = value ?? new(); }
    public double DurationSeconds { get; init; }
    public string? Codec { get; init; }
    public long? Bitrate { get; init; }
    public int? SampleRate { get; init; }
    public int? BitDepth { get; init; }
    public int? Channels { get; init; }
    public long? SizeBytes { get; init; }
    public string? Composer { get; init; }
    public double? Bpm { get; init; }
    public ReplayGainInfo? ReplayGain { get; init; }
    public bool HasLyrics { get; init; }
    public string? ArtUrl { get; init; }
    public string? StreamUrl { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
    public bool Loved { get; set; }
    public int Rating { get; set; }
    public int PlayCount { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public string? Path { get; init; }

    /// <summary>Only on a playlist's tracks: tells duplicates apart for remove and reorder.</summary>
    public string? EntryId { get; init; }

    [JsonIgnore] public string ArtistLine => string.IsNullOrWhiteSpace(Artist) ? (AlbumArtist ?? "") : Artist!;

    [JsonIgnore]
    public string Subtitle =>
        string.IsNullOrWhiteSpace(Album) ? ArtistLine : ArtistLine.Length == 0 ? Album! : $"{ArtistLine} · {Album}";

    [JsonIgnore] public string StreamPath => StreamUrl ?? $"/api/music/tracks/{Id}/stream";
}

public sealed class MusicAlbum
{
    private List<string> _genres = new();

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public string? ArtistId { get; init; }
    public int? Year { get; init; }
    public List<string> Genres { get => _genres; init => _genres = value ?? new(); }
    public int TrackCount { get; init; }
    public int DiscCount { get; init; }
    public double DurationSeconds { get; init; }
    public string? ArtUrl { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
    public bool Compilation { get; init; }
    public bool Loved { get; set; }
    public int Rating { get; set; }
    public int PlayCount { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
}

/// <summary>GET /api/music/albums/{id}: the album plus its tracks and neighbours.</summary>
public sealed class MusicAlbumDetail
{
    private List<MusicTrack> _tracks = new();
    private List<MusicAlbum> _more = new();
    private List<string> _formats = new();
    private List<string> _genres = new();

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public string? ArtistId { get; init; }
    public int? Year { get; init; }
    public List<string> Genres { get => _genres; init => _genres = value ?? new(); }
    public int TrackCount { get; init; }
    public int DiscCount { get; init; }
    public double DurationSeconds { get; init; }
    public string? ArtUrl { get; init; }
    public bool Compilation { get; init; }
    public bool Loved { get; set; }
    public int Rating { get; set; }
    public int PlayCount { get; init; }
    public List<MusicTrack> Tracks { get => _tracks; init => _tracks = value ?? new(); }
    public List<MusicAlbum> MoreByArtist { get => _more; init => _more = value ?? new(); }
    public long? TotalBytes { get; init; }
    public List<string> Formats { get => _formats; init => _formats = value ?? new(); }
}

public sealed class MusicArtist
{
    private List<string> _genres = new();

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int AlbumCount { get; init; }
    public int TrackCount { get; init; }
    public List<string> Genres { get => _genres; init => _genres = value ?? new(); }
    public string? ArtUrl { get; init; }
    public bool Loved { get; set; }
    public int PlayCount { get; init; }
}

public sealed class MusicArtistDetail
{
    private List<MusicAlbum> _albums = new();
    private List<MusicAlbum> _appearsOn = new();
    private List<MusicTrack> _top = new();
    private List<MusicArtist> _similar = new();
    private List<string> _genres = new();

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int AlbumCount { get; init; }
    public int TrackCount { get; init; }
    public List<string> Genres { get => _genres; init => _genres = value ?? new(); }
    public string? ArtUrl { get; init; }
    public bool Loved { get; set; }
    public int PlayCount { get; init; }
    public List<MusicAlbum> Albums { get => _albums; init => _albums = value ?? new(); }
    public List<MusicAlbum> AppearsOn { get => _appearsOn; init => _appearsOn = value ?? new(); }
    public List<MusicTrack> TopTracks { get => _top; init => _top = value ?? new(); }
    public List<MusicArtist> Similar { get => _similar; init => _similar = value ?? new(); }
}

public sealed class MusicGenre
{
    private List<string> _art = new();

    public string Name { get; init; } = "";
    public int TrackCount { get; init; }
    public int AlbumCount { get; init; }
    public List<string> ArtUrls { get => _art; init => _art = value ?? new(); }
}

/// <summary>One condition of a smart playlist. Value is a string, a number or a bool on the wire.</summary>
public sealed class SmartRule
{
    public string Field { get; set; } = "genre";
    public string Op { get; set; } = "is";
    public JsonElement Value { get; set; }
}

public sealed class SmartRules
{
    private List<SmartRule> _rules = new();

    public string Match { get; set; } = "all";
    public List<SmartRule> Rules { get => _rules; set => _rules = value ?? new(); }
    public string Sort { get; set; } = "random";
    public string Order { get; set; } = "asc";
    public int Limit { get; set; } = 100;
}

public sealed class MusicPlaylist
{
    private List<string> _art = new();
    private List<MusicTrack> _tracks = new();

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? OwnerName { get; init; }
    public bool Mine { get; init; }
    public bool Shared { get; init; }
    public bool Collaborative { get; init; }
    public bool Smart { get; init; }
    public SmartRules? Rules { get; init; }
    public int TrackCount { get; init; }
    public double DurationSeconds { get; init; }
    public List<string> ArtUrls { get => _art; init => _art = value ?? new(); }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool Pinned { get; set; }

    /// <summary>Only on GET /api/music/playlists/{id}.</summary>
    public List<MusicTrack> Tracks { get => _tracks; init => _tracks = value ?? new(); }

    /// <summary>Whether the signed-in user may add, remove and reorder tracks (owner or collaborative).</summary>
    [JsonIgnore] public bool CanEditTracks => !Smart && (Mine || Collaborative);
}

/// <summary>The body of POST and PUT /api/music/playlists.</summary>
public sealed class MusicPlaylistInput
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool Shared { get; init; }
    public bool Collaborative { get; init; }
    public SmartRules? Rules { get; init; }
    public List<string>? TrackIds { get; init; }
}

public sealed class MusicMix
{
    private List<string> _art = new();

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<string> ArtUrls { get => _art; init => _art = value ?? new(); }
    public string? SeedType { get; init; }
    public string? SeedId { get; init; }
}

public sealed class MusicRecentItem
{
    public string Kind { get; init; } = ""; // album | playlist | artist | mix
    public MusicAlbum? Album { get; init; }
    public MusicPlaylist? Playlist { get; init; }
    public MusicArtist? Artist { get; init; }
    public MusicMix? Mix { get; init; }
}

public sealed class MusicOnThisDay
{
    private List<MusicTrack> _tracks = new();

    public string Label { get; init; } = "";
    public List<MusicTrack> Tracks { get => _tracks; init => _tracks = value ?? new(); }
}

public sealed class MusicDecade
{
    public int Decade { get; init; }
    public string Label { get; init; } = "";
    public int TrackCount { get; init; }
}

public sealed class MusicHome
{
    private List<MusicPlaylist> _pinned = new();
    private List<MusicRecentItem> _recent = new();
    private List<MusicAlbum> _added = new();
    private List<MusicAlbum> _heavy = new();
    private List<MusicMix> _mixes = new();
    private List<MusicTrack> _forgotten = new();
    private List<MusicAlbum> _discover = new();
    private List<MusicTrack> _loved = new();
    private List<MusicDecade> _decades = new();

    public List<MusicPlaylist> Pinned { get => _pinned; init => _pinned = value ?? new(); }
    public List<MusicRecentItem> RecentlyPlayed { get => _recent; init => _recent = value ?? new(); }
    public List<MusicAlbum> RecentlyAdded { get => _added; init => _added = value ?? new(); }
    public List<MusicAlbum> HeavyRotation { get => _heavy; init => _heavy = value ?? new(); }
    public List<MusicMix> Mixes { get => _mixes; init => _mixes = value ?? new(); }
    public List<MusicTrack> ForgottenFavorites { get => _forgotten; init => _forgotten = value ?? new(); }
    public MusicOnThisDay? OnThisDay { get; init; }
    public List<MusicAlbum> Discover { get => _discover; init => _discover = value ?? new(); }
    public List<MusicTrack> LovedTracks { get => _loved; init => _loved = value ?? new(); }
    public List<MusicDecade> Decades { get => _decades; init => _decades = value ?? new(); }
    public int TrackCount { get; init; }
    public int AlbumCount { get; init; }
    public int ArtistCount { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        Pinned.Count == 0 && RecentlyPlayed.Count == 0 && RecentlyAdded.Count == 0 && HeavyRotation.Count == 0 &&
        Mixes.Count == 0 && ForgottenFavorites.Count == 0 && (OnThisDay?.Tracks.Count ?? 0) == 0 &&
        Discover.Count == 0 && LovedTracks.Count == 0 && Decades.Count == 0;
}

public sealed class MusicSearchResult
{
    private List<MusicArtist> _artists = new();
    private List<MusicAlbum> _albums = new();
    private List<MusicTrack> _tracks = new();
    private List<MusicPlaylist> _playlists = new();
    private List<string> _genres = new();

    public List<MusicArtist> Artists { get => _artists; init => _artists = value ?? new(); }
    public List<MusicAlbum> Albums { get => _albums; init => _albums = value ?? new(); }
    public List<MusicTrack> Tracks { get => _tracks; init => _tracks = value ?? new(); }
    public List<MusicPlaylist> Playlists { get => _playlists; init => _playlists = value ?? new(); }
    public List<string> Genres { get => _genres; init => _genres = value ?? new(); }

    [JsonIgnore]
    public bool IsEmpty => Artists.Count == 0 && Albums.Count == 0 && Tracks.Count == 0 && Playlists.Count == 0 && Genres.Count == 0;
}

public sealed class MusicFolderEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public int TrackCount { get; init; }
}

public sealed class MusicFolderListing
{
    private List<MusicFolderEntry> _folders = new();
    private List<MusicTrack> _tracks = new();

    public string? LibraryId { get; init; }
    public string Path { get; init; } = "";
    public string? Parent { get; init; }
    public List<MusicFolderEntry> Folders { get => _folders; init => _folders = value ?? new(); }
    public List<MusicTrack> Tracks { get => _tracks; init => _tracks = value ?? new(); }
}

public sealed class LyricsLine
{
    public double? Time { get; init; }
    public string Text { get; init; } = "";
}

public sealed class MusicLyrics
{
    private List<LyricsLine> _lines = new();

    public bool Synced { get; init; }
    public List<LyricsLine> Lines { get => _lines; init => _lines = value ?? new(); }
    public string? Plain { get; init; }
    public string? Source { get; init; }
}

public sealed class MusicMixResult
{
    private List<MusicTrack> _tracks = new();

    public string Name { get; init; } = "";
    public List<MusicTrack> Tracks { get => _tracks; init => _tracks = value ?? new(); }
}

// ---------------------------------------------------------------- Libraries and status (§2)

public sealed class MusicLibrary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool Network { get; init; }
    public int TrackCount { get; init; }
    public DateTimeOffset? LastScanFinishedAt { get; init; }
    public string? LastScanError { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed class MusicLibraryInput
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}

public sealed class MusicScanState
{
    public string Phase { get; init; } = ""; // files | tags | artwork | lyrics
    public string? LibraryName { get; init; }
    public int Processed { get; init; }
    public int Total { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}

public sealed class MusicStatus
{
    private List<MusicLibrary> _libraries = new();

    public bool Enabled { get; init; }
    public bool FfmpegAvailable { get; init; }
    public bool Scanning { get; init; }
    public MusicScanState? Scan { get; init; }
    public List<MusicLibrary> Libraries { get => _libraries; init => _libraries = value ?? new(); }
    public int TrackCount { get; init; }
    public int AlbumCount { get; init; }
    public int ArtistCount { get; init; }
    public double TotalDurationSeconds { get; init; }
    public long TotalBytes { get; init; }
    public DateTimeOffset? LastScanFinishedAt { get; init; }
    public string? LastScanError { get; init; }
}

// ---------------------------------------------------------------- Getting music (+ Add)

/// <summary>GET /api/music/downloads/status. Answers even when downloads are off.</summary>
public sealed class MusicDownloadStatus
{
    public bool Enabled { get; init; }
    public bool CanDownload { get; init; }

    /// <summary>False when there is no music library on the server's own disk to file albums into.</summary>
    public bool MusicLibrary { get; init; }
    public MediaDownloadServiceStatus? Status { get; init; }

    public static MusicDownloadStatus Off { get; } = new();
}

/// <summary>GET /api/music/downloads: the album downloads only, same shape as Movies' list.</summary>
public sealed class MusicDownloadList
{
    private List<MediaDownload> _active = new();
    private List<MediaDownload> _recent = new();

    public MediaDownloadServiceStatus? Status { get; init; }
    public List<MediaDownload> Active { get => _active; init => _active = value ?? new(); }
    public List<MediaDownload> Recent { get => _recent; init => _recent = value ?? new(); }
}

public sealed class MusicReleaseGroup
{
    public string? Mbid { get; init; }
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public int? Year { get; init; }
    public string? Type { get; init; }
    public int? TrackCount { get; init; }
    public string? ArtUrl { get; init; }
    public bool InLibrary { get; init; }
}

public sealed class MusicRelease
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Site { get; init; }
    public long SizeBytes { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Format { get; init; }
    public bool Lossless { get; init; }
    public bool Matches { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// A background release search (202 from POST, then polled every 2 s at
/// GET /api/music/downloads/search/{id} until phase is done or failed).
/// </summary>
public sealed class MusicReleaseSearch
{
    private List<MusicRelease> _results = new();
    private List<MusicRelease> _others = new();

    public string Id { get; init; } = "";
    public string Phase { get; init; } = ""; // connecting | searching | done | failed
    public string? Message { get; init; }
    public string? Error { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public int? Year { get; init; }
    public List<MusicRelease> Results { get => _results; init => _results = value ?? new(); }
    public List<MusicRelease> Others { get => _others; init => _others = value ?? new(); }

    [JsonIgnore] public bool IsFinished => Phase is "done" or "failed";
}

public sealed class MusicReleaseSearchInput
{
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public int? Year { get; init; }
    public string? Mbid { get; init; }
    public string? ArtUrl { get; init; }
}

public sealed class MusicGrabInput
{
    public string SearchId { get; init; } = "";
    public string ReleaseId { get; init; } = "";
    public string? LibraryId { get; init; }
}

// ---------------------------------------------------------------- Small bodies

public sealed class MusicPlayedInput
{
    public double SecondsListened { get; init; }
    public bool Completed { get; init; }
    public bool Skipped { get; init; }
    public DateTimeOffset? PlayedAt { get; init; }
    public string? DeviceName { get; init; }
}

public sealed class LovedBody
{
    public bool Loved { get; init; }
}

public sealed class RatingBody
{
    public int Rating { get; init; }
}

public sealed class PinnedBody
{
    public bool Pinned { get; init; }
}
