using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace SentinelCore.Web;

// Puts episodes on the server's disk, two at a time. A download is written to "<id>.<ext>.part"
// and resumed from where it stopped (HTTP Range) after a restart or a dropped connection; only a
// finished file gets its real name. Then, per the show's settings, ffmpeg makes it clearer (voice
// boost) and shorter (trim silence), and the household's limits are applied: the newest N per
// show, the storage cap, and played episodes cleared after a few days.
public sealed class PodcastDownloadManager
{
    public const string HttpClientName = "PodcastDownloads";
    public const int Parallel = 2;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".m4b", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wav", ".flac" };

    private readonly IPodcastStore _store;
    private readonly IHttpClientFactory _clients;
    private readonly PodcastAudioProcessor _audio;
    private readonly PodcastLocations _locations;
    private readonly AppSettingsFile _settings;
    private readonly ILogger<PodcastDownloadManager> _logger;
    private readonly ConcurrentDictionary<Guid, ActiveDownload> _active = new();
    private readonly SemaphoreSlim _pump = new(1, 1);
    private readonly SemaphoreSlim _limits = new(1, 1);

    private sealed class ActiveDownload(CancellationTokenSource cancel)
    {
        public CancellationTokenSource Cancel { get; } = cancel;
        public double Progress { get; set; }
    }

    public PodcastDownloadManager(IPodcastStore store, IHttpClientFactory clients, PodcastAudioProcessor audio,
        PodcastLocations locations, AppSettingsFile settings, ILogger<PodcastDownloadManager> logger)
    {
        _store = store;
        _clients = clients;
        _audio = audio;
        _locations = locations;
        _settings = settings;
        _logger = logger;
    }

    // Raised with the episode (and its finished download) once a file is ready.
    public event Action<PodcastEpisode>? Completed;

    public int ActiveCount => _active.Count;

    public double? ProgressOf(Guid episodeId) => _active.TryGetValue(episodeId, out var active) ? active.Progress : null;

    public bool IsActive(Guid episodeId) => _active.ContainsKey(episodeId);

    public async Task<int> QueuedCountAsync(CancellationToken cancellationToken) =>
        (await _store.GetDownloadsAsync(cancellationToken)).Count(d => d.State == PodcastDownloadStates.Queued);

    public async Task<PodcastDownload> RequestAsync(Guid episodeId, bool transcribeAfter, CancellationToken cancellationToken)
    {
        var existing = await _store.GetDownloadAsync(episodeId, cancellationToken) ?? new PodcastDownload { EpisodeId = episodeId };
        if (existing.IsDone && File.Exists(existing.FilePath))
        {
            if (transcribeAfter && !existing.TranscribeAfter)
            {
                existing.TranscribeAfter = true;
                await _store.SaveDownloadAsync(existing, cancellationToken);
                if (await _store.GetEpisodeAsync(episodeId, cancellationToken) is { } done) Completed?.Invoke(done);
            }
            return existing;
        }
        if (existing.State is PodcastDownloadStates.Queued or PodcastDownloadStates.Downloading && (IsActive(episodeId) || existing.State == PodcastDownloadStates.Queued))
        {
            if (transcribeAfter && !existing.TranscribeAfter)
            {
                existing.TranscribeAfter = true;
                await _store.SaveDownloadAsync(existing, cancellationToken);
            }
            return existing;
        }

        existing.State = PodcastDownloadStates.Queued;
        existing.Error = null;
        existing.FilePath = null;
        existing.RequestedAt = DateTimeOffset.UtcNow;
        existing.TranscribeAfter |= transcribeAfter;
        await _store.SaveDownloadAsync(existing, cancellationToken);
        Kick();
        return existing;
    }

    // Stops a running download and removes the episode's files, part files included.
    public async Task DeleteAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        if (_active.TryRemove(episodeId, out var active))
        {
            try { await active.Cancel.CancelAsync(); } catch (ObjectDisposedException) { }
        }
        var episode = await _store.GetEpisodeAsync(episodeId, cancellationToken);
        var download = await _store.GetDownloadAsync(episodeId, cancellationToken);
        if (download is null) return;
        await DeleteFilesAsync(episode, download, cancellationToken);
        download.State = PodcastDownloadStates.None;
        download.FilePath = null;
        download.FileBytes = null;
        download.FileDurationSeconds = null;
        download.FileMimeType = null;
        download.Enhanced = false;
        download.Cuts = [];
        download.Error = null;
        download.DownloadedAt = null;
        await _store.SaveDownloadAsync(download, cancellationToken);
    }

    private async Task DeleteFilesAsync(PodcastEpisode? episode, PodcastDownload download, CancellationToken cancellationToken)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        if (download.FilePath is { } file && Path.GetDirectoryName(file) is { } dir) folders.Add(dir);
        if (episode is not null) folders.Add(Path.Combine(await _locations.DownloadFolderAsync(cancellationToken), episode.PodcastId.ToString("N")));
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(folder, $"{download.EpisodeId:N}*"))
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Could not delete {Path}", path); }
            }
        }
    }

    // At startup: downloads that were running when the server stopped go back in the queue and
    // carry on from their part files.
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        foreach (var download in await _store.GetDownloadsAsync(cancellationToken))
        {
            if (download.State != PodcastDownloadStates.Downloading || IsActive(download.EpisodeId)) continue;
            download.State = PodcastDownloadStates.Queued;
            await _store.SaveDownloadAsync(download, cancellationToken);
        }
        Kick();
    }

    public void Kick() => _ = PumpAsync();

    private async Task PumpAsync()
    {
        await _pump.WaitAsync();
        try
        {
            while (_active.Count < Parallel)
            {
                var next = (await _store.GetDownloadsAsync(CancellationToken.None))
                    .Where(d => d.State == PodcastDownloadStates.Queued && !_active.ContainsKey(d.EpisodeId))
                    .OrderBy(d => d.RequestedAt)
                    .FirstOrDefault();
                if (next is null) break;
                var active = new ActiveDownload(new CancellationTokenSource());
                if (!_active.TryAdd(next.EpisodeId, active)) break;
                _ = Task.Run(() => RunAsync(next.EpisodeId, active));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start podcast downloads");
        }
        finally { _pump.Release(); }
    }

    private async Task RunAsync(Guid episodeId, ActiveDownload active)
    {
        try
        {
            await DownloadAsync(episodeId, active, active.Cancel.Token);
        }
        catch (OperationCanceledException) when (active.Cancel.IsCancellationRequested)
        {
            // Deleted while downloading; DeleteAsync tidied up.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downloading podcast episode {Episode} failed", episodeId);
            try
            {
                if (await _store.GetDownloadAsync(episodeId, CancellationToken.None) is { } download && download.State != PodcastDownloadStates.None)
                {
                    download.State = PodcastDownloadStates.Failed;
                    download.Error = Friendly(ex);
                    await _store.SaveDownloadAsync(download, CancellationToken.None);
                }
            }
            catch (Exception inner) { _logger.LogWarning(inner, "Could not record a failed podcast download"); }
        }
        finally
        {
            if (_active.TryGetValue(episodeId, out var current) && ReferenceEquals(current, active)) _active.TryRemove(episodeId, out _);
            active.Cancel.Dispose();
            Kick();
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"The publisher's server answered {(int)status} for the episode's file.",
        HttpRequestException => "The publisher's server could not be reached. It will be tried again when you ask.",
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase) => "The download folder's disk is full.",
        UnauthorizedAccessException => "The server is not allowed to write to the download folder.",
        TimeoutException or TaskCanceledException => "The publisher's server stopped sending the file.",
        _ => ex.Message
    };

    private async Task DownloadAsync(Guid episodeId, ActiveDownload active, CancellationToken cancellationToken)
    {
        var episode = await _store.GetEpisodeAsync(episodeId, cancellationToken);
        if (episode is null)
        {
            await _store.SaveDownloadAsync(new PodcastDownload { EpisodeId = episodeId }, cancellationToken);
            return;
        }
        var show = await _store.GetShowAsync(episode.PodcastId, cancellationToken);
        var options = await PodcastLocations.ReadOptionsAsync(_settings, cancellationToken);
        var folder = Path.Combine(_locations.DownloadFolderFor(options), episode.PodcastId.ToString("N"));
        Directory.CreateDirectory(folder);

        var download = await _store.GetDownloadAsync(episodeId, cancellationToken) ?? new PodcastDownload { EpisodeId = episodeId };
        download.State = PodcastDownloadStates.Downloading;
        download.Error = null;
        await _store.SaveDownloadAsync(download, cancellationToken);

        var target = Path.Combine(folder, $"{episodeId:N}{ExtensionFor(episode)}");
        var part = target + ".part";
        await FetchAsync(episode.EnclosureUrl, part, active, cancellationToken);
        File.Move(part, target, overwrite: true);

        var probe = await _audio.ProbeAsync(target, cancellationToken);
        // The file's own chapters are kept (in its original timing) unless the feed has its own.
        if (episode.ChaptersUrl is null && probe.Chapters.Count > 0)
            await _store.SaveChaptersAsync(episodeId, "file", probe.Chapters, cancellationToken);

        var finalPath = target;
        var mime = episode.MimeType is { Length: > 0 } feedMime && feedMime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? feedMime
            : PodcastFeedParser.GuessMimeType(target);
        var duration = probe.DurationSeconds ?? episode.DurationSeconds;
        var cuts = new List<PodcastCut>();
        var enhanced = false;
        var settings = show?.Settings ?? new PodcastShowSettings();
        if ((settings.VoiceBoost || settings.TrimSilence) && await _audio.FfmpegAvailableAsync(cancellationToken))
        {
            if (settings.TrimSilence)
                cuts = PodcastAudioProcessor.BuildCuts(await _audio.DetectSilencesAsync(target, probe.DurationSeconds, cancellationToken));
            if (settings.VoiceBoost || cuts.Count > 0)
            {
                var processed = Path.Combine(folder, $"{episodeId:N}.enhanced.m4a");
                var temporary = processed + ".tmp";
                if (await _audio.ProcessAsync(target, temporary, cuts, settings.VoiceBoost, cancellationToken))
                {
                    File.Move(temporary, processed, overwrite: true);
                    File.Delete(target);
                    finalPath = processed;
                    mime = "audio/mp4";
                    enhanced = true;
                    var after = await _audio.ProbeAsync(processed, cancellationToken);
                    duration = after.DurationSeconds ?? (duration is { } d ? d - cuts.Sum(c => c.Length) : null);
                }
                else
                {
                    try { File.Delete(temporary); } catch (IOException) { }
                    cuts = [];
                }
            }
        }

        // Re-read: the transcriber may have asked to be told about this file meanwhile.
        download = await _store.GetDownloadAsync(episodeId, cancellationToken) ?? download;
        if (download.State == PodcastDownloadStates.None)
        {
            // Deleted while it was being processed.
            File.Delete(finalPath);
            return;
        }
        download.State = PodcastDownloadStates.Done;
        download.Error = null;
        download.FilePath = finalPath;
        download.FileBytes = new FileInfo(finalPath).Length;
        download.FileDurationSeconds = duration;
        download.FileMimeType = mime;
        download.Enhanced = enhanced;
        download.Cuts = cuts;
        download.DownloadedAt = DateTimeOffset.UtcNow;
        await _store.SaveDownloadAsync(download, cancellationToken);
        episode.Download = download;
        _logger.LogInformation("Downloaded {Show}: {Title}{Saved}", show?.Title, episode.Title,
            cuts.Count > 0 ? $" ({cuts.Sum(c => c.Length):0} s of silence cut)" : "");

        try { Completed?.Invoke(episode); }
        catch (Exception ex) { _logger.LogWarning(ex, "A podcast download listener failed"); }

        await EnforceLimitsAsync(episodeId, cancellationToken);
    }

    private async Task FetchAsync(string url, string part, ActiveDownload active, CancellationToken cancellationToken)
    {
        var client = _clients.CreateClient(HttpClientName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                // The part file is already whole, or the file changed under it. Start again.
                File.Delete(part);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is { } type && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The episode's address returned a web page, not audio.");

            var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            var total = append
                ? response.Content.Headers.ContentRange?.Length ?? existing + (response.Content.Headers.ContentLength ?? 0)
                : response.Content.Headers.ContentLength ?? 0;
            var written = append ? existing : 0;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                if (total > 0) active.Progress = Math.Clamp((double)written / total, 0, 1);
            }
            active.Progress = 1;
            if (written == 0) throw new InvalidOperationException("The publisher's server sent an empty file.");
            return;
        }
        throw new InvalidOperationException("The publisher's server would not send the file from the start.");
    }

    private static string ExtensionFor(PodcastEpisode episode)
    {
        var path = Uri.TryCreate(episode.EnclosureUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "";
        var extension = Path.GetExtension(path);
        if (AudioExtensions.Contains(extension)) return extension.ToLowerInvariant();
        return (episode.MimeType ?? "").ToLowerInvariant() switch
        {
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" or "audio/aac" or "audio/x-aac" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/flac" or "audio/x-flac" => ".flac",
            _ => ".mp3"
        };
    }

    // ---- limits -----------------------------------------------------------------------------

    // Keep-newest-N per show, then the storage cap: played episodes go first, oldest first, then
    // the oldest of the rest. Nothing anyone has in Up Next is cleared for keep-N, and the episode
    // that has just finished downloading is never the one cleared to make room for itself.
    public async Task EnforceLimitsAsync(Guid? justDownloaded, CancellationToken cancellationToken)
    {
        await _limits.WaitAsync(cancellationToken);
        try
        {
            var options = await PodcastLocations.ReadOptionsAsync(_settings, cancellationToken);
            var done = (await _store.GetDownloadsAsync(cancellationToken)).Where(d => d.IsDone).ToList();
            if (done.Count == 0) return;
            var episodes = (await _store.GetEpisodesAsync(done.Select(d => d.EpisodeId), cancellationToken)).ToDictionary(e => e.Id);
            var shows = (await _store.GetShowsAsync(cancellationToken)).ToDictionary(s => s.Id);
            var queued = (await _store.AllQueuesAsync(cancellationToken)).Select(q => q.EpisodeId).ToHashSet();
            var removed = new HashSet<Guid>();

            foreach (var group in done.Where(d => episodes.ContainsKey(d.EpisodeId)).GroupBy(d => episodes[d.EpisodeId].PodcastId))
            {
                var keep = shows.TryGetValue(group.Key, out var show) ? show.Settings.KeepEpisodes : options.KeepEpisodes;
                if (keep <= 0) continue;
                var extra = group.OrderByDescending(d => Published(episodes[d.EpisodeId])).Skip(keep)
                    .Where(d => !queued.Contains(d.EpisodeId) && d.EpisodeId != justDownloaded);
                foreach (var download in extra)
                {
                    await DeleteAsync(download.EpisodeId, cancellationToken);
                    removed.Add(download.EpisodeId);
                }
            }

            var max = options.MaxStorageBytes();
            if (max <= 0) return;
            var remaining = done.Where(d => !removed.Contains(d.EpisodeId)).ToList();
            var total = remaining.Sum(d => d.FileBytes ?? 0);
            if (total <= max) return;

            var progress = (await _store.GetProgressForEpisodesAsync(remaining.Select(d => d.EpisodeId), cancellationToken))
                .GroupBy(p => p.EpisodeId).ToDictionary(g => g.Key, g => g.ToList());
            var candidates = remaining.Select(d => new StorageCandidate(
                d.EpisodeId, d.FileBytes ?? 0,
                episodes.TryGetValue(d.EpisodeId, out var e) ? Published(e) : DateTimeOffset.MinValue,
                IsPlayed(progress.GetValueOrDefault(d.EpisodeId)), queued.Contains(d.EpisodeId))).ToList();
            foreach (var episodeId in ChooseEvictions(candidates, max, justDownloaded))
            {
                await DeleteAsync(episodeId, cancellationToken);
                _logger.LogInformation("Podcast storage over its limit: removed the download of episode {Episode}", episodeId);
            }
        }
        finally { _limits.Release(); }
    }

    public sealed record StorageCandidate(Guid EpisodeId, long Bytes, DateTimeOffset PublishedAt, bool Played, bool Queued);

    // Which downloads go to bring the total under `max`: played ones first, then ones nobody has
    // queued, oldest first within each - never `keep`.
    public static IReadOnlyList<Guid> ChooseEvictions(IReadOnlyList<StorageCandidate> downloads, long max, Guid? keep)
    {
        var total = downloads.Sum(d => d.Bytes);
        var chosen = new List<Guid>();
        if (max <= 0 || total <= max) return chosen;
        var order = downloads
            .Where(d => d.EpisodeId != keep)
            .OrderBy(d => d.Played ? 0 : 1)
            .ThenBy(d => d.Queued ? 1 : 0)
            .ThenBy(d => d.PublishedAt);
        foreach (var download in order)
        {
            if (total <= max) break;
            chosen.Add(download.EpisodeId);
            total -= download.Bytes;
        }
        return chosen;
    }

    // Hourly: downloads everyone has finished go after DeletePlayedAfterDays, and so do downloads
    // of shows nobody follows any more.
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var options = await PodcastLocations.ReadOptionsAsync(_settings, cancellationToken);
        if (options.DeletePlayedAfterDays <= 0) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.DeletePlayedAfterDays);
        var done = (await _store.GetDownloadsAsync(cancellationToken)).Where(d => d.IsDone).ToList();
        if (done.Count == 0) return;
        var episodes = (await _store.GetEpisodesAsync(done.Select(d => d.EpisodeId), cancellationToken)).ToDictionary(e => e.Id);
        var followed = (await _store.GetSubscriptionsAsync(cancellationToken)).Where(s => s.Subscribed).Select(s => s.PodcastId).ToHashSet();
        var queued = (await _store.AllQueuesAsync(cancellationToken)).Select(q => q.EpisodeId).ToHashSet();
        var progress = (await _store.GetProgressForEpisodesAsync(done.Select(d => d.EpisodeId), cancellationToken))
            .GroupBy(p => p.EpisodeId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var download in done)
        {
            if (queued.Contains(download.EpisodeId)) continue;
            var rows = progress.GetValueOrDefault(download.EpisodeId);
            var playedLongAgo = IsPlayed(rows) && rows!.Where(p => p.Played).Max(p => p.PlayedAt ?? p.UpdatedAt) < cutoff;
            var orphaned = episodes.TryGetValue(download.EpisodeId, out var episode) && !followed.Contains(episode.PodcastId)
                && (download.DownloadedAt ?? DateTimeOffset.MinValue) < cutoff;
            if (playedLongAgo || orphaned || episode is null) await DeleteAsync(download.EpisodeId, cancellationToken);
        }
    }

    // Finished by someone, and nobody is part-way through it.
    private static bool IsPlayed(List<PodcastProgress>? rows) => rows is { Count: > 0 } && rows.Any(p => p.Played) && rows.All(p => p.Played);

    private static DateTimeOffset Published(PodcastEpisode episode) => episode.PublishedAt ?? episode.FirstSeenAt;
}
