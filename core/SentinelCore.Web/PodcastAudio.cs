using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// Where podcast files live. The download folder is a setting (blank = <data>/podcasts) and must
// be on this server's own disk; art, clips and scratch space sit under the data directory.
public sealed class PodcastLocations(string dataRoot, AppSettingsFile settings)
{
    public string DefaultDownloadFolder { get; } = Path.Combine(dataRoot, "podcasts");
    public string ArtFolder { get; } = Path.Combine(dataRoot, "podcast-art");
    public string ClipsFolder { get; } = Path.Combine(dataRoot, "podcast-clips");
    public string WorkFolder { get; } = Path.Combine(dataRoot, "podcast-work");

    public async Task<string> DownloadFolderAsync(CancellationToken cancellationToken)
    {
        var options = await ReadOptionsAsync(settings, cancellationToken);
        return DownloadFolderFor(options);
    }

    public string DownloadFolderFor(PodcastSettingsDto options) =>
        string.IsNullOrWhiteSpace(options.DownloadFolder) ? DefaultDownloadFolder : options.DownloadFolder.Trim();

    public static async Task<PodcastSettingsDto> ReadOptionsAsync(AppSettingsFile settings, CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<PodcastSettingsDto>(PodcastSettingsDto.SectionName, cancellationToken) ?? new PodcastSettingsDto();

    // Settings → Podcasts → Download folder. A folder on another computer is refused: downloads
    // are written, resumed, cut and served constantly, which a share does badly and SMB cannot do
    // here without mounting.
    public static string? ValidateDownloadFolder(string? folder)
    {
        var path = folder?.Trim() ?? "";
        if (path.Length == 0) return null;
        if (MediaNetworkPath.IsNetwork(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return "Podcasts download to a folder on this server's own disks, not to another computer. Choose a local folder, or leave it blank for the data folder.";
        if (!Path.IsPathRooted(path)) return "Use the folder's full path, like /mnt/storage/Podcasts.";
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"The server cannot write to {path}: {ex.Message}";
        }
    }
}

public sealed record PodcastProbe(double? DurationSeconds, IReadOnlyList<PodcastChapter> Chapters);

// Everything ffmpeg does for podcasts: reading a file's length and chapters, finding silences,
// making an episode louder and clearer and/or shorter, cutting clips, and the 16 kHz WAV whisper
// wants.
public sealed partial class PodcastAudioProcessor
{
    // Silences longer than this are cut down to KeepSeconds (half kept either side).
    public const double MinimumSilenceSeconds = 1.0;
    public const double KeepSeconds = 0.3;
    private const string SilenceNoise = "-40dB";

    // Highpass takes out rumble; the compressor and dynaudnorm even out a quiet guest and a loud
    // host. None of them adds or drops samples, so every timestamp stays where it was.
    public const string VoiceBoostFilter =
        "highpass=f=80,acompressor=threshold=-21dB:ratio=3:attack=10:release=250:makeup=2,dynaudnorm=f=200:g=15:p=0.9:m=10";

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly ILogger<PodcastAudioProcessor> _logger;
    private bool? _available;

    public PodcastAudioProcessor(IConfiguration configuration, ILogger<PodcastAudioProcessor> logger)
    {
        _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
        _ffprobePath = configuration["FfprobePath"] ?? MediaProbe.DeriveFfprobePath(_ffmpegPath);
        _logger = logger;
    }

    public async Task<bool> FfmpegAvailableAsync(CancellationToken cancellationToken)
    {
        if (_available is { } known) return known;
        var result = await PodcastProcess.RunAsync(_ffmpegPath, ["-version"], TimeSpan.FromSeconds(10), cancellationToken);
        _available = result.Started && result.ExitCode == 0;
        return _available.Value;
    }

    public async Task<PodcastProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await PodcastProcess.RunAsync(_ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_chapters", path], TimeSpan.FromMinutes(2), cancellationToken);
        return result.ExitCode == 0 ? ParseProbe(result.Stdout) : new PodcastProbe(null, []);
    }

    public static PodcastProbe ParseProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            double? duration = null;
            if (document.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                duration = seconds;
            var chapters = new List<PodcastChapter>();
            if (document.RootElement.TryGetProperty("chapters", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var chapter in list.EnumerateArray())
                {
                    var start = Seconds(chapter, "start_time");
                    if (start is null) continue;
                    var title = chapter.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var t) ? t.GetString() : null;
                    chapters.Add(new PodcastChapter(start.Value, Seconds(chapter, "end_time"), string.IsNullOrWhiteSpace(title) ? $"Chapter {chapters.Count + 1}" : title.Trim(), null, null));
                }
            }
            return new PodcastProbe(duration, chapters);
        }
        catch (JsonException) { return new PodcastProbe(null, []); }
    }

    private static double? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    public async Task<IReadOnlyList<(double Start, double End)>> DetectSilencesAsync(string path, double? duration, CancellationToken cancellationToken)
    {
        var result = await PodcastProcess.RunAsync(_ffmpegPath,
            ["-hide_banner", "-nostats", "-i", path, "-vn", "-af", $"silencedetect=noise={SilenceNoise}:d={MinimumSilenceSeconds.ToString(CultureInfo.InvariantCulture)}", "-f", "null", "-"],
            TimeSpan.FromHours(1), cancellationToken, ProcessPriorityClass.BelowNormal);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("Silence detection failed for {Path}: {Error}", path, PodcastProcess.Tail(result.Stderr));
            return [];
        }
        return ParseSilences(result.Stderr, duration);
    }

    // "[silencedetect @ 0x...] silence_start: 2.0001" then "... silence_end: 5 | silence_duration: 3".
    // A silence still running when the file ends has a start and no end.
    public static IReadOnlyList<(double Start, double End)> ParseSilences(string stderr, double? duration)
    {
        var silences = new List<(double, double)>();
        double? start = null;
        foreach (Match match in SilenceLine().Matches(stderr))
        {
            var value = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (match.Groups[1].Value == "start") start = Math.Max(0, value);
            else if (start is { } s)
            {
                silences.Add((s, value));
                start = null;
            }
        }
        if (start is { } open && duration is { } end && end > open) silences.Add((open, end));
        return silences;
    }

    [GeneratedRegex(@"silence_(start|end):\s*(-?[0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex SilenceLine();

    // Each silence over MinimumSilenceSeconds keeps KeepSeconds: half at either edge, so speech is
    // never clipped and the join still sounds like a breath.
    public static List<PodcastCut> BuildCuts(IEnumerable<(double Start, double End)> silences,
        double minimum = MinimumSilenceSeconds, double keep = KeepSeconds)
    {
        var cuts = new List<PodcastCut>();
        foreach (var (start, end) in silences.OrderBy(s => s.Start))
        {
            if (end - start <= minimum) continue;
            var cut = new PodcastCut(Math.Round(start + keep / 2, 3), Math.Round(end - keep / 2, 3));
            if (cut.Length <= 0) continue;
            if (cuts.Count > 0 && cut.Start <= cuts[^1].End) cuts[^1] = cuts[^1] with { End = Math.Max(cuts[^1].End, cut.End) };
            else cuts.Add(cut);
        }
        return cuts;
    }

    // The audio filter for a download: drop the cut stretches (in small frames, so the cuts land
    // within a few milliseconds of the map), restamp what is left, then the voice boost.
    public static string? FilterFor(IReadOnlyList<PodcastCut> cuts, bool voiceBoost)
    {
        var parts = new List<string>();
        if (cuts.Count > 0)
        {
            var terms = string.Join("+", cuts.Select(c => $"between(t,{Format(c.Start)},{Format(c.End)})"));
            parts.Add("asetnsamples=n=256:p=0");
            parts.Add($"aselect='not({terms})'");
            parts.Add("asetpts=N/SR/TB");
        }
        if (voiceBoost) parts.Add(VoiceBoostFilter);
        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // Re-encodes to AAC in an .m4a. The file's own chapters are dropped (they are kept from the
    // original in podcasts.db, where the cut map can convert them).
    public async Task<bool> ProcessAsync(string input, string output, IReadOnlyList<PodcastCut> cuts, bool voiceBoost, CancellationToken cancellationToken)
    {
        var filter = FilterFor(cuts, voiceBoost);
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", input, "-map", "0:a:0", "-vn" };
        if (filter is not null) args.AddRange(["-af", filter]);
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-map_metadata", "0", "-map_chapters", "-1", "-movflags", "+faststart", "-f", "mp4", output]);
        var result = await PodcastProcess.RunAsync(_ffmpegPath, args, TimeSpan.FromHours(2), cancellationToken, ProcessPriorityClass.BelowNormal);
        if (result.ExitCode == 0 && File.Exists(output)) return true;
        _logger.LogWarning("Podcast post-processing failed for {Path}: {Error}", input, PodcastProcess.Tail(result.Stderr));
        return false;
    }

    public async Task<bool> ToWavAsync(string input, string output, CancellationToken cancellationToken)
    {
        var result = await PodcastProcess.RunAsync(_ffmpegPath,
            ["-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", input, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", output],
            TimeSpan.FromHours(1), cancellationToken, ProcessPriorityClass.BelowNormal);
        return result.ExitCode == 0 && File.Exists(output);
    }

    // `input` is a local file or the publisher's URL (ffmpeg seeks over HTTP with ranges).
    public async Task<string?> CutClipAsync(string input, double start, double duration, string output, string? title, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error", "-y",
            "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
            "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"
        };
        if (!string.IsNullOrWhiteSpace(title)) args.AddRange(["-metadata", $"title={title.Trim()}"]);
        args.AddRange(["-f", "mp4", output]);
        var result = await PodcastProcess.RunAsync(_ffmpegPath, args, TimeSpan.FromMinutes(5), cancellationToken);
        if (result.ExitCode == 0 && File.Exists(output)) return null;
        var tail = PodcastProcess.Tail(result.Stderr, 2);
        _logger.LogWarning("Podcast clip failed: {Error}", tail);
        return string.IsNullOrWhiteSpace(tail) ? "ffmpeg could not cut the clip." : $"ffmpeg could not cut the clip: {tail}";
    }

    // Some publishers' files report an empty format duration; this is good enough to say how
    // long something is in a status line.
    public static string Describe(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var builder = new StringBuilder();
        if (time.TotalHours >= 1) builder.Append((int)time.TotalHours).Append(':').Append(time.Minutes.ToString("00", CultureInfo.InvariantCulture));
        else builder.Append(time.Minutes);
        builder.Append(':').Append(time.Seconds.ToString("00", CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
