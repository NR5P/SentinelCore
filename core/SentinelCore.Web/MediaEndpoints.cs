using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Web;

// The Movies tab's HTTP surface - the contract is core/docs/MOVIES.md, and every client (web,
// iOS/macOS, tvOS, Android, Windows) is built against that file rather than against this one.
public static class MediaEndpoints
{
    private const int DefaultPageSize = 60;
    private const int MaxPageSize = 500;
    private const int ShelfSize = 20;
    private const string OffMessage = "Movies & TV is turned off in Settings.";

    public static void MapMediaEndpoints(this WebApplication app)
    {
        var media = app.MapGroup("/api/media").RequireAuthorization();

        // Both of these answer while the feature is off, so every client can decide whether to
        // show the tab without treating "off" as an error.
        media.MapGet("/enabled", async (AppSettingsFile settings, CancellationToken ct) =>
            Results.Ok(new { enabled = (await ReadOptionsAsync(settings, ct)).Enabled }));

        media.MapGet("/status", async (AppSettingsFile settings, IMediaStore store, MediaScanner scanner, TmdbClient tmdb, CancellationToken ct) =>
        {
            var options = await ReadOptionsAsync(settings, ct);
            var libraries = await store.GetLibrariesAsync(ct);
            var (movies, shows, episodes) = await store.CountsAsync(ct);
            var progress = scanner.Progress;
            return Results.Ok(new
            {
                enabled = options.Enabled,
                ffmpegAvailable = await scanner.FfmpegAvailableAsync(ct),
                tmdbConfigured = await tmdb.ApiKeyAsync(ct) is not null,
                scanning = scanner.IsScanning,
                scan = progress is null ? null : new
                {
                    phase = progress.Phase,
                    libraryName = progress.LibraryName,
                    processed = progress.Processed,
                    total = progress.Total,
                    startedAt = progress.StartedAt
                },
                libraries = libraries.Select(LibraryDto),
                movieCount = movies,
                showCount = shows,
                episodeCount = episodes,
                lastScanFinishedAt = scanner.LastFinishedAt ?? libraries.Max(l => l.LastScanFinishedAt),
                lastScanError = scanner.LastError
            });
        });

        var enabled = media.MapGroup("").AddEndpointFilter(async (invocation, next) =>
        {
            var settings = invocation.HttpContext.RequestServices.GetRequiredService<AppSettingsFile>();
            var options = await ReadOptionsAsync(settings, invocation.HttpContext.RequestAborted);
            return options.Enabled ? await next(invocation) : Results.NotFound(new { error = OffMessage });
        });

        MapLibraries(enabled);
        MapBrowsing(enabled);
        MapPlayback(enabled);
        MapMetadata(enabled);
    }

    // MARK: - Libraries

    private static void MapLibraries(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/libraries", async (IMediaStore store, CancellationToken ct) =>
            Results.Ok((await store.GetLibrariesAsync(ct)).Select(LibraryDto)));

        enabled.MapPost("/libraries", async (MediaLibraryInput input, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var errors = await ValidateLibraryAsync(input, null, store, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var library = new MediaLibrary(Guid.NewGuid(), input.Name.Trim(), input.Kind, NormalizePath(input.Path), DateTimeOffset.UtcNow);
            await store.SaveLibraryAsync(library, ct);
            scanner.RequestScan(library.Id);
            return Results.Created($"/api/media/libraries/{library.Id}", LibraryDto(library));
        }).RequireAuthorization("AdminOnly");

        enabled.MapPut("/libraries/{id:guid}", async (Guid id, MediaLibraryInput input, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var existing = await store.GetLibraryAsync(id, ct);
            if (existing is null) return Results.NotFound(new { error = "That library no longer exists." });
            var errors = await ValidateLibraryAsync(input, id, store, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var path = NormalizePath(input.Path);
            var changed = !string.Equals(path, existing.Path, StringComparison.Ordinal) || input.Kind != existing.Kind;
            var updated = existing with { Name = input.Name.Trim(), Kind = input.Kind, Path = path };
            await store.SaveLibraryAsync(updated, ct);
            if (changed) scanner.RequestScan(id);
            return Results.Ok(LibraryDto(updated));
        }).RequireAuthorization("AdminOnly");

        enabled.MapDelete("/libraries/{id:guid}", async (Guid id, IMediaStore store, CancellationToken ct) =>
        {
            await store.DeleteLibraryAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        enabled.MapPost("/libraries/{id:guid}/scan", async (Guid id, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            if (await store.GetLibraryAsync(id, ct) is null) return Results.NotFound(new { error = "That library no longer exists." });
            return scanner.TryStartScan(id)
                ? Results.Accepted()
                : Results.Conflict(new { error = "A scan is already running." });
        }).RequireAuthorization("AdminOnly");

        enabled.MapPost("/scan", (MediaScanner scanner) =>
            scanner.TryStartScan(null)
                ? Results.Accepted()
                : Results.Conflict(new { error = "A scan is already running." }))
            .RequireAuthorization("AdminOnly");

        enabled.MapGet("/browse", (string? path) => Results.Ok(Browse(path))).RequireAuthorization("AdminOnly");
    }

    private static async Task<List<string>> ValidateLibraryAsync(MediaLibraryInput input, Guid? id, IMediaStore store, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name)) errors.Add("Give the library a name, like Movies or Kids TV.");
        if (!MediaLibraryKinds.IsValid(input.Kind)) errors.Add("Kind must be movies or shows.");
        var path = input.Path?.Trim() ?? "";
        if (path.Length == 0) errors.Add("Choose the folder the videos are in.");
        else if (!Path.IsPathRooted(path)) errors.Add("Use the folder's full path, like /mnt/movies/Media/Movies.");
        else if (!Directory.Exists(path))
            errors.Add(OperatingSystem.IsWindows()
                ? $"{path} does not exist on the server. For a folder on another computer, use its network path (\\\\PC\\Movies) or map it as a drive first."
                : $"{path} does not exist on the server. For a folder on another computer, mount it first, for example: sudo mount -t cifs //PC/Movies {path} -o username=YOU,uid={Environment.GetEnvironmentVariable("UID") ?? "1000"}");
        else
        {
            var normalized = NormalizePath(path);
            foreach (var other in await store.GetLibrariesAsync(ct))
                if (other.Id != id && string.Equals(NormalizePath(other.Path), normalized, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{other.Name} already uses that folder.");
        }
        return errors;
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static object Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = OperatingSystem.IsWindows()
                ? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName)
                : new[] { "/mnt", "/media", "/srv", "/data", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/Volumes", "/" }
                    .Where(p => p.Length > 0 && Directory.Exists(p)).Distinct();
            return new { path = "", parent = (string?)null, folders = roots.Select(r => Folder(r, r)).ToList() };
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) return new { path = full, parent = Path.GetDirectoryName(full), folders = new List<object>() };
        var folders = new List<object>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(1000))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.') || name is "$RECYCLE.BIN" or "System Volume Information" || name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase)) continue;
                folders.Add(Folder(name, directory));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new { path = full, parent = Path.GetDirectoryName(full) ?? (string?)null, folders };
    }

    private static object Folder(string name, string path)
    {
        var videos = 0;
        try
        {
            // Capped: a folder on a slow share with ten thousand files is not worth a stall for a hint.
            foreach (var file in Directory.EnumerateFiles(path).Take(2000))
                if (MediaNaming.IsVideoFile(file)) videos++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new { name, path, videoCount = videos };
    }

    // MARK: - Browsing

    private static void MapBrowsing(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/home", async (HttpContext context, IMediaStore store, CancellationToken ct) =>
        {
            var view = await MediaView.LoadAsync(store, UserKey(context), ct);
            var movies = view.Items.Where(i => i.Kind == MediaKinds.Movie).ToList();
            var shows = view.Items.Where(i => i.Kind == MediaKinds.Show).ToList();
            var episodes = view.Items.Where(i => i.Kind == MediaKinds.Episode).ToList();

            var continueWatching = view.ContinueWatching().Take(ShelfSize).ToList();
            var recentMovies = movies.OrderByDescending(m => m.AddedAt).ThenBy(m => m.SortTitle).Take(ShelfSize).ToList();
            var recentShows = shows.OrderByDescending(view.LatestEpisodeAdded).ThenBy(s => s.SortTitle).Take(ShelfSize).ToList();
            var recentEpisodes = episodes
                .OrderByDescending(e => e.AddedAt).ThenBy(e => e.SortTitle)
                .Take(ShelfSize).ToList();

            return Results.Ok(new
            {
                continueWatching = continueWatching.Select(view.Summary),
                recentMovies = recentMovies.Select(view.Summary),
                recentShows = recentShows.Select(view.Summary),
                recentEpisodes = recentEpisodes.Select(view.Summary),
                featured = view.Featured(continueWatching, recentMovies, shows) is { } featured ? view.Summary(featured) : null,
                movieCount = movies.Count,
                showCount = shows.Count,
                libraries = (await store.GetLibrariesAsync(ct)).Select(LibraryDto)
            });
        });

        enabled.MapGet("/items", async (
            HttpContext context, IMediaStore store, string? kind, Guid? libraryId, string? q, string? genre, string? sort, string? order,
            bool? unwatched, int? offset, int? limit, CancellationToken ct) =>
        {
            var kinds = kind switch
            {
                MediaKinds.Movie => new[] { MediaKinds.Movie },
                MediaKinds.Show => new[] { MediaKinds.Show },
                _ => new[] { MediaKinds.Movie, MediaKinds.Show }
            };
            var view = await MediaView.LoadAsync(store, UserKey(context), ct);
            IEnumerable<MediaItem> items = view.Items.Where(i => kinds.Contains(i.Kind) && (libraryId is null || i.LibraryId == libraryId));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = MediaNaming.NormalizeKey(q);
                items = items.Where(i => MediaNaming.NormalizeKey(i.Title).Contains(needle, StringComparison.Ordinal)
                    || i.Title.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(genre))
                items = items.Where(i => i.Genres.Contains(genre.Trim(), StringComparer.OrdinalIgnoreCase));
            if (unwatched == true) items = items.Where(i => !view.IsWatched(i));

            var sortKey = sort?.ToLowerInvariant() ?? "title";
            var descending = order is null ? sortKey is "added" or "rating" or "year" : order.Equals("desc", StringComparison.OrdinalIgnoreCase);
            items = sortKey switch
            {
                "added" => Order(items, i => i.Kind == MediaKinds.Show ? view.LatestEpisodeAdded(i) : i.AddedAt, descending),
                "year" => Order(items, i => i.Year ?? 0, descending),
                "rating" => Order(items, i => i.Rating ?? 0, descending),
                _ => Order(items, i => i.SortTitle, descending)
            };

            var list = items.ToList();
            var skip = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
            return Results.Ok(new
            {
                items = list.Skip(skip).Take(take).Select(view.Summary),
                total = list.Count,
                genres = await store.GenresAsync(kind is MediaKinds.Movie or MediaKinds.Show ? kind : null, libraryId, ct)
            });
        });

        enabled.MapGet("/items/{id:guid}", async (Guid id, HttpContext context, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var view = await MediaView.LoadAsync(store, UserKey(context), ct);
            var item = view.Find(id);
            return item is null
                ? Results.NotFound(new { error = "That title is not in the library any more." })
                : Results.Ok(view.Detail(item, IsAdmin(context), scanner.SeasonPoster));
        });

        enabled.MapGet("/items/{id:guid}/{art:regex(^(poster|backdrop|thumb)$)}", async (
            Guid id, string art, HttpContext context, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null) return Results.NotFound();
            string? path;
            try { path = await scanner.ResolveArtworkAsync(item, art, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { path = null; }
            return ServeImage(context, path);
        });

        enabled.MapGet("/items/{id:guid}/seasons/{season:int}/poster", (Guid id, int season, HttpContext context, MediaScanner scanner) =>
            ServeImage(context, scanner.SeasonPoster(id, season)));
    }

    private static IOrderedEnumerable<MediaItem> Order<TKey>(IEnumerable<MediaItem> items, Func<MediaItem, TKey> key, bool descending) =>
        descending
            ? items.OrderByDescending(key).ThenBy(i => i.SortTitle, StringComparer.Ordinal)
            : items.OrderBy(key).ThenBy(i => i.SortTitle, StringComparer.Ordinal);

    private static IResult ServeImage(HttpContext context, string? path)
    {
        if (path is null || !File.Exists(path)) return Results.NotFound();
        // The URL carries a version (see MediaView.ArtUrl), so a changed match is a new URL and
        // this can be cached freely.
        context.Response.Headers.CacheControl = "private, max-age=86400";
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return Results.File(path, type, lastModified: File.GetLastWriteTimeUtc(path));
    }

    // MARK: - Playback

    private static void MapPlayback(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/items/{id:guid}/playback", async (
            Guid id, double? start, string? video, string? audio, int? audioTrack, HttpContext context,
            IMediaStore store, MediaScanner scanner, MediaHlsManager hls, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var view = await MediaView.LoadAsync(store, UserKey(context), ct);
            var item = view.Find(id);
            if (item is null || !item.IsPlayable) return Results.NotFound(new { error = "That title is not in the library any more." });
            if (!File.Exists(item.Path))
                return Results.NotFound(new { error = "The video file is missing. If it is on another computer, check that the share is still mounted." });

            var ffmpeg = await scanner.FfmpegAvailableAsync(ct);
            if (item.ProbedAt is null && ffmpeg) await scanner.ProbeItemAsync(item, ct);

            var startSeconds = start is { } requested and >= 0 ? requested : view.ResumePoint(item);
            if (item.DurationSeconds is { } duration && startSeconds >= duration - 5) startSeconds = 0;

            var plan = item.ProbedAt is null || item.VideoCodec is null
                // Never probed (no ffmpeg on this box): an MP4 is the one thing worth trying as-is.
                ? new MediaPlaybackPlan(item.Container is "mp4" or "m4v" || Path.GetExtension(item.Path).ToLowerInvariant() is ".mp4" or ".m4v",
                    "copy", "copy", null, false, "not probed")
                : MediaPlaybackPlanner.Plan(item,
                    MediaPlaybackPlanner.ParseCodecs(video, "h264"),
                    MediaPlaybackPlanner.ParseCodecs(audio, "aac", "mp3"),
                    audioTrack);

            var (next, nextTitle) = view.NextAfter(item);
            var subtitleOffset = plan.Direct ? 0 : startSeconds;
            var subtitles = item.SubtitleTracks.Select(track => SubtitleDto(item, track, subtitleOffset)).ToList();
            var summary = view.Summary(item);

            if (plan.Direct)
            {
                var extension = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant();
                return Results.Ok(new
                {
                    itemId = item.Id,
                    title = view.PlayerTitle(item),
                    subtitle = view.PlayerSubtitle(item),
                    durationSeconds = item.DurationSeconds,
                    startSeconds,
                    resumeSeconds = view.ResumePoint(item),
                    mode = "direct",
                    url = $"/api/media/stream/{item.Id}/video.{extension}",
                    mimeType = MediaPlaybackPlanner.MimeType(extension),
                    video = "copy",
                    audio = "copy",
                    reason = plan.Reason,
                    subtitles,
                    nextEpisodeId = next?.Id,
                    nextEpisodeTitle = nextTitle,
                    backdropUrl = summary.BackdropUrl,
                    posterUrl = summary.PosterUrl
                });
            }

            if (!ffmpeg)
                return Results.Json(new { error = "This file needs ffmpeg to play, and ffmpeg is not installed on the server. MP4 files with H.264 video play without it." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            MediaHlsSession session;
            try
            {
                session = await hls.StartAsync(item, plan, startSeconds, item.SubtitleTracks, ct);
            }
            catch (MediaPlaybackException ex)
            {
                loggers.CreateLogger("SentinelCore.Web.MediaEndpoints").LogWarning(
                    "Could not start {Title}: {Message} {Log}", item.Title, ex.Message, string.Join(" | ", ex.FfmpegLog));
                var tail = ex.FfmpegLog.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(3).Select(line => line.Replace(item.Path, Path.GetFileName(item.Path)));
                return Results.Json(new { error = ex.Message, detail = string.Join("\n", tail) }, statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(new
            {
                itemId = item.Id,
                title = view.PlayerTitle(item),
                subtitle = view.PlayerSubtitle(item),
                durationSeconds = item.DurationSeconds,
                startSeconds,
                resumeSeconds = view.ResumePoint(item),
                mode = "hls",
                url = $"/api/media/hls/{session.Id}/master.m3u8",
                mimeType = "application/vnd.apple.mpegurl",
                video = plan.VideoMode,
                audio = plan.AudioMode,
                reason = plan.Reason,
                subtitles,
                nextEpisodeId = next?.Id,
                nextEpisodeTitle = nextTitle,
                backdropUrl = summary.BackdropUrl,
                posterUrl = summary.PosterUrl
            });
        });

        enabled.MapGet("/stream/{id:guid}/{fileName}", async (Guid id, IMediaStore store, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null || !item.IsPlayable || !File.Exists(item.Path)) return Results.NotFound();
            var extension = Path.GetExtension(item.Path).TrimStart('.');
            return Results.File(item.Path, MediaPlaybackPlanner.MimeType(extension), enableRangeProcessing: true,
                lastModified: File.GetLastWriteTimeUtc(item.Path));
        });

        enabled.MapGet("/hls/{session}/{file}", async (string session, string file, HttpContext context, MediaHlsManager hls, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, private";
            var found = hls.Get(session);
            if (found is null) return Results.NotFound(new { error = "This playback session has ended. Press play again." });
            hls.Touch(found);

            if (file == "master.m3u8") return Results.Text(hls.MasterPlaylist(found), "application/vnd.apple.mpegurl");
            if (file.StartsWith("sub_", StringComparison.Ordinal) && file.EndsWith(".m3u8", StringComparison.Ordinal)
                && int.TryParse(file[4..^5], out var subtitleIndex)
                && found.Subtitles.FirstOrDefault(s => s.Index == subtitleIndex) is { } subtitle)
                return Results.Text(hls.SubtitlePlaylist(found, subtitle), "application/vnd.apple.mpegurl");
            if (file == "index.m3u8")
            {
                var path = hls.FilePath(found, file);
                if (path is null) return Results.NotFound();
                string text;
                try { text = await File.ReadAllTextAsync(path, ct); }
                catch (IOException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
                return Results.Text(WithStartAtBeginning(text), "application/vnd.apple.mpegurl");
            }

            var segment = hls.FilePath(found, file);
            return segment is null ? Results.NotFound() : Results.File(segment, "video/mp4");
        });

        enabled.MapGet("/items/{id:guid}/subtitles/{index:int}.vtt", async (
            Guid id, int index, double? offset, bool? hls, IMediaStore store, MediaArtCache cache, MediaScanner scanner,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            var track = item?.SubtitleTracks.FirstOrDefault(t => t.Index == index);
            if (item is null || track is null) return Results.NotFound();
            var path = await MediaSubtitles.EnsureVttAsync(item, track, cache.SubtitlesRoot, scanner.FfmpegPath,
                loggers.CreateLogger("SentinelCore.Web.MediaSubtitles"), ct);
            if (path is null) return Results.NotFound(new { error = "These subtitles could not be read." });
            var text = MediaSubtitles.Shift(await File.ReadAllTextAsync(path, ct), offset ?? 0);
            // Apple's HLS rules want every WebVTT rendition pinned to the video's clock; ours both
            // start at zero, so the map says exactly that.
            if (hls == true && !text.Contains("X-TIMESTAMP-MAP", StringComparison.Ordinal))
            {
                var firstBreak = text.IndexOf('\n');
                text = firstBreak < 0
                    ? text + "\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n"
                    : text[..firstBreak] + "\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000" + text[firstBreak..];
            }
            return Results.Text(text, "text/vtt", Encoding.UTF8);
        });

        enabled.MapPost("/items/{id:guid}/progress", async (Guid id, MediaProgressInput input, HttpContext context, IMediaStore store, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null || !item.IsPlayable) return Results.NotFound(new { error = "That title is not in the library any more." });
            var userKey = UserKey(context);
            var duration = input.DurationSeconds is > 0 ? input.DurationSeconds : item.DurationSeconds;
            var position = Math.Max(0, input.PositionSeconds);

            if (duration is > 0 && position >= duration.Value * MediaProgress.WatchedFraction)
            {
                var watched = new MediaProgress(userKey, id, 0, duration, true, DateTimeOffset.UtcNow);
                await store.SetProgressAsync(watched, ct);
                return Results.Ok(ProgressDto(watched));
            }

            var existing = (await store.GetProgressAsync(userKey, ct)).FirstOrDefault(p => p.ItemId == id);
            if (position < MediaProgress.MinimumSavedSeconds)
            {
                // Opening something and backing out straight away should not bury a resume point,
                // nor un-watch something already finished.
                return JsonOrNull(ProgressDto(existing));
            }

            var saved = new MediaProgress(userKey, id, position, duration, false, DateTimeOffset.UtcNow);
            await store.SetProgressAsync(saved, ct);
            return Results.Ok(ProgressDto(saved));
        });

        enabled.MapPost("/items/{id:guid}/watched", async (Guid id, MediaWatchedInput input, HttpContext context, IMediaStore store, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null) return Results.NotFound(new { error = "That title is not in the library any more." });
            var targets = item.Kind == MediaKinds.Show
                ? await store.GetItemsAsync(new MediaQuery(Kinds: [MediaKinds.Episode], ParentId: id), ct)
                : [item];
            var result = await SetWatchedAsync(targets, input.Watched, UserKey(context), store, ct);
            return JsonOrNull(item.Kind == MediaKinds.Show ? null : result);
        });

        enabled.MapPost("/items/{id:guid}/seasons/{season:int}/watched", async (
            Guid id, int season, MediaWatchedInput input, HttpContext context, IMediaStore store, CancellationToken ct) =>
        {
            var episodes = (await store.GetItemsAsync(new MediaQuery(Kinds: [MediaKinds.Episode], ParentId: id), ct))
                .Where(e => e.SeasonNumber == season).ToList();
            if (episodes.Count == 0) return Results.NotFound(new { error = "That season is not in the library." });
            await SetWatchedAsync(episodes, input.Watched, UserKey(context), store, ct);
            return JsonOrNull(null);
        });
    }

    // ASP.NET writes an empty body for a null result, which every client's JSON parser rejects.
    // "No progress" is a real answer here, so it goes out as the literal it is.
    private static IResult JsonOrNull(object? value) =>
        value is null ? Results.Text("null", "application/json") : Results.Json(value);

    // ffmpeg writes an EVENT playlist, which a player treats like live TV and joins at the end
    // of. This line tells it to start at the beginning, which is where the viewer asked to be.
    public static string WithStartAtBeginning(string playlist)
    {
        if (playlist.Contains("#EXT-X-START", StringComparison.Ordinal)) return playlist;
        var firstBreak = playlist.IndexOf('\n');
        return firstBreak < 0
            ? playlist
            : playlist[..(firstBreak + 1)] + "#EXT-X-START:TIME-OFFSET=0,PRECISE=YES\n" + playlist[(firstBreak + 1)..];
    }

    private static async Task<object?> SetWatchedAsync(IReadOnlyList<MediaItem> targets, bool watched, string userKey, IMediaStore store, CancellationToken ct)
    {
        MediaProgress? last = null;
        foreach (var target in targets.Where(t => t.IsPlayable))
        {
            if (watched)
            {
                last = new MediaProgress(userKey, target.Id, 0, target.DurationSeconds, true, DateTimeOffset.UtcNow);
                await store.SetProgressAsync(last, ct);
            }
            else
            {
                await store.DeleteProgressAsync(userKey, target.Id, ct);
            }
        }
        return last is null ? null : ProgressDto(last);
    }

    // MARK: - Metadata fixes

    private static void MapMetadata(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/tmdb/search", async (string? q, string? kind, int? year, TmdbClient tmdb, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "Type a title to search for." });
            try
            {
                var results = await tmdb.SearchAsync(kind == MediaKinds.Show ? MediaKinds.Show : MediaKinds.Movie, q.Trim(), year, ct);
                return Results.Ok(results.Take(20).Select(r => new
                {
                    tmdbId = r.TmdbId,
                    title = r.Title,
                    year = r.Year,
                    overview = r.Overview,
                    posterUrl = r.PosterPath is null ? null : TmdbClient.ImageUrl(r.PosterPath, "w185")
                }));
            }
            catch (InvalidOperationException ex) when (ex is TmdbNotConfiguredException or TmdbRejectedKeyException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = "TMDB did not answer: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization("AdminOnly");

        enabled.MapPost("/items/{id:guid}/match", async (Guid id, MediaMatchInput input, HttpContext context, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null) return Results.NotFound(new { error = "That title is not in the library any more." });
            if (item.Kind == MediaKinds.Episode) return Results.BadRequest(new { error = "Fix the show's match instead; its episodes follow it." });
            if (input.TmdbId <= 0) return Results.BadRequest(new { error = "Choose a TMDB result." });
            return await RefetchAsync(item, input.TmdbId, context, store, scanner, ct);
        }).RequireAuthorization("AdminOnly");

        enabled.MapPost("/items/{id:guid}/refresh", async (Guid id, MediaRefreshInput? input, HttpContext context, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null) return Results.NotFound(new { error = "That title is not in the library any more." });
            // An episode's metadata comes from its show's match, so refreshing one refreshes the show.
            if (item.Kind == MediaKinds.Episode && item.ParentId is { } parent && await store.GetItemAsync(parent, ct) is { } show) item = show;
            if (input?.Unlock == true) item.MetadataLocked = false;
            return await RefetchAsync(item, item.MetadataLocked ? item.TmdbId : null, context, store, scanner, ct, answerFor: id);
        }).RequireAuthorization("AdminOnly");
    }

    private static async Task<IResult> RefetchAsync(
        MediaItem item, int? tmdbId, HttpContext context, IMediaStore store, MediaScanner scanner, CancellationToken ct, Guid? answerFor = null)
    {
        try
        {
            await scanner.FetchMetadataAsync(item, tmdbId, null, ct);
        }
        catch (InvalidOperationException ex) when (ex is TmdbNotConfiguredException or TmdbRejectedKeyException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return Results.Json(new { error = "TMDB did not answer: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        var view = await MediaView.LoadAsync(store, UserKey(context), ct);
        return view.Find(answerFor ?? item.Id) is { } fresh
            ? Results.Ok(view.Detail(fresh, IsAdmin(context), scanner.SeasonPoster))
            : Results.NotFound();
    }

    // MARK: - Shared

    public static async Task<MediaSettingsDto> ReadOptionsAsync(AppSettingsFile settings, CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<MediaSettingsDto>(MediaSettingsDto.SectionName, cancellationToken) ?? new MediaSettingsDto();

    // Watched state is per person, keyed like the Organizer's: the lower-cased username, or the
    // shared household ("") when login is off.
    public static string UserKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } && context.User.Identity?.Name is { Length: > 0 } name
            ? name.ToLowerInvariant()
            : string.Empty;

    private static bool IsAdmin(HttpContext context) => context.User.IsInRole(nameof(AccountRole.Admin));

    private static object LibraryDto(MediaLibrary library) => new
    {
        id = library.Id,
        name = library.Name,
        kind = library.Kind,
        path = library.Path,
        itemCount = library.ItemCount,
        createdAt = library.CreatedAt,
        lastScanStartedAt = library.LastScanStartedAt,
        lastScanFinishedAt = library.LastScanFinishedAt,
        lastScanError = library.LastScanError
    };

    public static object? ProgressDto(MediaProgress? progress) => progress is null ? null : new
    {
        positionSeconds = Math.Round(progress.PositionSeconds, 1),
        durationSeconds = progress.DurationSeconds is { } d ? Math.Round(d, 1) : (double?)null,
        percent = Math.Round(progress.Percent, 1),
        watched = progress.Watched,
        updatedAt = progress.UpdatedAt
    };

    private static object SubtitleDto(MediaItem item, MediaSubtitleTrack track, double offset) => new
    {
        index = track.Index,
        language = track.Language,
        label = MediaSubtitles.Label(track),
        url = $"/api/media/items/{item.Id}/subtitles/{track.Index}.vtt"
            + (offset > 0 ? "?offset=" + offset.ToString("0.###", CultureInfo.InvariantCulture) : ""),
        forced = track.Forced,
        external = track.External is not null
    };

    // Everything one request needs to describe items to one person: the catalog, their
    // progress, and the show/episode relations - loaded once, so a shelf of forty shows does not
    // turn into forty queries. The catalog is a few thousand rows; this is cheap.
    public sealed class MediaView
    {
        private readonly Dictionary<Guid, MediaItem> _byId;
        private readonly Dictionary<Guid, MediaProgress> _progress;
        private readonly Dictionary<Guid, List<MediaItem>> _episodesByShow;

        private MediaView(IReadOnlyList<MediaItem> items, IReadOnlyList<MediaProgress> progress)
        {
            Items = items;
            _byId = items.ToDictionary(i => i.Id);
            _progress = progress.GroupBy(p => p.ItemId).ToDictionary(g => g.Key, g => g.First());
            _episodesByShow = items.Where(i => i.Kind == MediaKinds.Episode && i.ParentId is not null)
                .GroupBy(i => i.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(EpisodeOrder).ThenBy(e => e.EpisodeNumber ?? 0).ToList());
            Progress = progress;
        }

        public IReadOnlyList<MediaItem> Items { get; }

        public IReadOnlyList<MediaProgress> Progress { get; }

        public static async Task<MediaView> LoadAsync(IMediaStore store, string userKey, CancellationToken cancellationToken) =>
            new(await store.GetItemsAsync(new MediaQuery(), cancellationToken), await store.GetProgressAsync(userKey, cancellationToken));

        public MediaItem? Find(Guid id) => _byId.GetValueOrDefault(id);

        // Specials (season 0) after the real seasons, which is where people expect them.
        private static int EpisodeOrder(MediaItem episode) => episode.SeasonNumber is null or 0 ? 10_000 : episode.SeasonNumber.Value;

        public List<MediaItem> EpisodesOf(Guid showId) => _episodesByShow.GetValueOrDefault(showId) ?? [];

        public DateTimeOffset LatestEpisodeAdded(MediaItem show) =>
            EpisodesOf(show.Id).Select(e => e.AddedAt).DefaultIfEmpty(show.AddedAt).Max();

        public MediaProgress? ProgressOf(Guid id) => _progress.GetValueOrDefault(id);

        public bool IsWatched(MediaItem item) => item.Kind == MediaKinds.Show
            ? EpisodesOf(item.Id).Count > 0 && EpisodesOf(item.Id).All(e => ProgressOf(e.Id)?.Watched == true)
            : ProgressOf(item.Id)?.Watched == true;

        public double ResumePoint(MediaItem item) =>
            ProgressOf(item.Id) is { Watched: false, PositionSeconds: >= MediaProgress.MinimumSavedSeconds } progress ? progress.PositionSeconds : 0;

        // The episode to play when someone presses Play on a show: the one they are part way
        // through, else the one after the last they finished, else the first they have not seen.
        public MediaItem? NextEpisode(MediaItem show)
        {
            var episodes = EpisodesOf(show.Id);
            if (episodes.Count == 0) return null;
            var inProgress = episodes
                .Select(e => (Episode: e, Progress: ProgressOf(e.Id)))
                .Where(p => p.Progress is { Watched: false, PositionSeconds: > 0 })
                .OrderByDescending(p => p.Progress!.UpdatedAt)
                .FirstOrDefault();
            if (inProgress.Episode is not null) return inProgress.Episode;

            var lastWatched = episodes
                .Select((e, index) => (Index: index, Progress: ProgressOf(e.Id)))
                .Where(p => p.Progress?.Watched == true)
                .OrderByDescending(p => p.Progress!.UpdatedAt)
                .FirstOrDefault();
            if (lastWatched.Progress is not null)
            {
                for (var i = lastWatched.Index + 1; i < episodes.Count; i++)
                    if (ProgressOf(episodes[i].Id)?.Watched != true) return episodes[i];
            }
            return episodes.FirstOrDefault(e => ProgressOf(e.Id)?.Watched != true) ?? episodes[0];
        }

        public (MediaItem? Next, string? Title) NextAfter(MediaItem item)
        {
            if (item.Kind != MediaKinds.Episode || item.ParentId is not { } showId) return (null, null);
            var episodes = EpisodesOf(showId);
            var index = episodes.FindIndex(e => e.Id == item.Id);
            if (index < 0 || index + 1 >= episodes.Count) return (null, null);
            var next = episodes[index + 1];
            return (next, EpisodeLabel(next));
        }

        // In-progress movies and episodes, newest first, plus "next up": the following episode of
        // a show whose latest activity was finishing an episode. One entry per show.
        public IEnumerable<MediaItem> ContinueWatching()
        {
            var seenShows = new HashSet<Guid>();
            foreach (var progress in Progress.OrderByDescending(p => p.UpdatedAt))
            {
                if (Find(progress.ItemId) is not { } item) continue;
                if (item.Kind == MediaKinds.Episode && item.ParentId is { } showId)
                {
                    if (!seenShows.Add(showId)) continue;
                    if (!progress.Watched && progress.PositionSeconds > 0) { yield return item; continue; }
                    if (progress.Watched && NextAfter(item).Next is { } next && ProgressOf(next.Id)?.Watched != true) yield return next;
                    continue;
                }
                if (item.Kind == MediaKinds.Movie && !progress.Watched && progress.PositionSeconds > 0) yield return item;
            }
        }

        // The hero at the top of Home: something being watched if it has a backdrop, else a
        // recently added title with one, changing daily so the page does not look frozen.
        public MediaItem? Featured(IReadOnlyList<MediaItem> continueWatching, IReadOnlyList<MediaItem> recentMovies, IReadOnlyList<MediaItem> shows)
        {
            foreach (var item in continueWatching)
            {
                var candidate = item.Kind == MediaKinds.Episode && item.ParentId is { } parent ? Find(parent) ?? item : item;
                if (candidate.HasBackdrop) return candidate;
            }
            var pool = recentMovies.Concat(shows.OrderByDescending(LatestEpisodeAdded).Take(ShelfSize))
                .Where(i => i.HasBackdrop && i.Overview is not null).ToList();
            if (pool.Count == 0) pool = Items.Where(i => i.Kind is MediaKinds.Movie or MediaKinds.Show && i.HasBackdrop).ToList();
            if (pool.Count == 0) return null;
            return pool[DateTimeOffset.UtcNow.DayOfYear % pool.Count];
        }

        public string PlayerTitle(MediaItem item) =>
            item.Kind == MediaKinds.Episode && item.ParentId is { } parent && Find(parent) is { } show ? show.Title : item.Title;

        public string? PlayerSubtitle(MediaItem item) => item.Kind == MediaKinds.Episode ? EpisodeLabel(item)
            : item.Year is { } year ? year.ToString(CultureInfo.InvariantCulture) : null;

        public static string EpisodeLabel(MediaItem episode)
        {
            var parts = new List<string>();
            if (episode.SeasonNumber is { } season) parts.Add(season == 0 ? "Special" : $"S{season}");
            if (episode.EpisodeNumber is { } number and < 1000) parts.Add($"E{number}");
            parts.Add(episode.Title);
            return string.Join(" · ", parts);
        }

        public static string? ArtUrl(MediaItem item, string kind)
        {
            var source = kind switch
            {
                "poster" => item.PosterLocal ?? item.PosterRemote,
                "backdrop" => item.BackdropLocal ?? item.BackdropRemote,
                "thumb" => item.ThumbLocal ?? item.ThumbRemote,
                _ => null
            };
            if (source is null) return null;
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)).AsSpan(0, 4)).ToLowerInvariant();
            return $"/api/media/items/{item.Id}/{kind}?v={version}";
        }

        public MediaSummary Summary(MediaItem item)
        {
            var show = item.Kind == MediaKinds.Episode && item.ParentId is { } parent ? Find(parent) : null;
            var summary = new MediaSummary
            {
                Id = item.Id,
                Kind = item.Kind,
                LibraryId = item.LibraryId,
                Title = item.Title,
                SortTitle = item.SortTitle,
                Overview = item.Overview,
                Year = item.Year,
                Genres = item.Genres,
                Rating = item.Rating,
                Certification = item.Certification,
                RuntimeMinutes = item.RuntimeMinutes ?? (item.DurationSeconds is { } seconds ? (int)Math.Round(seconds / 60) : null),
                DurationSeconds = item.DurationSeconds is { } d ? Math.Round(d, 1) : null,
                PosterUrl = show is not null ? ArtUrl(show, "poster") : ArtUrl(item, "poster"),
                BackdropUrl = ArtUrl(item, "backdrop") ?? (show is not null ? ArtUrl(show, "backdrop") : null),
                ThumbUrl = item.Kind == MediaKinds.Episode ? ArtUrl(item, "thumb") : null,
                AddedAt = item.Kind == MediaKinds.Show ? LatestEpisodeAdded(item) : item.AddedAt,
                Progress = item.IsPlayable ? ProgressDto(ProgressOf(item.Id)) : null
            };
            if (show is not null)
            {
                summary.ShowId = show.Id;
                summary.ShowTitle = show.Title;
                summary.SeasonNumber = item.SeasonNumber;
                summary.EpisodeNumber = item.EpisodeNumber is < 1000 ? item.EpisodeNumber : null;
                summary.AirDate = item.ReleaseDate;
            }
            if (item.Kind == MediaKinds.Show)
            {
                var episodes = EpisodesOf(item.Id);
                summary.SeasonCount = episodes.Select(e => e.SeasonNumber ?? 1).Where(s => s > 0).Distinct().Count();
                summary.EpisodeCount = episodes.Count;
                summary.WatchedEpisodeCount = episodes.Count(e => ProgressOf(e.Id)?.Watched == true);
                summary.NextEpisodeId = NextEpisode(item)?.Id;
                summary.Progress = null;
            }
            return summary;
        }

        public object Detail(MediaItem item, bool isAdmin, Func<Guid, int, string?> seasonPoster)
        {
            var summary = Summary(item);
            var seasons = item.Kind != MediaKinds.Show ? null : EpisodesOf(item.Id)
                .GroupBy(e => e.SeasonNumber ?? 1)
                .OrderBy(g => g.Key == 0 ? 10_000 : g.Key)
                .Select(g => new
                {
                    number = g.Key,
                    name = g.Key == 0 ? "Specials" : $"Season {g.Key}",
                    episodeCount = g.Count(),
                    watchedCount = g.Count(e => ProgressOf(e.Id)?.Watched == true),
                    posterUrl = seasonPoster(item.Id, g.Key) is null ? null : $"/api/media/items/{item.Id}/seasons/{g.Key}/poster",
                    episodes = g.Select(Summary).ToList()
                }).ToList();

            return new
            {
                summary.Id, summary.Kind, summary.LibraryId, summary.Title, summary.SortTitle, summary.Year, summary.Genres,
                summary.Rating, summary.Certification, summary.RuntimeMinutes, summary.DurationSeconds, summary.PosterUrl,
                summary.BackdropUrl, summary.ThumbUrl, summary.AddedAt, summary.Progress, summary.ShowId, summary.ShowTitle,
                summary.SeasonNumber, summary.EpisodeNumber, summary.AirDate, summary.SeasonCount, summary.EpisodeCount,
                summary.WatchedEpisodeCount, summary.NextEpisodeId, summary.Overview,
                tagline = item.Tagline,
                releaseDate = item.ReleaseDate,
                cast = item.Cast.Select(c => new { name = c.Name, character = c.Character }),
                tmdbId = item.TmdbId,
                tmdbUrl = item.TmdbId is { } tmdb
                    ? $"https://www.themoviedb.org/{(item.Kind == MediaKinds.Movie ? "movie" : "tv")}/{tmdb}"
                    : null,
                metadataLocked = item.MetadataLocked,
                metadataError = isAdmin ? item.MetadataError : null,
                file = !item.IsPlayable ? null : new
                {
                    container = item.Container ?? Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant(),
                    videoCodec = item.VideoCodec,
                    videoProfile = item.VideoProfile,
                    width = item.Width,
                    height = item.Height,
                    audioCodec = item.AudioCodec,
                    audioChannels = item.AudioChannels,
                    sizeBytes = item.FileSize,
                    bitrate = item.Bitrate,
                    path = isAdmin ? item.Path : null
                },
                audioTracks = !item.IsPlayable ? null : item.AudioTracks.Select(a => new
                {
                    index = a.Index,
                    codec = a.Codec,
                    channels = a.Channels,
                    language = a.Language,
                    label = AudioLabel(a),
                    isDefault = a.IsDefault
                }),
                subtitles = !item.IsPlayable ? null : item.SubtitleTracks.Select(t => SubtitleDto(item, t, 0)),
                seasons
            };
        }

        private static string AudioLabel(MediaAudioTrack track)
        {
            var language = MediaSubtitles.LanguageName(track.Language) ?? track.Title ?? $"Track {track.Index + 1}";
            var layout = track.Channels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", var n => $"{n}ch" };
            return $"{language} {layout}";
        }
    }

    public sealed class MediaSummary
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public Guid LibraryId { get; set; }
        public string Title { get; set; } = "";
        public string SortTitle { get; set; } = "";
        public string? Overview { get; set; }
        public int? Year { get; set; }
        public List<string> Genres { get; set; } = [];
        public double? Rating { get; set; }
        public string? Certification { get; set; }
        public int? RuntimeMinutes { get; set; }
        public double? DurationSeconds { get; set; }
        public string? PosterUrl { get; set; }
        public string? BackdropUrl { get; set; }
        public string? ThumbUrl { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public object? Progress { get; set; }
        public Guid? ShowId { get; set; }
        public string? ShowTitle { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string? AirDate { get; set; }
        public int? SeasonCount { get; set; }
        public int? EpisodeCount { get; set; }
        public int? WatchedEpisodeCount { get; set; }
        public Guid? NextEpisodeId { get; set; }
    }
}
