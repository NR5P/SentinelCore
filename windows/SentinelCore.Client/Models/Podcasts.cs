using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// Podcasts - /api/podcasts. The contract is core/docs/MUSIC_AND_PODCASTS.md §5-§6. Same rules as
// Music.cs: nullable where the wire may omit, lists never null after decoding.

/// <summary>Household-wide show settings; changing them is admin-only.</summary>
public sealed class PodcastSettings
{
    public bool AutoDownload { get; set; } = true;
    public int KeepEpisodes { get; set; } = 10;
    public bool VoiceBoost { get; set; }
    public bool TrimSilence { get; set; }
    public bool Transcribe { get; set; }
}

/// <summary>Per-person show settings.</summary>
public sealed class PodcastMySettings
{
    /// <summary>0.5-3.0; null means the app's default.</summary>
    public double? PlaybackSpeed { get; set; }
    public int SkipIntroSeconds { get; set; }
    public int SkipOutroSeconds { get; set; }
    public bool Notify { get; set; } = true;
    public bool AutoQueue { get; set; }
    public bool InBriefing { get; set; }
    public string Sort { get; set; } = "newest";
}

public sealed class PodcastFunding
{
    public string Url { get; init; } = "";
    public string? Title { get; init; }
}

public sealed class Podcast
{
    private List<string> _categories = new();
    private List<PodcastFunding> _funding = new();

    public string? Id { get; init; }
    public string FeedUrl { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? ArtUrl { get; init; }
    public string? Link { get; init; }
    public List<string> Categories { get => _categories; init => _categories = value ?? new(); }
    public string? Language { get; init; }
    public bool Explicit { get; init; }
    public int EpisodeCount { get; init; }
    public int UnplayedCount { get; init; }
    public DateTimeOffset? NewestEpisodeAt { get; init; }
    public DateTimeOffset? LastRefreshedAt { get; init; }
    public string? RefreshError { get; init; }
    public bool Subscribed { get; set; }
    public int SubscriberCount { get; init; }
    public List<PodcastFunding> Funding { get => _funding; init => _funding = value ?? new(); }
    public PodcastSettings? Settings { get; init; }
    public PodcastMySettings? MySettings { get; init; }
}

public sealed class EpisodeDownloadState
{
    public string State { get; init; } = "none"; // none | queued | downloading | done | failed
    public double Progress { get; init; }
    public string? Error { get; init; }
    public bool Enhanced { get; init; }
    public double TimeSavedSeconds { get; init; }

    [JsonIgnore] public bool IsDone => State == "done";
    [JsonIgnore] public bool IsActive => State is "queued" or "downloading";
}

public sealed class EpisodeProgress
{
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double Percent { get; init; }
    public bool Played { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public class PodcastEpisode
{
    public string Id { get; init; } = "";
    public string? PodcastId { get; init; }
    public string? PodcastTitle { get; init; }
    public string Title { get; init; } = "";
    public string? Summary { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public double DurationSeconds { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? EpisodeType { get; init; }
    public bool Explicit { get; init; }
    public string? Link { get; init; }
    public string? ArtUrl { get; init; }
    public string? AudioUrl { get; init; }
    public string? MimeType { get; init; }
    public long? SizeBytes { get; init; }
    public EpisodeDownloadState? Download { get; init; }
    public bool Archived { get; init; }
    public bool HasChapters { get; init; }
    public bool HasTranscript { get; init; }
    public string? TranscriptSource { get; init; }
    public EpisodeProgress? Progress { get; init; }
    public bool InQueue { get; init; }
    public int BookmarkCount { get; init; }

    [JsonIgnore] public string AudioPath => AudioUrl ?? $"/api/podcasts/episodes/{Id}/audio";
    [JsonIgnore] public bool IsPlayed => Progress?.Played == true;
    [JsonIgnore] public bool IsInProgress => Progress is { Played: false, PositionSeconds: > 0 };

    /// <summary>Where to start: the saved spot for one in progress, else the top.</summary>
    [JsonIgnore] public double ResumeSeconds => IsInProgress ? Progress!.PositionSeconds : 0;

    /// <summary>Time left, counting what has already been heard.</summary>
    [JsonIgnore]
    public double RemainingSeconds =>
        Math.Max(0, (Progress?.DurationSeconds is > 0 ? Progress.DurationSeconds : DurationSeconds) - ResumeSeconds);
}

/// <summary>GET /api/podcasts/episodes/{id}: the episode plus its show notes.</summary>
public sealed class PodcastEpisodeDetail : PodcastEpisode
{
    public string? Description { get; init; }
    public string? DescriptionText { get; init; }
}

public sealed class PodcastEpisodePage
{
    private List<PodcastEpisode> _items = new();
    private List<int> _seasons = new();

    public List<PodcastEpisode> Items { get => _items; init => _items = value ?? new(); }
    public int Total { get; init; }
    public List<int> Seasons { get => _seasons; init => _seasons = value ?? new(); }
}

public sealed class DirectoryShow
{
    public long? ItunesId { get; init; }
    public string Title { get; init; } = "";
    public string? Author { get; init; }
    public string? ArtUrl { get; init; }
    public string? FeedUrl { get; init; }
    public string? Genre { get; init; }
    public int? EpisodeCount { get; init; }
    public string? PodcastId { get; init; }
    public bool Subscribed { get; set; }
}

public sealed class DirectoryGenre
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
}

public sealed class PodcastPreview
{
    private List<PodcastEpisode> _episodes = new();

    public Podcast Podcast { get; init; } = new();
    public List<PodcastEpisode> Episodes { get => _episodes; init => _episodes = value ?? new(); }
}

public sealed class PodcastBookmark
{
    public string Id { get; init; } = "";
    public string EpisodeId { get; init; } = "";
    public string? EpisodeTitle { get; init; }
    public string? PodcastId { get; init; }
    public string? PodcastTitle { get; init; }
    public double PositionSeconds { get; init; }
    public string? Note { get; init; }
    public string? TranscriptSnippet { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? ArtUrl { get; init; }
}

public sealed class PodcastHome
{
    private List<PodcastEpisode> _upNext = new();
    private List<PodcastEpisode> _continue = new();
    private List<PodcastEpisode> _new = new();
    private List<PodcastEpisode> _short = new();
    private List<PodcastEpisode> _downloaded = new();
    private List<Podcast> _subscriptions = new();
    private List<PodcastBookmark> _bookmarks = new();

    public List<PodcastEpisode> UpNext { get => _upNext; init => _upNext = value ?? new(); }
    public List<PodcastEpisode> ContinueListening { get => _continue; init => _continue = value ?? new(); }
    public List<PodcastEpisode> NewEpisodes { get => _new; init => _new = value ?? new(); }
    public List<PodcastEpisode> ShortOnes { get => _short; init => _short = value ?? new(); }
    public List<PodcastEpisode> Downloaded { get => _downloaded; init => _downloaded = value ?? new(); }
    public List<Podcast> Subscriptions { get => _subscriptions; init => _subscriptions = value ?? new(); }
    public List<PodcastBookmark> Bookmarks { get => _bookmarks; init => _bookmarks = value ?? new(); }
    public double TimeSavedSeconds { get; init; }
}

public sealed class PodcastChapter
{
    public double StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public string Title { get; init; } = "";
    public string? ImageUrl { get; init; }
    public string? Url { get; init; }
}

public sealed class TranscriptSegment
{
    public double Start { get; init; }
    public double End { get; init; }
    public string? Speaker { get; init; }
    public string Text { get; init; } = "";
}

public sealed class PodcastTranscript
{
    private List<TranscriptSegment> _segments = new();

    public string? Source { get; init; }
    public string? Language { get; init; }
    public List<TranscriptSegment> Segments { get => _segments; init => _segments = value ?? new(); }
}

public sealed class TranscriptSearchHit
{
    public string EpisodeId { get; init; } = "";
    public string? EpisodeTitle { get; init; }
    public string? PodcastId { get; init; }
    public string? PodcastTitle { get; init; }
    public double StartSeconds { get; init; }
    public string? Snippet { get; init; }
}

public sealed class PodcastClip
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public double DurationSeconds { get; init; }
    public string? Title { get; init; }
    public string? EpisodeId { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed class PodcastFill
{
    private List<PodcastEpisode> _episodes = new();

    public List<PodcastEpisode> Episodes { get => _episodes; init => _episodes = value ?? new(); }
    public double TotalSeconds { get; init; }
}

public sealed class OpmlImportFailure
{
    public string FeedUrl { get; init; } = "";
    public string? Error { get; init; }
}

public sealed class OpmlImportResult
{
    private List<OpmlImportFailure> _failed = new();

    public int Added { get; init; }
    public int Existing { get; init; }
    public List<OpmlImportFailure> Failed { get => _failed; init => _failed = value ?? new(); }
}

public sealed class PodcastTranscriptsStatus
{
    public bool Enabled { get; init; }
    public bool Available { get; init; }
    public string? Error { get; init; }
    public int Queued { get; init; }
}

public sealed class PodcastStatus
{
    public bool Enabled { get; init; }
    public bool FfmpegAvailable { get; init; }
    public bool Refreshing { get; init; }
    public DateTimeOffset? LastRefreshAt { get; init; }
    public string? DownloadFolder { get; init; }
    public long StorageBytes { get; init; }
    public long MaxStorageBytes { get; init; }
    public int DownloadsActive { get; init; }
    public int DownloadsQueued { get; init; }
    public PodcastTranscriptsStatus? Transcripts { get; init; }
    public int PodcastCount { get; init; }
    public int EpisodeCount { get; init; }
}

// ---------------------------------------------------------------- Bodies

public sealed class PodcastSubscribeInput
{
    public string? FeedUrl { get; init; }
    public long? ItunesId { get; init; }
}

public sealed class PodcastQueueAddInput
{
    public string EpisodeId { get; init; } = "";
    public string Position { get; init; } = "last"; // next | last
}

public sealed class PodcastQueueOrderInput
{
    public List<string> EpisodeIds { get; init; } = new();
}

public sealed class EpisodeProgressInput
{
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double Speed { get; init; } = 1.0;
    public string? DeviceName { get; init; }
}

public sealed class PlayedBody
{
    public bool Played { get; init; }
    public DateTimeOffset? Before { get; init; }
}

public sealed class BookmarkInput
{
    public double PositionSeconds { get; init; }
    public string Note { get; init; } = "";
}

public sealed class BookmarkNoteInput
{
    public string Note { get; init; } = "";
}

public sealed class BookmarkToNoteResult
{
    public string NoteId { get; init; } = "";
}

public sealed class ClipInput
{
    public double StartSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public string Title { get; init; } = "";
}

public sealed class TranscribeResult
{
    public string State { get; init; } = "";
}
