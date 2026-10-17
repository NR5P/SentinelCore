using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// Reads a list someone exported from another service into the albums or movies it names, for
// "Import a list" on Music and Movies. Pure, for the tests. Contract: core/docs/MOVIE_DOWNLOADS.md
// "Import a list".
//
// There is no one standard, so this recognises the common exports by their columns or keys:
//   Music:  Exportify CSV, TuneMyMusic / Soundiiz CSV, Spotify's own "Download your data" JSON
//           (YourLibrary.json, Playlist1.json), or any CSV with artist + album (+ track) columns.
//   Movies: Letterboxd CSV (watchlist.csv, watched.csv...), IMDb list/ratings CSV, Trakt-style
//           JSON, or any CSV with a title (+ year) column.
// Torrent sites share music as whole albums, never songs, so tracks are grouped into the albums
// they come from: 300 liked songs off 40 albums is 40 downloads.
public static partial class MediaImportList
{
    public const string KindMusic = "music";
    public const string KindMovies = "movies";

    public sealed record ImportFile(string Name, string Text);

    public sealed record Album(string Artist, string Title, int? Year, int TrackCount, IReadOnlyList<string> Tracks, bool Single)
    {
        public string Key => AlbumKey(Artist, Title);
    }

    public sealed record Movie(string Title, int? Year, string? ImdbId)
    {
        public string Key => MovieKey(Title, Year);
    }

    public sealed class Result
    {
        public string Kind { get; set; } = KindMusic;
        // What the file looked like: "Exportify", "Spotify data export", "TuneMyMusic", "Letterboxd", "IMDb", "CSV", "JSON".
        public List<string> Sources { get; } = [];
        public List<Album> Albums { get; } = [];
        public List<Movie> Movies { get; } = [];
        public int Tracks { get; set; }
        // Rows that named a song but no album: nothing to search a torrent site for.
        public int NoAlbum { get; set; }
        // TV shows in a movie list: they need seasons picked, so they are left for "+ Add".
        public int Shows { get; set; }
        // Podcast episodes, local files, rows with no title, files that could not be read.
        public int Unreadable { get; set; }
        public List<string> Problems { get; } = [];
    }

    public static Result Parse(IReadOnlyList<ImportFile> files)
    {
        var result = new Result();
        var albums = new Dictionary<string, AlbumBuilder>(StringComparer.Ordinal);
        var movies = new Dictionary<string, Movie>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = file.Text.TrimStart('﻿');
            var trimmed = text.TrimStart();
            try
            {
                if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) ParseJson(file.Name, trimmed, result, albums, movies);
                else ParseCsv(file.Name, text, result, albums, movies);
            }
            catch (JsonException)
            {
                result.Problems.Add($"{file.Name}: not a JSON file this can read.");
            }
        }

        result.Albums.AddRange(albums.Values.Select(b => b.Build()).OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Year ?? 0));
        result.Movies.AddRange(movies.Values.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase));
        // One file decides: a Letterboxd list never has albums, a Spotify one never has movies.
        result.Kind = result.Movies.Count > result.Albums.Count ? KindMovies : KindMusic;
        return result;
    }

    // MARK: - CSV

    private static readonly string[] ArtistColumns = ["albumartistnames", "albumartistname", "albumartist", "artistnames", "artistname", "artists", "artist", "performer", "band"];
    private static readonly string[] AlbumColumns = ["albumname", "album", "albumtitle", "release", "releasename"];
    private static readonly string[] TrackColumns = ["trackname", "track", "tracktitle", "songname", "song", "songtitle", "name", "title"];
    private static readonly string[] DateColumns = ["albumreleasedate", "releasedate", "releaseyear", "albumyear", "year", "date"];
    private static readonly string[] TitleColumns = ["title", "name", "movie", "movietitle", "film", "originaltitle"];

    private static void ParseCsv(string fileName, string text, Result result, Dictionary<string, AlbumBuilder> albums, Dictionary<string, Movie> movies)
    {
        var rows = ReadCsv(text);
        if (rows.Count == 0) { result.Problems.Add($"{fileName}: empty."); return; }
        var header = rows[0].Select(Column).ToList();
        int Find(params string[] names)
        {
            foreach (var name in names)
            {
                var index = header.IndexOf(name);
                if (index >= 0) return index;
            }
            return -1;
        }

        var artist = Find(ArtistColumns);
        var album = Find(AlbumColumns);
        var track = Find(TrackColumns);
        if (artist >= 0 && (album >= 0 || track >= 0))
        {
            result.Sources.Add(header.Contains("spotifyid") || header.Contains("trackuri") || header.Contains("artistnames") ? "Exportify"
                : header.Contains("playlistname") || header.Contains("type") && header.Contains("isrc") ? "TuneMyMusic"
                : "CSV");
            // Exportify has both "Artist Name(s)" and "Album Artist Name(s)": the album's artist is
            // what the album is filed and shared under ("Various Artists" for a compilation).
            var trackArtist = Find("artistnames", "artistname", "artists", "artist");
            var date = Find(DateColumns);
            foreach (var row in rows.Skip(1))
            {
                string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                var who = FirstArtist(Cell(artist));
                if (who.Length == 0) who = FirstArtist(Cell(trackArtist));
                AddTrack(result, albums, who, Cell(album), Cell(track), Year(Cell(date)));
            }
            return;
        }

        var title = Find(TitleColumns);
        if (title >= 0)
        {
            var year = Find("year", "releaseyear", "releasedate", "date");
            var type = Find("titletype", "type", "mediatype", "kind");
            var imdb = Find("const", "imdbid", "imdb", "tconst");
            var uri = Find("letterboxduri");
            result.Sources.Add(uri >= 0 ? "Letterboxd" : header.Contains("const") ? "IMDb" : "CSV");
            foreach (var row in rows.Skip(1))
            {
                string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                AddMovie(result, movies, Cell(title), Year(Cell(year)), Cell(type), Cell(imdb));
            }
            return;
        }

        result.Problems.Add($"{fileName}: no artist/album or title column. The first row should name the columns, like \"Artist,Album,Track\" or \"Title,Year\".");
    }

    // RFC 4180, with the separator guessed from the first line (Excel in some countries writes
    // semicolons; a copy out of a spreadsheet is tabs).
    public static List<List<string>> ReadCsv(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var separator = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == separator) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows.Where(r => r.Any(c => c.Length > 0)).ToList();
    }

    // "Artist Name(s)" -> "artistnames".
    private static string Column(string header) =>
        new(header.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // MARK: - JSON

    private static void ParseJson(string fileName, string text, Result result, Dictionary<string, AlbumBuilder> albums, Dictionary<string, Movie> movies)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = document.RootElement;
        var before = result.Tracks + result.Movies.Count + movies.Count + albums.Count;

        if (root.ValueKind == JsonValueKind.Object)
        {
            // Spotify's YourLibrary.json: liked songs and saved albums.
            if (root.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                foreach (var item in tracks.EnumerateArray()) AddJsonTrack(result, albums, item);
            if (root.TryGetProperty("albums", out var saved) && saved.ValueKind == JsonValueKind.Array)
                foreach (var item in saved.EnumerateArray())
                    AddAlbum(albums, FirstArtist(Text(item, "artist", "artistName")), Text(item, "album", "albumName", "name", "title"), null);
            // Spotify's Playlist1.json: { playlists: [ { name, items: [ { track: { trackName, artistName, albumName } } ] } ] }.
            if (root.TryGetProperty("playlists", out var playlists) && playlists.ValueKind == JsonValueKind.Array)
                foreach (var playlist in playlists.EnumerateArray())
                    if (playlist.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object) AddJsonTrack(result, albums, track);
                            else result.Unreadable++; // a podcast episode or a local file
                        }
            if (result.Tracks + albums.Count > before) result.Sources.Add("Spotify data export");
            else result.Problems.Add($"{fileName}: no songs, albums or playlists in it.");
            return;
        }

        // A plain array: Trakt-style movies ({ movie: { title, year, ids: { imdb } } }) or rows with
        // the same names as the CSV columns.
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { result.Unreadable++; continue; }
            if (item.TryGetProperty("show", out _)) { result.Shows++; continue; }
            var movie = item.TryGetProperty("movie", out var m) && m.ValueKind == JsonValueKind.Object ? m : item;
            if (Text(item, "artist", "artistName", "albumArtist").Length > 0) { AddJsonTrack(result, albums, item); continue; }
            var imdb = movie.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object ? Text(ids, "imdb") : Text(movie, "imdbId", "imdb_id", "imdb");
            var yearText = movie.TryGetProperty("year", out var y) ? y.ToString() : Text(movie, "releaseDate", "release_date");
            AddMovie(result, movies, Text(movie, "title", "name"), Year(yearText), Text(movie, "type", "titleType"), imdb);
        }
        if (result.Tracks + movies.Count + albums.Count > before) result.Sources.Add("JSON");
    }

    private static void AddJsonTrack(Result result, Dictionary<string, AlbumBuilder> albums, JsonElement item)
    {
        var artist = FirstArtist(Text(item, "albumArtist", "artist", "artistName"));
        AddTrack(result, albums, artist, Text(item, "album", "albumName"), Text(item, "track", "trackName", "title", "name"), Year(Text(item, "releaseDate", "year")));
    }

    private static string Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String) return value.GetString()!.Trim();
                if (value.ValueKind == JsonValueKind.Number) return value.ToString();
            }
        return "";
    }

    // MARK: - Collecting

    private static void AddTrack(Result result, Dictionary<string, AlbumBuilder> albums, string artist, string album, string track, int? year)
    {
        if (artist.Length == 0 && album.Length == 0 && track.Length == 0) { result.Unreadable++; return; }
        result.Tracks++;
        if (album.Length == 0) { result.NoAlbum++; return; }
        AddAlbum(albums, artist, album, year).Add(track);
    }

    private static AlbumBuilder AddAlbum(Dictionary<string, AlbumBuilder> albums, string artist, string album, int? year)
    {
        var title = CleanAlbumTitle(album);
        var key = AlbumKey(artist, title);
        if (!albums.TryGetValue(key, out var builder)) albums[key] = builder = new AlbumBuilder(artist, title);
        builder.Year ??= year;
        return builder;
    }

    private static void AddMovie(Result result, Dictionary<string, Movie> movies, string title, int? year, string type, string imdb)
    {
        if (title.Length == 0) { result.Unreadable++; return; }
        var kind = Column(type);
        if (kind.StartsWith("tvseries") || kind.StartsWith("tvminiseries") || kind is "tvshow" or "show" or "series" or "tvepisode" or "episode")
        {
            result.Shows++;
            return;
        }
        var movie = new Movie(title, year, Regex.IsMatch(imdb, @"^tt\d+$") ? imdb : null);
        movies.TryAdd(movie.Key, movie);
    }

    private sealed class AlbumBuilder(string artist, string title)
    {
        private readonly List<string> _tracks = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        public int? Year { get; set; }

        public void Add(string track)
        {
            if (track.Length > 0 && _seen.Add(track)) _tracks.Add(track);
        }

        // A single: Spotify files a one-song release under an album named after the song.
        public Album Build() => new(artist.Length == 0 ? "Various Artists" : artist, title, Year, _tracks.Count, _tracks,
            _tracks.Count == 1 && AlbumKey("", CleanAlbumTitle(_tracks[0])) == AlbumKey("", title));
    }

    // MARK: - Names

    // Exportify writes "Artist A,Artist B" (older) or "Artist A;Artist B"; Spotify's data export one
    // name. The first artist is the one the album is shared under. A name with a comma in it
    // ("Tyler, The Creator") survives when the part after the comma starts with "the".
    public static string FirstArtist(string artists)
    {
        var text = artists.Trim();
        var semicolon = text.IndexOf(';');
        if (semicolon > 0) return text[..semicolon].Trim();
        var comma = text.IndexOf(',');
        while (comma > 0)
        {
            var rest = text[(comma + 1)..].TrimStart();
            if (!rest.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) return text[..comma].Trim();
            comma = text.IndexOf(',', comma + 1);
        }
        return text;
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(remaster(ed)?|deluxe|edition|expanded|anniversary|version|bonus|mono|stereo|reissue|super|special|collector'?s?|explicit|clean)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex EditionSuffix();

    [GeneratedRegex(@"\s+-\s+(\d{4}\s+)?(remaster(ed)?|deluxe|expanded|anniversary|special|bonus)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashEditionSuffix();

    // "Rumours (Super Deluxe)" and "Abbey Road (Remastered 2019)" are shared as "Rumours" and
    // "Abbey Road": the edition words would stop the release name from matching.
    public static string CleanAlbumTitle(string album)
    {
        var cleaned = DashEditionSuffix().Replace(EditionSuffix().Replace(album.Trim(), ""), "").Trim();
        return cleaned.Length == 0 ? album.Trim() : cleaned;
    }

    public static int? Year(string text)
    {
        var match = Regex.Match(text ?? "", @"\b(18|19|20)\d{2}\b");
        return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }

    public static string AlbumKey(string artist, string album) =>
        $"{string.Join(' ', MusicReleaseRanker.Words(artist).Order(StringComparer.Ordinal))}|{string.Join(' ', MusicReleaseRanker.Words(album).Order(StringComparer.Ordinal))}";

    public static string MovieKey(string title, int? year) =>
        $"{string.Join(' ', MusicReleaseRanker.Words(title).Order(StringComparer.Ordinal))}|{year}";
}
