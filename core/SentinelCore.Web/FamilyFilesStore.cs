namespace SentinelCore.Web;

// Where the family library's files physically are: a folder on this server, or a shared folder
// on another computer on the network - a Windows PC, Samba on a Linux box, a NAS, a Mac with File
// Sharing on - read and written over SMB by the server itself, with nothing mounted. The network
// half is the same code the Movies libraries read through (MediaNetwork.cs), and so are the saved
// sign-ins (media-network.json).
//
// Everything in FamilyFiles.cs and the backup drives goes through this, with the library's own
// relative paths ("Photos/2026/beach.jpg", "" for the top). The trash, the index and the
// thumbnails always stay on this server.

public sealed record FamilyStoreEntry(string Path, string Name, bool IsFolder, long Size, DateTime ModifiedUtc);

public interface IFamilyLibraryStore
{
    // What to show the admin: a local path or smb://host/share/folder.
    string Root { get; }

    bool IsNetwork { get; }

    // Null when nothing is at that path.
    FamilyStoreEntry? Stat(string relative);

    // What is directly inside a folder, or null when the folder isn't there.
    IReadOnlyList<FamilyStoreEntry>? List(string folder);

    // Every file below a folder. Hidden folders (".something") are not walked into.
    IEnumerable<FamilyStoreEntry> Walk(string folder);

    // Makes the folder and any missing parents; one already there is fine.
    void CreateFolder(string relative);

    Stream OpenRead(string relative);

    // A new file; its folder must exist. Throws IOException when the name is taken.
    Stream CreateNew(string relative);

    // A file or a folder, to a name nothing is at; makes the destination's parents.
    void Move(string from, string to);

    void SetModified(string relative, DateTime utc);

    void DeleteFile(string relative);

    // Removes the folder and the empty folders inside it, keeping anything with a file in it.
    void DeleteEmptyFolders(string relative);

    // Takes a file out of the library onto this server's disk (the trash) and back in.
    Task MoveOutAsync(string relative, string localPath, CancellationToken ct);
    Task MoveInAsync(string localPath, string relative, CancellationToken ct);

    // A path on this server's disk for the file, so it can be served straight from there; null
    // for a network library.
    string? LocalPath(string relative);
}

public static class FamilyLibraryLocation
{
    // A network library is smb://host/share/folder. On a Windows server a \\server\share path is
    // left to Windows (it signs in with the service's own account, as before); anywhere else it
    // means the same as smb://.
    public static bool IsNetwork(string? location) =>
        location is not null && (location.TrimStart().StartsWith(MediaNetworkPath.Scheme, StringComparison.OrdinalIgnoreCase)
            || (!OperatingSystem.IsWindows() && MediaNetworkPath.IsNetwork(location.TrimStart())));

    // The tidy form to save, or null when it can't be a library location. Blank is the default.
    public static string? Normalize(string? location, out string? problem)
    {
        problem = null;
        var text = location?.Trim() ?? "";
        if (text.Length == 0) return "";
        if (IsNetwork(text))
        {
            if (!MediaNetworkPath.TryParse(text, out var network))
            {
                problem = "That network address is not complete. It looks like smb://computer/SharedFolder/Family Files.";
                return null;
            }
            if (network.Share.Length == 0)
            {
                problem = $"Choose a shared folder on {network.Host}, not just the computer - like smb://{network.HostWithPort}/Family.";
                return null;
            }
            return network.ToString();
        }
        if (!Path.IsPathRooted(text))
        {
            problem = "The library folder must be a full path, e.g. /mnt/bigdisk/family-files, a shared folder on another computer like smb://192.168.1.20/Family, or blank for the default.";
            return null;
        }
        return text;
    }
}

public sealed class LocalFamilyLibraryStore(string root) : IFamilyLibraryStore
{
    public string Root => root;

    public bool IsNetwork => false;

    private string Disk(string relative) => FamilyFilePaths.ToDisk(root, relative);

    public string? LocalPath(string relative) => Disk(relative);

    public FamilyStoreEntry? Stat(string relative)
    {
        var path = Disk(relative);
        var file = new FileInfo(path);
        if (file.Exists) return new FamilyStoreEntry(relative, file.Name, false, file.Length, file.LastWriteTimeUtc);
        var directory = new DirectoryInfo(path);
        return directory.Exists ? new FamilyStoreEntry(relative, directory.Name, true, 0, directory.LastWriteTimeUtc) : null;
    }

    public IReadOnlyList<FamilyStoreEntry>? List(string folder)
    {
        var directory = new DirectoryInfo(Disk(folder));
        if (!directory.Exists) return null;
        var entries = new List<FamilyStoreEntry>();
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var relative = FamilyFilePaths.Combine(folder, entry.Name);
            if (entry is DirectoryInfo) entries.Add(new FamilyStoreEntry(relative, entry.Name, true, 0, entry.LastWriteTimeUtc));
            else if (entry is FileInfo file) entries.Add(new FamilyStoreEntry(relative, file.Name, false, file.Length, file.LastWriteTimeUtc));
        }
        return entries;
    }

    public IEnumerable<FamilyStoreEntry> Walk(string folder)
    {
        var start = Disk(folder);
        if (!Directory.Exists(start)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in new DirectoryInfo(start).EnumerateFiles("*", options))
        {
            var relative = FamilyFilePaths.FromDisk(root, file.FullName);
            if (relative.Split('/').SkipLast(1).Any(segment => segment.StartsWith('.'))) continue;
            yield return new FamilyStoreEntry(relative, file.Name, false, file.Length, file.LastWriteTimeUtc);
        }
    }

    public void CreateFolder(string relative) => Directory.CreateDirectory(Disk(relative));

    public Stream OpenRead(string relative) =>
        new FileStream(Disk(relative), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

    public Stream CreateNew(string relative) =>
        new FileStream(Disk(relative), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

    public void Move(string from, string to)
    {
        var source = Disk(from);
        var target = Disk(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(source)) File.Move(source, target);
        else Directory.Move(source, target);
    }

    public void SetModified(string relative, DateTime utc)
    {
        try { File.SetLastWriteTimeUtc(Disk(relative), utc); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void DeleteFile(string relative)
    {
        var path = Disk(relative);
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeleteEmptyFolders(string relative) => TryDeleteEmptyTree(Disk(relative));

    private static void TryDeleteEmptyTree(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var child in Directory.EnumerateDirectories(directory)) TryDeleteEmptyTree(child);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public Task MoveOutAsync(string relative, string localPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.Move(Disk(relative), localPath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task MoveInAsync(string localPath, string relative, CancellationToken ct)
    {
        var target = Disk(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(localPath, target);
        return Task.CompletedTask;
    }
}

// A library in a shared folder on another computer. SMB calls are synchronous; the async
// methods move them off the request thread.
public sealed class NetworkFamilyLibraryStore(string root, MediaNetworkFileSystem network) : IFamilyLibraryStore
{
    private readonly MediaNetworkPath _root = MediaNetworkPath.TryParse(root, out var parsed)
        ? parsed
        : throw new ArgumentException($"{root} is not a network path.", nameof(root));

    public string Root => _root.ToString();

    public bool IsNetwork => true;

    public string? LocalPath(string relative) => null;

    private string Remote(string relative)
    {
        var path = _root;
        if (relative.Length > 0)
            foreach (var segment in relative.Split('/')) path = path.Child(segment);
        return path.ToString();
    }

    private string RelativeOf(string folder, string name) => FamilyFilePaths.Combine(folder, name);

    public FamilyStoreEntry? Stat(string relative)
    {
        var entry = network.Stat(Remote(relative));
        return entry is null ? null : new FamilyStoreEntry(relative, relative.Length == 0 ? entry.Name : FamilyFilePaths.NameOf(relative),
            entry.IsDirectory, entry.Length, entry.ModifiedUtc);
    }

    public IReadOnlyList<FamilyStoreEntry>? List(string folder)
    {
        try
        {
            return network.List(Remote(folder))
                .Select(e => new FamilyStoreEntry(RelativeOf(folder, e.Name), e.Name, e.IsDirectory, e.Length, e.ModifiedUtc))
                .ToList();
        }
        catch (DirectoryNotFoundException) { return null; }
    }

    public IEnumerable<FamilyStoreEntry> Walk(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IReadOnlyList<FamilyStoreEntry>? entries;
            try { entries = List(current); }
            catch (UnauthorizedAccessException) { continue; }
            if (entries is null) continue;
            foreach (var entry in entries)
            {
                if (entry.IsFolder) { if (!entry.Name.StartsWith('.')) pending.Push(entry.Path); }
                else yield return entry;
            }
        }
    }

    public void CreateFolder(string relative)
    {
        // The library's own folder may not exist yet either; the share itself must.
        var built = _root with { Rest = "" };
        var segments = _root.Rest.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Concat(relative.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in segments)
        {
            built = built.Child(segment);
            network.CreateDirectory(built.ToString());
        }
    }

    public Stream OpenRead(string relative) => network.OpenRead(Remote(relative));

    public Stream CreateNew(string relative) => network.CreateNew(Remote(relative));

    public void Move(string from, string to)
    {
        var parent = FamilyFilePaths.ParentOf(to);
        CreateFolder(parent);
        network.Rename(Remote(from), Remote(to));
    }

    public void SetModified(string relative, DateTime utc)
    {
        try { network.SetLastWriteTime(Remote(relative), utc); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void DeleteFile(string relative) => network.Delete(Remote(relative));

    public void DeleteEmptyFolders(string relative)
    {
        try
        {
            if (List(relative) is not { } entries) return;
            foreach (var folder in entries.Where(e => e.IsFolder)) DeleteEmptyFolders(folder.Path);
            if (relative.Length > 0 && List(relative) is { Count: 0 }) network.Delete(Remote(relative));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Copied to this server, checked, and only then deleted on the share - a dropped connection
    // halfway leaves the file where it was.
    public async Task MoveOutAsync(string relative, string localPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var partial = localPath + ".part";
        try
        {
            long expected;
            await using (var input = await Task.Run(() => OpenRead(relative), ct))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                expected = input.Length;
                await input.CopyToAsync(output, 1 << 20, ct);
            }
            if (new FileInfo(partial).Length != expected) throw new IOException($"{relative} came back from {_root.Host} a different size.");
            if (Stat(relative) is { IsFolder: false } entry) File.SetLastWriteTimeUtc(partial, entry.ModifiedUtc);
            File.Move(partial, localPath, overwrite: true);
            await Task.Run(() => DeleteFile(relative), ct);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public async Task MoveInAsync(string localPath, string relative, CancellationToken ct)
    {
        var parent = FamilyFilePaths.ParentOf(relative);
        await Task.Run(() => CreateFolder(parent), ct);
        var partial = FamilyFilePaths.Combine(parent, $".restore-{Guid.NewGuid():N}.part");
        var expected = new FileInfo(localPath).Length;
        try
        {
            await using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var output = await Task.Run(() => CreateNew(partial), ct))
            {
                await input.CopyToAsync(output, 1 << 20, ct);
                await output.FlushAsync(ct);
            }
            if (Stat(partial)?.Size != expected) throw new IOException($"{relative} came out a different size on {_root.Host} - its disk may be full.");
            SetModified(partial, File.GetLastWriteTimeUtc(localPath));
            await Task.Run(() => network.Rename(Remote(partial), Remote(relative)), ct);
            partial = "";
            File.Delete(localPath);
        }
        finally
        {
            if (partial.Length > 0)
                try { DeleteFile(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }
}
