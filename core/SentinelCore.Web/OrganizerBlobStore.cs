using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace SentinelCore.Web;

// The pictures and files attached to Organizer notes, events, reminders and cards.
//
// Content-addressed: a file is stored under the SHA-256 of its bytes, and an attachment item only
// carries that hash. That is what makes offline attaching and disaster recovery simple. A phone
// can attach a photo with no signal, write the item straight away and upload the bytes whenever
// it next can, and a device re-seeding a rebuilt server just uploads whichever hashes the server
// says it's missing. Uploading the same photo twice is free, and nothing can be overwritten with
// different content, because the name IS the content.
//
// Who may read a blob is decided by the items, not here: a download is allowed when an attachment
// the person can see points at that hash (see OrganizerEndpoints).
public sealed class OrganizerBlobStore(string root)
{
    // Long edge of the thumbnails phones keep in light mode and the dashboard uses in lists.
    public const int ThumbnailEdge = 512;

    // Unreferenced blobs are kept this long before the sweep removes them - long enough for a
    // phone that attached a photo offline to upload the bytes before the item, or vice versa, and
    // for an accidental delete to be undone from another device's copy.
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromDays(30);

    private string BlobDirectory => Path.Combine(root, "organizer-blobs");
    private string ThumbnailDirectory => Path.Combine(root, "organizer-thumbnails");

    public static bool IsValidHash(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public string PathFor(string hash)
    {
        var lower = hash.ToLowerInvariant();
        return Path.Combine(BlobDirectory, lower[..2], lower);
    }

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    // Writes the upload to a temporary file while hashing it, and only moves it into place if it
    // hashes to the name it was sent under. False means it didn't - a truncated upload or a
    // client bug - and nothing is kept.
    public async Task<bool> SaveAsync(string hash, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var target = PathFor(hash);
        if (File.Exists(target))
        {
            await content.CopyToAsync(Stream.Null, cancellationToken);
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.part";
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new InvalidDataException("too large");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), hash, StringComparison.OrdinalIgnoreCase))
                return false;

            File.Move(temporary, target, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // A JPEG no wider or taller than ThumbnailEdge, made on first request and kept. Null when the
    // blob isn't there or isn't an image ImageSharp can read (a PDF, a video) - the client shows a
    // file icon for those.
    public async Task<string?> GetThumbnailAsync(string hash, CancellationToken cancellationToken)
    {
        var source = PathFor(hash);
        if (!File.Exists(source)) return null;

        var thumbnail = Path.Combine(ThumbnailDirectory, $"{hash.ToLowerInvariant()}-{ThumbnailEdge}.jpg");
        if (File.Exists(thumbnail)) return thumbnail;

        Directory.CreateDirectory(ThumbnailDirectory);
        var temporary = thumbnail + $".{Guid.NewGuid():N}.part";
        try
        {
            using var image = await Image.LoadAsync(SafeImageDecoding.Options, source, cancellationToken);
            image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(ThumbnailEdge, ThumbnailEdge),
            }));
            await image.SaveAsJpegAsync(temporary, new JpegEncoder { Quality = 80 }, cancellationToken);
            File.Move(temporary, thumbnail, overwrite: true);
            return thumbnail;
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Deletes blobs no live attachment points at and that are older than OrphanGrace, along with
    // their thumbnails. Returns how many went.
    public int Sweep(IReadOnlySet<string> liveHashes, DateTimeOffset now)
    {
        if (!Directory.Exists(BlobDirectory)) return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(BlobDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var isPart = name.EndsWith(".part", StringComparison.Ordinal);
            if (!isPart && liveHashes.Contains(name)) continue;
            if (now - File.GetLastWriteTimeUtc(file) < (isPart ? TimeSpan.FromDays(1) : OrphanGrace)) continue;

            File.Delete(file);
            if (!isPart)
            {
                var thumbnail = Path.Combine(ThumbnailDirectory, $"{name}-{ThumbnailEdge}.jpg");
                if (File.Exists(thumbnail)) File.Delete(thumbnail);
            }
            removed++;
        }
        return removed;
    }
}

// Runs the orphan sweep once a day.
public sealed class OrganizerBlobSweepWorker(
    IOrganizerStore store, OrganizerBlobStore blobs, ILogger<OrganizerBlobSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                var live = await store.GetAllLiveBlobHashesAsync(stoppingToken);
                var removed = blobs.Sweep(live, DateTimeOffset.UtcNow);
                if (removed > 0) logger.LogInformation("Removed {Count} unused Organizer attachment file(s).", removed);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Organizer attachment sweep failed; will try again tomorrow.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
