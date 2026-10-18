using System.Security.Cryptography;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace SentinelCore.Web;

// FAMILY FILES: a shared file library on this server - the photos, scans and documents a household
// wants kept - that every app and the dashboard can add to and fetch from, mirrored to one or more
// backup drives (FamilyFilesDrives.cs) and checked for duplicate files every night
// (FamilyFilesJobs.cs).
//
// The library is a plain folder tree, not a database: what a backup drive holds is a copy anyone
// can open in a file manager with this software gone. Nothing is ever deleted outright - a file
// removed from the library, and every duplicate the nightly check retires, goes to a trash folder
// beside the library and is kept for a while first.

// The relative paths clients send ("Photos/2026/beach.jpg"). Always forward slashes, never absolute,
// never reaching outside the library, and made of names any of the platforms a backup drive might
// be read on will accept.
public static class FamilyFilePaths
{
    public const int MaxNameLength = 200;
    public const int MaxPathLength = 1024;

    private static readonly char[] Reserved = ['<', '>', ':', '"', '|', '?', '*', '\\', '/'];

    // A clean relative path, or null when it can't be one. "" is the library root.
    public static string? Normalize(string? raw)
    {
        if (raw is null) return null;
        var text = raw.Replace('\\', '/').Trim();
        if (text.Length > MaxPathLength) return null;

        var segments = new List<string>();
        foreach (var segment in text.Split('/'))
        {
            if (segment.Length == 0) continue;
            if (!IsValidName(segment)) return null;
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        if (name.Trim() != name) return false;
        if (name == "." || name == ".." || name.StartsWith('.')) return false;
        if (name.EndsWith('.')) return false;
        if (name.IndexOfAny(Reserved) >= 0) return false;
        return name.All(c => c >= ' ' && c != '\x7f');
    }

    public static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    public static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public static string NameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public static string ToDisk(string root, string relative) =>
        relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static string FromDisk(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    // "beach.jpg" -> "beach (2).jpg", "beach (3).jpg" ... until nothing is at that name.
    public static string NextFreeName(string folder, string name, Func<string, bool> taken)
    {
        var candidate = Combine(folder, name);
        if (!taken(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            candidate = Combine(folder, $"{stem} ({n}){extension}");
            if (!taken(candidate)) return candidate;
        }
    }

    public static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".heic";

    public static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        ".tif" or ".tiff" => "image/tiff",
        ".svg" => "image/svg+xml",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream"
    };
}

public sealed record FamilyFileEntry(string Path, string Name, long Size, DateTimeOffset ModifiedUtc, string? Sha256, bool IsImage);
public sealed record FamilyFolderEntry(string Path, string Name);
// One pass over the whole library. `Unreadable` names the folders and files that couldn't be
// read this time: they are not in `Files`, but that doesn't mean they are gone, so nothing may be
// removed from a backup on the strength of an incomplete scan.
public sealed record FamilyLibraryScan(IReadOnlyList<FamilyFileEntry> Files, IReadOnlyList<string> Unreadable)
{
    public bool Complete => Unreadable.Count == 0;
}

public sealed record FamilyFolderListing(string Path, IReadOnlyList<FamilyFolderEntry> Folders, IReadOnlyList<FamilyFileEntry> Files);

// Where everything lives. The library folder is a setting (a household with a big disk points it
// there, or at a shared folder on another computer); the index, trash and state stay under the
// data directory regardless.
public sealed class FamilyFilesLocations(string dataDirectory, AppSettingsFile settings, MediaNetworkFileSystem? network = null)
{
    public string DefaultLibrary { get; } = Path.Combine(dataDirectory, "family-files", "library");
    public string Trash { get; } = Path.Combine(dataDirectory, "family-files", "trash");
    public string IndexFile { get; } = Path.Combine(dataDirectory, "family-files", "index.json");
    public string StateFile { get; } = Path.Combine(dataDirectory, "family-files", "state.json");
    public string DrivesFile { get; } = Path.Combine(dataDirectory, "family-files", "drives.json");
    public string Thumbnails { get; } = Path.Combine(dataDirectory, "family-files", "thumbnails");
    public string Work { get; } = Path.Combine(dataDirectory, "family-files", "work");

    public MediaNetworkFileSystem? Network => network;

    public async Task<FamilyFilesSettingsDto> OptionsAsync(CancellationToken ct) =>
        await settings.GetSectionAsync<FamilyFilesSettingsDto>(FamilyFilesSettingsDto.SectionName, ct) ?? new FamilyFilesSettingsDto();

    public async Task<string> LibraryAsync(CancellationToken ct) => (await StoreAsync(ct)).Root;

    public async Task<IFamilyLibraryStore> StoreAsync(CancellationToken ct) => StoreFor((await OptionsAsync(ct)).LibraryFolder);

    public IFamilyLibraryStore StoreFor(string? location)
    {
        var normalized = FamilyLibraryLocation.Normalize(location, out _);
        if (string.IsNullOrEmpty(normalized)) return new LocalFamilyLibraryStore(DefaultLibrary);
        if (FamilyLibraryLocation.IsNetwork(normalized))
            return new NetworkFamilyLibraryStore(normalized, network ?? throw new InvalidOperationException("Network libraries need the network file system."));
        return new LocalFamilyLibraryStore(normalized);
    }
}

// Cached SHA-256 of every file in the library, keyed by relative path and trusted only while the
// file's size and modification time still match - so a nightly pass over a library that hasn't
// changed reads no file contents at all, and one that has only reads what's new. That matters
// twice over for a library on another computer, where every byte read crosses the network.
public sealed class FamilyFilesIndex(string path)
{
    public sealed record Entry(long Size, long ModifiedTicks, string Sha256);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, Entry>? _entries;

    private async Task<Dictionary<string, Entry>> EntriesAsync(CancellationToken ct)
    {
        if (_entries is not null) return _entries;
        try
        {
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                _entries = await JsonSerializer.DeserializeAsync<Dictionary<string, Entry>>(stream, Options, ct);
            }
        }
        catch (JsonException)
        {
            // Only a cache: a damaged index costs one full re-hash.
        }
        return _entries ??= new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    public async Task<string?> KnownHashAsync(FamilyStoreEntry file, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await EntriesAsync(ct);
            return entries.TryGetValue(file.Path, out var entry) && Matches(entry, file) ? entry.Sha256 : null;
        }
        finally { _lock.Release(); }
    }

    // The file's hash, computed and remembered if the index doesn't already know it.
    public async Task<string> HashAsync(IFamilyLibraryStore store, FamilyStoreEntry file, CancellationToken ct)
    {
        if (await KnownHashAsync(file, ct) is { } known) return known;
        var hash = await ComputeAsync(store, file.Path, ct);
        await RememberAsync(file, hash, ct);
        return hash;
    }

    public async Task RememberAsync(FamilyStoreEntry file, string hash, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await EntriesAsync(ct);
            entries[file.Path] = new Entry(file.Size, file.ModifiedUtc.Ticks, hash);
        }
        finally { _lock.Release(); }
    }

    public async Task ForgetAsync(string relative, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { (await EntriesAsync(ct)).Remove(relative); }
        finally { _lock.Release(); }
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await EntriesAsync(ct);
            if (entries.Remove(from, out var entry)) entries[to] = entry;
        }
        finally { _lock.Release(); }
    }

    // A library pointed somewhere else (another disk, another computer) starts with no hashes
    // that could be trusted - paths alone can't tell the two apart.
    public async Task ClearAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { (await EntriesAsync(ct)).Clear(); }
        finally { _lock.Release(); }
        await SaveAsync(ct);
    }

    // Walks the whole library: hashes anything new or changed, drops anything gone, saves, and
    // returns every file with its hash. `progress` gets (done, total) as it goes.
    public async Task<FamilyLibraryScan> RescanAsync(IFamilyLibraryStore store, Action<int, int>? progress, CancellationToken ct)
    {
        var unreadable = new List<string>();
        var files = await Task.Run(() => store.Walk("", unreadable.Add)
            .Where(f => !FamilyFilesLibrary.IsHiddenOrPartial(f.Name) && FamilyFilePaths.Normalize(f.Path) == f.Path)
            .ToList(), ct);

        var result = new List<FamilyFileEntry>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(file.Path);
            string hash;
            try
            {
                hash = await HashAsync(store, file, ct);
            }
            catch (MediaNetworkException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(file.Path);
                done++;
                continue;
            }
            result.Add(new FamilyFileEntry(file.Path, file.Name, file.Size, file.ModifiedUtc, hash, FamilyFilePaths.IsImage(file.Name)));
            progress?.Invoke(++done, files.Count);
        }

        await _lock.WaitAsync(ct);
        try
        {
            var entries = await EntriesAsync(ct);
            foreach (var gone in entries.Keys.Where(k => !seen.Contains(k)).ToList()) entries.Remove(gone);
        }
        finally { _lock.Release(); }

        await SaveAsync(ct);
        return new FamilyLibraryScan(result, unreadable);
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await EntriesAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, entries, Options, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    private static bool Matches(Entry entry, FamilyStoreEntry file) =>
        entry.Size == file.Size && entry.ModifiedTicks == file.ModifiedUtc.Ticks;

    public static async Task<string> ComputeAsync(IFamilyLibraryStore store, string relative, CancellationToken ct)
    {
        await using var stream = await Task.Run(() => store.OpenRead(relative), ct);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}

// The library folder itself: listing, saving uploads, moving, and handing deletions to the trash.
public sealed class FamilyFilesLibrary(FamilyFilesLocations locations, FamilyFilesIndex index, FamilyFilesTrash trash)
{
    public const int ThumbnailEdge = 400;

    public static bool IsHiddenOrPartial(string name) => name.StartsWith('.') || name.EndsWith(".part", StringComparison.Ordinal);

    public Task<string> RootAsync(CancellationToken ct) => locations.LibraryAsync(ct);

    public Task<IFamilyLibraryStore> StoreAsync(CancellationToken ct) => locations.StoreAsync(ct);

    public async Task<FamilyFolderListing?> ListAsync(string folder, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        return await Task.Run(async () =>
        {
            if (folder.Length == 0) store.CreateFolder("");
            if (store.List(folder) is not { } entries) return null;

            var folders = entries
                .Where(e => e.IsFolder && !e.Name.StartsWith('.') && FamilyFilePaths.IsValidName(e.Name))
                .Select(e => new FamilyFolderEntry(e.Path, e.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var files = new List<FamilyFileEntry>();
            foreach (var file in entries.Where(e => !e.IsFolder && !IsHiddenOrPartial(e.Name) && FamilyFilePaths.IsValidName(e.Name)))
                files.Add(new FamilyFileEntry(file.Path, file.Name, file.Size, file.ModifiedUtc,
                    await index.KnownHashAsync(file, ct), FamilyFilePaths.IsImage(file.Name)));
            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new FamilyFolderListing(folder, folders, files);
        }, ct);
    }

    // Every file under a folder (or the whole library), newest first, optionally filtered by a
    // word in the name. What the apps' search box and "recent" list read.
    public async Task<IReadOnlyList<FamilyFileEntry>> SearchAsync(string? query, int limit, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return await Task.Run(() => store.Walk("")
            .Where(f => !IsHiddenOrPartial(f.Name) && FamilyFilePaths.Normalize(f.Path) == f.Path)
            .Where(f => words.Length == 0 || words.All(w => f.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new FamilyFileEntry(f.Path, f.Name, f.Size, f.ModifiedUtc, null, FamilyFilePaths.IsImage(f.Name)))
            .OrderByDescending(f => f.ModifiedUtc)
            .Take(limit)
            .ToList(), ct);
    }

    public async Task<(int Files, long Bytes)> TotalsAsync(CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        return await Task.Run(() =>
        {
            var count = 0;
            long bytes = 0;
            foreach (var file in store.Walk(""))
            {
                if (IsHiddenOrPartial(file.Name)) continue;
                count++;
                bytes += file.Size;
            }
            return (count, bytes);
        }, ct);
    }

    public async Task<FamilyStoreEntry?> FindAsync(string relative, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        var file = await Task.Run(() => store.Stat(relative), ct);
        return file is { IsFolder: false } && !IsHiddenOrPartial(file.Name) ? file : null;
    }

    public async Task<bool> CreateFolderAsync(string relative, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        return await Task.Run(() =>
        {
            if (store.Stat(relative) is { IsFolder: false }) return false;
            store.CreateFolder(relative);
            return true;
        }, ct);
    }

    public sealed record SaveResult(string Path, long Size, string Sha256, bool AlreadyThere);

    // Streams an upload to a temporary file beside its destination, hashing as it goes, and only
    // then gives it its name. A file already at that name with the same bytes is left alone (the
    // upload is simply acknowledged); one with different bytes makes the new arrival "name (2)",
    // so nothing anyone put in the library is ever overwritten by a later upload.
    public async Task<SaveResult> SaveAsync(string folder, string name, Stream content, long maxBytes, DateTimeOffset? modified, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        await Task.Run(() => store.CreateFolder(folder), ct);

        var temporary = FamilyFilePaths.Combine(folder, $".upload-{Guid.NewGuid():N}.part");
        var placed = false;
        try
        {
            long total = 0;
            string hash;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var output = await Task.Run(() => store.CreateNew(temporary), ct))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new InvalidDataException("too large");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct);
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }

            return await Task.Run(async () =>
            {
                if (store.Stat(temporary)?.Size != total)
                    throw new IOException(store.IsNetwork ? "The file came out a different size on the other computer - its disk may be full." : "The file came out a different size - the disk may be full.");

                var wanted = FamilyFilePaths.Combine(folder, name);
                if (store.Stat(wanted) is { IsFolder: false } existing && existing.Size == total && await index.HashAsync(store, existing, ct) == hash)
                    return new SaveResult(wanted, total, hash, AlreadyThere: true);

                var final = FamilyFilePaths.NextFreeName(folder, name, candidate => store.Stat(candidate) is not null);
                if (modified is { } stamp) store.SetModified(temporary, stamp.UtcDateTime);
                store.Move(temporary, final);
                placed = true;
                if (store.Stat(final) is { } landed) await index.RememberAsync(landed, hash, ct);
                return new SaveResult(final, total, hash, AlreadyThere: false);
            }, ct);
        }
        finally
        {
            if (!placed)
                try { store.DeleteFile(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    // Rename or move a file or folder. Returns the error to show, or null on success.
    public async Task<string?> MoveAsync(string from, string to, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        if (from.Length == 0 || to.Length == 0) return "Choose what to move and where.";
        if (to == from || to.StartsWith(from + "/", StringComparison.Ordinal)) return "A folder can't be moved into itself.";

        return await Task.Run(async () =>
        {
            if (store.Stat(to) is not null) return $"There is already something called {FamilyFilePaths.NameOf(to)} there.";
            switch (store.Stat(from))
            {
                case { IsFolder: false }:
                    store.Move(from, to);
                    await index.MoveAsync(from, to, ct);
                    return null;
                case { IsFolder: true }:
                    var moved = store.Walk(from).Select(f => f.Path).ToList();
                    store.Move(from, to);
                    foreach (var relative in moved)
                        await index.MoveAsync(relative, to + relative[from.Length..], ct);
                    return null;
                default:
                    return "That file or folder isn't in the library any more.";
            }
        }, ct);
    }

    // To the trash, never gone: a file or a whole folder.
    public async Task<FamilyFilesTrash.Batch?> DeleteAsync(string relative, string? by, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        if (relative.Length == 0) return null;
        var found = await Task.Run(() => store.Stat(relative), ct);
        List<string> paths;
        if (found is { IsFolder: false }) paths = [relative];
        else if (found is { IsFolder: true })
            paths = await Task.Run(() => store.Walk(relative).Where(f => !IsHiddenOrPartial(f.Name)).Select(f => f.Path).ToList(), ct);
        else return null;

        var batch = await trash.MoveAsync(store, paths, FamilyFilesTrash.ReasonDeleted, by, ct);
        foreach (var path in paths) await index.ForgetAsync(path, ct);
        if (found.IsFolder) await Task.Run(() => store.DeleteEmptyFolders(relative), ct);
        return batch;
    }

    // A small JPEG for the file lists, made on first request and kept; null for anything that
    // isn't an image ImageSharp can read.
    public async Task<string?> ThumbnailAsync(FamilyStoreEntry file, CancellationToken ct)
    {
        if (!FamilyFilePaths.IsImage(file.Name)) return null;
        var store = await StoreAsync(ct);
        var hash = await index.HashAsync(store, file, ct);
        var thumbnail = Path.Combine(locations.Thumbnails, $"{hash}-{ThumbnailEdge}.jpg");
        if (File.Exists(thumbnail)) return thumbnail;

        Directory.CreateDirectory(locations.Thumbnails);
        var temporary = thumbnail + $".{Guid.NewGuid():N}.part";
        try
        {
            await using var input = await Task.Run(() => store.OpenRead(file.Path), ct);
            using var image = await Image.LoadAsync(SafeImageDecoding.Options, input, ct);
            image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(ThumbnailEdge, ThumbnailEdge)
            }));
            await image.SaveAsJpegAsync(temporary, new JpegEncoder { Quality = 78 }, ct);
            File.Move(temporary, thumbnail, overwrite: true);
            return thumbnail;
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ImageProcessingException)
        {
            return null;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

// Where deleted files and retired duplicates wait. Each deletion is a batch: a folder holding the
// files at their original relative paths, plus a sidecar saying when, why and by whom - so "put it
// back" is a move and the report can say what went where.
public sealed class FamilyFilesTrash(string root)
{
    public const string ReasonDeleted = "deleted";
    public const string ReasonDuplicate = "duplicate";

    public sealed record BatchFile(string Path, long Size, string? KeptCopy = null);
    public sealed record Batch(string Id, DateTimeOffset DeletedUtc, string Reason, string? By, IReadOnlyList<BatchFile> Files);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Root => root;

    public Task<Batch> MoveAsync(IFamilyLibraryStore library, IReadOnlyList<string> paths, string reason, string? by, CancellationToken ct) =>
        MoveAsync(library, paths.Select(p => (p, (string?)null)).ToList(), reason, by, ct);

    // `items` are (relative path, the copy that was kept - duplicates only). The trash is always
    // on this server, so a library on another computer has its deletions copied here first.
    public async Task<Batch> MoveAsync(IFamilyLibraryStore library, IReadOnlyList<(string Path, string? KeptCopy)> items, string reason, string? by, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            var folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);

            var files = new List<BatchFile>();
            var batch = new Batch(id, now, reason, by, files);
            try
            {
                foreach (var (relative, kept) in items)
                {
                    if (await Task.Run(() => library.Stat(relative), ct) is not { IsFolder: false } source) continue;
                    await library.MoveOutAsync(relative, FamilyFilePaths.ToDisk(folder, relative), ct);
                    files.Add(new BatchFile(relative, source.Size, kept));
                }
            }
            finally
            {
                // Written even when a move failed partway (a dropped connection to the library's
                // computer): what did reach the trash must show there, ready to put back.
                await using var stream = File.Create(Path.Combine(root, id + ".json"));
                await JsonSerializer.SerializeAsync(stream, batch with { Files = files.ToList() }, Options, CancellationToken.None);
            }
            return batch with { Files = files.ToList() };
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<Batch>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(root)) return [];
        var batches = new List<Batch>();
        foreach (var sidecar in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(sidecar);
                if (await JsonSerializer.DeserializeAsync<Batch>(stream, Options, ct) is { } batch) batches.Add(batch);
            }
            catch (JsonException) { }
        }
        return batches.OrderByDescending(b => b.DeletedUtc).ToList();
    }

    // Puts one file, or a whole batch, back. A file whose place has been taken since goes back
    // as "name (restored).ext". Returns how many came back.
    public async Task<int> RestoreAsync(IFamilyLibraryStore library, string batchId, string? onlyPath, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var sidecar = Path.Combine(root, batchId + ".json");
            if (!File.Exists(sidecar)) return 0;
            Batch? batch;
            await using (var stream = File.OpenRead(sidecar))
                batch = await JsonSerializer.DeserializeAsync<Batch>(stream, Options, ct);
            if (batch is null) return 0;

            var restored = 0;
            var remaining = new List<BatchFile>();
            foreach (var file in batch.Files)
            {
                if (onlyPath is not null && file.Path != onlyPath) { remaining.Add(file); continue; }
                var source = FamilyFilePaths.ToDisk(Path.Combine(root, batchId), file.Path);
                if (!File.Exists(source)) continue;

                var target = file.Path;
                if (await Task.Run(() => library.Stat(target), ct) is not null)
                {
                    var stem = Path.GetFileNameWithoutExtension(file.Path);
                    var extension = Path.GetExtension(file.Path);
                    target = await Task.Run(() => FamilyFilePaths.NextFreeName(FamilyFilePaths.ParentOf(file.Path), $"{stem} (restored){extension}",
                        candidate => library.Stat(candidate) is not null), ct);
                }
                await library.MoveInAsync(source, target, ct);
                restored++;
            }

            if (remaining.Count == 0)
            {
                File.Delete(sidecar);
                var folder = Path.Combine(root, batchId);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            else
            {
                await using var stream = File.Create(sidecar);
                await JsonSerializer.SerializeAsync(stream, batch with { Files = remaining }, Options, ct);
            }
            return restored;
        }
        finally { _lock.Release(); }
    }

    // Removes batches older than `keep`, or all of them when `keep` is zero.
    public async Task<int> PurgeAsync(TimeSpan keep, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(root)) return 0;
            var cutoff = DateTimeOffset.UtcNow - keep;
            var removed = 0;
            foreach (var batch in await ListUnlockedAsync(ct))
            {
                if (batch.DeletedUtc > cutoff) continue;
                var folder = Path.Combine(root, batch.Id);
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                    File.Delete(Path.Combine(root, batch.Id + ".json"));
                    removed += batch.Files.Count;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }
        finally { _lock.Release(); }
    }

    private async Task<IReadOnlyList<Batch>> ListUnlockedAsync(CancellationToken ct)
    {
        var batches = new List<Batch>();
        foreach (var sidecar in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(sidecar);
                if (await JsonSerializer.DeserializeAsync<Batch>(stream, Options, ct) is { } batch) batches.Add(batch);
            }
            catch (JsonException) { }
        }
        return batches;
    }
}
