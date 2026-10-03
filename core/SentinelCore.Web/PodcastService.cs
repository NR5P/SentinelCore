using System.Collections.Concurrent;
using System.Text.Json;
using SentinelCore;

namespace SentinelCore.Web;

// Tells people a show they follow has something new. Behind an interface so the tests can see
// exactly who would have been pushed without a phone.
public interface IPodcastNotifier
{
    // `userKeys` holds "" when login is off: that means every registered phone.
    Task NotifyAsync(IReadOnlyCollection<string> userKeys, PodcastShow show, IReadOnlyList<PodcastEpisode> episodes, CancellationToken cancellationToken);
}

public sealed class PodcastPushNotifier(AppSettingsFile settings, PushNotificationDispatcher push, ILogger<PodcastPushNotifier> logger) : IPodcastNotifier
{
    // The apps open the episode for this type; EventId carries the newest episode's id.
    public const string PushType = "podcast";

    public async Task NotifyAsync(IReadOnlyCollection<string> userKeys, PodcastShow show, IReadOnlyList<PodcastEpisode> episodes, CancellationToken cancellationToken)
    {
        if (userKeys.Count == 0 || episodes.Count == 0) return;
        var options = await settings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, cancellationToken) ?? new PushNotificationOptions();
        if (!options.Enabled || !PushProviderSelection.AnyConfigured(options)) return;

        var everyone = userKeys.Contains("");
        var wanted = userKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newest = episodes[0];
        var body = episodes.Count == 1 ? newest.Title : $"{episodes.Count} new episodes, including \"{newest.Title}\"";
        var message = new PushMessage(show.Title, body, newest.Id.ToString(), null, PushType);
        try
        {
            var report = await push.SendAsync(options, message, cancellationToken,
                everyone ? null : device => device.Username is { } owner && wanted.Contains(owner));
            logger.LogInformation("New-episode push for {Show} reached {Delivered}/{Attempted} phones", show.Title, report.Delivered, report.Attempted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The new-episode push for {Show} failed", show.Title);
        }
    }
}

public sealed record PodcastSubscribeResult(PodcastShow? Show, string? Error);

// Subscribing, refreshing and what happens when an episode arrives; plus the bits of an episode
// fetched lazily from its feed (chapters, transcripts) and the progress rules.
public sealed class PodcastService(
    IPodcastStore store,
    PodcastHttp http,
    PodcastDownloadManager downloads,
    PodcastTranscriber transcriber,
    IPodcastNotifier notifier,
    AppSettingsFile settings,
    ILogger<PodcastService> logger)
{
    // A feed that suddenly publishes its back catalogue is not twenty new episodes to push about.
    private const int MaxNewPerRefresh = 20;
    private static readonly TimeSpan NewEpisodeSlack = TimeSpan.FromDays(2);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();
    private int _refreshingAll;

    public bool IsRefreshing => Volatile.Read(ref _refreshingAll) > 0 || _refreshLocks.Values.Any(l => l.CurrentCount == 0);

    public DateTimeOffset? LastRefreshAt { get; private set; }

    public static Guid ShowId(string feedUrl) => MediaNaming.StableId(Guid.Empty, "podcast:" + PodcastFeedUrl.Key(feedUrl));

    public static Guid EpisodeId(Guid podcastId, string feedKey) => MediaNaming.StableId(podcastId, feedKey);

    public async Task<(PodcastFeed Feed, PodcastFetchResult Fetch)> ReadFeedAsync(string url, CancellationToken cancellationToken)
    {
        PodcastFetchResult fetch;
        try { fetch = await http.FetchFeedAsync(url, null, null, cancellationToken); }
        catch (HttpRequestException ex) { throw new PodcastFeedException($"The feed could not be reached: {ex.Message}"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new PodcastFeedException("The feed's server did not answer in time."); }
        return (PodcastFeedParser.Parse(fetch.Content!), fetch);
    }

    public async Task<PodcastShow?> FindShowAsync(string feedUrl, CancellationToken cancellationToken)
    {
        var key = PodcastFeedUrl.Key(feedUrl);
        return (await store.GetShowsAsync(cancellationToken)).FirstOrDefault(s => PodcastFeedUrl.Key(s.FeedUrl) == key || s.Id == ShowId(feedUrl));
    }

    public async Task<PodcastSubscribeResult> SubscribeAsync(string userKey, string feedUrlInput, long? itunesId, CancellationToken cancellationToken)
    {
        var feedUrl = PodcastFeedUrl.Normalize(feedUrlInput);
        if (feedUrl is null) return new PodcastSubscribeResult(null, "That is not a feed address. It looks like https://feeds.example.com/show.xml.");

        if (await FindShowAsync(feedUrl, cancellationToken) is { } existing)
        {
            if (itunesId is not null && existing.ItunesId is null)
            {
                existing.ItunesId = itunesId;
                await store.SaveShowAsync(existing, cancellationToken);
            }
            await SetSubscribedAsync(userKey, existing.Id, true, cancellationToken);
            return new PodcastSubscribeResult(existing, null);
        }

        PodcastFeed feed;
        PodcastFetchResult fetch;
        try { (feed, fetch) = await ReadFeedAsync(feedUrl, cancellationToken); }
        catch (PodcastFeedException ex) { return new PodcastSubscribeResult(null, ex.Message); }

        // An old address that now redirects to a show the household already has is that show.
        var finalUrl = feed.NewFeedUrl ?? fetch.MovedTo ?? feedUrl;
        if (finalUrl != feedUrl && await FindShowAsync(finalUrl, cancellationToken) is { } moved)
        {
            await SetSubscribedAsync(userKey, moved.Id, true, cancellationToken);
            return new PodcastSubscribeResult(moved, null);
        }

        var options = await PodcastLocations.ReadOptionsAsync(settings, cancellationToken);
        var show = new PodcastShow
        {
            Id = ShowId(feedUrl),
            FeedUrl = feedUrl,
            ItunesId = itunesId,
            CreatedAt = DateTimeOffset.UtcNow,
            Settings = new PodcastShowSettings { AutoDownload = options.AutoDownloadNew, KeepEpisodes = options.KeepEpisodes }
        };
        await SetSubscribedAsync(userKey, show.Id, true, cancellationToken);
        await ApplyFeedAsync(show, feed, fetch, isFirst: true, cancellationToken);
        return new PodcastSubscribeResult(show, null);
    }

    // OPML import: the show is listed straight away under the OPML's title and filled in by a
    // refresh in the background (a feed that fails shows its refreshError).
    public async Task<(PodcastShow Show, bool Added)> AddUnfetchedAsync(string userKey, string feedUrl, string? title, CancellationToken cancellationToken)
    {
        if (await FindShowAsync(feedUrl, cancellationToken) is { } existing)
        {
            var subscribed = (await store.GetSubscriptionsAsync(cancellationToken)).Any(s => s.UserKey == userKey && s.PodcastId == existing.Id && s.Subscribed);
            if (!subscribed) await SetSubscribedAsync(userKey, existing.Id, true, cancellationToken);
            return (existing, !subscribed);
        }
        var options = await PodcastLocations.ReadOptionsAsync(settings, cancellationToken);
        var show = new PodcastShow
        {
            Id = ShowId(feedUrl),
            FeedUrl = feedUrl,
            Title = string.IsNullOrWhiteSpace(title) ? new Uri(feedUrl).Host : title.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            Settings = new PodcastShowSettings { AutoDownload = options.AutoDownloadNew, KeepEpisodes = options.KeepEpisodes }
        };
        await store.SaveShowAsync(show, cancellationToken);
        await SetSubscribedAsync(userKey, show.Id, true, cancellationToken);
        return (show, true);
    }

    public async Task SetSubscribedAsync(string userKey, Guid podcastId, bool subscribed, CancellationToken cancellationToken)
    {
        var current = (await store.GetSubscriptionsAsync(cancellationToken)).FirstOrDefault(s => s.UserKey == userKey && s.PodcastId == podcastId);
        var now = DateTimeOffset.UtcNow;
        if (current is not null && current.Subscribed == subscribed) return;
        await store.SaveSubscriptionAsync(new PodcastSubscription(
            userKey, podcastId, subscribed,
            subscribed ? now : current?.SubscribedAt ?? now,
            current?.Settings ?? new PodcastMySettings(),
            now), cancellationToken);
    }

    // Every show somebody follows whose turn has come (or all of them, when asked by hand).
    public async Task RefreshAllAsync(bool force, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refreshingAll);
        try
        {
            var options = await PodcastLocations.ReadOptionsAsync(settings, cancellationToken);
            var followed = (await store.GetSubscriptionsAsync(cancellationToken)).Where(s => s.Subscribed).Select(s => s.PodcastId).ToHashSet();
            var due = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(15, options.RefreshMinutes));
            foreach (var show in await store.GetShowsAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!followed.Contains(show.Id)) continue;
                if (!force && show.LastRefreshedAt is { } last && last > due) continue;
                await RefreshAsync(show.Id, cancellationToken);
            }
            LastRefreshAt = DateTimeOffset.UtcNow;
        }
        finally { Interlocked.Decrement(ref _refreshingAll); }
    }

    public async Task<PodcastShow?> RefreshAsync(Guid podcastId, CancellationToken cancellationToken)
    {
        var gate = _refreshLocks.GetOrAdd(podcastId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var show = await store.GetShowAsync(podcastId, cancellationToken);
            if (show is null) return null;
            var isFirst = show.LastRefreshedAt is null;
            PodcastFetchResult fetch;
            try
            {
                fetch = await http.FetchFeedAsync(show.FeedUrl, isFirst ? null : show.ETag, isFirst ? null : show.LastModified, cancellationToken);
                if (fetch.NotModified)
                {
                    show.LastRefreshedAt = DateTimeOffset.UtcNow;
                    show.RefreshError = null;
                    if (fetch.MovedTo is { } movedTo) show.FeedUrl = movedTo;
                    await store.SaveShowAsync(show, cancellationToken);
                    return show;
                }
                var feed = PodcastFeedParser.Parse(fetch.Content!);
                await ApplyFeedAsync(show, feed, fetch, isFirst, cancellationToken);
                return show;
            }
            catch (Exception ex) when (ex is PodcastFeedException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                show.RefreshError = ex is PodcastFeedException ? ex.Message : $"The feed could not be reached: {ex.Message}";
                // Counted as a refresh, so a dead feed is retried on the schedule rather than every minute.
                show.LastRefreshedAt = isFirst ? null : DateTimeOffset.UtcNow;
                await store.SaveShowAsync(show, cancellationToken);
                logger.LogInformation("Refreshing {Show} failed: {Error}", show.Title, show.RefreshError);
                return show;
            }
        }
        finally { gate.Release(); }
    }

    private async Task ApplyFeedAsync(PodcastShow show, PodcastFeed feed, PodcastFetchResult fetch, bool isFirst, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        show.Title = feed.Title;
        show.Author = feed.Author;
        show.Description = PodcastText.ToPlainText(feed.DescriptionHtml);
        show.ImageUrl = feed.ImageUrl ?? show.ImageUrl;
        show.Link = feed.Link;
        show.Categories = feed.Categories;
        show.Language = feed.Language;
        show.Explicit = feed.Explicit;
        show.Funding = feed.Funding;

        // The publisher moved the feed: itunes:new-feed-url, or a permanent redirect.
        var moved = feed.NewFeedUrl is { } newUrl && PodcastFeedUrl.Key(newUrl) != PodcastFeedUrl.Key(show.FeedUrl) ? newUrl
            : fetch.MovedTo is { } redirect && PodcastFeedUrl.Key(redirect) != PodcastFeedUrl.Key(show.FeedUrl) ? redirect
            : null;
        if (moved is not null)
        {
            logger.LogInformation("{Show} moved its feed from {Old} to {New}", show.Title, show.FeedUrl, moved);
            show.FeedUrl = moved;
            show.ETag = null;
            show.LastModified = null;
        }
        else
        {
            show.ETag = fetch.ETag;
            show.LastModified = fetch.LastModified;
        }

        var existing = (await store.GetEpisodesAsync(show.Id, cancellationToken)).ToDictionary(e => e.Id);
        var previousNewest = existing.Count == 0 ? (DateTimeOffset?)null : existing.Values.Max(e => e.PublishedAt ?? e.FirstSeenAt);
        var present = new HashSet<Guid>();
        var upserts = new List<PodcastEpisode>();
        var added = new List<PodcastEpisode>();
        foreach (var item in feed.Items)
        {
            var id = EpisodeId(show.Id, item.Key);
            if (!present.Add(id)) continue;
            var isNew = !existing.TryGetValue(id, out var episode);
            episode ??= new PodcastEpisode { Id = id, PodcastId = show.Id, FeedGuid = item.Key, FirstSeenAt = now };
            episode.Title = item.Title;
            episode.DescriptionHtml = PodcastText.SanitizeHtml(item.DescriptionHtml);
            episode.DescriptionText = PodcastText.ToPlainText(episode.DescriptionHtml);
            episode.PublishedAt = item.PublishedAt ?? episode.PublishedAt;
            episode.DurationSeconds = item.DurationSeconds ?? episode.DurationSeconds;
            episode.Season = item.Season;
            episode.EpisodeNumber = item.Episode;
            episode.EpisodeType = item.EpisodeType;
            episode.Explicit = item.Explicit;
            episode.Link = item.Link;
            episode.ImageUrl = item.ImageUrl;
            episode.EnclosureUrl = item.EnclosureUrl;
            episode.MimeType = item.MimeType;
            episode.SizeBytes = item.Length;
            episode.Transcripts = item.Transcripts;
            episode.ChaptersUrl = item.ChaptersUrl;
            episode.Archived = false;
            upserts.Add(episode);
            if (isNew) added.Add(episode);
        }
        await store.UpsertEpisodesAsync(upserts, cancellationToken);
        // A feed that came back with no episodes at all is far more likely broken for the moment
        // than really empty; nothing is archived on its word.
        if (feed.Items.Count > 0) await store.ArchiveMissingAsync(show.Id, present, cancellationToken);

        var all = existing.Values.Concat(added).ToList();
        show.NewestEpisodeAt = all.Count == 0 ? null : all.Max(e => e.PublishedAt ?? e.FirstSeenAt);
        show.LastRefreshedAt = now;
        show.RefreshError = null;
        await store.SaveShowAsync(show, cancellationToken);

        var fresh = isFirst
            ? []
            : added.Where(e => previousNewest is null || (e.PublishedAt ?? now) > previousNewest.Value - NewEpisodeSlack)
                .OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt)
                .Take(MaxNewPerRefresh)
                .ToList();
        await OnNewEpisodesAsync(show, upserts, fresh, isFirst, cancellationToken);
    }

    private async Task OnNewEpisodesAsync(PodcastShow show, IReadOnlyList<PodcastEpisode> inFeed, IReadOnlyList<PodcastEpisode> fresh, bool isFirst, CancellationToken cancellationToken)
    {
        var options = await PodcastLocations.ReadOptionsAsync(settings, cancellationToken);
        var transcribe = show.Settings.Transcribe && options.TranscriptsEnabled;
        if (isFirst)
        {
            // Following a show downloads its newest episode, and pushes nobody.
            var newest = inFeed.OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt).FirstOrDefault();
            if (newest is not null && show.Settings.AutoDownload)
                await downloads.RequestAsync(newest.Id, transcribe && newest.Transcripts.Count == 0, cancellationToken);
            return;
        }
        if (fresh.Count == 0) return;

        var keep = show.Settings.KeepEpisodes;
        foreach (var episode in fresh.Take(keep > 0 ? keep : fresh.Count))
        {
            var wantsWhisper = transcribe && episode.Transcripts.Count == 0;
            if (show.Settings.AutoDownload || wantsWhisper)
                await downloads.RequestAsync(episode.Id, wantsWhisper, cancellationToken);
        }

        // Feed transcripts are fetched now so new episodes are searchable straight away.
        foreach (var episode in fresh.Where(e => e.Transcripts.Count > 0))
        {
            try { await TranscriptAsync(episode, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Feed transcript fetch failed"); }
        }

        var followers = (await store.GetSubscriptionsAsync(cancellationToken)).Where(s => s.PodcastId == show.Id && s.Subscribed).ToList();
        foreach (var follower in followers.Where(f => f.Settings.AutoQueue))
        {
            var queue = (await store.GetQueueAsync(follower.UserKey, cancellationToken)).Select(q => q.EpisodeId).ToList();
            var additions = fresh.OrderBy(e => e.PublishedAt ?? e.FirstSeenAt).Select(e => e.Id).Where(id => !queue.Contains(id)).ToList();
            if (additions.Count > 0) await store.SetQueueAsync(follower.UserKey, [.. queue, .. additions], cancellationToken);
        }

        var notify = followers.Where(f => f.Settings.Notify).Select(f => f.UserKey).Distinct().ToList();
        if (notify.Count > 0) await notifier.NotifyAsync(notify, show, fresh, cancellationToken);
    }

    // ---- extras fetched from the feed -------------------------------------------------------

    // The stored transcript, else the feed's (fetched once and kept). Null when there is none.
    public async Task<PodcastTranscript?> TranscriptAsync(PodcastEpisode episode, CancellationToken cancellationToken)
    {
        if (await store.GetTranscriptAsync(episode.Id, cancellationToken) is { } stored) return stored;
        foreach (var link in episode.Transcripts.OrderBy(t => PodcastTranscriptParser.Preference(t.Type)))
        {
            var bytes = await http.GetSmallAsync(link.Url, cancellationToken);
            if (bytes is null) continue;
            var transcript = PodcastTranscriptParser.Parse(bytes, link.Type, "feed", link.Language);
            if (transcript is null) continue;
            await store.SaveTranscriptAsync(episode.Id, transcript, cancellationToken);
            return transcript;
        }
        return null;
    }

    // In the original timing; the caller converts. podcast:chapters first, else the file's own.
    public async Task<IReadOnlyList<PodcastChapter>> ChaptersAsync(PodcastEpisode episode, CancellationToken cancellationToken)
    {
        if (await store.GetChaptersAsync(episode.Id, cancellationToken) is { } cached) return cached;
        if (episode.ChaptersUrl is null) return [];
        var bytes = await http.GetSmallAsync(episode.ChaptersUrl, cancellationToken);
        if (bytes is null) return [];
        var chapters = ParseChaptersJson(bytes, episode.DurationSeconds);
        await store.SaveChaptersAsync(episode.Id, "feed", chapters, cancellationToken);
        return chapters;
    }

    // { "version": "1.2.0", "chapters": [ { "startTime": 0, "title": "Intro", "img": "…", "url": "…", "toc": true } ] }
    public static IReadOnlyList<PodcastChapter> ParseChaptersJson(byte[] json, double? duration)
    {
        var chapters = new List<PodcastChapter>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("chapters", out var list) || list.ValueKind != JsonValueKind.Array) return chapters;
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("toc", out var toc) && toc.ValueKind == JsonValueKind.False) continue;
                if (!item.TryGetProperty("startTime", out var start) || !start.TryGetDouble(out var startSeconds)) continue;
                double? end = item.TryGetProperty("endTime", out var e) && e.TryGetDouble(out var endSeconds) ? endSeconds : null;
                var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var image = item.TryGetProperty("img", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                var url = item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                chapters.Add(new PodcastChapter(Math.Max(0, startSeconds), end, string.IsNullOrWhiteSpace(title) ? $"Chapter {chapters.Count + 1}" : title.Trim(), image, url));
            }
        }
        catch (JsonException) { }
        return WithEnds(chapters, duration);
    }

    // Every chapter ends where the next starts; the last at the episode's end.
    public static IReadOnlyList<PodcastChapter> WithEnds(IEnumerable<PodcastChapter> chapters, double? duration)
    {
        var ordered = chapters.OrderBy(c => c.StartSeconds).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var next = i + 1 < ordered.Count ? ordered[i + 1].StartSeconds : duration;
            if (ordered[i].EndSeconds is null || ordered[i].EndSeconds <= ordered[i].StartSeconds || next is { } n && ordered[i].EndSeconds > n)
                ordered[i] = ordered[i] with { EndSeconds = next };
        }
        return ordered;
    }

    // ---- listening --------------------------------------------------------------------------

    // The episode's length as the person hears it: the downloaded file's, or the feed's.
    public static double? CurrentDuration(PodcastEpisode episode) =>
        episode.Download is { IsDone: true, FileDurationSeconds: { } file } ? file : episode.DurationSeconds;

    // MUSIC_AND_PODCASTS.md §6 Listening. Positions arrive in the file's time and are stored in
    // the original's. Each report also adds what was listened to since the last one (and what
    // speed, trimmed silence and skips saved) to the Stats log.
    public async Task<PodcastProgress?> ReportProgressAsync(string userKey, PodcastEpisode episode, PodcastProgressInput input, CancellationToken cancellationToken)
    {
        var timeline = episode.Timeline;
        var existing = (await store.GetProgressForEpisodesAsync([episode.Id], cancellationToken)).FirstOrDefault(p => p.UserKey == userKey);
        var position = Math.Max(0, input.PositionSeconds);
        if (position < PodcastProgress.MinimumSavedSeconds) return existing;

        var duration = input.DurationSeconds is > 0 ? input.DurationSeconds : CurrentDuration(episode);
        var played = PodcastProgress.IsPlayed(position, duration);
        var now = DateTimeOffset.UtcNow;

        var speed = Math.Clamp(input.Speed is > 0 ? input.Speed.Value : 1, 0.5, 4);
        var previous = existing is { Played: false } ? timeline.ToFile(existing.PositionSeconds) : (double?)null;
        var delta = previous is { } from ? position - from : Math.Min(position, 15 * speed);
        var elapsed = existing is null ? 15 : (now - existing.UpdatedAt).TotalSeconds;
        if (delta > 0 && delta <= 1800 && delta <= elapsed * speed * 1.5 + 20)
        {
            var wall = delta / speed;
            var saved = delta - wall;
            saved += timeline.ToOriginal(position) - timeline.ToOriginal(position - delta) - delta;
            var mine = (await store.GetSubscriptionsAsync(cancellationToken)).FirstOrDefault(s => s.UserKey == userKey && s.PodcastId == episode.PodcastId)?.Settings;
            if (mine is not null)
            {
                if (existing is null && mine.SkipIntroSeconds > 0 && position >= mine.SkipIntroSeconds) saved += mine.SkipIntroSeconds;
                if (played && existing is not { Played: true } && mine.SkipOutroSeconds > 0) saved += mine.SkipOutroSeconds;
            }
            await store.AddListeningAsync(new PodcastListening(userKey, HourStart(now), episode.PodcastId, wall, Math.Max(0, saved)), cancellationToken);
        }

        var progress = new PodcastProgress(userKey, episode.Id, timeline.ToOriginal(position), timeline.ToOriginal(duration), played, now,
            played ? existing?.PlayedAt ?? now : null);
        await store.SetProgressAsync(progress, cancellationToken);
        if (played) await RemoveFromQueueAsync(userKey, episode.Id, cancellationToken);
        return progress;
    }

    public async Task<PodcastProgress?> SetPlayedAsync(string userKey, PodcastEpisode episode, bool played, CancellationToken cancellationToken)
    {
        if (!played)
        {
            await store.DeleteProgressAsync(userKey, episode.Id, cancellationToken);
            return null;
        }
        var existing = (await store.GetProgressForEpisodesAsync([episode.Id], cancellationToken)).FirstOrDefault(p => p.UserKey == userKey);
        var now = DateTimeOffset.UtcNow;
        var progress = new PodcastProgress(userKey, episode.Id, existing?.PositionSeconds ?? 0, existing?.DurationSeconds ?? episode.DurationSeconds,
            true, now, existing?.PlayedAt ?? now);
        await store.SetProgressAsync(progress, cancellationToken);
        await RemoveFromQueueAsync(userKey, episode.Id, cancellationToken);
        return progress;
    }

    public async Task RemoveFromQueueAsync(string userKey, Guid episodeId, CancellationToken cancellationToken)
    {
        var queue = (await store.GetQueueAsync(userKey, cancellationToken)).Select(q => q.EpisodeId).ToList();
        if (queue.Remove(episodeId)) await store.SetQueueAsync(userKey, queue, cancellationToken);
    }

    public static DateTimeOffset HourStart(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    public async Task<string?> TranscribeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        var options = await PodcastLocations.ReadOptionsAsync(settings, cancellationToken);
        if (await transcriber.CheckAsync(options, cancellationToken) is { } problem) return problem;
        transcriber.Enqueue(episodeId);
        return null;
    }
}
