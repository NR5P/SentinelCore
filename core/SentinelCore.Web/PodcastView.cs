using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Web;

// Everything one person's answer depends on, loaded once per request: their progress, Up Next,
// bookmarks and subscriptions, plus the household's shows, followers, chapters and transcripts.
// Builds the contract's Podcast, Episode, Bookmark and progress objects (MUSIC_AND_PODCASTS.md §5).
public sealed class PodcastView
{
    // Unplayed counts only go back this far before someone followed a show; a show with two
    // thousand episodes is not two thousand unplayed ones.
    private static readonly TimeSpan UnplayedLookback = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnfollowedUnplayedWindow = TimeSpan.FromDays(14);

    private readonly Dictionary<Guid, PodcastProgress> _progress;
    private readonly List<Guid> _queue;
    private readonly HashSet<Guid> _queued;
    private readonly Dictionary<Guid, int> _bookmarkCounts;
    private readonly Dictionary<Guid, PodcastSubscription> _mine;
    private readonly Dictionary<Guid, int> _followers;
    private readonly IReadOnlyDictionary<Guid, string> _transcripts;
    private readonly IReadOnlySet<Guid> _chapters;
    private readonly Dictionary<Guid, PodcastShow> _shows;
    private readonly ILookup<Guid, PodcastEpisodeHeader> _headers;
    private readonly PodcastDownloadManager _downloads;

    private PodcastView(
        string userKey, bool isAdmin, Dictionary<Guid, PodcastProgress> progress, List<Guid> queue, Dictionary<Guid, int> bookmarkCounts,
        Dictionary<Guid, PodcastSubscription> mine, Dictionary<Guid, int> followers, IReadOnlyDictionary<Guid, string> transcripts,
        IReadOnlySet<Guid> chapters, Dictionary<Guid, PodcastShow> shows, ILookup<Guid, PodcastEpisodeHeader> headers, PodcastDownloadManager downloads)
    {
        UserKey = userKey;
        IsAdmin = isAdmin;
        _progress = progress;
        _queue = queue;
        _queued = [.. queue];
        _bookmarkCounts = bookmarkCounts;
        _mine = mine;
        _followers = followers;
        _transcripts = transcripts;
        _chapters = chapters;
        _shows = shows;
        _headers = headers;
        _downloads = downloads;
    }

    public string UserKey { get; }
    public bool IsAdmin { get; }

    public static async Task<PodcastView> LoadAsync(IPodcastStore store, PodcastDownloadManager downloads, string userKey, bool isAdmin, CancellationToken cancellationToken)
    {
        var subscriptions = await store.GetSubscriptionsAsync(cancellationToken);
        return new PodcastView(
            userKey,
            isAdmin,
            (await store.GetProgressAsync(userKey, cancellationToken)).ToDictionary(p => p.EpisodeId),
            [.. (await store.GetQueueAsync(userKey, cancellationToken)).Select(q => q.EpisodeId)],
            (await store.GetBookmarksAsync(userKey, cancellationToken)).GroupBy(b => b.EpisodeId).ToDictionary(g => g.Key, g => g.Count()),
            subscriptions.Where(s => s.UserKey == userKey).ToDictionary(s => s.PodcastId),
            subscriptions.Where(s => s.Subscribed).GroupBy(s => s.PodcastId).ToDictionary(g => g.Key, g => g.Count()),
            await store.TranscriptSourcesAsync(cancellationToken),
            await store.EpisodesWithChaptersAsync(cancellationToken),
            (await store.GetShowsAsync(cancellationToken)).ToDictionary(s => s.Id),
            (await store.GetEpisodeHeadersAsync(cancellationToken)).ToLookup(h => h.PodcastId),
            downloads);
    }

    public IReadOnlyList<Guid> Queue => _queue;
    public IEnumerable<PodcastShow> Shows => _shows.Values;
    public PodcastShow? Show(Guid id) => _shows.GetValueOrDefault(id);
    public PodcastSubscription? Mine(Guid podcastId) => _mine.GetValueOrDefault(podcastId);
    public bool IsSubscribed(Guid podcastId) => _mine.TryGetValue(podcastId, out var s) && s.Subscribed;
    public IEnumerable<Guid> SubscribedIds => _mine.Values.Where(s => s.Subscribed).Select(s => s.PodcastId);
    public PodcastProgress? ProgressOf(Guid episodeId) => _progress.GetValueOrDefault(episodeId);
    public bool IsPlayed(Guid episodeId) => _progress.TryGetValue(episodeId, out var p) && p.Played;
    public bool IsInProgress(Guid episodeId) => _progress.TryGetValue(episodeId, out var p) && !p.Played && p.PositionSeconds > 0;
    public bool InQueue(Guid episodeId) => _queued.Contains(episodeId);
    public int BookmarkCount(Guid episodeId) => _bookmarkCounts.GetValueOrDefault(episodeId);
    public IEnumerable<PodcastProgress> AllProgress => _progress.Values;
    public PodcastMySettings MySettings(Guid podcastId) => Mine(podcastId)?.Settings ?? new PodcastMySettings();
    public int Followers(Guid podcastId) => _followers.GetValueOrDefault(podcastId);
    public bool HasTranscript(PodcastEpisode episode) => _transcripts.ContainsKey(episode.Id) || episode.Transcripts.Count > 0;

    public IEnumerable<PodcastEpisodeHeader> HeadersOf(Guid podcastId) => _headers[podcastId];

    public int UnplayedCount(Guid podcastId)
    {
        var since = Mine(podcastId) is { Subscribed: true } mine
            ? mine.SubscribedAt - UnplayedLookback
            : DateTimeOffset.UtcNow - UnfollowedUnplayedWindow;
        return _headers[podcastId].Count(h => !h.Archived && h.PublishedAt >= since && !IsPlayed(h.Id));
    }

    // ---- objects ----------------------------------------------------------------------------

    public static string ShowArtUrl(PodcastShow show) => $"/api/podcasts/{show.Id}/art{Version(show.ImageUrl)}";

    public object Podcast(PodcastShow show) => new
    {
        id = show.Id,
        feedUrl = show.FeedUrl,
        title = show.Title,
        author = show.Author,
        description = show.Description,
        artUrl = ShowArtUrl(show),
        link = show.Link,
        categories = show.Categories,
        language = show.Language,
        @explicit = show.Explicit,
        episodeCount = _headers[show.Id].Count(),
        unplayedCount = UnplayedCount(show.Id),
        newestEpisodeAt = show.NewestEpisodeAt,
        lastRefreshedAt = show.LastRefreshedAt,
        refreshError = show.RefreshError,
        subscribed = IsSubscribed(show.Id),
        subscriberCount = Followers(show.Id),
        funding = show.Funding.Select(f => new { url = f.Url, title = f.Title }),
        settings = SettingsDto(show.Settings),
        mySettings = MySettingsDto(MySettings(show.Id))
    };

    public static object SettingsDto(PodcastShowSettings settings) => new
    {
        autoDownload = settings.AutoDownload,
        keepEpisodes = settings.KeepEpisodes,
        voiceBoost = settings.VoiceBoost,
        trimSilence = settings.TrimSilence,
        transcribe = settings.Transcribe
    };

    public static object MySettingsDto(PodcastMySettings settings) => new
    {
        playbackSpeed = settings.PlaybackSpeed,
        skipIntroSeconds = settings.SkipIntroSeconds,
        skipOutroSeconds = settings.SkipOutroSeconds,
        notify = settings.Notify,
        autoQueue = settings.AutoQueue,
        inBriefing = settings.InBriefing,
        sort = settings.Sort
    };

    public static string EpisodeArtUrl(PodcastEpisode episode, PodcastShow? show) =>
        $"/api/podcasts/episodes/{episode.Id}/art{Version(episode.ImageUrl ?? show?.ImageUrl)}";

    public object Episode(PodcastEpisode episode) => EpisodeFields(episode, detail: false);

    public object EpisodeDetail(PodcastEpisode episode) => EpisodeFields(episode, detail: true);

    private Dictionary<string, object?> EpisodeFields(PodcastEpisode episode, bool detail)
    {
        var show = Show(episode.PodcastId);
        var download = episode.Download;
        var active = _downloads.ProgressOf(episode.Id);
        var state = active is not null ? PodcastDownloadStates.Downloading
            : download is null ? PodcastDownloadStates.None
            : download.State == PodcastDownloadStates.Done && !download.IsDone ? PodcastDownloadStates.None
            : download.State == PodcastDownloadStates.Downloading ? PodcastDownloadStates.Queued
            : download.State;
        var done = state == PodcastDownloadStates.Done;
        var fields = new Dictionary<string, object?>
        {
            ["id"] = episode.Id,
            ["podcastId"] = episode.PodcastId,
            ["podcastTitle"] = show?.Title,
            ["title"] = episode.Title,
            ["summary"] = episode.Summary,
            ["publishedAt"] = episode.PublishedAt ?? episode.FirstSeenAt,
            ["durationSeconds"] = PodcastService.CurrentDuration(episode) is { } duration ? Math.Round(duration, 1) : null,
            ["season"] = episode.Season,
            ["episode"] = episode.EpisodeNumber,
            ["episodeType"] = episode.EpisodeType,
            ["explicit"] = episode.Explicit,
            ["link"] = episode.Link,
            ["artUrl"] = EpisodeArtUrl(episode, show),
            ["audioUrl"] = $"/api/podcasts/episodes/{episode.Id}/audio",
            ["mimeType"] = done ? download!.FileMimeType ?? episode.MimeType : episode.MimeType,
            ["sizeBytes"] = done ? download!.FileBytes : episode.SizeBytes,
            ["download"] = new
            {
                state,
                progress = done ? 1.0 : Math.Round(active ?? 0, 3),
                error = state == PodcastDownloadStates.Failed ? download?.Error : null,
                enhanced = done && download!.Enhanced,
                timeSavedSeconds = done ? Math.Round(download!.TimeSavedSeconds, 1) : 0
            },
            ["archived"] = episode.Archived,
            ["hasChapters"] = episode.ChaptersUrl is not null || _chapters.Contains(episode.Id),
            ["hasTranscript"] = HasTranscript(episode),
            ["transcriptSource"] = _transcripts.TryGetValue(episode.Id, out var source) ? source : episode.Transcripts.Count > 0 ? "feed" : null,
            ["progress"] = Progress(ProgressOf(episode.Id), episode),
            ["inQueue"] = InQueue(episode.Id),
            ["bookmarkCount"] = _bookmarkCounts.GetValueOrDefault(episode.Id)
        };
        if (detail)
        {
            fields["description"] = episode.DescriptionHtml;
            fields["descriptionText"] = episode.DescriptionText;
        }
        return fields;
    }

    // Stored in the original timing; answered in the file's (see Podcast.cs).
    public static object? Progress(PodcastProgress? progress, PodcastEpisode episode)
    {
        if (progress is null) return null;
        var timeline = episode.Timeline;
        var position = timeline.ToFile(progress.PositionSeconds);
        var duration = timeline.ToFile(progress.DurationSeconds) ?? PodcastService.CurrentDuration(episode);
        var percent = progress.Played ? 100 : duration is > 0 ? Math.Clamp(position / duration.Value * 100, 0, 100) : 0;
        return new
        {
            positionSeconds = Math.Round(position, 1),
            durationSeconds = duration is { } d ? Math.Round(d, 1) : (double?)null,
            percent = Math.Round(percent, 1),
            played = progress.Played,
            updatedAt = progress.UpdatedAt
        };
    }

    // The seconds left to hear, for Fill my drive.
    public double RemainingSeconds(PodcastEpisode episode)
    {
        var duration = PodcastService.CurrentDuration(episode) ?? 0;
        var progress = ProgressOf(episode.Id);
        if (progress is null || progress.Played) return duration;
        return Math.Max(0, duration - episode.Timeline.ToFile(progress.PositionSeconds));
    }

    public object Bookmark(PodcastBookmark bookmark, PodcastEpisode? episode)
    {
        var show = episode is null ? null : Show(episode.PodcastId);
        return new
        {
            id = bookmark.Id,
            episodeId = bookmark.EpisodeId,
            episodeTitle = episode?.Title,
            podcastId = episode?.PodcastId,
            podcastTitle = show?.Title,
            positionSeconds = Math.Round(episode is null ? bookmark.PositionSeconds : episode.Timeline.ToFile(bookmark.PositionSeconds), 1),
            note = bookmark.Note,
            transcriptSnippet = bookmark.TranscriptSnippet,
            createdAt = bookmark.CreatedAt,
            artUrl = episode is null ? null : EpisodeArtUrl(episode, show)
        };
    }

    // A short fingerprint of the image address, so changed art is a new URL and old art can be
    // cached for a day.
    private static string Version(string? imageUrl) =>
        imageUrl is null ? "" : "?v=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl)), 0, 4).ToLowerInvariant();
}
