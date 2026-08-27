using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SentinelCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace SentinelCore.Web;

// Card-sized copies of event snapshots, made once and kept on disk.
//
// The Events tab is the landing tab, it asks for fifty rows, and every row rendered the camera's
// full-resolution frame into a card a few hundred pixels wide. Measured on this install: 2,619
// stored snapshots, 3.27 GB, averaging 1,277 KB each at 3840x2160 or 2560x1920 - so opening the
// tab pulled on the order of 60 MB and made the browser decode fifty multi-megapixel JPEGs to
// paint thumbnails.
//
// The bytes are the smaller half of that. JPEG decode happens on the main thread, and so do the
// MSE appendBuffer calls feeding the live tiles, so fifty full-resolution decodes land on exactly
// the thread the video needs - which is how a busy Events tab becomes a stuttering Live tab.
//
// What is stored is deliberately left alone. 408 KB for a 3840x2160 frame is about 0.05 bytes per
// pixel, an efficient encode rather than a lavish one, and the size that remains tracks scene
// complexity - daylight frames average roughly four times their night counterparts here, which is
// the content, not a setting. On a security snapshot that detail is the point: it is what lets
// someone read a plate or recognise a face afterwards. So the original stays exactly as written
// and only the *card* gets a smaller copy.
public sealed class EventImageThumbnails
{
    // Long edge. Cards sit in a `repeat(auto-fill, minmax(320px, 1fr))` grid, so they run from
    // 320px to roughly 500px wide; 960 covers the widest of those at 2x device pixels and still
    // lands around a tenth of the original's bytes. Chosen over something tighter because a
    // visibly soft snapshot would be a bad trade for a page that is already fast at this size.
    public const int MaxEdge = 960;

    // Quality for the downscale. Higher than a thumbnail strictly needs, because these are
    // photographs of the thing the user is trying to identify rather than decoration, and the
    // saving between 82 and, say, 70 is small once the pixel count has already fallen ~16x.
    private const int JpegQuality = 82;

    private readonly string _cacheDirectory;
    private readonly ILogger<EventImageThumbnails> _logger;

    // One gate per cache key, so fifty cards arriving together do not all decode the same frame.
    // Keyed by the output name rather than the source path: two events sharing a snapshot share
    // the work, and the key already encodes everything that would make the output differ.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _inFlight = new();

    public EventImageThumbnails(IConfiguration configuration, IHostEnvironment environment, ILogger<EventImageThumbnails> logger)
    {
        var configured = configuration["CamerasDataDirectory"] ?? "data";
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _cacheDirectory = Path.Combine(root, "thumbnails");
        _logger = logger;
    }

    /// <summary>
    /// The path to a card-sized copy of <paramref name="sourcePath"/>, generating it if this is
    /// the first request for it. Null when the source cannot be read or decoded, which the caller
    /// should treat as "serve the original" rather than as an error - a card showing the full
    /// frame is worse than this, but it is not broken.
    /// </summary>
    public async Task<string?> GetOrCreateAsync(string sourcePath, CancellationToken cancellationToken)
    {
        FileInfo source;
        try
        {
            source = new FileInfo(sourcePath);
            if (!source.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var cachePath = Path.Combine(_cacheDirectory, CacheName(source));
        if (File.Exists(cachePath)) return cachePath;

        var gate = _inFlight.GetOrAdd(cachePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-checked under the gate: the request that was ahead of this one has finished by now.
            if (File.Exists(cachePath)) return cachePath;
            return await RenderAsync(source, cachePath, cancellationToken) ? cachePath : null;
        }
        finally
        {
            gate.Release();
            _inFlight.TryRemove(cachePath, out _);
        }
    }

    private async Task<bool> RenderAsync(FileInfo source, string cachePath, CancellationToken cancellationToken)
    {
        // A temporary beside the destination, then an atomic move - the same rule the rest of this
        // server follows. A half-written JPEG served to a card is worse than a slow one, and the
        // browser caches what it is given.
        var temporaryPath = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            using var image = await Image.LoadAsync(source.FullName, cancellationToken);

            // Only ever downwards. A camera that already sends something small - a published
            // browser webcam, an older model - would otherwise have its frame blown up to 960 and
            // re-encoded, which costs bytes and quality to gain nothing.
            if (image.Width > MaxEdge || image.Height > MaxEdge)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(MaxEdge, MaxEdge),
                    Mode = ResizeMode.Max
                }));
            }

            // Orientation is applied rather than carried: the card renders the file directly, and
            // an EXIF-rotated frame that arrives sideways in a 4:3 box reads as a broken picture.
            image.Mutate(context => context.AutoOrient());

            await image.SaveAsJpegAsync(temporaryPath, new JpegEncoder { Quality = JpegQuality }, cancellationToken);
            AtomicFile.Replace(temporaryPath, cachePath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A snapshot this cannot decode is not worth failing a page over - the caller falls
            // back to the original, which is what every card did before this existed.
            _logger.LogWarning(ex, "Could not build a card-sized copy of {Path}; serving the original instead", source.FullName);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { /* best effort */ }
            return false;
        }
    }

    // Content-addressed by everything that would change the output: which file, the version of it
    // (length and last-write time), and the size this class renders at. So a snapshot overwritten
    // in place produces a new name rather than serving the previous picture, and raising MaxEdge
    // invalidates every cached copy without anyone having to remember to clear the directory.
    //
    // Hashed rather than derived from the path so the name is a fixed length and has no directory
    // separators in it, and SHA-256 rather than a cheaper hash because a collision here would
    // serve one camera's snapshot in place of another's.
    private static string CacheName(FileInfo source)
    {
        var key = $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{MaxEdge}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash)[..32] + ".jpg";
    }
}
