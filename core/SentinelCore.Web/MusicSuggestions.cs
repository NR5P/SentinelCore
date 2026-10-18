using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// "Suggested for you" on the Music tab (MUSIC_AND_PODCASTS.md §4 "Suggestions"): albums by artists
// like the ones someone loves, plays, puts on playlists or imported from Spotify, that are not in
// the library yet - each one a tap away from being downloaded through an imported list.
//
// Who is like whom comes from ListenBrainz (free, open data, no key or account), keyed by
// MusicBrainz artist ids; an artist's best-known albums come from ListenBrainz popularity. Every
// answer is cached in media-downloads.db for a month, so after the first build a refresh is quick
// and a server without internet still shows the last list it made.

public sealed record MusicSimilarArtist(string Mbid, string Name, double Score);

public sealed record MusicPopularAlbum(string Mbid, string Title, int? Year, long Listens, string? Type);

public interface IMusicSimilarity
{
    // Null when MusicBrainz knows nobody by that name.
    Task<string?> ArtistMbidAsync(string name, CancellationToken cancellationToken);
    // Most similar first.
    Task<IReadOnlyList<MusicSimilarArtist>> SimilarAsync(string artistMbid, CancellationToken cancellationToken);
    // Most listened first.
    Task<IReadOnlyList<MusicPopularAlbum>> TopAlbumsAsync(string artistMbid, CancellationToken cancellationToken);
}

public sealed record MusicSuggestionSeed(string Artist, double Weight, string Reason);

public sealed record MusicSuggestedAlbum(
    string Key, string Artist, string ArtistMbid, string Title, int? Year, string Mbid, long Listens,
    IReadOnlyList<string> Because, double Score);

// Whose music someone likes, and how much, from what the server already knows about them.
public static class MusicSuggestionSeeds
{
    public const int MaxSeeds = 12;

    public static List<MusicSuggestionSeed> Build(MusicView view, IEnumerable<MusicPlaylist> ownPlaylists, IEnumerable<string> importedArtists)
    {
        var catalog = view.Catalog;
        var seeds = new Dictionary<string, (string Name, double Weight, Dictionary<string, double> Reasons)>(StringComparer.Ordinal);
        void Add(string? name, double weight, string reason)
        {
            var key = MusicText.Key(name);
            if (key.Length == 0 || key == MusicText.Key(MusicText.VariousArtists) || weight <= 0) return;
            if (!seeds.TryGetValue(key, out var seed)) seed = (name!.Trim(), 0, new Dictionary<string, double>());
            seed.Reasons[reason] = seed.Reasons.GetValueOrDefault(reason) + weight;
            seeds[key] = (seed.Name, seed.Weight + weight, seed.Reasons);
        }

        foreach (var (kind, id) in view.User.Loved)
            Add(ArtistOf(catalog, kind, id), kind switch { MusicLoveKinds.Artist => 6, MusicLoveKinds.Album => 4, _ => 3 }, "loved");
        foreach (var ((kind, id), rating) in view.User.Ratings)
            if (rating >= 4) Add(ArtistOf(catalog, kind, id), 1.5 * (rating - 3), "rated");

        var plays = new Dictionary<string, (string Name, int Count)>(StringComparer.Ordinal);
        foreach (var (trackId, stats) in view.User.Stats)
        {
            if (stats.Plays == 0 || !catalog.TracksById.TryGetValue(trackId, out var track)) continue;
            var key = MusicText.Key(track.PrimaryArtist);
            plays[key] = (track.PrimaryArtist, plays.GetValueOrDefault(key).Count + stats.Plays);
        }
        foreach (var (name, count) in plays.Values) Add(name, Math.Min(8, 2 * Math.Log2(1 + count)), "played");

        var listed = new Dictionary<string, (string Name, int Count)>(StringComparer.Ordinal);
        foreach (var playlist in ownPlaylists.Where(p => !p.Smart))
            foreach (var entry in playlist.Entries)
                if (catalog.TracksById.TryGetValue(entry.TrackId, out var track))
                {
                    var key = MusicText.Key(track.PrimaryArtist);
                    listed[key] = (track.PrimaryArtist, listed.GetValueOrDefault(key).Count + 1);
                }
        foreach (var (name, count) in listed.Values) Add(name, Math.Min(5, count), "playlists");

        foreach (var group in importedArtists.Where(a => !string.IsNullOrWhiteSpace(a)).GroupBy(MusicText.Key))
            Add(group.First(), Math.Min(4, group.Count()), "imported");

        // Nothing to go on yet: whoever fills the most of the library.
        if (seeds.Count == 0)
            foreach (var artist in catalog.Artists.Where(a => a.IsAlbumArtist).OrderByDescending(a => a.Tracks.Count).Take(8))
                Add(artist.Name, 1, "library");

        return seeds.Values
            .OrderByDescending(s => s.Weight).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSeeds)
            .Select(s => new MusicSuggestionSeed(s.Name, Math.Round(s.Weight, 2), s.Reasons.MaxBy(r => r.Value).Key))
            .ToList();
    }

    private static string? ArtistOf(MusicCatalog catalog, string kind, Guid id) => kind switch
    {
        MusicLoveKinds.Artist => catalog.ArtistsById.GetValueOrDefault(id)?.Name,
        MusicLoveKinds.Album => catalog.AlbumsById.GetValueOrDefault(id) is { Compilation: false } album ? album.Artist : null,
        _ => catalog.TracksById.GetValueOrDefault(id)?.PrimaryArtist
    };
}

// Seeds -> similar artists not in the library -> their best-known studio albums.
public static class MusicSuggestionRanker
{
    public const int SimilarPerSeed = 40;
    public const int MaxArtists = 24;
    public const int AlbumsPerArtist = 2;
    public const int MaxAlbums = 40;

    // Live records, hits collections and the like are rarely what someone means by "an album by".
    private static readonly Regex NotAStudioAlbum = new(
        @"\b(live|greatest hits|best of|the very best|hits|anthology|collection|essentials?|gold|bbc|sessions|demos|unplugged|in concert|remixes|karaoke|tribute)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string AlbumKey(string artist, string title) => $"{MusicText.Key(artist)}|{MusicText.Key(title)}";

    public static string ArtistDismissal(string artist) => "artist:" + MusicText.Key(artist);

    public static string AlbumDismissal(string artist, string title) => "album:" + AlbumKey(artist, title);

    // libraryArtists: MusicText keys of everyone with music in the library already.
    // hasAlbum: whether the library has that album (artist, title).
    public static async Task<List<MusicSuggestedAlbum>> RankAsync(
        IReadOnlyList<MusicSuggestionSeed> seeds, IMusicSimilarity similarity, IReadOnlySet<string> libraryArtists,
        Func<string, string, Task<bool>> hasAlbum, IReadOnlySet<string> dismissed, CancellationToken cancellationToken)
    {
        var seedKeys = seeds.Select(s => MusicText.Key(s.Artist)).ToHashSet(StringComparer.Ordinal);
        var candidates = new Dictionary<string, (string Mbid, string Name, double Score, Dictionary<string, double> Because)>(StringComparer.Ordinal);
        var reached = 0;
        Exception? lastError = null;
        foreach (var seed in seeds)
        {
            IReadOnlyList<MusicSimilarArtist> similar;
            try
            {
                if (await similarity.ArtistMbidAsync(seed.Artist, cancellationToken) is not { } mbid) { reached++; continue; }
                similar = await similarity.SimilarAsync(mbid, cancellationToken);
                reached++;
            }
            catch (MediaDownloadException ex) { lastError = ex; continue; }

            var top = similar.Take(SimilarPerSeed).ToList();
            var best = top.Count == 0 ? 0 : top.Max(s => s.Score);
            if (best <= 0) continue;
            foreach (var artist in top)
            {
                var key = MusicText.Key(artist.Name);
                if (key.Length == 0 || seedKeys.Contains(key) || libraryArtists.Contains(key) || dismissed.Contains("artist:" + key)) continue;
                var contribution = seed.Weight * artist.Score / best;
                var candidate = candidates.TryGetValue(key, out var c) ? c : (artist.Mbid, artist.Name, 0, new Dictionary<string, double>());
                candidate.Because[seed.Artist] = candidate.Because.GetValueOrDefault(seed.Artist) + contribution;
                candidates[key] = (candidate.Mbid, candidate.Name, candidate.Score + contribution, candidate.Because);
            }
        }
        if (reached == 0 && lastError is not null) throw lastError;

        // Summed scores alone hand every slot to the household names that are "similar" to
        // everyone (Queen, The Beatles), so each seed gets its own share of the artists, in
        // proportion to its weight and at least two, filled with its own closest matches first.
        var chosenArtists = new List<string>();
        var bySeed = seeds.Select(seed => candidates
                .Where(c => c.Value.Because.ContainsKey(seed.Artist))
                .OrderByDescending(c => c.Value.Because[seed.Artist]).Select(c => c.Key).ToList())
            .ToList();
        var totalWeight = seeds.Sum(s => s.Weight);
        var quota = seeds.Select(s => Math.Max(2, (int)Math.Round(MaxArtists * s.Weight / Math.Max(totalWeight, 0.001)))).ToList();
        var taken = new int[seeds.Count];
        var cursor = new int[seeds.Count];
        for (var progress = true; progress && chosenArtists.Count < MaxArtists;)
        {
            progress = false;
            for (var s = 0; s < seeds.Count && chosenArtists.Count < MaxArtists; s++)
            {
                if (taken[s] >= quota[s]) continue;
                while (cursor[s] < bySeed[s].Count && chosenArtists.Contains(bySeed[s][cursor[s]])) cursor[s]++;
                if (cursor[s] >= bySeed[s].Count) continue;
                chosenArtists.Add(bySeed[s][cursor[s]++]);
                taken[s]++;
                progress = true;
            }
        }
        // Quotas can leave room when a seed has few matches: fill it with the best of the rest.
        chosenArtists.AddRange(candidates.OrderByDescending(c => c.Value.Score).Select(c => c.Key)
            .Where(k => !chosenArtists.Contains(k)).Take(MaxArtists - chosenArtists.Count));

        var picks = new List<List<MusicSuggestedAlbum>>();
        foreach (var artist in chosenArtists.Select(k => candidates[k]))
        {
            IReadOnlyList<MusicPopularAlbum> albums;
            try { albums = await similarity.TopAlbumsAsync(artist.Mbid, cancellationToken); }
            catch (MediaDownloadException) { continue; }
            var because = artist.Because.OrderByDescending(b => b.Value).Take(3).Select(b => b.Key).ToList();
            var chosen = new List<MusicSuggestedAlbum>();
            foreach (var album in albums)
            {
                if (chosen.Count >= AlbumsPerArtist) break;
                if (album.Type is { } type && !type.Equals("Album", StringComparison.OrdinalIgnoreCase)) continue;
                if (NotAStudioAlbum.IsMatch(album.Title)) continue;
                if (dismissed.Contains(AlbumDismissal(artist.Name, album.Title))) continue;
                if (chosen.Any(a => MusicText.Key(a.Title) == MusicText.Key(album.Title))) continue;
                if (await hasAlbum(artist.Name, album.Title)) continue;
                chosen.Add(new MusicSuggestedAlbum(AlbumKey(artist.Name, album.Title), artist.Name, artist.Mbid, album.Title,
                    album.Year, album.Mbid, album.Listens, because, Math.Round(artist.Score, 3)));
            }
            if (chosen.Count > 0) picks.Add(chosen);
        }

        // Every artist's best album first, then the second ones, so the top of the list is varied.
        var result = new List<MusicSuggestedAlbum>();
        for (var round = 0; round < AlbumsPerArtist && result.Count < MaxAlbums; round++)
            foreach (var artist in picks)
                if (round < artist.Count && result.Count < MaxAlbums) result.Add(artist[round]);
        return result;
    }
}

// ListenBrainz + MusicBrainz, each answer cached for a month in media-downloads.db.
public sealed class ListenBrainzMusicSimilarity(
    IHttpClientFactory httpClientFactory, MediaDownloadStore store, TimeProvider clock, ILogger<ListenBrainzMusicSimilarity> logger) : IMusicSimilarity
{
    public const string HttpClientName = "MusicSuggestions";
    public static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
    // ListenBrainz Labs' listening-session model over the last ~20 years of listens.
    private const string SimilarAlgorithm = "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // MusicBrainz asks for one request a second.
    private readonly SemaphoreSlim _musicBrainzGate = new(1, 1);
    private DateTimeOffset _lastMusicBrainz = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _listenBrainzGate = new(1, 1);
    private DateTimeOffset _lastListenBrainz = DateTimeOffset.MinValue;

    // 25 requests per 10 s, under ListenBrainz' 30 for clients without a token.
    private async Task PaceListenBrainzAsync(CancellationToken cancellationToken)
    {
        await _listenBrainzGate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastListenBrainz + TimeSpan.FromSeconds(0.4) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastListenBrainz = DateTimeOffset.UtcNow;
        }
        finally { _listenBrainzGate.Release(); }
    }

    public async Task<string?> ArtistMbidAsync(string name, CancellationToken cancellationToken)
    {
        var key = MusicText.Key(name);
        var cached = await CachedAsync<string>("mbid:" + key, cancellationToken);
        if (cached is not null) return cached.Length == 0 ? null : cached;

        var escaped = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var url = $"https://musicbrainz.org/ws/2/artist/?fmt=json&limit=5&query={Uri.EscapeDataString($"artist:\"{escaped}\"")}";
        await _musicBrainzGate.WaitAsync(cancellationToken);
        JsonDocument document;
        try
        {
            var wait = _lastMusicBrainz + TimeSpan.FromSeconds(1.1) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastMusicBrainz = DateTimeOffset.UtcNow;
            document = await GetAsync(url, "MusicBrainz", cancellationToken);
        }
        finally { _musicBrainzGate.Release(); }

        string? found = null;
        using (document)
        {
            if (document.RootElement.TryGetProperty("artists", out var artists))
            {
                var list = artists.EnumerateArray().Select(a => (
                    Id: a.TryGetProperty("id", out var i) ? i.GetString() : null,
                    Name: a.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Score: a.TryGetProperty("score", out var s) && s.TryGetInt32(out var v) ? v : 0)).ToList();
                found = list.FirstOrDefault(a => a.Score >= 90 && MusicText.Key(a.Name) == key).Id
                    ?? list.FirstOrDefault(a => a.Score == 100).Id;
            }
        }
        await StoreAsync("mbid:" + key, found ?? "", cancellationToken);
        return found;
    }

    public async Task<IReadOnlyList<MusicSimilarArtist>> SimilarAsync(string artistMbid, CancellationToken cancellationToken)
    {
        if (await CachedAsync<List<MusicSimilarArtist>>("similar:" + artistMbid, cancellationToken) is { } cached) return cached;
        var url = $"https://labs.api.listenbrainz.org/similar-artists/json?artist_mbids={Uri.EscapeDataString(artistMbid)}&algorithm={SimilarAlgorithm}";
        using var document = await GetAsync(url, "ListenBrainz", cancellationToken);
        var list = new List<MusicSimilarArtist>();
        foreach (var row in Rows(document.RootElement))
        {
            var mbid = row.TryGetProperty("artist_mbid", out var m) ? m.GetString() : null;
            var name = row.TryGetProperty("name", out var n) ? n.GetString() : null;
            var score = row.TryGetProperty("score", out var s) && s.TryGetDouble(out var v) ? v : 0;
            if (mbid is { Length: > 0 } && name is { Length: > 0 } && mbid != artistMbid) list.Add(new MusicSimilarArtist(mbid, name, score));
        }
        list = list.OrderByDescending(a => a.Score).ToList();
        await StoreAsync("similar:" + artistMbid, list, cancellationToken);
        return list;
    }

    public async Task<IReadOnlyList<MusicPopularAlbum>> TopAlbumsAsync(string artistMbid, CancellationToken cancellationToken)
    {
        if (await CachedAsync<List<MusicPopularAlbum>>("albums:" + artistMbid, cancellationToken) is { } cached) return cached;
        var url = $"https://api.listenbrainz.org/1/popularity/top-release-groups-for-artist/{Uri.EscapeDataString(artistMbid)}";
        using var document = await GetAsync(url, "ListenBrainz", cancellationToken);
        var list = new List<MusicPopularAlbum>();
        foreach (var row in Rows(document.RootElement))
        {
            var mbid = row.TryGetProperty("release_group_mbid", out var m) ? m.GetString() : null;
            if (mbid is not { Length: > 0 } || !row.TryGetProperty("release_group", out var group)) continue;
            var title = group.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (title is not { Length: > 0 }) continue;
            int? year = group.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: >= 4 } date
                && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
            var type = group.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var listens = row.TryGetProperty("total_listen_count", out var l) && l.TryGetInt64(out var c) ? c : 0;
            list.Add(new MusicPopularAlbum(mbid, title, year, listens, type));
        }
        list = list.OrderByDescending(a => a.Listens).Take(25).ToList();
        await StoreAsync("albums:" + artistMbid, list, cancellationToken);
        return list;
    }

    // Both APIs answer with a bare array; older Labs answers wrapped it in [{ "data": [...] }].
    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (row.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var inner in data.EnumerateArray()) yield return inner;
            else yield return row;
        }
    }

    private async Task<JsonDocument> GetAsync(string url, string service, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (service == "ListenBrainz") await PaceListenBrainzAsync(cancellationToken);
                using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
                // Over the limit (30 requests in 10 s without a token) ListenBrainz answers 429, or
                // sometimes 401; it says how long to wait.
                if ((int)response.StatusCode is 429 or 401 && attempt == 0)
                {
                    var wait = response.Headers.TryGetValues("X-RateLimit-Reset-In", out var values)
                        && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : 10;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 30)), cancellationToken);
                    continue;
                }
                // An artist ListenBrainz has no data for answers 404 or 400; that's "nothing", not an outage.
                if ((int)response.StatusCode is 400 or 404) return JsonDocument.Parse("[]");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken) ?? JsonDocument.Parse("[]");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("{Service} request {Url} failed: {Message}", service, url, ex.Message);
            throw new MediaDownloadException($"Couldn't reach {service} to work out suggestions. Try again in a while.");
        }
    }

    private async Task<T?> CachedAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        if (await store.GetSuggestionCacheAsync(key, cancellationToken) is not { } row || clock.GetUtcNow() - row.FetchedAt > CacheFor) return null;
        try { return JsonSerializer.Deserialize<T>(row.Data, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private Task StoreAsync<T>(string key, T value, CancellationToken cancellationToken) =>
        store.SetSuggestionCacheAsync(key, clock.GetUtcNow(), JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
}

// Builds each person's suggestions in the background (the first build asks the internet about a
// dozen artists and takes half a minute), keeps the last one, and works out "already in the
// library" and "already downloading" fresh on every read.
public sealed class MusicSuggestionService(
    MusicService music, IMusicAudioSource audio, MediaImportService imports, MediaDownloadStore store, IMusicSimilarity similarity,
    TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<MusicSuggestionService> logger)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private sealed class Entry
    {
        public Task? Building;
        public DateTimeOffset? BuiltAt;
        public string? Error;
        public DateTimeOffset? ErrorAt;
        public List<MusicSuggestionSeed> Seeds = [];
        public List<MusicSuggestedAlbum> Albums = [];
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<object> GetAsync(string userKey, string userName, bool isAdmin, bool refresh, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(userKey, _ => new Entry());
        lock (entry)
        {
            var stale = entry.BuiltAt is not { } built || clock.GetUtcNow() - built > StaleAfter;
            // After a failure (no internet, ListenBrainz down) a plain read tries again every few minutes.
            var retry = entry.Error is null || clock.GetUtcNow() - entry.ErrorAt > TimeSpan.FromMinutes(5);
            if ((refresh || stale && retry) && entry.Building is not { IsCompleted: false })
            {
                entry.Error = null;
                entry.Building = Task.Run(() => BuildAsync(entry, userKey, userName, isAdmin, lifetime.ApplicationStopping));
            }
        }
        // A rebuild from the cache is quick: give it a moment so the answer is usually final.
        if (entry.Building is { IsCompleted: false } running)
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
        return await SnapshotAsync(entry, userName, cancellationToken);
    }

    // Not interested in this artist (title null) or this one album.
    public async Task DismissAsync(string userKey, string artist, string? title, bool dismissed, CancellationToken cancellationToken)
    {
        var key = title is null ? MusicSuggestionRanker.ArtistDismissal(artist) : MusicSuggestionRanker.AlbumDismissal(artist, title);
        await store.SetSuggestionDismissedAsync(userKey, key, dismissed, clock.GetUtcNow(), cancellationToken);
        if (dismissed && _entries.TryGetValue(userKey, out var entry))
            lock (entry)
                entry.Albums = entry.Albums.Where(a => title is null
                    ? MusicText.Key(a.Artist) != MusicText.Key(artist)
                    : a.Key != MusicSuggestionRanker.AlbumKey(artist, title)).ToList();
    }

    private async Task BuildAsync(Entry entry, string userKey, string userName, bool isAdmin, CancellationToken cancellationToken)
    {
        try
        {
            var view = await music.ViewAsync(userKey, isAdmin, cancellationToken);
            var playlists = (await music.VisiblePlaylistsAsync(userKey, cancellationToken)).Where(p => p.OwnerKey == userKey);
            var imported = (await imports.ListAsync(MediaImportList.KindMusic, cancellationToken))
                .Where(j => string.Equals(j.RequestedBy, userName, StringComparison.Ordinal))
                .SelectMany(j => j.Items).Select(i => i.Artist ?? "");
            var seeds = MusicSuggestionSeeds.Build(view, playlists, imported);
            var libraryArtists = view.Catalog.Artists.Select(a => MusicText.Key(a.Name))
                .Concat(view.Catalog.Tracks.SelectMany(t => t.Artists).Select(MusicText.Key))
                .ToHashSet(StringComparer.Ordinal);
            var dismissed = await store.GetSuggestionDismissalsAsync(userKey, cancellationToken);
            var albums = await MusicSuggestionRanker.RankAsync(seeds, similarity, libraryArtists,
                (artist, title) => audio.HasAlbumAsync(artist, title, cancellationToken), dismissed, cancellationToken);
            lock (entry)
            {
                entry.Seeds = seeds;
                entry.Albums = albums;
                entry.BuiltAt = clock.GetUtcNow();
                entry.Error = null;
            }
        }
        catch (MediaDownloadException ex)
        {
            lock (entry) (entry.Error, entry.ErrorAt) = (ex.Message, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't work out music suggestions for {User}", userKey);
            lock (entry) (entry.Error, entry.ErrorAt) = ("Couldn't work out suggestions: " + ex.Message, clock.GetUtcNow());
        }
    }

    private async Task<object> SnapshotAsync(Entry entry, string userName, CancellationToken cancellationToken)
    {
        List<MusicSuggestedAlbum> albums;
        List<MusicSuggestionSeed> seeds;
        string? error;
        DateTimeOffset? builtAt;
        bool working;
        lock (entry)
        {
            albums = entry.Albums;
            seeds = entry.Seeds;
            error = entry.Error;
            builtAt = entry.BuiltAt;
            working = entry.Building is { IsCompleted: false };
        }

        // On its way already: on one of this person's imported lists and not given up on, or a
        // download that is still going.
        var onTheWay = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in await imports.ListAsync(MediaImportList.KindMusic, cancellationToken))
            if (string.Equals(job.RequestedBy, userName, StringComparison.Ordinal))
                foreach (var item in job.Items.Where(i => i.State is not (MediaImportStates.Canceled or MediaImportStates.NotFound or MediaImportStates.Failed)))
                    onTheWay.Add(MusicSuggestionRanker.AlbumKey(item.Artist ?? "", item.Title));
        foreach (var download in (await imports.DownloadsAsync(cancellationToken)).Values)
            if (download.Kind == MediaDownloadKinds.Album && MediaDownloadStates.IsActive(download.State))
                onTheWay.Add(MusicSuggestionRanker.AlbumKey(download.Artist ?? "", download.Title));

        var shown = new List<object>();
        foreach (var album in albums)
        {
            if (await audio.HasAlbumAsync(album.Artist, album.Title, cancellationToken)) continue;
            shown.Add(new
            {
                key = album.Key,
                artist = album.Artist,
                title = album.Title,
                year = album.Year,
                mbid = album.Mbid,
                artUrl = MusicDownloadLookup.ArtUrl(album.Mbid),
                because = album.Because,
                listens = album.Listens,
                queued = onTheWay.Contains(album.Key)
            });
        }
        return new
        {
            state = working ? "working" : error is not null ? "error" : "ready",
            builtAt,
            message = error,
            seeds = seeds.Select(s => new { artist = s.Artist, reason = s.Reason }),
            albums = shown
        };
    }
}

public sealed class MusicSuggestionDismissInput
{
    public string Artist { get; set; } = string.Empty;
    // Null for the whole artist.
    public string? Title { get; set; }
    // False takes a "not interested" back.
    public bool Dismissed { get; set; } = true;
}
