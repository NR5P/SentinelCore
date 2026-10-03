using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SentinelCore.Web;

// Transcripts: the four formats a feed's podcast:transcript can point at (WebVTT, SRT, the
// Podcasting 2.0 JSON and its HTML), and whisper.cpp for shows that publish none.
public static partial class PodcastTranscriptParser
{
    // Null when the file is none of the formats, or holds no text.
    public static PodcastTranscript? Parse(byte[] content, string? type, string source, string? language)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var kind = (type ?? "").ToLowerInvariant();
        var trimmed = text.TrimStart();
        IReadOnlyList<PodcastTranscriptSegment> segments;
        if (kind.Contains("vtt", StringComparison.Ordinal) || trimmed.StartsWith("WEBVTT", StringComparison.Ordinal)) segments = ParseVtt(text);
        else if (kind.Contains("json", StringComparison.Ordinal) || trimmed.StartsWith('{') || trimmed.StartsWith('[')) segments = ParseJson(text);
        else if (kind.Contains("srt", StringComparison.Ordinal) || kind.Contains("subrip", StringComparison.Ordinal)) segments = ParseSrt(text);
        else if (kind.Contains("html", StringComparison.Ordinal) || trimmed.StartsWith('<')) segments = ParseHtml(text);
        else if (text.Contains("-->", StringComparison.Ordinal)) segments = ParseSrt(text);
        else segments = PlainText(text);

        segments = Consolidate(segments);
        return segments.Count == 0 ? null : new PodcastTranscript(source, language, segments);
    }

    public static IReadOnlyList<PodcastTranscriptSegment> ParseVtt(string text) => ParseCues(text, vtt: true);

    public static IReadOnlyList<PodcastTranscriptSegment> ParseSrt(string text) => ParseCues(text, vtt: false);

    private static List<PodcastTranscriptSegment> ParseCues(string text, bool vtt)
    {
        var segments = new List<PodcastTranscriptSegment>();
        var blocks = BlankLine().Split(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            var timingIndex = lines.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0) continue;
            var timing = CueTiming().Match(lines[timingIndex]);
            if (!timing.Success) continue;
            var start = ParseTimestamp(timing.Groups[1].Value);
            var end = ParseTimestamp(timing.Groups[2].Value);
            if (start is null || end is null) continue;

            string? speaker = null;
            var body = string.Join(" ", lines.Skip(timingIndex + 1));
            if (vtt)
            {
                var voice = VoiceTag().Match(body);
                if (voice.Success) speaker = voice.Groups[1].Value.Trim();
            }
            body = WebUtility.HtmlDecode(AnyTag().Replace(body, "")).Trim();
            if (body.Length == 0) continue;
            segments.Add(new PodcastTranscriptSegment(start.Value, Math.Max(start.Value, end.Value), speaker, Spaces().Replace(body, " ")));
        }
        return segments;
    }

    // { "version": "1.0.0", "segments": [ { "speaker", "startTime", "endTime", "body" } ] }
    public static IReadOnlyList<PodcastTranscriptSegment> ParseJson(string text)
    {
        var segments = new List<PodcastTranscriptSegment>();
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var array = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.TryGetProperty("segments", out var found) ? found : default;
            if (array.ValueKind != JsonValueKind.Array) return segments;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var start = Number(item, "startTime") ?? Number(item, "start");
                var end = Number(item, "endTime") ?? Number(item, "end") ?? start;
                var body = String(item, "body") ?? String(item, "text");
                if (start is null || string.IsNullOrWhiteSpace(body)) continue;
                segments.Add(new PodcastTranscriptSegment(start.Value, Math.Max(start.Value, end ?? start.Value), String(item, "speaker")?.Trim(), body.Trim()));
            }
        }
        catch (JsonException) { }
        return segments;
    }

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return ParseTimestamp(text ?? "");
        }
        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The Podcasting 2.0 HTML transcript: <cite>Speaker:</cite> <time>0:00</time> <p>Words</p>,
    // repeated. A page with no <time> stamps is kept as one block of text.
    public static IReadOnlyList<PodcastTranscriptSegment> ParseHtml(string html)
    {
        var segments = new List<PodcastTranscriptSegment>();
        string? speaker = null;
        double? time = null;
        foreach (Match match in HtmlParts().Matches(html))
        {
            var tag = match.Groups[1].Value.ToLowerInvariant();
            var inner = WebUtility.HtmlDecode(AnyTag().Replace(match.Groups[2].Value, "")).Trim();
            switch (tag)
            {
                case "cite":
                    speaker = inner.TrimEnd(':').Trim();
                    if (speaker.Length == 0) speaker = null;
                    break;
                case "time":
                    time = ParseTimestamp(inner);
                    break;
                case "p" when inner.Length > 0 && time is { } start:
                    segments.Add(new PodcastTranscriptSegment(start, start, speaker, Spaces().Replace(inner, " ")));
                    time = null;
                    break;
            }
        }
        if (segments.Count == 0) return PlainText(PodcastText.ToPlainText(html));

        // HTML transcripts carry starts only; each runs until the next.
        for (var i = 0; i < segments.Count; i++)
        {
            var end = i + 1 < segments.Count ? segments[i + 1].Start : segments[i].Start + 5;
            segments[i] = segments[i] with { End = Math.Max(segments[i].Start, end) };
        }
        return segments;
    }

    private static List<PodcastTranscriptSegment> PlainText(string text)
    {
        var clean = Spaces().Replace(text, " ").Trim();
        return clean.Length == 0 ? [] : [new PodcastTranscriptSegment(0, 0, null, clean)];
    }

    // "01:02:03.500", "02:03.5", "1:02:03,500" (SRT), "62.5".
    public static double? ParseTimestamp(string text)
    {
        var value = text.Trim().Replace(',', '.');
        if (value.Length == 0) return null;
        double total = 0;
        foreach (var part in value.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0) return null;
            total = total * 60 + number;
        }
        return total;
    }

    // Word-by-word transcripts (common in JSON) become sentence-sized segments, so the player and
    // search show lines rather than one word per row. Same speaker, up to about ten seconds, ended
    // early at a full stop.
    public static IReadOnlyList<PodcastTranscriptSegment> Consolidate(IReadOnlyList<PodcastTranscriptSegment> segments)
    {
        if (segments.Count == 0 || segments.Average(s => s.Text.Length) >= 25) return segments;
        var merged = new List<PodcastTranscriptSegment>();
        PodcastTranscriptSegment? current = null;
        foreach (var segment in segments)
        {
            if (current is not null && current.Speaker == segment.Speaker && segment.End - current.Start <= 10
                && !current.Text.EndsWith('.') && !current.Text.EndsWith('?') && !current.Text.EndsWith('!'))
            {
                current = current with { End = Math.Max(current.End, segment.End), Text = current.Text + " " + segment.Text };
                continue;
            }
            if (current is not null) merged.Add(current);
            current = segment;
        }
        if (current is not null) merged.Add(current);
        return merged;
    }

    // whisper-cli -oj: { "result": { "language": "en" }, "transcription": [ { "offsets": { "from": 0, "to": 4260 }, "text": " ..." } ] }
    public static PodcastTranscript? ParseWhisperJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? language = null;
            if (root.TryGetProperty("result", out var result) && result.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String)
                language = lang.GetString();
            if (!root.TryGetProperty("transcription", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            var segments = new List<PodcastTranscriptSegment>();
            foreach (var item in items.EnumerateArray())
            {
                var text = item.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(text) || !item.TryGetProperty("offsets", out var offsets)) continue;
                var from = offsets.TryGetProperty("from", out var f) && f.TryGetDouble(out var fromMs) ? fromMs / 1000 : 0;
                var to = offsets.TryGetProperty("to", out var e) && e.TryGetDouble(out var toMs) ? toMs / 1000 : from;
                segments.Add(new PodcastTranscriptSegment(from, Math.Max(from, to), null, text));
            }
            return segments.Count == 0 ? null : new PodcastTranscript("whisper", language, segments);
        }
        catch (JsonException) { return null; }
    }

    // The text around a moment, for a bookmark: every segment overlapping [from, to].
    public static string? Snippet(PodcastTranscript transcript, double from, double to)
    {
        var words = transcript.Segments
            .Where(s => s.End >= from && s.Start <= to || s.End <= s.Start && s.Start >= from && s.Start <= to)
            .Select(s => s.Text)
            .ToList();
        if (words.Count == 0) return null;
        var text = string.Join(" ", words).Trim();
        return text.Length > 600 ? PodcastText.Summary(text, 600) : text;
    }

    // Best first when a feed offers several.
    public static int Preference(string? type)
    {
        var kind = (type ?? "").ToLowerInvariant();
        if (kind.Contains("json", StringComparison.Ordinal)) return 0;
        if (kind.Contains("vtt", StringComparison.Ordinal)) return 1;
        if (kind.Contains("srt", StringComparison.Ordinal) || kind.Contains("subrip", StringComparison.Ordinal)) return 2;
        if (kind.Contains("html", StringComparison.Ordinal)) return 3;
        return 4;
    }

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLine();

    [GeneratedRegex(@"((?:\d+:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)\s*-->\s*((?:\d+:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?)")]
    private static partial Regex CueTiming();

    [GeneratedRegex(@"<v(?:\.[^ >]*)?\s+([^>]+)>")]
    private static partial Regex VoiceTag();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<(cite|time|p)\b[^>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlParts();
}

// Runs whisper.cpp. Behind an interface so the tests never need it installed.
public interface IPodcastWhisperRunner
{
    // Null when whisper can run with these settings, else what is missing, in words.
    Task<string?> CheckAsync(PodcastSettingsDto options, CancellationToken cancellationToken);
    Task<PodcastTranscript?> TranscribeAsync(string wavPath, string workDirectory, PodcastSettingsDto options, CancellationToken cancellationToken);
}

public sealed class WhisperCppRunner(ILogger<WhisperCppRunner> logger) : IPodcastWhisperRunner
{
    private (string Key, string? Error, DateTimeOffset At)? _checked;

    public async Task<string?> CheckAsync(PodcastSettingsDto options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.WhisperModel))
            return "Choose a whisper model file (a ggml-*.bin, e.g. ggml-base.en.bin from huggingface.co/ggerganov/whisper.cpp) in Settings → Podcasts.";
        if (!File.Exists(options.WhisperModel))
            return $"The whisper model {options.WhisperModel} is not on the server.";

        var command = string.IsNullOrWhiteSpace(options.WhisperCommand) ? "whisper-cli" : options.WhisperCommand.Trim();
        var key = command + "|" + options.WhisperModel;
        if (_checked is { } last && last.Key == key && last.At > DateTimeOffset.UtcNow.AddMinutes(-5)) return last.Error;

        var result = await PodcastProcess.RunAsync(command, ["--help"], TimeSpan.FromSeconds(20), cancellationToken);
        var error = result.Started
            ? null
            : $"{command} could not be run on the server. Install whisper.cpp (\"brew install whisper-cpp\" on a Mac; build it from github.com/ggerganov/whisper.cpp on Linux) or set the full path to whisper-cli in Settings → Podcasts.";
        _checked = (key, error, DateTimeOffset.UtcNow);
        return error;
    }

    public async Task<PodcastTranscript?> TranscribeAsync(string wavPath, string workDirectory, PodcastSettingsDto options, CancellationToken cancellationToken)
    {
        var command = string.IsNullOrWhiteSpace(options.WhisperCommand) ? "whisper-cli" : options.WhisperCommand.Trim();
        var outBase = Path.Combine(workDirectory, Path.GetFileNameWithoutExtension(wavPath) + "-whisper");
        try
        {
            // An English-only model (ggml-*.en.bin) is English; any other is asked to work the
            // language out, so a Spanish show is not transcribed as English gibberish.
            var language = Path.GetFileName(options.WhisperModel).Contains(".en.", StringComparison.OrdinalIgnoreCase) ? "en" : "auto";
            var result = await PodcastProcess.RunAsync(command,
                ["-m", options.WhisperModel, "-f", wavPath, "-oj", "-of", outBase, "-l", language,
                 "-t", Math.Clamp(options.WhisperThreads, 1, 32).ToString(CultureInfo.InvariantCulture), "-np"],
                TimeSpan.FromHours(6), cancellationToken, ProcessPriorityClass.BelowNormal);
            var json = outBase + ".json";
            if (result.ExitCode != 0 || !File.Exists(json))
            {
                logger.LogWarning("whisper exited {Code}: {Error}", result.ExitCode, PodcastProcess.Tail(result.Stderr));
                throw new InvalidOperationException($"whisper stopped with an error: {PodcastProcess.Tail(result.Stderr, 2)}");
            }
            return PodcastTranscriptParser.ParseWhisperJson(await File.ReadAllTextAsync(json, cancellationToken));
        }
        finally
        {
            try { File.Delete(outBase + ".json"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed record PodcastProcessResult(bool Started, int ExitCode, string Stdout, string Stderr);

// Runs a tool to completion and keeps both of its outputs - ffmpeg's silencedetect and ffprobe
// answer on stderr and stdout respectively. Both pipes are drained while it runs (see
// FfmpegProcess for why an undrained pipe is a hang).
public static class PodcastProcess
{
    public static async Task<PodcastProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken, ProcessPriorityClass? priority = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new PodcastProcessResult(false, -1, "", ex.Message);
        }
        if (process is null) return new PodcastProcessResult(false, -1, "", "");

        using (process)
        {
            if (priority is { } p)
            {
                try { process.PriorityClass = p; } catch { /* best effort */ }
            }
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                if (cancellationToken.IsCancellationRequested) throw;
                return new PodcastProcessResult(true, -1, "", "timed out");
            }
            return new PodcastProcessResult(true, process.ExitCode, await stdout, await stderr);
        }
    }

    public static string Tail(string text, int lines = 8) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));
}

// One whisper job at a time: transcription is the heaviest thing this server does, and two at
// once only makes both slower. Episodes not downloaded yet are downloaded first.
public sealed class PodcastTranscriber
{
    private readonly IPodcastStore _store;
    private readonly AppSettingsFile _settings;
    private readonly IPodcastWhisperRunner _whisper;
    private readonly PodcastAudioProcessor _audio;
    private readonly PodcastLocations _locations;
    private readonly PodcastDownloadManager _downloads;
    private readonly ILogger<PodcastTranscriber> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly HashSet<Guid> _pending = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;

    public PodcastTranscriber(IPodcastStore store, AppSettingsFile settings, IPodcastWhisperRunner whisper, PodcastAudioProcessor audio,
        PodcastLocations locations, PodcastDownloadManager downloads, ILogger<PodcastTranscriber> logger)
    {
        _store = store;
        _settings = settings;
        _whisper = whisper;
        _audio = audio;
        _locations = locations;
        _downloads = downloads;
        _logger = logger;
        _downloads.Completed += episode =>
        {
            if (episode.Download?.TranscribeAfter == true) Enqueue(episode.Id);
        };
    }

    public int Queued
    {
        get { lock (_gate) return _pending.Count; }
    }

    public Guid? Current { get; private set; }

    public string? LastError { get; private set; }

    public Task<string?> CheckAsync(PodcastSettingsDto options, CancellationToken cancellationToken) =>
        options.TranscriptsEnabled
            ? _whisper.CheckAsync(options, cancellationToken)
            : Task.FromResult<string?>("Transcripts are turned off in Settings → Podcasts.");

    public bool IsQueued(Guid episodeId)
    {
        lock (_gate) return _pending.Contains(episodeId);
    }

    public void Enqueue(Guid episodeId)
    {
        lock (_gate)
        {
            if (!_pending.Add(episodeId)) return;
            _worker ??= Task.Run(RunAsync);
        }
        _queue.Writer.TryWrite(episodeId);
    }

    private async Task RunAsync()
    {
        var token = _shutdown.Token;
        try
        {
            await foreach (var episodeId in _queue.Reader.ReadAllAsync(token))
            {
                Current = episodeId;
                try { await TranscribeAsync(episodeId, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _logger.LogWarning(ex, "Transcribing podcast episode {Episode} failed", episodeId);
                }
                finally
                {
                    Current = null;
                    lock (_gate) _pending.Remove(episodeId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TranscribeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        var options = await PodcastLocations.ReadOptionsAsync(_settings, cancellationToken);
        if (await CheckAsync(options, cancellationToken) is { } problem)
        {
            LastError = problem;
            return;
        }
        var episode = await _store.GetEpisodeAsync(episodeId, cancellationToken);
        if (episode is null) return;
        if (episode.Download is not { IsDone: true } download || !File.Exists(download.FilePath))
        {
            // Downloaded first; the download's completion queues this again.
            lock (_gate) _pending.Remove(episodeId);
            await _downloads.RequestAsync(episodeId, transcribeAfter: true, cancellationToken);
            return;
        }

        var work = _locations.WorkFolder;
        Directory.CreateDirectory(work);
        var wav = Path.Combine(work, $"{episodeId:N}-16k.wav");
        try
        {
            if (!await _audio.ToWavAsync(download.FilePath!, wav, cancellationToken))
                throw new InvalidOperationException("ffmpeg could not read the episode's audio.");
            var transcript = await _whisper.TranscribeAsync(wav, work, options, cancellationToken)
                ?? throw new InvalidOperationException("whisper heard no words in this episode.");

            // Stored in the publisher's timing, like everything else (see Podcast.cs).
            var timeline = download.Timeline;
            var segments = transcript.Segments
                .Select(s => s with { Start = timeline.ToOriginal(s.Start), End = timeline.ToOriginal(s.End) })
                .ToList();
            await _store.SaveTranscriptAsync(episodeId, transcript with { Source = "whisper", Segments = segments }, cancellationToken);
            download.TranscribeAfter = false;
            if (await _store.GetDownloadAsync(episodeId, cancellationToken) is { } fresh)
            {
                fresh.TranscribeAfter = false;
                await _store.SaveDownloadAsync(fresh, cancellationToken);
            }
            LastError = null;
            _logger.LogInformation("Transcribed {Title} ({Count} segments)", episode.Title, segments.Count);
        }
        finally
        {
            try { File.Delete(wav); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Stop() => _shutdown.Cancel();
}
