using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// "+ Add" on the Music tab: find an album on MusicBrainz, search the household's torrent sites
// for it through the same VPN-only stack as Movies (MediaDownloadManager does the queue, the
// VPN and qBittorrent), and file it into a music library as Artist/Album (Year)/. Contract:
// core/docs/MUSIC_AND_PODCASTS.md §4 "Getting music".

public static class MediaDownloadKinds
{
    public const string Album = "album";
}

// One torrent a site offered for an album, as the "pick a release" list shows it.
public sealed record MusicRelease(
    string Id,
    string Title,
    string? Indexer,
    long? SizeBytes,
    int? Seeders,
    int? Leechers,
    DateTimeOffset? PublishedAt,
    string? Format,
    bool Lossless,
    bool Matches,
    string? Warning,
    double Score,
    // Never sent to clients: the magnet or .torrent URL the grab uses.
    string DownloadSource,
    string? InfoHash);

public sealed class MusicDownloadSearchInput
{
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? Mbid { get; set; }
    public string? ArtUrl { get; set; }
}

public sealed class MusicDownloadGrabInput
{
    public Guid SearchId { get; set; }
    public string ReleaseId { get; set; } = string.Empty;
    public Guid? LibraryId { get; set; }
    // An album already in the library: null asks (409 alreadyInLibrary), true downloads it again
    // anyway (a better copy, say).
    public bool? DownloadAgain { get; set; }
}

// Reads what a music torrent's name says about it, and ranks releases for one album. Pure, for
// the tests: "Fleetwood Mac - Rumours (1977) [FLAC 24-96]" -> FLAC 24-bit, lossless.
public static partial class MusicReleaseRanker
{
    public sealed record Parsed(string? Format, bool Lossless, bool Discography, bool Single, bool Deluxe);

    [GeneratedRegex(@"\b(24[\s\-]?bit|24[\s\-/]?(44|48|88|96|176|192)|hi[\s\-]?res)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HiResPattern();

    [GeneratedRegex(@"\b(discography|discografia|complete\s+(studio\s+)?albums|collection\s+\d{4}\s*-\s*\d{4}|\d+\s+albums)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiscographyPattern();

    [GeneratedRegex(@"\b(single|\(single\)|\[single\])\b", RegexOptions.IgnoreCase)]
    private static partial Regex SinglePattern();

    [GeneratedRegex(@"\b(deluxe|expanded|anniversary|remaster(ed)?|special\s+edition|bonus)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeluxePattern();

    [GeneratedRegex(@"\b320\b|320\s?kbps", RegexOptions.IgnoreCase)]
    private static partial Regex Mp3_320Pattern();

    [GeneratedRegex(@"\bV0\b|\bVBR\b", RegexOptions.IgnoreCase)]
    private static partial Regex Mp3V0Pattern();

    public static Parsed Parse(string title)
    {
        string? format = null;
        var lossless = false;
        if (Regex.IsMatch(title, @"\bFLAC\b", RegexOptions.IgnoreCase))
        {
            format = HiResPattern().IsMatch(title) ? "FLAC 24-bit" : "FLAC";
            lossless = true;
        }
        else if (Regex.IsMatch(title, @"\bALAC\b", RegexOptions.IgnoreCase)) { format = "ALAC"; lossless = true; }
        else if (Regex.IsMatch(title, @"\b(WAV|APE|WavPack|DSD|DSF)\b", RegexOptions.IgnoreCase)) { format = "Lossless"; lossless = true; }
        else if (Regex.IsMatch(title, @"\bMP3\b", RegexOptions.IgnoreCase) || Mp3_320Pattern().IsMatch(title) || Mp3V0Pattern().IsMatch(title))
            format = Mp3_320Pattern().IsMatch(title) ? "MP3 320" : Mp3V0Pattern().IsMatch(title) ? "MP3 V0" : "MP3";
        else if (Regex.IsMatch(title, @"\b(AAC|M4A)\b", RegexOptions.IgnoreCase)) format = "AAC";
        else if (Regex.IsMatch(title, @"\b(OGG|Vorbis|Opus)\b", RegexOptions.IgnoreCase)) format = "OGG";

        return new Parsed(format, lossless, DiscographyPattern().IsMatch(title), SinglePattern().IsMatch(title), DeluxePattern().IsMatch(title));
    }

    // Both the artist's and the album's words appear in the release name (ignoring case, accents,
    // punctuation and "the"/"and"). "Various Artists" albums only need the album.
    public static bool Matches(string releaseTitle, string artist, string album)
    {
        var haystack = Words(releaseTitle);
        bool Covered(string text)
        {
            var words = Words(text).Where(w => w is not ("the" or "and" or "a")).ToList();
            return words.Count == 0 || words.All(haystack.Contains);
        }
        var various = artist.Trim().Equals("Various Artists", StringComparison.OrdinalIgnoreCase) || artist.Trim().Length == 0;
        return Covered(album) && (various || Covered(artist));
    }

    public static string? Warning(Parsed parsed, int? seeders)
    {
        if (parsed.Discography) return "Discography: much more than this album.";
        if (parsed.Single) return "Looks like a single, not the album.";
        if (seeders is 0) return "Nobody is sharing it right now.";
        if (parsed.Format is null) return "Doesn't say what format it is.";
        return null;
    }

    // Higher is better. `preferLossless` when most of the library already is.
    public static double Score(Parsed parsed, bool matches, int? seeders, long? size, bool preferLossless)
    {
        double score = matches ? 100 : 0;
        score += parsed.Format switch
        {
            "FLAC 24-bit" => preferLossless ? 28 : 6,
            "FLAC" or "ALAC" or "Lossless" => preferLossless ? 30 : 12,
            "MP3 320" => preferLossless ? 12 : 30,
            "MP3 V0" => preferLossless ? 10 : 26,
            "AAC" => preferLossless ? 8 : 18,
            "MP3" => 8,
            "OGG" => 6,
            _ => 0
        };
        if (parsed.Discography) score -= 60;
        if (parsed.Single) score -= 40;
        // A whole album is tens to hundreds of megabytes; a few MB is a single or a fake, and
        // tens of GB is a discography whatever it says.
        if (size is { } bytes)
        {
            if (bytes < 15_000_000) score -= 30;
            else if (bytes > 6_000_000_000) score -= 30;
        }
        score += Math.Min(40, Math.Log2(1 + Math.Max(0, seeders ?? 0)) * 6);
        return score;
    }

    public static HashSet<string> Words(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : c == '&' ? ' ' : ' ');
        }
        return builder.ToString().Replace(" and ", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }
}

// Files a finished album download as <library>/<Artist>/<Album> (<Year>)/, keeping the
// release's own disc folders (CD1, Disc 2) and dropping everything that is not music, a cue
// sheet, a rip log, lyrics or a picture. Planning is pure for the tests.
public static partial class MusicDownloadImporter
{
    private static readonly HashSet<string> KeptExtras = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cue", ".log", ".lrc", ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    [GeneratedRegex(@"^(cd|disc|disk|dvd)\s*[-_.]?\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolderPattern();

    public static List<MediaDownloadImporter.Move> Plan(MediaDownload download, IReadOnlyList<MediaDownloadImporter.SourceFile> files, string stagingRoot, string libraryPath)
    {
        var moves = new List<MediaDownloadImporter.Move>();
        var audio = files.Where(f => AudioFileTypes.IsAudioFile(f.RelativePath) && !IsJunk(f.RelativePath)).ToList();
        if (audio.Count == 0) return moves;

        var artist = MediaDownloadImporter.SafeName(string.IsNullOrWhiteSpace(download.Artist) ? "Unknown Artist" : download.Artist!);
        var albumName = MediaDownloadImporter.SafeName(download.Year is { } year ? $"{download.Title} ({year})" : download.Title);
        var albumFolder = Path.Combine(libraryPath, artist, albumName);
        // A second copy of an album already in the library goes beside it, not over it.
        for (var n = 2; Directory.Exists(albumFolder) && Directory.EnumerateFileSystemEntries(albumFolder).Any(); n++)
            albumFolder = Path.Combine(libraryPath, artist, $"{albumName} ({n})");

        // The folder the music sits in inside the torrent: everything is placed relative to it,
        // so "Rumours [FLAC]/CD1/01.flac" lands as ".../Rumours (1977)/CD1/01.flac".
        var root = CommonFolder(audio.Select(f => f.RelativePath).ToList());
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.RelativePath);
            var isAudio = AudioFileTypes.IsAudioFile(file.RelativePath);
            if (!isAudio && !KeptExtras.Contains(extension)) continue;
            if (IsJunk(file.RelativePath)) continue;
            var relative = root.Length == 0 ? file.RelativePath : file.RelativePath.StartsWith(root + "/", StringComparison.Ordinal) || file.RelativePath.StartsWith(root + "\\", StringComparison.Ordinal)
                ? file.RelativePath[(root.Length + 1)..]
                : null;
            if (relative is null) continue;
            var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            // Only disc folders survive as folders; anything deeper (a "Scans" folder, artwork
            // folders) is flattened to the album folder or, for non-music, skipped.
            string destination;
            if (parts.Length == 1) destination = Path.Combine(albumFolder, MediaDownloadImporter.SafeName(parts[0]));
            else if (parts.Length == 2 && DiscFolderPattern().IsMatch(parts[0]))
                destination = Path.Combine(albumFolder, MediaDownloadImporter.SafeName(parts[0]), MediaDownloadImporter.SafeName(parts[1]));
            else if (isAudio) destination = Path.Combine(albumFolder, MediaDownloadImporter.SafeName(string.Join(" - ", parts)));
            else continue;
            moves.Add(new MediaDownloadImporter.Move(Path.Combine(stagingRoot, file.RelativePath), destination));
        }
        return moves;
    }

    // Moves the planned files and returns the audio files that landed. Never overwrites.
    public static List<string> Execute(IReadOnlyList<MediaDownloadImporter.Move> moves)
    {
        var landed = new List<string>();
        foreach (var move in moves)
        {
            if (!File.Exists(move.Source) || File.Exists(move.Destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
            File.Move(move.Source, move.Destination);
            if (AudioFileTypes.IsAudioFile(move.Destination)) landed.Add(move.Destination);
        }
        return landed;
    }

    private static bool IsJunk(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.StartsWith("._", StringComparison.Ordinal) || name.StartsWith('.')
            || relativePath.Split(['/', '\\']).Any(p => p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase));
    }

    private static string CommonFolder(IReadOnlyList<string> paths)
    {
        var folders = paths.Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "").ToList();
        // Disc folders are part of the album, not its root.
        var stripped = folders.Select(f =>
        {
            var last = f.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return last is not null && DiscFolderPattern().IsMatch(last) ? f[..Math.Max(0, f.Length - last.Length)].TrimEnd('/') : f;
        }).ToList();
        var common = stripped[0];
        foreach (var folder in stripped.Skip(1))
        {
            while (common.Length > 0 && !(folder == common || folder.StartsWith(common + "/", StringComparison.Ordinal)))
            {
                var cut = common.LastIndexOf('/');
                common = cut < 0 ? "" : common[..cut];
            }
        }
        return common;
    }
}

public sealed record MusicLookupResult(string Mbid, string Title, string Artist, int? Year, string? Type, int? TrackCount, string ArtUrl, bool InLibrary);

// MusicBrainz (musicbrainz.org, free, no key) for "which album did you mean": release groups
// matching what was typed, each with a Cover Art Archive picture. MusicBrainz asks for a
// descriptive User-Agent and at most one request a second, which this keeps to.
public sealed class MusicDownloadLookup(IHttpClientFactory httpClientFactory, ILogger<MusicDownloadLookup> logger)
{
    public const string HttpClientName = "MusicDownloadLookup";
    private const string ApiBase = "https://musicbrainz.org/ws/2/";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<(string Mbid, string Title, string Artist, int? Year, string? Type, int? TrackCount)>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        // Lucene syntax would turn "AC/DC" or "Sgt. Pepper's" into errors; plain words search all
        // fields, which is what a person typing "fleetwood mac rumours" wants.
        var cleaned = Regex.Replace(query, @"[+\-&|!(){}\[\]^""~*?:\\/]", " ").Trim();
        var url = $"{ApiBase}release-group?fmt=json&limit=15&query={Uri.EscapeDataString(cleaned)}";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastRequest + TimeSpan.FromSeconds(1.1) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastRequest = DateTimeOffset.UtcNow;
            using var document = await httpClientFactory.CreateClient(HttpClientName).GetFromJsonAsync<JsonDocument>(url, cancellationToken)
                ?? throw new MediaDownloadException("MusicBrainz sent back nothing.");
            var results = new List<(string, string, string, int?, string?, int?)>();
            if (!document.RootElement.TryGetProperty("release-groups", out var groups)) return results;
            foreach (var group in groups.EnumerateArray())
            {
                var id = group.TryGetProperty("id", out var i) ? i.GetString() : null;
                var title = group.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (id is null || title is null) continue;
                var artist = group.TryGetProperty("artist-credit", out var credits)
                    ? string.Concat(credits.EnumerateArray().Select(c =>
                        (c.TryGetProperty("name", out var n) ? n.GetString() : null) + (c.TryGetProperty("joinphrase", out var j) ? j.GetString() : "")))
                    : "";
                int? year = group.TryGetProperty("first-release-date", out var date) && date.GetString() is { Length: >= 4 } d
                    && int.TryParse(d[..4], out var y) ? y : null;
                var primary = group.TryGetProperty("primary-type", out var p) ? p.GetString() : null;
                var secondary = group.TryGetProperty("secondary-types", out var s) ? s.EnumerateArray().Select(x => x.GetString()).FirstOrDefault() : null;
                int? tracks = group.TryGetProperty("releases", out var releases)
                    ? releases.EnumerateArray().Select(r => r.TryGetProperty("track-count", out var tc) && tc.TryGetInt32(out var count) ? count : (int?)null).FirstOrDefault(c => c is not null)
                    : null;
                results.Add((id, title, artist, year, secondary ?? primary, tracks));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("MusicBrainz search for {Query} failed: {Message}", query, ex.Message);
            throw new MediaDownloadException("Couldn't reach MusicBrainz to look that up. Try again in a moment.");
        }
        finally { _gate.Release(); }
    }

    public static string ArtUrl(string mbid) => $"https://coverartarchive.org/release-group/{mbid}/front-250";
}
