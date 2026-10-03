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
    // TMDB's 0-10 vote average; 7 and up is "people liked it".
    private const double TopRatedFloor = 7.0;
    // A genre needs this many titles before it gets its own shelf on Home.
    private const int GenreShelfMinimum = 6;
    private const int GenreShelfCount = 3;
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

        enabled.MapPost("/libraries", async (MediaLibraryInput input, IMediaStore store, MediaScanner scanner, MediaFiles files, CancellationToken ct) =>
        {
            var errors = await ValidateLibraryAsync(input, null, store, files, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var library = new MediaLibrary(Guid.NewGuid(), input.Name.Trim(), input.Kind, NormalizePath(input.Path), DateTimeOffset.UtcNow);
            await store.SaveLibraryAsync(library, ct);
            scanner.RequestScan(library.Id);
            return Results.Created($"/api/media/libraries/{library.Id}", LibraryDto(library));
        }).RequireAuthorization("AdminOnly");

        enabled.MapPut("/libraries/{id:guid}", async (Guid id, MediaLibraryInput input, IMediaStore store, MediaScanner scanner, MediaFiles files, CancellationToken ct) =>
        {
            var existing = await store.GetLibraryAsync(id, ct);
            if (existing is null) return Results.NotFound(new { error = "That library no longer exists." });
            var errors = await ValidateLibraryAsync(input, id, store, files, ct);
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

        enabled.MapGet("/browse", async (string? path, MediaFiles files, CancellationToken ct) =>
            Results.Ok(await Task.Run(() => Browse(path, files), ct))).RequireAuthorization("AdminOnly");

        MapNetwork(enabled);
    }

    private static async Task<List<string>> ValidateLibraryAsync(MediaLibraryInput input, Guid? id, IMediaStore store, MediaFiles files, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name)) errors.Add("Give the library a name, like Movies or Kids TV.");
        if (!MediaLibraryKinds.IsValid(input.Kind)) errors.Add("Kind must be movies or shows.");
        var path = input.Path?.Trim() ?? "";
        if (path.Length == 0) errors.Add("Choose the folder the videos are in.");
        else if (MediaNetworkPath.IsNetwork(path))
        {
            if (!MediaNetworkPath.TryParse(path, out var network))
                errors.Add("That network address is not complete. It looks like smb://computer/SharedFolder/Movies.");
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
        else if (!Path.IsPathRooted(path)) errors.Add("Use the folder's full path, like /mnt/movies/Media/Movies.");
        else if (!Directory.Exists(path))
            errors.Add($"{path} does not exist on the server. For a folder on another computer, choose it under Network computers in the folder picker, or type its network address (smb://PC/Movies).");

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

    // The folder picker every client shows. With no path: the places worth starting from on the
    // server itself, plus the network computers already signed in to. A computer (smb://host)
    // lists its shared folders; anything below that, or any local path, lists its folders.
    // Failures come back as `error` (and `needsLogin` when a sign-in would fix it) with a 200, so
    // a client can show the message and a sign-in form without special-casing status codes.
    public static object Browse(string? path, MediaFiles files, Func<string, bool>? isMediaFile = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var computers = files.Network.Computers.All()
                .OrderBy(c => c.Name ?? c.Host, StringComparer.OrdinalIgnoreCase)
                .Select(c => ComputerDto(c))
                .ToList();
            return new
            {
                path = "",
                parent = (string?)null,
                kind = "roots",
                folders = LocalRoots().Select(r => Folder(r.Name, r.Path, isMediaFile)).ToList(),
                computers,
                videoCount = 0
            };
        }

        if (MediaNetworkPath.IsNetwork(path))
        {
            if (!MediaNetworkPath.TryParse(path, out var network))
                return BrowseError(path, "", "That network address is not complete. It looks like smb://computer/SharedFolder.", false, null);
            var parent = network.Parent()?.ToString() ?? "";
            try
            {
                if (network.Share.Length == 0)
                {
                    var shares = files.Network.ListShares(network);
                    return new
                    {
                        path = network.ToString(),
                        parent,
                        kind = "computer",
                        host = network.HostWithPort,
                        folders = shares.Select(s => new { name = s, path = network.Child(s).ToString(), videoCount = (int?)null }).ToList(),
                        videoCount = 0,
                        hint = shares.Count == 0
                            ? $"{network.Host} did not list any shared folders. Type the shared folder's name into the path, like smb://{network.HostWithPort}/Movies."
                            : null
                    };
                }

                var entries = files.Network.List(network.ToString());
                return new
                {
                    path = network.ToString(),
                    parent,
                    kind = "network",
                    host = network.HostWithPort,
                    folders = entries.Where(e => e.IsDirectory && !IsHiddenFolder(e.Name))
                        .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(1000)
                        .Select(e => new { name = e.Name, path = e.Path, videoCount = (int?)null })
                        .ToList(),
                    videoCount = entries.Count(e => !e.IsDirectory && (isMediaFile ?? MediaNaming.IsVideoFile)(e.Name))
                };
            }
            catch (MediaNetworkException ex) { return BrowseError(network.ToString(), parent, ex.Message, ex.NeedsLogin, network.HostWithPort); }
            catch (UnauthorizedAccessException ex) { return BrowseError(network.ToString(), parent, ex.Message, true, network.HostWithPort); }
            catch (IOException ex) { return BrowseError(network.ToString(), parent, ex.Message, false, network.HostWithPort); }
        }

        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BrowseError(path, "", "That is not a folder path this server understands.", false, null);
        }
        var localParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)) ?? "";
        if (!Directory.Exists(full)) return BrowseError(full, localParent, $"{full} does not exist on the server.", false, null);
        var folders = new List<object>();
        var videos = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(1000))
            {
                var name = Path.GetFileName(directory);
                if (IsHiddenFolder(name)) continue;
                folders.Add(Folder(name, directory, isMediaFile));
            }
            videos = Directory.EnumerateFiles(full).Take(5000).Count(isMediaFile ?? MediaNaming.IsVideoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BrowseError(full, localParent, $"The server cannot read {full}: {ex.Message}", false, null);
        }
        return new { path = full, parent = localParent, kind = "local", folders, videoCount = videos };
    }

    private static object BrowseError(string path, string parent, string error, bool needsLogin, string? host) => new
    {
        path,
        parent,
        kind = MediaNetworkPath.IsNetwork(path) ? "network" : "local",
        host,
        folders = new List<object>(),
        videoCount = 0,
        error,
        needsLogin
    };

    private static bool IsHiddenFolder(string name) =>
        name.StartsWith('.') || name is "$RECYCLE.BIN" or "System Volume Information" or "@eaDir" or "#recycle"
        || name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase);

    // Where a person would look for a movie folder on this server: every drive on Windows; on
    // Linux and macOS the usual mount folders, the home folder, every mounted disk (a USB drive
    // at /media/me/Passport, an NTFS disk at /mnt/movies, /Volumes/Movies on a Mac) and "/" for
    // anything else.
    private static List<(string Name, string Path)> LocalRoots()
    {
        if (OperatingSystem.IsWindows())
            return DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => (string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName))
                .ToList();

        var roots = new List<(string Name, string Path)>();
        void Add(string name, string path)
        {
            if (path.Length == 0 || !Directory.Exists(path)) return;
            if (roots.Any(r => string.Equals(r.Path, path, StringComparison.Ordinal))) return;
            roots.Add((name, path));
        }

        string[] systemPrefixes = ["/proc", "/sys", "/dev", "/run", "/snap", "/boot", "/var", "/System", "/private", "/tmp", "/etc", "/usr", "/Library", "/opt"];
        var mounts = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                var mount = drive.RootDirectory.FullName.TrimEnd('/');
                if (mount.Length == 0) continue;
                if (systemPrefixes.Any(p => mount == p || mount.StartsWith(p + "/", StringComparison.Ordinal))) continue;
                // On a Mac every disk a person plugs in or connects appears under /Volumes; the
                // other mounts are the system's own (Xcode's simulator images, for one).
                if (OperatingSystem.IsMacOS() && !mount.StartsWith("/Volumes/", StringComparison.Ordinal)) continue;
                if (mount.StartsWith("/Volumes/", StringComparison.Ordinal) && mount.Contains("Recovery", StringComparison.OrdinalIgnoreCase)) continue;
                mounts.Add(mount);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var mount in mounts.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)) Add(mount, mount);
        foreach (var path in new[] { "/mnt", "/media", "/srv", "/data", "/Volumes" }) Add(path, path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add($"Home ({home})", home);
        Add("/", "/");
        return roots;
    }

    private static object Folder(string name, string path, Func<string, bool>? isMediaFile)
    {
        var videos = 0;
        try
        {
            // Capped: a folder on a slow share with ten thousand files is not worth a stall for a hint.
            foreach (var file in Directory.EnumerateFiles(path).Take(2000))
                if ((isMediaFile ?? MediaNaming.IsVideoFile)(file)) videos++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new { name, path, videoCount = (int?)videos };
    }

    // MARK: - Network computers

    public static void MapNetwork(RouteGroupBuilder enabled)
    {
        var network = enabled.MapGroup("/network").RequireAuthorization("AdminOnly");

        network.MapGet("", (MediaFiles files) =>
            Results.Ok(files.Network.Computers.All().OrderBy(c => c.Name ?? c.Host, StringComparer.OrdinalIgnoreCase).Select(c => ComputerDto(c))));

        // Looks for computers sharing files for a few seconds (mDNS). Windows computers do not
        // show up here; they are added by name or address.
        network.MapGet("/discover", async (MediaFiles files, MediaNetworkResolver resolver, CancellationToken ct) =>
        {
            var saved = files.Network.Computers.All();
            var found = await resolver.DiscoverAsync(TimeSpan.FromSeconds(3), ct);
            return Results.Ok(found.Select(h =>
            {
                var match = saved.FirstOrDefault(c =>
                    string.Equals(c.Host, h.Host, StringComparison.OrdinalIgnoreCase)
                    || (h.Address is not null && string.Equals(c.Host, h.Address, StringComparison.OrdinalIgnoreCase)));
                return new
                {
                    name = h.Name,
                    host = h.Host,
                    address = h.Address,
                    path = MediaNetworkPath.Scheme + h.Host,
                    savedId = match?.Id
                };
            }));
        });

        // Signs in to a computer and saves the sign-in when it works. Saving the same host again
        // replaces the old sign-in, so "sign in again" is always this call.
        network.MapPost("", async (MediaNetworkComputerInput input, MediaFiles files, CancellationToken ct) =>
            await SaveComputerAsync(null, input, files, ct));

        network.MapPut("/{id:guid}", async (Guid id, MediaNetworkComputerInput input, MediaFiles files, CancellationToken ct) =>
            files.Network.Computers.Get(id) is null
                ? Results.NotFound(new { error = "That computer is not saved any more." })
                : await SaveComputerAsync(id, input, files, ct));

        network.MapDelete("/{id:guid}", (Guid id, MediaFiles files) =>
        {
            files.Network.Computers.Delete(id);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> SaveComputerAsync(Guid? id, MediaNetworkComputerInput input, MediaFiles files, CancellationToken ct)
    {
        var hostText = input.Host?.Trim() ?? "";
        if (!MediaNetworkPath.TryParse(MediaNetworkPath.IsNetwork(hostText) ? hostText : MediaNetworkPath.Scheme + hostText, out var parsed))
            return Results.BadRequest(new { errors = new[] { "Type the computer's name or IP address, like DESKTOP-PC or 192.168.1.20." } });
        var computerPath = parsed with { Share = "", Rest = "" };
        var store = files.Network.Computers;
        var existing = id is { } known ? store.Get(known) : store.ForHost(computerPath.HostKey);
        var username = input.Username?.Trim() ?? "";
        var domain = input.Domain?.Trim() ?? "";
        // "WORKGROUP\me" typed into the username box.
        if (domain.Length == 0 && username.Contains('\\'))
        {
            var slash = username.IndexOf('\\');
            (domain, username) = (username[..slash], username[(slash + 1)..]);
        }
        var password = string.IsNullOrEmpty(input.Password) && existing is not null && string.Equals(existing.Username, username, StringComparison.OrdinalIgnoreCase)
            ? existing.Password
            : input.Password ?? "";
        var candidate = new MediaNetworkComputer(existing?.Id ?? Guid.NewGuid(), computerPath.HostWithPort, username, password, domain,
            existing?.CreatedAt ?? DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(input.Name) ? existing?.Name : input.Name.Trim());

        IReadOnlyList<string> shares;
        try { shares = await Task.Run(() => files.Network.ListShares(computerPath, candidate), ct); }
        catch (MediaNetworkException ex)
        {
            return Results.BadRequest(new { errors = new[] { ex.Message }, needsLogin = ex.NeedsLogin });
        }
        store.Save(candidate);
        var body = new
        {
            computer = ComputerDto(candidate),
            shares = shares.Select(s => new { name = s, path = computerPath.Child(s).ToString() })
        };
        return existing is null
            ? Results.Created($"/api/media/network/{candidate.Id}", body)
            : Results.Ok(body);
    }

    private static object ComputerDto(MediaNetworkComputer computer) => new
    {
        id = computer.Id,
        name = computer.Name ?? computer.Host,
        host = computer.Host,
        username = computer.Username,
        domain = computer.Domain,
        hasPassword = computer.Password.Length > 0,
        path = MediaNetworkPath.Scheme + computer.Host,
        createdAt = computer.CreatedAt
    };

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
                myList = view.MyList().Take(ShelfSize).Select(view.Summary),
                mostPlayed = view.MostPlayed().Take(ShelfSize).Select(view.Summary),
                recentlyWatched = view.RecentlyWatched(continueWatching).Take(ShelfSize).Select(view.Summary),
                topRated = view.TopRated().Take(ShelfSize).Select(view.Summary),
                genreShelves = view.GenreShelves(GenreShelfCount, DateTimeOffset.UtcNow)
                    .Select(shelf => new { genre = shelf.Genre, items = shelf.Items.Select(view.Summary) }),
                featured = view.Featured(continueWatching, recentMovies, shows) is { } featured ? view.Summary(featured) : null,
                movieCount = movies.Count,
                showCount = shows.Count,
                libraries = (await store.GetLibrariesAsync(ct)).Select(LibraryDto)
            });
        });

        enabled.MapGet("/items", async (
            HttpContext context, IMediaStore store, string? kind, Guid? libraryId, string? q, string? genre, string? sort, string? order,
            bool? unwatched, bool? list, int? offset, int? limit, CancellationToken ct) =>
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
            if (list == true) items = items.Where(view.InList);

            var sortKey = sort?.ToLowerInvariant() ?? "title";
            var descending = order is null ? sortKey is "added" or "rating" or "year" or "plays" : order.Equals("desc", StringComparison.OrdinalIgnoreCase);
            items = sortKey switch
            {
                "plays" => descending
                    ? items.OrderByDescending(view.PlayCount).ThenByDescending(i => view.PlaysOf(i)?.LastPlayedAt ?? DateTimeOffset.MinValue).ThenBy(i => i.SortTitle, StringComparer.Ordinal)
                    : Order(items, view.PlayCount, descending),
                "added" => Order(items, i => i.Kind == MediaKinds.Show ? view.LatestEpisodeAdded(i) : i.AddedAt, descending),
                "year" => Order(items, i => i.Year ?? 0, descending),
                "rating" => Order(items, i => i.Rating ?? 0, descending),
                _ => Order(items, i => i.SortTitle, descending)
            };

            var matches = items.ToList();
            var skip = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
            return Results.Ok(new
            {
                items = matches.Skip(skip).Take(take).Select(view.Summary),
                total = matches.Count,
                genres = await store.GenresAsync(kind is MediaKinds.Movie or MediaKinds.Show ? kind : null, libraryId, ct)
            });
        });

        // "Surprise me": one movie or show at random, unwatched ones first.
        enabled.MapGet("/random", async (HttpContext context, IMediaStore store, string? kind, Guid? libraryId, string? genre, bool? unwatched, CancellationToken ct) =>
        {
            var view = await MediaView.LoadAsync(store, UserKey(context), ct);
            var pool = view.Items.Where(i => (kind is MediaKinds.Movie or MediaKinds.Show ? i.Kind == kind : i.Kind is MediaKinds.Movie or MediaKinds.Show)
                && (libraryId is null || i.LibraryId == libraryId)
                && (string.IsNullOrWhiteSpace(genre) || i.Genres.Contains(genre.Trim(), StringComparer.OrdinalIgnoreCase))).ToList();
            if (unwatched != false && pool.Where(i => !view.IsWatched(i)).ToList() is { Count: > 0 } fresh) pool = fresh;
            return pool.Count == 0
                ? Results.NotFound(new { error = "Nothing in the library matches that yet." })
                : Results.Ok(view.Summary(pool[Random.Shared.Next(pool.Count)]));
        });

        enabled.MapGet("/items/{id:guid}", async (Guid id, HttpContext context, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var view = await MediaView.LoadAsync(store, UserKey(context), ct);
            var item = view.Find(id);
            return item is null
                ? Results.NotFound(new { error = "That title is not in the library any more." })
                : Results.Ok(view.Detail(item, IsAdmin(context), scanner.SeasonPoster));
        });

        // Deletes a movie's files from the disk and forgets it. Cannot be undone.
        enabled.MapDelete("/items/{id:guid}", async (Guid id, IMediaStore store, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null) return Results.NotFound(new { error = "That title is not in the library any more." });
            var library = await store.GetLibraryAsync(item.LibraryId, ct);
            if (MediaItemDeletion.Problem(item, library) is { } problem) return Results.BadRequest(new { error = problem });
            try
            {
                await Task.Run(() => MediaItemDeletion.Delete(item.Path, library!.Path), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = $"Could not delete {item.Title}: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
            }
            await store.DeleteItemAsync(id, ct);
            loggers.CreateLogger("SentinelCore.Media").LogInformation("Media: deleted {Title} ({Path})", item.Title, item.Path);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        enabled.MapGet("/items/{id:guid}/{art:regex(^(poster|backdrop|thumb)$)}", async (
            Guid id, string art, HttpContext context, IMediaStore store, MediaScanner scanner, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null) return Results.NotFound();
            string? path;
            try { path = await scanner.ResolveArtworkAsync(item, art, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { path = null; }
            return ServeImage(context, scanner.Files, path);
        });

        enabled.MapGet("/items/{id:guid}/seasons/{season:int}/poster", (Guid id, int season, HttpContext context, MediaScanner scanner) =>
            ServeImage(context, scanner.Files, scanner.SeasonPoster(id, season)));
    }

    private static IOrderedEnumerable<MediaItem> Order<TKey>(IEnumerable<MediaItem> items, Func<MediaItem, TKey> key, bool descending) =>
        descending
            ? items.OrderByDescending(key).ThenBy(i => i.SortTitle, StringComparer.Ordinal)
            : items.OrderBy(key).ThenBy(i => i.SortTitle, StringComparer.Ordinal);

    private static IResult ServeImage(HttpContext context, MediaFiles files, string? path)
    {
        if (path is null || !files.FileExists(path)) return Results.NotFound();
        // The URL carries a version (see MediaView.ArtUrl), so a changed match is a new URL and
        // this can be cached freely.
        context.Response.Headers.CacheControl = "private, max-age=86400";
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        try { return files.Serve(path, type, ranges: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.NotFound(); }
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
            if (!await Task.Run(() => scanner.Files.FileExists(item.Path), ct))
                return Results.NotFound(new { error = MediaNetworkPath.IsNetwork(item.Path)
                    ? "The video file could not be opened. Check the computer that shares it is on and awake."
                    : "The video file is missing. If it is on another computer, check that the share is still mounted." });

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
            var play = new MediaPlay(UserKey(context), item.Id, DateTimeOffset.UtcNow);

            if (plan.Direct)
            {
                await store.RecordPlayAsync(play, MediaPlay.SameSitting, ct);
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
                session = await hls.StartAsync(item, plan, startSeconds, item.SubtitleTracks, ct, scanner.Files.FfmpegInput(item.Path));
            }
            catch (MediaPlaybackException ex)
            {
                loggers.CreateLogger("SentinelCore.Web.MediaEndpoints").LogWarning(
                    "Could not start {Title}: {Message} {Log}", item.Title, ex.Message, string.Join(" | ", ex.FfmpegLog));
                // Neither the server's folders nor the relay's secret URL belong in a client's error.
                var input = scanner.Files.FfmpegInput(item.Path);
                var tail = ex.FfmpegLog.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(3)
                    .Select(line => line.Replace(input, Path.GetFileName(item.Path)).Replace(item.Path, Path.GetFileName(item.Path)));
                return Results.Json(new { error = ex.Message, detail = string.Join("\n", tail) }, statusCode: StatusCodes.Status502BadGateway);
            }
            await store.RecordPlayAsync(play, MediaPlay.SameSitting, ct);

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

        enabled.MapGet("/stream/{id:guid}/{fileName}", async (Guid id, IMediaStore store, MediaFiles files, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null || !item.IsPlayable) return Results.NotFound();
            var extension = Path.GetExtension(item.Path).TrimStart('.');
            try
            {
                return await Task.Run(() => files.FileExists(item.Path)
                    ? files.Serve(item.Path, MediaPlaybackPlanner.MimeType(extension), ranges: true)
                    : Results.NotFound(), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.NotFound(); }
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
            var path = await MediaSubtitles.EnsureVttAsync(item, track, scanner.Files, cache.SubtitlesRoot, scanner.FfmpegPath,
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

        enabled.MapPost("/items/{id:guid}/list", async (Guid id, MediaListInput input, HttpContext context, IMediaStore store, CancellationToken ct) =>
        {
            var item = await store.GetItemAsync(id, ct);
            if (item is null) return Results.NotFound(new { error = "That title is not in the library any more." });
            // My List holds movies and shows; saving an episode saves its show.
            var target = item.Kind == MediaKinds.Episode && item.ParentId is { } parent ? parent : item.Id;
            await store.SetInWatchlistAsync(UserKey(context), target, input.InList, ct);
            return Results.Ok(new { itemId = target, inList = input.InList });
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
        network = MediaNetworkPath.IsNetwork(library.Path),
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

        private readonly IReadOnlyDictionary<Guid, MediaPlayStats> _plays;
        private readonly Dictionary<Guid, DateTimeOffset> _list;

        private MediaView(IReadOnlyList<MediaItem> items, IReadOnlyList<MediaProgress> progress,
            IReadOnlyDictionary<Guid, MediaPlayStats> plays, IReadOnlyList<MediaWatchlistEntry> watchlist)
        {
            Items = items;
            _plays = plays;
            _list = watchlist.GroupBy(w => w.ItemId).ToDictionary(g => g.Key, g => g.First().AddedAt);
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
            new(await store.GetItemsAsync(new MediaQuery(), cancellationToken), await store.GetProgressAsync(userKey, cancellationToken),
                await store.PlayStatsAsync(cancellationToken), await store.GetWatchlistAsync(userKey, cancellationToken));

        // Everyone's plays. A show counts the plays of its episodes.
        public MediaPlayStats? PlaysOf(MediaItem item)
        {
            if (item.Kind != MediaKinds.Show) return _plays.GetValueOrDefault(item.Id);
            var episodes = EpisodesOf(item.Id).Select(e => _plays.GetValueOrDefault(e.Id)).OfType<MediaPlayStats>().ToList();
            return episodes.Count == 0 ? null : new MediaPlayStats(episodes.Sum(p => p.Count), episodes.Max(p => p.LastPlayedAt));
        }

        public int PlayCount(MediaItem item) => PlaysOf(item)?.Count ?? 0;

        public bool InList(MediaItem item) => _list.ContainsKey(item.Id);

        // My List, newest addition first. Only movies and shows go in it.
        public IEnumerable<MediaItem> MyList() => _list.OrderByDescending(e => e.Value)
            .Select(e => Find(e.Key)).OfType<MediaItem>().Where(i => i.Kind is MediaKinds.Movie or MediaKinds.Show);

        // The household's favourites: most plays first, the more recently played breaking ties.
        public IEnumerable<MediaItem> MostPlayed() => Items
            .Where(i => i.Kind is MediaKinds.Movie or MediaKinds.Show)
            .Select(i => (Item: i, Plays: PlaysOf(i)))
            .Where(p => p.Plays is { Count: > 0 })
            .OrderByDescending(p => p.Plays!.Count).ThenByDescending(p => p.Plays!.LastPlayedAt).ThenBy(p => p.Item.SortTitle, StringComparer.Ordinal)
            .Select(p => p.Item);

        // What this person finished lately: movies they watched to the end and shows they have
        // seen episodes of, newest first. Anything already on Continue Watching is left off.
        public IEnumerable<MediaItem> RecentlyWatched(IReadOnlyCollection<MediaItem> continueWatching)
        {
            var skip = continueWatching.Select(i => i.Kind == MediaKinds.Episode && i.ParentId is { } show ? show : i.Id).ToHashSet();
            var seen = new HashSet<Guid>();
            foreach (var progress in Progress.Where(p => p.Watched).OrderByDescending(p => p.UpdatedAt))
            {
                if (Find(progress.ItemId) is not { } item) continue;
                var target = item.Kind == MediaKinds.Episode && item.ParentId is { } parent ? Find(parent) : item;
                if (target is null || skip.Contains(target.Id) || !seen.Add(target.Id)) continue;
                yield return target;
            }
        }

        // Well-reviewed titles this person has not finished yet. Needs TMDB ratings.
        public IEnumerable<MediaItem> TopRated() => Items
            .Where(i => i.Kind is MediaKinds.Movie or MediaKinds.Show && i.Rating is >= TopRatedFloor && !IsWatched(i))
            .OrderByDescending(i => i.Rating).ThenBy(i => i.SortTitle, StringComparer.Ordinal);

        // A few genre shelves that change daily: genres with enough titles to fill a row, picked
        // by the day of the year, each row unwatched-first in a daily shuffle.
        public List<(string Genre, List<MediaItem> Items)> GenreShelves(int count, DateTimeOffset now)
        {
            var titles = Items.Where(i => i.Kind is MediaKinds.Movie or MediaKinds.Show).ToList();
            var genres = titles.SelectMany(i => i.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= GenreShelfMinimum)
                .Select(g => g.Key).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
            if (genres.Count == 0) return [];
            var day = now.UtcDateTime.DayOfYear + now.UtcDateTime.Year * 366;
            var start = day % genres.Count;
            return Enumerable.Range(0, Math.Min(count, genres.Count))
                .Select(offset => genres[(start + offset * Math.Max(1, genres.Count / count)) % genres.Count])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(genre => (genre, titles
                    .Where(i => i.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(IsWatched)
                    .ThenBy(i => DailyShuffle(i.Id, day))
                    .Take(ShelfSize).ToList()))
                .ToList();
        }

        // Guid.GetHashCode is stable across restarts (HashCode.Combine is not), so the day's
        // order survives the server restarting.
        private static uint DailyShuffle(Guid id, int day)
        {
            var x = unchecked((uint)id.GetHashCode() ^ (uint)day * 2654435761u);
            x ^= x >> 16;
            x = unchecked(x * 0x7feb352du);
            x ^= x >> 15;
            x = unchecked(x * 0x846ca68bu);
            return x ^ (x >> 16);
        }

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
                Progress = item.IsPlayable ? ProgressDto(ProgressOf(item.Id)) : null,
                PlayCount = PlayCount(item),
                InList = InList(item)
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
                summary.WatchedEpisodeCount, summary.NextEpisodeId, summary.Overview, summary.PlayCount, summary.InList,
                tagline = item.Tagline,
                releaseDate = item.ReleaseDate,
                cast = item.Cast.Select(c => new { name = c.Name, character = c.Character }),
                tmdbId = item.TmdbId,
                tmdbUrl = item.TmdbId is { } tmdb
                    ? $"https://www.themoviedb.org/{(item.Kind == MediaKinds.Movie ? "movie" : "tv")}/{tmdb}"
                    : null,
                metadataLocked = item.MetadataLocked,
                // DELETE /api/media/items/{id} would be allowed (admins, movies on the server's own disks).
                canDelete = isAdmin && item.Kind == MediaKinds.Movie && !MediaNetworkPath.IsNetwork(item.Path),
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
        public int PlayCount { get; set; }
        public bool InList { get; set; }
    }
}
