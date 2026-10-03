using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace SentinelCore.Web;

// The Podcasts tab's HTTP surface - the contract is core/docs/MUSIC_AND_PODCASTS.md (§5 and §6),
// and every client (web, iOS/macOS, tvOS, Android, Windows) is built against that file rather
// than against this one.
public static class PodcastEndpoints
{
    private const string OffMessage = "Podcasts are turned off in Settings.";
    private const string MissingShow = "That show is not on this server any more.";
    private const string MissingEpisode = "That episode is not on this server any more.";
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;
    private const int RowSize = 20;
    private const int NewEpisodeDays = 14;
    private const double ShortEpisodeSeconds = 20 * 60;
    public const double MinimumClipSeconds = 5;
    public const double MaximumClipSeconds = 180;

    public static void MapPodcastEndpoints(this WebApplication app)
    {
        // Every route answers 404 while the tab is off; /api/audio/enabled (the shared audio
        // code's) is what clients ask first.
        var podcasts = app.MapGroup("/api/podcasts").RequireAuthorization().AddEndpointFilter(async (invocation, next) =>
        {
            var settings = invocation.HttpContext.RequestServices.GetRequiredService<AppSettingsFile>();
            var options = await PodcastLocations.ReadOptionsAsync(settings, invocation.HttpContext.RequestAborted);
            return options.Enabled ? await next(invocation) : Results.NotFound(new { error = OffMessage });
        });

        MapStatus(podcasts);
        MapDirectory(podcasts);
        MapShows(podcasts);
        MapHome(podcasts);
        MapQueue(podcasts);
        MapEpisodes(podcasts);
        MapTranscripts(podcasts);
        MapBookmarks(podcasts);
        MapClips(podcasts);
        MapOpml(podcasts);
    }

    private static string UserKey(HttpContext context) => MediaEndpoints.UserKey(context);

    private static bool IsAdmin(HttpContext context) => context.User.IsInRole(nameof(AccountRole.Admin));

    private static Task<PodcastView> ViewAsync(HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        PodcastView.LoadAsync(store, downloads, UserKey(context), IsAdmin(context), ct);

    // ASP.NET writes an empty body for a null result, which every client's JSON parser rejects.
    private static IResult JsonOrNull(object? value) =>
        value is null ? Results.Text("null", "application/json") : Results.Json(value);

    // MARK: - Status

    private static void MapStatus(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/status", async (HttpContext context, AppSettingsFile settings, IPodcastStore store, PodcastService service,
            PodcastDownloadManager downloads, PodcastTranscriber transcriber, PodcastAudioProcessor audio, PodcastLocations locations, CancellationToken ct) =>
        {
            var options = await PodcastLocations.ReadOptionsAsync(settings, ct);
            var all = await store.GetDownloadsAsync(ct);
            var (shows, episodes) = await store.CountsAsync(ct);
            var problem = await transcriber.CheckAsync(options, ct);
            return Results.Ok(new
            {
                enabled = options.Enabled,
                ffmpegAvailable = await audio.FfmpegAvailableAsync(ct),
                refreshing = service.IsRefreshing,
                lastRefreshAt = service.LastRefreshAt ?? (await store.GetShowsAsync(ct)).Max(s => s.LastRefreshedAt),
                downloadFolder = IsAdmin(context) ? locations.DownloadFolderFor(options) : null,
                storageBytes = all.Where(d => d.IsDone).Sum(d => d.FileBytes ?? 0),
                maxStorageBytes = options.MaxStorageBytes(),
                downloadsActive = downloads.ActiveCount,
                downloadsQueued = all.Count(d => d.State == PodcastDownloadStates.Queued && !downloads.IsActive(d.EpisodeId)),
                transcripts = new
                {
                    enabled = options.TranscriptsEnabled,
                    available = problem is null,
                    error = problem ?? transcriber.LastError,
                    queued = transcriber.Queued
                },
                podcastCount = shows,
                episodeCount = episodes
            });
        });
    }

    // MARK: - Finding shows

    private static void MapDirectory(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/directory/search", async (string? q, HttpContext context, AppSettingsFile settings, PodcastDirectory directory,
            IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "Type a show's name to search for." });
            var options = await PodcastLocations.ReadOptionsAsync(settings, ct);
            return await DirectoryAsync(() => directory.SearchAsync(q, options.DirectoryCountry, ct), context, store, downloads, ct);
        });

        podcasts.MapGet("/directory/top", async (int? genre, HttpContext context, AppSettingsFile settings, PodcastDirectory directory,
            IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var options = await PodcastLocations.ReadOptionsAsync(settings, ct);
            return await DirectoryAsync(() => directory.TopAsync(options.DirectoryCountry, genre, ct), context, store, downloads, ct);
        });

        podcasts.MapGet("/directory/genres", () =>
            Results.Ok(PodcastDirectory.Genres.Select(g => new { id = g.Id, name = g.Name })));

        // The top shows in the genres the household listens to most, minus what it already has.
        podcasts.MapGet("/recommended", async (HttpContext context, AppSettingsFile settings, PodcastDirectory directory,
            IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var options = await PodcastLocations.ReadOptionsAsync(settings, ct);
            var shows = await store.GetShowsAsync(ct);
            var followed = (await store.GetSubscriptionsAsync(ct)).Where(s => s.Subscribed).Select(s => s.PodcastId).ToHashSet();
            var listening = (await store.GetListeningAsync(null, DateTimeOffset.UtcNow.AddDays(-180), DateTimeOffset.MaxValue, ct))
                .GroupBy(l => l.PodcastId).ToDictionary(g => g.Key, g => g.Sum(l => l.ListenedSeconds));
            var weights = new Dictionary<int, double>();
            foreach (var show in shows.Where(s => followed.Contains(s.Id) || listening.ContainsKey(s.Id)))
            {
                var weight = 600 + listening.GetValueOrDefault(show.Id);
                foreach (var genre in show.Categories.Select(PodcastDirectory.GenreIdFor).OfType<int>().Distinct())
                    weights[genre] = weights.GetValueOrDefault(genre) + weight;
            }
            var genres = weights.OrderByDescending(w => w.Value).Take(3).Select(w => (int?)w.Key).ToList();
            if (genres.Count == 0) genres.Add(null);

            return await DirectoryAsync(async () =>
            {
                var have = shows.Select(s => PodcastFeedUrl.Key(s.FeedUrl)).ToHashSet();
                var haveIds = shows.Where(s => s.ItunesId is not null).Select(s => s.ItunesId!.Value).ToHashSet();
                var picks = new List<PodcastDirectoryEntry>();
                var seen = new HashSet<long>();
                var charts = new List<IReadOnlyList<PodcastDirectoryEntry>>();
                foreach (var genre in genres) charts.Add(await directory.TopAsync(options.DirectoryCountry, genre, ct));
                // Interleaved, so the second genre is not buried under fifty of the first.
                for (var rank = 0; rank < 50 && picks.Count < 25; rank++)
                    foreach (var chart in charts.Where(c => rank < c.Count))
                    {
                        var entry = chart[rank];
                        if (haveIds.Contains(entry.ItunesId) || entry.FeedUrl is { } url && have.Contains(PodcastFeedUrl.Key(url))) continue;
                        if (seen.Add(entry.ItunesId)) picks.Add(entry);
                    }
                return picks;
            }, context, store, downloads, ct);
        });

        podcasts.MapGet("/preview", async (string? feedUrl, long? itunesId, HttpContext context, AppSettingsFile settings, PodcastService service,
            PodcastDirectory directory, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var options = await PodcastLocations.ReadOptionsAsync(settings, ct);
            var (url, error) = await FeedUrlForAsync(feedUrl, itunesId, options, directory, ct);
            if (url is null) return Results.BadRequest(new { error });
            try
            {
                var (feed, fetch) = await service.ReadFeedAsync(url, ct);
                var existing = await service.FindShowAsync(url, ct);
                var view = await ViewAsync(context, store, downloads, ct);
                var items = feed.Items.OrderByDescending(i => i.PublishedAt ?? DateTimeOffset.MinValue).ToList();
                return Results.Ok(new
                {
                    podcast = new
                    {
                        feedUrl = feed.NewFeedUrl ?? fetch.MovedTo ?? url,
                        title = feed.Title,
                        author = feed.Author,
                        description = PodcastText.ToPlainText(feed.DescriptionHtml),
                        artUrl = feed.ImageUrl,
                        link = feed.Link,
                        categories = feed.Categories,
                        language = feed.Language,
                        @explicit = feed.Explicit,
                        episodeCount = items.Count,
                        newestEpisodeAt = items.FirstOrDefault()?.PublishedAt,
                        funding = feed.Funding.Select(f => new { url = f.Url, title = f.Title }),
                        podcastId = existing?.Id,
                        subscribed = existing is not null && view.IsSubscribed(existing.Id)
                    },
                    episodes = items.Select(item =>
                    {
                        var text = PodcastText.ToPlainText(PodcastText.SanitizeHtml(item.DescriptionHtml));
                        return new
                        {
                            title = item.Title,
                            summary = PodcastText.Summary(text, 300),
                            publishedAt = item.PublishedAt,
                            durationSeconds = item.DurationSeconds,
                            season = item.Season,
                            episode = item.Episode,
                            episodeType = item.EpisodeType,
                            @explicit = item.Explicit,
                            link = item.Link,
                            artUrl = item.ImageUrl ?? feed.ImageUrl,
                            mimeType = item.MimeType,
                            sizeBytes = item.Length
                        };
                    })
                });
            }
            catch (PodcastFeedException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static async Task<(string? Url, string? Error)> FeedUrlForAsync(string? feedUrl, long? itunesId, PodcastSettingsDto options, PodcastDirectory directory, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(feedUrl))
        {
            var normalized = PodcastFeedUrl.Normalize(feedUrl);
            return normalized is null ? (null, "That is not a feed address. It looks like https://feeds.example.com/show.xml.") : (normalized, null);
        }
        if (itunesId is not { } id) return (null, "Give a feed address or an Apple Podcasts id.");
        try
        {
            var found = (await directory.LookupAsync([id], options.DirectoryCountry, ct)).FirstOrDefault();
            return found?.FeedUrl is { } url
                ? (url, null)
                : (null, "Apple's directory has no feed address for that show, so it can only be listened to in Apple Podcasts.");
        }
        catch (PodcastDirectoryException ex) { return (null, ex.Message); }
    }

    private static async Task<IResult> DirectoryAsync(Func<Task<IReadOnlyList<PodcastDirectoryEntry>>> load, HttpContext context,
        IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct)
    {
        IReadOnlyList<PodcastDirectoryEntry> entries;
        try { entries = await load(); }
        catch (PodcastDirectoryException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway); }
        var view = await ViewAsync(context, store, downloads, ct);
        var byFeed = view.Shows.GroupBy(s => PodcastFeedUrl.Key(s.FeedUrl)).ToDictionary(g => g.Key, g => g.First());
        var byItunes = view.Shows.Where(s => s.ItunesId is not null).GroupBy(s => s.ItunesId!.Value).ToDictionary(g => g.Key, g => g.First());
        return Results.Ok(entries.Select(entry =>
        {
            var show = byItunes.GetValueOrDefault(entry.ItunesId)
                ?? (entry.FeedUrl is { } url ? byFeed.GetValueOrDefault(PodcastFeedUrl.Key(url)) : null);
            return new
            {
                itunesId = entry.ItunesId,
                title = entry.Title,
                author = entry.Author,
                artUrl = entry.ArtUrl,
                feedUrl = entry.FeedUrl,
                genre = entry.Genre,
                episodeCount = entry.EpisodeCount,
                podcastId = show?.Id,
                subscribed = show is not null && view.IsSubscribed(show.Id)
            };
        }));
    }

    // MARK: - Subscriptions and shows

    private static void MapShows(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("", async (bool? all, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var view = await ViewAsync(context, store, downloads, ct);
            var shows = all == true ? view.Shows : view.Shows.Where(s => view.IsSubscribed(s.Id));
            return Results.Ok(shows.OrderByDescending(s => s.NewestEpisodeAt ?? s.CreatedAt).Select(view.Podcast));
        });

        podcasts.MapPost("/subscriptions", async (PodcastSubscribeInput input, HttpContext context, AppSettingsFile settings, PodcastService service,
            PodcastDirectory directory, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var options = await PodcastLocations.ReadOptionsAsync(settings, ct);
            var (url, error) = await FeedUrlForAsync(input.FeedUrl, input.ItunesId, options, directory, ct);
            if (url is null) return Results.BadRequest(new { errors = new[] { error } });
            var result = await service.SubscribeAsync(UserKey(context), url, input.ItunesId, ct);
            if (result.Show is null) return Results.BadRequest(new { errors = new[] { result.Error } });
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Created($"/api/podcasts/{result.Show.Id}", view.Podcast(view.Show(result.Show.Id) ?? result.Show));
        });

        podcasts.MapDelete("/{id:guid}/subscription", async (Guid id, HttpContext context, PodcastService service, IPodcastStore store, CancellationToken ct) =>
        {
            if (await store.GetShowAsync(id, ct) is null) return Results.NotFound(new { error = MissingShow });
            await service.SetSubscribedAsync(UserKey(context), id, false, ct);
            return Results.NoContent();
        });

        podcasts.MapDelete("/{id:guid}", async (Guid id, IPodcastStore store, PodcastDownloadManager downloads, PodcastLocations locations, CancellationToken ct) =>
        {
            var show = await store.GetShowAsync(id, ct);
            if (show is null) return Results.NoContent();
            foreach (var episode in await store.GetEpisodesAsync(id, ct))
                if (episode.Download is not null) await downloads.DeleteAsync(episode.Id, ct);
            foreach (var clip in await store.GetClipsAsync(null, ct))
                if (await store.GetEpisodeAsync(clip.EpisodeId, ct) is { } episode && episode.PodcastId == id) DeleteQuietly(clip.FilePath);
            var folder = Path.Combine(await locations.DownloadFolderAsync(ct), id.ToString("N"));
            try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch (IOException) { }
            await store.DeleteShowAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        podcasts.MapGet("/{id:guid}", async (Guid id, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var view = await ViewAsync(context, store, downloads, ct);
            return view.Show(id) is { } show ? Results.Ok(view.Podcast(show)) : Results.NotFound(new { error = MissingShow });
        });

        podcasts.MapPut("/{id:guid}/settings", async (Guid id, PodcastShowSettings input, HttpContext context, IPodcastStore store,
            PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var show = await store.GetShowAsync(id, ct);
            if (show is null) return Results.NotFound(new { error = MissingShow });
            if (input.KeepEpisodes is < 0 or > 1000) return Results.BadRequest(new { errors = new[] { "Keep episodes is 0 (all) to 1000." } });
            show.Settings = input.Copy();
            await store.SaveShowAsync(show, ct);
            await downloads.EnforceLimitsAsync(null, ct);
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Ok(view.Podcast(show));
        }).RequireAuthorization("AdminOnly");

        podcasts.MapPut("/{id:guid}/my-settings", async (Guid id, PodcastMySettings input, HttpContext context, IPodcastStore store,
            PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var show = await store.GetShowAsync(id, ct);
            if (show is null) return Results.NotFound(new { error = MissingShow });
            var errors = new List<string>();
            if (input.PlaybackSpeed is { } speed && (speed < 0.5 || speed > 3.0)) errors.Add("Playback speed is 0.5 to 3.0, or blank for the app's own.");
            if (input.SkipIntroSeconds is < 0 or > 600 || input.SkipOutroSeconds is < 0 or > 600) errors.Add("Skips are 0 to 600 seconds.");
            var sort = input.Sort?.Trim().ToLowerInvariant() ?? "newest";
            if (sort is not ("newest" or "oldest")) errors.Add("Sort is newest or oldest.");
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var settings = input.Copy();
            settings.Sort = sort;
            var userKey = UserKey(context);
            var current = (await store.GetSubscriptionsAsync(ct)).FirstOrDefault(s => s.UserKey == userKey && s.PodcastId == id);
            var now = DateTimeOffset.UtcNow;
            await store.SaveSubscriptionAsync(current is null
                ? new PodcastSubscription(userKey, id, false, now, settings, now)
                : current with { Settings = settings, UpdatedAt = now }, ct);
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Ok(view.Podcast(show));
        });

        podcasts.MapPost("/{id:guid}/refresh", async (Guid id, HttpContext context, PodcastService service, IPodcastStore store,
            PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            if (await service.RefreshAsync(id, ct) is null) return Results.NotFound(new { error = MissingShow });
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Ok(view.Podcast(view.Show(id)!));
        });

        podcasts.MapPost("/refresh", (PodcastService service, IHostApplicationLifetime lifetime, ILoggerFactory loggers) =>
        {
            _ = Task.Run(async () =>
            {
                try { await service.RefreshAllAsync(force: true, lifetime.ApplicationStopping); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { loggers.CreateLogger("SentinelCore.Web.PodcastEndpoints").LogWarning(ex, "Refreshing every podcast failed"); }
            });
            return Results.Accepted();
        });

        podcasts.MapGet("/{id:guid}/art", async (Guid id, HttpContext context, IPodcastStore store, PodcastHttp http, PodcastLocations locations, CancellationToken ct) =>
        {
            var show = await store.GetShowAsync(id, ct);
            return show is null ? Results.NotFound() : await ServeArtAsync(context, show.ImageUrl, http, locations, ct);
        });

        podcasts.MapGet("/{id:guid}/episodes", async (Guid id, string? sort, string? filter, string? q, int? season, int? offset, int? limit,
            HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var view = await ViewAsync(context, store, downloads, ct);
            if (view.Show(id) is null) return Results.NotFound(new { error = MissingShow });
            IEnumerable<PodcastEpisode> episodes = await store.GetEpisodesAsync(id, ct);
            episodes = (filter?.Trim().ToLowerInvariant()) switch
            {
                "unplayed" => episodes.Where(e => !view.IsPlayed(e.Id)),
                "downloaded" => episodes.Where(e => e.Download?.IsDone == true),
                "inprogress" => episodes.Where(e => view.IsInProgress(e.Id)),
                "bookmarked" => episodes.Where(e => view.BookmarkCount(e.Id) > 0),
                _ => episodes
            };
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                episodes = episodes.Where(e => e.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                    || e.DescriptionText.Contains(term, StringComparison.CurrentCultureIgnoreCase));
            }
            var list = episodes.ToList();
            var seasons = list.Where(e => e.Season is not null).Select(e => e.Season!.Value).Distinct().Order().ToList();
            if (season is { } wanted) list = [.. list.Where(e => e.Season == wanted)];
            var order = (sort ?? view.MySettings(id).Sort)?.Trim().ToLowerInvariant();
            list = order == "oldest"
                ? [.. list.OrderBy(e => e.PublishedAt ?? e.FirstSeenAt).ThenBy(e => e.EpisodeNumber)]
                : [.. list.OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt).ThenByDescending(e => e.EpisodeNumber)];
            var skip = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
            return Results.Ok(new { items = list.Skip(skip).Take(take).Select(view.Episode), total = list.Count, seasons });
        });

        podcasts.MapPost("/{id:guid}/played", async (Guid id, PodcastPlayedAllInput input, HttpContext context, IPodcastStore store, CancellationToken ct) =>
        {
            if (await store.GetShowAsync(id, ct) is null) return Results.NotFound(new { error = MissingShow });
            var userKey = UserKey(context);
            var episodes = (await store.GetEpisodesAsync(id, ct))
                .Where(e => input.Before is not { } before || (e.PublishedAt ?? e.FirstSeenAt) < before)
                .ToList();
            var ids = episodes.Select(e => e.Id).ToHashSet();
            var existing = (await store.GetProgressAsync(userKey, ct)).Where(p => ids.Contains(p.EpisodeId)).ToDictionary(p => p.EpisodeId);
            var now = DateTimeOffset.UtcNow;
            if (input.Played)
            {
                await store.SetProgressManyAsync([.. episodes
                    .Where(e => existing.GetValueOrDefault(e.Id) is not { Played: true })
                    .Select(e => new PodcastProgress(userKey, e.Id, existing.GetValueOrDefault(e.Id)?.PositionSeconds ?? 0, e.DurationSeconds, true, now, now))], ct);
                var queue = (await store.GetQueueAsync(userKey, ct)).Select(q => q.EpisodeId).ToList();
                if (queue.RemoveAll(ids.Contains) > 0) await store.SetQueueAsync(userKey, queue, ct);
            }
            else
            {
                await store.DeleteProgressManyAsync(userKey, existing.Keys.ToList(), ct);
            }
            return Results.NoContent();
        });
    }

    // MARK: - Home and Fill my drive

    private static void MapHome(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/home", async (HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var view = await ViewAsync(context, store, downloads, ct);
            var userKey = UserKey(context);
            var now = DateTimeOffset.UtcNow;
            var subscribed = view.SubscribedIds.ToList();

            var queue = await EpisodesInOrderAsync(store, view.Queue, ct);
            var inProgress = view.AllProgress.Where(p => !p.Played && p.PositionSeconds > 0).OrderByDescending(p => p.UpdatedAt).Take(RowSize).ToList();
            var continuing = await EpisodesInOrderAsync(store, inProgress.Select(p => p.EpisodeId).ToList(), ct);
            var recent = await store.GetEpisodesForShowsAsync(subscribed, now.AddDays(-60), ct);
            var fresh = recent.Where(e => !e.Archived && !view.IsPlayed(e.Id) && (e.PublishedAt ?? e.FirstSeenAt) >= now.AddDays(-NewEpisodeDays))
                .OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt).Take(50);
            var shortOnes = recent.Where(e => !view.IsPlayed(e.Id) && PodcastService.CurrentDuration(e) is > 0 and < ShortEpisodeSeconds)
                .OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt).Take(RowSize);
            var downloadedIds = (await store.GetDownloadsAsync(ct)).Where(d => d.IsDone).Select(d => d.EpisodeId).ToList();
            var downloaded = (await store.GetEpisodesAsync(downloadedIds, ct)).Where(e => !view.IsPlayed(e.Id))
                .OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt).Take(50);
            var bookmarks = (await store.GetBookmarksAsync(userKey, ct)).Take(10).ToList();
            var bookmarkEpisodes = (await store.GetEpisodesAsync(bookmarks.Select(b => b.EpisodeId), ct)).ToDictionary(e => e.Id);
            var saved = (await store.GetListeningAsync(userKey, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct)).Sum(l => l.SavedSeconds);

            return Results.Ok(new
            {
                upNext = queue.Select(view.Episode),
                continueListening = continuing.Select(view.Episode),
                newEpisodes = fresh.Select(view.Episode),
                shortOnes = shortOnes.Select(view.Episode),
                downloaded = downloaded.Select(view.Episode),
                subscriptions = view.Shows.Where(s => view.IsSubscribed(s.Id)).OrderByDescending(s => s.NewestEpisodeAt ?? s.CreatedAt).Select(view.Podcast),
                bookmarks = bookmarks.Select(b => view.Bookmark(b, bookmarkEpisodes.GetValueOrDefault(b.EpisodeId))),
                timeSavedSeconds = Math.Round(saved)
            });
        });

        // Unplayed or part-played episodes from the shows this person follows (Up Next first,
        // then newest) that fit in the time, counting only what is left of each.
        podcasts.MapGet("/fill", async (int? minutes, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var budget = Math.Clamp(minutes ?? 30, 1, 24 * 60) * 60.0;
            var view = await ViewAsync(context, store, downloads, ct);
            var queued = await EpisodesInOrderAsync(store, view.Queue, ct);
            var recent = (await store.GetEpisodesForShowsAsync(view.SubscribedIds, DateTimeOffset.UtcNow.AddDays(-90), ct))
                .Where(e => !view.InQueue(e.Id) && !e.Archived)
                .OrderByDescending(e => e.PublishedAt ?? e.FirstSeenAt);
            var picked = new List<PodcastEpisode>();
            var total = 0.0;
            foreach (var episode in queued.Concat(recent))
            {
                if (view.IsPlayed(episode.Id)) continue;
                var remaining = view.RemainingSeconds(episode);
                if (remaining <= 0 || total + remaining > budget) continue;
                picked.Add(episode);
                total += remaining;
            }
            return Results.Ok(new { episodes = picked.Select(view.Episode), totalSeconds = Math.Round(total) });
        });
    }

    private static async Task<List<PodcastEpisode>> EpisodesInOrderAsync(IPodcastStore store, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var found = (await store.GetEpisodesAsync(ids, ct)).ToDictionary(e => e.Id);
        return [.. ids.Where(found.ContainsKey).Select(id => found[id])];
    }

    // MARK: - Up Next

    private static void MapQueue(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/queue", async (HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
            await QueueAsync(context, store, downloads, ct));

        podcasts.MapPost("/queue", async (PodcastQueueAddInput input, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            if (await store.GetEpisodeAsync(input.EpisodeId, ct) is null) return Results.NotFound(new { error = MissingEpisode });
            var position = input.Position?.Trim().ToLowerInvariant() ?? "last";
            if (position is not ("next" or "last")) return Results.BadRequest(new { error = "Position is next or last." });
            var userKey = UserKey(context);
            var queue = (await store.GetQueueAsync(userKey, ct)).Select(q => q.EpisodeId).ToList();
            queue.Remove(input.EpisodeId);
            if (position == "next") queue.Insert(0, input.EpisodeId);
            else queue.Add(input.EpisodeId);
            await store.SetQueueAsync(userKey, queue, ct);
            return await QueueAsync(context, store, downloads, ct);
        });

        podcasts.MapPut("/queue", async (PodcastQueueSetInput input, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var known = (await store.GetEpisodesAsync(input.EpisodeIds ?? [], ct)).Select(e => e.Id).ToHashSet();
            await store.SetQueueAsync(UserKey(context), [.. (input.EpisodeIds ?? []).Where(known.Contains).Distinct()], ct);
            return await QueueAsync(context, store, downloads, ct);
        });

        podcasts.MapDelete("/queue/{episodeId:guid}", async (Guid episodeId, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var userKey = UserKey(context);
            var queue = (await store.GetQueueAsync(userKey, ct)).Select(q => q.EpisodeId).ToList();
            if (queue.Remove(episodeId)) await store.SetQueueAsync(userKey, queue, ct);
            return await QueueAsync(context, store, downloads, ct);
        });
    }

    private static async Task<IResult> QueueAsync(HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct)
    {
        var view = await ViewAsync(context, store, downloads, ct);
        return Results.Ok((await EpisodesInOrderAsync(store, view.Queue, ct)).Select(view.Episode));
    }

    // MARK: - Episodes and listening

    private static void MapEpisodes(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/episodes/{id:guid}", async (Guid id, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Ok(view.EpisodeDetail(episode));
        });

        podcasts.MapPost("/episodes/{id:guid}/progress", async (Guid id, PodcastProgressInput input, HttpContext context, IPodcastStore store,
            PodcastService service, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var progress = await service.ReportProgressAsync(UserKey(context), episode, input, ct);
            return JsonOrNull(PodcastView.Progress(progress, episode));
        });

        podcasts.MapPost("/episodes/{id:guid}/played", async (Guid id, PodcastPlayedInput input, HttpContext context, IPodcastStore store,
            PodcastService service, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var progress = await service.SetPlayedAsync(UserKey(context), episode, input.Played, ct);
            return JsonOrNull(PodcastView.Progress(progress, episode));
        });

        podcasts.MapGet("/episodes/{id:guid}/art", async (Guid id, HttpContext context, IPodcastStore store, PodcastHttp http, PodcastLocations locations, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound();
            var show = await store.GetShowAsync(episode.PodcastId, ct);
            var result = episode.ImageUrl is not null ? await ServeArtAsync(context, episode.ImageUrl, http, locations, ct) : null;
            return result is not null && result is not IStatusCodeHttpResult { StatusCode: 404 }
                ? result
                : await ServeArtAsync(context, show?.ImageUrl, http, locations, ct);
        });

        podcasts.MapGet("/episodes/{id:guid}/audio", async (Guid id, int? download, HttpContext context, IPodcastStore store, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var show = await store.GetShowAsync(episode.PodcastId, ct);
            return await ServeAudioAsync(context, episode, show, download == 1, clients, ct);
        });

        podcasts.MapPost("/episodes/{id:guid}/download", async (Guid id, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            if (await store.GetEpisodeAsync(id, ct) is null) return Results.NotFound(new { error = MissingEpisode });
            await downloads.RequestAsync(id, transcribeAfter: false, ct);
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Ok(view.Episode((await store.GetEpisodeAsync(id, ct))!));
        });

        podcasts.MapDelete("/episodes/{id:guid}/download", async (Guid id, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var view = await ViewAsync(context, store, downloads, ct);
            var lastFollower = view.Followers(episode.PodcastId) == 0 || view.IsSubscribed(episode.PodcastId) && view.Followers(episode.PodcastId) == 1;
            if (!IsAdmin(context) && !lastFollower)
                return Results.Json(new { error = "Other people follow this show, so only an admin can delete its downloads." }, statusCode: StatusCodes.Status403Forbidden);
            await downloads.DeleteAsync(id, ct);
            view = await ViewAsync(context, store, downloads, ct);
            return Results.Ok(view.Episode((await store.GetEpisodeAsync(id, ct))!));
        });

        podcasts.MapGet("/episodes/{id:guid}/chapters", async (Guid id, IPodcastStore store, PodcastService service, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var timeline = episode.Timeline;
            var original = await service.ChaptersAsync(episode, ct);
            var duration = PodcastService.CurrentDuration(episode);
            var chapters = PodcastService.WithEnds(original.Select(c => c with
            {
                StartSeconds = timeline.ToFile(c.StartSeconds),
                EndSeconds = timeline.ToFile(c.EndSeconds)
            }), duration);
            return Results.Ok(chapters.Select(c => new
            {
                startSeconds = Math.Round(c.StartSeconds, 2),
                endSeconds = c.EndSeconds is { } end ? Math.Round(end, 2) : (double?)null,
                title = c.Title,
                imageUrl = c.ImageUrl,
                url = c.Url
            }));
        });
    }

    // MARK: - Transcripts

    private static void MapTranscripts(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/episodes/{id:guid}/transcript", async (Guid id, IPodcastStore store, PodcastService service, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var transcript = await service.TranscriptAsync(episode, ct);
            if (transcript is null) return Results.NotFound(new { error = "This episode has no transcript yet." });
            var timeline = episode.Timeline;
            return Results.Ok(new
            {
                source = transcript.Source,
                language = transcript.Language,
                segments = transcript.Segments.Select(s => new
                {
                    start = Math.Round(timeline.ToFile(s.Start), 2),
                    end = Math.Round(timeline.ToFile(s.End), 2),
                    speaker = s.Speaker,
                    text = s.Text
                })
            });
        });

        podcasts.MapPost("/episodes/{id:guid}/transcribe", async (Guid id, IPodcastStore store, PodcastService service, CancellationToken ct) =>
        {
            if (await store.GetEpisodeAsync(id, ct) is null) return Results.NotFound(new { error = MissingEpisode });
            var problem = await service.TranscribeAsync(id, ct);
            return problem is null
                ? Results.Json(new { state = "queued" }, statusCode: StatusCodes.Status202Accepted)
                : Results.BadRequest(new { error = problem });
        });

        podcasts.MapGet("/transcripts/search", async (string? q, IPodcastStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "Type the words to look for." });
            var hits = await store.SearchTranscriptsAsync(q, 50, ct);
            var episodes = (await store.GetEpisodesAsync(hits.Select(h => h.EpisodeId), ct)).ToDictionary(e => e.Id);
            var shows = (await store.GetShowsAsync(ct)).ToDictionary(s => s.Id);
            var words = SearchWords(q);
            var results = new List<object>();
            foreach (var hit in hits.Where(h => episodes.ContainsKey(h.EpisodeId)))
            {
                var episode = episodes[hit.EpisodeId];
                results.Add(new
                {
                    episodeId = episode.Id,
                    episodeTitle = episode.Title,
                    podcastId = episode.PodcastId,
                    podcastTitle = shows.GetValueOrDefault(episode.PodcastId)?.Title,
                    startSeconds = Math.Round(episode.Timeline.ToFile(await PreciseStartAsync(store, hit, words, ct)), 1),
                    snippet = hit.Snippet
                });
            }
            return Results.Ok(results);
        });
    }

    // The index holds a few segments per row, so a hit says where that block starts. Playing from
    // there can be half a minute early; this finds the segment inside the block that actually has
    // the words (the first one with most of them), so tapping a result lands on them.
    private static async Task<double> PreciseStartAsync(IPodcastStore store, PodcastTranscriptHit hit, IReadOnlyList<string> words, CancellationToken ct)
    {
        if (words.Count == 0 || await store.GetTranscriptAsync(hit.EpisodeId, ct) is not { } transcript) return hit.StartSeconds;
        var best = hit.StartSeconds;
        var bestScore = 0;
        foreach (var segment in transcript.Segments.Where(s => s.Start >= hit.StartSeconds - 0.01).Take(40))
        {
            var text = SearchWords(segment.Text);
            var score = words.Select((word, index) => index == words.Count - 1
                ? text.Any(t => t.StartsWith(word, StringComparison.Ordinal))
                : text.Contains(word)).Count(found => found);
            if (score > bestScore)
            {
                (best, bestScore) = (segment.Start, score);
                if (score == words.Count) break;
            }
        }
        return best;
    }

    private static List<string> SearchWords(string text)
    {
        var folded = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            folded.Append(char.IsLetterOrDigit(c) || c == '\'' ? char.ToLowerInvariant(c) : ' ');
        }
        return folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    // MARK: - Bookmarks

    private static void MapBookmarks(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/bookmarks", async (Guid? episodeId, HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var view = await ViewAsync(context, store, downloads, ct);
            var bookmarks = (await store.GetBookmarksAsync(UserKey(context), ct)).Where(b => episodeId is null || b.EpisodeId == episodeId).ToList();
            var episodes = (await store.GetEpisodesAsync(bookmarks.Select(b => b.EpisodeId), ct)).ToDictionary(e => e.Id);
            return Results.Ok(bookmarks.Select(b => view.Bookmark(b, episodes.GetValueOrDefault(b.EpisodeId))));
        });

        podcasts.MapPost("/episodes/{id:guid}/bookmarks", async (Guid id, PodcastBookmarkInput input, HttpContext context, IPodcastStore store,
            PodcastService service, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            var position = episode.Timeline.ToOriginal(Math.Max(0, input.PositionSeconds));
            string? snippet = null;
            try
            {
                if (await service.TranscriptAsync(episode, ct) is { } transcript)
                    snippet = PodcastTranscriptParser.Snippet(transcript, position - 15, position + 15);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { }
            var now = DateTimeOffset.UtcNow;
            var bookmark = new PodcastBookmark(Guid.NewGuid(), UserKey(context), id, position, input.Note?.Trim() ?? "", snippet, now, now);
            await store.SaveBookmarkAsync(bookmark, ct);
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Created($"/api/podcasts/bookmarks/{bookmark.Id}", view.Bookmark(bookmark, episode));
        });

        podcasts.MapPut("/bookmarks/{id:guid}", async (Guid id, PodcastBookmarkNoteInput input, HttpContext context, IPodcastStore store,
            PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var bookmark = await store.GetBookmarkAsync(id, ct);
            if (bookmark is null || bookmark.UserKey != UserKey(context)) return Results.NotFound(new { error = "That bookmark no longer exists." });
            bookmark = bookmark with { Note = input.Note?.Trim() ?? "", UpdatedAt = DateTimeOffset.UtcNow };
            await store.SaveBookmarkAsync(bookmark, ct);
            var view = await ViewAsync(context, store, downloads, ct);
            return Results.Ok(view.Bookmark(bookmark, await store.GetEpisodeAsync(bookmark.EpisodeId, ct)));
        });

        podcasts.MapDelete("/bookmarks/{id:guid}", async (Guid id, HttpContext context, IPodcastStore store, CancellationToken ct) =>
        {
            var bookmark = await store.GetBookmarkAsync(id, ct);
            if (bookmark is not null && bookmark.UserKey == UserKey(context)) await store.DeleteBookmarkAsync(id, ct);
            return Results.NoContent();
        });

        // An Organizer note in the person's Personal space (Family when login is off), titled
        // "<Podcast> — <Episode>", with the time, their note, the transcript around it and a link.
        podcasts.MapPost("/bookmarks/{id:guid}/to-note", async (Guid id, HttpContext context, IPodcastStore store, AppSettingsFile settings,
            IOrganizerStore organizer, OrganizerPushNudger nudger, CancellationToken ct) =>
        {
            if (!(await OrganizerEndpoints.ReadOptionsAsync(settings, ct)).Enabled)
                return Results.NotFound(new { error = "The Organizer is turned off in Settings." });
            var bookmark = await store.GetBookmarkAsync(id, ct);
            if (bookmark is null || bookmark.UserKey != UserKey(context)) return Results.NotFound(new { error = "That bookmark no longer exists." });
            var episode = await store.GetEpisodeAsync(bookmark.EpisodeId, ct);
            var show = episode is null ? null : await store.GetShowAsync(episode.PodcastId, ct);

            var user = OrganizerEndpoints.UserOf(context);
            var space = user.Username is { } username ? OrganizerSpaceIds.Personal(username) : OrganizerSpaceIds.Family;
            var at = episode is null ? bookmark.PositionSeconds : episode.Timeline.ToFile(bookmark.PositionSeconds);
            var noteId = $"podcast-{Guid.NewGuid():N}";
            var fields = new JsonObject
            {
                ["spaceId"] = space,
                ["title"] = $"{show?.Title ?? "Podcast"} — {episode?.Title ?? "Episode"}",
                ["body"] = NoteBody(bookmark, episode, at),
                ["tags"] = new JsonArray("podcast")
            };
            var link = episode?.Link ?? show?.Link;
            if (link is not null) fields["links"] = new JsonArray(new JsonObject { ["url"] = link, ["title"] = episode?.Title ?? show?.Title });
            var change = new OrganizerChange { Id = noteId, Kind = OrganizerKinds.Note, Fields = fields };
            var clock = OrganizerClock.Server(DateTimeOffset.UtcNow);
            foreach (var (name, _) in fields) change.Clocks[name] = clock;
            var result = await organizer.ApplyChangesAsync(user, [change], null, ct);
            if (result.Rejected.FirstOrDefault() is { } rejection)
                return Results.BadRequest(new { error = $"The Organizer refused the note: {rejection.Error}" });
            nudger.Changed();
            return Results.Ok(new { noteId });
        });
    }

    private static string NoteBody(PodcastBookmark bookmark, PodcastEpisode? episode, double at)
    {
        var body = new StringBuilder();
        body.Append("**At ").Append(PodcastAudioProcessor.Describe(at)).Append("**");
        if (bookmark.Note.Length > 0) body.Append("\n\n").Append(bookmark.Note);
        if (bookmark.TranscriptSnippet is { Length: > 0 } snippet) body.Append("\n\n> ").Append(snippet.Replace("\n", "\n> "));
        if (episode?.Link is { } link) body.Append("\n\n[Episode page](").Append(link).Append(')');
        return body.ToString();
    }

    // MARK: - Clips

    private static void MapClips(RouteGroupBuilder podcasts)
    {
        podcasts.MapPost("/episodes/{id:guid}/clips", async (Guid id, PodcastClipInput input, HttpContext context, IPodcastStore store,
            PodcastAudioProcessor audio, PodcastLocations locations, CancellationToken ct) =>
        {
            var episode = await store.GetEpisodeAsync(id, ct);
            if (episode is null) return Results.NotFound(new { error = MissingEpisode });
            if (double.IsNaN(input.DurationSeconds) || input.DurationSeconds < MinimumClipSeconds || input.DurationSeconds > MaximumClipSeconds)
                return Results.BadRequest(new { error = "A clip is 5 seconds to 3 minutes long." });
            if (input.StartSeconds < 0 || PodcastService.CurrentDuration(episode) is { } length && input.StartSeconds >= length)
                return Results.BadRequest(new { error = "The clip starts outside the episode." });
            if (!await audio.FfmpegAvailableAsync(ct))
                return Results.Json(new { error = "Clips need ffmpeg, and ffmpeg is not installed on the server." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            // The start is in the time the person heard: the downloaded file's when there is one.
            var source = episode.Download is { IsDone: true } done && File.Exists(done.FilePath) ? done.FilePath! : episode.EnclosureUrl;
            Directory.CreateDirectory(locations.ClipsFolder);
            var clipId = Guid.NewGuid();
            var path = Path.Combine(locations.ClipsFolder, $"{clipId:N}.m4a");
            var title = string.IsNullOrWhiteSpace(input.Title) ? episode.Title : input.Title.Trim();
            var error = await audio.CutClipAsync(source, input.StartSeconds, input.DurationSeconds, path, title, ct);
            if (error is not null) return Results.Json(new { error }, statusCode: StatusCodes.Status502BadGateway);

            // A clip asked to run past the end of the episode stops at the end. Record the length of
            // the file that was actually cut, or the clip list says "30 s" for a 15 s clip.
            var cutSeconds = (await audio.ProbeAsync(path, ct)).DurationSeconds;
            var clipSeconds = cutSeconds is > 0 && cutSeconds.Value < input.DurationSeconds - 0.5 ? Math.Round(cutSeconds.Value, 1) : input.DurationSeconds;
            var clip = new PodcastClip(clipId, UserKey(context), id, input.StartSeconds, clipSeconds, input.Title?.Trim() ?? "", path, DateTimeOffset.UtcNow);
            await store.SaveClipAsync(clip, ct);
            return Results.Created($"/api/podcasts/clips/{clipId}.m4a", new { id = clipId, url = ClipUrl(clipId), durationSeconds = clip.DurationSeconds });
        });

        podcasts.MapGet("/clips", async (HttpContext context, IPodcastStore store, CancellationToken ct) =>
        {
            var clips = await store.GetClipsAsync(UserKey(context), ct);
            var episodes = (await store.GetEpisodesAsync(clips.Select(c => c.EpisodeId), ct)).ToDictionary(e => e.Id);
            var shows = (await store.GetShowsAsync(ct)).ToDictionary(s => s.Id);
            return Results.Ok(clips.Select(clip =>
            {
                var episode = episodes.GetValueOrDefault(clip.EpisodeId);
                return new
                {
                    id = clip.Id,
                    url = ClipUrl(clip.Id),
                    episodeId = clip.EpisodeId,
                    episodeTitle = episode?.Title,
                    podcastId = episode?.PodcastId,
                    podcastTitle = episode is null ? null : shows.GetValueOrDefault(episode.PodcastId)?.Title,
                    startSeconds = clip.StartSeconds,
                    durationSeconds = clip.DurationSeconds,
                    title = clip.Title,
                    createdAt = clip.CreatedAt
                };
            }));
        });

        podcasts.MapGet("/clips/{file}", async (string file, IPodcastStore store, CancellationToken ct) =>
        {
            if (!file.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(file[..^4], out var id)) return Results.NotFound();
            var clip = await store.GetClipAsync(id, ct);
            if (clip is null || !File.Exists(clip.FilePath)) return Results.NotFound(new { error = "That clip no longer exists." });
            var name = SafeFileName(string.IsNullOrWhiteSpace(clip.Title) ? "Podcast clip" : clip.Title) + ".m4a";
            return Results.File(clip.FilePath, "audio/mp4", name, enableRangeProcessing: true);
        });

        podcasts.MapDelete("/clips/{id:guid}", async (Guid id, HttpContext context, IPodcastStore store, CancellationToken ct) =>
        {
            var clip = await store.GetClipAsync(id, ct);
            if (clip is null) return Results.NoContent();
            if (clip.UserKey != UserKey(context) && !IsAdmin(context)) return Results.NotFound(new { error = "That clip no longer exists." });
            DeleteQuietly(clip.FilePath);
            await store.DeleteClipAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static string ClipUrl(Guid id) => $"/api/podcasts/clips/{id}.m4a";

    // MARK: - OPML

    private static void MapOpml(RouteGroupBuilder podcasts)
    {
        podcasts.MapGet("/opml", async (HttpContext context, IPodcastStore store, PodcastDownloadManager downloads, CancellationToken ct) =>
        {
            var view = await ViewAsync(context, store, downloads, ct);
            var shows = view.Shows.Where(s => view.IsSubscribed(s.Id)).OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase);
            return Results.File(Encoding.UTF8.GetBytes(Opml(shows)), "text/x-opml", "podcasts.opml");
        });

        podcasts.MapPost("/opml", async (HttpRequest request, HttpContext context, PodcastService service, IHostApplicationLifetime lifetime,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            string text;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8))
            {
                var buffer = new char[5 * 1024 * 1024 + 1];
                var read = await reader.ReadBlockAsync(buffer, ct);
                if (read > 5 * 1024 * 1024) return Results.BadRequest(new { error = "That OPML file is larger than 5 MB." });
                text = new string(buffer, 0, read);
            }
            List<(string Url, string? Title)> outlines;
            try { outlines = ParseOpml(text); }
            catch (XmlException) { return Results.BadRequest(new { error = "That is not an OPML file. Export one from your old podcast app (usually Settings → Export subscriptions)." }); }

            var userKey = UserKey(context);
            var added = 0;
            var existing = 0;
            var failed = new List<object>();
            var toFetch = new List<Guid>();
            foreach (var (raw, title) in outlines)
            {
                var url = PodcastFeedUrl.Normalize(raw);
                if (url is null)
                {
                    failed.Add(new { feedUrl = raw, error = "That is not a feed address." });
                    continue;
                }
                var (show, isNew) = await service.AddUnfetchedAsync(userKey, url, title, ct);
                if (isNew) added++;
                else existing++;
                if (show.LastRefreshedAt is null) toFetch.Add(show.Id);
            }

            _ = Task.Run(async () =>
            {
                foreach (var id in toFetch.Distinct())
                {
                    try { await service.RefreshAsync(id, lifetime.ApplicationStopping); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { loggers.CreateLogger("SentinelCore.Web.PodcastEndpoints").LogWarning(ex, "OPML feed {Id} failed", id); }
                }
            });
            return Results.Ok(new { added, existing, failed });
        });
    }

    public static string Opml(IEnumerable<PodcastShow> shows)
    {
        var body = new XElement("body", shows.Select(show =>
        {
            var outline = new XElement("outline",
                new XAttribute("type", "rss"),
                new XAttribute("text", show.Title),
                new XAttribute("title", show.Title),
                new XAttribute("xmlUrl", show.FeedUrl));
            if (show.Link is not null) outline.Add(new XAttribute("htmlUrl", show.Link));
            return outline;
        }));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("opml", new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", "Podcasts from SentinelCore"),
                    new XElement("dateCreated", DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture))),
                body));
        using var writer = new Utf8StringWriter();
        document.Save(writer);
        return writer.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    // Every outline with an xmlUrl, however deeply the exporting app nested its folders.
    public static List<(string Url, string? Title)> ParseOpml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(PodcastFeedParser.Repair(text)), settings);
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName.Equals("opml", StringComparison.OrdinalIgnoreCase) != true)
            throw new XmlException("not OPML");
        return [.. document.Descendants()
            .Where(e => e.Name.LocalName.Equals("outline", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Url: (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("xmlUrl", StringComparison.OrdinalIgnoreCase)),
                          Title: (string?)e.Attribute("title") ?? (string?)e.Attribute("text")))
            .Where(o => !string.IsNullOrWhiteSpace(o.Url))
            .Select(o => (o.Url!.Trim(), o.Title))
            .DistinctBy(o => o.Item1)];
    }

    // MARK: - Files

    // The show's or episode's art, fetched once into the art cache. The URL carries a version
    // (PodcastView), so a day's caching is safe.
    private static async Task<IResult> ServeArtAsync(HttpContext context, string? imageUrl, PodcastHttp http, PodcastLocations locations, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return Results.NotFound();
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl)), 0, 16).ToLowerInvariant();
        var path = Path.Combine(locations.ArtFolder, name + ".img");
        if (!File.Exists(path))
        {
            var bytes = await http.GetSmallAsync(imageUrl, ct);
            if (bytes is null || ImageType(bytes) is null) return Results.NotFound();
            Directory.CreateDirectory(locations.ArtFolder);
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, path, overwrite: true);
        }
        var head = new byte[16];
        await using (var file = File.OpenRead(path)) _ = await file.ReadAsync(head, ct);
        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(path, ImageType(head) ?? "image/jpeg");
    }

    private static string? ImageType(byte[] bytes) => bytes switch
    {
        [0xFF, 0xD8, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0x47, 0x49, 0x46, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null
    };

    // The downloaded file when there is one (with ranges), otherwise the publisher's file relayed
    // with the player's Range passed through - a player never needs the publisher's address.
    private static async Task<IResult> ServeAudioAsync(HttpContext context, PodcastEpisode episode, PodcastShow? show, bool download,
        IHttpClientFactory clients, CancellationToken ct)
    {
        if (episode.Download is { IsDone: true } done && File.Exists(done.FilePath))
        {
            var name = download ? DownloadName(show, episode, Path.GetExtension(done.FilePath)) : null;
            return Results.File(done.FilePath!, done.FileMimeType ?? "audio/mpeg", name, enableRangeProcessing: true);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, episode.EnclosureUrl);
        if (context.Request.Headers.Range.ToString() is { Length: > 0 } range) request.Headers.TryAddWithoutValidation("Range", range);
        if (context.Request.Headers.IfRange.ToString() is { Length: > 0 } ifRange) request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        HttpResponseMessage upstream;
        try
        {
            upstream = await clients.CreateClient(PodcastDownloadManager.HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            request.Dispose();
            return Results.Json(new { error = $"The publisher's server could not be reached: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
        }
        if (!upstream.IsSuccessStatusCode && upstream.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var status = (int)upstream.StatusCode;
            upstream.Dispose();
            request.Dispose();
            return Results.Json(new { error = $"The publisher's server answered {status} for this episode." }, statusCode: StatusCodes.Status502BadGateway);
        }
        var extension = Path.GetExtension(new Uri(episode.EnclosureUrl).AbsolutePath);
        return new RelayResult(upstream, request, episode.MimeType ?? PodcastFeedParser.GuessMimeType(episode.EnclosureUrl),
            download ? DownloadName(show, episode, string.IsNullOrEmpty(extension) ? ".mp3" : extension) : null);
    }

    private sealed class RelayResult(HttpResponseMessage upstream, HttpRequestMessage request, string fallbackType, string? downloadName) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using (request)
            using (upstream)
            {
                var response = context.Response;
                response.StatusCode = (int)upstream.StatusCode;
                var headers = upstream.Content.Headers;
                var type = headers.ContentType?.MediaType;
                // Plenty of hosts label MP3s application/octet-stream; players want the real type.
                response.ContentType = type is not null && (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    ? type
                    : fallbackType;
                if (headers.ContentLength is { } length) response.ContentLength = length;
                if (headers.ContentRange is { } contentRange) response.Headers.ContentRange = contentRange.ToString();
                response.Headers.AcceptRanges = upstream.Headers.AcceptRanges.Count > 0 ? string.Join(",", upstream.Headers.AcceptRanges) : "bytes";
                if (headers.LastModified is { } modified) response.Headers.LastModified = modified.ToString("R", CultureInfo.InvariantCulture);
                if (upstream.Headers.ETag is { } etag) response.Headers.ETag = etag.ToString();
                if (downloadName is not null)
                    response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = downloadName, FileName = "\"" + Ascii(downloadName) + "\"" }.ToString();
                try
                {
                    await using var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
                    await body.CopyToAsync(response.Body, context.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
                {
                    // The player seeked or closed; nothing to tell anyone.
                }
            }
        }
    }

    private static string DownloadName(PodcastShow? show, PodcastEpisode episode, string extension) =>
        SafeFileName($"{show?.Title ?? "Podcast"} - {episode.Title}") + extension;

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '"', '?', '*', '<', '>', '|']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length > 120 ? clean[..120].Trim() : clean.Length == 0 ? "episode" : clean;
    }

    private static string Ascii(string text) => new(text.Select(c => c is >= ' ' and <= '~' && c != '"' ? c : '_').ToArray());

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
