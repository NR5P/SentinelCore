namespace SentinelCore.Web;

// One file or folder as a listing reports it. The listing carries size and date so a scan of a
// network share costs one round trip per folder rather than one per file.
public sealed record MediaFsEntry(string Name, string Path, bool IsDirectory, long Length, DateTime ModifiedUtc);

// What the scanner, the artwork and subtitle code and the stream endpoint need from wherever a
// library's files live: a folder on the server (LocalMediaFileSystem) or a shared folder on
// another computer, read over SMB without mounting anything (MediaNetworkFileSystem). Paths are
// opaque strings owned by the implementation: local paths are ordinary OS paths, network ones
// are smb://host/share/folder/file (see MediaNetworkPath).
public interface IMediaFileSystem
{
    bool DirectoryExists(string path);

    // Throws IOException (or UnauthorizedAccessException) when the folder cannot be read.
    IReadOnlyList<MediaFsEntry> List(string directory);

    MediaFsEntry? Stat(string path);

    // A seekable, read-only stream. Throws IOException when the file cannot be opened.
    Stream OpenRead(string path);

    string Combine(string directory, string name);

    string? Parent(string path);

    string Relative(string root, string path);
}

public sealed class LocalMediaFileSystem : IMediaFileSystem
{
    public static readonly LocalMediaFileSystem Instance = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<MediaFsEntry> List(string directory)
    {
        var info = new DirectoryInfo(directory);
        var entries = new List<MediaFsEntry>();
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo)
                entries.Add(new MediaFsEntry(entry.Name, entry.FullName, true, 0, entry.LastWriteTimeUtc));
            else if (entry is FileInfo file)
                entries.Add(new MediaFsEntry(file.Name, file.FullName, false, file.Length, file.LastWriteTimeUtc));
        }
        return entries;
    }

    public MediaFsEntry? Stat(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists) return new MediaFsEntry(file.Name, file.FullName, false, file.Length, file.LastWriteTimeUtc);
        var directory = new DirectoryInfo(path);
        return directory.Exists ? new MediaFsEntry(directory.Name, directory.FullName, true, 0, directory.LastWriteTimeUtc) : null;
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);

    public string Combine(string directory, string name) => Path.Combine(directory, name);

    public string? Parent(string path) => Path.GetDirectoryName(path);

    public string Relative(string root, string path) => Path.GetRelativePath(root, path);
}

// Picks the file system for a path, and turns a path into something ffmpeg can open: the path
// itself for a local file, a loopback URL from MediaFileRelay for one on a network share (ffmpeg
// cannot speak SMB, and mounting shares needs root on Linux).
public sealed class MediaFiles(MediaNetworkFileSystem network, MediaFileRelay relay)
{
    public MediaNetworkFileSystem Network { get; } = network;

    public IMediaFileSystem For(string path) =>
        MediaNetworkPath.IsNetwork(path) ? Network : LocalMediaFileSystem.Instance;

    public bool FileExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return For(path).Stat(path) is { IsDirectory: false }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public string FfmpegInput(string path) => MediaNetworkPath.IsNetwork(path) ? relay.UrlFor(path) : path;

    // What to hand Results.Stream / Results.File: a local path is served straight from disk.
    public IResult Serve(string path, string contentType, bool ranges)
    {
        if (!MediaNetworkPath.IsNetwork(path))
            return Results.File(path, contentType, lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: ranges);
        var entry = Network.Stat(path);
        if (entry is null or { IsDirectory: true }) return Results.NotFound();
        return Results.Stream(Network.OpenRead(path), contentType,
            lastModified: new DateTimeOffset(DateTime.SpecifyKind(entry.ModifiedUtc, DateTimeKind.Utc)), enableRangeProcessing: ranges);
    }
}
