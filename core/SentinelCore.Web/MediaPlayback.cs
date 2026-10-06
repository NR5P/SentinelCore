using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SentinelCore.Web;

// How a file will reach a given player: handed over as-is (direct play) when the container and
// codecs are ones the device decodes, otherwise repackaged - and only re-encoded when it has to
// be - into HLS by ffmpeg. Pure, so MediaPlaybackTests can pin every rule.
// RebuildTimestamps: the file's frame timestamps are in decode order (MediaItem.TimestampsInDecodeOrder).
// A copied video has them re-derived from the bitstream; a re-encode needs nothing, since ffmpeg
// stamps decoded frames afresh in the order the decoder shows them (checked on the Rambo file).
public sealed record MediaPlaybackPlan(bool Direct, string VideoMode, string AudioMode, int? AudioTrackIndex, bool TagHvc1, string Reason, bool RebuildTimestamps = false);

public static class MediaPlaybackPlanner
{
    private static readonly HashSet<string> DirectContainers = new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "mov" };

    // `timestampRebuildCodecs`: the codecs this box's ffmpeg can re-derive timestamps for without
    // re-encoding (its dts2pts filter; h264 in 8.0, hevc too from 8.1).
    public static MediaPlaybackPlan Plan(MediaItem item, IReadOnlySet<string> clientVideo, IReadOnlySet<string> clientAudio, int? requestedAudioTrack,
        IReadOnlySet<string>? timestampRebuildCodecs = null)
    {
        var videoCodec = NormalizeVideo(item.VideoCodec);
        var rebuild = item.TimestampsInDecodeOrder;
        // A file stamped in decode order can still have its video copied when ffmpeg can fix
        // the stamps in passing; otherwise it is re-encoded, which stamps every frame afresh.
        var videoOk = videoCodec is not null && clientVideo.Contains(videoCodec)
            && (!rebuild || timestampRebuildCodecs?.Contains(videoCodec) == true);

        var audioTrack = requestedAudioTrack is { } wanted
            ? item.AudioTracks.FirstOrDefault(track => track.Index == wanted)
            : null;
        var defaultTrack = item.AudioTracks.FirstOrDefault(track => track.IsDefault) ?? item.AudioTracks.FirstOrDefault();
        audioTrack ??= defaultTrack;
        var audioCodec = NormalizeAudio(audioTrack?.Codec ?? item.AudioCodec);
        var audioOk = audioTrack is null || (audioCodec is not null && clientAudio.Contains(audioCodec));
        var usesDefaultAudio = audioTrack is null || defaultTrack is null || audioTrack.Index == defaultTrack.Index;

        // An HEVC track has to be tagged hvc1 for Apple to touch it; ffmpeg's muxer writes hev1
        // unless told otherwise, and so do plenty of the files out there.
        var hevcNeedsRetag = videoCodec == "hevc" && !string.Equals(item.VideoTag, "hvc1", StringComparison.OrdinalIgnoreCase);

        if (item.Container is not null && DirectContainers.Contains(item.Container) && videoOk && audioOk && usesDefaultAudio && !hevcNeedsRetag && !rebuild)
            return new MediaPlaybackPlan(true, "copy", "copy", audioTrack?.Index, false, "the device plays this file as it is");

        // A device that plays MP3 in a file still cannot take it inside fMP4 HLS segments: Apple's
        // HLS carries AAC, AC-3 and E-AC-3 there, and AVPlayer answers "Cannot Open" to anything
        // else (an XviD/MP3 .avi, the common old rip, hit this on 2026-09-29).
        var audioCopiesIntoHls = audioOk && (audioTrack is null || audioCodec is "aac" or "ac3" or "eac3");
        var reason = rebuild ? (videoOk ? "the frames are stamped out of order and are put right" : "the frames are stamped out of order; re-encoding puts them right")
            : !videoOk ? $"{item.VideoCodec ?? "the video"} needs converting to H.264"
            : !audioOk ? $"{audioTrack?.Codec ?? "the audio"} needs converting to AAC"
            : hevcNeedsRetag ? "HEVC must be retagged for this device"
            : !usesDefaultAudio ? "a different audio track was chosen"
            : $"{item.Container ?? "this container"} is repackaged as HLS";
        return new MediaPlaybackPlan(
            false,
            videoOk ? "copy" : "h264",
            audioTrack is null ? "none" : audioCopiesIntoHls ? "copy" : "aac",
            audioTrack?.Index,
            videoOk && videoCodec == "hevc",
            reason,
            rebuild);
    }

    public static string? NormalizeVideo(string? codec) => codec?.ToLowerInvariant() switch
    {
        "h264" or "avc" or "avc1" => "h264",
        "hevc" or "h265" or "hvc1" or "hev1" => "hevc",
        null => null,
        var other => other
    };

    public static string? NormalizeAudio(string? codec) => codec?.ToLowerInvariant() switch
    {
        "aac" or "aac_latm" => "aac",
        "mp3" or "mp2" => "mp3",
        "ac3" or "ac-3" => "ac3",
        "eac3" or "ec-3" => "eac3",
        null => null,
        var other => other
    };

    // "h264,hevc" from the query string into the set Plan wants; blank means the safe default.
    public static HashSet<string> ParseCodecs(string? list, params string[] defaults)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var codec in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(codec.ToLowerInvariant());
        if (set.Count == 0) foreach (var codec in defaults) set.Add(codec);
        return set;
    }

    // `ffmpeg -h bsf=dts2pts` -> {"h264", "hevc"}, from its "    Supported codecs: h264 hevc" line.
    public static HashSet<string> ParseBsfCodecs(string help)
    {
        const string marker = "Supported codecs:";
        var line = help.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(marker, StringComparison.OrdinalIgnoreCase));
        return line is null ? [] : ParseCodecs(line[marker.Length..].Replace(' ', ','));
    }

    public static string MimeType(string? container) => container?.ToLowerInvariant() switch
    {
        "mp4" or "m4v" => "video/mp4",
        "mov" => "video/quicktime",
        "mkv" => "video/x-matroska",
        "webm" => "video/webm",
        "avi" => "video/x-msvideo",
        "mpg" or "mpeg" => "video/mpeg",
        "ts" or "m2ts" => "video/mp2t",
        _ => "application/octet-stream"
    };

    // The ffmpeg command for one HLS session. Video and audio are copied whenever the plan
    // allows, so a remux of a two-hour film is disk-bound and finishes in a minute or two; a
    // transcode runs at roughly real time on this hardware and is capped at 1080p, which is what
    // the devices watching it can show anyway.
    // `input` is what ffmpeg opens: the file itself, or the relay URL for one on a network share.
    public static List<string> BuildFfmpegArguments(MediaItem item, MediaPlaybackPlan plan, double startSeconds, string outputDirectory, string? input = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        if (startSeconds > 0)
            args.AddRange(["-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", input ?? item.Path]);
        args.AddRange(["-map", "0:v:0"]);
        if (plan.AudioMode != "none")
            args.AddRange(["-map", $"0:a:{plan.AudioTrackIndex ?? 0}"]);
        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);

        if (plan.VideoMode == "copy")
        {
            args.AddRange(["-c:v", "copy"]);
            if (plan.TagHvc1) args.AddRange(["-tag:v", "hvc1"]);
            // Re-derives each frame's timestamp from its picture order in the bitstream.
            if (plan.RebuildTimestamps) args.AddRange(["-bsf:v", "dts2pts"]);
        }
        else
        {
            args.AddRange([
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-profile:v", "high", "-level", "4.1",
                "-pix_fmt", "yuv420p", "-vf", "scale=-2:'min(1080,ih)'",
                "-maxrate", "8M", "-bufsize", "16M",
                // Keyframes exactly on the segment boundaries, and nowhere else the encoder feels
                // like, so every segment starts decodable and lasts what the playlist says.
                "-force_key_frames", "expr:gte(t,n_forced*6)", "-x264-params", "scenecut=0"
            ]);
        }

        if (plan.AudioMode == "copy") args.AddRange(["-c:a", "copy"]);
        else if (plan.AudioMode == "aac") args.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "192k"]);

        args.AddRange([
            "-avoid_negative_ts", "make_zero",
            "-max_muxing_queue_size", "2048",
            "-f", "hls",
            "-hls_time", "6",
            "-hls_playlist_type", "event",
            "-hls_list_size", "0",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "init.mp4",
            "-hls_flags", "temp_file+independent_segments",
            "-hls_segment_filename", Path.Combine(outputDirectory, "seg_%05d.m4s"),
            Path.Combine(outputDirectory, "index.m3u8")
        ]);
        return args;
    }
}

// One ffmpeg writing HLS segments for one viewer of one file. Sessions are keyed by what they
// produce (item, plan, start point), so a second device asking for the same thing shares the
// segments already on disk, and they are killed three minutes after the last request.
public sealed class MediaHlsSession
{
    public required string Id { get; init; }
    public required Guid ItemId { get; init; }
    public required string Directory { get; init; }
    public required double StartSeconds { get; init; }
    public required double? DurationSeconds { get; init; }
    public required IReadOnlyList<MediaSubtitleTrack> Subtitles { get; init; }
    public required long Bandwidth { get; init; }
    public required string Key { get; init; }
    public Process? Process { get; set; }
    public DateTimeOffset LastAccessed { get; set; } = DateTimeOffset.UtcNow;
    public ConcurrentQueue<string> Log { get; } = new();

    public bool Finished => Process is null || Process.HasExited;

    public string PlaylistPath => Path.Combine(Directory, "index.m3u8");

    public IReadOnlyList<string> LogTail => [.. Log];
}

public sealed class MediaHlsManager : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    private const int MaxSessions = 6;

    private readonly ConcurrentDictionary<string, MediaHlsSession> _sessions = new();
    private readonly string _ffmpegPath;
    private readonly string _root;
    private readonly ILogger<MediaHlsManager> _logger;
    private readonly Timer _sweeper;

    public MediaHlsManager(IConfiguration configuration, string dataDirectory, ILogger<MediaHlsManager> logger)
    {
        _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
        _root = Path.Combine(dataDirectory, "media-hls");
        _logger = logger;
        // Whatever a previous process left behind is unreachable now: its session ids died with it.
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        Directory.CreateDirectory(_root);
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public MediaHlsSession? Get(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    public void Touch(MediaHlsSession session) => session.LastAccessed = DateTimeOffset.UtcNow;

    // Sessions a player has fetched from within `within` - someone actually watching, as opposed
    // to a transcode left over from a closed tab. Auto-update waits for these to finish.
    public int ActiveSessionCount(TimeSpan within)
    {
        var cutoff = DateTimeOffset.UtcNow - within;
        return _sessions.Values.Count(s => s.LastAccessed >= cutoff);
    }

    public async Task<MediaHlsSession> StartAsync(
        MediaItem item, MediaPlaybackPlan plan, double startSeconds, IReadOnlyList<MediaSubtitleTrack> subtitles, CancellationToken cancellationToken,
        string? input = null)
    {
        var key = $"{item.Id:N}|{plan.VideoMode}|{plan.AudioMode}|{plan.AudioTrackIndex}|{plan.RebuildTimestamps}|{Math.Round(startSeconds)}";
        var existing = _sessions.Values.FirstOrDefault(s => s.Key == key && (!s.Finished || File.Exists(s.PlaylistPath)));
        if (existing is not null && (File.Exists(existing.PlaylistPath) || !existing.Finished))
        {
            Touch(existing);
            if (await WaitForPlaylistAsync(existing, TimeSpan.FromSeconds(60), cancellationToken)) return existing;
            Kill(existing);
        }

        EvictIfCrowded();

        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        var session = new MediaHlsSession
        {
            Id = id,
            ItemId = item.Id,
            Directory = directory,
            StartSeconds = startSeconds,
            DurationSeconds = item.DurationSeconds,
            Subtitles = subtitles,
            Bandwidth = item.Bitrate is { } bitrate and > 0 ? bitrate : 8_000_000,
            Key = key
        };

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in MediaPlaybackPlanner.BuildFfmpegArguments(item, plan, startSeconds, directory, input)) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(directory);
            throw new MediaPlaybackException("ffmpeg could not be started: " + ex.Message, []);
        }
        try { process.PriorityClass = plan.VideoMode == "copy" ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }
        session.Process = process;
        _ = Drain(process.StandardError, session);
        _ = Drain(process.StandardOutput, session);
        _sessions[id] = session;

        // A copy produces its first segment as fast as the disk reads; a transcode needs to
        // encode six seconds of video first.
        var budget = plan.VideoMode == "copy" ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(75);
        if (await WaitForPlaylistAsync(session, budget, cancellationToken)) return session;

        var log = session.LogTail;
        Kill(session);
        throw new MediaPlaybackException(
            process.HasExited ? "ffmpeg could not repackage this file." : "ffmpeg took too long to start playing this file.", log);
    }

    public string? FilePath(MediaHlsSession session, string fileName)
    {
        var safe = Path.GetFileName(fileName);
        if (safe != fileName || safe.Length == 0) return null;
        var path = Path.Combine(session.Directory, safe);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    // The playlist a player opens first: the one video rendition plus the text subtitles as
    // renditions of their own, so every player's own subtitle menu offers them.
    public string MasterPlaylist(MediaHlsSession session)
    {
        var builder = new StringBuilder();
        builder.Append("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        foreach (var subtitle in session.Subtitles)
        {
            var name = MediaSubtitles.Label(subtitle).Replace("\"", "");
            builder.Append("#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"").Append(name).Append('"');
            if (subtitle.Language is { Length: > 0 } language) builder.Append(",LANGUAGE=\"").Append(language).Append('"');
            builder.Append(",DEFAULT=NO,AUTOSELECT=YES,FORCED=").Append(subtitle.Forced ? "YES" : "NO");
            builder.Append(",URI=\"sub_").Append(subtitle.Index).Append(".m3u8\"\n");
        }
        builder.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(Math.Max(session.Bandwidth, 1_000_000));
        if (session.Subtitles.Count > 0) builder.Append(",SUBTITLES=\"subs\"");
        builder.Append("\nindex.m3u8\n");
        return builder.ToString();
    }

    // A subtitle rendition is one WebVTT file covering the whole session, shifted to the
    // session's start.
    public string SubtitlePlaylist(MediaHlsSession session, MediaSubtitleTrack subtitle)
    {
        var remaining = Math.Max(1, (session.DurationSeconds ?? 4 * 3600) - session.StartSeconds);
        var offset = session.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return "#EXTM3U\n#EXT-X-VERSION:3\n" +
            $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(remaining)}\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n" +
            $"#EXTINF:{remaining.ToString("0.000", CultureInfo.InvariantCulture)},\n" +
            $"/api/media/items/{session.ItemId:N}/subtitles/{subtitle.Index}.vtt?offset={offset}\n#EXT-X-ENDLIST\n";
    }

    private static async Task<bool> WaitForPlaylistAsync(MediaHlsSession session, TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(session.PlaylistPath))
            {
                string text;
                try { text = await File.ReadAllTextAsync(session.PlaylistPath, cancellationToken); }
                catch (IOException) { text = ""; }
                if (text.Contains("#EXTINF", StringComparison.Ordinal)) return true;
            }
            if (session.Finished && session.Process is { ExitCode: not 0 }) return false;
            if (session.Finished) return File.Exists(session.PlaylistPath);
            await Task.Delay(200, cancellationToken);
        }
        return false;
    }

    private static async Task Drain(StreamReader reader, MediaHlsSession session)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                session.Log.Enqueue(line);
                while (session.Log.Count > 20) session.Log.TryDequeue(out _);
            }
        }
        catch (Exception) { /* process gone */ }
    }

    private void EvictIfCrowded()
    {
        while (_sessions.Count >= MaxSessions)
        {
            var oldest = _sessions.Values.OrderBy(s => s.LastAccessed).FirstOrDefault();
            if (oldest is null) return;
            _logger.LogInformation("Stopping the least recently used movie session to make room");
            Kill(oldest);
        }
    }

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;
        foreach (var session in _sessions.Values)
            if (session.LastAccessed < cutoff) Kill(session);
    }

    private void Kill(MediaHlsSession session)
    {
        if (!_sessions.TryRemove(new KeyValuePair<string, MediaHlsSession>(session.Id, session))) return;
        try
        {
            if (session.Process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            session.Process?.Dispose();
        }
        catch (Exception) { /* best effort */ }
        TryDelete(session.Directory);
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var session in _sessions.Values) Kill(session);
    }
}

public sealed class MediaPlaybackException(string message, IReadOnlyList<string> ffmpegLog) : Exception(message)
{
    public IReadOnlyList<string> FfmpegLog { get; } = ffmpegLog;
}

// Text subtitles as WebVTT: extracted from the file (or converted from the sidecar .srt) once,
// cached, and shifted to a session's start on the way out.
public static class MediaSubtitles
{
    public static string Label(MediaSubtitleTrack track)
    {
        var language = LanguageName(track.Language);
        var parts = new List<string>();
        if (track.Title is { Length: > 0 } title && !string.Equals(title, language, StringComparison.OrdinalIgnoreCase)) parts.Add(title);
        else if (language is not null) parts.Add(language);
        else parts.Add($"Subtitle {track.Index + 1}");
        if (track.Forced) parts.Add("(forced)");
        if (track.External is not null) parts.Add("(file)");
        return string.Join(' ', parts);
    }

    public static string? LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        "en" or "eng" => "English",
        "es" or "spa" => "Spanish",
        "fr" or "fre" or "fra" => "French",
        "de" or "ger" or "deu" => "German",
        "it" or "ita" => "Italian",
        "pt" or "por" => "Portuguese",
        "ja" or "jpn" => "Japanese",
        "ko" or "kor" => "Korean",
        "zh" or "chi" or "zho" => "Chinese",
        "nl" or "dut" or "nld" => "Dutch",
        "sv" or "swe" => "Swedish",
        "ru" or "rus" => "Russian",
        null or "" or "und" => null,
        var other => other.ToUpperInvariant()
    };

    public static async Task<string?> EnsureVttAsync(
        MediaItem item, MediaSubtitleTrack track, MediaFiles files, string cacheDirectory, string ffmpegPath, ILogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var cached = Path.Combine(cacheDirectory, $"{item.Id:N}-{track.Index}.vtt");
        if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;

        var input = track.External ?? item.Path;
        if (!await Task.Run(() => files.FileExists(input), cancellationToken)) return null;
        if (track.External is not null && track.Codec == "vtt")
        {
            await using (var source = files.For(input).OpenRead(input))
            await using (var destination = File.Create(cached))
                await source.CopyToAsync(destination, cancellationToken);
            return cached;
        }

        var temporary = cached + ".part";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", files.FfmpegInput(input) };
        if (track.External is null) args.AddRange(["-map", $"0:s:{track.StreamIndex ?? 0}"]);
        args.AddRange(["-f", "webvtt", temporary]);
        var ok = await FfmpegProcess.RunAsync(ffmpegPath, args, TimeSpan.FromMinutes(3), logger, "subtitle extraction", ProcessPriorityClass.BelowNormal, cancellationToken);
        if (!ok || !File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } return null; }
        File.Move(temporary, cached, overwrite: true);
        return cached;
    }

    // Moves every cue earlier by `offsetSeconds`, dropping what ends before the new zero. Pure.
    public static string Shift(string vtt, double offsetSeconds)
    {
        if (offsetSeconds <= 0) return vtt;
        var output = new StringBuilder();
        var lines = vtt.Replace("\r\n", "\n").Split('\n');
        var skippingCue = false;
        foreach (var line in lines)
        {
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow > 0)
            {
                var start = ParseTimestamp(line[..arrow].Trim());
                var rest = line[(arrow + 3)..].TrimStart();
                var endText = rest.Split(' ', 2)[0];
                var settings = rest.Length > endText.Length ? rest[endText.Length..] : "";
                var end = ParseTimestamp(endText);
                if (start is null || end is null) { output.Append(line).Append('\n'); continue; }
                var newEnd = end.Value - offsetSeconds;
                if (newEnd <= 0) { skippingCue = true; TrimTrailingCueHeader(output); continue; }
                var newStart = Math.Max(0, start.Value - offsetSeconds);
                output.Append(Format(newStart)).Append(" --> ").Append(Format(newEnd)).Append(settings).Append('\n');
                skippingCue = false;
                continue;
            }
            if (line.Length == 0) skippingCue = false;
            if (!skippingCue) output.Append(line).Append('\n');
        }
        return output.ToString();
    }

    // A cue that was dropped may have had an identifier line just before its timing line.
    private static void TrimTrailingCueHeader(StringBuilder output)
    {
        var text = output.ToString();
        var lastBreak = text.TrimEnd('\n').LastIndexOf('\n');
        var lastLine = text.TrimEnd('\n')[(lastBreak + 1)..];
        if (lastLine.Length > 0 && !lastLine.Contains("-->", StringComparison.Ordinal) && !lastLine.StartsWith("WEBVTT", StringComparison.Ordinal)
            && !lastLine.StartsWith("NOTE", StringComparison.Ordinal) && !lastLine.StartsWith("STYLE", StringComparison.Ordinal))
            output.Length = lastBreak + 1;
    }

    private static double? ParseTimestamp(string text)
    {
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total;
    }

    private static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }
}
