namespace SentinelCore.Web;

// "Import a list" on Music and Movies: a list of albums or films (from MediaImportList) worked
// through one at a time in the background. Each one is searched on the household's sites through
// the same VPN-only stack as "+ Add", the best matching release with people sharing it is picked
// the way a person would pick it from the list, and it joins the ordinary download queue.
// Contract: core/docs/MOVIE_DOWNLOADS.md "Import a list".

public static class MediaImportStates
{
    public const string Waiting = "waiting";
    public const string Searching = "searching";
    // A download was queued; how it is getting on is that download's own state.
    public const string Queued = "queued";
    public const string InLibrary = "inLibrary";
    public const string NotFound = "notFound";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

public sealed class MediaImportJob
{
    public Guid Id { get; set; }
    // MediaImportList.KindMusic or KindMovies.
    public string Kind { get; set; } = MediaImportList.KindMusic;
    public string Name { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public bool RequestedByAdmin { get; set; }
    public Guid? LibraryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    // Why nothing is moving: "Waiting for the VPN: ..." while the services will not start.
    public string? Message { get; set; }
    public bool Paused { get; set; }
    public List<MediaImportItem> Items { get; set; } = [];
}

public sealed class MediaImportItem
{
    public string Id { get; set; } = string.Empty;
    public string? Artist { get; set; }
    // The album, or the film.
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int TrackCount { get; set; }
    public string State { get; set; } = MediaImportStates.Waiting;
    public string? Message { get; set; }
    public Guid? DownloadId { get; set; }
    public string? ReleaseTitle { get; set; }
}

public sealed class MediaImportPreviewInput
{
    public List<MediaImportFileInput> Files { get; set; } = [];
}

public sealed class MediaImportFileInput
{
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class MediaImportStartInput
{
    public string? Name { get; set; }
    public Guid? LibraryId { get; set; }
    public List<MediaImportStartItem> Items { get; set; } = [];
}

public sealed class MediaImportStartItem
{
    public string? Artist { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int TrackCount { get; set; }
}

public sealed class MediaImportService(
    MediaDownloadStore store,
    MediaDownloadManager manager,
    IConfiguration configuration,
    ILogger<MediaImportService> logger,
    IMusicAudioSource? music = null)
{
    public const int MaxItems = 5000;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    // How many of one list's downloads run at once. qBittorrent would take them all, but a few at
    // a time finishes albums sooner (each gets the bandwidth) and stops a 400-album list from
    // filling the disk with half-done downloads.
    private int ActiveDownloads => Math.Clamp(configuration.GetValue("MediaDownloads:ImportActiveDownloads", 3), 1, 20);
    // A search that never finishes (the VPN hung part way) gives up after this.
    private TimeSpan SearchTimeout => TimeSpan.FromSeconds(configuration.GetValue("MediaDownloads:ImportSearchTimeoutSeconds", 600));
    // After the services fail to start, how long until the next try.
    private TimeSpan RetryAfterTrouble => TimeSpan.FromSeconds(configuration.GetValue("MediaDownloads:ImportRetrySeconds", 300));
    // A download with nobody sharing it stops holding one of the list's slots after this.
    private static readonly TimeSpan StuckAfter = TimeSpan.FromHours(1);

    public void Wake() => _wake.Release();

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _wake.WaitAsync(timeout, cancellationToken);

    // MARK: - Preview

    public async Task<object> PreviewAsync(MediaImportPreviewInput input, string expectedKind, CancellationToken cancellationToken)
    {
        var parsed = MediaImportList.Parse(input.Files.Select(f => new MediaImportList.ImportFile(f.Name ?? "", f.Text ?? "")).ToList());
        var albums = new List<object>();
        if (expectedKind == MediaImportList.KindMusic)
            foreach (var album in parsed.Albums)
                albums.Add(new
                {
                    artist = album.Artist,
                    title = album.Title,
                    year = album.Year,
                    trackCount = album.TrackCount,
                    tracks = album.Tracks.Take(5),
                    single = album.Single,
                    inLibrary = music is not null && await music.HasAlbumAsync(album.Artist, album.Title, cancellationToken)
                });
        var movies = new List<object>();
        if (expectedKind == MediaImportList.KindMovies)
            foreach (var movie in parsed.Movies)
                movies.Add(new
                {
                    title = movie.Title,
                    year = movie.Year,
                    imdbId = movie.ImdbId,
                    inLibrary = await manager.HasMovieAsync(movie.Title, movie.Year, cancellationToken)
                });

        var problems = parsed.Problems.ToList();
        if (expectedKind == MediaImportList.KindMusic && parsed.Albums.Count == 0 && parsed.Movies.Count > 0)
            problems.Add("That looks like a list of movies. Import it on the Movies tab.");
        if (expectedKind == MediaImportList.KindMovies && parsed.Movies.Count == 0 && parsed.Albums.Count > 0)
            problems.Add("That looks like a list of music. Import it on the Music tab.");
        return new
        {
            kind = expectedKind,
            sources = parsed.Sources.Distinct(),
            tracks = parsed.Tracks,
            noAlbum = parsed.NoAlbum,
            shows = parsed.Shows,
            unreadable = parsed.Unreadable,
            problems,
            albums,
            movies
        };
    }

    // MARK: - Lists

    public async Task<MediaImportJob> StartAsync(string kind, MediaImportStartInput input, string requestedBy, bool isAdmin, CancellationToken cancellationToken)
    {
        var items = new List<MediaImportItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wanted in input.Items)
        {
            var title = wanted.Title?.Trim() ?? "";
            if (title.Length == 0) continue;
            var artist = kind == MediaImportList.KindMusic ? (wanted.Artist?.Trim() ?? "") : null;
            var key = kind == MediaImportList.KindMusic ? MediaImportList.AlbumKey(artist!, title) : MediaImportList.MovieKey(title, wanted.Year);
            if (!seen.Add(key)) continue;
            items.Add(new MediaImportItem { Id = $"i{items.Count + 1}", Artist = artist, Title = title, Year = wanted.Year, TrackCount = Math.Max(0, wanted.TrackCount) });
        }
        if (items.Count == 0) throw new MediaDownloadException(kind == MediaImportList.KindMusic ? "Choose at least one album." : "Choose at least one movie.");
        if (items.Count > MaxItems) throw new MediaDownloadException($"That is more than {MaxItems} at once. Split the list up.");

        var job = new MediaImportJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(input.Name) ? "Imported list" : input.Name.Trim()[..Math.Min(input.Name.Trim().Length, 120)],
            RequestedBy = requestedBy,
            RequestedByAdmin = isAdmin,
            LibraryId = input.LibraryId,
            CreatedAt = DateTimeOffset.UtcNow,
            Items = items
        };
        await store.SaveImportAsync(job, cancellationToken);
        logger.LogInformation("Imports: {User} started {Name} ({Count} {Kind})", requestedBy, job.Name, items.Count, kind);
        Wake();
        return job;
    }

    public async Task<IReadOnlyList<MediaImportJob>> ListAsync(string kind, CancellationToken cancellationToken) =>
        (await store.AllImportsAsync(cancellationToken)).Where(j => j.Kind == kind).OrderByDescending(j => j.CreatedAt).ToList();

    public Task<MediaImportJob?> GetAsync(Guid id, CancellationToken cancellationToken) => store.GetImportAsync(id, cancellationToken);

    // Changes one list under the lock the worker also takes, so a cancel never loses to a pass
    // that read the list before it.
    public async Task<MediaImportJob?> UpdateAsync(Guid id, Func<MediaImportJob, bool> change, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (await store.GetImportAsync(id, cancellationToken) is not { } job) return null;
            if (change(job)) await store.SaveImportAsync(job, cancellationToken);
            return job;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (await store.GetImportAsync(id, cancellationToken) is null) return false;
            await store.DeleteImportAsync(id, cancellationToken);
            return true;
        }
        finally { _lock.Release(); }
    }

    public async Task<Dictionary<Guid, MediaDownload>> DownloadsAsync(CancellationToken cancellationToken) =>
        (await store.AllAsync(cancellationToken)).ToDictionary(d => d.Id);

    // MARK: - Working through the lists

    // One step of the oldest list that has something waiting. Returns how long to rest before the
    // next step: short while there is work, long when there is none.
    public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        var idle = TimeSpan.FromSeconds(60);
        var jobs = (await store.AllImportsAsync(cancellationToken))
            .Where(j => !j.Paused && j.Items.Any(i => i.State is MediaImportStates.Waiting or MediaImportStates.Searching))
            .OrderBy(j => j.CreatedAt)
            .ToList();
        if (jobs.Count == 0) return idle;
        var options = await manager.OptionsAsync(cancellationToken);
        if (!options.Enabled) return idle;

        var downloads = await DownloadsAsync(cancellationToken);
        foreach (var job in jobs)
        {
            // A list keeps a few downloads going at once; the next list waits its turn rather
            // than all of them piling into qBittorrent together.
            var running = job.Items.Count(i => i.DownloadId is { } d && downloads.TryGetValue(d, out var download)
                && MediaDownloadStates.IsActive(download.State)
                && (download.Progress > 0 || DateTimeOffset.UtcNow - download.CreatedAt < StuckAfter));
            if (running >= ActiveDownloads) return TimeSpan.FromSeconds(30);

            var item = job.Items.First(i => i.State is MediaImportStates.Waiting or MediaImportStates.Searching);
            return await WorkOnAsync(job.Id, item.Id, cancellationToken);
        }
        return idle;
    }

    private async Task<TimeSpan> WorkOnAsync(Guid jobId, string itemId, CancellationToken cancellationToken)
    {
        MediaImportJob? job = null;
        MediaImportItem? item = null;
        bool Find(MediaImportJob j)
        {
            job = j;
            item = j.Items.FirstOrDefault(i => i.Id == itemId && i.State is MediaImportStates.Waiting or MediaImportStates.Searching);
            return item is not null;
        }

        // Still wanted? It may have arrived some other way since the list was made.
        if (await UpdateAsync(jobId, Find, cancellationToken) is null || item is null) return TimeSpan.Zero;
        var isMusic = job!.Kind == MediaImportList.KindMusic;
        var have = isMusic
            ? music is not null && await music.HasAlbumAsync(item.Artist ?? "", item.Title, cancellationToken)
            : await manager.HasMovieAsync(item.Title, item.Year, cancellationToken);
        if (have)
        {
            await SetAsync(jobId, itemId, i => { i.State = MediaImportStates.InLibrary; i.Message = null; }, cancellationToken);
            return TimeSpan.Zero;
        }

        await SetAsync(jobId, itemId, i => i.State = MediaImportStates.Searching, cancellationToken);
        var search = isMusic
            ? manager.StartAlbumSearch(new MusicDownloadSearchInput { Artist = item.Artist ?? "", Album = item.Title, Year = item.Year })
            : manager.StartSearch(new MediaDownloadSearchInput { Kind = MediaKinds.Movie, Title = item.Title, Year = item.Year });
        var started = DateTimeOffset.UtcNow;
        while (search.Phase is MediaDownloadSearch.Connecting or MediaDownloadSearch.Searching)
        {
            if (DateTimeOffset.UtcNow - started > SearchTimeout)
            {
                search.Error = "The search took too long.";
                search.Phase = MediaDownloadSearch.Failed;
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        if (search.Phase == MediaDownloadSearch.Failed)
        {
            // The VPN or the sites, not this album: put it back and try again later, rather than
            // marking every album on the list "failed" while the VPN is down.
            var message = search.Error ?? "The search failed.";
            logger.LogInformation("Imports: search for {Title} failed, trying again later: {Message}", item.Title, message);
            await UpdateAsync(jobId, j =>
            {
                j.Message = $"Waiting to try again: {message}";
                if (j.Items.FirstOrDefault(i => i.Id == itemId) is { State: MediaImportStates.Searching } back) back.State = MediaImportStates.Waiting;
                return true;
            }, cancellationToken);
            return RetryAfterTrouble;
        }

        string? releaseId = null;
        string? releaseTitle = null;
        var found = isMusic ? search.AlbumResults.Count : search.Results.Count;
        if (isMusic)
        {
            // Results are best first. A discography would be far more than the album, so it is
            // only ever picked by a person.
            var best = search.AlbumResults.FirstOrDefault(r => r.Matches && r.Seeders is > 0 && !MusicReleaseRanker.Parse(r.Title).Discography);
            (releaseId, releaseTitle) = (best?.Id, best?.Title);
        }
        else
        {
            var best = search.Results.FirstOrDefault(r => r.Matches && r.Seeders is > 0);
            (releaseId, releaseTitle) = (best?.Id, best?.Title);
        }

        if (releaseId is null)
        {
            var why = found == 0 ? "Nothing on your sites." : $"{found} found, but none match it with people sharing.";
            await SetAsync(jobId, itemId, i => { i.State = MediaImportStates.NotFound; i.Message = why; }, cancellationToken, clearJobMessage: true);
            return TimeSpan.FromSeconds(2);
        }

        try
        {
            // Canceled while it searched? Then nothing is queued.
            if (await GetAsync(jobId, cancellationToken) is not { } current || current.Items.FirstOrDefault(i => i.Id == itemId)?.State != MediaImportStates.Searching)
                return TimeSpan.Zero;
            var download = isMusic
                ? await manager.GrabAlbumAsync(new MusicDownloadGrabInput { SearchId = search.Id, ReleaseId = releaseId, LibraryId = job.LibraryId }, job.RequestedBy, cancellationToken)
                : await manager.GrabAsync(new MediaDownloadGrabInput { SearchId = search.Id, ReleaseId = releaseId, LibraryId = job.LibraryId, Replace = false },
                    job.RequestedBy, cancellationToken, mayReplace: false);
            await SetAsync(jobId, itemId, i =>
            {
                i.State = MediaImportStates.Queued;
                i.DownloadId = download.Id;
                i.ReleaseTitle = releaseTitle;
                i.Message = null;
            }, cancellationToken, clearJobMessage: true);
        }
        catch (MediaDownloadException ex)
        {
            await SetAsync(jobId, itemId, i => { i.State = MediaImportStates.Failed; i.Message = ex.Message; }, cancellationToken, clearJobMessage: true);
        }
        return TimeSpan.FromSeconds(2);
    }

    private Task SetAsync(Guid jobId, string itemId, Action<MediaImportItem> change, CancellationToken cancellationToken, bool clearJobMessage = false) =>
        UpdateAsync(jobId, j =>
        {
            // A cancel that landed meanwhile wins.
            if (j.Items.FirstOrDefault(i => i.Id == itemId) is not { } item || item.State == MediaImportStates.Canceled) return false;
            change(item);
            if (clearJobMessage) j.Message = null;
            return true;
        }, cancellationToken);
}

public sealed class MediaImportWorker(MediaImportService imports, ILogger<MediaImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lists left part way when the server last stopped carry on, after the download worker
        // has had its own head start.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!stoppingToken.IsCancellationRequested)
        {
            var rest = TimeSpan.FromSeconds(60);
            try { rest = await imports.StepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Imports: pass failed"); }
            if (rest <= TimeSpan.Zero) continue;
            try { await imports.WaitAsync(rest, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
