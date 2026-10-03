namespace SentinelCore.Web;

// The seams between the Music tab (Music*.cs, music.db), the Podcasts tab (Podcast*.cs,
// podcasts.db) and what the two share (Audio*.cs, audio.db): play where you left off, speakers,
// wake-up alarms, the morning briefing, stats, ListenBrainz and backups. The shared code only
// ever talks to the two tabs through these, so each tab owns its own tables and DTO shapes.
// Contract: core/docs/MUSIC_AND_PODCASTS.md.

// One playable thing resolved for a client: its id, and the exact JSON object the contract
// calls a Track or an Episode, as the tab itself would have answered it for this user.
public sealed record AudioEntry(Guid Id, object Dto);

public static class AudioItemTypes
{
    public const string Track = "track";
    public const string Episode = "episode";
}

// A wake-up alarm's source or a speaker's "play this": playlist, album, artist, mix, shuffle
// (music) or podcast (podcasts). Briefing is put together by the shared code itself.
public static class AudioSourceTypes
{
    public const string Playlist = "playlist";
    public const string Album = "album";
    public const string Artist = "artist";
    public const string Mix = "mix";
    public const string Shuffle = "shuffle";
    public const string Podcast = "podcast";
    public const string Briefing = "briefing";

    public static bool IsMusic(string? type) => type is Playlist or Album or Artist or Mix or Shuffle;
}

public sealed record AudioLibraryInfo(Guid Id, string Name, string Path);

public interface IMusicAudioSource
{
    Task<bool> EnabledAsync(CancellationToken cancellationToken);

    // Tracks in the order asked for; ids that no longer exist are left out.
    Task<IReadOnlyList<AudioEntry>> ResolveTracksAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

    // The queue a wake-up alarm or a speaker plays for a source (AudioSourceTypes.IsMusic).
    // Empty when the source is gone.
    Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken);

    // The "music" object of GET /api/audio/stats for [from, to).
    Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    // For "+ Add" (MusicDownloads): where albums can be filed, and a rescan once one is.
    Task<IReadOnlyList<AudioLibraryInfo>> LibrariesAsync(CancellationToken cancellationToken);
    void RequestScan(Guid libraryId);
    Task<bool> HasAlbumAsync(string artist, string album, CancellationToken cancellationToken);
    // 0..1: how much of the library is FLAC/ALAC/WAV/AIFF, so release ranking can prefer lossless.
    Task<double> LosslessShareAsync(CancellationToken cancellationToken);

    Task<MusicBackup> ExportAsync(CancellationToken cancellationToken);
    // Newest edit wins; nothing in the current data is rolled back.
    Task<int> RestoreAsync(MusicBackup backup, CancellationToken cancellationToken);
}

public interface IPodcastAudioSource
{
    Task<bool> EnabledAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AudioEntry>> ResolveEpisodesAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

    // The newest unplayed episode of each show this user put in the briefing, at most `max`.
    Task<IReadOnlyList<AudioEntry>> BriefingEpisodesAsync(string userKey, int max, CancellationToken cancellationToken);

    // AudioSourceTypes.Podcast: the newest unplayed episode of that show (else the newest).
    Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken);

    // The "podcasts" object of GET /api/audio/stats for [from, to).
    Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<PodcastBackup> ExportAsync(CancellationToken cancellationToken);
    Task<int> RestoreAsync(PodcastBackup backup, CancellationToken cancellationToken);
}

// The Music tab hands every counted play here; AudioListenBrainz submits it for the people who
// connected an account. Fire and forget: it queues and retries on its own.
public interface IListenBrainzSubmitter
{
    void Listened(string userKey, AudioListen listen);
    void PlayingNow(string userKey, AudioListen listen);
}

public sealed record AudioListen(
    string Artist,
    string Title,
    string? Album,
    double? DurationSeconds,
    DateTimeOffset PlayedAt,
    int? TrackNumber = null);
