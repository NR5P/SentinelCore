using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

// The Podcasts tab: shows followed by RSS, their episodes, and each person's listening. Storage is
// SqlitePodcastStore (podcasts.db), feeds are read by PodcastFeedParser through PodcastFeedFetcher,
// PodcastService owns subscribing and refreshing, PodcastDownloadManager puts episodes on the
// server's disk (PodcastAudioProcessor makes them clearer and shorter on the way), and the HTTP
// surface is PodcastEndpoints. The wire contract every client is built against is
// core/docs/MUSIC_AND_PODCASTS.md, sections 5 and 6.
//
// Time: everything stored against an episode (progress, bookmarks, chapters, transcripts) is kept
// in the publisher's original timing. When trim-silence has made the downloaded file shorter, the
// API converts on the way in and out with the episode's cut map (PodcastTimeline), so a client only
// ever sees the downloaded file's time - and a download finishing or being deleted needs nothing
// moved.

public sealed class PodcastSettingsDto
{
    public const string SectionName = "Podcasts";

    public bool Enabled { get; set; } = true;
    public string DownloadFolder { get; set; } = string.Empty;
    [Range(15, 1440)]
    public int RefreshMinutes { get; set; } = 60;
    public bool AutoDownloadNew { get; set; } = true;
    [Range(0, 1000)]
    public int KeepEpisodes { get; set; } = 10;
    [Range(0, 365)]
    public int DeletePlayedAfterDays { get; set; } = 7;
    [Range(0, 100000)]
    public int MaxStorageGb { get; set; } = 50;
    public string DirectoryCountry { get; set; } = "us";
    public bool TranscriptsEnabled { get; set; }
    public string WhisperCommand { get; set; } = "whisper-cli";
    public string WhisperModel { get; set; } = string.Empty;
    [Range(1, 32)]
    public int WhisperThreads { get; set; } = 4;

    // A method rather than a property: every property here is a setting (SettingsSchemaTests).
    public long MaxStorageBytes() => MaxStorageGb <= 0 ? 0 : MaxStorageGb * 1024L * 1024 * 1024;
}

public static class PodcastEpisodeTypes
{
    public const string Full = "full";
    public const string Trailer = "trailer";
    public const string Bonus = "bonus";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Trailer => Trailer,
        Bonus => Bonus,
        _ => Full
    };
}

public static class PodcastDownloadStates
{
    public const string None = "none";
    public const string Queued = "queued";
    public const string Downloading = "downloading";
    public const string Done = "done";
    public const string Failed = "failed";
}

public sealed record PodcastFunding(string Url, string? Title);

// Household-wide, per show. Mutable with setters because it is also the PUT body.
public sealed class PodcastShowSettings
{
    public bool AutoDownload { get; set; } = true;
    public int KeepEpisodes { get; set; } = 10;
    public bool VoiceBoost { get; set; }
    public bool TrimSilence { get; set; }
    public bool Transcribe { get; set; }

    public PodcastShowSettings Copy() => (PodcastShowSettings)MemberwiseClone();
}

// Per person, per show. Kept when someone unfollows, so following again remembers their speed.
public sealed class PodcastMySettings
{
    public double? PlaybackSpeed { get; set; }
    public int SkipIntroSeconds { get; set; }
    public int SkipOutroSeconds { get; set; }
    public bool Notify { get; set; } = true;
    public bool AutoQueue { get; set; }
    public bool InBriefing { get; set; }
    public string Sort { get; set; } = "newest";

    public PodcastMySettings Copy() => (PodcastMySettings)MemberwiseClone();
}

public sealed class PodcastShow
{
    public Guid Id { get; set; }
    public string FeedUrl { get; set; } = string.Empty;
    public long? ItunesId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? Link { get; set; }
    public List<string> Categories { get; set; } = [];
    public string? Language { get; set; }
    public bool Explicit { get; set; }
    public List<PodcastFunding> Funding { get; set; } = [];
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public string? RefreshError { get; set; }
    public DateTimeOffset? NewestEpisodeAt { get; set; }
    public PodcastShowSettings Settings { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record PodcastEpisodeHeader(Guid Id, Guid PodcastId, DateTimeOffset PublishedAt, bool Archived);

public sealed record PodcastTranscriptLink(string Url, string Type, string? Language);

// What the feed says about one episode. Written only by a refresh; the download side lives in
// PodcastDownload so the two never overwrite each other's work.
public sealed class PodcastEpisode
{
    public Guid Id { get; set; }
    public Guid PodcastId { get; set; }
    public string FeedGuid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DescriptionHtml { get; set; } = string.Empty;
    public string DescriptionText { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public int? Season { get; set; }
    public int? EpisodeNumber { get; set; }
    public string EpisodeType { get; set; } = PodcastEpisodeTypes.Full;
    public bool Explicit { get; set; }
    public string? Link { get; set; }
    public string? ImageUrl { get; set; }
    public string EnclosureUrl { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long? SizeBytes { get; set; }
    public bool Archived { get; set; }
    public List<PodcastTranscriptLink> Transcripts { get; set; } = [];
    public string? ChaptersUrl { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }

    // Not stored with the episode: joined in from PodcastDownloads when read.
    [System.Text.Json.Serialization.JsonIgnore]
    public PodcastDownload? Download { get; set; }

    public PodcastTimeline Timeline => Download?.Timeline ?? PodcastTimeline.Identity;

    public string Summary => PodcastText.Summary(DescriptionText, 300);
}

// One stretch of silence trim-silence took out, in the original episode's seconds.
public sealed record PodcastCut(double Start, double End)
{
    public double Length => End - Start;
}

public sealed class PodcastDownload
{
    public Guid EpisodeId { get; set; }
    public string State { get; set; } = PodcastDownloadStates.None;
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public long? FileBytes { get; set; }
    public double? FileDurationSeconds { get; set; }
    public string? FileMimeType { get; set; }
    public bool Enhanced { get; set; }
    public List<PodcastCut> Cuts { get; set; } = [];
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? DownloadedAt { get; set; }
    public bool TranscribeAfter { get; set; }

    public bool IsDone => State == PodcastDownloadStates.Done && FilePath is not null;

    // The cut map only applies while the shortened file is what audioUrl serves.
    public PodcastTimeline Timeline => IsDone && Cuts.Count > 0 ? new PodcastTimeline(Cuts) : PodcastTimeline.Identity;

    public double TimeSavedSeconds => IsDone ? Cuts.Sum(c => c.Length) : 0;
}

// Converts between the publisher's timing and the downloaded file's, for a file trim-silence
// shortened. Seconds inside a removed stretch land on the join.
public sealed class PodcastTimeline(IReadOnlyList<PodcastCut> cuts)
{
    public static readonly PodcastTimeline Identity = new([]);

    public IReadOnlyList<PodcastCut> Cuts { get; } = cuts;

    public bool IsIdentity => Cuts.Count == 0;

    public double ToFile(double original)
    {
        var removed = 0.0;
        foreach (var cut in Cuts)
        {
            if (original <= cut.Start) break;
            if (original < cut.End) return Math.Max(0, cut.Start - removed);
            removed += cut.Length;
        }
        return Math.Max(0, original - removed);
    }

    public double ToOriginal(double file)
    {
        var removed = 0.0;
        foreach (var cut in Cuts)
        {
            // The cut starts at this point of the file's own time.
            if (file < cut.Start - removed) break;
            removed += cut.Length;
        }
        return Math.Max(0, file + removed);
    }

    public double? ToFile(double? original) => original is { } value ? ToFile(value) : null;
    public double? ToOriginal(double? file) => file is { } value ? ToOriginal(value) : null;
}

public sealed record PodcastSubscription(
    string UserKey,
    Guid PodcastId,
    bool Subscribed,
    DateTimeOffset SubscribedAt,
    PodcastMySettings Settings,
    DateTimeOffset UpdatedAt);

// Where one person is in one episode, in the original timing. Keyed by the lower-cased username
// like Movies' MediaProgress, so it survives a rebuilt server's new account ids.
public sealed record PodcastProgress(
    string UserKey,
    Guid EpisodeId,
    double PositionSeconds,
    double? DurationSeconds,
    bool Played,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PlayedAt = null)
{
    // Within this of the end, or past PlayedFraction, an episode is finished.
    public const double PlayedTailSeconds = 30;
    public const double PlayedFraction = 0.95;
    public const double MinimumSavedSeconds = 10;

    public static bool IsPlayed(double position, double? duration) =>
        duration is > 0 && (position >= duration.Value - PlayedTailSeconds || position >= duration.Value * PlayedFraction);
}

public sealed record PodcastQueueEntry(string UserKey, Guid EpisodeId, double Position, DateTimeOffset AddedAt);

public sealed record PodcastBookmark(
    Guid Id,
    string UserKey,
    Guid EpisodeId,
    double PositionSeconds,
    string Note,
    string? TranscriptSnippet,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PodcastClip(
    Guid Id,
    string UserKey,
    Guid EpisodeId,
    double StartSeconds,
    double DurationSeconds,
    string Title,
    string FilePath,
    DateTimeOffset CreatedAt);

// Listening time per person, per show, per hour (UTC hour start): what Stats and Wrapped read.
public sealed record PodcastListening(
    string UserKey,
    DateTimeOffset HourStart,
    Guid PodcastId,
    double ListenedSeconds,
    double SavedSeconds);

public sealed record PodcastChapter(double StartSeconds, double? EndSeconds, string Title, string? ImageUrl, string? Url);

public sealed record PodcastTranscriptSegment(double Start, double End, string? Speaker, string Text);

public sealed record PodcastTranscript(string Source, string? Language, IReadOnlyList<PodcastTranscriptSegment> Segments);

public sealed record PodcastTranscriptHit(Guid EpisodeId, double StartSeconds, string Snippet);

// What goes in the backup (MUSIC_AND_PODCASTS.md §10): the shows with their settings, everyone's
// subscriptions, progress, queues and bookmarks. Episodes still in a feed come back on the first
// refresh with the same ids (made from the show id and the feed's guid), so only the archived
// ones - which no feed will ever send again - travel. Listening is the Stats history. Downloads,
// art, chapters and transcripts are rebuilt. Every list is optional so later additions (the
// shared audio code's wake-up alarms) can be appended without a schema bump.
public sealed record PodcastBackup(
    IReadOnlyList<PodcastShow>? Shows = null,
    IReadOnlyList<PodcastSubscription>? Subscriptions = null,
    IReadOnlyList<PodcastProgress>? Progress = null,
    IReadOnlyList<PodcastQueueEntry>? Queue = null,
    IReadOnlyList<PodcastBookmark>? Bookmarks = null,
    IReadOnlyList<PodcastEpisode>? ArchivedEpisodes = null,
    IReadOnlyList<PodcastListening>? Listening = null);

// Request bodies.

public sealed class PodcastSubscribeInput
{
    public string? FeedUrl { get; set; }
    public long? ItunesId { get; set; }
}

public sealed class PodcastPlayedAllInput
{
    public bool Played { get; set; }
    public DateTimeOffset? Before { get; set; }
}

public sealed class PodcastQueueAddInput
{
    public Guid EpisodeId { get; set; }
    public string? Position { get; set; }
}

public sealed class PodcastQueueSetInput
{
    public List<Guid> EpisodeIds { get; set; } = [];
}

public sealed class PodcastProgressInput
{
    public double PositionSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public double? Speed { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class PodcastPlayedInput
{
    public bool Played { get; set; }
}

public sealed class PodcastBookmarkInput
{
    public double PositionSeconds { get; set; }
    public string? Note { get; set; }
}

public sealed class PodcastBookmarkNoteInput
{
    public string? Note { get; set; }
}

public sealed class PodcastClipInput
{
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public string? Title { get; set; }
}
