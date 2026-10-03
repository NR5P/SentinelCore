namespace SentinelCore.Web;

// Checks followed shows every refreshMinutes, picks up downloads the last run left half-done, and
// once an hour clears played and orphaned downloads.
public sealed class PodcastWorker(PodcastService service, PodcastDownloadManager downloads, AppSettingsFile settings, ILogger<PodcastWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        var resumed = false;
        var lastSweep = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = await PodcastLocations.ReadOptionsAsync(settings, stoppingToken);
                if (options.Enabled)
                {
                    if (!resumed)
                    {
                        await downloads.ResumeAsync(stoppingToken);
                        resumed = true;
                    }
                    await service.RefreshAllAsync(force: false, stoppingToken);
                    if (DateTimeOffset.UtcNow - lastSweep > SweepInterval)
                    {
                        await downloads.SweepAsync(stoppingToken);
                        await downloads.EnforceLimitsAsync(null, stoppingToken);
                        lastSweep = DateTimeOffset.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Podcast refresh scheduling failed"); }

            try { await Task.Delay(CheckInterval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

// The Podcasts tab as the shared audio code sees it (AudioShared.cs): episodes resolved for a
// person, the briefing's episodes, a wake-up alarm's queue, Stats, and the backup section.
public sealed class PodcastAudioSource(
    IPodcastStore store,
    PodcastDownloadManager downloads,
    AppSettingsFile settings) : IPodcastAudioSource
{
    private const int TopShows = 10;

    public async Task<bool> EnabledAsync(CancellationToken cancellationToken) =>
        (await PodcastLocations.ReadOptionsAsync(settings, cancellationToken)).Enabled;

    public async Task<IReadOnlyList<AudioEntry>> ResolveEpisodesAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var view = await PodcastView.LoadAsync(store, downloads, userKey, isAdmin, cancellationToken);
        var found = (await store.GetEpisodesAsync(ids, cancellationToken)).ToDictionary(e => e.Id);
        return [.. ids.Where(found.ContainsKey).Select(id => new AudioEntry(id, view.Episode(found[id])))];
    }

    // Shows in the order the person followed them (oldest first), the newest unplayed episode of
    // each that is in the briefing.
    public async Task<IReadOnlyList<AudioEntry>> BriefingEpisodesAsync(string userKey, int max, CancellationToken cancellationToken)
    {
        var view = await PodcastView.LoadAsync(store, downloads, userKey, false, cancellationToken);
        var shows = (await store.GetSubscriptionsAsync(cancellationToken))
            .Where(s => s.UserKey == userKey && s.Subscribed && s.Settings.InBriefing)
            .OrderBy(s => s.SubscribedAt)
            .Select(s => s.PodcastId)
            .ToList();
        var entries = new List<AudioEntry>();
        foreach (var podcastId in shows)
        {
            if (entries.Count >= max) break;
            var newest = (await store.GetEpisodesForShowsAsync([podcastId], DateTimeOffset.UtcNow.AddDays(-60), cancellationToken))
                .Where(e => !e.Archived && !view.IsPlayed(e.Id))
                .OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt)
                .FirstOrDefault();
            if (newest is not null) entries.Add(new AudioEntry(newest.Id, view.Episode(newest)));
        }
        return entries;
    }

    public async Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken)
    {
        if (type != AudioSourceTypes.Podcast || !Guid.TryParse(id, out var podcastId)) return [];
        var episodes = (await store.GetEpisodesAsync(podcastId, cancellationToken)).Where(e => !e.Archived).ToList();
        if (episodes.Count == 0) return [];
        var played = (await store.GetProgressAsync(userKey, cancellationToken)).Where(p => p.Played).Select(p => p.EpisodeId).ToHashSet();
        var ordered = episodes.OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt).ToList();
        return [(ordered.FirstOrDefault(e => !played.Contains(e.Id)) ?? ordered[0]).Id];
    }

    // The "podcasts" object of GET /api/audio/stats (MUSIC_AND_PODCASTS.md §9). byHour and
    // byWeekday are in this server's local time, Monday first.
    public async Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var view = await PodcastView.LoadAsync(store, downloads, userKey, false, cancellationToken);
        var listening = await store.GetListeningAsync(userKey, from, to, cancellationToken);
        var finished = view.AllProgress.Where(p => p.Played && p.PlayedAt is { } at && at >= from && at < to).ToList();
        var finishedShows = (await store.GetEpisodesAsync(finished.Select(p => p.EpisodeId), cancellationToken))
            .GroupBy(e => e.PodcastId).ToDictionary(g => g.Key, g => g.Count());

        var byHour = new double[24];
        var byWeekday = new double[7];
        foreach (var row in listening)
        {
            var local = TimeZoneInfo.ConvertTime(row.HourStart, TimeZoneInfo.Local);
            byHour[local.Hour] += row.ListenedSeconds / 60;
            byWeekday[((int)local.DayOfWeek + 6) % 7] += row.ListenedSeconds / 60;
        }

        var top = listening.GroupBy(l => l.PodcastId)
            .Select(g => (PodcastId: g.Key, Seconds: g.Sum(l => l.ListenedSeconds)))
            .Concat(finishedShows.Keys.Where(id => listening.All(l => l.PodcastId != id)).Select(id => (PodcastId: id, Seconds: 0.0)))
            .Where(t => view.Show(t.PodcastId) is not null)
            .OrderByDescending(t => t.Seconds)
            .ThenByDescending(t => finishedShows.GetValueOrDefault(t.PodcastId))
            .Take(TopShows)
            .Select(t => new
            {
                podcast = view.Podcast(view.Show(t.PodcastId)!),
                minutes = (int)Math.Round(t.Seconds / 60),
                episodes = finishedShows.GetValueOrDefault(t.PodcastId)
            })
            .ToList();

        return new
        {
            minutes = (int)Math.Round(listening.Sum(l => l.ListenedSeconds) / 60),
            episodesFinished = finished.Count,
            timeSavedMinutes = (int)Math.Round(listening.Sum(l => l.SavedSeconds) / 60),
            topShows = top,
            byHour = byHour.Select(m => (int)Math.Round(m)).ToArray(),
            byWeekday = byWeekday.Select(m => (int)Math.Round(m)).ToArray()
        };
    }

    public Task<PodcastBackup> ExportAsync(CancellationToken cancellationToken) => store.ExportAsync(cancellationToken);

    public Task<int> RestoreAsync(PodcastBackup backup, CancellationToken cancellationToken) => store.RestoreAsync(backup, cancellationToken);
}
