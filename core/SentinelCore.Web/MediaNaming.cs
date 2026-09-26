using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// Turns the file and folder names a media library actually has into titles a person (and TMDB)
// would recognise. The names in a real collection are what a download left behind -
// "17.Again.2009.1080p.BrRip.x264.YIFY.mp4", "Black Mirror (2011) - S01E01 - The National Anthem
// (1080p BluRay x265 Silence).mkv", "Bobs.Burgers.S07.1080p.WEB-DL.DD5.1.H.264" - so this is a set
// of heuristics, kept pure so every one of them is pinned by MediaNamingTests.
public static partial class MediaNaming
{
    public sealed record ParsedTitle(string Title, int? Year);

    // `ShowName` is whatever came before the episode marker ("Star Wars The Clone Wars" in "Star
    // Wars The Clone Wars S01E01 Ambush"), cleaned - often a better show title than the folder's.
    public sealed record ParsedEpisode(int? Season, int? Episode, string? Title, ParsedTitle? ShowName = null);

    // A year is only a year when it stands alone: "2012 (2009)" is the film 2012 from 2009, and
    // "1080p" or "x264" must never read as one.
    [GeneratedRegex(@"(?<![\dA-Za-z])(19[2-9]\d|20[0-4]\d)(?![\dA-Za-z])")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketGroup();

    // Release tags that are safe to cut a title at wherever they appear. Deliberately without
    // bare "web", "ts", "cam" or "dv", any of which is a word a title can contain.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:480p|576p|720p|1080p|1080i|2160p|4k|uhd|hdr10\+?|hdr|bluray|blu-ray|bdrip|brrip|bd-rip|dvdrip|dvd-rip|dvd|webrip|web-rip|web-dl|webdl|hdtv|hdrip|hdcam|telesync|x264|x265|h264|h265|h[ .]264|h[ .]265|hevc|avc|xvid|divx|aac(?:2\.0|5\.1|2 0|5 1)?|ac3|eac3|dd5(?:\.1| 1)?|ddp5(?:\.1| 1)?|ddp|dts(?:-hd)?|truehd|atmos|10bit|10-bit|8bit|remastered|remaster|proper|repack|rerip|extended|unrated|uncut|theatrical|imax|limited|internal|readnfo|multi|dual|dubbed|subbed|yify|yts(?:\.mx|\.ag|\.lt)?|rarbg|ettv|eztv|mkvcage|amzn|dsnp|hmax|hulu|complete|seasons?(?:[ ._-]?\d{1,2})?|s\d{1,2}(?:[ ._-]?-[ ._-]?s?\d{1,2})?|s\d{1,2}[ ._-]?e\d{1,3}|\d{1,2}x\d{2,3})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex TagToken();

    // Extra tokens that only an episode title is allowed to be cut at - after "S01E01" nothing
    // ordinary starts with REAL or WEB, but a film called "Charlotte's Web" must survive.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:real|web|nf|hdr|dv|ts|cam|sdr|avi|mkv|mp4|mpg)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnlyTag();

    // Also "S01E01E02" (a double episode - the first number wins) and "s1e01b" (a half-episode
    // segment, as cartoons are cut: the letter is dropped and both halves keep the number).
    [GeneratedRegex(@"(?<![A-Za-z0-9])S(\d{1,2})[ ._-]?E(\d{1,3})(?:[ ._-]?-?[ ._-]?E(\d{1,3}))?[a-dx]?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodePattern();

    // A "[1x05]" in brackets would be thrown away with the other bracketed tags before the
    // episode patterns ever saw it; this unwraps it first.
    [GeneratedRegex(@"\[\s*(\d{1,2}x\d{2,3}|S\d{1,2}[ ._-]?E\d{1,3})\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedEpisodeMarker();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(\d{1,2})x(\d{2,3})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CrossPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:E|Ep|Episode)[ ._-]?(\d{1,3})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnlyPattern();

    [GeneratedRegex(@"^(?:season|series|s)[ ._-]?(\d{1,2})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderPattern();

    [GeneratedRegex(@"^specials?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsFolderPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*(?:www\.[A-Za-z0-9-]+\.[A-Za-z]{2,4}|[A-Za-z0-9-]+\.(?:com|org|net|to|mx|me|cc|info|lt|ag))(?![A-Za-z0-9])\s*-\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SitePrefix();

    [GeneratedRegex(@"(?<=[\p{L}\p{N}])\.(?=[\p{L}\p{N}])")]
    private static partial Regex DotBetweenWords();

    [GeneratedRegex(@"^0\d[ ._-]+(?=\p{L})")]
    private static partial Regex TrackNumber();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "and", "of" };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".webm", ".flv"
    };

    public static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "metadata", "extras", "samples", "sample", "featurettes", "trailers", ".trickplay", "lost+found"
    };

    public static bool IsVideoFile(string fileName) => VideoExtensions.Contains(System.IO.Path.GetExtension(fileName));

    public static bool IsSkippedFolder(string folderName) =>
        folderName.StartsWith('.') || SkippedFolders.Contains(folderName) || folderName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase);

    // Samples and trailers sit next to the film they belong to and would otherwise show up as a
    // second copy of it with a two-minute runtime.
    public static bool IsSkippedFile(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return stem.StartsWith('.')
            || Regex.IsMatch(stem, @"(?<![A-Za-z0-9])(sample|trailer)(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
    }

    // "17.Again.2009.1080p.BrRip.x264.YIFY" -> ("17 Again", 2009). Works for a file name (without
    // its extension) and for a folder name alike.
    public static ParsedTitle ParseTitle(string name)
    {
        var text = Prepare(name);
        int? year = null;

        var yearMatches = YearPattern().Matches(text);
        Match? chosen = null;
        foreach (Match candidate in yearMatches)
        {
            // A year at the very start is the title ("2012"), unless another year follows it.
            if (candidate.Index == 0 && yearMatches.Count > 1) continue;
            if (candidate.Index == 0) break;
            chosen = candidate;
            break;
        }

        if (chosen is not null)
        {
            year = int.Parse(chosen.Value);
            text = text[..chosen.Index];
        }

        text = CutAtTags(text, TagToken());
        var title = Tidy(text);
        if (title.Length == 0) title = Tidy(Prepare(name));
        if (title.Length == 0) title = name.Trim();
        return new ParsedTitle(title, year);
    }

    // The season and episode of a file inside a show, and the episode's own title when the file
    // name carries one: "Black Mirror (2011) - S01E01 - The National Anthem (1080p BluRay x265
    // Silence)" -> (1, 1, "The National Anthem"). `folderSeason` is what the containing "Season 3"
    // folder said, used when the file name names only the episode.
    public static ParsedEpisode ParseEpisode(string fileNameWithoutExtension, int? folderSeason)
    {
        var text = Prepare(BracketedEpisodeMarker().Replace(fileNameWithoutExtension, " $1 "));

        var match = SeasonEpisodePattern().Match(text);
        if (match.Success)
            return new ParsedEpisode(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                EpisodeTitle(text[(match.Index + match.Length)..]), ShowNameBefore(text, match.Index));

        match = CrossPattern().Match(text);
        if (match.Success)
            return new ParsedEpisode(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                EpisodeTitle(text[(match.Index + match.Length)..]), ShowNameBefore(text, match.Index));

        match = EpisodeOnlyPattern().Match(text);
        if (match.Success)
            return new ParsedEpisode(folderSeason ?? 1, int.Parse(match.Groups[1].Value),
                EpisodeTitle(text[(match.Index + match.Length)..]), ShowNameBefore(text, match.Index));

        return new ParsedEpisode(folderSeason, null, null);
    }

    private static ParsedTitle? ShowNameBefore(string text, int index)
    {
        if (index == 0) return null;
        var parsed = ParseTitle(text[..index]);
        return parsed.Title.Length >= 2 ? parsed : null;
    }

    // The title of an episode with no number in its name - old cartoons, a folder of specials:
    // "Mickey Mouse 1928 Plane Crazy" in "Mickey Mouse" is "Plane Crazy (1928)". The show's name
    // at the front is dropped, a year right after it is kept as a suffix, and release tags go.
    public static string UnnumberedEpisodeTitle(string fileNameWithoutExtension, string showTitle)
    {
        var text = Prepare(fileNameWithoutExtension);
        var showWords = Prepare(showTitle).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Folder names like "Disney - Mickey Mouse" put a label before the name the files use, so
        // try the show title's tail as well as the whole of it.
        for (var skip = 0; skip < showWords.Length; skip++)
        {
            var candidate = string.Join(' ', showWords[skip..]).Trim(' ', '-');
            if (candidate.Length < 3) break;
            if (NormalizeKey(text).StartsWith(NormalizeKey(candidate), StringComparison.Ordinal))
            {
                text = DropLeadingWords(text, candidate);
                break;
            }
        }

        text = text.TrimStart(' ', '-', '–', '.', ',');
        int? year = null;
        var leadingYear = Regex.Match(text, @"^(19[2-9]\d|20[0-4]\d)(?![\dA-Za-z])");
        if (leadingYear.Success && text.Length > leadingYear.Length + 1)
        {
            year = int.Parse(leadingYear.Value);
            text = text[leadingYear.Length..];
        }
        var title = Tidy(CutAtTags(text, TagToken()));
        if (title.Length == 0) title = Tidy(Prepare(fileNameWithoutExtension));
        return year is { } y ? $"{title} ({y})" : title;
    }

    // Removes as many leading words from `text` as it takes to consume `prefix`, compared
    // ignoring punctuation and case ("Schoolhouse Rock - America Rock" minus "schoolhouse rock").
    private static string DropLeadingWords(string text, string prefix)
    {
        var target = NormalizeKey(prefix);
        var consumed = "";
        var index = 0;
        while (index < text.Length && consumed.Length < target.Length)
        {
            var c = text[index];
            if (char.IsLetterOrDigit(c)) consumed += char.ToLowerInvariant(c);
            index++;
        }
        return text[index..];
    }

    // What to ask TMDB for, best guess first. Boxed sets name files "John Wick Chapter 1 - Keanu
    // Reeves" or "Tremors - Comedy Horror"; the part before the dash finds the film when the whole
    // does not, but is only tried second because "Mission Impossible - Fallout" needs its tail.
    public static IReadOnlyList<string> SearchCandidates(string title)
    {
        var candidates = new List<string> { title };
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 1) candidates.Add(title[..dash].Trim());
        var ampersand = title.Replace(" and ", " & ", StringComparison.OrdinalIgnoreCase);
        if (ampersand != title) candidates.Add(ampersand);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // "Season 1 (1080p Mixed)" -> 1, "Specials" -> 0, anything else -> null.
    public static int? ParseSeasonFolder(string folderName)
    {
        var name = folderName.Trim();
        if (SpecialsFolderPattern().IsMatch(name)) return 0;
        var match = SeasonFolderPattern().Match(name);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    // What two folder names have to share to be the same show: "Bob's Burgers S01-S06" and
    // "Bobs.Burgers.S07" both come here as "bobsburgers".
    public static string NormalizeKey(string title) => NonAlphanumeric().Replace(title.ToLowerInvariant(), "");

    // Lower-case, articles moved to the end, so "The Simpsons" sorts under S like every shelf does.
    public static string SortTitle(string title)
    {
        var lower = title.Trim().ToLowerInvariant();
        foreach (var article in new[] { "the ", "a ", "an " })
            if (lower.StartsWith(article, StringComparison.Ordinal) && lower.Length > article.Length)
                return lower[article.Length..] + ", " + lower[..(article.Length - 1)];
        return lower;
    }

    // A stable id from what the item is, not when it was found, so a rescan - or a restore of a
    // backup onto a rebuilt server - lands on the same ids and everyone's watched state survives.
    public static Guid StableId(Guid libraryId, string discriminator)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{libraryId:N}|{discriminator}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Prepare(string name)
    {
        // "www.torrenting.com - Fiddler.on.the.Roof": the site that shared it, not the film.
        var text = SitePrefix().Replace(name, "");
        text = BracketGroup().Replace(text, " ").Trim();
        // Scene names use dots as spaces. A dot between two letters or digits is one; a dot
        // followed by a space is punctuation and stays (Mr. Rogers).
        text = DotBetweenWords().Replace(text, " ");
        text = text.Replace('_', ' ');
        // "01 John Wick Chapter 1" - a position in a boxed set. Only zero-padded numbers, since
        // "17 Again" and "21 Jump Street" start with digits that are the title.
        text = TrackNumber().Replace(text, "");
        return Whitespace().Replace(text, " ").Trim();
    }

    // Cut at the first release tag that leaves a real title behind. "The Complete Donald Duck
    // Collection" must not become "The" just because "Complete" is usually a tag.
    private static string CutAtTags(string text, Regex tags)
    {
        foreach (Match match in tags.Matches(text))
        {
            var prefix = Tidy(text[..match.Index]);
            if (prefix.Length >= 2 && !IsOnlyStopwords(prefix)) return prefix;
        }
        return text;
    }

    private static bool IsOnlyStopwords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(Stopwords.Contains);

    private static string? EpisodeTitle(string rest)
    {
        var text = rest;
        // Parenthesised groups after the episode number are tags ("(1080p BluRay x265 Silence)").
        text = Regex.Replace(text, @"\([^)]*\)", " ");
        text = CutAtTags(text, TagToken());
        text = CutAtTags(text, EpisodeOnlyTag());
        var title = Tidy(text);
        // What CutAtTags refused to cut down to (nothing but tags, "REAL WEB") is not a title either.
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.All(word => TagToken().IsMatch(word) || EpisodeOnlyTag().IsMatch(word))) return null;
        return title;
    }

    private static string Tidy(string text)
    {
        var tidy = Whitespace().Replace(text, " ").Trim();
        // Cutting at the year can leave an opening bracket with nothing to close it: "Its A
        // Wonderful Life { 1946 }" or "Jeremiah Johnson (Western 1972)". Everything from an
        // unmatched opener on belonged to the tags.
        foreach (var (open, close) in new[] { ('(', ')'), ('{', '}'), ('[', ']') })
        {
            if (tidy.Count(c => c == open) > tidy.Count(c => c == close))
                tidy = tidy[..tidy.LastIndexOf(open)];
        }
        tidy = tidy.Trim(' ', '-', '–', '(', ')', '[', ']', '{', '}', ',', '.');
        tidy = Whitespace().Replace(tidy, " ").Trim();
        // "Title -" or "- Title" left behind by the separators scene names use.
        while (tidy.EndsWith(" -", StringComparison.Ordinal)) tidy = tidy[..^2].TrimEnd();
        while (tidy.StartsWith("- ", StringComparison.Ordinal)) tidy = tidy[2..].TrimStart();
        return tidy;
    }
}
