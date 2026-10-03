using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SentinelCore.Web;

public sealed class MusicTranscodeException(string message) : Exception(message);

// Serves each device a track it can play. The original file when the device decodes its codec
// (and it is under the data-saver bitrate, if one was asked for); otherwise the track converted
// once to AAC in an .m4a with its index at the front (so a player can start before the end
// arrives), kept in <data>/audio-cache/ and trimmed to the newest 5 GB. Two devices asking for
// the same conversion at once share one ffmpeg run.
public sealed class MusicTranscoder(MediaFiles files, string cacheRoot, string ffmpegPath, ILogger<MusicTranscoder> logger)
{
    public const long CacheLimitBytes = 5L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _running = new(StringComparer.Ordinal);
    private readonly object _trimLock = new();

    public string CacheRoot { get; } = cacheRoot;

    public long CacheLimit { get; init; } = CacheLimitBytes;

    // "mp3,aac,flac" as a client sends it, with the spellings people use for the same thing.
    public static IReadOnlySet<string> ParseCodecs(string? text)
    {
        var codecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (string.IsNullOrWhiteSpace(text) ? "mp3,aac" : text).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            codecs.Add(raw.ToLowerInvariant() switch
            {
                "ogg" => "vorbis",
                "aif" => "aiff",
                "wave" => "wav",
                "m4a" or "mp4a" => "aac",
                var other => other
            });
        return codecs;
    }

    // The original when the device plays its codec and it is within the bitrate it asked for;
    // otherwise AAC at that bitrate, or at Settings' quality when it named none.
    public static (bool Transcode, int Kbps) Decide(MusicTrack track, IReadOnlySet<string> codecs, int? maxBitrateKbps, int settingsKbps)
    {
        var withinBitrate = maxBitrateKbps is not { } max || track.Bitrate is not { } bitrate || bitrate <= max * 1000L;
        if (codecs.Contains(track.Codec) && withinBitrate) return (false, 0);
        var kbps = Math.Clamp(maxBitrateKbps ?? settingsKbps, 32, 320);
        // No point making a 256 kbps copy of a 128 kbps MP3 for a device that cannot play MP3.
        if (track.Bitrate is { } source && !MusicTags.IsLossless(track.Codec)) kbps = Math.Min(kbps, Math.Max(64, (int)(source / 1000)));
        return (true, kbps);
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" or ".mp4" or ".alac" => "audio/mp4",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".wav" => "audio/wav",
        ".aif" or ".aiff" => "audio/aiff",
        ".wma" => "audio/x-ms-wma",
        _ => "application/octet-stream"
    };

    public string CachePathFor(MusicTrack track, int kbps)
    {
        // The file's size and date are in the name, so a re-tagged or replaced file is converted
        // afresh instead of serving the old one.
        var stamp = $"{track.Path}|{track.FileSize}|{track.FileModifiedAt?.UtcTicks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)).AsSpan(0, 6)).ToLowerInvariant();
        return Path.Combine(CacheRoot, $"{track.Id:N}-{kbps.ToString(CultureInfo.InvariantCulture)}-{hash}.m4a");
    }

    // The converted copy, made now if it is not in the cache. The caller giving up (a skipped
    // track) does not stop a conversion another device may be waiting on.
    public async Task<string> ConvertAsync(MusicTrack track, int kbps, CancellationToken cancellationToken)
    {
        var destination = CachePathFor(track, kbps);
        if (File.Exists(destination))
        {
            Touch(destination);
            return destination;
        }
        var conversion = _running.GetOrAdd(destination, key => new Lazy<Task<string>>(() => RunAsync(track, kbps, key)));
        try
        {
            return await conversion.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (conversion.Value.IsCompleted) _running.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(destination, conversion));
        }
    }

    private async Task<string> RunAsync(MusicTrack track, int kbps, string destination)
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            var temporary = destination + $".{Guid.NewGuid():N}.part";
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", files.FfmpegInput(track.Path),
                "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", $"{kbps}k"
            };
            if (track.Channels is > 2) args.AddRange(["-ac", "2"]);
            args.AddRange(["-movflags", "+faststart", "-f", "mp4", temporary]);
            var ok = await FfmpegProcess.RunAsync(ffmpegPath, args, ConversionTimeout, logger, $"music conversion of {Path.GetFileName(track.Path)}",
                ProcessPriorityClass.BelowNormal, CancellationToken.None);
            if (!ok || !File.Exists(temporary))
            {
                TryDelete(temporary);
                throw new MusicTranscodeException("This track could not be converted for this device.");
            }
            File.Move(temporary, destination, overwrite: true);
            Touch(destination);
            Trim();
            return destination;
        }
        finally
        {
            _running.TryRemove(destination, out _);
        }
    }

    // Least recently served first, until the cache is under its limit.
    public void Trim()
    {
        lock (_trimLock)
        {
            if (!Directory.Exists(CacheRoot)) return;
            var cached = new DirectoryInfo(CacheRoot).EnumerateFiles("*.m4a").ToList();
            var total = cached.Sum(f => f.Length);
            foreach (var file in cached.OrderBy(f => f.LastAccessTimeUtc).ThenBy(f => f.LastWriteTimeUtc))
            {
                if (total <= CacheLimit) break;
                total -= file.Length;
                TryDelete(file.FullName);
            }
        }
    }

    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
