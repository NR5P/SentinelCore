using System.Globalization;

namespace SentinelCore.Web;

// The Music tab's working parts in one place, for MusicEndpoints and for the shared audio code
// (IMusicAudioSource: play where you left off, speakers, wake-up alarms, stats, backups), which
// only ever reaches the Music tab through this.
public sealed class MusicService(
    IMusicStore store,
    MusicCatalogCache catalogCache,
    MusicScanner scanner,
    AppSettingsFile settings,
    IListenBrainzSubmitter listenBrainz,
    ILogger<MusicService> logger) : IMusicAudioSource
{
    public IMusicStore Store => store;
    public MusicScanner Scanner => scanner;

    public async Task<MusicSettingsDto> OptionsAsync(CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<MusicSettingsDto>(MusicSettingsDto.SectionName, cancellationToken) ?? new MusicSettingsDto();

    public async Task<bool> EnabledAsync(CancellationToken cancellationToken) => (await OptionsAsync(cancellationToken)).Enabled;

    public Task<MusicCatalog> CatalogAsync(CancellationToken cancellationToken) => catalogCache.GetAsync(cancellationToken);

    public async Task<MusicView> ViewAsync(string userKey, bool isAdmin, CancellationToken cancellationToken) =>
        new(await catalogCache.GetAsync(cancellationToken), await MusicUserState.LoadAsync(store, userKey, cancellationToken), isAdmin);

    // MARK: - Playlists

    public async Task<List<MusicPlaylist>> VisiblePlaylistsAsync(string userKey, CancellationToken cancellationToken) =>
        (await store.GetPlaylistsAsync(cancellationToken)).Where(p => p.CanSee(userKey)).ToList();

    // A playlist's tracks in order, each with the entry it came from (null for a smart
    // playlist, whose tracks are worked out from its rules now). Entries whose track has left
    // the library are skipped, not deleted, in case the folder comes back.
    public static List<(MusicTrack Track, Guid? EntryId)> PlaylistTracks(MusicView view, MusicPlaylist playlist, DateTimeOffset now, Random? random = null)
    {
        if (playlist.Rules is { } rules)
            return MusicSmartPlaylists.Evaluate(view, rules, now, random ?? Random.Shared).Select(t => (t, (Guid?)null)).ToList();
        var list = new List<(MusicTrack, Guid?)>();
        foreach (var entry in playlist.Entries)
            if (view.Catalog.TracksById.TryGetValue(entry.TrackId, out var track)) list.Add((track, entry.EntryId));
        return list;
    }

    // MARK: - Plays

    // Judges a reported listen by the counting rule, records it, and hands a counted play to
    // ListenBrainz. Duplicate reports (the same person, track and time within 5 s) are dropped.
    public async Task<MusicPlayRules.Outcome> RecordListenAsync(string userKey, MusicTrack track, MusicPlayedInput input, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(0, input.SecondsListened);
        var outcome = MusicPlayRules.Judge(seconds, input.Skipped, track.DurationSeconds);
        if (outcome == MusicPlayRules.Outcome.None) return outcome;
        var now = DateTimeOffset.UtcNow;
        var playedAt = input.PlayedAt is { } at && at <= now.AddMinutes(5) ? at.ToUniversalTime() : now;
        var device = string.IsNullOrWhiteSpace(input.DeviceName) ? null : input.DeviceName.Trim()[..Math.Min(input.DeviceName.Trim().Length, 100)];
        var play = new MusicPlay(userKey, track.Id, playedAt, Math.Round(seconds, 1), outcome == MusicPlayRules.Outcome.Skip, device);
        var recorded = await store.RecordPlayAsync(play, MusicPlayRules.DuplicateWindow, cancellationToken);
        if (recorded && outcome == MusicPlayRules.Outcome.Play)
            listenBrainz.Listened(userKey, Listen(track, playedAt));
        return recorded ? outcome : MusicPlayRules.Outcome.None;
    }

    public void PlayingNow(string userKey, MusicTrack track) => listenBrainz.PlayingNow(userKey, Listen(track, DateTimeOffset.UtcNow));

    private static AudioListen Listen(MusicTrack track, DateTimeOffset playedAt) =>
        new(track.ArtistText.Contains(';') ? string.Join(", ", track.Artists) : track.ArtistText, track.Title,
            string.IsNullOrWhiteSpace(track.Album) || track.Album == MusicText.UnknownAlbum ? null : track.Album,
            track.DurationSeconds > 0 ? Math.Round(track.DurationSeconds) : null, playedAt, track.TrackNumber);

    // MARK: - Mixes and radio

    public sealed record MixResult(string Name, List<MusicTrack> Tracks);

    public static readonly string[] SeedTypes = ["track", "album", "artist", "genre", "playlist", "decade"];

    // One of the person's mixes by id (daily-1, decade-1990, loved, discover). Null when it is
    // not one of theirs today.
    public static MixResult? Mix(MusicView view, string mixId, IReadOnlySet<Guid> exclude, int limit, DateTimeOffset now)
    {
        var mix = MusicDiscovery.Mixes(view, now).FirstOrDefault(m => string.Equals(m.Dto.Id, mixId, StringComparison.OrdinalIgnoreCase));
        if (mix is null) return null;
        // The first page of a mix is the day's; asking for more is fresh each time.
        var random = exclude.Count == 0 ? MusicDiscovery.DailyRandom(view.User.UserKey, mix.Dto.Id, now) : Random.Shared;
        return new MixResult(mix.Dto.Name, mix.Tracks(limit, exclude, random));
    }

    // A radio from a track, album, artist, genre, playlist or decade. Null when the seed is not
    // in the library.
    public static MixResult? Radio(MusicView view, string seedType, string seedId, IReadOnlyList<MusicPlaylist> visiblePlaylists,
        IReadOnlySet<Guid> exclude, int limit, DateTimeOffset now)
    {
        var catalog = view.Catalog;
        var empty = new HashSet<Guid>();
        var noGenres = new HashSet<string>();
        var noDecades = new HashSet<int>();
        MusicDiscovery.RadioSeed seed;
        string name;
        switch (seedType)
        {
            case "track":
            {
                if (!Guid.TryParse(seedId, out var id) || !catalog.TracksById.TryGetValue(id, out var track)) return null;
                seed = new(track.Artists.Select(MusicIds.Artist).ToHashSet(), track.Genres.Select(MusicText.Key).ToHashSet(),
                    MusicCatalog.Decade(track.Year) is { } d ? new HashSet<int> { d } : noDecades, [track]);
                name = $"{track.Title} Radio";
                break;
            }
            case "album":
            {
                if (!Guid.TryParse(seedId, out var id) || !catalog.AlbumsById.TryGetValue(id, out var album)) return null;
                var artists = album.Tracks.SelectMany(t => t.Artists).Select(MusicIds.Artist).ToHashSet();
                if (!album.Compilation) artists.Add(album.ArtistId);
                seed = new(artists, album.Genres.Select(MusicText.Key).ToHashSet(),
                    MusicCatalog.Decade(album.Year) is { } d ? new HashSet<int> { d } : noDecades, []);
                name = $"{album.Title} Radio";
                break;
            }
            case "artist":
            {
                if (!Guid.TryParse(seedId, out var id) || !catalog.ArtistsById.TryGetValue(id, out var artist)) return null;
                var decades = artist.Tracks.Select(t => MusicCatalog.Decade(t.Year)).OfType<int>().GroupBy(d => d)
                    .OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToHashSet();
                seed = new(new HashSet<Guid> { id }, artist.Genres.Select(MusicText.Key).ToHashSet(), decades, []);
                name = $"{artist.Name} Radio";
                break;
            }
            case "genre":
            {
                var key = MusicText.Key(seedId);
                var genre = catalog.Genres.FirstOrDefault(g => MusicText.Key(g) == key);
                if (genre is null) return null;
                seed = new(empty, new HashSet<string> { key }, noDecades, [], t => t.Genres.Any(g => MusicText.Key(g) == key));
                name = $"{genre} Radio";
                break;
            }
            case "playlist":
            {
                if (!Guid.TryParse(seedId, out var id) || visiblePlaylists.FirstOrDefault(p => p.Id == id) is not { } playlist) return null;
                var tracks = PlaylistTracks(view, playlist, now).Select(x => x.Track).ToList();
                seed = new(tracks.GroupBy(t => t.ArtistId).OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key).ToHashSet(),
                    tracks.SelectMany(t => t.Genres).GroupBy(MusicText.Key).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToHashSet(),
                    tracks.Select(t => MusicCatalog.Decade(t.Year)).OfType<int>().GroupBy(d => d).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToHashSet(),
                    []);
                name = $"{playlist.Name} Radio";
                break;
            }
            case "decade":
            {
                var digits = new string(seedId.Where(char.IsDigit).ToArray());
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var decade)) return null;
                if (decade < 100) decade += decade >= 30 ? 1900 : 2000;
                decade = decade / 10 * 10;
                if (!catalog.Tracks.Any(t => MusicCatalog.Decade(t.Year) == decade)) return null;
                seed = new(empty, noGenres, new HashSet<int> { decade }, [], t => MusicCatalog.Decade(t.Year) == decade);
                name = $"{MusicCatalog.DecadeLabel(decade)} Radio";
                break;
            }
            default:
                return null;
        }
        return new MixResult(name, MusicDiscovery.Radio(view, seed, exclude, limit, now, Random.Shared));
    }

    // MARK: - IMusicAudioSource

    public async Task<IReadOnlyList<AudioEntry>> ResolveTracksAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var view = await ViewAsync(userKey, isAdmin, cancellationToken);
        var entries = new List<AudioEntry>();
        foreach (var id in ids)
            if (view.Catalog.TracksById.TryGetValue(id, out var track)) entries.Add(new AudioEntry(id, view.Track(track)));
        return entries;
    }

    public async Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken)
    {
        var view = await ViewAsync(userKey, false, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var catalog = view.Catalog;
        switch (type)
        {
            case AudioSourceTypes.Playlist:
            {
                if (!Guid.TryParse(id, out var playlistId)) return [];
                var playlist = (await VisiblePlaylistsAsync(userKey, cancellationToken)).FirstOrDefault(p => p.Id == playlistId);
                return playlist is null ? [] : PlaylistTracks(view, playlist, now).Select(x => x.Track.Id).ToList();
            }
            case AudioSourceTypes.Album:
                return Guid.TryParse(id, out var albumId) && catalog.AlbumsById.TryGetValue(albumId, out var album)
                    ? album.Tracks.Select(t => t.Id).ToList()
                    : [];
            case AudioSourceTypes.Artist:
                return Guid.TryParse(id, out var artistId) && catalog.ArtistsById.TryGetValue(artistId, out var artist)
                    ? MusicDiscovery.Spaced(artist.Tracks.OrderBy(_ => Random.Shared.Next()).ToList(), 200).Concat(artist.Tracks).Distinct().Take(200).Select(t => t.Id).ToList()
                    : [];
            case AudioSourceTypes.Mix:
            {
                if (string.IsNullOrWhiteSpace(id)) return [];
                // A mix id (daily-1), or "seedType:seedId" for a radio.
                var colon = id.IndexOf(':');
                var result = colon > 0
                    ? Radio(view, id[..colon], id[(colon + 1)..], await VisiblePlaylistsAsync(userKey, cancellationToken), new HashSet<Guid>(), 50, now)
                    : Mix(view, id, new HashSet<Guid>(), 50, now);
                return result?.Tracks.Select(t => t.Id).ToList() ?? [];
            }
            case AudioSourceTypes.Shuffle:
                return MusicDiscovery.Shuffle(view, 200, now, Random.Shared).Select(t => t.Id).ToList();
            default:
                return [];
        }
    }

    public async Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        MusicDiscovery.Stats(await ViewAsync(userKey, false, cancellationToken), from, to);

    public async Task<IReadOnlyList<AudioLibraryInfo>> LibrariesAsync(CancellationToken cancellationToken) =>
        (await store.GetLibrariesAsync(cancellationToken)).Select(l => new AudioLibraryInfo(l.Id, l.Name, l.Path)).ToList();

    public void RequestScan(Guid libraryId) => scanner.RequestScan(libraryId);

    public async Task<bool> HasAlbumAsync(string artist, string album, CancellationToken cancellationToken)
    {
        var catalog = await catalogCache.GetAsync(cancellationToken);
        var artistKey = MusicText.Key(artist);
        // Edition words don't count: "Rumours (Deluxe Edition)" here means "Rumours" is had.
        var albumKey = MusicText.EditionlessKey(album);
        return catalog.Albums.Any(a => MusicText.EditionlessKey(a.Title) == albumKey
            && (MusicText.Key(a.Artist) == artistKey || a.Tracks.Any(t => t.Artists.Any(n => MusicText.Key(n) == artistKey))));
    }

    public async Task<double> LosslessShareAsync(CancellationToken cancellationToken)
    {
        var catalog = await catalogCache.GetAsync(cancellationToken);
        return catalog.Tracks.Count == 0 ? 0 : catalog.Tracks.Count(t => t.Codec is "flac" or "alac" or "wav" or "aiff") / (double)catalog.Tracks.Count;
    }

    public async Task<MusicBackup> ExportAsync(CancellationToken cancellationToken) => new(
        await store.GetLibrariesAsync(cancellationToken),
        await store.AllPlaysAsync(cancellationToken),
        await store.AllLovesAsync(cancellationToken),
        await store.AllRatingsAsync(cancellationToken),
        await store.GetPlaylistsAsync(cancellationToken),
        await store.AllPinsAsync(cancellationToken));

    // The libraries keep their ids, and track ids are made from the library id and the file's
    // path, so the rescan this starts lands on the ids the plays and playlists point at.
    // Everything merges newest-edit-wins: an old backup rolls nothing back.
    public async Task<int> RestoreAsync(MusicBackup backup, CancellationToken cancellationToken)
    {
        var restored = 0;
        var existing = (await store.GetLibrariesAsync(cancellationToken)).ToDictionary(l => l.Id);
        var librariesRestored = 0;
        foreach (var library in backup.Libraries ?? [])
        {
            if (library.Id == Guid.Empty || string.IsNullOrWhiteSpace(library.Name) || string.IsNullOrWhiteSpace(library.Path)) continue;
            var keep = existing.GetValueOrDefault(library.Id);
            await store.SaveLibraryAsync(library with
            {
                LastScanStartedAt = keep?.LastScanStartedAt,
                LastScanFinishedAt = keep?.LastScanFinishedAt,
                LastScanError = keep?.LastScanError,
                TrackCount = keep?.TrackCount ?? 0
            }, cancellationToken);
            librariesRestored++;
        }
        restored += librariesRestored;
        restored += await store.RestorePlaysAsync((backup.Plays ?? []).Where(p => p.TrackId != Guid.Empty && p.UserKey is not null), cancellationToken);
        restored += await store.RestoreLovesAsync((backup.Loves ?? []).Where(l => l.ItemId != Guid.Empty && l.UserKey is not null && MusicLoveKinds.IsValid(l.Kind)), cancellationToken);
        restored += await store.RestoreRatingsAsync((backup.Ratings ?? []).Where(r => r.ItemId != Guid.Empty && r.UserKey is not null && MusicLoveKinds.IsValid(r.Kind)), cancellationToken);
        restored += await store.RestorePlaylistsAsync((backup.Playlists ?? [])
            .Where(p => p.Id != Guid.Empty && p.OwnerKey is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p with { Description = p.Description ?? "", Entries = p.Entries ?? [] }), cancellationToken);
        restored += await store.RestorePinsAsync((backup.Pins ?? []).Where(p => p.PlaylistId != Guid.Empty && p.UserKey is not null), cancellationToken);
        if (librariesRestored > 0) scanner.RequestScan(null);
        logger.LogInformation("Music restore: {Count} rows", restored);
        return restored;
    }
}
