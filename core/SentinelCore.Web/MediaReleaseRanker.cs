using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// Reads a torrent's release name ("Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.DV.HDR.H.265-FLUX")
// for what a person choosing between twenty of them wants to know - quality, codec, source,
// whether it is really the film or episode asked for - and orders them best first. Pure, so every
// rule is pinned by MediaReleaseRankerTests.
public static partial class MediaReleaseRanker
{
    public sealed record Request(string Kind, string Title, int? Year, int? Season, int? Episode, string PreferredQuality);

    public sealed record Parsed(string? Quality, string? Codec, string? Source, bool Hdr, int? Season, int? Episode, bool SeasonPack, bool MultiSeason, string Title, int? Year);

    private static readonly string[] QualityOrder = ["480p", "720p", "1080p", "2160p"];

    [GeneratedRegex(@"(?<![A-Za-z0-9])(2160p|4k|uhd|1080p|1080i|720p|576p|480p)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QualityPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(x265|h[ .]?265|hevc|x264|h[ .]?264|avc|av1|xvid|divx|vc-?1|mpeg-?2)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CodecPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(remux|blu-?ray|bdrip|brrip|bdremux|web-?dl|webrip|web|hdtv|dvdrip|dvd|hdrip|hdcam|camrip|cam|telesync|hdts|ts|telecine|tc|screener|scr)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SourcePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(hdr10\+?|hdr|dv|dovi|dolby[ .]?vision)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex HdrPattern();

    // "S01" with no episode after it, or "Season 1" - a whole season in one torrent.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:S(\d{1,2})(?![0-9]|[ ._-]?E\d)|Season[ ._-]?(\d{1,2})(?![0-9]))", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPackPattern();

    // "S01-S05", "S01-05", "Seasons 1-5", "Complete Series".
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:S\d{1,2}[ ._-]*-[ ._-]*S?\d{1,2}(?![0-9]|E)|Seasons?[ ._-]?\d{1,2}[ ._-]*-[ ._-]*\d{1,2}|complete[ ._-]series)", RegexOptions.IgnoreCase)]
    private static partial Regex MultiSeasonPattern();

    public static Parsed Parse(string releaseTitle)
    {
        var quality = QualityPattern().Match(releaseTitle) is { Success: true } q
            ? q.Value.ToLowerInvariant() switch { "4k" or "uhd" => "2160p", "1080i" => "1080p", "576p" => "480p", var v => v }
            : null;
        var codec = CodecPattern().Match(releaseTitle) is { Success: true } c
            ? Regex.Replace(c.Value.ToLowerInvariant(), @"[ .-]", "") switch
            {
                "x265" or "h265" or "hevc" => "HEVC",
                "x264" or "h264" or "avc" => "H.264",
                "av1" => "AV1",
                "xvid" or "divx" => "XviD",
                "vc1" => "VC-1",
                _ => "MPEG-2"
            }
            : null;
        var source = SourcePattern().Match(releaseTitle) is { Success: true } s
            ? Regex.Replace(s.Value.ToLowerInvariant(), "-", "") switch
            {
                "remux" or "bdremux" => "Remux",
                "bluray" or "bdrip" or "brrip" => "BluRay",
                "webdl" => "WEB-DL",
                "webrip" or "web" => "WEBRip",
                "hdtv" => "HDTV",
                "dvdrip" or "dvd" => "DVD",
                "hdrip" => "HDRip",
                _ => "CAM"
            }
            : null;
        var hdr = HdrPattern().IsMatch(releaseTitle);

        var episode = MediaNaming.ParseEpisode(releaseTitle, null);
        var multi = MultiSeasonPattern().IsMatch(releaseTitle);
        int? packSeason = null;
        if (episode.Episode is null && !multi && SeasonPackPattern().Match(releaseTitle) is { Success: true } pack)
            packSeason = int.Parse(pack.Groups[1].Success ? pack.Groups[1].Value : pack.Groups[2].Value);

        var title = MediaNaming.ParseTitle(releaseTitle);
        if (episode.Episode is not null && episode.ShowName is { } show) title = show;

        return new Parsed(quality, codec, source, hdr,
            episode.Episode is not null ? episode.Season : packSeason,
            episode.Episode,
            packSeason is not null,
            multi,
            title.Title,
            title.Year);
    }

    // Whether a release is what was asked for: the same title (and year, give or take one, for a
    // film), and for TV the same episode, or a pack of exactly the season asked for.
    public static bool Matches(Parsed parsed, Request request)
    {
        if (!TitleMatches(parsed.Title, request.Title)) return false;
        if (request.Kind == MediaKinds.Movie)
        {
            if (parsed.Season is not null || parsed.Episode is not null) return false;
            return request.Year is null || parsed.Year is null || Math.Abs(parsed.Year.Value - request.Year.Value) <= 1;
        }
        if (parsed.MultiSeason) return false;
        if (request.Season is null) return parsed.Episode is not null || parsed.SeasonPack;
        if (request.Episode is null) return parsed.SeasonPack && parsed.Season == request.Season;
        return parsed.Season == request.Season && parsed.Episode == request.Episode;
    }

    public static bool TitleMatches(string releaseTitle, string wanted)
    {
        var a = Key(releaseTitle);
        var b = Key(wanted);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b;
    }

    // Titles compared without punctuation, "and"/"&", a leading "The" or a trailing country:
    // "Bob's Burgers" and "Bobs.Burgers", "The Office" and "The.Office.US".
    private static string Key(string title)
    {
        var text = title.Replace("&", " and ", StringComparison.Ordinal).Trim();
        text = Regex.Replace(text, @"^(the|a|an)\s+", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[\s(]+(us|uk|au|nz|ca)\)?$", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\band\b", "", RegexOptions.IgnoreCase);
        return MediaNaming.NormalizeKey(text);
    }

    public static string? Warning(Parsed parsed, int? seeders)
    {
        if (parsed.Source == "CAM") return "Filmed in a cinema - poor picture and sound.";
        if (seeders is 0) return "Nobody is sharing this right now; it may never finish.";
        if (parsed.Codec is "AV1" or "XviD" or "VC-1" or "MPEG-2") return $"{parsed.Codec} video has to be converted to play on most devices.";
        return null;
    }

    public static double Score(Parsed parsed, bool matches, int? seeders, long? size, string preferredQuality)
    {
        double score = matches ? 1000 : 0;
        var seeds = seeders ?? 0;
        score += seeds == 0 ? -300 : Math.Min(Math.Log2(seeds + 1) * 8, 80);

        if (preferredQuality != MediaDownloadsSettingsDto.QualityAny && parsed.Quality is not null)
        {
            var want = Array.IndexOf(QualityOrder, preferredQuality);
            var have = Array.IndexOf(QualityOrder, parsed.Quality);
            if (want >= 0 && have >= 0)
                score += have == want ? 60 : have > want ? 35 - (have - want) * 10 : 30 - (want - have) * 15;
        }
        else if (parsed.Quality is not null) score += Array.IndexOf(QualityOrder, parsed.Quality) * 10;

        score += parsed.Source switch
        {
            "BluRay" or "WEB-DL" => 20,
            "WEBRip" => 15,
            "Remux" => 5,
            "HDTV" or "HDRip" => 5,
            "DVD" => 0,
            "CAM" => -400,
            _ => 0
        };
        score += parsed.Codec switch
        {
            "H.264" or "HEVC" => 10,
            "AV1" => -15,
            "XviD" or "VC-1" or "MPEG-2" => -20,
            _ => 0
        };
        // Tens of gigabytes for one film is a disc copy nobody needs to wait for.
        if (size is > 40L * 1024 * 1024 * 1024) score -= 30;
        return score;
    }
}
