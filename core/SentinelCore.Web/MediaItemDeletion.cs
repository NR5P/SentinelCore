namespace SentinelCore.Web;

// Deleting a movie from the library: its video, the files named after it (subtitles, posters),
// and - when the movie had a folder to itself - that whole folder. Only on this server's own
// disks; a share on another computer is read-only to SentinelCore. Used by
// DELETE /api/media/items/{id} and by a download that replaces a copy already in the library.
public static class MediaItemDeletion
{
    // Null when the movie can be deleted here, else why not, in words for the app.
    public static string? Problem(MediaItem item, MediaLibrary? library)
    {
        if (item.Kind != MediaKinds.Movie) return "Only movies can be deleted.";
        if (library is null) return "The library that movie is in has been removed.";
        if (MediaNetworkPath.IsNetwork(item.Path) || MediaNetworkPath.IsNetwork(library.Path))
            return "That movie is on another computer's share. Delete it on that computer.";
        if (!IsInside(library.Path, item.Path)) return "That movie's file is not inside its library.";
        return null;
    }

    // The files and folder Delete would remove, worked out without touching anything.
    public static (string? Folder, List<string> Files) Plan(string videoPath, string libraryRoot)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(folder)) return (null, File.Exists(videoPath) ? [videoPath] : []);

        // A folder of its own: nothing else in it (outside Sample/Extras folders) is a video.
        var ownFolder = !string.Equals(folder.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.Ordinal)
            && !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(videoPath), StringComparison.Ordinal))
                .Where(f => !InSkippedFolder(folder, f))
                .Any(MediaNaming.IsVideoFile);
        if (ownFolder) return (folder, []);

        // Shares its folder: the video, plus its sidecars ("Movie.en.srt", "Movie-poster.jpg").
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var files = Directory.EnumerateFiles(folder)
            .Where(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(videoPath), StringComparison.Ordinal)
                || (!MediaNaming.IsVideoFile(f) && IsSidecarOf(Path.GetFileName(f), stem)))
            .ToList();
        return (null, files);
    }

    public static void Delete(string videoPath, string libraryRoot)
    {
        var (folder, files) = Plan(videoPath, libraryRoot);
        if (folder is not null)
        {
            Directory.Delete(folder, recursive: true);
            return;
        }
        foreach (var file in files) File.Delete(file);
    }

    private static bool IsSidecarOf(string fileName, string stem) =>
        fileName.Length > stem.Length
        && fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
        && fileName[stem.Length] is '.' or '-';

    private static bool InSkippedFolder(string folder, string file)
    {
        var relative = Path.GetDirectoryName(Path.GetRelativePath(folder, file));
        return !string.IsNullOrEmpty(relative)
            && relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(MediaNaming.IsSkippedFolder);
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
