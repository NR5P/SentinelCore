using System.Globalization;
using System.Text;

namespace SentinelCore.Web;

// The Music tab's HTTP surface - the contract is core/docs/MUSIC_AND_PODCASTS.md (sections 2-4),
// and every client (web, iOS/macOS, tvOS, Android, Windows) is built against that file rather
// than against this one.
public static class MusicEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 1000;
    public const string OffMessage = "Music is turned off in Settings.";
    private const string TrackGone = "That track is not in the library any more.";
    private const string AlbumGone = "That album is not in the library any more.";
    private const string ArtistGone = "That artist is not in the library any more.";
    private const string PlaylistGone = "That playlist doesn't exist, or isn't shared with you.";
    private const string LibraryGone = "That library no longer exists.";

    public static void MapMusicEndpoints(this WebApplication app)
    {
        // Every /api/music route answers 404 while Music is off; /api/audio/enabled is how a
        // client finds out without treating "off" as an error.
        var music = app.MapGroup("/api/music").RequireAuthorization().AddEndpointFilter(async (invocation, next) =>
        {
            var service = invocation.HttpContext.RequestServices.GetRequiredService<MusicService>();
            return await service.EnabledAsync(invocation.HttpContext.RequestAborted)
                ? await next(invocation)
                : Results.NotFound(new { error = OffMessage });
        });

        MapLibraries(music);
        MapBrowsing(music);
        MapArtAndLyrics(music);
        MapStreaming(music);
        MapListening(music);
        MapPlaylists(music);
    }

    private static string UserKey(HttpContext context) => MediaEndpoints.UserKey(context);

    private static bool IsAdmin(HttpContext context) => context.User.IsInRole(nameof(AccountRole.Admin));

    private static Task<MusicView> ViewAsync(MusicService service, HttpContext context, CancellationToken ct) =>
        service.ViewAsync(UserKey(context), IsAdmin(context), ct);

    // MARK: - Libraries

    private static void MapLibraries(RouteGroupBuilder music)
    {
        music.MapGet("/status", async (MusicService service, MediaScanner media, CancellationToken ct) =>
        {
            var options = await service.OptionsAsync(ct);
            var libraries = await service.Store.GetLibrariesAsync(ct);
            var catalog = await service.CatalogAsync(ct);
            var scanner = service.Scanner;
            var progress = scanner.Progress;
            return Results.Ok(new
            {
                enabled = options.Enabled,
                ffmpegAvailable = await media.FfmpegAvailableAsync(ct),
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
                trackCount = catalog.Tracks.Count,
                albumCount = catalog.Albums.Count,
                artistCount = catalog.Artists.Count,
                totalDurationSeconds = Math.Round(catalog.Tracks.Sum(t => t.DurationSeconds)),
                totalBytes = catalog.Tracks.Sum(t => t.FileSize),
                lastScanFinishedAt = scanner.LastFinishedAt ?? libraries.Max(l => l.LastScanFinishedAt),
                lastScanError = scanner.LastError ?? libraries.Select(l => l.LastScanError).FirstOrDefault(e => e is not null)
            });
        });

        music.MapGet("/libraries", async (MusicService service, CancellationToken ct) =>
            Results.Ok((await service.Store.GetLibrariesAsync(ct)).Select(LibraryDto)));

        music.MapPost("/libraries", async (MusicLibraryInput input, MusicService service, MediaFiles files, CancellationToken ct) =>
        {
            var errors = await ValidateLibraryAsync(input, null, service.Store, files, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var library = new MusicLibrary(Guid.NewGuid(), input.Name.Trim(), NormalizePath(input.Path), DateTimeOffset.UtcNow);
            await service.Store.SaveLibraryAsync(library, ct);
            service.Scanner.RequestScan(library.Id);
            return Results.Created($"/api/music/libraries/{library.Id}", LibraryDto(library));
        }).RequireAuthorization("AdminOnly");

        music.MapPut("/libraries/{id:guid}", async (Guid id, MusicLibraryInput input, MusicService service, MediaFiles files, CancellationToken ct) =>
        {
            var existing = await service.Store.GetLibraryAsync(id, ct);
            if (existing is null) return Results.NotFound(new { error = LibraryGone });
            var errors = await ValidateLibraryAsync(input, id, service.Store, files, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var path = NormalizePath(input.Path);
            var moved = !string.Equals(path, existing.Path, StringComparison.Ordinal);
            var updated = existing with { Name = input.Name.Trim(), Path = path };
            await service.Store.SaveLibraryAsync(updated, ct);
            if (moved) service.Scanner.RequestScan(id);
            return Results.Ok(LibraryDto(updated));
        }).RequireAuthorization("AdminOnly");

        music.MapDelete("/libraries/{id:guid}", async (Guid id, MusicService service, CancellationToken ct) =>
        {
            await service.Store.DeleteLibraryAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        music.MapPost("/libraries/{id:guid}/scan", async (Guid id, MusicService service, CancellationToken ct) =>
        {
            if (await service.Store.GetLibraryAsync(id, ct) is null) return Results.NotFound(new { error = LibraryGone });
            return service.Scanner.TryStartScan(id) ? Results.Accepted() : Results.Conflict(new { error = "A scan is already running." });
        }).RequireAuthorization("AdminOnly");

        music.MapPost("/scan", (MusicService service) =>
            service.Scanner.TryStartScan(null) ? Results.Accepted() : Results.Conflict(new { error = "A scan is already running." }))
            .RequireAuthorization("AdminOnly");
    }

    private static async Task<List<string>> ValidateLibraryAsync(MusicLibraryInput input, Guid? id, IMusicStore store, MediaFiles files, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name)) errors.Add("Give the library a name, like Music or Dad's Records.");
        var path = input.Path?.Trim() ?? "";
        if (path.Length == 0) errors.Add("Choose the folder the music is in.");
        else if (MediaNetworkPath.IsNetwork(path))
        {
            if (!MediaNetworkPath.TryParse(path, out var network))
                errors.Add("That network address is not complete. It looks like smb://computer/SharedFolder/Music.");
            else if (network.Share.Length == 0)
                errors.Add($"Choose a shared folder on {network.Host}, not the computer itself.");
            else
            {
                try
                {
                    if (!await Task.Run(() => files.Network.DirectoryExists(network.ToString()), ct))
                        errors.Add($"{network} is not a folder {network.Host} shares.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(ex.Message);
                }
            }
        }
        else if (!Path.IsPathRooted(path)) errors.Add("Use the folder's full path, like /mnt/music.");
        else if (!Directory.Exists(path))
            errors.Add($"{path} does not exist on the server. For a folder on another computer, choose it under Network computers in the folder picker, or type its network address (smb://PC/Music).");
        else
        {
            try { _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"The server cannot read {path}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
        {
            var normalized = NormalizePath(path);
            foreach (var other in await store.GetLibrariesAsync(ct))
                if (other.Id != id && string.Equals(NormalizePath(other.Path), normalized, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{other.Name} already uses that folder.");
        }
        return errors;
    }

    private static string NormalizePath(string path) =>
        MediaNetworkPath.Normalize(path) ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static object LibraryDto(MusicLibrary library) => new
    {
        id = library.Id,
        name = library.Name,
        path = library.Path,
        network = MediaNetworkPath.IsNetwork(library.Path),
        trackCount = library.TrackCount,
        lastScanFinishedAt = library.LastScanFinishedAt,
        lastScanError = library.LastScanError,
        createdAt = library.CreatedAt
    };

    // MARK: - Home, browsing and search

    private static (int Skip, int Take) Page(int? offset, int? limit) =>
        (Math.Max(0, offset ?? 0), Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize));

    private static bool Descending(string sort, string? order, params string[] descendingByDefault) =>
        order is null ? descendingByDefault.Contains(sort) : order.Equals("desc", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<T> Order<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, bool descending, Func<T, string> tieBreak) =>
        descending
            ? items.OrderByDescending(key).ThenBy(tieBreak, StringComparer.Ordinal)
            : items.OrderBy(key).ThenBy(tieBreak, StringComparer.Ordinal);

    private static void MapBrowsing(RouteGroupBuilder music)
    {
        music.MapGet("/home", async (HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            var playlists = await service.VisiblePlaylistsAsync(view.User.UserKey, ct);
            var pins = (await service.Store.GetPinsAsync(view.User.UserKey, ct)).Select(p => p.PlaylistId).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(MusicDiscovery.Home(view, playlists, pins,
                p => MusicService.PlaylistTracks(view, p, now).Select(x => x.Track).ToList(), now));
        });

        music.MapGet("/artists", async (HttpContext context, MusicService service, string? q, string? genre, string? sort, string? order,
            bool? albumArtistsOnly, int? offset, int? limit, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            IEnumerable<MusicArtistInfo> artists = view.Catalog.Artists;
            if (albumArtistsOnly == true) artists = artists.Where(a => a.IsAlbumArtist);
            if (Words(q) is { Length: > 0 } words) artists = artists.Where(a => MusicText.Matches(words, a.Words));
            if (!string.IsNullOrWhiteSpace(genre)) artists = artists.Where(a => a.Tracks.Any(t => HasGenre(t, genre)));
            var key = sort ?? "name";
            var descending = Descending(key, order, "plays", "added");
            artists = key switch
            {
                "plays" => Order(artists, view.ArtistPlays, descending, a => MusicText.SortKey(a.Name)),
                "added" => Order(artists, a => a.Tracks.Count == 0 ? DateTimeOffset.MinValue : a.Tracks.Max(t => t.AddedAt), descending, a => MusicText.SortKey(a.Name)),
                _ => Order(artists, a => MusicText.SortKey(a.Name), descending, a => a.Name)
            };
            var list = artists.ToList();
            var (skip, take) = Page(offset, limit);
            return Results.Ok(new { items = list.Skip(skip).Take(take).Select(view.Artist), total = list.Count });
        });

        music.MapGet("/albums", async (HttpContext context, MusicService service, string? q, Guid? artistId, string? genre, int? year, int? decade,
            string? sort, string? order, int? offset, int? limit, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            IEnumerable<MusicAlbumInfo> albums = view.Catalog.Albums;
            if (Words(q) is { Length: > 0 } words) albums = albums.Where(a => MusicText.Matches(words, a.Words));
            if (artistId is { } artist) albums = albums.Where(a => a.ArtistId == artist || a.Tracks.Any(t => t.Artists.Any(n => MusicIds.Artist(n) == artist)));
            if (!string.IsNullOrWhiteSpace(genre)) albums = albums.Where(a => a.Tracks.Any(t => HasGenre(t, genre)));
            if (year is { } y) albums = albums.Where(a => a.Year == y);
            if (decade is { } d) albums = albums.Where(a => MusicCatalog.Decade(a.Year) == d / 10 * 10);
            var key = sort ?? "title";
            var descending = Descending(key, order, "year", "added", "plays", "rating");
            string Tie(MusicAlbumInfo a) => MusicText.SortKey(a.Title);
            albums = key switch
            {
                "artist" => Order(albums, a => MusicText.SortKey(a.Artist), descending, a => $"{a.Year:0000}{Tie(a)}"),
                "year" => Order(albums, a => a.Year ?? 0, descending, Tie),
                "added" => Order(albums, a => a.AddedAt, descending, Tie),
                "plays" => Order(albums, view.AlbumPlays, descending, Tie),
                "rating" => Order(albums, a => view.User.RatingOf(MusicLoveKinds.Album, a.Id), descending, Tie),
                _ => Order(albums, Tie, descending, a => a.Artist)
            };
            var list = albums.ToList();
            var (skip, take) = Page(offset, limit);
            return Results.Ok(new { items = list.Skip(skip).Take(take).Select(view.Album), total = list.Count });
        });

        music.MapGet("/tracks", async (HttpContext context, MusicService service, string? q, Guid? artistId, Guid? albumId, string? genre,
            int? decade, bool? loved, int? minRating, string? sort, string? order, int? offset, int? limit, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            IEnumerable<MusicTrack> tracks = view.Catalog.Tracks;
            if (Words(q) is { Length: > 0 } words) tracks = tracks.Where(t => MusicText.Matches(words, view.Catalog.TrackWords[t.Id]));
            if (artistId is { } artist) tracks = tracks.Where(t => t.Artists.Any(n => MusicIds.Artist(n) == artist) || MusicIds.Artist(t.AlbumArtist) == artist);
            if (albumId is { } album) tracks = tracks.Where(t => t.AlbumId == album);
            if (!string.IsNullOrWhiteSpace(genre)) tracks = tracks.Where(t => HasGenre(t, genre));
            if (decade is { } d) tracks = tracks.Where(t => MusicCatalog.Decade(t.Year) == d / 10 * 10);
            if (loved is { } l) tracks = tracks.Where(t => view.User.IsLoved(MusicLoveKinds.Track, t.Id) == l);
            if (minRating is > 0) tracks = tracks.Where(t => view.User.RatingOf(MusicLoveKinds.Track, t.Id) >= minRating);
            var key = sort ?? (albumId is null ? "title" : "album");
            var descending = Descending(key, order, "year", "added", "plays", "lastPlayed", "rating", "duration");
            string Tie(MusicTrack t) => MusicText.SortKey(t.Title);
            tracks = key switch
            {
                "artist" => Order(tracks, t => MusicText.SortKey(t.PrimaryArtist), descending, t => $"{MusicText.SortKey(t.Album)}|{t.DiscNumber ?? 1:000}{t.TrackNumber ?? 0:0000}"),
                // The catalog's own order is album artist, album, disc, track.
                "album" => descending ? tracks.Reverse() : tracks,
                "year" => Order(tracks, t => t.Year ?? 0, descending, Tie),
                "added" => Order(tracks, t => t.AddedAt, descending, Tie),
                "plays" => Order(tracks, t => view.User.PlayCount(t.Id), descending, Tie),
                "lastPlayed" => Order(tracks, t => view.User.LastPlayed(t.Id) ?? DateTimeOffset.MinValue, descending, Tie),
                "rating" => Order(tracks, t => view.User.RatingOf(MusicLoveKinds.Track, t.Id), descending, Tie),
                "duration" => Order(tracks, t => t.DurationSeconds, descending, Tie),
                _ => Order(tracks, Tie, descending, t => t.PrimaryArtist)
            };
            var list = tracks.ToList();
            var (skip, take) = Page(offset, limit);
            return Results.Ok(new { items = list.Skip(skip).Take(take).Select(t => view.Track(t)), total = list.Count });
        });

        music.MapGet("/tracks/{id:guid}", async (Guid id, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            return view.Catalog.TracksById.TryGetValue(id, out var track)
                ? Results.Ok(view.Track(track))
                : Results.NotFound(new { error = TrackGone });
        });

        music.MapGet("/genres", async (HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            var genres = view.Catalog.Tracks
                .SelectMany(t => t.Genres.Select(g => (Key: MusicText.Key(g), Name: g, Track: t)))
                .GroupBy(x => x.Key)
                .Select(g => new
                {
                    name = g.GroupBy(x => x.Name).OrderByDescending(n => n.Count()).First().Key,
                    trackCount = g.Select(x => x.Track.Id).Distinct().Count(),
                    albumCount = g.Select(x => x.Track.AlbumId).Distinct().Count(),
                    artUrls = view.ArtUrls(g.Select(x => x.Track).OrderBy(t => MusicDiscovery.Shuffle(t.AlbumId, 0)))
                })
                .OrderBy(g => g.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Results.Ok(genres);
        });

        music.MapGet("/artists/{id:guid}", async (Guid id, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            if (!view.Catalog.ArtistsById.TryGetValue(id, out var artist)) return Results.NotFound(new { error = ArtistGone });
            var household = (await service.Store.HouseholdPlayCountsAsync(ct)).ToDictionary(p => p.TrackId, p => p.Plays);
            return Results.Ok(view.ArtistDetail(artist, household));
        });

        music.MapGet("/albums/{id:guid}", async (Guid id, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            return view.Catalog.AlbumsById.TryGetValue(id, out var album)
                ? Results.Ok(view.AlbumDetail(album))
                : Results.NotFound(new { error = AlbumGone });
        });

        music.MapGet("/search", async (HttpContext context, MusicService service, string? q, CancellationToken ct) =>
        {
            var words = Words(q);
            if (words.Length == 0) return Results.Ok(new { artists = Array.Empty<object>(), albums = Array.Empty<object>(), tracks = Array.Empty<object>(), playlists = Array.Empty<object>(), genres = Array.Empty<string>() });
            var view = await ViewAsync(service, context, ct);
            var catalog = view.Catalog;
            var key = MusicText.Key(q);
            var now = DateTimeOffset.UtcNow;
            var playlists = await service.VisiblePlaylistsAsync(view.User.UserKey, ct);
            var pins = (await service.Store.GetPinsAsync(view.User.UserKey, ct)).Select(p => p.PlaylistId).ToHashSet();
            return Results.Ok(new
            {
                artists = catalog.Artists.Where(a => MusicText.Matches(words, a.Words))
                    .OrderByDescending(a => MusicText.Key(a.Name) == key).ThenByDescending(a => a.IsAlbumArtist)
                    .ThenByDescending(view.ArtistPlays).ThenByDescending(a => a.Tracks.Count)
                    .Take(8).Select(view.Artist),
                albums = catalog.Albums.Where(a => MusicText.Matches(words, a.Words))
                    .OrderByDescending(a => MusicText.Key(a.Title) == key).ThenByDescending(view.AlbumPlays).ThenBy(a => MusicText.SortKey(a.Title), StringComparer.Ordinal)
                    .Take(12).Select(view.Album),
                tracks = catalog.Tracks.Where(t => MusicText.Matches(words, catalog.TrackWords[t.Id]))
                    .OrderByDescending(t => MusicText.Key(t.Title) == key).ThenByDescending(t => view.User.PlayCount(t.Id)).ThenBy(t => MusicText.SortKey(t.Title), StringComparer.Ordinal)
                    .Take(25).Select(t => view.Track(t)),
                playlists = playlists.Where(p => MusicText.Matches(words, MusicText.Words(p.Name)))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(8)
                    .Select(p => view.Playlist(p, MusicService.PlaylistTracks(view, p, now).Select(x => x.Track).ToList(), pins.Contains(p.Id))),
                genres = catalog.Genres.Where(g => MusicText.Matches(words, MusicText.Words(g))).Take(10)
            });
        });

        // Browsing by folder, from the catalog rather than the disk, so it answers instantly
        // even for a library on a sleeping computer.
        music.MapGet("/folders", async (HttpContext context, MusicService service, Guid? libraryId, string? path, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            if (libraryId is not { } id)
                return Results.Ok(new
                {
                    libraryId = (Guid?)null,
                    path = "",
                    parent = (string?)null,
                    folders = view.Catalog.Libraries.Select(l => new
                    {
                        name = l.Name,
                        path = "",
                        trackCount = view.Catalog.Tracks.Count(t => t.LibraryId == l.Id),
                        libraryId = l.Id
                    }),
                    tracks = Array.Empty<MusicTrackDto>()
                });
            if (view.Catalog.Libraries.All(l => l.Id != id)) return Results.NotFound(new { error = LibraryGone });
            var folder = (path ?? "").Replace('\\', '/').Trim('/');
            var prefix = folder.Length == 0 ? "" : folder + "/";
            var inside = view.Catalog.Tracks.Where(t => t.LibraryId == id && t.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (folder.Length > 0 && inside.Count == 0) return Results.NotFound(new { error = "That folder has no music in it any more." });
            var folders = inside.Where(t => t.RelativePath.IndexOf('/', prefix.Length) > 0)
                .GroupBy(t => t.RelativePath[prefix.Length..t.RelativePath.IndexOf('/', prefix.Length)], StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { name = g.Key, path = prefix + g.Key, trackCount = g.Count(), libraryId = id });
            var tracks = inside.Where(t => t.RelativePath.IndexOf('/', prefix.Length) < 0)
                .OrderBy(t => t.DiscNumber ?? 1).ThenBy(t => t.TrackNumber ?? int.MaxValue).ThenBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(t => view.Track(t));
            var slash = folder.LastIndexOf('/');
            return Results.Ok(new
            {
                libraryId = (Guid?)id,
                path = folder,
                parent = folder.Length == 0 ? null : slash < 0 ? "" : folder[..slash],
                folders,
                tracks
            });
        });
    }

    private static string[] Words(string? q) => MusicText.Words(q);

    private static bool HasGenre(MusicTrack track, string genre)
    {
        var key = MusicText.Key(genre);
        return track.Genres.Any(g => MusicText.Key(g) == key);
    }

    // MARK: - Art and lyrics

    private static void MapArtAndLyrics(RouteGroupBuilder music)
    {
        music.MapGet("/albums/{id:guid}/art", async (Guid id, int? size, HttpContext context, MusicService service, MusicArtCache art, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(ct);
            return await ServeArtAsync(context, service.Scanner.Files, art, catalog.AlbumsById.GetValueOrDefault(id)?.ArtPath, size, ct);
        });

        music.MapGet("/artists/{id:guid}/art", async (Guid id, int? size, HttpContext context, MusicService service, MusicArtCache art, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(ct);
            var artist = catalog.ArtistsById.GetValueOrDefault(id);
            return await ServeArtAsync(context, service.Scanner.Files, art, artist is null ? null : catalog.ArtistArt(artist), size, ct);
        });

        music.MapGet("/tracks/{id:guid}/art", async (Guid id, int? size, HttpContext context, MusicService service, MusicArtCache art, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(ct);
            var track = catalog.TracksById.GetValueOrDefault(id);
            return await ServeArtAsync(context, service.Scanner.Files, art, track is null ? null : catalog.AlbumOf(track)?.ArtPath, size, ct);
        });

        music.MapGet("/tracks/{id:guid}/lyrics", async (Guid id, MusicService service, MusicLyricsService lyrics, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(ct);
            if (!catalog.TracksById.TryGetValue(id, out var track)) return Results.NotFound(new { error = TrackGone });
            var found = await lyrics.GetAsync(track, ct);
            return found is null
                ? Results.NotFound(new { error = "No lyrics were found for this track." })
                : Results.Ok(new
                {
                    synced = found.Synced,
                    lines = found.Lines.Select(l => new { time = l.Time, text = l.Text }),
                    plain = found.Plain,
                    source = found.Source
                });
        });
    }

    private static async Task<IResult> ServeArtAsync(HttpContext context, MediaFiles files, MusicArtCache art, string? path, int? size, CancellationToken ct)
    {
        if (path is null) return Results.NotFound();
        var fs = files.For(path);
        try
        {
            if (!await Task.Run(() => files.FileExists(path), ct)) return Results.NotFound();
            var serve = size is { } wanted ? await art.SizedAsync(path, fs, wanted, ct) : path;
            context.Response.Headers.CacheControl = "private, max-age=86400";
            var type = Path.GetExtension(serve).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
            return files.Serve(serve, type, ranges: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.NotFound(); }
    }

    // MARK: - Streaming

    private static void MapStreaming(RouteGroupBuilder music)
    {
        music.MapGet("/tracks/{id:guid}/stream", (Guid id, string? codecs, int? maxBitrate, HttpContext context, MusicService service,
            MusicTranscoder transcoder, MediaScanner media, CancellationToken ct) =>
            StreamAsync(id, codecs, maxBitrate, download: false, context, service, transcoder, media, ct));

        music.MapGet("/tracks/{id:guid}/download", (Guid id, string? codecs, int? maxBitrate, HttpContext context, MusicService service,
            MusicTranscoder transcoder, MediaScanner media, CancellationToken ct) =>
            StreamAsync(id, codecs, maxBitrate, download: true, context, service, transcoder, media, ct));
    }

    private static async Task<IResult> StreamAsync(Guid id, string? codecs, int? maxBitrate, bool download, HttpContext context,
        MusicService service, MusicTranscoder transcoder, MediaScanner media, CancellationToken ct)
    {
        var catalog = await service.CatalogAsync(ct);
        if (!catalog.TracksById.TryGetValue(id, out var track)) return Results.NotFound(new { error = TrackGone });
        var files = service.Scanner.Files;
        if (!await Task.Run(() => files.FileExists(track.Path), ct))
            return Results.NotFound(new { error = MediaNetworkPath.IsNetwork(track.Path)
                ? "The music file could not be opened. Check the computer that shares it is on and awake."
                : "The music file is missing. If it is on another computer, check that the share is still mounted." });

        var options = await service.OptionsAsync(ct);
        var (transcode, kbps) = MusicTranscoder.Decide(track, MusicTranscoder.ParseCodecs(codecs), maxBitrate is > 0 ? maxBitrate : null, options.TranscodeBitrate);
        var headers = context.Response.Headers;
        if (!transcode)
        {
            headers["X-Audio-Codec"] = track.Codec;
            headers["X-Audio-Transcoded"] = "false";
            var extension = Path.GetExtension(track.Path);
            return ServeFile(files, track.Path, MusicTranscoder.ContentType(track.Path), download ? DownloadName(track, extension) : null);
        }

        if (!await media.FfmpegAvailableAsync(ct))
            return Results.Json(new { error = "This track needs converting for this device, and ffmpeg is not installed on the server." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        string converted;
        try { converted = await transcoder.ConvertAsync(track, kbps, ct); }
        catch (MusicTranscodeException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway); }
        headers["X-Audio-Codec"] = "aac";
        headers["X-Audio-Transcoded"] = "true";
        return Results.File(converted, "audio/mp4", download ? DownloadName(track, ".m4a") : null,
            lastModified: File.GetLastWriteTimeUtc(converted), enableRangeProcessing: true);
    }

    private static IResult ServeFile(MediaFiles files, string path, string contentType, string? downloadName)
    {
        if (downloadName is null) return files.Serve(path, contentType, ranges: true);
        if (!MediaNetworkPath.IsNetwork(path))
            return Results.File(path, contentType, downloadName, lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: true);
        var entry = files.Network.Stat(path);
        if (entry is null or { IsDirectory: true }) return Results.NotFound();
        return Results.Stream(files.Network.OpenRead(path), contentType, downloadName,
            lastModified: new DateTimeOffset(DateTime.SpecifyKind(entry.ModifiedUtc, DateTimeKind.Utc)), enableRangeProcessing: true);
    }

    // "Fleetwood Mac - Dreams.flac", with anything a file system would refuse taken out.
    public static string DownloadName(MusicTrack track, string extension)
    {
        var name = $"{track.PrimaryArtist} - {track.Title}";
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return (clean.Length == 0 ? "track" : clean) + extension;
    }

    // MARK: - Listening, loves, ratings, mixes

    private static void MapListening(RouteGroupBuilder music)
    {
        music.MapPost("/tracks/{id:guid}/played", async (Guid id, MusicPlayedInput input, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(ct);
            if (!catalog.TracksById.TryGetValue(id, out var track)) return Results.NotFound(new { error = TrackGone });
            await service.RecordListenAsync(UserKey(context), track, input, ct);
            return Results.NoContent();
        });

        music.MapPost("/now-playing-track", async (MusicNowPlayingInput input, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var catalog = await service.CatalogAsync(ct);
            if (!catalog.TracksById.TryGetValue(input.TrackId, out var track)) return Results.NotFound(new { error = TrackGone });
            service.PlayingNow(UserKey(context), track);
            return Results.NoContent();
        });

        foreach (var (route, kind) in new[] { ("tracks", MusicLoveKinds.Track), ("albums", MusicLoveKinds.Album), ("artists", MusicLoveKinds.Artist) })
        {
            music.MapPost($"/{route}/{{id:guid}}/love", async (Guid id, MusicLoveInput input, HttpContext context, MusicService service, CancellationToken ct) =>
            {
                if (!Exists(await service.CatalogAsync(ct), kind, id)) return Results.NotFound(new { error = GoneMessage(kind) });
                await service.Store.SetLoveAsync(new MusicLove(UserKey(context), kind, id, input.Loved, DateTimeOffset.UtcNow), ct);
                return Results.Ok(new { loved = input.Loved });
            });
        }

        foreach (var (route, kind) in new[] { ("tracks", MusicLoveKinds.Track), ("albums", MusicLoveKinds.Album) })
        {
            music.MapPost($"/{route}/{{id:guid}}/rating", async (Guid id, MusicRatingInput input, HttpContext context, MusicService service, CancellationToken ct) =>
            {
                if (input.Rating is < 0 or > 5) return Results.BadRequest(new { error = "A rating is 1 to 5 stars, or 0 to clear it." });
                if (!Exists(await service.CatalogAsync(ct), kind, id)) return Results.NotFound(new { error = GoneMessage(kind) });
                await service.Store.SetRatingAsync(new MusicRating(UserKey(context), kind, id, input.Rating, DateTimeOffset.UtcNow), ct);
                return Results.Ok(new { rating = input.Rating });
            });
        }

        music.MapGet("/mix", async (HttpContext context, MusicService service, string? mix, string? seedType, string? seedId, int? limit,
            string? excludeIds, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            var count = Math.Clamp(limit ?? 50, 1, 200);
            var exclude = (excludeIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            MusicService.MixResult? result;
            if (!string.IsNullOrWhiteSpace(mix))
            {
                result = MusicService.Mix(view, mix.Trim(), exclude, count, now);
                if (result is null) return Results.NotFound(new { error = "That mix isn't available right now. Open Music Home for today's mixes." });
            }
            else
            {
                if (seedType is null || !MusicService.SeedTypes.Contains(seedType))
                    return Results.BadRequest(new { error = $"Ask for a mix (?mix=daily-1), or a radio with seedType set to one of {string.Join(", ", MusicService.SeedTypes)} and a seedId." });
                if (string.IsNullOrWhiteSpace(seedId)) return Results.BadRequest(new { error = "Say what the radio should start from with seedId." });
                result = MusicService.Radio(view, seedType, seedId.Trim(), await service.VisiblePlaylistsAsync(view.User.UserKey, ct), exclude, count, now);
                if (result is null) return Results.NotFound(new { error = "That isn't in the library any more." });
            }
            return Results.Ok(new { name = result.Name, tracks = result.Tracks.Select(t => view.Track(t)) });
        });

        music.MapGet("/shuffle", async (HttpContext context, MusicService service, int? limit, CancellationToken ct) =>
        {
            var view = await ViewAsync(service, context, ct);
            var tracks = MusicDiscovery.Shuffle(view, Math.Clamp(limit ?? 200, 1, 1000), DateTimeOffset.UtcNow, Random.Shared);
            return Results.Ok(new { name = "Shuffle All", tracks = tracks.Select(t => view.Track(t)) });
        });
    }

    private static bool Exists(MusicCatalog catalog, string kind, Guid id) => kind switch
    {
        MusicLoveKinds.Track => catalog.TracksById.ContainsKey(id),
        MusicLoveKinds.Album => catalog.AlbumsById.ContainsKey(id),
        _ => catalog.ArtistsById.ContainsKey(id)
    };

    private static string GoneMessage(string kind) => kind switch
    {
        MusicLoveKinds.Track => TrackGone,
        MusicLoveKinds.Album => AlbumGone,
        _ => ArtistGone
    };

    // MARK: - Playlists

    private static IResult Forbidden(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);

    private static void MapPlaylists(RouteGroupBuilder music)
    {
        async Task<(MusicView View, HashSet<Guid> Pins)> ContextAsync(HttpContext context, MusicService service, CancellationToken ct)
        {
            var view = await ViewAsync(service, context, ct);
            var pins = (await service.Store.GetPinsAsync(view.User.UserKey, ct)).Select(p => p.PlaylistId).ToHashSet();
            return (view, pins);
        }

        MusicPlaylistDto Summary(MusicView view, HashSet<Guid> pins, MusicPlaylist playlist) =>
            view.Playlist(playlist, MusicService.PlaylistTracks(view, playlist, DateTimeOffset.UtcNow).Select(x => x.Track).ToList(), pins.Contains(playlist.Id));

        async Task<MusicPlaylist?> VisibleAsync(MusicService service, HttpContext context, Guid id, CancellationToken ct) =>
            await service.Store.GetPlaylistAsync(id, ct) is { } playlist && (playlist.CanSee(UserKey(context)) || IsAdmin(context)) ? playlist : null;

        music.MapGet("/playlists", async (HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var (view, pins) = await ContextAsync(context, service, ct);
            var playlists = await service.VisiblePlaylistsAsync(view.User.UserKey, ct);
            return Results.Ok(playlists
                .OrderByDescending(p => pins.Contains(p.Id)).ThenByDescending(p => p.OwnerKey == view.User.UserKey).ThenByDescending(p => p.UpdatedAt)
                .Select(p => Summary(view, pins, p)));
        });

        music.MapPost("/playlists", async (MusicPlaylistInput input, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var errors = ValidatePlaylist(input, requireName: true);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var (view, pins) = await ContextAsync(context, service, ct);
            var now = DateTimeOffset.UtcNow;
            var shared = input.Shared == true || input.Collaborative == true;
            var entries = input.Rules is not null ? [] : (input.TrackIds ?? [])
                .Where(view.Catalog.TracksById.ContainsKey)
                .Select(t => new MusicPlaylistEntry(Guid.NewGuid(), t, now, view.User.UserKey)).ToList();
            var playlist = new MusicPlaylist(Guid.NewGuid(), view.User.UserKey, input.Name!.Trim(), input.Description?.Trim() ?? "",
                shared, input.Collaborative == true, input.Rules, now, now, entries);
            await service.Store.SavePlaylistAsync(playlist, ct);
            return Results.Created($"/api/music/playlists/{playlist.Id}", Summary(view, pins, playlist));
        });

        music.MapGet("/playlists/{id:guid}", async (Guid id, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            var (view, pins) = await ContextAsync(context, service, ct);
            var tracks = MusicService.PlaylistTracks(view, playlist, DateTimeOffset.UtcNow);
            var summary = view.Playlist(playlist, tracks.Select(x => x.Track).ToList(), pins.Contains(playlist.Id));
            return Results.Ok(new MusicPlaylistDetailDto
            {
                Id = summary.Id, Name = summary.Name, Description = summary.Description, OwnerName = summary.OwnerName, Mine = summary.Mine,
                Shared = summary.Shared, Collaborative = summary.Collaborative, Smart = summary.Smart, Rules = summary.Rules,
                TrackCount = summary.TrackCount, DurationSeconds = summary.DurationSeconds, ArtUrls = summary.ArtUrls,
                CreatedAt = summary.CreatedAt, UpdatedAt = summary.UpdatedAt, Pinned = summary.Pinned,
                Tracks = tracks.Select(x => view.Track(x.Track, x.EntryId)).ToList()
            });
        });

        music.MapPut("/playlists/{id:guid}", async (Guid id, MusicPlaylistInput input, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            if (!playlist.CanEdit(UserKey(context), IsAdmin(context))) return Forbidden("Only the person who made this playlist can change it.");
            var errors = ValidatePlaylist(input, requireName: false);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var collaborative = input.Collaborative ?? playlist.Collaborative;
            var updated = playlist with
            {
                Name = string.IsNullOrWhiteSpace(input.Name) ? playlist.Name : input.Name.Trim(),
                Description = input.Description?.Trim() ?? playlist.Description,
                Shared = (input.Shared ?? playlist.Shared) || collaborative,
                Collaborative = collaborative,
                Rules = playlist.Smart ? input.Rules ?? playlist.Rules : playlist.Rules,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await service.Store.SavePlaylistAsync(updated, ct);
            var (view, pins) = await ContextAsync(context, service, ct);
            return Results.Ok(Summary(view, pins, updated));
        });

        music.MapDelete("/playlists/{id:guid}", async (Guid id, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            if (!playlist.CanEdit(UserKey(context), IsAdmin(context))) return Forbidden("Only the person who made this playlist can delete it.");
            await service.Store.DeletePlaylistAsync(id, ct);
            return Results.NoContent();
        });

        music.MapPost("/playlists/{id:guid}/tracks", async (Guid id, MusicPlaylistTracksInput input, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            if (playlist.Smart) return Results.BadRequest(new { error = "A smart playlist fills itself from its rules. Change the rules instead." });
            if (!playlist.CanEditTracks(UserKey(context), IsAdmin(context)))
                return Forbidden("Only the person who made this playlist can add to it, unless they make it collaborative.");
            var (view, pins) = await ContextAsync(context, service, ct);
            var now = DateTimeOffset.UtcNow;
            var added = (input.TrackIds ?? []).Where(view.Catalog.TracksById.ContainsKey)
                .Select(t => new MusicPlaylistEntry(Guid.NewGuid(), t, now, view.User.UserKey)).ToList();
            if (added.Count == 0 && (input.TrackIds?.Count ?? 0) > 0) return Results.NotFound(new { error = TrackGone });
            var entries = playlist.Entries.ToList();
            var at = input.Position is { } position ? Math.Clamp(position, 0, entries.Count) : entries.Count;
            entries.InsertRange(at, added);
            var updated = playlist with { Entries = entries, UpdatedAt = now };
            await service.Store.SavePlaylistAsync(updated, ct);
            return Results.Ok(Summary(view, pins, updated));
        });

        music.MapDelete("/playlists/{id:guid}/tracks/{entryId:guid}", async (Guid id, Guid entryId, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            if (!playlist.CanEditTracks(UserKey(context), IsAdmin(context))) return Forbidden("Only the person who made this playlist can take tracks out of it.");
            if (playlist.Entries.All(e => e.EntryId != entryId)) return Results.NotFound(new { error = "That track is not in this playlist any more." });
            await service.Store.SavePlaylistAsync(playlist with
            {
                Entries = playlist.Entries.Where(e => e.EntryId != entryId).ToList(),
                UpdatedAt = DateTimeOffset.UtcNow
            }, ct);
            return Results.NoContent();
        });

        music.MapPut("/playlists/{id:guid}/order", async (Guid id, MusicPlaylistOrderInput input, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            if (!playlist.CanEditTracks(UserKey(context), IsAdmin(context))) return Forbidden("Only the person who made this playlist can reorder it.");
            var byId = playlist.Entries.ToDictionary(e => e.EntryId);
            var wanted = input.EntryIds ?? [];
            if (wanted.Count != byId.Count || wanted.Distinct().Count() != wanted.Count || wanted.Any(e => !byId.ContainsKey(e)))
                return Results.BadRequest(new { error = "That order doesn't match the playlist's tracks - someone may have just changed it. Reload it and try again." });
            await service.Store.SavePlaylistAsync(playlist with { Entries = wanted.Select(e => byId[e]).ToList(), UpdatedAt = DateTimeOffset.UtcNow }, ct);
            return Results.NoContent();
        });

        music.MapPost("/playlists/{id:guid}/pin", async (Guid id, MusicPinInput input, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            if (await VisibleAsync(service, context, id, ct) is null) return Results.NotFound(new { error = PlaylistGone });
            await service.Store.SetPinnedAsync(UserKey(context), id, input.Pinned, ct);
            return Results.Ok(new { pinned = input.Pinned });
        });

        music.MapPost("/playlists/{id:guid}/duplicate", async (Guid id, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            var (view, pins) = await ContextAsync(context, service, ct);
            var now = DateTimeOffset.UtcNow;
            // A smart playlist's copy is an ordinary one, holding the tracks its rules give today.
            var entries = MusicService.PlaylistTracks(view, playlist, now)
                .Select(x => new MusicPlaylistEntry(Guid.NewGuid(), x.Track.Id, now, view.User.UserKey)).ToList();
            var copy = new MusicPlaylist(Guid.NewGuid(), view.User.UserKey, $"{playlist.Name} (copy)", playlist.Description,
                false, false, null, now, now, entries);
            await service.Store.SavePlaylistAsync(copy, ct);
            return Results.Created($"/api/music/playlists/{copy.Id}", Summary(view, pins, copy));
        });

        music.MapGet("/playlists/{id:guid}/m3u", async (Guid id, HttpContext context, MusicService service, CancellationToken ct) =>
        {
            var playlist = await VisibleAsync(service, context, id, ct);
            if (playlist is null) return Results.NotFound(new { error = PlaylistGone });
            var view = await ViewAsync(service, context, ct);
            var text = new StringBuilder("#EXTM3U\n");
            text.Append("#PLAYLIST:").Append(playlist.Name).Append('\n');
            foreach (var (track, _) in MusicService.PlaylistTracks(view, playlist, DateTimeOffset.UtcNow))
            {
                text.Append("#EXTINF:").Append(Math.Round(track.DurationSeconds).ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(track.PrimaryArtist).Append(" - ").Append(track.Title).Append('\n');
                text.Append(track.RelativePath).Append('\n');
            }
            var name = new string(playlist.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray());
            return Results.File(Encoding.UTF8.GetBytes(text.ToString()), "audio/x-mpegurl", $"{name}.m3u8");
        });
    }

    private static List<string> ValidatePlaylist(MusicPlaylistInput input, bool requireName)
    {
        var errors = new List<string>();
        if (requireName && string.IsNullOrWhiteSpace(input.Name)) errors.Add("Give the playlist a name.");
        if (input.Name is { Length: > 200 }) errors.Add("That name is too long; keep it under 200 characters.");
        if (input.Rules is { } rules) errors.AddRange(MusicSmartPlaylists.Validate(rules));
        return errors;
    }
}
