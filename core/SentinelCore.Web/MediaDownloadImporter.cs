using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// Files a finished download into its library the way the scanner reads best:
//   Movies/Dune Part Two (2024)/Dune Part Two (2024).mkv  (+ .en.srt)
//   TV Shows/Bluey (2018)/Season 2/Bluey - S02E05.mkv
// Only the video (and English subtitles) is kept; samples, .nfo files and the rest of what a
// release carries are left behind and deleted with the staging folder. Planning is pure, so
// MediaDownloadImporterTests can pin it without moving anything.
public static partial class MediaDownloadImporter
{
    public sealed record SourceFile(string RelativePath, long Size);

    public sealed record Move(string Source, string Destination);

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt", ".ass", ".ssa" };

    [GeneratedRegex(@"(?<![A-Za-z])(en|eng|english)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishPattern();

    [GeneratedRegex(@"(?<![A-Za-z])(forced|sdh|hi)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex AlternatePattern();

    // `stagingRoot` is where qBittorrent put the files (file paths are relative to it);
    // `showFolder` is the folder an already-known show lives in, so new episodes join it.
    public static List<Move> Plan(MediaDownload download, IReadOnlyList<SourceFile> files, string stagingRoot, MediaLibrary library, string? showFolder)
    {
        var usable = files.Where(f => !InSkippedFolder(f.RelativePath) && !MediaNaming.IsSkippedFile(Path.GetFileName(f.RelativePath))).ToList();
        var videos = usable.Where(f => MediaNaming.IsVideoFile(f.RelativePath)).ToList();
        var subtitles = usable.Where(f => SubtitleExtensions.Contains(Path.GetExtension(f.RelativePath))).ToList();
        var moves = new List<Move>();

        if (download.Kind == MediaKinds.Movie)
        {
            var video = videos.OrderByDescending(v => v.Size).FirstOrDefault();
            if (video is null) return moves;
            var name = SafeName(download.Year is { } year ? $"{download.Title} ({year})" : download.Title);
            var folder = Path.Combine(library.Path, name);
            var stem = Unique(folder, name, Path.GetExtension(video.RelativePath));
            moves.Add(new Move(Path.Combine(stagingRoot, video.RelativePath), Path.Combine(folder, stem + Path.GetExtension(video.RelativePath).ToLowerInvariant())));
            if (BestSubtitle(subtitles, video, videos.Count) is { } subtitle)
                moves.Add(new Move(Path.Combine(stagingRoot, subtitle.File.RelativePath), Path.Combine(folder, stem + subtitle.Suffix)));
            return moves;
        }

        var show = showFolder ?? Path.Combine(library.Path, SafeName(download.Year is { } y ? $"{download.Title} ({y})" : download.Title));
        var taken = new HashSet<(int, int)>();
        foreach (var video in videos.OrderByDescending(v => v.Size))
        {
            var directory = Path.GetDirectoryName(video.RelativePath);
            var folderSeason = string.IsNullOrEmpty(directory) ? null : MediaNaming.ParseSeasonFolder(Path.GetFileName(directory));
            var parsed = MediaNaming.ParseEpisode(Path.GetFileNameWithoutExtension(video.RelativePath), folderSeason ?? download.Season);
            if (parsed.Episode is not { } episode) continue;
            var season = parsed.Season ?? download.Season ?? 1;
            if (download.Season is { } wantSeason && season != wantSeason) continue;
            if (download.Episode is { } wantEpisode && episode != wantEpisode) continue;
            // Two copies of one episode in a pack (a "proper" beside the original): the bigger wins.
            if (!taken.Add((season, episode))) continue;

            var seasonFolder = Path.Combine(show, $"Season {season}");
            var stem = SafeName($"{download.Title} - S{season:00}E{episode:00}");
            stem = Unique(seasonFolder, stem, Path.GetExtension(video.RelativePath));
            moves.Add(new Move(Path.Combine(stagingRoot, video.RelativePath), Path.Combine(seasonFolder, stem + Path.GetExtension(video.RelativePath).ToLowerInvariant())));
            if (BestSubtitle(subtitles, video, videos.Count) is { } subtitle)
                moves.Add(new Move(Path.Combine(stagingRoot, subtitle.File.RelativePath), Path.Combine(seasonFolder, stem + subtitle.Suffix)));
        }
        return moves;
    }

    // Moves the planned files and returns the video paths that landed. A destination that
    // appeared since planning is never overwritten.
    public static List<string> Execute(IReadOnlyList<Move> moves)
    {
        var landed = new List<string>();
        foreach (var move in moves)
        {
            if (!File.Exists(move.Source) || File.Exists(move.Destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
            File.Move(move.Source, move.Destination);
            if (MediaNaming.IsVideoFile(move.Destination)) landed.Add(move.Destination);
        }
        return landed;
    }

    // The id the scanner will give a movie file, so a finished download can link to it.
    public static Guid MovieItemId(MediaLibrary library, string path) =>
        MediaNaming.StableId(library.Id, Path.GetRelativePath(Path.GetFullPath(library.Path), path));

    // The id the scanner gives a show whose folder is `folder` (see MediaScanner.WalkShows).
    public static Guid ShowItemId(MediaLibrary library, string folder) =>
        MediaNaming.StableId(library.Id, "show:" + MediaNaming.NormalizeKey(MediaNaming.ParseTitle(Path.GetFileName(folder)).Title));

    private static bool InSkippedFolder(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(directory)) return false;
        return directory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(MediaNaming.IsSkippedFolder);
    }

    // The English subtitle for one video: named after it, or in a Subs folder of its own, or -
    // when the release holds a single video - any English subtitle in it. A lone subtitle of
    // unknown language beside a lone video is kept as the plain sidecar.
    private static (SourceFile File, string Suffix)? BestSubtitle(List<SourceFile> subtitles, SourceFile video, int videoCount)
    {
        var stem = Path.GetFileNameWithoutExtension(video.RelativePath);
        var related = subtitles.Where(s => s.RelativePath.Contains(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        if (related.Count == 0 && videoCount == 1) related = subtitles;
        var english = related
            .Where(s => EnglishPattern().IsMatch(Path.GetFileNameWithoutExtension(s.RelativePath)) || EnglishPattern().IsMatch(Path.GetFileName(Path.GetDirectoryName(s.RelativePath) ?? "")))
            .OrderBy(s => AlternatePattern().IsMatch(Path.GetFileNameWithoutExtension(s.RelativePath)) ? 1 : 0)
            .ThenByDescending(s => s.Size)
            .FirstOrDefault();
        if (english is not null) return (english, ".en" + Path.GetExtension(english.RelativePath).ToLowerInvariant());
        if (related.Count == 1 && videoCount == 1) return (related[0], Path.GetExtension(related[0].RelativePath).ToLowerInvariant());
        return null;
    }

    private static string Unique(string folder, string stem, string extension)
    {
        var candidate = stem;
        for (var n = 2; File.Exists(Path.Combine(folder, candidate + extension.ToLowerInvariant())); n++) candidate = $"{stem} ({n})";
        return candidate;
    }

    // Safe on every file system the library might sit on, NTFS included.
    public static string SafeName(string name)
    {
        var cleaned = Regex.Replace(name, @"[<>:""/\\|?*\x00-\x1F]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "Download" : cleaned;
    }
}
