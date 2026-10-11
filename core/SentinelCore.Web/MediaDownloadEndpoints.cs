
namespace SentinelCore.Web;

// "+ Add" on the Movies tab. Contract: core/docs/MOVIE_DOWNLOADS.md.
public static class MediaDownloadEndpoints
{
    private const string OffMessage = "Movie downloads are turned off. An admin can turn them on under Settings -> MOVIE DOWNLOADS.";
    private const string NotAllowedMessage = "Only an admin can add movies and shows. An admin can let everyone under Settings -> MOVIE DOWNLOADS.";
    // How long a site-list request waits for the services before telling the app to try again,
    // so no request outlives a phone's HTTP timeout.
    private static readonly TimeSpan SitesWait = TimeSpan.FromSeconds(25);

    public static void MapMediaDownloadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/media/downloads").RequireAuthorization();

        // Answers while off, like /api/media/enabled, so apps can decide whether to show "+ Add".
        group.MapGet("/status", async (HttpContext context, MediaDownloadManager manager, AppSettingsFile settings, IMediaStore store, TmdbClient tmdb, CancellationToken ct) =>
        {
            var options = await manager.OptionsAsync(ct);
            var media = await MediaEndpoints.ReadOptionsAsync(settings, ct);
            var libraries = await store.GetLibrariesAsync(ct);
            var status = await manager.StatusAsync(ct);
            return Results.Ok(new
            {
                enabled = options.Enabled && media.Enabled,
                canDownload = options.Enabled && media.Enabled && CanDownload(context, options),
                canManageSites = options.Enabled && media.Enabled && IsAdmin(context),
                tmdbConfigured = await tmdb.ApiKeyAsync(ct) is not null,
                movieLibrary = libraries.Any(l => l.Kind == MediaLibraryKinds.Movies && !MediaNetworkPath.IsNetwork(l.Path)),
                showLibrary = libraries.Any(l => l.Kind == MediaLibraryKinds.Shows && !MediaNetworkPath.IsNetwork(l.Path)),
                status
            });
        });

        var enabled = group.MapGroup("").AddEndpointFilter(async (invocation, next) =>
        {
            var services = invocation.HttpContext.RequestServices;
            var ct = invocation.HttpContext.RequestAborted;
            var options = await MediaDownloadManager.ReadOptionsAsync(services.GetRequiredService<AppSettingsFile>(), ct);
            var media = await MediaEndpoints.ReadOptionsAsync(services.GetRequiredService<AppSettingsFile>(), ct);
            if (!options.Enabled || !media.Enabled) return Results.NotFound(new { error = OffMessage });
            if (!CanDownload(invocation.HttpContext, options)) return Results.Json(new { error = NotAllowedMessage }, statusCode: StatusCodes.Status403Forbidden);
            return await next(invocation);
        });

        MapLookup(enabled);
        MapSearch(enabled);
        MapQueue(enabled);
        MapSites(enabled.MapGroup("/sites").RequireAuthorization("AdminOnly"));
    }

    // MARK: - Finding the title (TMDB)

    public const string LookupAnyKind = "any";

    // One list from a TMDB search per kind: titles that are exactly what was typed first (so
    // "bluey" puts the show above "Bluey: The Movie"), then the rest taking turns between kinds in
    // TMDB's own order, which is already best guess first within each.
    public static IEnumerable<(string Kind, TmdbClient.SearchResult Result)> MergeLookups(
        string query, IReadOnlyList<string> kinds, IReadOnlyList<IReadOnlyList<TmdbClient.SearchResult>> found)
    {
        var turns = new List<(string Kind, TmdbClient.SearchResult Result)>();
        var longest = found.Count == 0 ? 0 : found.Max(f => f.Count);
        for (var i = 0; i < longest; i++)
            for (var k = 0; k < kinds.Count; k++)
                if (i < found[k].Count) turns.Add((kinds[k], found[k][i]));
        var typed = TitleKey(query);
        return turns.Where(t => TitleKey(t.Result.Title) == typed).Concat(turns.Where(t => TitleKey(t.Result.Title) != typed));
    }

    // "The Lord of the Rings: Fellowship & Co." -> "lordoftheringsfellowshipandco".
    private static string TitleKey(string title)
    {
        var text = title.Trim().ToLowerInvariant().Replace("&", "and");
        if (text.StartsWith("the ")) text = text[4..];
        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    private static void MapLookup(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/lookup", async (string? q, string? kind, TmdbClient tmdb, IMediaStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "Type a title to search for." });
            if (await tmdb.ApiKeyAsync(ct) is null) return Results.Ok(new { tmdbConfigured = false, results = Array.Empty<object>() });
            // `any` is what a library search that found nothing asks: every kind that can be
            // downloaded, best guesses first. Music joins this list when there is music to download.
            string[] kinds = kind == LookupAnyKind ? [MediaKinds.Movie, MediaKinds.Show]
                : kind == MediaKinds.Show ? [MediaKinds.Show] : [MediaKinds.Movie];
            try
            {
                var query = q.Trim();
                var found = await Task.WhenAll(kinds.Select(k => tmdb.SearchAsync(k, query, null, ct)));
                var owned = (await store.GetItemsAsync(new MediaQuery(Kinds: kinds), ct))
                    .Where(i => i.TmdbId is not null)
                    .GroupBy(i => (i.Kind, i.TmdbId!.Value))
                    .ToDictionary(g => g.Key, g => g.First().Id);
                var ranked = kinds.Length == 1
                    ? found[0].Select(r => (Kind: kinds[0], Result: r))
                    : MergeLookups(query, kinds, found);
                return Results.Ok(new
                {
                    tmdbConfigured = true,
                    results = ranked.Take(20).Select(x => new
                    {
                        kind = x.Kind,
                        tmdbId = x.Result.TmdbId,
                        title = x.Result.Title,
                        year = x.Result.Year,
                        overview = x.Result.Overview,
                        posterPath = x.Result.PosterPath,
                        posterUrl = x.Result.PosterPath is null ? null : TmdbClient.ImageUrl(x.Result.PosterPath, "w342"),
                        inLibrary = owned.ContainsKey((x.Kind, x.Result.TmdbId)),
                        itemId = owned.TryGetValue((x.Kind, x.Result.TmdbId), out var id) ? id : (Guid?)null
                    })
                });
            }
            catch (InvalidOperationException ex) when (ex is TmdbNotConfiguredException or TmdbRejectedKeyException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = "TMDB did not answer: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // A show's seasons and episodes, marking the episodes already in the library.
        enabled.MapGet("/show/{tmdbId:int}", async (int tmdbId, TmdbClient tmdb, IMediaStore store, CancellationToken ct) =>
        {
            try
            {
                var show = await tmdb.GetShowAsync(tmdbId, ct);
                if (show is null) return Results.NotFound(new { error = "TMDB does not know that show." });
                var owned = new HashSet<(int, int)>();
                var ownedShow = (await store.GetItemsAsync(new MediaQuery(Kinds: [MediaKinds.Show]), ct)).FirstOrDefault(s => s.TmdbId == tmdbId);
                if (ownedShow is not null)
                    foreach (var episode in await store.GetItemsAsync(new MediaQuery(Kinds: [MediaKinds.Episode], ParentId: ownedShow.Id), ct))
                        if (episode.SeasonNumber is { } s && episode.EpisodeNumber is { } e) owned.Add((s, e));

                var numbers = show.SeasonNumbers.OrderBy(n => n == 0 ? int.MaxValue : n).ToList();
                var seasons = await Task.WhenAll(numbers.Select(n => tmdb.GetSeasonAsync(tmdbId, n, ct)));
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                return Results.Ok(new
                {
                    tmdbId,
                    title = show.Title,
                    year = show.Year,
                    overview = show.Overview,
                    posterPath = show.PosterPath,
                    posterUrl = show.PosterPath is null ? null : TmdbClient.ImageUrl(show.PosterPath, "w342"),
                    itemId = ownedShow?.Id,
                    seasons = seasons.OfType<TmdbClient.Season>().Select(season => new
                    {
                        number = season.Number,
                        name = season.Name,
                        episodeCount = season.Episodes.Count,
                        ownedCount = season.Episodes.Count(e => owned.Contains((season.Number, e.Number))),
                        episodes = season.Episodes.Select(e => new
                        {
                            number = e.Number,
                            name = e.Name,
                            airDate = e.AirDate,
                            // Not out yet: nothing to find.
                            aired = e.AirDate is null || (DateOnly.TryParse(e.AirDate, out var aired) && aired <= today),
                            inLibrary = owned.Contains((season.Number, e.Number))
                        })
                    })
                });
            }
            catch (InvalidOperationException ex) when (ex is TmdbNotConfiguredException or TmdbRejectedKeyException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = "TMDB did not answer: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    // MARK: - Searching the sites

    private static void MapSearch(RouteGroupBuilder enabled)
    {
        enabled.MapPost("/search", (MediaDownloadSearchInput input, MediaDownloadManager manager) =>
        {
            input.Title = input.Title?.Trim() ?? "";
            if (input.Title.Length == 0) return Results.BadRequest(new { error = "Type a title to search for." });
            if (input.Kind is not (MediaKinds.Movie or MediaKinds.Show)) return Results.BadRequest(new { error = "Kind must be movie or show." });
            if (input.Kind == MediaKinds.Movie) { input.Season = null; input.Episode = null; }
            if (input.Episode is not null && input.Season is null) return Results.BadRequest(new { error = "Choose the season as well as the episode." });
            var search = manager.StartSearch(input);
            return Results.Json(SearchDto(search), statusCode: StatusCodes.Status202Accepted);
        });

        enabled.MapGet("/search/{id:guid}", (Guid id, MediaDownloadManager manager) =>
            manager.GetSearch(id) is { } search
                ? Results.Ok(SearchDto(search))
                : Results.NotFound(new { error = "That search has expired. Search again." }));
    }

    private static object SearchDto(MediaDownloadSearch search)
    {
        var done = search.Phase == MediaDownloadSearch.Done;
        return new
        {
            id = search.Id,
            phase = search.Phase,
            message = search.Phase switch
            {
                MediaDownloadSearch.Connecting => "Connecting to the VPN...",
                MediaDownloadSearch.Searching => "Searching your sites...",
                MediaDownloadSearch.Failed => search.Error,
                _ => search.Results.Count == 0 ? "Nothing found on your sites." : null
            },
            error = search.Error,
            kind = search.Input.Kind,
            title = search.Input.Title,
            year = search.Input.Year,
            season = search.Input.Season,
            episode = search.Input.Episode,
            results = done ? search.Results.Where(r => r.Matches && r.Seeders is not 0).Select(ReleaseDto) : [],
            others = done ? search.Results.Where(r => !r.Matches || r.Seeders is 0).Select(ReleaseDto) : []
        };
    }

    private static object ReleaseDto(MediaRelease release) => new
    {
        id = release.Id,
        title = release.Title,
        site = release.Indexer,
        sizeBytes = release.SizeBytes,
        seeders = release.Seeders,
        leechers = release.Leechers,
        publishedAt = release.PublishedAt,
        quality = release.Quality,
        codec = release.Codec,
        source = release.Source,
        hdr = release.Hdr,
        season = release.Season,
        episode = release.Episode,
        seasonPack = release.SeasonPack,
        matches = release.Matches,
        warning = release.Warning
    };

    // MARK: - The queue

    private static void MapQueue(RouteGroupBuilder enabled)
    {
        enabled.MapGet("", async (MediaDownloadManager manager, CancellationToken ct) =>
        {
            // Albums have their own list on the Music tab.
            var all = (await manager.ListAsync(ct)).Where(d => d.Kind != MediaDownloadKinds.Album).ToList();
            var active = all.Where(d => MediaDownloadStates.IsActive(d.State)).OrderBy(d => d.CreatedAt).ToList();
            var recent = all.Where(d => !MediaDownloadStates.IsActive(d.State)).OrderByDescending(d => d.FinishedAt ?? d.CreatedAt).Take(30).ToList();
            return Results.Ok(new
            {
                status = await manager.StatusAsync(ct),
                active = active.Select(DownloadDto),
                recent = recent.Select(DownloadDto)
            });
        });

        enabled.MapPost("", async (MediaDownloadGrabInput input, HttpContext context, MediaDownloadManager manager, CancellationToken ct) =>
        {
            try
            {
                var download = await manager.GrabAsync(input, context.User.Identity?.Name ?? "", ct, mayReplace: IsAdmin(context));
                return Results.Created($"/api/media/downloads/{download.Id}", DownloadDto(download));
            }
            // The app asks "Replace the one you have?" and sends the same request again with
            // replace true or false.
            catch (MediaDownloadDuplicateException ex)
            {
                return Results.Json(new
                {
                    error = ex.Message,
                    code = "alreadyInLibrary",
                    existing = new
                    {
                        itemId = ex.Existing.Id,
                        title = ex.Existing.Title,
                        year = ex.Existing.Year,
                        sizeBytes = ex.Existing.FileSize,
                        height = ex.Existing.Height,
                        canReplace = ex.CanReplace
                    }
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (MediaDownloadException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        enabled.MapGet("/{id:guid}", async (Guid id, MediaDownloadManager manager, CancellationToken ct) =>
            await manager.GetAsync(id, ct) is { } download ? Results.Ok(DownloadDto(download)) : Results.NotFound(new { error = "That download is gone." }));

        // Cancel and retry belong to whoever asked for the download, and to admins - not to any
        // account that can see the list.
        enabled.MapDelete("/{id:guid}", async (Guid id, HttpContext context, MediaDownloadManager manager, CancellationToken ct) =>
        {
            if (await manager.GetAsync(id, ct) is not { } download) return Results.NotFound(new { error = "That download is gone." });
            if (!MayManage(context, download)) return Results.Json(new { error = NotYoursMessage }, statusCode: StatusCodes.Status403Forbidden);
            return await manager.CancelAsync(id, ct) ? Results.NoContent() : Results.NotFound(new { error = "That download is gone." });
        });

        // "Test VPN": starts everything if needed (up to about a minute), proves the tunnel, and
        // answers in words, with the status for the light.
        enabled.MapPost("/vpn/test", async (MediaDownloadManager manager, CancellationToken ct) =>
        {
            var (ok, message) = await manager.TestVpnAsync(ct);
            return Results.Ok(new { ok, message, status = await manager.StatusAsync(ct) });
        });

        enabled.MapPost("/{id:guid}/retry", async (Guid id, HttpContext context, MediaDownloadManager manager, CancellationToken ct) =>
        {
            if (await manager.GetAsync(id, ct) is not { } existing) return Results.NotFound(new { error = "That download is gone." });
            if (!MayManage(context, existing)) return Results.Json(new { error = NotYoursMessage }, statusCode: StatusCodes.Status403Forbidden);
            return await manager.RetryAsync(id, ct) is { } download ? Results.Ok(DownloadDto(download)) : Results.NotFound(new { error = "That download is gone." });
        });
    }

    public static object DownloadDto(MediaDownload download) => new
    {
        id = download.Id,
        kind = download.Kind,
        title = download.Title,
        year = download.Year,
        tmdbId = download.TmdbId,
        season = download.Season,
        episode = download.Episode,
        // "S02E05", "Season 2", or null for a film: the line under the title.
        subtitle = download.Kind == MediaKinds.Episode
            ? download.Season is { } s ? download.Episode is { } e ? $"S{s:00}E{e:00}" : $"Season {s}" : null
            : download.Kind == MediaDownloadKinds.Album ? download.Artist : null,
        posterUrl = download.ArtUrl ?? (download.PosterPath is null ? null : TmdbClient.ImageUrl(download.PosterPath, "w342")),
        artist = download.Artist,
        releaseTitle = download.ReleaseTitle,
        site = download.Indexer,
        sizeBytes = download.SizeBytes,
        state = download.State,
        progress = Math.Round(download.Progress, 4),
        downloadedBytes = download.DownloadedBytes,
        speedBytesPerSecond = download.SpeedBytesPerSecond,
        etaSeconds = download.EtaSeconds,
        peers = download.Peers,
        error = download.Error,
        libraryId = download.LibraryId,
        itemId = download.ItemId,
        replacesItemId = download.ReplaceItemId,
        requestedBy = download.RequestedBy,
        createdAt = download.CreatedAt,
        completedAt = download.CompletedAt,
        finishedAt = download.FinishedAt
    };

    // MARK: - Sites (admin)

    private static void MapSites(RouteGroupBuilder sites)
    {
        sites.MapGet("", async (MediaDownloadManager manager, ProwlarrClient prowlarr, CancellationToken ct) =>
            await WithServicesAsync(manager, ct, async () => Results.Ok((await prowlarr.SitesAsync(ct)).Select(s => new
            {
                id = s.Id,
                name = s.Name,
                enabled = s.Enabled,
                privacy = s.Privacy,
                language = s.Language,
                description = s.Description,
                definition = s.Definition
            }))));

        sites.MapGet("/catalog", async (string? q, MediaDownloadManager manager, ProwlarrClient prowlarr, CancellationToken ct) =>
            await WithServicesAsync(manager, ct, async () =>
            {
                var catalog = await prowlarr.CatalogAsync(ct);
                var query = q?.Trim() ?? "";
                var matches = catalog.Where(d => query.Length == 0
                    || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (d.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
                return Results.Ok(matches
                    .OrderBy(d => d.Privacy == "public" ? 0 : 1)
                    .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(query.Length == 0 ? 400 : 100)
                    .Select(d => new
                    {
                        definition = d.Definition,
                        name = d.Name,
                        description = d.Description,
                        privacy = d.Privacy,
                        language = d.Language,
                        // A site that needs nothing typed in can be added with one tap.
                        needsSetup = d.Fields.Any(f => f.Type is "text" or "password" && f.Value is null && ProwlarrClient.IsCredential(f.Name)),
                        fields = d.Fields.Select(f => new
                        {
                            name = f.Name,
                            label = f.Label,
                            type = f.Type,
                            help = f.HelpText,
                            value = f.Value,
                            options = f.Options
                        })
                    }));
            }));

        sites.MapPost("", async (MediaDownloadSiteInput input, MediaDownloadManager manager, ProwlarrClient prowlarr, CancellationToken ct) =>
            await WithServicesAsync(manager, ct, async () =>
            {
                if (string.IsNullOrWhiteSpace(input.Definition)) return Results.BadRequest(new { error = "Choose a site to add." });
                var site = await prowlarr.AddSiteAsync(input.Definition, input.Name, input.Fields, ct);
                return Results.Created($"/api/media/downloads/sites/{site.Id}", new { id = site.Id, name = site.Name, enabled = site.Enabled, privacy = site.Privacy });
            }));

        sites.MapPut("/{id:int}", async (int id, MediaDownloadSiteUpdateInput input, MediaDownloadManager manager, ProwlarrClient prowlarr, CancellationToken ct) =>
            await WithServicesAsync(manager, ct, async () =>
            {
                await prowlarr.SetSiteEnabledAsync(id, input.Enabled, ct);
                return Results.Ok(new { id, enabled = input.Enabled });
            }));

        sites.MapDelete("/{id:int}", async (int id, MediaDownloadManager manager, ProwlarrClient prowlarr, CancellationToken ct) =>
            await WithServicesAsync(manager, ct, async () =>
            {
                await prowlarr.DeleteSiteAsync(id, ct);
                return Results.NoContent();
            }));
    }

    // Prowlarr lives inside the VPN, so the site list needs the services up. A request waits a
    // little for them, then answers 503 { starting: true } so the app can say "connecting" and
    // ask again rather than hanging.
    private static async Task<IResult> WithServicesAsync(MediaDownloadManager manager, CancellationToken ct, Func<Task<IResult>> action)
    {
        var ready = manager.EnsureReadyAsync(ct);
        var finished = await Task.WhenAny(ready, Task.Delay(SitesWait, ct));
        if (finished != ready)
        {
            _ = ready.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return Results.Json(new { error = "Connecting to the VPN... try again in a moment.", starting = true }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            await ready;
            return await action();
        }
        catch (MediaDownloadException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        catch (ProwlarrException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    }

    private static bool IsAdmin(HttpContext context) => context.User.IsInRole(nameof(AccountRole.Admin));

    public const string NotYoursMessage = "Only the person who asked for this download, or an admin, can change it.";

    public static bool MayManage(HttpContext context, MediaDownload download) =>
        IsAdmin(context) || string.Equals(download.RequestedBy, context.User.Identity?.Name ?? "", StringComparison.OrdinalIgnoreCase);

    public static bool CanDownload(HttpContext context, MediaDownloadsSettingsDto options) =>
        options.AllowedUsers == "everyone" || IsAdmin(context);
}
