using System.Net;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace SentinelCore.Web;

// Where album and artist art the server made itself lives: <data>/music-art/album-<id>.jpg for a
// picture taken out of a file or fetched from the Cover Art Archive, and sized/ for the smaller
// copies the art endpoints hand out. Folder art (cover.jpg beside the tracks) is never copied
// here; it is served from where it is.
public sealed class MusicArtCache(string root)
{
    // The sizes a ?size= is rounded up to, so the cache holds a handful of copies per album
    // rather than one per pixel width some client happened to ask for.
    private static readonly int[] Buckets = [64, 128, 200, 300, 400, 600, 800, 1200];

    public string Root { get; } = root;

    public string SizedRoot => Path.Combine(Root, "sized");

    public string AlbumPath(Guid albumId, string extension) => Path.Combine(Root, $"album-{albumId:N}{extension}");

    public static string AlbumKey(Guid albumId) => $"album:{albumId:N}";

    public static string ArtistKey(Guid artistId) => $"artist:{artistId:N}";

    public static int Bucket(int size)
    {
        var clamped = Math.Clamp(size, 64, 1200);
        return Buckets.First(b => b >= clamped);
    }

    // A copy of the image no wider or taller than `size`, made once and kept. The original when
    // it is already that small, or when it cannot be decoded (the client still gets a picture).
    public async Task<string> SizedAsync(string original, IMediaFileSystem fs, int size, CancellationToken cancellationToken)
    {
        var bucket = Bucket(size);
        var entry = fs.Stat(original);
        if (entry is null) return original;
        var stamp = $"{original}|{entry.Length}|{entry.ModifiedUtc.Ticks}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stamp)).AsSpan(0, 10)).ToLowerInvariant();
        var sized = Path.Combine(SizedRoot, $"{hash}-{bucket}.jpg");
        if (File.Exists(sized)) return sized;
        try
        {
            Directory.CreateDirectory(SizedRoot);
            await using var stream = fs.OpenRead(original);
            using var image = await Image.LoadAsync(SafeImageDecoding.Options, stream, cancellationToken);
            if (image.Width <= bucket && image.Height <= bucket) return original;
            image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(bucket, bucket), Mode = ResizeMode.Max }));
            // A temporary name of its own: an album page asks for the same new size several times
            // at once (every track row shows the cover), and two requests writing one shared .tmp
            // collided ("being used by another process" -> 500).
            var temporary = $"{sized}.{Guid.NewGuid():N}.tmp";
            try
            {
                await image.SaveAsJpegAsync(temporary, new JpegEncoder { Quality = 85 }, cancellationToken);
                if (!File.Exists(sized)) File.Move(temporary, sized, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
            return File.Exists(sized) ? sized : original;
        }
        catch (Exception ex) when (ex is ImageFormatException or UnknownImageFormatException or InvalidImageContentException or IOException or NotSupportedException)
        {
            return original;
        }
    }
}

// Album covers from the Cover Art Archive, found through MusicBrainz. MusicBrainz asks every
// client for a descriptive User-Agent and at most one request a second; both are kept here for
// every caller in the process. A release id in the file's tags skips the search entirely.
public sealed class MusicCoverArtClient(IHttpClientFactory http, ILogger<MusicCoverArtClient> logger)
{
    public const string HttpClientName = "MusicBrainz";
    private static readonly TimeSpan MusicBrainzSpacing = TimeSpan.FromSeconds(1.1);
    private readonly SemaphoreSlim _musicBrainzGate = new(1, 1);
    private DateTimeOffset _lastMusicBrainzRequest = DateTimeOffset.MinValue;

    // The release group's id on MusicBrainz for an album, or null when nothing matches well.
    public async Task<string?> FindReleaseGroupAsync(string artist, string album, CancellationToken cancellationToken)
    {
        var query = $"releasegroup:\"{Escape(album)}\"" + (MusicAlbumRules.IsVariousArtists(artist) ? "" : $" AND artist:\"{Escape(artist)}\"");
        using var document = await MusicBrainzAsync($"https://musicbrainz.org/ws/2/release-group/?query={Uri.EscapeDataString(query)}&fmt=json&limit=5", cancellationToken);
        if (document is null || !document.RootElement.TryGetProperty("release-groups", out var groups)) return null;
        foreach (var group in groups.EnumerateArray())
        {
            var score = group.TryGetProperty("score", out var s) && s.TryGetInt32(out var value) ? value : 0;
            var title = group.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (score >= 90 && MusicText.Key(title) == MusicText.Key(album) && group.TryGetProperty("id", out var id))
                return id.GetString();
        }
        return null;
    }

    private async Task<JsonDocument?> MusicBrainzAsync(string url, CancellationToken cancellationToken)
    {
        await _musicBrainzGate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastMusicBrainzRequest + MusicBrainzSpacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastMusicBrainzRequest = DateTimeOffset.UtcNow;
            using var response = await http.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        finally { _musicBrainzGate.Release(); }
    }

    // Downloads the front cover (500 px) of a release or release group into `destination`.
    // False when the archive has none.
    public async Task<bool> DownloadFrontAsync(string kind, string mbid, string destination, CancellationToken cancellationToken)
    {
        var url = $"https://coverartarchive.org/{kind}/{Uri.EscapeDataString(mbid)}/front-500";
        using var response = await http.CreateClient(HttpClientName).GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        await using (var file = File.Create(temporary))
            await response.Content.CopyToAsync(file, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
        logger.LogDebug("Cover Art Archive: saved {Url}", url);
        return true;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
