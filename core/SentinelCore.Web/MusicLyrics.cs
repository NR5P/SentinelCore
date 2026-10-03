using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

public sealed record MusicLyricLine(double? Time, string Text);

// GET /api/music/tracks/{id}/lyrics, before it becomes JSON.
public sealed record MusicLyricsResult(bool Synced, IReadOnlyList<MusicLyricLine> Lines, string Plain, string Source);

// LRC: "[01:02.50]text", several stamps on one line for a repeated chorus, an [offset:+500]
// header in milliseconds (positive shows every line that much sooner), and the [ar:]/[ti:]
// header lines and <00:12.34> word stamps of enhanced LRC, which are dropped.
public static partial class MusicLrc
{
    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    [GeneratedRegex(@"^\[[a-zA-Z#]+:[^\]]*\]$")]
    private static partial Regex Header();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordStamp();

    public static bool LooksSynced(string? text) => text is not null && text.Split('\n').Any(line => Stamp().IsMatch(line.Trim()));

    public static MusicLyricsResult Parse(string text, string source)
    {
        var offsetSeconds = 0.0;
        var timed = new List<(double Time, int Order, string Text)>();
        var plain = new List<string>();
        var order = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (Offset().Match(line) is { Success: true } offset)
            {
                offsetSeconds = int.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
                continue;
            }
            var stamps = new List<double>();
            while (Stamp().Match(line) is { Success: true } stamp)
            {
                var minutes = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = stamp.Groups[3].Success
                    ? int.Parse(stamp.Groups[3].Value, CultureInfo.InvariantCulture) / Math.Pow(10, stamp.Groups[3].Value.Length)
                    : 0;
                stamps.Add(minutes * 60 + seconds + fraction);
                line = line[stamp.Length..].TrimStart();
            }
            if (stamps.Count == 0 && Header().IsMatch(line)) continue;
            var words = WordStamp().Replace(line, "").Trim();
            if (stamps.Count == 0)
            {
                plain.Add(words);
                continue;
            }
            foreach (var at in stamps) timed.Add((at, order++, words));
            plain.Add(words);
        }

        if (timed.Count == 0)
        {
            // Unsynced: keep the verse breaks, but not blank lines at either end.
            var lines = TrimBlank(plain);
            return new MusicLyricsResult(false, lines.Select(l => new MusicLyricLine(null, l)).ToList(), string.Join("\n", lines), source);
        }
        var sorted = timed
            .Select(t => (Time: Math.Max(0, Math.Round(t.Time - offsetSeconds, 3)), t.Order, t.Text))
            .OrderBy(t => t.Time).ThenBy(t => t.Order)
            .Select(t => new MusicLyricLine(t.Time, t.Text))
            .ToList();
        return new MusicLyricsResult(true, sorted, string.Join("\n", TrimBlank(sorted.Select(l => l.Text).ToList())), source);
    }

    private static List<string> TrimBlank(List<string> lines)
    {
        var start = 0;
        var end = lines.Count;
        while (start < end && lines[start].Length == 0) start++;
        while (end > start && lines[end - 1].Length == 0) end--;
        return lines.GetRange(start, end - start);
    }
}

// Finds a track's lyrics: a <file>.lrc beside it, then lyrics embedded in the file, then LRCLIB
// (lrclib.net, free, no key) the first time someone asks, when Settings allow it. What LRCLIB
// says - including "none" - is kept in music.db, so a song with no lyrics online is asked about
// once a month at most.
public sealed class MusicLyricsService(IMusicStore store, MediaFiles files, IHttpClientFactory http, AppSettingsFile settings, ILogger<MusicLyricsService> logger)
{
    public const string HttpClientName = "MusicLrclib";
    private static readonly TimeSpan NotFoundRetry = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MusicLyricsResult?> GetAsync(MusicTrack track, CancellationToken cancellationToken)
    {
        var fs = files.For(track.Path);
        if (track.LyricsSidecar is { } sidecar)
        {
            try
            {
                var text = await Task.Run(() => ReadText(fs, sidecar), cancellationToken);
                if (!string.IsNullOrWhiteSpace(text)) return MusicLrc.Parse(text, "sidecar");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Could not read {Path}: {Message}", sidecar, ex.Message);
            }
        }
        if (track.HasEmbeddedLyrics)
        {
            try
            {
                var text = await Task.Run(() => MusicTags.ReadLyrics(fs, track.Path), cancellationToken);
                if (!string.IsNullOrWhiteSpace(text)) return MusicLrc.Parse(text, "embedded");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("Could not read the lyrics in {Path}: {Message}", track.Path, ex.Message);
            }
        }

        var cached = await store.GetLyricsAsync(track.Id, cancellationToken);
        if (cached is { Found: true, Body: { } body }) return MusicLrc.Parse(body, cached.Source ?? "lrclib");
        if (cached is { Found: false } && cached.FetchedAt > DateTimeOffset.UtcNow - NotFoundRetry) return null;

        var options = await settings.GetSectionAsync<MusicSettingsDto>(MusicSettingsDto.SectionName, cancellationToken) ?? new();
        if (!options.FetchLyricsOnline) return null;

        string? found;
        try { found = await LookUpAsync(track, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            // Not cached: LRCLIB being down is not the same as the song having no lyrics.
            logger.LogInformation("LRCLIB lookup for {Title} failed: {Message}", track.Title, ex.Message);
            return null;
        }
        await store.SetLyricsAsync(new MusicLyricsRecord(track.Id, found is not null, found is null ? null : "lrclib",
            found is not null && MusicLrc.LooksSynced(found), found, DateTimeOffset.UtcNow), cancellationToken);
        return found is null ? null : MusicLrc.Parse(found, "lrclib");
    }

    private static string ReadText(IMediaFileSystem fs, string path)
    {
        using var stream = fs.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // The exact match first (artist, title, album and length within a couple of seconds), then a
    // search, keeping the result whose length is closest.
    private async Task<string?> LookUpAsync(MusicTrack track, CancellationToken cancellationToken)
    {
        var client = http.CreateClient(HttpClientName);
        var artist = track.PrimaryArtist;
        var query = $"artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(track.Title)}"
            + $"&album_name={Uri.EscapeDataString(track.Album)}&duration={Math.Round(track.DurationSeconds).ToString(CultureInfo.InvariantCulture)}";
        using (var response = await client.GetAsync("https://lrclib.net/api/get?" + query, cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                var hit = await response.Content.ReadFromJsonAsync<LrclibTrack>(JsonOptions, cancellationToken);
                if (Text(hit) is { } text) return text;
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }

        var search = $"track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(artist)}";
        var results = await client.GetFromJsonAsync<List<LrclibTrack>>("https://lrclib.net/api/search?" + search, JsonOptions, cancellationToken) ?? [];
        var best = results
            .Where(r => Text(r) is not null && (track.DurationSeconds <= 0 || r.Duration is not { } d || Math.Abs(d - track.DurationSeconds) <= 3))
            .OrderByDescending(r => r.SyncedLyrics is not null)
            .ThenBy(r => r.Duration is { } d ? Math.Abs(d - track.DurationSeconds) : 99)
            .FirstOrDefault();
        return Text(best);
    }

    private static string? Text(LrclibTrack? track) =>
        track is null || track.Instrumental ? null
        : !string.IsNullOrWhiteSpace(track.SyncedLyrics) ? track.SyncedLyrics
        : !string.IsNullOrWhiteSpace(track.PlainLyrics) ? track.PlainLyrics
        : null;

    private sealed class LrclibTrack
    {
        public double? Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
