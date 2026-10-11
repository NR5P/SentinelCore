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
public sealed record FamilyFolderListing(string Path, IReadOnlyList<FamilyFolderEntry> Folders, IReadOnlyList<FamilyFileEntry> Files);

// Where everything lives. The library folder is a setting (a household with a big disk points it
// there); the index, trash and state stay under the data directory regardless.
public sealed class FamilyFilesLocations(string dataDirectory, AppSettingsFile settings)
{
    public string DefaultLibrary { get; } = Path.Combine(dataDirectory, "family-files", "library");
    public string Trash { get; } = Path.Combine(dataDirectory, "family-files", "trash");
    public string IndexFile { get; } = Path.Combine(dataDirectory, "family-files", "index.json");
    public string StateFile { get; } = Path.Combine(dataDirectory, "family-files", "state.json");
    public string DrivesFile { get; } = Path.Combine(dataDirectory, "family-files", "drives.json");
    public string Thumbnails { get; } = Path.Combine(dataDirectory, "family-files", "thumbnails");
    public string Work { get; } = Path.Combine(dataDirectory, "family-files", "work");

    public async Task<FamilyFilesSettingsDto> OptionsAsync(CancellationToken ct) =>
        await settings.GetSectionAsync<FamilyFilesSettingsDto>(FamilyFilesSettingsDto.SectionName, ct) ?? new FamilyFilesSettingsDto();

    public async Task<string> LibraryAsync(CancellationToken ct)
    {
        var options = await OptionsAsync(ct);
        var folder = options.LibraryFolder?.Trim();
        return string.IsNullOrEmpty(folder) || !Path.IsPathRooted(folder) ? DefaultLibrary : folder;
    }
}

// Cached SHA-256 of every file in the library, keyed by relative path and trusted only while the
// file's size and modification time still match - so a nightly pass over a library that hasn't
// changed reads no file contents at all, and one that has only reads what's new.
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

    public async Task<string?> KnownHashAsync(string relative, FileInfo file, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await EntriesAsync(ct);
            return entries.TryGetValue(relative, out var entry) && Matches(entry, file) ? entry.Sha256 : null;
        }
        finally { _lock.Release(); }
    }

    // The file's hash, computed and remembered if the index doesn't already know it.
    public async Task<string> HashAsync(string relative, FileInfo file, CancellationToken ct)
    {
        if (await KnownHashAsync(relative, file, ct) is { } known) return known;
        var hash = await ComputeAsync(file.FullName, ct);
        await RememberAsync(relative, file, hash, ct);
        return hash;
    }

    public async Task RememberAsync(string relative, FileInfo file, string hash, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await EntriesAsync(ct);
            entries[relative] = new Entry(file.Length, file.LastWriteTimeUtc.Ticks, hash);
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

    // Walks the whole library: hashes anything new or changed, drops anything gone, saves, and
    // returns every file with its hash. `progress` gets (done, total) as it goes.
    public async Task<IReadOnlyList<FamilyFileEntry>> RescanAsync(string root, Action<int, int>? progress, CancellationToken ct)
    {
        var files = Directory.Exists(root)
            ? new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !FamilyFilesLibrary.IsHiddenOrPartial(f.Name))
                .ToList()
            : [];

        var result = new List<FamilyFileEntry>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = FamilyFilePaths.FromDisk(root, file.FullName);
            if (FamilyFilePaths.Normalize(relative) != relative) { done++; continue; }
            seen.Add(relative);
            string hash;
            try
            {
                hash = await HashAsync(relative, file, ct);
            }
            catch (IOException) { done++; continue; }
            catch (UnauthorizedAccessException) { done++; continue; }
            result.Add(new FamilyFileEntry(relative, file.Name, file.Length, file.LastWriteTimeUtc, hash, FamilyFilePaths.IsImage(file.Name)));
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
        return result;
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

    private static bool Matches(Entry entry, FileInfo file) =>
        entry.Size == file.Length && entry.ModifiedTicks == file.LastWriteTimeUtc.Ticks;

    public static async Task<string> ComputeAsync(string fullPath, CancellationToken ct)
    {
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
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

    public async Task<FamilyFolderListing?> ListAsync(string folder, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var disk = FamilyFilePaths.ToDisk(root, folder);
        if (folder.Length == 0) Directory.CreateDirectory(disk);
        if (!Directory.Exists(disk)) return null;

        var directory = new DirectoryInfo(disk);
        var folders = directory.EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.') && FamilyFilePaths.IsValidName(d.Name))
            .Select(d => new FamilyFolderEntry(FamilyFilePaths.Combine(folder, d.Name), d.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = new List<FamilyFileEntry>();
        foreach (var file in directory.EnumerateFiles().Where(f => !IsHiddenOrPartial(f.Name) && FamilyFilePaths.IsValidName(f.Name)))
        {
            var relative = FamilyFilePaths.Combine(folder, file.Name);
            files.Add(new FamilyFileEntry(relative, file.Name, file.Length, file.LastWriteTimeUtc,
                await index.KnownHashAsync(relative, file, ct), FamilyFilePaths.IsImage(file.Name)));
        }
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new FamilyFolderListing(folder, folders, files);
    }

    // Every file under a folder (or the whole library), newest first, optionally filtered by a
    // word in the name. What the apps' search box and "recent" list read.
    public async Task<IReadOnlyList<FamilyFileEntry>> SearchAsync(string? query, int limit, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        if (!Directory.Exists(root)) return [];
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<FamilyFileEntry>();
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (IsHiddenOrPartial(file.Name)) continue;
            var relative = FamilyFilePaths.FromDisk(root, file.FullName);
            if (FamilyFilePaths.Normalize(relative) != relative) continue;
            if (words.Length > 0 && !words.All(w => relative.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            matches.Add(new FamilyFileEntry(relative, file.Name, file.Length, file.LastWriteTimeUtc, null, FamilyFilePaths.IsImage(file.Name)));
        }
        return matches.OrderByDescending(f => f.ModifiedUtc).Take(limit).ToList();
    }

    public async Task<(int Files, long Bytes)> TotalsAsync(CancellationToken ct)
    {
        var root = await RootAsync(ct);
        if (!Directory.Exists(root)) return (0, 0);
        var count = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (IsHiddenOrPartial(file.Name)) continue;
            count++;
            bytes += file.Length;
        }
        return (count, bytes);
    }

    public async Task<FileInfo?> FindAsync(string relative, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var file = new FileInfo(FamilyFilePaths.ToDisk(root, relative));
        return file.Exists && !IsHiddenOrPartial(file.Name) ? file : null;
    }

    public async Task<bool> CreateFolderAsync(string relative, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var disk = FamilyFilePaths.ToDisk(root, relative);
        if (File.Exists(disk)) return false;
        Directory.CreateDirectory(disk);
        return true;
    }

    public sealed record SaveResult(string Path, long Size, string Sha256, bool AlreadyThere);

    // Streams an upload to a temporary file beside its destination, hashing as it goes, and only
    // then gives it its name. A file already at that name with the same bytes is left alone (the
    // upload is simply acknowledged); one with different bytes makes the new arrival "name (2)",
    // so nothing anyone put in the library is ever overwritten by a later upload.
    public async Task<SaveResult> SaveAsync(string folder, string name, Stream content, long maxBytes, DateTimeOffset? modified, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var directory = FamilyFilePaths.ToDisk(root, folder);
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".upload-{Guid.NewGuid():N}.part");
        try
        {
            long total = 0;
            string hash;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
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

            var wanted = FamilyFilePaths.Combine(folder, name);
            var existing = new FileInfo(FamilyFilePaths.ToDisk(root, wanted));
            if (existing.Exists && existing.Length == total && await index.HashAsync(wanted, existing, ct) == hash)
                return new SaveResult(wanted, total, hash, AlreadyThere: true);

            var final = FamilyFilePaths.NextFreeName(folder, name, candidate => File.Exists(FamilyFilePaths.ToDisk(root, candidate)));
            var target = FamilyFilePaths.ToDisk(root, final);
            if (modified is { } stamp)
            {
                try { File.SetLastWriteTimeUtc(temporary, stamp.UtcDateTime); }
                catch (IOException) { }
            }
            File.Move(temporary, target);
            await index.RememberAsync(final, new FileInfo(target), hash, ct);
            return new SaveResult(final, total, hash, AlreadyThere: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Rename or move a file or folder. Returns the error to show, or null on success.
    public async Task<string?> MoveAsync(string from, string to, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        if (from.Length == 0 || to.Length == 0) return "Choose what to move and where.";
        if (to == from || to.StartsWith(from + "/", StringComparison.Ordinal)) return "A folder can't be moved into itself.";

        var source = FamilyFilePaths.ToDisk(root, from);
        var target = FamilyFilePaths.ToDisk(root, to);
        if (File.Exists(target) || Directory.Exists(target)) return $"There is already something called {FamilyFilePaths.NameOf(to)} there.";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(source))
        {
            File.Move(source, target);
            await index.MoveAsync(from, to, ct);
            return null;
        }
        if (Directory.Exists(source))
        {
            var moved = new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => FamilyFilePaths.FromDisk(root, f.FullName)).ToList();
            Directory.Move(source, target);
            foreach (var relative in moved)
                await index.MoveAsync(relative, to + relative[from.Length..], ct);
            return null;
        }
        return "That file or folder isn't in the library any more.";
    }

    // To the trash, never gone: a file or a whole folder.
    public async Task<FamilyFilesTrash.Batch?> DeleteAsync(string relative, string? by, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        if (relative.Length == 0) return null;
        var disk = FamilyFilePaths.ToDisk(root, relative);
        List<string> paths;
        if (File.Exists(disk)) paths = [relative];
        else if (Directory.Exists(disk))
            paths = new DirectoryInfo(disk).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !IsHiddenOrPartial(f.Name))
                .Select(f => FamilyFilePaths.FromDisk(root, f.FullName)).ToList();
        else return null;

        var batch = await trash.MoveAsync(root, paths, FamilyFilesTrash.ReasonDeleted, by, ct);
        foreach (var path in paths) await index.ForgetAsync(path, ct);
        if (Directory.Exists(disk)) TryDeleteEmptyTree(disk);
        return batch;
    }

    // A small JPEG for the file lists, made on first request and kept; null for anything that
    // isn't an image ImageSharp can read.
    public async Task<string?> ThumbnailAsync(string relative, FileInfo file, CancellationToken ct)
    {
        if (!FamilyFilePaths.IsImage(file.Name)) return null;
        var hash = await index.HashAsync(relative, file, ct);
        var thumbnail = Path.Combine(locations.Thumbnails, $"{hash}-{ThumbnailEdge}.jpg");
        if (File.Exists(thumbnail)) return thumbnail;

        Directory.CreateDirectory(locations.Thumbnails);
        var temporary = thumbnail + $".{Guid.NewGuid():N}.part";
        try
        {
            using var image = await Image.LoadAsync(SafeImageDecoding.Options, file.FullName, ct);
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

    internal static void TryDeleteEmptyTree(string directory)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory)) TryDeleteEmptyTree(child);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    public Task<Batch> MoveAsync(string library, IReadOnlyList<string> paths, string reason, string? by, CancellationToken ct) =>
        MoveAsync(library, paths.Select(p => (p, (string?)null)).ToList(), reason, by, ct);

    // `items` are (relative path, the copy that was kept - duplicates only).
    public async Task<Batch> MoveAsync(string library, IReadOnlyList<(string Path, string? KeptCopy)> items, string reason, string? by, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            var folder = Path.Combine(root, id);
            Directory.CreateDirectory(folder);

            var files = new List<BatchFile>();
            foreach (var (relative, kept) in items)
            {
                var source = FamilyFilePaths.ToDisk(library, relative);
                if (!File.Exists(source)) continue;
                var target = FamilyFilePaths.ToDisk(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var size = new FileInfo(source).Length;
                File.Move(source, target, overwrite: true);
                files.Add(new BatchFile(relative, size, kept));
            }

            var batch = new Batch(id, now, reason, by, files);
            await using (var stream = File.Create(Path.Combine(root, id + ".json")))
                await JsonSerializer.SerializeAsync(stream, batch, Options, ct);
            return batch;
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
    public async Task<int> RestoreAsync(string library, string batchId, string? onlyPath, CancellationToken ct)
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

                var target = FamilyFilePaths.ToDisk(library, file.Path);
                if (File.Exists(target))
                {
                    var stem = Path.GetFileNameWithoutExtension(file.Path);
                    var extension = Path.GetExtension(file.Path);
                    target = FamilyFilePaths.ToDisk(library,
                        FamilyFilePaths.NextFreeName(FamilyFilePaths.ParentOf(file.Path), $"{stem} (restored){extension}",
                            candidate => File.Exists(FamilyFilePaths.ToDisk(library, candidate))));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target);
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
