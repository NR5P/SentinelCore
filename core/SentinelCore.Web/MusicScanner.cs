namespace SentinelCore.Web;

// Turns folders of audio files into the catalog, in passes that each make the shelf a little
// better: the file walk (seconds), the tags (TagLib, ffprobe for the odd file), the album rules
// that need the whole library at once (who an album belongs to, compilations), and artwork
// (embedded, then folder, then the Cover Art Archive). Only one scan runs at a time; asking for
// one while one is running is refused, not queued - except RequestScan, which queues one more.
public sealed class MusicScanner : IDisposable
{
    public const long MinimumFileBytes = 16 * 1024;
    private const int PurgeMissingAfterDays = 30;
    private static readonly TimeSpan RecheckMissingArtAfter = TimeSpan.FromDays(30);
    private static readonly string[] CoverNames = ["cover", "folder", "front", "album"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    private readonly IMusicStore _store;
    private readonly MediaFiles _files;
    private readonly MusicArtCache _art;
    private readonly MusicCoverArtClient _coverArt;
    private readonly AppSettingsFile _settings;
    private readonly ILogger<MusicScanner> _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _pendingRescan;

    public MusicScanner(IMusicStore store, MediaFiles files, MusicArtCache art, MusicCoverArtClient coverArt, AppSettingsFile settings,
        IConfiguration configuration, ILogger<MusicScanner> logger)
    {
        _store = store;
        _files = files;
        _art = art;
        _coverArt = coverArt;
        _settings = settings;
        _logger = logger;
        FfmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
        FfprobePath = configuration["FfprobePath"] ?? MediaProbe.DeriveFfprobePath(FfmpegPath);
    }

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    public MediaFiles Files => _files;

    public bool IsScanning => _scanLock.CurrentCount == 0;

    public MediaScanProgress? Progress { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LastFinishedAt { get; private set; }

    // Starts a scan in the background. False when one is already running.
    public bool TryStartScan(Guid? libraryId)
    {
        if (!_scanLock.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunLockedAsync(libraryId, _shutdown.Token);
                while (Interlocked.Exchange(ref _pendingRescan, 0) == 1 && !_shutdown.IsCancellationRequested)
                    await RunLockedAsync(null, _shutdown.Token);
            }
            finally { _scanLock.Release(); }
        });
        return true;
    }

    // Like TryStartScan, but never refused: if a scan is running, another follows it. What adding
    // or moving a library, and a finished album download, want.
    public void RequestScan(Guid? libraryId)
    {
        if (TryStartScan(libraryId)) return;
        Interlocked.Exchange(ref _pendingRescan, 1);
        if (TryStartScan(null)) Interlocked.Exchange(ref _pendingRescan, 0);
    }

    // Runs a scan to completion on the caller's thread - what tests want. False when a scan was
    // already running.
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
                await ScanFilesAsync(library, cancellationToken);
            }
            await _store.PurgeMissingAsync(DateTimeOffset.UtcNow.AddDays(-PurgeMissingAfterDays), cancellationToken);
            var touched = await TagPassAsync(startedAt, cancellationToken);
            var changedAlbums = await ResolvePassAsync(touched, cancellationToken);
            await ArtworkPassAsync(changedAlbums, startedAt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Music scan failed");
            LastError = ex.Message;
        }
        finally
        {
            Progress = null;
            LastFinishedAt = DateTimeOffset.UtcNow;
        }
    }

    // MARK: - Pass 1: files

    private async Task ScanFilesAsync(MusicLibrary library, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await _store.SaveLibraryAsync(library with { LastScanStartedAt = started }, cancellationToken);
        var fs = _files.For(library.Path);
        var network = MediaNetworkPath.IsNetwork(library.Path);

        List<MusicTrack> tracks;
        try
        {
            tracks = await Task.Run(() =>
            {
                if (!fs.DirectoryExists(library.Path))
                    throw new DirectoryNotFoundException(network
                        ? "The shared folder could not be found. Check the computer is on and the folder is still shared."
                        : "The folder does not exist or cannot be read. If it is a network share, check that it is mounted.");
                return Walk(library, fs);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = ex is DirectoryNotFoundException or MediaNetworkException ? ex.Message : $"Could not read the folder: {ex.Message}";
            await _store.SaveLibraryAsync(library with { LastScanStartedAt = started, LastScanFinishedAt = DateTimeOffset.UtcNow, LastScanError = message }, cancellationToken);
            return;
        }

        // A library that suddenly has nothing in it is almost always an unplugged drive or a
        // sleeping computer, not a household that deleted every song. Keep the catalog and say so.
        if (tracks.Count == 0 && library.TrackCount > 0)
        {
            await _store.SaveLibraryAsync(library with
            {
                LastScanStartedAt = started,
                LastScanFinishedAt = DateTimeOffset.UtcNow,
                LastScanError = network
                    ? "No music files were found. If the other computer is asleep or its sharing changed, fix that and scan again; the catalog was kept."
                    : "No music files were found. If this is a network share, check that it is still mounted; the catalog was kept."
            }, cancellationToken);
            return;
        }

        await _store.UpsertFilesAsync(tracks, cancellationToken);
        var missing = await _store.ReconcileMissingAsync(library.Id, tracks.Select(t => t.Id).ToHashSet(), cancellationToken);
        _logger.LogInformation("Music library {Name}: {Count} tracks, {Missing} missing", library.Name, tracks.Count, missing);
        await _store.SaveLibraryAsync(library with
        {
            LastScanStartedAt = started,
            LastScanFinishedAt = DateTimeOffset.UtcNow,
            LastScanError = null,
            TrackCount = tracks.Count
        }, cancellationToken);
    }

    public static List<MusicTrack> Walk(MusicLibrary library) => Walk(library, LocalMediaFileSystem.Instance);

    // Every audio file of 16 KB or more, outside hidden folders and the recycle bin, with the
    // .lrc beside it if there is one.
    public static List<MusicTrack> Walk(MusicLibrary library, IMediaFileSystem fs)
    {
        var root = fs is LocalMediaFileSystem ? Path.GetFullPath(library.Path) : library.Path;
        var now = DateTimeOffset.UtcNow;
        var tracks = new List<MusicTrack>();
        var pending = new Stack<string>();
        pending.Push(root);
        var first = true;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IReadOnlyList<MediaFsEntry> entries;
            try { entries = fs.List(directory); }
            catch (Exception ex) when (!first && ex is IOException or UnauthorizedAccessException) { continue; }
            first = false;
            foreach (var child in entries.Where(e => e.IsDirectory && !MusicText.IsSkippedFolder(e.Name))
                         .OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase))
                pending.Push(child.Path);

            var lyrics = entries.Where(e => !e.IsDirectory && e.Name.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => Path.GetFileNameWithoutExtension(e.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in entries.Where(e => !e.IsDirectory && MusicText.IsAudioFile(e.Name) && !MusicText.IsSkippedFile(e.Name)
                         && e.Length >= MinimumFileBytes).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var relative = fs.Relative(root, file.Path).Replace('\\', '/');
                var modified = new DateTimeOffset(DateTime.SpecifyKind(file.ModifiedUtc, DateTimeKind.Utc), TimeSpan.Zero);
                tracks.Add(new MusicTrack
                {
                    Id = MusicIds.Track(library.Id, relative),
                    LibraryId = library.Id,
                    Path = file.Path,
                    RelativePath = relative,
                    FileSize = file.Length,
                    FileModifiedAt = modified,
                    // A library's very first scan dates each file by when it was written, so
                    // "Recently added" is not everything at once; after that, when it was found.
                    AddedAt = library.LastScanFinishedAt is null && modified < now ? modified : now,
                    LyricsSidecar = lyrics.GetValueOrDefault(Path.GetFileNameWithoutExtension(file.Name))
                });
            }
        }
        return tracks;
    }

    // MARK: - Pass 2: tags

    private async Task<HashSet<Guid>> TagPassAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var touched = new HashSet<Guid>();
        var processed = 0;
        for (var round = 0; round < 10_000; round++)
        {
            var batch = await _store.TracksNeedingTagsAsync(200, cancellationToken);
            if (batch.Count == 0) break;
            var total = processed + batch.Count;
            // Four at a time: reading tags is a few small reads per file, so on a share this is
            // round-trip bound, not CPU bound.
            var gate = new SemaphoreSlim(4, 4);
            await Task.WhenAll(batch.Select(async track =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await MusicTags.ReadAsync(track, _files, FfprobePath, _logger, cancellationToken);
                    Progress = new MediaScanProgress("tags", null, Interlocked.Increment(ref processed), total, startedAt);
                }
                finally { gate.Release(); }
            }));
            await _store.UpdateTrackDataAsync(batch, cancellationToken);
            foreach (var track in batch) touched.Add(track.Id);
        }
        return touched;
    }

    // MARK: - Pass 3: album rules

    // Works out album artists, compilations and ids across every library at once (an album's
    // tracks can be spread over two). Returns the albums whose art should be looked at again.
    private async Task<HashSet<Guid>> ResolvePassAsync(HashSet<Guid> touched, CancellationToken cancellationToken)
    {
        var tracks = await _store.GetTracksAsync(null, includeMissing: false, cancellationToken);
        var oldAlbums = tracks.Where(t => touched.Contains(t.Id)).Select(t => t.AlbumId).ToHashSet();
        var changed = MusicAlbumRules.Resolve(tracks);
        if (changed.Count > 0) await _store.UpdateTrackDataAsync(changed, cancellationToken);
        var albums = tracks.Where(t => touched.Contains(t.Id) || changed.Contains(t)).Select(t => t.AlbumId).ToHashSet();
        albums.UnionWith(oldAlbums);
        albums.Remove(Guid.Empty);
        return albums;
    }

    // MARK: - Pass 4: artwork

    private async Task ArtworkPassAsync(HashSet<Guid> changedAlbums, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var options = await _settings.GetSectionAsync<MusicSettingsDto>(MusicSettingsDto.SectionName, cancellationToken) ?? new();
        var tracks = await _store.GetTracksAsync(null, includeMissing: false, cancellationToken);
        var libraries = (await _store.GetLibrariesAsync(cancellationToken)).ToDictionary(l => l.Id);
        var albums = tracks.GroupBy(t => t.AlbumId).ToList();

        // Albums whose tracks changed are looked at afresh: a new cover.jpg or a re-tagged file
        // should win over what was found before.
        var stale = changedAlbums.Select(MusicArtCache.AlbumKey)
            .Concat(tracks.Where(t => changedAlbums.Contains(t.AlbumId)).Select(t => MusicArtCache.ArtistKey(MusicIds.Artist(t.AlbumArtist))))
            .Distinct().ToList();
        if (stale.Count > 0) await _store.DeleteArtAsync(stale, cancellationToken);
        var known = await _store.GetArtAsync(cancellationToken);

        var listings = new Dictionary<string, IReadOnlyList<MediaFsEntry>>(StringComparer.Ordinal);
        IReadOnlyList<MediaFsEntry> List(string directory)
        {
            if (listings.TryGetValue(directory, out var entries)) return entries;
            try { entries = _files.For(directory).List(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { entries = []; }
            return listings[directory] = entries;
        }

        var todo = albums.Where(g => known.GetValueOrDefault(MusicArtCache.AlbumKey(g.Key)) is not { } art
            || (art.Path is null && art.CheckedAt < DateTimeOffset.UtcNow - RecheckMissingArtAfter)).ToList();
        var processed = 0;
        var onlineFailures = 0;
        foreach (var album in todo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Progress = new MediaScanProgress("artwork", null, processed++, todo.Count, startedAt);
            var ordered = album.OrderBy(t => t.DiscNumber ?? 1).ThenBy(t => t.TrackNumber ?? int.MaxValue).ThenBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            var record = await FindAlbumArtAsync(album.Key, ordered, List, options.FetchArtworkOnline && onlineFailures < 3, cancellationToken);
            if (record is null) { onlineFailures++; continue; }
            if (record.Source == "caa") onlineFailures = 0;
            await _store.SetArtAsync(record, cancellationToken);
        }

        // Artist pictures: artist.jpg in the folder that holds the artist's album folders.
        var artistTodo = tracks.Where(t => !t.Compilation)
            .GroupBy(t => MusicIds.Artist(t.AlbumArtist))
            .Where(g => !known.ContainsKey(MusicArtCache.ArtistKey(g.Key)))
            .ToList();
        foreach (var artist in artistTodo)
        {
            string? found = null;
            foreach (var track in artist)
            {
                var segments = track.RelativePath.Split('/');
                // Artist/Album/track (or Artist/Album/CD1/track): the artist folder is two up.
                var depth = segments.Length - 1 - (segments.Length > 2 && MusicText.IsDiscFolder(segments[^2]) ? 1 : 0);
                if (depth < 2 || !libraries.ContainsKey(track.LibraryId)) continue;
                var albumFolder = _files.For(track.Path).Parent(track.Path);
                if (albumFolder is not null && MusicText.IsDiscFolder(Path.GetFileName(albumFolder))) albumFolder = _files.For(albumFolder).Parent(albumFolder);
                var artistFolder = albumFolder is null ? null : _files.For(albumFolder).Parent(albumFolder);
                if (artistFolder is null) continue;
                found = FindImage(List(artistFolder), ["artist"]);
                if (found is not null) break;
            }
            await _store.SetArtAsync(new MusicArtRecord(MusicArtCache.ArtistKey(artist.Key), found, found is null ? "none" : "folder", DateTimeOffset.UtcNow), cancellationToken);
        }
    }

    // The album's cover by the documented preference: the first track's embedded picture, a
    // cover/folder/front/album image beside the tracks, then the Cover Art Archive. Null when an
    // online lookup failed for a reason worth retrying (the network), so nothing is recorded.
    private async Task<MusicArtRecord?> FindAlbumArtAsync(Guid albumId, List<MusicTrack> ordered,
        Func<string, IReadOnlyList<MediaFsEntry>> list, bool online, CancellationToken cancellationToken)
    {
        var key = MusicArtCache.AlbumKey(albumId);
        foreach (var track in ordered.Where(t => t.HasEmbeddedArt).Take(3))
        {
            try
            {
                var picture = await Task.Run(() => MusicTags.ReadPicture(_files.For(track.Path), track.Path), cancellationToken);
                if (picture is { } found)
                {
                    var extension = found.MimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                    var destination = _art.AlbumPath(albumId, extension);
                    Directory.CreateDirectory(_art.Root);
                    await File.WriteAllBytesAsync(destination, found.Data, cancellationToken);
                    return new MusicArtRecord(key, destination, "embedded", DateTimeOffset.UtcNow);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Could not read the picture in {Path}: {Message}", track.Path, ex.Message);
            }
        }

        foreach (var folder in ordered.Select(t => _files.For(t.Path).Parent(t.Path)).OfType<string>().Distinct().Take(3))
        {
            if (FindImage(list(folder), CoverNames) is { } image) return new MusicArtRecord(key, image, "folder", DateTimeOffset.UtcNow);
            if (MusicText.IsDiscFolder(Path.GetFileName(folder)) && _files.For(folder).Parent(folder) is { } parent
                && FindImage(list(parent), CoverNames) is { } parentImage)
                return new MusicArtRecord(key, parentImage, "folder", DateTimeOffset.UtcNow);
        }

        if (!online || ordered.Count == 0) return new MusicArtRecord(key, null, "none", DateTimeOffset.UtcNow);
        var first = ordered[0];
        try
        {
            var destination = _art.AlbumPath(albumId, ".jpg");
            var fetched = false;
            if (ordered.Select(t => t.MusicBrainzReleaseId).FirstOrDefault(id => id is not null) is { } release)
                fetched = await _coverArt.DownloadFrontAsync("release", release, destination, cancellationToken);
            var group = ordered.Select(t => t.MusicBrainzReleaseGroupId).FirstOrDefault(id => id is not null);
            if (!fetched && group is null && first.Album != MusicText.UnknownAlbum)
                group = await _coverArt.FindReleaseGroupAsync(first.AlbumArtist, first.Album, cancellationToken);
            if (!fetched && group is not null)
                fetched = await _coverArt.DownloadFrontAsync("release-group", group, destination, cancellationToken);
            return new MusicArtRecord(key, fetched ? destination : null, fetched ? "caa" : "none", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cover Art Archive lookup for {Album} failed: {Message}", first.Album, ex.Message);
            return null;
        }
    }

    private static string? FindImage(IReadOnlyList<MediaFsEntry> entries, string[] names)
    {
        var byName = new Dictionary<string, MediaFsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => !e.IsDirectory)) byName.TryAdd(entry.Name, entry);
        foreach (var name in names)
        foreach (var extension in ImageExtensions)
            if (byName.TryGetValue(name + extension, out var found)) return found.Path;
        return null;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _scanLock.Dispose();
    }
}

// Scans at startup and every RescanHours after, so an album copied onto the share tonight is on
// the shelf by morning without anyone pressing anything.
public sealed class MusicScanWorker(MusicScanner scanner, IMusicStore store, AppSettingsFile settings, ILogger<MusicScanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = await settings.GetSectionAsync<MusicSettingsDto>(MusicSettingsDto.SectionName, stoppingToken) ?? new();
                if (options.Enabled)
                {
                    var libraries = await store.GetLibrariesAsync(stoppingToken);
                    var due = first || (options.RescanHours > 0 && libraries.Count > 0
                        && (scanner.LastFinishedAt ?? libraries.Max(l => l.LastScanFinishedAt) ?? DateTimeOffset.MinValue)
                            < DateTimeOffset.UtcNow.AddHours(-options.RescanHours));
                    if (due && libraries.Count > 0 && scanner.TryStartScan(null))
                        logger.LogInformation("Music library scan started ({Reason})", first ? "startup" : "scheduled");
                }
                first = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Music scan scheduling failed"); }

            try { await Task.Delay(CheckInterval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
