using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace SentinelCore.Web;

// BACKUP DRIVES for the family file library (FamilyFiles.cs): each one is a folder - a USB disk,
// a mounted network share, a \\server\share path - or an SFTP account on another computer, and
// the library is mirrored to every enabled drive each night and on demand.
//
// A drive holds a plain copy of the library tree plus one manifest file listing what this feature
// wrote there. The manifest is what makes deletion safe: a file that has left the library is
// removed from the drive only if the manifest says we put it there, so pointing a drive at a
// folder that already has other things in it never costs those things. "Restore" runs the other
// way - anything on the drive that the library lacks is copied back - which is how a library is
// rebuilt on a new machine from whichever drive survived.

public sealed class FamilyDrive : IValidatableObject
{
    public const string KindFolder = "folder";
    public const string KindSftp = "sftp";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = KindFolder;
    public bool Enabled { get; set; } = true;

    public string FolderPath { get; set; } = "";

    public string SftpHost { get; set; } = "";
    public int SftpPort { get; set; } = 22;
    public string SftpUsername { get; set; } = "";
    public string SftpPassword { get; set; } = "";
    public string SftpFolder { get; set; } = "";
    public string SftpHostKeyFingerprint { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }

    public bool IsSftp => string.Equals(Kind, KindSftp, StringComparison.OrdinalIgnoreCase);

    // Where it points, in words, with nothing secret in it.
    public string Location => IsSftp
        ? $"sftp://{SftpHost.Trim()}{(SftpPort == 22 ? "" : $":{SftpPort}")}/{SftpFolder.Trim().TrimStart('/')}"
        : FolderPath.Trim();

    public FamilyDrive Normalized()
    {
        Name = Name.Trim();
        Kind = Kind.Trim().ToLowerInvariant();
        FolderPath = FolderPath.Trim();
        SftpHost = SftpHost.Trim();
        SftpUsername = SftpUsername.Trim();
        SftpFolder = SftpFolder.Trim();
        SftpHostKeyFingerprint = SftpHostKeyFingerprint.Trim();
        return this;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name))
            yield return new ValidationResult("Give the drive a name, e.g. \"USB drive\" or \"NAS\".", [nameof(Name)]);

        if (Kind == KindFolder)
        {
            if (string.IsNullOrWhiteSpace(FolderPath))
                yield return new ValidationResult("Choose the folder on the drive to back up to.", [nameof(FolderPath)]);
            else if (!Path.IsPathRooted(FolderPath.Trim()))
                yield return new ValidationResult(
                    "The folder must be a full path, e.g. /mnt/usb/Family Files or \\\\nas\\backups\\family.", [nameof(FolderPath)]);
        }
        else if (Kind == KindSftp)
        {
            if (string.IsNullOrWhiteSpace(SftpHost))
                yield return new ValidationResult("Enter the address of the computer to send to.", [nameof(SftpHost)]);
            if (SftpPort is < 1 or > 65535)
                yield return new ValidationResult("The SFTP port must be between 1 and 65535.", [nameof(SftpPort)]);
            if (string.IsNullOrWhiteSpace(SftpUsername))
                yield return new ValidationResult("Enter the username for that computer.", [nameof(SftpUsername)]);
            if (string.IsNullOrEmpty(SftpPassword))
                yield return new ValidationResult("Enter the password for that account.", [nameof(SftpPassword)]);
            if (!string.IsNullOrWhiteSpace(SftpHostKeyFingerprint) && !AutoExportSftp.IsFingerprint(SftpHostKeyFingerprint))
                yield return new ValidationResult(
                    "The host key fingerprint should look like SHA256:AbC...= (43 characters after the prefix), as ssh prints it.",
                    [nameof(SftpHostKeyFingerprint)]);
        }
        else
        {
            yield return new ValidationResult("The drive has to be a folder or an SFTP server.", [nameof(Kind)]);
        }
    }

    // What every signed-in person may see about a drive: no password, no fingerprint.
    public object Public(FamilyDriveState? state, bool admin) => new
    {
        id = Id,
        name = Name,
        kind = Kind,
        enabled = Enabled,
        location = Location,
        folderPath = admin ? FolderPath : null,
        sftpHost = admin ? SftpHost : null,
        sftpPort = admin ? SftpPort : (int?)null,
        sftpUsername = admin ? SftpUsername : null,
        sftpFolder = admin ? SftpFolder : null,
        sftpHostKeyFingerprint = admin ? SftpHostKeyFingerprint : null,
        hasPassword = admin ? !string.IsNullOrEmpty(SftpPassword) : (bool?)null,
        lastAttemptUtc = state?.LastAttemptUtc,
        lastSuccessUtc = state?.LastSuccessUtc,
        lastResult = state?.LastResult ?? "",
        lastMessage = state?.LastMessage ?? "",
        lastFileCount = state?.LastFileCount,
        lastBytes = state?.LastBytes,
        lastCopied = state?.LastCopied,
        lastRemoved = state?.LastRemoved,
        lastFreeBytes = state?.LastFreeBytes,
        lastHostKeyFingerprint = state?.LastHostKeyFingerprint
    };
}

public sealed record FamilyDriveState
{
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    // "ok" or "failed".
    public string LastResult { get; init; } = "";
    public string LastMessage { get; init; } = "";
    public int? LastFileCount { get; init; }
    public long? LastBytes { get; init; }
    public int? LastCopied { get; init; }
    public int? LastRemoved { get; init; }
    public long? LastFreeBytes { get; init; }
    public string? LastHostKeyFingerprint { get; init; }
}

// The drives, in a JSON file of their own under the data directory. Not a settings section: the
// settings form is flat, and this is a list.
public sealed class FamilyDriveStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<FamilyDrive>> GetAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { return await LoadAsync(ct); }
        finally { _lock.Release(); }
    }

    public async Task<FamilyDrive?> FindAsync(string id, CancellationToken ct) =>
        (await GetAllAsync(ct)).FirstOrDefault(d => d.Id == id);

    public async Task<FamilyDrive> UpsertAsync(FamilyDrive drive, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var drives = await LoadAsync(ct);
            var index = drives.FindIndex(d => d.Id == drive.Id);
            if (index < 0)
            {
                if (string.IsNullOrWhiteSpace(drive.Id)) drive.Id = Guid.NewGuid().ToString("N");
                drive.CreatedUtc = drive.CreatedUtc == default ? DateTimeOffset.UtcNow : drive.CreatedUtc;
                drives.Add(drive);
            }
            else
            {
                // A saved form sends the password back blank when it hasn't been changed.
                if (string.IsNullOrEmpty(drive.SftpPassword)) drive.SftpPassword = drives[index].SftpPassword;
                drive.CreatedUtc = drives[index].CreatedUtc;
                drives[index] = drive;
            }
            await SaveAsync(drives, ct);
            return drive;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var drives = await LoadAsync(ct);
            var removed = drives.RemoveAll(d => d.Id == id) > 0;
            if (removed) await SaveAsync(drives, ct);
            return removed;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<FamilyDrive>> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return [];
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<FamilyDrive>>(stream, Options, ct) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveAsync(List<FamilyDrive> drives, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, drives, Options, ct);
        File.Move(temporary, path, overwrite: true);
    }
}

// What a drive remembers about the copy it holds. Written last, after the files, so a sync that
// dies halfway leaves the previous manifest and the next run simply re-checks.
public sealed class FamilyDriveManifest
{
    public const string FileName = ".sentinelcore-family-files.json";

    public sealed record Entry(long Size, string Sha256);

    public int Version { get; set; } = 1;
    public DateTimeOffset WrittenUtc { get; set; }
    public Dictionary<string, Entry> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed record FamilyDriveSyncResult(
    bool Ok, string Message, int FileCount, long Bytes, int Copied, int Removed, int Failed, long? FreeBytes, string? HostKey);

public sealed record FamilyDriveRestoreResult(bool Ok, string Message, int Copied, long Bytes, int Skipped, int Failed);

// One drive, opened. Everything a sync or restore needs, with the folder and SFTP cases behind it.
public interface IFamilyDriveTarget : IAsyncDisposable
{
    string? HostKey { get; }
    Task OpenAsync(CancellationToken ct);
    Task<FamilyDriveManifest> ReadManifestAsync(CancellationToken ct);
    Task WriteManifestAsync(FamilyDriveManifest manifest, CancellationToken ct);
    Task<long?> SizeOfAsync(string relative, CancellationToken ct);
    Task UploadAsync(string localPath, string relative, long bytes, CancellationToken ct);
    Task DownloadAsync(string relative, string localPath, CancellationToken ct);
    Task DeleteAsync(string relative, CancellationToken ct);
    IAsyncEnumerable<(string Relative, long Size)> WalkAsync(CancellationToken ct);
    Task<long?> FreeBytesAsync(CancellationToken ct);
}

public static class FamilyDriveTargets
{
    public static IFamilyDriveTarget Open(FamilyDrive drive) =>
        drive.IsSftp ? new SftpFamilyDriveTarget(drive) : new FolderFamilyDriveTarget(drive.FolderPath.Trim());

    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web);

    public static FamilyDriveManifest ParseManifest(Stream stream)
    {
        try
        {
            return JsonSerializer.Deserialize<FamilyDriveManifest>(stream, ManifestOptions) ?? new FamilyDriveManifest();
        }
        catch (JsonException)
        {
            // An unreadable manifest means "we don't know what we wrote": every file is
            // re-checked and nothing is deleted this run.
            return new FamilyDriveManifest();
        }
    }

    public static byte[] SerializeManifest(FamilyDriveManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestOptions);
}

public sealed class FolderFamilyDriveTarget(string root) : IFamilyDriveTarget
{
    public string? HostKey => null;

    public Task OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public async Task<FamilyDriveManifest> ReadManifestAsync(CancellationToken ct)
    {
        var path = Path.Combine(root, FamilyDriveManifest.FileName);
        if (!File.Exists(path)) return new FamilyDriveManifest();
        await using var stream = File.OpenRead(path);
        return FamilyDriveTargets.ParseManifest(stream);
    }

    public async Task WriteManifestAsync(FamilyDriveManifest manifest, CancellationToken ct)
    {
        var path = Path.Combine(root, FamilyDriveManifest.FileName);
        await File.WriteAllBytesAsync(path + ".partial", FamilyDriveTargets.SerializeManifest(manifest), ct);
        File.Move(path + ".partial", path, overwrite: true);
    }

    public Task<long?> SizeOfAsync(string relative, CancellationToken ct)
    {
        var file = new FileInfo(FamilyFilePaths.ToDisk(root, relative));
        return Task.FromResult(file.Exists ? file.Length : (long?)null);
    }

    public async Task UploadAsync(string localPath, string relative, long bytes, CancellationToken ct)
    {
        var target = FamilyFilePaths.ToDisk(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        await using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await input.CopyToAsync(output, ct);
            await output.FlushAsync(ct);
        }
        if (new FileInfo(partial).Length != bytes)
        {
            File.Delete(partial);
            throw new FamilyDriveException("The copy came out a different size from the original - the drive may be full.");
        }
        try { File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(localPath)); }
        catch (IOException) { }
        File.Move(partial, target, overwrite: true);
    }

    public async Task DownloadAsync(string relative, string localPath, CancellationToken ct)
    {
        await using var input = new FileStream(FamilyFilePaths.ToDisk(root, relative), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await input.CopyToAsync(output, ct);
    }

    public Task DeleteAsync(string relative, CancellationToken ct)
    {
        var target = FamilyFilePaths.ToDisk(root, relative);
        if (File.Exists(target)) File.Delete(target);
        var parent = Path.GetDirectoryName(target);
        while (parent is not null && parent.Length > root.Length)
        {
            try
            {
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                else break;
            }
            catch (IOException) { break; }
            catch (UnauthorizedAccessException) { break; }
            parent = Path.GetDirectoryName(parent);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(string Relative, long Size)> WalkAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (FamilyFilesLibrary.IsHiddenOrPartial(file.Name) || file.Name.EndsWith(".partial", StringComparison.Ordinal)) continue;
            var relative = FamilyFilePaths.FromDisk(root, file.FullName);
            if (FamilyFilePaths.Normalize(relative) != relative) continue;
            yield return (relative, file.Length);
        }
        await Task.CompletedTask;
    }

    public Task<long?> FreeBytesAsync(CancellationToken ct)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? root);
            return Task.FromResult<long?>(drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return Task.FromResult<long?>(null);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SftpFamilyDriveTarget(FamilyDrive drive) : IFamilyDriveTarget
{
    private SftpClient? _client;
    private string _folder = "";
    private string? _seen;

    public string? HostKey => _seen is { } seen ? "SHA256:" + seen : null;

    public async Task OpenAsync(CancellationToken ct)
    {
        var expected = string.IsNullOrWhiteSpace(drive.SftpHostKeyFingerprint) ? null : Normalise(drive.SftpHostKeyFingerprint);
        var client = new SftpClient(drive.SftpHost.Trim(), drive.SftpPort, drive.SftpUsername.Trim(), drive.SftpPassword)
        {
            OperationTimeout = TimeSpan.FromMinutes(5)
        };
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
        client.HostKeyReceived += (_, e) =>
        {
            _seen = e.FingerPrintSHA256;
            e.CanTrust = expected is null || string.Equals(expected, e.FingerPrintSHA256, StringComparison.Ordinal);
        };

        try
        {
            await client.ConnectAsync(ct);
        }
        catch (SshConnectionException) when (expected is not null && _seen is { } actual && !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            client.Dispose();
            throw new FamilyDriveHostKeyException(
                $"The SFTP server's host key is SHA256:{actual}, not the SHA256:{expected} saved for this drive, so nothing was sent. " +
                "If you replaced or reinstalled that computer, paste the new fingerprint into the drive; if you didn't, something else may be answering at that address.",
                "SHA256:" + actual);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _folder = await EnsureFolderAsync(client, drive.SftpFolder, ct);
    }

    private SftpClient Client => _client ?? throw new InvalidOperationException("The drive hasn't been opened.");

    private string Remote(string relative) => _folder.Length == 0 ? relative : $"{_folder}/{relative}";

    public async Task<FamilyDriveManifest> ReadManifestAsync(CancellationToken ct)
    {
        var path = Remote(FamilyDriveManifest.FileName);
        if (!await Client.ExistsAsync(path, ct)) return new FamilyDriveManifest();
        using var buffer = new MemoryStream();
        await Client.DownloadFileAsync(path, buffer, ct);
        buffer.Position = 0;
        return FamilyDriveTargets.ParseManifest(buffer);
    }

    public async Task WriteManifestAsync(FamilyDriveManifest manifest, CancellationToken ct)
    {
        var path = Remote(FamilyDriveManifest.FileName);
        using var buffer = new MemoryStream(FamilyDriveTargets.SerializeManifest(manifest));
        await Client.UploadFileAsync(buffer, path + ".partial", ct);
        if (await Client.ExistsAsync(path, ct)) await Client.DeleteFileAsync(path, ct);
        await Client.RenameFileAsync(path + ".partial", path, ct);
    }

    public async Task<long?> SizeOfAsync(string relative, CancellationToken ct)
    {
        var path = Remote(relative);
        if (!await Client.ExistsAsync(path, ct)) return null;
        return (await Client.GetAttributesAsync(path, ct)).Size;
    }

    public async Task UploadAsync(string localPath, string relative, long bytes, CancellationToken ct)
    {
        var path = Remote(relative);
        var parent = FamilyFilePaths.ParentOf(relative);
        if (parent.Length > 0) await EnsureFolderAsync(Client, Remote(parent), ct);

        var partial = path + ".partial";
        await using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await Client.UploadFileAsync(input, partial, ct);

        var uploaded = await Client.GetAttributesAsync(partial, ct);
        if (uploaded.Size != bytes)
        {
            await Client.DeleteFileAsync(partial, ct);
            throw new FamilyDriveException("The file on the SFTP server came out a different size from the original - its disk may be full.");
        }
        if (await Client.ExistsAsync(path, ct)) await Client.DeleteFileAsync(path, ct);
        await Client.RenameFileAsync(partial, path, ct);
    }

    public async Task DownloadAsync(string relative, string localPath, CancellationToken ct)
    {
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await Client.DownloadFileAsync(Remote(relative), output, ct);
    }

    public async Task DeleteAsync(string relative, CancellationToken ct)
    {
        var path = Remote(relative);
        if (await Client.ExistsAsync(path, ct)) await Client.DeleteFileAsync(path, ct);

        var parent = FamilyFilePaths.ParentOf(relative);
        while (parent.Length > 0)
        {
            var remote = Remote(parent);
            var empty = true;
            await foreach (var entry in Client.ListDirectoryAsync(remote, ct))
            {
                if (entry.Name is "." or "..") continue;
                empty = false;
                break;
            }
            if (!empty) break;
            try { await Client.DeleteDirectoryAsync(remote, ct); }
            catch (SshException) { break; }
            parent = FamilyFilePaths.ParentOf(parent);
        }
    }

    public async IAsyncEnumerable<(string Relative, long Size)> WalkAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            var remote = folder.Length == 0 ? (_folder.Length == 0 ? "." : _folder) : Remote(folder);
            var entries = new List<ISftpFile>();
            await foreach (var entry in Client.ListDirectoryAsync(remote, ct)) entries.Add(entry);
            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..") continue;
                if (!FamilyFilePaths.IsValidName(entry.Name)) continue;
                var relative = FamilyFilePaths.Combine(folder, entry.Name);
                if (entry.IsDirectory) pending.Push(relative);
                else if (entry.IsRegularFile && !entry.Name.EndsWith(".partial", StringComparison.Ordinal))
                    yield return (relative, entry.Length);
            }
        }
    }

    public async Task<long?> FreeBytesAsync(CancellationToken ct)
    {
        try
        {
            var status = await Client.GetStatusAsync(_folder.Length == 0 ? "." : _folder, ct);
            return (long)status.AvailableBlocks * (long)status.BlockSize;
        }
        catch (SshException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }

    private static string Normalise(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("SHA256:", StringComparison.Ordinal)) trimmed = trimmed["SHA256:".Length..];
        return trimmed.TrimEnd('=');
    }

    private static async Task<string> EnsureFolderAsync(SftpClient client, string? folder, CancellationToken ct)
    {
        var trimmed = folder?.Trim().TrimEnd('/') ?? "";
        if (trimmed.Length == 0) return "";

        var built = trimmed.StartsWith('/') ? "/" : "";
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            built = built.Length == 0 || built == "/" ? built + segment : built + "/" + segment;
            if (!await client.ExistsAsync(built, ct)) await client.CreateDirectoryAsync(built, ct);
        }
        return trimmed;
    }
}

public class FamilyDriveException(string message) : Exception(message);

public sealed class FamilyDriveHostKeyException(string message, string seen) : FamilyDriveException(message)
{
    public string Seen { get; } = seen;
}

// The mirror itself, and its reverse.
public static class FamilyDriveSync
{
    public delegate void Progress(int done, int total, string current);

    // Mirrors `library` (every file with its hash) onto the drive. Files already there with the
    // same hash and size are left alone; everything else is copied; files the manifest says we
    // wrote that the library no longer has are removed.
    public static async Task<FamilyDriveSyncResult> MirrorAsync(
        FamilyDrive drive, string libraryRoot, IReadOnlyList<FamilyFileEntry> library, Progress? progress, ILogger logger, CancellationToken ct)
    {
        await using var target = FamilyDriveTargets.Open(drive);
        await target.OpenAsync(ct);
        var manifest = await target.ReadManifestAsync(ct);
        var wanted = library.Where(f => f.Sha256 is not null).ToDictionary(f => f.Path, StringComparer.Ordinal);

        var copied = 0;
        var failed = 0;
        long bytes = 0;
        var done = 0;
        var total = wanted.Count;
        var manifestDirty = false;
        var lastManifestSave = DateTimeOffset.UtcNow;
        var firstFailure = (string?)null;

        foreach (var file in wanted.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            bytes += file.Size;
            progress?.Invoke(done, total, file.Path);
            done++;

            if (manifest.Files.TryGetValue(file.Path, out var known) && known.Sha256 == file.Sha256 && known.Size == file.Size)
            {
                // Cheap spot-check that the drive still has it: a wiped or swapped drive with an
                // old manifest on it would otherwise look complete.
                if (await target.SizeOfAsync(file.Path, ct) == file.Size) continue;
            }

            try
            {
                await target.UploadAsync(FamilyFilePaths.ToDisk(libraryRoot, file.Path), file.Path, file.Size, ct);
                manifest.Files[file.Path] = new FamilyDriveManifest.Entry(file.Size, file.Sha256!);
                manifestDirty = true;
                copied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SshException or FamilyDriveException)
            {
                failed++;
                firstFailure ??= $"{file.Path}: {exception.Message}";
                logger.LogWarning(exception, "Could not copy {File} to drive {Drive}", file.Path, drive.Name);
                if (exception is SshConnectionException) throw;
            }

            if (manifestDirty && DateTimeOffset.UtcNow - lastManifestSave > TimeSpan.FromMinutes(1))
            {
                await target.WriteManifestAsync(manifest, ct);
                manifestDirty = false;
                lastManifestSave = DateTimeOffset.UtcNow;
            }
        }

        // Only after every copy succeeded: a night with copy failures is not one to also delete on.
        var removed = 0;
        if (failed == 0)
        {
            foreach (var gone in manifest.Files.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await target.DeleteAsync(gone, ct);
                    manifest.Files.Remove(gone);
                    manifestDirty = true;
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SshException)
                {
                    logger.LogWarning(exception, "Could not remove {File} from drive {Drive}", gone, drive.Name);
                }
            }
        }

        manifest.WrittenUtc = DateTimeOffset.UtcNow;
        await target.WriteManifestAsync(manifest, ct);
        var free = await target.FreeBytesAsync(ct);

        var ok = failed == 0;
        var message = ok
            ? $"{wanted.Count} file(s), {AutoExportRunner.FormatBytes(bytes)} on the drive" +
              (copied > 0 ? $"; copied {copied}" : "; nothing new to copy") +
              (removed > 0 ? $"; removed {removed} no longer in the library" : "") + "."
            : $"{failed} file(s) could not be copied ({firstFailure}). {copied} copied; nothing was removed from the drive.";
        return new FamilyDriveSyncResult(ok, message, wanted.Count, bytes, copied, removed, failed, free, target.HostKey);
    }

    // Copies anything on the drive that the library lacks back into the library. Nothing in the
    // library is touched, and a file present in both at different sizes comes back as "name (from
    // drive).ext" so the two can be compared.
    public static async Task<FamilyDriveRestoreResult> RestoreAsync(
        FamilyDrive drive, string libraryRoot, string workDirectory, Progress? progress, ILogger logger, CancellationToken ct)
    {
        await using var target = FamilyDriveTargets.Open(drive);
        await target.OpenAsync(ct);
        Directory.CreateDirectory(workDirectory);

        var files = new List<(string Relative, long Size)>();
        await foreach (var entry in target.WalkAsync(ct)) files.Add(entry);

        var copied = 0;
        var skipped = 0;
        var failed = 0;
        long bytes = 0;
        var done = 0;
        foreach (var (relative, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(done++, files.Count, relative);

            var destination = FamilyFilePaths.ToDisk(libraryRoot, relative);
            if (File.Exists(destination))
            {
                if (new FileInfo(destination).Length == size) { skipped++; continue; }
                var stem = Path.GetFileNameWithoutExtension(relative);
                var extension = Path.GetExtension(relative);
                destination = FamilyFilePaths.ToDisk(libraryRoot, FamilyFilePaths.NextFreeName(
                    FamilyFilePaths.ParentOf(relative), $"{stem} (from drive){extension}",
                    candidate => File.Exists(FamilyFilePaths.ToDisk(libraryRoot, candidate))));
            }

            var temporary = Path.Combine(workDirectory, $"restore-{Guid.NewGuid():N}.part");
            try
            {
                await target.DownloadAsync(relative, temporary, ct);
                if (new FileInfo(temporary).Length != size) throw new FamilyDriveException("the copy came back a different size");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(temporary, destination);
                bytes += size;
                copied++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SshException or FamilyDriveException)
            {
                failed++;
                logger.LogWarning(exception, "Could not restore {File} from drive {Drive}", relative, drive.Name);
                if (exception is SshConnectionException) throw;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        var ok = failed == 0;
        var message = copied == 0 && failed == 0
            ? $"The library already has everything on this drive ({skipped} file(s))."
            : $"Brought back {copied} file(s) ({AutoExportRunner.FormatBytes(bytes)}); {skipped} already in the library" +
              (failed > 0 ? $"; {failed} could not be read" : "") + ".";
        return new FamilyDriveRestoreResult(ok, message, copied, bytes, skipped, failed);
    }

    // Connects, makes sure the folder is writable, and reports what it saw.
    public static async Task<(bool Ok, string Message, string? HostKey)> TestAsync(FamilyDrive drive, CancellationToken ct)
    {
        await using var target = FamilyDriveTargets.Open(drive);
        await target.OpenAsync(ct);
        var manifest = await target.ReadManifestAsync(ct);
        var free = await target.FreeBytesAsync(ct);
        var message = manifest.Files.Count > 0
            ? $"Reached {drive.Location}: it holds {manifest.Files.Count} file(s) from this library, last written {manifest.WrittenUtc:yyyy-MM-dd HH:mm} UTC."
            : $"Reached {drive.Location}: nothing from this library on it yet.";
        if (free is { } bytes) message += $" {AutoExportRunner.FormatBytes(bytes)} free.";
        return (true, message, target.HostKey);
    }

    public static string Describe(Exception exception) => exception switch
    {
        FamilyDriveException => exception.Message,
        SshAuthenticationException => "The SFTP server refused the username or password.",
        SshConnectionException or System.Net.Sockets.SocketException => $"Could not connect to the SFTP server: {exception.Message}",
        SftpPermissionDeniedException => $"The SFTP server refused to let this account write there: {exception.Message}",
        SftpPathNotFoundException => $"The SFTP server has no such folder: {exception.Message}",
        SshException => $"The SFTP server reported a problem: {exception.Message}",
        UnauthorizedAccessException => $"No permission to write to that folder: {exception.Message}",
        DirectoryNotFoundException => $"That folder isn't there - is the drive plugged in or the share mounted? ({exception.Message})",
        IOException => $"Could not write to the drive: {exception.Message}",
        _ => $"The backup failed: {exception.Message}"
    };
}
