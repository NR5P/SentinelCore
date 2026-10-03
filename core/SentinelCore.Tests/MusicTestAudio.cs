using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Real audio files for the Music tests, made by ffmpeg on the spot: a few seconds of a sine tone
// with whatever tags (and embedded cover) a test asks for. Tests that use these skip themselves
// on a machine without ffmpeg, like the other real-ffmpeg tests.
internal static class MusicTestAudio
{
    public static async Task<bool> FfmpegAvailableAsync() =>
        await FfmpegProcess.RunAsync("ffmpeg", ["-version"], TimeSpan.FromSeconds(10), NullLogger.Instance, "test", null, CancellationToken.None);

    // The codec ffmpeg should use for the file's extension.
    private static string[] Codec(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => ["-c:a", "libmp3lame", "-b:a", "128k"],
        ".flac" => ["-c:a", "flac"],
        ".m4a" => ["-c:a", "aac", "-b:a", "128k"],
        ".ogg" => ["-c:a", "vorbis", "-strict", "experimental", "-ac", "2"],
        ".opus" => ["-c:a", "libopus", "-b:a", "96k"],
        ".wav" => ["-c:a", "pcm_s16le"],
        _ => throw new ArgumentException(path)
    };

    public static async Task MakeAsync(string path, IReadOnlyDictionary<string, string>? tags = null, int seconds = 8,
        string? coverImage = null, double frequency = 440)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration={seconds}" };
        if (coverImage is not null) args.AddRange(["-i", coverImage, "-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v", "attached_pic"]);
        args.AddRange(Codec(path));
        if (Path.GetExtension(path) == ".mp3") args.AddRange(["-id3v2_version", "3"]);
        foreach (var (key, value) in tags ?? new Dictionary<string, string>())
            args.AddRange(["-metadata", $"{key}={value}"]);
        args.Add(path);
        var ok = await FfmpegProcess.RunAsync("ffmpeg", args, TimeSpan.FromMinutes(1), NullLogger.Instance, "test audio", null, CancellationToken.None);
        Assert.True(ok, $"ffmpeg could not make {path}");
    }

    public static async Task MakeImageAsync(string path, string color = "red", int size = 320)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ok = await FfmpegProcess.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c={color}:s={size}x{size}", "-frames:v", "1", path],
            TimeSpan.FromMinutes(1), NullLogger.Instance, "test image", null, CancellationToken.None);
        Assert.True(ok, $"ffmpeg could not make {path}");
    }

    public static Dictionary<string, string> Tags(string title, string artist, string album, int track, string? albumArtist = null,
        string? genre = null, int? year = null) =>
        new()
        {
            ["title"] = title,
            ["artist"] = artist,
            ["album"] = album,
            ["track"] = track.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["album_artist"] = albumArtist ?? "",
            ["genre"] = genre ?? "",
            ["date"] = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
}
