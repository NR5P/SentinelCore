using System.Collections.Concurrent;

namespace SentinelCore.Web;

// A search that is still running, or finished within the last half hour. Kept in memory only:
// a restart simply means searching again.
public sealed class MediaDownloadSearch
{
    public const string Connecting = "connecting";
    public const string Searching = "searching";
    public const string Done = "done";
    public const string Failed = "failed";

    public Guid Id { get; init; }
    public required MediaDownloadSearchInput Input { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Phase { get; set; } = Connecting;
    public string? Error { get; set; }
    public List<MediaRelease> Results { get; set; } = [];
}

// Everything between "+ Add" and the new title on the Home screen: searches, the download queue,
// bringing the VPN and torrent services up when there is work and down again when there is none,
// and filing finished downloads into the library. The worker loop is MediaDownloadWorker.
public sealed class MediaDownloadManager(
    MediaDownloadStore downloads,
    IMediaStore media,
    MediaScanner scanner,
    IMediaDownloadStack stack,
    QbittorrentClient qbittorrent,
    ProwlarrClient prowlarr,
    AppSettingsFile settings,
    IHttpClientFactory httpClientFactory,
    MediaVpnConfig vpnConfig,
    IConfiguration configuration,
    ILogger<MediaDownloadManager> logger)
{
    public const string HomeIpClientName = "MediaDownloadsHomeIp";
    // How long a search or download waits for the VPN and the services to come up.
    private TimeSpan StartTimeout => TimeSpan.FromSeconds(configuration.GetValue("MediaDownloads:StartTimeoutSeconds", 120));
    private static readonly TimeSpan SearchesKeptFor = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, MediaDownloadSearch> _searches = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private (DateTimeOffset At, string Ip)? _homeIp;

    public static async Task<MediaDownloadsSettingsDto> ReadOptionsAsync(AppSettingsFile settings, CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<MediaDownloadsSettingsDto>(MediaDownloadsSettingsDto.SectionName, cancellationToken) ?? new MediaDownloadsSettingsDto();

    public Task<MediaDownloadsSettingsDto> OptionsAsync(CancellationToken cancellationToken) => ReadOptionsAsync(settings, cancellationToken);

    // Something someone did that needs the services: keeps them up for another IdleMinutes.
    public void Touch() => _lastActivity = DateTimeOffset.UtcNow;

    public DateTimeOffset LastActivity => _lastActivity;

    public void Wake() => _wake.Release();

    public Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken) => _wake.WaitAsync(timeout, cancellationToken);

    // MARK: - Services and VPN

    // Brings the VPN, qBittorrent and Prowlarr up (if this server runs them) and waits until the
    // tunnel is proven and both answer. Throws with something a person can act on.
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        Touch();
        var options = await OptionsAsync(cancellationToken);
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            var state = await stack.StateAsync(cancellationToken);
            if (state == MediaDownloadServiceStates.NotInstalled)
                throw new MediaDownloadException("The VPN and torrent services are not installed on this server yet. Run core/downloads/install-downloads.sh on it (see Settings -> MOVIE DOWNLOADS).");
            // The tunnel script reads its settings when it starts, so a change of VPN (on/off, or
            // another provider or location) while everything is running means starting over.
            // Worked out only when something changed or the services are about to start - a
            // Mullvad account is asked for a device once, not on every pass.
            var running = state is MediaDownloadServiceStates.Running or MediaDownloadServiceStates.Starting;
            var key = VpnSettingsKey(options);
            if ((!running || key != _appliedVpnKey)
                && await stack.ConfigureAsync(options.UseVpn, options.UseVpn ? await vpnConfig.BuildAsync(options, cancellationToken) : null, cancellationToken)
                && running)
            {
                logger.LogInformation("Movie downloads: VPN settings changed; restarting the services");
                _verified = null;
                await stack.StopAsync(cancellationToken);
                for (var i = 0; i < 30 && await stack.StateAsync(cancellationToken) is not (MediaDownloadServiceStates.Stopped or MediaDownloadServiceStates.Failed); i++)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                state = MediaDownloadServiceStates.Stopped;
            }
            _appliedVpnKey = key;
            if (state is MediaDownloadServiceStates.Stopped or MediaDownloadServiceStates.Failed) await stack.StartAsync(cancellationToken);

            var deadline = DateTimeOffset.UtcNow + StartTimeout;
            string? lastProblem = null;
            while (true)
            {
                lastProblem = await ProblemAsync(options, cancellationToken);
                if (lastProblem is null) return;
                if (DateTimeOffset.UtcNow > deadline) break;
                if (await stack.StateAsync(cancellationToken) == MediaDownloadServiceStates.Failed)
                {
                    lastProblem = "The VPN or torrent services failed to start. On the server, `journalctl -u sentinelcore-vpn` says why - usually the VPN configuration.";
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            if (options.UseVpn && options.VpnProvider == MediaVpnProviders.Mullvad
                && await vpnConfig.ForgetMullvadDeviceIfGoneAsync(options, cancellationToken))
            {
                _appliedVpnKey = null;
                lastProblem = "This server's key had been removed from your Mullvad account; a new one is registered on the next try.";
            }
            throw new MediaDownloadException(lastProblem ?? "The download services did not come up.");
        }
        finally { _startLock.Release(); }
    }

    private string? _appliedVpnKey;

    private static string VpnSettingsKey(MediaDownloadsSettingsDto o) => string.Join("\u001f",
        o.UseVpn, o.VpnProvider, o.MullvadAccountNumber, o.MullvadLocation, o.WireGuardPrivateKey, o.WireGuardAddress,
        o.WireGuardDns, o.WireGuardServerPublicKey, o.WireGuardServerEndpoint);

    // Null when everything answers; otherwise what is still wrong.
    private async Task<string?> ProblemAsync(MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        if (options.UseVpn)
        {
            var vpn = await stack.VpnStatusAsync(cancellationToken);
            if (vpn.State != MediaVpnStatus.Connected)
            {
                _verified = null;
                return vpn.Error is { } error ? $"Waiting for the VPN: {error}" : "Waiting for the VPN to connect.";
            }
            var home = await HomeIpAsync(cancellationToken);
            if (home is not null && home == vpn.Ip)
            {
                _verified = null;
                await stack.StopAsync(cancellationToken);
                return "The VPN is not hiding this server's address, so downloads were stopped. Check the VPN configuration.";
            }
            // Only "proven" when the home address was actually known to compare against.
            if (home is not null) _verified = (DateTimeOffset.UtcNow, vpn.Ip!);
        }
        try { await qbittorrent.VersionAsync(cancellationToken); }
        catch (QbittorrentException ex) { return ex.Message; }
        try { await prowlarr.VersionAsync(cancellationToken); }
        catch (ProwlarrException ex) { return ex.Message; }
        return null;
    }

    // The address this server is seen from without the VPN, to prove the tunnel changes it.
    private async Task<string?> HomeIpAsync(CancellationToken cancellationToken)
    {
        if (_homeIp is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(30)) return cached.Ip;
        try
        {
            var client = httpClientFactory.CreateClient(HomeIpClientName);
            var ip = (await client.GetStringAsync("https://api.ipify.org", cancellationToken)).Trim();
            _homeIp = (DateTimeOffset.UtcNow, ip);
            return ip;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return _homeIp?.Ip;
        }
    }

    public async Task<object> StatusAsync(CancellationToken cancellationToken)
    {
        var options = await OptionsAsync(cancellationToken);
        var state = await stack.StateAsync(cancellationToken);
        var vpn = await stack.VpnStatusAsync(cancellationToken);
        return new
        {
            enabled = options.Enabled,
            services = state,
            vpn = VpnDto(vpn, options),
            idleMinutes = options.IdleMinutes
        };
    }

    // When the tunnel was last proven to hide the home address, and what the sites saw instead.
    private (DateTimeOffset At, string Ip)? _verified;

    private object VpnDto(MediaVpnStatus vpn, MediaDownloadsSettingsDto options)
    {
        var verified = options.UseVpn && vpn.State == MediaVpnStatus.Connected && _verified is { } v && v.Ip == vpn.Ip ? v.At : (DateTimeOffset?)null;
        return new
        {
            // "disabled": Use VPN is off in Settings and downloads use the home connection.
            state = options.UseVpn ? vpn.State : MediaVpnStatus.Disabled,
            required = options.UseVpn,
            // The address the sites see was compared with the home address and differs.
            verified = verified is not null,
            verifiedAt = verified,
            ip = options.UseVpn ? vpn.Ip : null,
            location = options.UseVpn ? vpn.Location : null,
            country = options.UseVpn ? vpn.Country : null,
            provider = options.UseVpn ? vpn.Org : null,
            checkedAt = vpn.CheckedAt,
            error = options.UseVpn ? vpn.Error : null
        };
    }

    // "Test VPN": brings everything up and proves the tunnel, then says what the sites see.
    public async Task<(bool Ok, string Message)> TestVpnAsync(CancellationToken cancellationToken)
    {
        var options = await OptionsAsync(cancellationToken);
        try { await EnsureReadyAsync(cancellationToken); }
        catch (Exception ex) when (ex is MediaDownloadException or InvalidOperationException) { return (false, ex.Message); }
        if (!options.UseVpn)
            return (true, "The VPN is turned off in Settings: searching and downloading use your home internet connection, so the sites see your home address.");
        var vpn = await stack.VpnStatusAsync(cancellationToken);
        var where = vpn.Location is { } location ? $" in {location}" : "";
        return _verified is not null
            ? (true, $"Working. The sites see {vpn.Ip}{where}, not your home address.")
            : (true, $"Connected{where} ({vpn.Ip}), but your home address could not be looked up to compare it with.");
    }

    // MARK: - Searching

    public MediaDownloadSearch StartSearch(MediaDownloadSearchInput input)
    {
        foreach (var (id, old) in _searches)
            if (DateTimeOffset.UtcNow - old.CreatedAt > SearchesKeptFor) _searches.TryRemove(id, out _);

        var search = new MediaDownloadSearch { Id = Guid.NewGuid(), Input = input, CreatedAt = DateTimeOffset.UtcNow };
        _searches[search.Id] = search;
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureReadyAsync(CancellationToken.None);
                search.Phase = MediaDownloadSearch.Searching;
                search.Results = await SearchReleasesAsync(input, CancellationToken.None);
                search.Phase = MediaDownloadSearch.Done;
            }
            catch (Exception ex) when (ex is MediaDownloadException or ProwlarrException or QbittorrentException or InvalidOperationException)
            {
                search.Error = ex.Message;
                search.Phase = MediaDownloadSearch.Failed;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Movie downloads: search for {Title} failed", input.Title);
                search.Error = "The search failed: " + ex.Message;
                search.Phase = MediaDownloadSearch.Failed;
            }
        });
        return search;
    }

    public MediaDownloadSearch? GetSearch(Guid id) => _searches.TryGetValue(id, out var search) ? search : null;

    private async Task<List<MediaRelease>> SearchReleasesAsync(MediaDownloadSearchInput input, CancellationToken cancellationToken)
    {
        var options = await OptionsAsync(cancellationToken);
        var isMovie = input.Kind != MediaKinds.Show;
        var request = new MediaReleaseRanker.Request(isMovie ? MediaKinds.Movie : MediaKinds.Episode, input.Title, input.Year,
            input.Season, input.Episode, options.PreferredQuality);
        var title = SearchText(input.Title);
        // Some sites find nothing for a query that starts with "The" (The Pirate Bay: "The Addams
        // Family" 0 results, "Addams Family" 86), so the title is tried without a leading article
        // too. Results are still matched against the full title.
        var titles = new List<string> { title };
        if (System.Text.RegularExpressions.Regex.Match(title, @"^(?:the|a|an)\s+(.{3,})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } bare)
            titles.Add(bare.Groups[1].Value);
        var queries = new List<string>();
        foreach (var t in titles)
        {
            if (isMovie)
            {
                if (input.Year is { } year) queries.Add($"{t} {year}");
                queries.Add(t);
            }
            else if (input.Season is { } season)
            {
                if (input.Episode is { } episode) queries.Add($"{t} S{season:00}E{episode:00}");
                else { queries.Add($"{t} S{season:00}"); queries.Add($"{t} Season {season}"); }
            }
            else queries.Add(t);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var releases = new List<MediaRelease>();
        foreach (var query in queries)
        {
            var found = await prowlarr.SearchAsync(query, isMovie ? ProwlarrClient.MovieCategory : ProwlarrClient.TvCategory, cancellationToken);
            foreach (var release in found)
            {
                var source = release.MagnetUrl ?? release.DownloadUrl;
                if (source is null) continue;
                if (!seen.Add(release.InfoHash ?? $"{release.Title}|{release.Size}")) continue;
                var parsed = MediaReleaseRanker.Parse(release.Title);
                var matches = MediaReleaseRanker.Matches(parsed, request);
                releases.Add(new MediaRelease(
                    $"r{releases.Count + 1}", release.Title, release.Indexer, release.Size, release.Seeders, release.Leechers, release.PublishDate,
                    parsed.Quality, parsed.Codec, parsed.Source, parsed.Hdr, parsed.Season, parsed.Episode, parsed.SeasonPack, matches,
                    MediaReleaseRanker.Warning(parsed, release.Seeders),
                    MediaReleaseRanker.Score(parsed, matches, release.Seeders, release.Size, options.PreferredQuality),
                    source, release.InfoHash?.ToLowerInvariant()));
            }
            // The broader query is only worth its time when the precise one found nothing usable.
            if (releases.Any(r => r.Matches && r.Seeders is > 0)) break;
        }
        return releases.OrderByDescending(r => r.Score).ThenByDescending(r => r.Seeders ?? 0).ToList();
    }

    // Sites search words, not punctuation: "Dune: Part Two" is found as "Dune Part Two".
    private static string SearchText(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title.Replace("&", "and"), @"[^\p{L}\p{N}' ]+", " ").Replace("'", "").Trim() is var text && text.Length > 0
            ? System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ")
            : title;

    // MARK: - Queue

    public async Task<MediaDownload> GrabAsync(MediaDownloadGrabInput input, string requestedBy, CancellationToken cancellationToken)
    {
        var search = GetSearch(input.SearchId) ?? throw new MediaDownloadException("That search has expired. Search again.");
        var release = search.Results.FirstOrDefault(r => r.Id == input.ReleaseId) ?? throw new MediaDownloadException("That release is not in the search any more. Search again.");
        var kind = search.Input.Kind == MediaKinds.Show ? MediaKinds.Episode : MediaKinds.Movie;
        var library = await PickLibraryAsync(kind, input.LibraryId, cancellationToken);

        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            if (release.InfoHash is { } hash)
                foreach (var existing in await downloads.AllAsync(cancellationToken))
                    if (existing.InfoHash == hash && MediaDownloadStates.IsActive(existing.State)) return existing;

            var id = Guid.NewGuid();
            var download = new MediaDownload
            {
                Id = id,
                Kind = kind,
                Title = search.Input.Title.Trim(),
                Year = search.Input.Year,
                TmdbId = search.Input.TmdbId,
                // A season pack found by a whole-show search still says which season it is.
                Season = kind == MediaKinds.Episode ? search.Input.Season ?? release.Season : null,
                Episode = kind == MediaKinds.Episode ? (search.Input.Season is null ? release.Episode : search.Input.Episode) : null,
                PosterPath = search.Input.PosterPath,
                ReleaseTitle = release.Title,
                Indexer = release.Indexer,
                SizeBytes = release.SizeBytes,
                Source = release.DownloadSource,
                InfoHash = release.InfoHash,
                LibraryId = library.Id,
                StagingPath = Path.Combine(library.Path, ".downloads", id.ToString("N")),
                RequestedBy = requestedBy,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await downloads.SaveAsync(download, cancellationToken);
            logger.LogInformation("Movie downloads: {User} queued {Release} for {Title}", requestedBy, release.Title, download.Title);
            Touch();
            Wake();
            return download;
        }
        finally { _queueLock.Release(); }
    }

    private async Task<MediaLibrary> PickLibraryAsync(string kind, Guid? libraryId, CancellationToken cancellationToken)
    {
        var wanted = kind == MediaKinds.Movie ? MediaLibraryKinds.Movies : MediaLibraryKinds.Shows;
        var libraries = (await media.GetLibrariesAsync(cancellationToken))
            .Where(l => l.Kind == wanted && !MediaNetworkPath.IsNetwork(l.Path))
            .ToList();
        var library = libraryId is { } id ? libraries.FirstOrDefault(l => l.Id == id) : libraries.FirstOrDefault(l => Directory.Exists(l.Path));
        if (library is null)
            throw new MediaDownloadException(kind == MediaKinds.Movie
                ? "There is no movie library on this server's own disks to save to. Add one under Movies -> Libraries."
                : "There is no TV library on this server's own disks to save to. Add one (kind: TV shows) under Movies -> Libraries.");
        if (!Directory.Exists(library.Path)) throw new MediaDownloadException($"{library.Path} is not there. Is the disk mounted?");
        return library;
    }

    public async Task<IReadOnlyList<MediaDownload>> ListAsync(CancellationToken cancellationToken) => await downloads.AllAsync(cancellationToken);

    public async Task<MediaDownload?> GetAsync(Guid id, CancellationToken cancellationToken) => await downloads.GetAsync(id, cancellationToken);

    // Stops an active download (its files go too) or forgets a finished one.
    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            var download = await downloads.GetAsync(id, cancellationToken);
            if (download is null) return false;
            if (!MediaDownloadStates.IsActive(download.State))
            {
                await downloads.DeleteAsync(id, cancellationToken);
                return true;
            }
            if (download.State is MediaDownloadStates.Downloading or MediaDownloadStates.Seeding)
            {
                try
                {
                    if (await FindTorrentAsync(download, cancellationToken) is { } torrent)
                        await qbittorrent.DeleteAsync(torrent.Hash, deleteFiles: true, cancellationToken);
                }
                catch (QbittorrentException ex) { logger.LogWarning("Movie downloads: could not remove {Title} from qBittorrent: {Message}", download.Title, ex.Message); }
            }
            DeleteStaging(download);
            download.State = MediaDownloadStates.Canceled;
            download.FinishedAt = DateTimeOffset.UtcNow;
            await downloads.SaveAsync(download, cancellationToken);
            Wake();
            return true;
        }
        finally { _queueLock.Release(); }
    }

    public async Task<MediaDownload?> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            var download = await downloads.GetAsync(id, cancellationToken);
            if (download is null || download.State != MediaDownloadStates.Failed) return download;
            download.State = MediaDownloadStates.Queued;
            download.Error = null;
            download.Progress = 0;
            download.FinishedAt = null;
            await downloads.SaveAsync(download, cancellationToken);
            Touch();
            Wake();
            return download;
        }
        finally { _queueLock.Release(); }
    }

    // MARK: - Worker

    // One pass of the worker: start what is queued, follow what is downloading, file what is
    // finished, and shut the services down once nothing has needed them for IdleMinutes.
    // Returns whether anything is still active.
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var options = await OptionsAsync(cancellationToken);
        var all = await downloads.AllAsync(cancellationToken);
        var active = all.Where(d => MediaDownloadStates.IsActive(d.State)).ToList();

        if (active.Count == 0 || !options.Enabled)
        {
            await StopWhenIdleAsync(options, cancellationToken);
            return false;
        }
        Touch();

        try { await EnsureReadyAsync(cancellationToken); }
        catch (Exception ex) when (ex is MediaDownloadException or InvalidOperationException)
        {
            foreach (var waiting in active.Where(d => d.Error != ex.Message))
            {
                waiting.Error = ex.Message;
                await downloads.SaveAsync(waiting, cancellationToken);
            }
            return true;
        }

        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            var torrents = await qbittorrent.ListAsync(cancellationToken);
            foreach (var download in active)
            {
                // Re-read: a cancel may have landed while this pass was waiting for the lock.
                var current = await downloads.GetAsync(download.Id, cancellationToken);
                if (current is null || !MediaDownloadStates.IsActive(current.State)) continue;
                try { await AdvanceAsync(current, torrents, options, cancellationToken); }
                catch (Exception ex) when (ex is QbittorrentException or ProwlarrException or MediaDownloadException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Movie downloads: {Title} failed: {Message}", current.Title, ex.Message);
                    current.State = MediaDownloadStates.Failed;
                    current.Error = ex.Message;
                    current.FinishedAt = DateTimeOffset.UtcNow;
                    await downloads.SaveAsync(current, cancellationToken);
                }
            }
        }
        catch (QbittorrentException ex)
        {
            logger.LogWarning("Movie downloads: qBittorrent is not answering: {Message}", ex.Message);
        }
        finally { _queueLock.Release(); }
        return true;
    }

    private async Task StopWhenIdleAsync(MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastActivity < TimeSpan.FromMinutes(Math.Max(1, options.IdleMinutes))) return;
        var state = await stack.StateAsync(cancellationToken);
        if (state is MediaDownloadServiceStates.Running or MediaDownloadServiceStates.Starting)
            await stack.StopAsync(cancellationToken);
    }

    private async Task AdvanceAsync(MediaDownload download, IReadOnlyList<QbittorrentClient.Torrent> torrents, MediaDownloadsSettingsDto options, CancellationToken cancellationToken)
    {
        var torrent = torrents.FirstOrDefault(t => HasTag(t, download));
        if (download.State == MediaDownloadStates.Queued)
        {
            if (torrent is null)
            {
                var (magnet, file) = await prowlarr.FetchReleaseAsync(download.Source, cancellationToken);
                Directory.CreateDirectory(download.StagingPath);
                await qbittorrent.AddAsync(magnet, file, download.StagingPath, Tag(download), cancellationToken);
                logger.LogInformation("Movie downloads: started {Release}", download.ReleaseTitle);
            }
            download.State = MediaDownloadStates.Downloading;
            download.Error = null;
            await downloads.SaveAsync(download, cancellationToken);
            return;
        }

        if (torrent is null)
        {
            // Just added: qBittorrent can take a moment to list a magnet link.
            if (download.State == MediaDownloadStates.Downloading && download.DownloadedBytes == 0 && DateTimeOffset.UtcNow - download.CreatedAt < TimeSpan.FromMinutes(2)) return;
            throw new MediaDownloadException("The torrent disappeared from qBittorrent.");
        }
        if (torrent.State is "error" or "missingFiles")
            throw new MediaDownloadException(torrent.State == "missingFiles" ? "qBittorrent lost the downloaded files." : "qBittorrent reported an error with this torrent. Try another release.");

        download.InfoHash ??= torrent.Hash;
        download.Progress = Math.Clamp(torrent.Progress, 0, 1);
        download.DownloadedBytes = torrent.Downloaded;
        download.SizeBytes = torrent.Size > 0 ? torrent.Size : download.SizeBytes;
        download.SpeedBytesPerSecond = torrent.DownloadSpeed;
        download.EtaSeconds = torrent.EtaSeconds;
        download.Peers = torrent.Seeds + torrent.Peers;
        download.Error = torrent.State is "stalledDL" or "metaDL" && torrent.Seeds == 0 ? "Looking for people sharing it..." : null;

        if (download.Progress < 1)
        {
            await downloads.SaveAsync(download, cancellationToken);
            return;
        }

        download.CompletedAt ??= torrent.CompletedAt ?? DateTimeOffset.UtcNow;
        download.SpeedBytesPerSecond = 0;
        download.EtaSeconds = null;
        if (DateTimeOffset.UtcNow < download.CompletedAt.Value.AddMinutes(options.SeedMinutes))
        {
            download.State = MediaDownloadStates.Seeding;
            download.Error = null;
            await downloads.SaveAsync(download, cancellationToken);
            return;
        }

        download.State = MediaDownloadStates.Importing;
        download.Error = null;
        await downloads.SaveAsync(download, cancellationToken);
        await ImportAsync(download, torrent, cancellationToken);
    }

    private async Task ImportAsync(MediaDownload download, QbittorrentClient.Torrent torrent, CancellationToken cancellationToken)
    {
        var files = await qbittorrent.FilesAsync(torrent.Hash, cancellationToken);
        // Out of qBittorrent first (keeping the files), so nothing is still writing or sharing them.
        await qbittorrent.DeleteAsync(torrent.Hash, deleteFiles: false, cancellationToken);

        var library = await media.GetLibraryAsync(download.LibraryId, cancellationToken)
            ?? throw new MediaDownloadException("The library it was going into has been removed.");
        string? showFolder = null;
        if (download.Kind == MediaKinds.Episode) showFolder = await ExistingShowFolderAsync(library, download, cancellationToken);

        var plan = MediaDownloadImporter.Plan(download, files.Select(f => new MediaDownloadImporter.SourceFile(f.Name, f.Size)).ToList(),
            download.StagingPath, library, showFolder);
        if (plan.Count == 0)
            throw new MediaDownloadException(download.Kind == MediaKinds.Movie
                ? "The download had no video file in it."
                : "None of the videos in the download were the episodes asked for.");

        var landed = await Task.Run(() => MediaDownloadImporter.Execute(plan), cancellationToken);
        DeleteStaging(download);
        download.ImportedPaths = landed;
        download.ItemId = download.Kind == MediaKinds.Movie
            ? landed.Count > 0 ? MediaDownloadImporter.MovieItemId(library, landed[0]) : null
            : MediaDownloadImporter.ShowItemId(library, showFolder ?? Path.GetDirectoryName(Path.GetDirectoryName(plan[0].Destination)!)!);
        download.State = MediaDownloadStates.Done;
        download.Progress = 1;
        download.FinishedAt = DateTimeOffset.UtcNow;
        await downloads.SaveAsync(download, cancellationToken);
        logger.LogInformation("Movie downloads: filed {Count} file(s) for {Title} into {Library}", landed.Count, download.Title, library.Name);
        scanner.RequestScan(library.Id);
    }

    // The folder a show already has in the library, so a new season joins the old ones instead
    // of starting a second folder.
    private async Task<string?> ExistingShowFolderAsync(MediaLibrary library, MediaDownload download, CancellationToken cancellationToken)
    {
        var shows = await media.GetItemsAsync(new MediaQuery(Kinds: [MediaKinds.Show], LibraryId: library.Id), cancellationToken);
        var show = shows.FirstOrDefault(s => download.TmdbId is { } tmdb && s.TmdbId == tmdb)
            ?? shows.FirstOrDefault(s => MediaReleaseRanker.TitleMatches(s.Title, download.Title));
        return show is not null && Directory.Exists(show.Path) ? show.Path : null;
    }

    private async Task<QbittorrentClient.Torrent?> FindTorrentAsync(MediaDownload download, CancellationToken cancellationToken) =>
        (await qbittorrent.ListAsync(cancellationToken)).FirstOrDefault(t => HasTag(t, download));

    private static string Tag(MediaDownload download) => "sc-" + download.Id.ToString("N");

    private static bool HasTag(QbittorrentClient.Torrent torrent, MediaDownload download) =>
        torrent.Tags.Split(',', StringSplitOptions.TrimEntries).Contains(Tag(download))
        || (download.InfoHash is { } hash && string.Equals(torrent.Hash, hash, StringComparison.OrdinalIgnoreCase));

    private void DeleteStaging(MediaDownload download)
    {
        try
        {
            if (Directory.Exists(download.StagingPath)) Directory.Delete(download.StagingPath, recursive: true);
            var parent = Path.GetDirectoryName(download.StagingPath);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Movie downloads: could not tidy {Path}: {Message}", download.StagingPath, ex.Message);
        }
    }
}

public sealed class MediaDownloadException(string message) : Exception(message);

public sealed class MediaDownloadWorker(MediaDownloadManager manager, ILogger<MediaDownloadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Downloads left running when the server last stopped carry on.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!stoppingToken.IsCancellationRequested)
        {
            var busy = false;
            try { busy = await manager.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Movie downloads: worker pass failed"); }
            try { await manager.WaitForWorkAsync(busy ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
