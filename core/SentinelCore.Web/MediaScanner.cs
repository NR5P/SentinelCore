using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

public sealed record MediaScanProgress(string Phase, string? LibraryName, int Processed, int Total, DateTimeOffset StartedAt);

// Where downloaded artwork lives: <data>/media-art/<itemId>-poster.jpg and friends. Local artwork
// beside the video is never copied here; it is served from where it is.
public sealed class MediaArtCache(string root, string subtitlesRoot)
{
    public string Root { get; } = root;

    // Extracted WebVTT subtitles, one file per track - see MediaSubtitles.
    public string SubtitlesRoot { get; } = subtitlesRoot;

    public string PathFor(Guid itemId, string kind) => Path.Combine(Root, $"{itemId:N}-{kind}.jpg");

    public string SeasonPosterPath(Guid showId, int season) => PathFor(showId, $"season{season}");
}

// Turns folders of video files into the catalog, in three passes that each get the shelf a
// little better: the file walk (seconds - titles from names, local artwork), the TMDB pass
// (descriptions, ratings, official posters) and the ffprobe pass (durations and codecs). Only
// one scan runs at a time; a request to scan while one is running is refused, not queued.
public sealed partial class MediaScanner : IDisposable
{
    private const int TmdbRetryDays = 7;
    private const int PurgeMissingAfterDays = 30;

    private static readonly string[] PosterNames = ["poster.jpg", "poster.png", "folder.jpg", "folder.png", "cover.jpg", "cover.png"];
    private static readonly string[] BackdropNames = ["backdrop.jpg", "backdrop.png", "fanart.jpg", "fanart.png"];

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:season|series)[ ._-]?(\d{1,2})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonInName();

    private readonly IMediaStore _store;
    private readonly MediaFiles _files;
    private readonly TmdbClient _tmdb;
    private readonly MediaArtCache _art;
    private readonly ILogger<MediaScanner> _logger;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool? _ffmpegAvailable;

    public MediaScanner(IMediaStore store, MediaFiles files, TmdbClient tmdb, MediaArtCache art, IConfiguration configuration, ILogger<MediaScanner> logger)
    {
        _store = store;
        _files = files;
        _tmdb = tmdb;
        _art = art;
        _logger = logger;
        _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
        _ffprobePath = configuration["FfprobePath"] ?? MediaProbe.DeriveFfprobePath(_ffmpegPath);
    }

    public string FfmpegPath => _ffmpegPath;

    public MediaFiles Files => _files;

    public bool IsScanning => _scanLock.CurrentCount == 0;

    public MediaScanProgress? Progress { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastFinishedAt { get; private set; }

    // Checked once per process: a box without ffmpeg can still direct-play MP4 files, and the
    // dashboard says what is missing instead of failing at the first MKV.
    public async Task<bool> FfmpegAvailableAsync(CancellationToken cancellationToken)
    {
        if (_ffmpegAvailable is { } known) return known;
        var ok = await FfmpegProcess.RunAsync(_ffmpegPath, ["-version"], TimeSpan.FromSeconds(10), _logger, "version check", null, cancellationToken);
        _ffmpegAvailable = ok;
        return ok;
    }

    // Which codecs ffmpeg's dts2pts filter handles here (see MediaPlaybackPlanner.Plan). Asked once
    // per process; an older ffmpeg without the filter just answers nothing.
    public async Task<IReadOnlySet<string>> TimestampRebuildCodecsAsync(CancellationToken cancellationToken)
    {
        if (_timestampRebuildCodecs is { } known) return known;
        var output = await FfmpegProcess.RunCapturingStdoutAsync(
            _ffmpegPath, ["-hide_banner", "-h", "bsf=dts2pts"], TimeSpan.FromSeconds(10), _logger, "dts2pts check", null, cancellationToken);
        var codecs = MediaPlaybackPlanner.ParseBsfCodecs(output is null ? "" : System.Text.Encoding.UTF8.GetString(output));
        _timestampRebuildCodecs = codecs;
        return codecs;
    }

    private IReadOnlySet<string>? _timestampRebuildCodecs;

    // Starts a scan in the background. False when one is already running.
    public bool TryStartScan(Guid? libraryId)
    {
        if (!_scanLock.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunLockedAsync(libraryId, _shutdown.Token);
                // A library added while that scan was running asked for one of its own.
                while (Interlocked.Exchange(ref _pendingRescan, 0) == 1 && !_shutdown.IsCancellationRequested)
                    await RunLockedAsync(null, _shutdown.Token);
            }
            finally { _scanLock.Release(); }
        });
        return true;
    }

    // Like TryStartScan, but never refused: if a scan is running, another follows it. What adding
    // or moving a library wants - the admin should not have to come back and press Scan.
    public void RequestScan(Guid? libraryId)
    {
        if (TryStartScan(libraryId)) return;
        Interlocked.Exchange(ref _pendingRescan, 1);
        // The running scan may have finished between the refusal and the flag; try once more.
        if (TryStartScan(null)) Interlocked.Exchange(ref _pendingRescan, 0);
    }

    private int _pendingRescan;

    // Runs a scan to completion on the caller's thread - what tests and "scan this new library"
    // want. False when a scan was already running.
    public async Task<bool> ScanAsync(Guid? libraryId, CancellationToken cancellationToken)
    {
        if (!await _scanLock.WaitAsync(0, cancellationToken)) return false;
        try { await RunLockedAsync(libraryId, cancellationToken); return true; }
        finally { _scanLock.Release(); }
    }

    private async Task RunLockedAsync(Guid? libraryId, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        LastError = null;
        try
        {
            var libraries = await _store.GetLibrariesAsync(cancellationToken);
            foreach (var library in libraries.Where(l => libraryId is null || l.Id == libraryId))
            {
                Progress = new MediaScanProgress("files", library.Name, 0, 0, startedAt);
                await ScanLibraryAsync(library, cancellationToken);
            }
            await _store.PurgeMissingAsync(DateTimeOffset.UtcNow.AddDays(-PurgeMissingAfterDays), cancellationToken);
            await MetadataPassAsync(startedAt, cancellationToken);
            await ProbePassAsync(startedAt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Media scan failed");
            LastError = ex.Message;
        }
        finally
        {
            Progress = null;
            LastFinishedAt = DateTimeOffset.UtcNow;
        }
    }

    // MARK: - Pass 1: files

    private async Task ScanLibraryAsync(MediaLibrary library, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await _store.SaveLibraryAsync(library with { LastScanStartedAt = started }, cancellationToken);
        var fs = _files.For(library.Path);
        var network = MediaNetworkPath.IsNetwork(library.Path);

        List<MediaItem> items;
        try
        {
            // The whole walk is blocking file I/O (and network round trips for a share).
            items = await Task.Run(() =>
            {
                if (!fs.DirectoryExists(library.Path))
                    throw new DirectoryNotFoundException(network
                        ? "The shared folder could not be found. Check the computer is on and the folder is still shared."
                        : "The folder does not exist or cannot be read. If it is a network share, check that it is mounted.");
                return library.Kind == MediaLibraryKinds.Shows ? WalkShows(library, fs) : WalkMovies(library, fs);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = ex is DirectoryNotFoundException or MediaNetworkException ? ex.Message : $"Could not read the folder: {ex.Message}";
            await _store.SaveLibraryAsync(library with { LastScanStartedAt = started, LastScanFinishedAt = DateTimeOffset.UtcNow,
                LastScanError = message }, cancellationToken);
            return;
        }

        // A library that suddenly has nothing in it is almost always an unmounted drive, not a
        // household that deleted every film. Keep the catalog and say so.
        if (items.Count == 0 && library.ItemCount > 0)
        {
            await _store.SaveLibraryAsync(library with { LastScanStartedAt = started, LastScanFinishedAt = DateTimeOffset.UtcNow,
                LastScanError = network
                    ? "No video files were found. If the other computer is asleep or its sharing changed, fix that and scan again; the catalog was kept."
                    : "No video files were found. If this is a network share, check that it is still mounted; the catalog was kept." }, cancellationToken);
            return;
        }

        await _store.UpsertItemsAsync(items, cancellationToken);
        var present = items.Select(i => i.Id).ToHashSet();
        var missing = await _store.ReconcileMissingAsync(library.Id, present, cancellationToken);
        var count = items.Count(i => i.Kind != MediaKinds.Episode);
        _logger.LogInformation("Media library {Name}: {Count} titles, {Episodes} episodes, {Missing} missing",
            library.Name, count, items.Count(i => i.Kind == MediaKinds.Episode), missing);
        await _store.SaveLibraryAsync(library with { LastScanStartedAt = started, LastScanFinishedAt = DateTimeOffset.UtcNow,
            LastScanError = null, ItemCount = count }, cancellationToken);
    }

    public static List<MediaItem> WalkMovies(MediaLibrary library) => WalkMovies(library, LocalMediaFileSystem.Instance);

    public static List<MediaItem> WalkShows(MediaLibrary library) => WalkShows(library, LocalMediaFileSystem.Instance);

    // Every video file is a movie; the title comes from whichever of the file name and its
    // folder name says more (a year beats no year, and a dedicated folder beats a file).
    public static List<MediaItem> WalkMovies(MediaLibrary library, IMediaFileSystem fs)
    {
        var root = RootOf(library, fs);
        var listings = new FolderListings(fs);
        var now = DateTimeOffset.UtcNow;
        var items = new List<MediaItem>();
        foreach (var (directory, files) in VideoFilesByDirectory(listings, root))
        {
            var dedicated = files.Count <= 2 && !PathsEqual(directory, root);
            foreach (var file in files)
            {
                var stem = Path.GetFileNameWithoutExtension(file.Name);
                var fromFile = MediaNaming.ParseTitle(stem);
                var parsed = fromFile;
                if (dedicated)
                {
                    var fromFolder = MediaNaming.ParseTitle(Path.GetFileName(directory));
                    var folderScore = (fromFolder.Year is null ? 0 : 2) + 1;
                    var fileScore = fromFile.Year is null ? 0 : 2;
                    // A folder truncated by whoever made it ("The Gift of the") loses to the file
                    // name that finishes the thought ("The Gift of the Magi").
                    var fileExtendsFolder = MediaNaming.NormalizeKey(fromFile.Title).Length > MediaNaming.NormalizeKey(fromFolder.Title).Length
                        && MediaNaming.NormalizeKey(fromFile.Title).StartsWith(MediaNaming.NormalizeKey(fromFolder.Title), StringComparison.Ordinal);
                    if (folderScore >= fileScore && !(fileExtendsFolder && fromFolder.Year == fromFile.Year))
                        parsed = fromFolder with { Year = fromFolder.Year ?? fromFile.Year };
                }
                var item = new MediaItem
                {
                    Id = MediaNaming.StableId(library.Id, fs.Relative(root, file.Path)),
                    LibraryId = library.Id,
                    Kind = MediaKinds.Movie,
                    Title = parsed.Title,
                    SortTitle = MediaNaming.SortTitle(parsed.Title),
                    Year = parsed.Year,
                    Path = file.Path,
                    FileSize = file.Length,
                    FileModifiedAt = file.ModifiedUtc,
                    AddedAt = AddedAtFor(file.ModifiedUtc, library, now)
                };
                item.PosterLocal = FindArt(listings, directory, dedicated ? PosterNames : [], stem, "-poster");
                item.BackdropLocal = FindArt(listings, directory, dedicated ? BackdropNames : [], stem, "-backdrop");
                items.Add(item);
            }
        }
        return items;
    }

    // Each top-level folder is a show; folders that clean to the same title are one show.
    public static List<MediaItem> WalkShows(MediaLibrary library, IMediaFileSystem fs)
    {
        var root = RootOf(library, fs);
        var listings = new FolderListings(fs);
        var now = DateTimeOffset.UtcNow;
        var groups = new Dictionary<string, ShowGroup>(StringComparer.Ordinal);
        var top = listings.Get(root);

        foreach (var directory in top.Where(e => e.IsDirectory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = directory.Name;
            if (MediaNaming.IsSkippedFolder(name)) continue;
            var parsed = MediaNaming.ParseTitle(name);
            var key = MediaNaming.NormalizeKey(parsed.Title);
            if (key.Length == 0) continue;
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new ShowGroup(parsed.Title, parsed.Year);
            else
            {
                if (group.Year is null || (parsed.Year is { } y && y < group.Year)) group.Year = parsed.Year ?? group.Year;
                if (parsed.Year is not null && group.TitleHadYear is false) { group.Title = parsed.Title; group.TitleHadYear = true; }
            }
            group.TitleHadYear |= parsed.Year is not null;
            group.Folders.Add(directory.Path);
        }

        foreach (var file in top.Where(e => !e.IsDirectory && MediaNaming.IsVideoFile(e.Name) && !MediaNaming.IsSkippedFile(e.Name))
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var parsed = MediaNaming.ParseTitle(Path.GetFileNameWithoutExtension(file.Name));
            var key = MediaNaming.NormalizeKey(parsed.Title);
            if (key.Length == 0) continue;
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new ShowGroup(parsed.Title, parsed.Year);
            group.LooseFiles.Add(file);
        }

        var items = new List<MediaItem>();
        foreach (var (key, group) in groups)
        {
            var showId = MediaNaming.StableId(library.Id, "show:" + key);
            var show = new MediaItem
            {
                Id = showId,
                LibraryId = library.Id,
                Kind = MediaKinds.Show,
                Title = group.Title,
                SortTitle = MediaNaming.SortTitle(group.Title),
                Year = group.Year,
                Path = group.Folders.FirstOrDefault() ?? root,
                AddedAt = now
            };
            foreach (var folder in group.Folders)
            {
                show.PosterLocal ??= FindArt(listings, folder, PosterNames, null, null);
                show.BackdropLocal ??= FindArt(listings, folder, BackdropNames, null, null);
            }

            var parsedEpisodes = new List<(MediaItem Item, MediaNaming.ParsedEpisode Parsed)>();
            foreach (var folder in group.Folders)
            {
                var folderSeason = SeasonInName().Match(Path.GetFileName(folder)) is { Success: true } m ? int.Parse(m.Groups[1].Value) : (int?)null;
                foreach (var (directory, files) in VideoFilesByDirectory(listings, folder))
                {
                    var season = folderSeason;
                    for (var probe = directory; probe is not null && !PathsEqual(probe, folder); probe = fs.Parent(probe))
                    {
                        if (MediaNaming.ParseSeasonFolder(Path.GetFileName(probe)) is { } s) { season = s; break; }
                    }
                    foreach (var file in files) parsedEpisodes.Add(Episode(library, fs, listings, root, showId, show, directory, file, season, now));
                }
            }
            foreach (var file in group.LooseFiles) parsedEpisodes.Add(Episode(library, fs, listings, root, showId, show, root, file, 1, now));
            if (parsedEpisodes.Count == 0) continue;
            var episodes = parsedEpisodes.Select(p => p.Item).ToList();

            // Folders get named by whoever copied them ("CloneWars", "mr_rogers"); the files
            // inside usually carry the real name ("Star Wars The Clone Wars S01E01 ..."). When most
            // numbered episodes agree on one, that is the show's title.
            var named = parsedEpisodes.Where(p => p.Parsed.ShowName is not null).Select(p => p.Parsed.ShowName!).ToList();
            var numbered = parsedEpisodes.Count(p => p.Parsed.Episode is not null);
            if (named.Count > 0 && named.Count * 2 >= numbered)
            {
                var common = named.GroupBy(n => MediaNaming.NormalizeKey(n.Title)).OrderByDescending(g => g.Count()).First();
                if (common.Count() * 2 >= named.Count)
                {
                    var best = common.First();
                    show.Title = best.Title;
                    show.SortTitle = MediaNaming.SortTitle(best.Title);
                    show.Year ??= common.Select(n => n.Year).FirstOrDefault(y => y is not null);
                    foreach (var episode in episodes) episode.Year = show.Year;
                }
            }

            // Files without an episode number line up after the numbered ones, in file-name
            // order - which for old cartoons named "Mickey Mouse 1929 ..." is also release order.
            var unnumbered = 0;
            foreach (var (episode, _) in parsedEpisodes.Where(p => p.Parsed.Episode is null)
                .OrderBy(p => Path.GetFileName(p.Item.Path), StringComparer.OrdinalIgnoreCase))
            {
                episode.EpisodeNumber = 1000 + ++unnumbered;
                var stem = Path.GetFileNameWithoutExtension(episode.Path);
                var fromShow = MediaNaming.UnnumberedEpisodeTitle(stem, show.Title);
                var fromFolder = MediaNaming.UnnumberedEpisodeTitle(stem, group.Title);
                episode.Title = fromFolder.Length < fromShow.Length ? fromFolder : fromShow;
            }
            foreach (var episode in episodes)
                episode.SortTitle = $"{show.SortTitle} {episode.SeasonNumber:D3} {episode.EpisodeNumber:D4}";

            items.Add(show);
            items.AddRange(episodes);
        }
        return items;
    }

    private static (MediaItem, MediaNaming.ParsedEpisode) Episode(
        MediaLibrary library, IMediaFileSystem fs, FolderListings listings, string root, Guid showId, MediaItem show,
        string directory, MediaFsEntry file, int? folderSeason, DateTimeOffset now)
    {
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        var parsed = MediaNaming.ParseEpisode(stem, folderSeason);
        var season = parsed.Season ?? folderSeason ?? 1;
        var title = parsed.Title
            ?? (parsed.Episode is { } number ? $"Episode {number}" : MediaNaming.ParseTitle(stem).Title);
        return (new MediaItem
        {
            Id = MediaNaming.StableId(library.Id, fs.Relative(root, file.Path)),
            LibraryId = library.Id,
            Kind = MediaKinds.Episode,
            ParentId = showId,
            Title = title,
            SortTitle = show.SortTitle,
            Year = show.Year,
            SeasonNumber = season,
            EpisodeNumber = parsed.Episode,
            Path = file.Path,
            FileSize = file.Length,
            FileModifiedAt = file.ModifiedUtc,
            AddedAt = AddedAtFor(file.ModifiedUtc, library, now),
            ThumbLocal = FindArt(listings, directory, [], stem, "-thumb") ?? FindArt(listings, directory, [], stem, "")
        }, parsed);
    }

    // A local library's root is made absolute the way it always was, which keeps item ids (a
    // hash of the path relative to it) stable across this change.
    private static string RootOf(MediaLibrary library, IMediaFileSystem fs) =>
        fs is LocalMediaFileSystem ? Path.GetFullPath(library.Path) : library.Path;

    // "Recently added" on a library's very first scan would otherwise be every file at once, all
    // stamped with the same second. The file's own date is the better answer then; after that, a
    // file is added when the scanner first sees it, whatever date it was copied with.
    private static DateTimeOffset AddedAtFor(DateTime modifiedUtc, MediaLibrary library, DateTimeOffset now)
    {
        if (library.LastScanFinishedAt is not null) return now;
        var written = new DateTimeOffset(DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc), TimeSpan.Zero);
        return written < now ? written : now;
    }

    private sealed class ShowGroup(string title, int? year)
    {
        public string Title = title;
        public int? Year = year;
        public bool TitleHadYear;
        public readonly List<string> Folders = [];
        public readonly List<MediaFsEntry> LooseFiles = [];
    }

    // Each folder is listed once per walk, whatever asks for it: the walk itself, then the
    // artwork lookups beside each video. On a network share that is the difference between one
    // round trip per folder and several per file.
    private sealed class FolderListings(IMediaFileSystem fs)
    {
        private readonly Dictionary<string, IReadOnlyList<MediaFsEntry>> _listed = new(StringComparer.Ordinal);

        // The root's own failure is the scan's failure; anything below it is skipped.
        public IReadOnlyList<MediaFsEntry> Get(string directory) =>
            _listed.TryGetValue(directory, out var entries) ? entries : _listed[directory] = fs.List(directory);

        public IReadOnlyList<MediaFsEntry> TryGet(string directory)
        {
            try { return Get(directory); }
            catch (UnauthorizedAccessException) { return _listed[directory] = []; }
            catch (IOException) { return _listed[directory] = []; }
        }
    }

    private static IEnumerable<(string Directory, List<MediaFsEntry> Files)> VideoFilesByDirectory(FolderListings listings, string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var first = true;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var entries = first ? listings.Get(directory) : listings.TryGet(directory);
            first = false;
            // Pushed in reverse so they pop in name order, the order the old walk visited them in.
            foreach (var child in entries.Where(e => e.IsDirectory && !MediaNaming.IsSkippedFolder(e.Name))
                         .OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase))
                pending.Push(child.Path);
            var files = entries
                .Where(e => !e.IsDirectory && MediaNaming.IsVideoFile(e.Name) && !MediaNaming.IsSkippedFile(e.Name))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count > 0) yield return (directory, files);
        }
    }

    private static string? FindArt(FolderListings listings, string directory, string[] folderNames, string? stem, string? suffix)
    {
        var entries = listings.TryGet(directory);
        if (entries.Count == 0) return null;
        var byName = new Dictionary<string, MediaFsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => !e.IsDirectory))
            byName.TryAdd(entry.Name, entry);
        foreach (var name in folderNames)
            if (byName.TryGetValue(name, out var found)) return found.Path;
        if (stem is not null && suffix is not null)
            foreach (var extension in new[] { ".jpg", ".jpeg", ".png" })
                if (byName.TryGetValue(stem + suffix + extension, out var found)) return found.Path;
        return null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // MARK: - Pass 2: TMDB

    private async Task MetadataPassAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        if (await _tmdb.ApiKeyAsync(cancellationToken) is null) return;

        var retryBefore = DateTimeOffset.UtcNow.AddDays(-TmdbRetryDays);
        var seasonCache = new Dictionary<(int, int), TmdbClient.Season?>();
        var processed = 0;
        var consecutiveFailures = 0;
        for (var round = 0; round < 50; round++)
        {
            var batch = await _store.ItemsNeedingMetadataAsync(retryBefore, 200, cancellationToken);
            if (batch.Count == 0) break;
            var total = processed + batch.Count;
            foreach (var item in batch)
            {
                Progress = new MediaScanProgress("metadata", null, processed, total, startedAt);
                try
                {
                    await FetchMetadataAsync(item, null, seasonCache, cancellationToken);
                    consecutiveFailures = 0;
                }
                catch (TmdbRejectedKeyException ex) { LastError = ex.Message; return; }
                catch (TmdbNotConfiguredException) { return; }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning("TMDB request failed for {Title}: {Message}", item.Title, ex.Message);
                    if (++consecutiveFailures >= 3) { LastError = "TMDB is not answering: " + ex.Message; return; }
                }
                processed++;
            }
        }
    }

    // Matches one item against TMDB (or takes the match the admin chose) and writes what came
    // back. For a show, also fills in every episode from the season listings.
    public async Task FetchMetadataAsync(MediaItem item, int? chosenTmdbId, Dictionary<(int, int), TmdbClient.Season?>? seasonCache, CancellationToken cancellationToken)
    {
        seasonCache ??= new();
        var now = DateTimeOffset.UtcNow;
        switch (item.Kind)
        {
            case MediaKinds.Movie:
            {
                var tmdbId = chosenTmdbId ?? await FindAsync(MediaKinds.Movie, item.Title, item.Year, cancellationToken);
                var movie = tmdbId is { } id ? await _tmdb.GetMovieAsync(id, cancellationToken) : null;
                if (movie is null)
                {
                    item.MetadataFetchedAt = now;
                    item.MetadataError = "No match found on TMDB.";
                    await _store.UpdateMetadataAsync(item, cancellationToken);
                    return;
                }
                item.TmdbId = movie.TmdbId;
                item.Title = movie.Title.Length > 0 ? movie.Title : item.Title;
                item.SortTitle = MediaNaming.SortTitle(item.Title);
                item.Year = movie.Year ?? item.Year;
                item.Overview = movie.Overview;
                item.Tagline = movie.Tagline;
                item.Genres = movie.Genres;
                item.Rating = movie.Rating;
                item.Certification = movie.Certification;
                item.RuntimeMinutes = movie.RuntimeMinutes;
                item.ReleaseDate = movie.ReleaseDate;
                item.Cast = movie.Cast;
                item.PosterRemote = movie.PosterPath;
                item.BackdropRemote = movie.BackdropPath;
                item.MetadataFetchedAt = now;
                item.MetadataError = null;
                if (chosenTmdbId is not null) item.MetadataLocked = true;
                await _store.UpdateMetadataAsync(item, cancellationToken);
                await WarmArtworkAsync(item, cancellationToken);
                return;
            }
            case MediaKinds.Show:
            {
                var tmdbId = chosenTmdbId ?? await FindAsync(MediaKinds.Show, item.Title, item.Year, cancellationToken);
                var show = tmdbId is { } id ? await _tmdb.GetShowAsync(id, cancellationToken) : null;
                var episodes = await _store.GetItemsAsync(new MediaQuery(Kinds: [MediaKinds.Episode], ParentId: item.Id, IncludeMissing: true), cancellationToken);
                if (show is null)
                {
                    item.MetadataFetchedAt = now;
                    item.MetadataError = "No match found on TMDB.";
                    await _store.UpdateMetadataAsync(item, cancellationToken);
                    foreach (var episode in episodes.Where(e => e.MetadataFetchedAt is null))
                    {
                        episode.MetadataFetchedAt = now;
                        await _store.UpdateMetadataAsync(episode, cancellationToken);
                    }
                    return;
                }
                item.TmdbId = show.TmdbId;
                item.Title = show.Title.Length > 0 ? show.Title : item.Title;
                item.SortTitle = MediaNaming.SortTitle(item.Title);
                item.Year = show.Year ?? item.Year;
                item.Overview = show.Overview;
                item.Tagline = show.Tagline;
                item.Genres = show.Genres;
                item.Rating = show.Rating;
                item.Certification = show.Certification;
                item.RuntimeMinutes = show.RuntimeMinutes;
                item.ReleaseDate = show.FirstAirDate;
                item.Cast = show.Cast;
                item.PosterRemote = show.PosterPath;
                item.BackdropRemote = show.BackdropPath;
                item.MetadataFetchedAt = now;
                item.MetadataError = null;
                if (chosenTmdbId is not null) item.MetadataLocked = true;
                await _store.UpdateMetadataAsync(item, cancellationToken);
                await WarmArtworkAsync(item, cancellationToken);
                foreach (var episode in episodes)
                    await FillEpisodeAsync(episode, item, seasonCache, cancellationToken);
                return;
            }
            case MediaKinds.Episode:
            {
                var show = item.ParentId is { } parent ? await _store.GetItemAsync(parent, cancellationToken) : null;
                if (show?.TmdbId is null)
                {
                    // The show's own turn (it sorts first) either matched and filled this in, or
                    // it did not match; either way there is nothing to do here.
                    item.MetadataFetchedAt = now;
                    await _store.UpdateMetadataAsync(item, cancellationToken);
                    return;
                }
                await FillEpisodeAsync(item, show, seasonCache, cancellationToken);
                return;
            }
        }
    }

    private async Task FillEpisodeAsync(MediaItem episode, MediaItem show, Dictionary<(int, int), TmdbClient.Season?> seasonCache, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var showTmdbId = show.TmdbId!.Value;
        var seasonNumber = episode.SeasonNumber ?? 1;
        if (!seasonCache.TryGetValue((showTmdbId, seasonNumber), out var season))
        {
            season = await _tmdb.GetSeasonAsync(showTmdbId, seasonNumber, cancellationToken);
            seasonCache[(showTmdbId, seasonNumber)] = season;
            if (season?.PosterPath is { } posterPath)
            {
                var destination = _art.SeasonPosterPath(show.Id, seasonNumber);
                if (!File.Exists(destination)) await _tmdb.DownloadImageAsync(posterPath, "w342", destination, cancellationToken);
            }
        }
        var match = episode.EpisodeNumber is { } number and < 1000
            ? season?.Episodes.FirstOrDefault(e => e.Number == number)
            : null;
        episode.TmdbId = showTmdbId;
        episode.Genres = show.Genres;
        episode.Certification = show.Certification;
        episode.RuntimeMinutes ??= show.RuntimeMinutes;
        if (match is not null)
        {
            if (match.Name.Length > 0) episode.Title = match.Name;
            episode.Overview = match.Overview;
            episode.ReleaseDate = match.AirDate;
            episode.Rating = match.Rating;
            episode.ThumbRemote = match.StillPath;
        }
        episode.MetadataFetchedAt = now;
        episode.MetadataError = null;
        await _store.UpdateMetadataAsync(episode, cancellationToken);
    }

    // A title with its year first; if that finds nothing, the title alone - years parsed from
    // file names are wrong often enough (a re-release, a remaster) to be worth a second try.
    private async Task<int?> FindAsync(string kind, string title, int? year, CancellationToken cancellationToken)
    {
        foreach (var candidate in MediaNaming.SearchCandidates(title))
        {
            var results = await _tmdb.SearchAsync(kind, candidate, year, cancellationToken);
            if (results.Count == 0 && year is not null) results = await _tmdb.SearchAsync(kind, candidate, null, cancellationToken);
            if (results.Count > 0) return results[0].TmdbId;
        }
        return null;
    }

    // Posters and backdrops are fetched as soon as a title is matched, so the shelf is never a
    // wall of grey; episode stills wait until someone opens the show.
    private async Task WarmArtworkAsync(MediaItem item, CancellationToken cancellationToken)
    {
        await ResolveArtworkAsync(item, "poster", cancellationToken);
        await ResolveArtworkAsync(item, "backdrop", cancellationToken);
    }

    // The file to serve for an item's poster, backdrop or thumb: the local file beside the
    // video if there is one, else the TMDB image from the cache (downloaded now if it is not
    // there yet), else null.
    public async Task<string?> ResolveArtworkAsync(MediaItem item, string kind, CancellationToken cancellationToken)
    {
        var (local, remote, size) = kind switch
        {
            "poster" => (item.PosterLocal, item.PosterRemote, "w500"),
            "backdrop" => (item.BackdropLocal, item.BackdropRemote, "w1280"),
            "thumb" => (item.ThumbLocal, item.ThumbRemote, "w500"),
            _ => (null, null, "w500")
        };
        if (local is not null && _files.FileExists(local)) return local;
        if (remote is null) return null;
        var cached = _art.PathFor(item.Id, kind);
        if (File.Exists(cached)) return cached;
        if (await _tmdb.ApiKeyAsync(cancellationToken) is null && !File.Exists(cached)) return null;
        return await _tmdb.DownloadImageAsync(remote, size, cached, cancellationToken) ? cached : null;
    }

    public string? SeasonPoster(Guid showId, int season)
    {
        var path = _art.SeasonPosterPath(showId, season);
        return File.Exists(path) ? path : null;
    }

    // MARK: - Pass 3: ffprobe

    private async Task ProbePassAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        if (!await FfmpegAvailableAsync(cancellationToken)) return;
        var processed = 0;
        for (var round = 0; round < 100; round++)
        {
            var batch = await _store.ItemsNeedingProbeAsync(100, cancellationToken);
            if (batch.Count == 0) break;
            var total = processed + batch.Count;
            // Two at a time: the files sit on one disk (or one network share) and ffprobe only
            // reads their headers, so this is latency-bound, not CPU-bound.
            var gate = new SemaphoreSlim(2, 2);
            var tasks = batch.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await ProbeItemAsync(item, cancellationToken);
                    Progress = new MediaScanProgress("probing", null, Interlocked.Increment(ref processed), total, startedAt);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
        }
    }

    public async Task ProbeItemAsync(MediaItem item, CancellationToken cancellationToken)
    {
        if (!await Task.Run(() => _files.FileExists(item.Path), cancellationToken))
        {
            item.ProbedAt = DateTimeOffset.UtcNow;
            item.ProbeError = "The file is missing.";
        }
        else
        {
            // One odd file must not stop the probe pass for the rest of the library (a sidecar
            // subtitle once threw here and left most of the shelf unprobed).
            try { await MediaProbe.ProbeAsync(item, _files, _ffprobePath, _logger, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Probing {Path} failed", item.Path);
                item.ProbedAt = DateTimeOffset.UtcNow;
                item.ProbeError = $"Reading this file failed: {ex.Message}";
            }
        }
        await _store.UpdateProbeAsync(item, cancellationToken);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _scanLock.Dispose();
    }
}

// Scans at startup and every RescanHours after, so a film copied onto the share tonight is on
// the shelf by morning without anyone pressing anything.
public sealed class MediaScanWorker(MediaScanner scanner, IMediaStore store, AppSettingsFile settings, ILogger<MediaScanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = await settings.GetSectionAsync<MediaSettingsDto>(MediaSettingsDto.SectionName, stoppingToken) ?? new();
                if (options.Enabled)
                {
                    var libraries = await store.GetLibrariesAsync(stoppingToken);
                    var due = first || (options.RescanHours > 0 && libraries.Count > 0
                        && (scanner.LastFinishedAt ?? libraries.Max(l => l.LastScanFinishedAt) ?? DateTimeOffset.MinValue)
                            < DateTimeOffset.UtcNow.AddHours(-options.RescanHours));
                    if (due && libraries.Count > 0 && scanner.TryStartScan(null))
                        logger.LogInformation("Media library scan started ({Reason})", first ? "startup" : "scheduled");
                }
                first = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Media scan scheduling failed"); }

            try { await Task.Delay(CheckInterval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
