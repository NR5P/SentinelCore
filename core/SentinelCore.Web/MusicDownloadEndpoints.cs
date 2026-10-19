namespace SentinelCore.Web;

// "+ Add" on the Music tab. Contract: core/docs/MUSIC_AND_PODCASTS.md §4 "Getting music". The
// queue, VPN and torrent services are Movies' (MediaDownloadManager); this is the album-shaped
// front of it.
public static class MusicDownloadEndpoints
{
    private const string OffMessage = "Downloads are turned off. An admin can turn them on under Settings -> MOVIE DOWNLOADS.";
    private const string NotAllowedMessage = "Only an admin can add music. An admin can let everyone under Settings -> MOVIE DOWNLOADS.";
    private const string MusicOffMessage = "Music is turned off in Settings.";

    public static void MapMusicDownloadEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/music/downloads").RequireAuthorization();

        // Answers while off, so the app can decide whether to show "+ Add".
        group.MapGet("/status", async (HttpContext context, MediaDownloadManager manager, IMusicAudioSource music, CancellationToken ct) =>
        {
            if (!await music.EnabledAsync(ct)) return Results.NotFound(new { error = MusicOffMessage });
            var options = await manager.OptionsAsync(ct);
            var musicLibrary = await manager.FirstMusicLibraryAsync(ct) is not null;
            return Results.Ok(new
            {
                enabled = options.Enabled,
                canDownload = options.Enabled && musicLibrary && MediaDownloadEndpoints.CanDownload(context, options),
                musicLibrary,
                status = await manager.StatusAsync(ct)
            });
        });

        var enabled = group.MapGroup("").AddEndpointFilter(async (invocation, next) =>
        {
            var services = invocation.HttpContext.RequestServices;
            var ct = invocation.HttpContext.RequestAborted;
            if (!await services.GetRequiredService<IMusicAudioSource>().EnabledAsync(ct)) return Results.NotFound(new { error = MusicOffMessage });
            var options = await MediaDownloadManager.ReadOptionsAsync(services.GetRequiredService<AppSettingsFile>(), ct);
            if (!options.Enabled) return Results.NotFound(new { error = OffMessage });
            if (!MediaDownloadEndpoints.CanDownload(invocation.HttpContext, options))
                return Results.Json(new { error = NotAllowedMessage }, statusCode: StatusCodes.Status403Forbidden);
            return await next(invocation);
        });

        enabled.MapImports(MediaImportList.KindMusic);

        // "Suggested for you" (MusicSuggestions.cs). Downloading one is an imported list
        // (POST /imports), so it needs nothing of its own.
        enabled.MapGet("/suggestions", async (bool? refresh, HttpContext context, MusicSuggestionService suggestions, CancellationToken ct) =>
            Results.Ok(await suggestions.GetAsync(MediaEndpoints.UserKey(context), context.User.Identity?.Name ?? "",
                context.User.IsInRole(nameof(AccountRole.Admin)), refresh == true, ct)));

        enabled.MapPost("/suggestions/dismiss", async (MusicSuggestionDismissInput input, HttpContext context, MusicSuggestionService suggestions, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Artist)) return Results.BadRequest(new { error = "Say which artist." });
            await suggestions.DismissAsync(MediaEndpoints.UserKey(context), input.Artist.Trim(),
                string.IsNullOrWhiteSpace(input.Title) ? null : input.Title.Trim(), input.Dismissed, ct);
            return Results.NoContent();
        });

        enabled.MapGet("/lookup", async (string? q, MusicDownloadLookup lookup, IMusicAudioSource music, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "Type an album or an artist to search for." });
            try
            {
                var found = await lookup.SearchAsync(q.Trim(), ct);
                var results = new List<MusicLookupResult>();
                foreach (var (mbid, title, artist, year, type, tracks) in found)
                    results.Add(new MusicLookupResult(mbid, title, artist, year, type, tracks, MusicDownloadLookup.ArtUrl(mbid),
                        await music.HasAlbumAsync(artist, title, ct)));
                return Results.Ok(results);
            }
            catch (MediaDownloadException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway); }
        });

        enabled.MapPost("/search", (MusicDownloadSearchInput input, MediaDownloadManager manager) =>
        {
            input.Album = input.Album?.Trim() ?? "";
            input.Artist = input.Artist?.Trim() ?? "";
            if (input.Album.Length == 0) return Results.BadRequest(new { error = "Choose the album to search for." });
            var search = manager.StartAlbumSearch(input);
            return Results.Json(SearchDto(search), statusCode: StatusCodes.Status202Accepted);
        });

        enabled.MapGet("/search/{id:guid}", (Guid id, MediaDownloadManager manager) =>
            manager.GetSearch(id) is { Album: not null } search
                ? Results.Ok(SearchDto(search))
                : Results.NotFound(new { error = "That search has expired. Search again." }));

        enabled.MapGet("", async (MediaDownloadManager manager, CancellationToken ct) =>
        {
            var all = (await manager.ListAsync(ct)).Where(d => d.Kind == MediaDownloadKinds.Album).ToList();
            var active = all.Where(d => MediaDownloadStates.IsActive(d.State)).OrderBy(d => d.CreatedAt).ToList();
            var recent = all.Where(d => !MediaDownloadStates.IsActive(d.State)).OrderByDescending(d => d.FinishedAt ?? d.CreatedAt).Take(30).ToList();
            return Results.Ok(new
            {
                status = await manager.StatusAsync(ct),
                active = active.Select(MediaDownloadEndpoints.DownloadDto),
                recent = recent.Select(MediaDownloadEndpoints.DownloadDto)
            });
        });

        enabled.MapPost("", async (MusicDownloadGrabInput input, HttpContext context, MediaDownloadManager manager, CancellationToken ct) =>
        {
            try
            {
                var download = await manager.GrabAlbumAsync(input, context.User.Identity?.Name ?? "", ct);
                return Results.Created($"/api/music/downloads/{download.Id}", MediaDownloadEndpoints.DownloadDto(download));
            }
            // The app asks "Download it again?" and sends the same request with downloadAgain true.
            catch (MediaDownloadAlbumDuplicateException ex)
            {
                return Results.Json(new
                {
                    error = ex.Message,
                    code = "alreadyInLibrary",
                    existing = new { title = ex.Album, artist = ex.Artist }
                }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (MediaDownloadException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        enabled.MapGet("/{id:guid}", async (Guid id, MediaDownloadManager manager, CancellationToken ct) =>
            await manager.GetAsync(id, ct) is { Kind: MediaDownloadKinds.Album } download
                ? Results.Ok(MediaDownloadEndpoints.DownloadDto(download))
                : Results.NotFound(new { error = "That download is gone." }));

        enabled.MapDelete("/{id:guid}", async (Guid id, MediaDownloadManager manager, CancellationToken ct) =>
            await manager.GetAsync(id, ct) is { Kind: MediaDownloadKinds.Album } && await manager.CancelAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "That download is gone." }));

        enabled.MapPost("/{id:guid}/retry", async (Guid id, MediaDownloadManager manager, CancellationToken ct) =>
            await manager.GetAsync(id, ct) is { Kind: MediaDownloadKinds.Album } && await manager.RetryAsync(id, ct) is { } download
                ? Results.Ok(MediaDownloadEndpoints.DownloadDto(download))
                : Results.NotFound(new { error = "That download is gone." }));
    }

    private static object SearchDto(MediaDownloadSearch search)
    {
        var done = search.Phase == MediaDownloadSearch.Done;
        var album = search.Album!;
        return new
        {
            id = search.Id,
            phase = search.Phase,
            message = search.Phase switch
            {
                MediaDownloadSearch.Connecting => "Connecting to the VPN...",
                MediaDownloadSearch.Searching => "Searching your sites...",
                MediaDownloadSearch.Failed => search.Error,
                _ => search.AlbumResults.Count == 0 ? "Nothing found on your sites." : null
            },
            error = search.Error,
            artist = album.Artist,
            album = album.Album,
            year = album.Year,
            results = done ? search.AlbumResults.Where(r => r.Matches && r.Seeders is not 0).Select(ReleaseDto) : [],
            others = done ? search.AlbumResults.Where(r => !r.Matches || r.Seeders is 0).Select(ReleaseDto) : []
        };
    }

    private static object ReleaseDto(MusicRelease release) => new
    {
        id = release.Id,
        title = release.Title,
        site = release.Indexer,
        sizeBytes = release.SizeBytes,
        seeders = release.Seeders,
        leechers = release.Leechers,
        publishedAt = release.PublishedAt,
        format = release.Format,
        lossless = release.Lossless,
        matches = release.Matches,
        warning = release.Warning
    };
}
