using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// What a video file actually contains, from ffprobe: the duration the progress bar needs, and the
// container and codecs MediaPlaybackPlanner decides direct play against. Run once per file (and
// again when the file's size or date changes), in the background, after the scan has already put
// the title on the shelf.
public static partial class MediaProbe
{
    // Text subtitles can be turned into WebVTT for every player; bitmap ones (PGS, DVD) cannot
    // without burning them into the picture, so they are not offered.
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text"
    };

    private static readonly string[] SidecarExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    [GeneratedRegex(@"^(\d+):(\d+):(\d+(?:\.\d+)?)$")]
    private static partial Regex ClockDuration();

    // The same rule CameraDiscoveryService uses: ffprobe ships beside ffmpeg under the same name
    // pattern, so a configured "/opt/ffmpeg/bin/ffmpeg" finds "/opt/ffmpeg/bin/ffprobe".
    public static string DeriveFfprobePath(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
        var file = Path.GetFileName(ffmpegPath);
        if (!file.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)) return "ffprobe";
        var probe = file.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        return directory.Length == 0 ? probe : Path.Combine(directory, probe);
    }

    public static async Task<bool> ProbeAsync(MediaItem item, string ffprobePath, ILogger logger, CancellationToken cancellationToken)
    {
        var args = new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", item.Path };
        var output = await FfmpegProcess.RunCapturingStdoutAsync(
            ffprobePath, args, TimeSpan.FromSeconds(60), logger, $"probe of {Path.GetFileName(item.Path)}",
            ProcessPriorityClass.BelowNormal, cancellationToken);

        item.ProbedAt = DateTimeOffset.UtcNow;
        if (output is null)
        {
            item.ProbeError = "ffprobe could not read this file.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            Apply(item, document.RootElement);
            item.ProbeError = null;
            return true;
        }
        catch (JsonException ex)
        {
            item.ProbeError = $"ffprobe returned something unexpected: {ex.Message}";
            return false;
        }
    }

    // Pure: ffprobe's JSON in, the item's file facts out. Separated so MediaProbeTests can feed
    // it captured output without an ffprobe on the test machine.
    public static void Apply(MediaItem item, JsonElement root)
    {
        item.Container = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant();
        if (root.TryGetProperty("format", out var format))
        {
            var formatName = format.TryGetProperty("format_name", out var name) ? name.GetString() ?? "" : "";
            if (formatName.Contains("matroska", StringComparison.OrdinalIgnoreCase) && item.Container != "webm") item.Container = "mkv";
            item.DurationSeconds = Seconds(format, "duration");
            item.Bitrate = Long(format, "bit_rate");
        }

        var audio = new List<MediaAudioTrack>();
        var subtitles = new List<MediaSubtitleTrack>();
        var sawVideo = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = String(stream, "codec_type");
                var codec = String(stream, "codec_name") ?? "";
                switch (type)
                {
                    case "video" when !sawVideo && !IsAttachedPicture(stream):
                        sawVideo = true;
                        item.VideoCodec = codec;
                        item.VideoProfile = String(stream, "profile");
                        item.VideoTag = String(stream, "codec_tag_string")?.Trim('[', ']', '0');
                        item.PixelFormat = String(stream, "pix_fmt");
                        item.Width = Int(stream, "width");
                        item.Height = Int(stream, "height");
                        item.DurationSeconds ??= Seconds(stream, "duration") ?? TagDuration(stream);
                        break;
                    case "audio":
                        audio.Add(new MediaAudioTrack(
                            audio.Count, codec, Int(stream, "channels") ?? 2, Tag(stream, "language"), Tag(stream, "title"),
                            Disposition(stream, "default")));
                        break;
                    case "subtitle" when TextSubtitleCodecs.Contains(codec):
                        subtitles.Add(new MediaSubtitleTrack(
                            subtitles.Count, codec, Tag(stream, "language"), Tag(stream, "title"),
                            Disposition(stream, "forced"), Disposition(stream, "default"), null, SubtitleStreamOrdinal(streams, stream)));
                        break;
                }
            }
        }

        foreach (var sidecar in FindSidecarSubtitles(item.Path))
            subtitles.Add(sidecar with { Index = subtitles.Count });

        item.AudioTracks = audio;
        item.SubtitleTracks = subtitles;
        var mainAudio = audio.FirstOrDefault(track => track.IsDefault) ?? audio.FirstOrDefault();
        item.AudioCodec = mainAudio?.Codec;
        item.AudioChannels = mainAudio?.Channels;
    }

    // "Movie.srt", "Movie.en.srt", "Movie.eng.forced.srt" beside "Movie.mkv".
    public static IReadOnlyList<MediaSubtitleTrack> FindSidecarSubtitles(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (directory is null || !Directory.Exists(directory)) return [];
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var found = new List<MediaSubtitleTrack>();
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            if (!SidecarExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
            var middle = name[(stem.Length + 1)..^extension.Length];
            var parts = middle.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var forced = parts.Any(part => part.Equals("forced", StringComparison.OrdinalIgnoreCase));
            var language = parts.FirstOrDefault(part => part.Length is 2 or 3 && !part.Equals("sdh", StringComparison.OrdinalIgnoreCase) && !part.Equals("forced", StringComparison.OrdinalIgnoreCase));
            found.Add(new MediaSubtitleTrack(0, extension.TrimStart('.').ToLowerInvariant(), language, null, forced, false, file, null));
        }
        return found;
    }

    private static int SubtitleStreamOrdinal(JsonElement streams, JsonElement target)
    {
        var ordinal = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            if (String(stream, "codec_type") != "subtitle") continue;
            if (stream.GetRawText() == target.GetRawText()) return ordinal;
            ordinal++;
        }
        return ordinal;
    }

    private static bool IsAttachedPicture(JsonElement stream) => Disposition(stream, "attached_pic");

    private static bool Disposition(JsonElement stream, string flag) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.TryGetProperty(flag, out var value)
        && value.ValueKind == JsonValueKind.Number && value.GetInt32() != 0;

    private static string? Tag(JsonElement stream, string key) =>
        stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty(key, out var value) ? value.GetString() : null;

    // Matroska files often carry the duration only as a tag on the video stream, written by
    // mkvmerge as "00:44:06.040000000".
    private static double? TagDuration(JsonElement stream)
    {
        if (!stream.TryGetProperty("tags", out var tags)) return null;
        foreach (var tag in tags.EnumerateObject())
        {
            if (!tag.Name.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase)) continue;
            var match = ClockDuration().Match(tag.Value.GetString() ?? "");
            if (match.Success)
                return int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60
                    + double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static string? String(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    // ffprobe writes numbers as strings ("duration": "2646.048000").
    private static double? Seconds(JsonElement element, string key)
    {
        var text = String(element, key);
        return text is not null && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds : null;
    }

    private static long? Long(JsonElement element, string key)
    {
        var text = String(element, key);
        return text is not null && long.TryParse(text, out var value) && value > 0 ? value : null;
    }
}
