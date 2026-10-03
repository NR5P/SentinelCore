using System.Globalization;
using System.Text.Json.Serialization;

namespace SentinelCore.Web;

public sealed class MusicAlbumInfo
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public Guid ArtistId { get; init; }
    public int? Year { get; init; }
    public List<string> Genres { get; init; } = [];
    public List<MusicTrack> Tracks { get; init; } = [];
    public bool Compilation { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public string? ArtPath { get; init; }
    public string[] Words { get; init; } = [];
    public double DurationSeconds => Tracks.Sum(t => t.DurationSeconds);
    public int DiscCount => Math.Max(1, Tracks.Select(t => t.DiscNumber ?? 1).Distinct().Count());
}

public sealed class MusicArtistInfo
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public List<MusicAlbumInfo> Albums { get; init; } = [];
    public List<MusicAlbumInfo> AppearsOn { get; init; } = [];
    public List<MusicTrack> Tracks { get; init; } = [];
    public List<string> Genres { get; init; } = [];
    public string? ImagePath { get; init; }
    public string[] Words { get; init; } = [];
    public bool IsAlbumArtist => Albums.Count > 0;
}

// The whole catalog in memory, built once per change of music.db's tracks or art and shared by
// every request: albums and artists are worked out from the tracks, and the search words are
// folded once. A household library is thousands to tens of thousands of tracks - a few
// megabytes - and every list, mix and smart playlist is a walk over it.
public sealed class MusicCatalog
{
    public MusicCatalog(long version, IReadOnlyList<MusicTrack> tracks, IReadOnlyDictionary<string, MusicArtRecord> art, IReadOnlyList<MusicLibrary> libraries)
    {
        Version = version;
        Libraries = libraries;
        Tracks = tracks
            .OrderBy(t => MusicText.SortKey(t.AlbumArtist), StringComparer.Ordinal)
            .ThenBy(t => MusicText.Key(t.Album), StringComparer.Ordinal)
            .ThenBy(t => t.DiscNumber ?? 1).ThenBy(t => t.TrackNumber ?? int.MaxValue)
            .ThenBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        TracksById = Tracks.ToDictionary(t => t.Id);
        TrackWords = Tracks.ToDictionary(t => t.Id, t => MusicText.Words($"{t.Title} {t.ArtistText} {string.Join(' ', t.Artists)} {t.Album}"));

        var albums = new List<MusicAlbumInfo>();
        foreach (var group in Tracks.GroupBy(t => t.AlbumId))
        {
            var list = group.ToList();
            var first = list[0];
            var artPath = art.GetValueOrDefault(MusicArtCache.AlbumKey(group.Key))?.Path;
            albums.Add(new MusicAlbumInfo
            {
                Id = group.Key,
                Title = MostCommon(list.Select(t => t.Album)) ?? first.Album,
                Artist = MostCommon(list.Select(t => t.AlbumArtist)) ?? first.AlbumArtist,
                ArtistId = MusicIds.Artist(first.AlbumArtist),
                Year = list.Where(t => t.Year is not null).GroupBy(t => t.Year).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault(),
                Genres = TopGenres(list, 3),
                Tracks = list,
                Compilation = list.Any(t => t.Compilation),
                AddedAt = list.Max(t => t.AddedAt),
                ArtPath = artPath,
                Words = MusicText.Words($"{first.Album} {first.AlbumArtist}")
            });
        }
        Albums = albums;
        AlbumsById = albums.ToDictionary(a => a.Id);

        // Every name a track is credited to, and every album artist, is an artist.
        var names = new Dictionary<Guid, List<string>>();
        void Name(string name)
        {
            var id = MusicIds.Artist(name);
            if (!names.TryGetValue(id, out var list)) names[id] = list = [];
            list.Add(name);
        }
        foreach (var track in Tracks)
        {
            foreach (var artist in track.Artists) Name(artist);
            Name(track.AlbumArtist);
        }
        var tracksByArtist = new Dictionary<Guid, List<MusicTrack>>();
        foreach (var track in Tracks)
        foreach (var id in track.Artists.Select(MusicIds.Artist).Distinct())
        {
            if (!tracksByArtist.TryGetValue(id, out var list)) tracksByArtist[id] = list = [];
            list.Add(track);
        }
        var albumsByArtist = albums.GroupBy(a => a.ArtistId).ToDictionary(g => g.Key, g => g.ToList());
        var artists = new List<MusicArtistInfo>();
        foreach (var (id, spellings) in names)
        {
            var own = albumsByArtist.GetValueOrDefault(id) ?? [];
            var tracksOf = tracksByArtist.GetValueOrDefault(id) ?? [];
            var appears = tracksOf.Select(t => t.AlbumId).Distinct().Where(a => AlbumsById[a].ArtistId != id).Select(a => AlbumsById[a]).ToList();
            var name = MostCommon(spellings) ?? spellings[0];
            artists.Add(new MusicArtistInfo
            {
                Id = id,
                Name = name,
                Albums = own.OrderByDescending(a => a.Year ?? 0).ThenBy(a => MusicText.SortKey(a.Title), StringComparer.Ordinal).ToList(),
                AppearsOn = appears.OrderByDescending(a => a.Year ?? 0).ToList(),
                Tracks = tracksOf.Count > 0 ? tracksOf : own.SelectMany(a => a.Tracks).ToList(),
                Genres = TopGenres(tracksOf.Count > 0 ? tracksOf : own.SelectMany(a => a.Tracks), 3),
                ImagePath = art.GetValueOrDefault(MusicArtCache.ArtistKey(id))?.Path,
                Words = MusicText.Words(name)
            });
        }
        Artists = artists.OrderBy(a => MusicText.SortKey(a.Name), StringComparer.Ordinal).ToList();
        ArtistsById = Artists.ToDictionary(a => a.Id);

        Genres = Tracks.SelectMany(t => t.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public long Version { get; }
    public IReadOnlyList<MusicLibrary> Libraries { get; }
    public IReadOnlyList<MusicTrack> Tracks { get; }
    public IReadOnlyDictionary<Guid, MusicTrack> TracksById { get; }
    public IReadOnlyDictionary<Guid, string[]> TrackWords { get; }
    public IReadOnlyList<MusicAlbumInfo> Albums { get; }
    public IReadOnlyDictionary<Guid, MusicAlbumInfo> AlbumsById { get; }
    public IReadOnlyList<MusicArtistInfo> Artists { get; }
    public IReadOnlyDictionary<Guid, MusicArtistInfo> ArtistsById { get; }
    public IReadOnlyList<string> Genres { get; }

    public MusicAlbumInfo? AlbumOf(MusicTrack track) => AlbumsById.GetValueOrDefault(track.AlbumId);

    public static int? Decade(int? year) => year is { } y ? y / 10 * 10 : null;

    public static string DecadeLabel(int decade) => decade >= 2000 ? $"{decade}s" : $"{decade % 100:00}s";

    // An artist's picture: artist.jpg from their folder, else the cover of their newest album.
    public string? ArtistArt(MusicArtistInfo artist) =>
        artist.ImagePath ?? artist.Albums.Concat(artist.AppearsOn).FirstOrDefault(a => a.ArtPath is not null)?.ArtPath;

    private static string? MostCommon(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();

    private static List<string> TopGenres(IEnumerable<MusicTrack> tracks, int count) =>
        tracks.SelectMany(t => t.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(count).Select(g => g.First()).ToList();
}

// Keeps one MusicCatalog for the whole process and rebuilds it when music.db says the tracks or
// the art changed.
public sealed class MusicCatalogCache(IMusicStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MusicCatalog? _current;

    public async Task<MusicCatalog> GetAsync(CancellationToken cancellationToken)
    {
        var current = _current;
        var version = store.CatalogVersion;
        if (current is not null && current.Version == version) return current;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            version = store.CatalogVersion;
            if (_current is { } fresh && fresh.Version == version) return fresh;
            var tracks = (await store.GetTracksAsync(null, includeMissing: false, cancellationToken))
                .Where(t => t.TagsReadAt is not null && t.AlbumId != Guid.Empty).ToList();
            var art = await store.GetArtAsync(cancellationToken);
            var libraries = await store.GetLibrariesAsync(cancellationToken);
            var built = new MusicCatalog(version, tracks, art, libraries);
            _current = built;
            return built;
        }
        finally { _gate.Release(); }
    }
}

// What one person did with the catalog: plays and skips per track, loves and ratings.
public sealed class MusicUserState
{
    public sealed record TrackStats(int Plays, int Skips, DateTimeOffset? LastPlayedAt, DateTimeOffset? LastSkippedAt);

    private MusicUserState(string userKey, IReadOnlyList<MusicPlay> plays, IReadOnlyList<MusicLove> loves, IReadOnlyList<MusicRating> ratings)
    {
        UserKey = userKey;
        Plays = plays;
        Stats = plays.GroupBy(p => p.TrackId).ToDictionary(g => g.Key, g => new TrackStats(
            g.Count(p => !p.Skipped), g.Count(p => p.Skipped),
            g.Where(p => !p.Skipped).Select(p => (DateTimeOffset?)p.PlayedAt).Max(),
            g.Where(p => p.Skipped).Select(p => (DateTimeOffset?)p.PlayedAt).Max()));
        Loved = loves.Where(l => l.Loved).Select(l => (l.Kind, l.ItemId)).ToHashSet();
        LovedAt = loves.Where(l => l.Loved).ToDictionary(l => (l.Kind, l.ItemId), l => l.UpdatedAt);
        Ratings = ratings.Where(r => r.Rating > 0).ToDictionary(r => (r.Kind, r.ItemId), r => r.Rating);
    }

    public string UserKey { get; }
    // Oldest first; skips included (Skipped set).
    public IReadOnlyList<MusicPlay> Plays { get; }
    public IReadOnlyDictionary<Guid, TrackStats> Stats { get; }
    public IReadOnlySet<(string Kind, Guid Id)> Loved { get; }
    public IReadOnlyDictionary<(string Kind, Guid Id), DateTimeOffset> LovedAt { get; }
    public IReadOnlyDictionary<(string Kind, Guid Id), int> Ratings { get; }

    public static async Task<MusicUserState> LoadAsync(IMusicStore store, string userKey, CancellationToken cancellationToken) =>
        new(userKey, await store.GetPlaysAsync(userKey, cancellationToken), await store.GetLovesAsync(userKey, cancellationToken),
            await store.GetRatingsAsync(userKey, cancellationToken));

    public bool IsLoved(string kind, Guid id) => Loved.Contains((kind, id));

    public int RatingOf(string kind, Guid id) => Ratings.GetValueOrDefault((kind, id));

    public int PlayCount(Guid trackId) => Stats.GetValueOrDefault(trackId)?.Plays ?? 0;

    public int SkipCount(Guid trackId) => Stats.GetValueOrDefault(trackId)?.Skips ?? 0;

    public DateTimeOffset? LastPlayed(Guid trackId) => Stats.GetValueOrDefault(trackId)?.LastPlayedAt;

    // The most recent play or skip: what "nothing played in the last hour" means for a radio.
    public DateTimeOffset? LastHeard(Guid trackId) =>
        Stats.GetValueOrDefault(trackId) is { } s ? new[] { s.LastPlayedAt, s.LastSkippedAt }.Max() : null;
}

// MARK: - DTOs (MUSIC_AND_PODCASTS.md section 3)

public sealed record MusicReplayGainDto(double? TrackGain, double? TrackPeak, double? AlbumGain, double? AlbumPeak);

public sealed class MusicTrackDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public List<string> Artists { get; init; } = [];
    public Guid ArtistId { get; init; }
    public string AlbumArtist { get; init; } = "";
    public string Album { get; init; } = "";
    public Guid AlbumId { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int? Year { get; init; }
    public List<string> Genres { get; init; } = [];
    public double DurationSeconds { get; init; }
    public string Codec { get; init; } = "";
    public long? Bitrate { get; init; }
    public int? SampleRate { get; init; }
    public int? BitDepth { get; init; }
    public int? Channels { get; init; }
    public long SizeBytes { get; init; }
    public string? Composer { get; init; }
    public int? Bpm { get; init; }
    public MusicReplayGainDto? ReplayGain { get; init; }
    public bool HasLyrics { get; init; }
    public string? ArtUrl { get; init; }
    public string StreamUrl { get; init; } = "";
    public DateTimeOffset AddedAt { get; init; }
    public bool Loved { get; init; }
    public int Rating { get; init; }
    public int PlayCount { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public string? Path { get; init; }
    // Only on a playlist's tracks: which entry this is, so duplicates can be told apart.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? EntryId { get; init; }
}

public class MusicAlbumDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public Guid ArtistId { get; init; }
    public int? Year { get; init; }
    public List<string> Genres { get; init; } = [];
    public int TrackCount { get; init; }
    public int DiscCount { get; init; }
    public double DurationSeconds { get; init; }
    public string? ArtUrl { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public bool Compilation { get; init; }
    public bool Loved { get; init; }
    public int Rating { get; init; }
    public int PlayCount { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
}

public sealed class MusicAlbumDetailDto : MusicAlbumDto
{
    public List<MusicTrackDto> Tracks { get; init; } = [];
    public List<MusicAlbumDto> MoreByArtist { get; init; } = [];
    public long TotalBytes { get; init; }
    public List<string> Formats { get; init; } = [];
}

public class MusicArtistDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int AlbumCount { get; init; }
    public int TrackCount { get; init; }
    public List<string> Genres { get; init; } = [];
    public string? ArtUrl { get; init; }
    public bool Loved { get; init; }
    public int PlayCount { get; init; }
}

public sealed class MusicArtistDetailDto : MusicArtistDto
{
    public List<MusicAlbumDto> Albums { get; init; } = [];
    public List<MusicAlbumDto> AppearsOn { get; init; } = [];
    public List<MusicTrackDto> TopTracks { get; init; } = [];
    public List<MusicArtistDto> Similar { get; init; } = [];
}

public class MusicPlaylistDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public bool Mine { get; init; }
    public bool Shared { get; init; }
    public bool Collaborative { get; init; }
    public bool Smart { get; init; }
    public MusicSmartRules? Rules { get; init; }
    public int TrackCount { get; init; }
    public double DurationSeconds { get; init; }
    public List<string> ArtUrls { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool Pinned { get; init; }
}

public sealed class MusicPlaylistDetailDto : MusicPlaylistDto
{
    public List<MusicTrackDto> Tracks { get; init; } = [];
}

public sealed record MusicMixDto(string Id, string Name, string Description, List<string> ArtUrls, string SeedType, string SeedId);

// The catalog as one person sees it: their loves, ratings and plays on every object, and the
// path only for admins.
public sealed class MusicView(MusicCatalog catalog, MusicUserState user, bool isAdmin)
{
    public MusicCatalog Catalog { get; } = catalog;
    public MusicUserState User { get; } = user;
    public bool IsAdmin { get; } = isAdmin;

    public static string AlbumArtUrl(Guid albumId) => $"/api/music/albums/{albumId}/art";

    public static string ArtistArtUrl(Guid artistId) => $"/api/music/artists/{artistId}/art";

    public string? ArtUrlOf(MusicTrack track) => Catalog.AlbumOf(track)?.ArtPath is null ? null : AlbumArtUrl(track.AlbumId);

    public MusicTrackDto Track(MusicTrack t, Guid? entryId = null) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Artist = t.ArtistText.Contains(';') ? string.Join(", ", t.Artists) : t.ArtistText,
        Artists = t.Artists,
        ArtistId = t.ArtistId,
        AlbumArtist = t.AlbumArtist,
        Album = t.Album,
        AlbumId = t.AlbumId,
        TrackNumber = t.TrackNumber,
        DiscNumber = t.DiscNumber,
        Year = t.Year,
        Genres = t.Genres,
        DurationSeconds = Math.Round(t.DurationSeconds, 1),
        Codec = t.Codec,
        Bitrate = t.Bitrate,
        SampleRate = t.SampleRate,
        BitDepth = t.BitDepth,
        Channels = t.Channels,
        SizeBytes = t.FileSize,
        Composer = t.Composer,
        Bpm = t.Bpm,
        ReplayGain = t.TrackGain is null && t.TrackPeak is null && t.AlbumGain is null && t.AlbumPeak is null
            ? null
            : new MusicReplayGainDto(t.TrackGain, t.TrackPeak, t.AlbumGain, t.AlbumPeak),
        HasLyrics = t.HasLyrics,
        ArtUrl = ArtUrlOf(t),
        StreamUrl = $"/api/music/tracks/{t.Id}/stream",
        AddedAt = t.AddedAt,
        Loved = User.IsLoved(MusicLoveKinds.Track, t.Id),
        Rating = User.RatingOf(MusicLoveKinds.Track, t.Id),
        PlayCount = User.PlayCount(t.Id),
        LastPlayedAt = User.LastPlayed(t.Id),
        Path = IsAdmin ? t.RelativePath : null,
        EntryId = entryId
    };

    public int AlbumPlays(MusicAlbumInfo album) => album.Tracks.Sum(t => User.PlayCount(t.Id));

    public DateTimeOffset? AlbumLastPlayed(MusicAlbumInfo album) => album.Tracks.Select(t => User.LastPlayed(t.Id)).Max();

    public int ArtistPlays(MusicArtistInfo artist) => artist.Tracks.Sum(t => User.PlayCount(t.Id));

    public MusicAlbumDto Album(MusicAlbumInfo a) => new()
    {
        Id = a.Id,
        Title = a.Title,
        Artist = a.Artist,
        ArtistId = a.ArtistId,
        Year = a.Year,
        Genres = a.Genres,
        TrackCount = a.Tracks.Count,
        DiscCount = a.DiscCount,
        DurationSeconds = Math.Round(a.DurationSeconds, 1),
        ArtUrl = a.ArtPath is null ? null : AlbumArtUrl(a.Id),
        AddedAt = a.AddedAt,
        Compilation = a.Compilation,
        Loved = User.IsLoved(MusicLoveKinds.Album, a.Id),
        Rating = User.RatingOf(MusicLoveKinds.Album, a.Id),
        PlayCount = AlbumPlays(a),
        LastPlayedAt = AlbumLastPlayed(a)
    };

    public MusicAlbumDetailDto AlbumDetail(MusicAlbumInfo a)
    {
        var summary = Album(a);
        var artist = Catalog.ArtistsById.GetValueOrDefault(a.ArtistId);
        return new MusicAlbumDetailDto
        {
            Id = summary.Id, Title = summary.Title, Artist = summary.Artist, ArtistId = summary.ArtistId, Year = summary.Year,
            Genres = summary.Genres, TrackCount = summary.TrackCount, DiscCount = summary.DiscCount, DurationSeconds = summary.DurationSeconds,
            ArtUrl = summary.ArtUrl, AddedAt = summary.AddedAt, Compilation = summary.Compilation, Loved = summary.Loved,
            Rating = summary.Rating, PlayCount = summary.PlayCount, LastPlayedAt = summary.LastPlayedAt,
            Tracks = a.Tracks.Select(t => Track(t)).ToList(),
            MoreByArtist = a.Compilation || artist is null ? [] : artist.Albums.Where(o => o.Id != a.Id).Take(20).Select(Album).ToList(),
            TotalBytes = a.Tracks.Sum(t => t.FileSize),
            Formats = a.Tracks.Select(Format).Distinct().ToList()
        };
    }

    // "flac 16/44.1" for lossless, "mp3 320" for lossy.
    public static string Format(MusicTrack t)
    {
        if (MusicTags.IsLossless(t.Codec) && t.SampleRate is { } rate)
            return $"{t.Codec} {(t.BitDepth is { } bits ? bits + "/" : "")}{(rate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)}";
        return t.Bitrate is { } bitrate ? $"{t.Codec} {Math.Round(bitrate / 1000.0)}" : t.Codec;
    }

    public MusicArtistDto Artist(MusicArtistInfo a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        AlbumCount = a.Albums.Count,
        TrackCount = a.Tracks.Count,
        Genres = a.Genres,
        ArtUrl = Catalog.ArtistArt(a) is null ? null : ArtistArtUrl(a.Id),
        Loved = User.IsLoved(MusicLoveKinds.Artist, a.Id),
        PlayCount = ArtistPlays(a)
    };

    public MusicArtistDetailDto ArtistDetail(MusicArtistInfo a, IReadOnlyDictionary<Guid, int> householdPlays)
    {
        var summary = Artist(a);
        var genres = a.Genres.Select(MusicText.Key).ToHashSet();
        var similar = genres.Count == 0 ? [] : Catalog.Artists
            .Where(o => o.Id != a.Id && o.IsAlbumArtist && !MusicAlbumRules.IsVariousArtists(o.Name))
            .Select(o => (Artist: o, Shared: o.Genres.Count(g => genres.Contains(MusicText.Key(g)))))
            .Where(x => x.Shared > 0)
            .OrderByDescending(x => x.Shared).ThenByDescending(x => x.Artist.Tracks.Count).ThenBy(x => x.Artist.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8).Select(x => Artist(x.Artist)).ToList();
        return new MusicArtistDetailDto
        {
            Id = summary.Id, Name = summary.Name, AlbumCount = summary.AlbumCount, TrackCount = summary.TrackCount,
            Genres = summary.Genres, ArtUrl = summary.ArtUrl, Loved = summary.Loved, PlayCount = summary.PlayCount,
            Albums = a.Albums.Select(Album).ToList(),
            AppearsOn = a.AppearsOn.Select(Album).ToList(),
            TopTracks = a.Tracks.OrderByDescending(t => householdPlays.GetValueOrDefault(t.Id))
                .ThenByDescending(t => User.PlayCount(t.Id)).ThenBy(t => t.Year ?? 0)
                .Take(10).Select(t => Track(t)).ToList(),
            Similar = similar
        };
    }

    public static string OwnerName(string ownerKey) => ownerKey.Length == 0 ? "Household" : ownerKey;

    public MusicPlaylistDto Playlist(MusicPlaylist p, IReadOnlyList<MusicTrack> tracks, bool pinned) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        OwnerName = OwnerName(p.OwnerKey),
        Mine = p.OwnerKey == User.UserKey,
        Shared = p.Shared,
        Collaborative = p.Collaborative,
        Smart = p.Smart,
        Rules = p.Rules,
        TrackCount = tracks.Count,
        DurationSeconds = Math.Round(tracks.Sum(t => t.DurationSeconds), 1),
        ArtUrls = ArtUrls(tracks),
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        Pinned = pinned
    };

    // Up to four different album covers, for a 2x2 mosaic.
    public List<string> ArtUrls(IEnumerable<MusicTrack> tracks) =>
        tracks.Select(t => t.AlbumId).Distinct()
            .Select(id => Catalog.AlbumsById.GetValueOrDefault(id))
            .Where(a => a?.ArtPath is not null).Take(4).Select(a => AlbumArtUrl(a!.Id)).ToList();
}
