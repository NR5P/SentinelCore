using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SentinelCore.Web;

// The Music tab: what a music library is, what the scanner finds in it, and what each person
// does with it (plays, loves, ratings, playlists). Storage is SqliteMusicStore (music.db),
// scanning is MusicScanner, the in-memory catalog every list is answered from is MusicCatalog,
// streaming is MusicTranscoder and the HTTP surface is MusicEndpoints. The wire contract every
// client is built against is core/docs/MUSIC_AND_PODCASTS.md (sections 2-4).

public sealed record MusicLibrary(
    Guid Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastScanStartedAt = null,
    DateTimeOffset? LastScanFinishedAt = null,
    string? LastScanError = null,
    int TrackCount = 0);

// One audio file in the catalog. Mutable because the scanner fills it in over passes (files,
// tags, then the album/artist rules that need the whole library at once) that each touch a
// different part.
public sealed class MusicTrack
{
    public Guid Id { get; set; }
    public Guid LibraryId { get; set; }
    // The file's full path (local, or smb://...) and its path inside the library with forward
    // slashes - the second is what the id is made from, and what folder browsing and the .m3u8
    // export show.
    public string Path { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTimeOffset? FileModifiedAt { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public bool Missing { get; set; }
    // A <file>.lrc beside the track, found by the file walk.
    public string? LyricsSidecar { get; set; }

    // Null until the tags have been read (and again whenever the file changes).
    public DateTimeOffset? TagsReadAt { get; set; }
    public string? TagError { get; set; }

    // What the file's tags (or, failing that, its name and folders) say.
    public string Title { get; set; } = string.Empty;
    public string ArtistText { get; set; } = string.Empty;
    public string? AlbumArtistTag { get; set; }
    public bool CompilationTag { get; set; }
    public string Album { get; set; } = string.Empty;
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = [];
    public string? Composer { get; set; }
    public int? Bpm { get; set; }
    public double? TrackGain { get; set; }
    public double? TrackPeak { get; set; }
    public double? AlbumGain { get; set; }
    public double? AlbumPeak { get; set; }
    public bool HasEmbeddedArt { get; set; }
    public bool HasEmbeddedLyrics { get; set; }
    public string? MusicBrainzReleaseId { get; set; }
    public string? MusicBrainzReleaseGroupId { get; set; }

    // What the audio itself is.
    public double DurationSeconds { get; set; }
    public string Codec { get; set; } = string.Empty;
    public long? Bitrate { get; set; }
    public int? SampleRate { get; set; }
    public int? BitDepth { get; set; }
    public int? Channels { get; set; }

    // Worked out from the whole library by MusicArtists.Resolve: the artists this track is
    // credited to, who the album belongs to, and the ids both lead to.
    public List<string> Artists { get; set; } = [];
    public string AlbumArtist { get; set; } = string.Empty;
    public bool Compilation { get; set; }
    public Guid AlbumId { get; set; }
    public Guid ArtistId { get; set; }

    public string PrimaryArtist => Artists.Count > 0 ? Artists[0] : ArtistText;
    public bool HasLyrics => LyricsSidecar is not null || HasEmbeddedLyrics;
}

public static class MusicLoveKinds
{
    public const string Track = "track";
    public const string Album = "album";
    public const string Artist = "artist";

    public static bool IsValid(string? kind) => kind is Track or Album or Artist;
}

// One listen by one person, reported by a player when a track ended or was skipped (see the
// counting rule in MusicPlayRules). Keyed by the lower-cased username (the empty string when
// login is off), like the Movies tab's plays, so a rebuilt server's new account ids lose nothing.
// Skips live in the same table with Skipped set: they feed the radio and shuffle, not counts.
public sealed record MusicPlay(
    string UserKey,
    Guid TrackId,
    DateTimeOffset PlayedAt,
    double SecondsListened,
    bool Skipped,
    string? DeviceName = null);

// A love (or an un-love, kept so a restore cannot bring back something its owner since took
// back - newest edit wins).
public sealed record MusicLove(string UserKey, string Kind, Guid ItemId, bool Loved, DateTimeOffset UpdatedAt);

// A 1-5 star rating; 0 is "unrated" and is kept for the same reason an un-love is.
public sealed record MusicRating(string UserKey, string Kind, Guid ItemId, int Rating, DateTimeOffset UpdatedAt);

public sealed record MusicPlaylistEntry(Guid EntryId, Guid TrackId, DateTimeOffset AddedAt, string? AddedBy = null);

// A playlist and its tracks in order. A smart playlist (Rules set) has no entries of its own:
// its tracks are worked out fresh from the rules every time it is opened.
public sealed record MusicPlaylist(
    Guid Id,
    string OwnerKey,
    string Name,
    string Description,
    bool Shared,
    bool Collaborative,
    MusicSmartRules? Rules,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MusicPlaylistEntry> Entries)
{
    public bool Smart => Rules is not null;

    public bool CanSee(string userKey) => Shared || OwnerKey == userKey;

    public bool CanEdit(string userKey, bool isAdmin) => isAdmin || OwnerKey == userKey;

    public bool CanEditTracks(string userKey, bool isAdmin) =>
        CanEdit(userKey, isAdmin) || (Collaborative && Shared);
}

// Someone pinned a playlist to the top of their Music Home.
public sealed record MusicPin(string UserKey, Guid PlaylistId, DateTimeOffset PinnedAt);

// What goes in the backup: the libraries (configuration) and what people did, which a rescan
// cannot bring back. The catalog is rebuilt from the files with the same ids, and art and lyrics
// are refetched. Pins ride along with the playlists they point at.
public sealed record MusicBackup(
    IReadOnlyList<MusicLibrary>? Libraries = null,
    IReadOnlyList<MusicPlay>? Plays = null,
    IReadOnlyList<MusicLove>? Loves = null,
    IReadOnlyList<MusicRating>? Ratings = null,
    IReadOnlyList<MusicPlaylist>? Playlists = null,
    IReadOnlyList<MusicPin>? Pins = null);

// A smart playlist's rules (MUSIC_AND_PODCASTS.md section 3). Classes rather than records so a
// client may leave anything out and get the defaults.
public sealed class MusicSmartRules
{
    public string Match { get; set; } = "all";
    public List<MusicSmartRule> Rules { get; set; } = [];
    public string Sort { get; set; } = "random";
    public string Order { get; set; } = "asc";
    public int Limit { get; set; } = 100;
}

public sealed class MusicSmartRule
{
    public string Field { get; set; } = string.Empty;
    public string Op { get; set; } = string.Empty;
    public JsonElement Value { get; set; }
}

public sealed class MusicSettingsDto
{
    public const string SectionName = "Music";

    public bool Enabled { get; set; } = true;
    [Range(0, 168)]
    public int RescanHours { get; set; } = 12;
    public bool FetchArtworkOnline { get; set; } = true;
    public bool FetchLyricsOnline { get; set; } = true;
    [Range(96, 320)]
    public int TranscodeBitrate { get; set; } = 256;
    public bool PauseSpeakersOnAlarm { get; set; } = true;
    public bool AnnounceOnSpeakers { get; set; }
}

public sealed class MusicLibraryInput
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class MusicPlayedInput
{
    public double SecondsListened { get; set; }
    public bool Completed { get; set; }
    public bool Skipped { get; set; }
    public DateTimeOffset? PlayedAt { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class MusicNowPlayingInput
{
    public Guid TrackId { get; set; }
}

public sealed class MusicLoveInput
{
    public bool Loved { get; set; }
}

public sealed class MusicRatingInput
{
    public int Rating { get; set; }
}

public sealed class MusicPlaylistInput
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? Shared { get; set; }
    public bool? Collaborative { get; set; }
    public MusicSmartRules? Rules { get; set; }
    public List<Guid>? TrackIds { get; set; }
}

public sealed class MusicPlaylistTracksInput
{
    public List<Guid> TrackIds { get; set; } = [];
    public int? Position { get; set; }
}

public sealed class MusicPlaylistOrderInput
{
    public List<Guid> EntryIds { get; set; } = [];
}

public sealed class MusicPinInput
{
    public bool Pinned { get; set; }
}

// The rules a reported listen is judged by (MUSIC_AND_PODCASTS.md, "Playing, loving, rating").
public static class MusicPlayRules
{
    public const double MinimumSeconds = 30;
    public const double LongEnoughSeconds = 240;
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);

    public enum Outcome { None, Play, Skip }

    // A play: at least 30 s, and half the track or four minutes. A skip: skipped inside the first
    // 30 s. Anything else (skipped at 40 s of a long track) is neither.
    public static Outcome Judge(double secondsListened, bool skipped, double durationSeconds)
    {
        if (secondsListened >= MinimumSeconds
            && (secondsListened >= LongEnoughSeconds || (durationSeconds > 0 && secondsListened >= durationSeconds * 0.5)))
            return Outcome.Play;
        if (skipped && secondsListened < MinimumSeconds) return Outcome.Skip;
        return Outcome.None;
    }
}

// Ids that stay the same across rescans and restores (MUSIC_AND_PODCASTS.md, top): a track's is
// made from its library and its path inside it, an album's from its album artist and title, an
// artist's from the name. The names are normalized first, so "Fleetwood Mac" and "fleetwood
// mac" are one artist and an accent typed one way or the other does not split an album.
public static class MusicIds
{
    public static Guid Track(Guid libraryId, string relativePath) =>
        MediaNaming.StableId(libraryId, "music:" + relativePath.Replace('\\', '/'));

    public static Guid Album(string albumArtist, string title) => Hash($"album|{MusicText.Key(albumArtist)}|{MusicText.Key(title)}");

    public static Guid Artist(string name) => Hash($"artist|{MusicText.Key(name)}");

    private static Guid Hash(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes("sentinelcore-music|" + text)).AsSpan(0, 16));
}
