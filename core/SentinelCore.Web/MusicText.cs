using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// Text rules the Music tab leans on everywhere: what makes two names the same name, how search
// matches, how a file name says what it is when the tags do not, and how an artist credit is
// split into artists.
public static partial class MusicText
{
    public const string VariousArtists = "Various Artists";
    public const string UnknownArtist = "Unknown Artist";
    public const string UnknownAlbum = "Unknown Album";

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".alac", ".ogg", ".oga", ".opus", ".wav", ".aif", ".aiff",
        ".wma", ".ape", ".wv", ".dsf", ".dff"
    };

    public static bool IsAudioFile(string fileName) => AudioExtensions.Contains(System.IO.Path.GetExtension(fileName));

    // Hidden folders, the Windows recycle bin and the junk NAS boxes leave behind.
    public static bool IsSkippedFolder(string name) =>
        name.StartsWith('.') || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
        || name is "System Volume Information" or "@eaDir" or "#recycle" or "#snapshot";

    // macOS's "._Song.mp3" resource-fork twins look like audio files and are not.
    public static bool IsSkippedFile(string name) => name.StartsWith("._", StringComparison.Ordinal) || name.StartsWith('.');

    // MARK: - Names

    // What two names have to share to be the same artist or album: case, accents and spacing
    // do not count. "Beyoncé" and "Beyonce" are one artist.
    public static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var folded = Fold(text);
        return Whitespace().Replace(folded, " ").Trim();
    }

    // Lower-case with the accents taken off.
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC)
            .Replace('ß', 's').Replace('ø', 'o').Replace('æ', 'a').Replace('œ', 'o').Replace('ł', 'l').Replace('đ', 'd');
    }

    // "The Beatles" sorts under B, like every music app does.
    public static string SortKey(string text)
    {
        var key = Key(text);
        foreach (var article in new[] { "the ", "a ", "an " })
            if (key.StartsWith(article, StringComparison.Ordinal) && key.Length > article.Length)
                return key[article.Length..];
        return key;
    }

    // MARK: - Search

    // The words of a text, folded: what a search query's words have to be the start of.
    public static string[] Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : NonWord().Split(Fold(text)).Where(w => w.Length > 0).ToArray();

    // Every query word has to start one of the text's words: "flee mac" finds Fleetwood Mac,
    // "eetwood" does not, and "beyonce" finds Beyoncé.
    public static bool Matches(string[] queryWords, IEnumerable<string> words)
    {
        if (queryWords.Length == 0) return false;
        var list = words as IReadOnlyCollection<string> ?? words.ToList();
        foreach (var query in queryWords)
        {
            var found = false;
            foreach (var word in list)
                if (word.StartsWith(query, StringComparison.Ordinal)) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    // MARK: - Artist credits

    // Groups whose names contain " & " that must never be split into two artists. A library's
    // own album artists are added to this at scan time, so whatever band the household has is
    // safe as long as one of its albums is tagged with an album artist.
    private static readonly HashSet<string> KnownBands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Simon & Garfunkel", "Hall & Oates", "Daryl Hall & John Oates", "Earth, Wind & Fire", "Crosby, Stills & Nash",
        "Crosby, Stills, Nash & Young", "Brooks & Dunn", "Big & Rich", "Ike & Tina Turner", "Peaches & Herb", "Sam & Dave",
        "Captain & Tennille", "Ashford & Simpson", "Iron & Wine", "Chase & Status", "Above & Beyond", "Matt & Kim",
        "Sonny & Cher", "Jan & Dean", "Chad & Jeremy", "Peter & Gordon", "Seals & Crofts", "England Dan & John Ford Coley",
        "Loggins & Messina", "Zager & Evans", "Of Monsters & Men", "Mumford & Sons", "She & Him", "Angus & Julia Stone",
        "Johnnie & Jack", "Les Paul & Mary Ford", "Ferrante & Teicher", "Flatt & Scruggs", "Marshall & Lester",
        "Delaney & Bonnie", "Sly & Robbie", "Eric B. & Rakim", "Belle & Sebastian", "Bell & James", "Chas & Dave"
    };

    // Splits a track's artist credit into the artists it names: on ";", " feat. ", " ft. ",
    // " featuring " and " & ", never inside a known band ("Simon & Garfunkel") and never before
    // "& the" ("Tom Petty & the Heartbreakers" is one act). The first is the primary artist.
    public static List<string> SplitArtists(string? credit, IReadOnlySet<string>? libraryBands = null)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(credit)) return result;
        void Add(string name)
        {
            var trimmed = name.Trim().Trim(',').Trim();
            if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) result.Add(trimmed);
        }
        if (IsBand(credit.Trim(), libraryBands)) { Add(credit); return result; }
        foreach (var part in Featuring().Split(credit))
        foreach (var piece in part.Split(';'))
        {
            var name = piece.Trim();
            if (name.Length == 0) continue;
            if (IsBand(name, libraryBands) || !name.Contains(" & ", StringComparison.Ordinal)) { Add(name); continue; }
            // Split on " & " except where the next words are "the ...", which names a backing band.
            var start = 0;
            foreach (Match match in Ampersand().Matches(name))
            {
                var rest = name[(match.Index + match.Length)..];
                if (rest.StartsWith("the ", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("his ", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("her ", StringComparison.OrdinalIgnoreCase)) continue;
                var left = name[start..match.Index];
                if (left.Trim().Length > 0) Add(left);
                start = match.Index + match.Length;
            }
            Add(name[start..]);
        }
        return result;
    }

    private static bool IsBand(string name, IReadOnlySet<string>? libraryBands) =>
        KnownBands.Contains(name) || (libraryBands?.Contains(name) ?? false);

    // MARK: - File names

    // What a file with no tags says about itself: "01 - Song" is track 1, "Song"; "1-03 Song" is
    // disc 1, track 3.
    public static (int? Disc, int? Track, string Title) ParseFileName(string fileNameWithoutExtension)
    {
        var name = fileNameWithoutExtension.Replace('_', ' ').Trim();
        if (DiscTrackPrefix().Match(name) is { Success: true } discTrack)
            return (int.Parse(discTrack.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(discTrack.Groups[2].Value, CultureInfo.InvariantCulture), Tidy(discTrack.Groups[3].Value, name));
        if (TrackPrefix().Match(name) is { Success: true } track)
            return (null, int.Parse(track.Groups[1].Value, CultureInfo.InvariantCulture), Tidy(track.Groups[2].Value, name));
        return (null, null, name);
    }

    private static string Tidy(string title, string fallback)
    {
        var tidy = Whitespace().Replace(title, " ").Trim(' ', '-', '.', '_');
        return tidy.Length == 0 ? fallback : tidy;
    }

    // A "CD1" / "Disc 2" folder is part of the album above it, not an album called "CD1".
    public static bool IsDiscFolder(string name) => DiscFolder().IsMatch(name.Trim());

    public static int? DiscNumberOf(string folderName) =>
        DiscFolder().Match(folderName.Trim()) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    // "2007-03-12" or "1977" or "1977/01/01" -> 1977.
    public static int? ParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = YearDigits().Match(text);
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 1000 and < 3000
            ? year
            : null;
    }

    // "3/12" -> 3.
    public static int? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = LeadingDigits().Match(text.Trim());
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    // "-6.20 dB" -> -6.2.
    public static double? ParseGain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = SignedDecimal().Match(text);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }

    // A genre tag can hold several ("Rock; Pop"), and ID3v1 numbers come through as "(17)".
    public static List<string> SplitGenres(IEnumerable<string?> tags)
    {
        var genres = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            foreach (var piece in tag.Split([';', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var genre = Whitespace().Replace(piece, " ").Trim();
                if (genre.Length == 0 || NumericGenre().IsMatch(genre)) continue;
                if (!genres.Contains(genre, StringComparer.OrdinalIgnoreCase)) genres.Add(genre);
            }
        }
        return genres;
    }

    // The album without its edition: "Rumours (Deluxe Edition)", "Rumours [2004 Remaster]" and
    // "Rumours - Remastered" are all "rumours", so a list or a download never fetches an album
    // again because the copy here came out under another edition's name. Only brackets and dash
    // tails made of edition words go: "Greatest Hits Vol. 2" and "Live at Leeds" stay themselves.
    public static string EditionlessKey(string? title)
    {
        var key = Key(title);
        if (key.Length == 0) return key;
        string before;
        do
        {
            before = key;
            key = EditionTail().Replace(key, "").Trim();
        }
        while (key != before && key.Length > 0);
        return key.Length == 0 ? Key(title) : key;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A trailing "(...)" / "[...]" / " - ..." whose every word is an edition word or a year.
    [GeneratedRegex(@"\s*(?:[\(\[]\s*(?:(?:deluxe|edition|remaster|remastered|remasters|expanded|anniversary|explicit|clean|bonus|tracks?|special|collectors?|collector's|version|reissue|mono|stereo|club|limited|super|legacy|definitive|complete|ultimate|platinum|international|japan|japanese|uk|us|eu|single|ep|lp|album|digital|hd|hi-res|24-bit|24bit|edit|re-release|anniversary|th|st|nd|rd|\d{1,4}(?:th|st|nd|rd)?|-|&|and|the|of|cd|disc|\d)\s*)+[\)\]]|\s+-\s+(?:(?:deluxe|edition|remaster|remastered|expanded|anniversary|explicit|special|version|reissue|mono|stereo|single|ep|\d{4})\s*)+)$")]
    private static partial Regex EditionTail();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.|featuring)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s+&\s+")]
    private static partial Regex Ampersand();

    [GeneratedRegex(@"^(\d)[-.](\d{1,3})(?:\s*[-.]\s*|\s+)(.+)$")]
    private static partial Regex DiscTrackPrefix();

    [GeneratedRegex(@"^(\d{1,3})(?:\s*[-.)]\s*|\s+)(.+)$")]
    private static partial Regex TrackPrefix();

    [GeneratedRegex(@"^(?:cd|disc|disk)\s*[-_]?\s*(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolder();

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex YearDigits();

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingDigits();

    [GeneratedRegex(@"[-+]?\d+(?:\.\d+)?")]
    private static partial Regex SignedDecimal();

    [GeneratedRegex(@"^\(?\d+\)?$")]
    private static partial Regex NumericGenre();
}
