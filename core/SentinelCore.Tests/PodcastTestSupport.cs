using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// A publisher's web server on loopback: feeds (with ETag and gzip), redirects, and episode files
// served with Range support - what PodcastHttp and the download manager talk to in production,
// minus the internet.
internal sealed class PodcastTestServer : IDisposable
{
    public sealed record Reply(int Status, byte[] Body, string ContentType, Dictionary<string, string> Headers);

    private HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, Func<HttpListenerRequest, Reply>> _routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();

    public ConcurrentDictionary<string, int> Hits { get; } = new(StringComparer.Ordinal);
    public ConcurrentBag<string> RangeRequests { get; } = [];

    public PodcastTestServer()
    {
        // The free port is only free until someone else takes it; with the whole suite running in
        // parallel that happens, so a taken port is simply tried again with another.
        for (var attempt = 0; ; attempt++)
        {
            var url = $"http://127.0.0.1:{FreePort()}";
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(url + "/");
                _listener.Start();
                BaseUrl = url;
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                _listener.Close();
            }
        }
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public string Url(string path) => BaseUrl + path;

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Route(string path, Func<HttpListenerRequest, Reply> handler) => _routes[path] = handler;

    // A feed answering 304 to its own ETag.
    public void Feed(string path, string xml, string? etag = null, bool gzip = false)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        Route(path, request =>
        {
            if (etag is not null && request.Headers["If-None-Match"] == etag)
                return new Reply(304, [], "application/rss+xml", new() { ["ETag"] = etag });
            var headers = new Dictionary<string, string>();
            if (etag is not null) headers["ETag"] = etag;
            var body = bytes;
            if (gzip && (request.Headers["Accept-Encoding"] ?? "").Contains("gzip", StringComparison.Ordinal))
            {
                using var buffer = new MemoryStream();
                using (var zip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(bytes);
                body = buffer.ToArray();
                headers["Content-Encoding"] = "gzip";
            }
            return new Reply(200, body, "application/rss+xml; charset=utf-8", headers);
        });
    }

    public void Redirect(string path, string to, int status = 301) =>
        Route(path, _ => new Reply(status, [], "text/plain", new() { ["Location"] = to }));

    public void Bytes(string path, byte[] content, string contentType) =>
        Route(path, request =>
        {
            var range = request.Headers["Range"];
            if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                RangeRequests.Add(path + " " + range);
                var parts = range[6..].Split('-');
                var start = long.Parse(parts[0]);
                var end = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : content.Length - 1;
                if (start >= content.Length)
                    return new Reply(416, [], contentType, new() { ["Content-Range"] = $"bytes */{content.Length}" });
                end = Math.Min(end, content.Length - 1);
                return new Reply(206, content[(int)start..(int)(end + 1)], contentType,
                    new() { ["Content-Range"] = $"bytes {start}-{end}/{content.Length}", ["Accept-Ranges"] = "bytes" });
            }
            return new Reply(200, content, contentType, new() { ["Accept-Ranges"] = "bytes" });
        });

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => Answer(context));
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        Hits.AddOrUpdate(path, 1, (_, n) => n + 1);
        try
        {
            var reply = _routes.TryGetValue(path, out var handler)
                ? handler(context.Request)
                : new Reply(404, Encoding.UTF8.GetBytes("not here"), "text/plain", []);
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.ContentType;
            foreach (var (name, value) in reply.Headers) context.Response.Headers[name] = value;
            if (reply.Status != 304)
            {
                context.Response.ContentLength64 = reply.Body.Length;
                context.Response.OutputStream.Write(reply.Body);
            }
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
    }
}

internal sealed class RecordingPodcastNotifier : IPodcastNotifier
{
    public ConcurrentQueue<(IReadOnlyCollection<string> Users, PodcastShow Show, IReadOnlyList<PodcastEpisode> Episodes)> Sent { get; } = new();

    public Task NotifyAsync(IReadOnlyCollection<string> userKeys, PodcastShow show, IReadOnlyList<PodcastEpisode> episodes, CancellationToken cancellationToken)
    {
        Sent.Enqueue((userKeys.ToList(), show, episodes.ToList()));
        return Task.CompletedTask;
    }
}

internal sealed class FakeWhisperRunner : IPodcastWhisperRunner
{
    public int Runs;

    public Task<string?> CheckAsync(PodcastSettingsDto options, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<PodcastTranscript?> TranscribeAsync(string wavPath, string workDirectory, PodcastSettingsDto options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Runs);
        Assert.True(File.Exists(wavPath), "whisper was given a WAV that does not exist");
        return Task.FromResult<PodcastTranscript?>(new PodcastTranscript("whisper", "en",
        [
            new PodcastTranscriptSegment(0, 2, null, "A tone to begin with."),
            new PodcastTranscriptSegment(2.3, 4.3, null, "Then a higher tone after the pause.")
        ]));
    }
}

// The podcast services wired by hand, as Program.cs wires them, against a scratch data folder.
internal sealed class PodcastTestKit : IDisposable
{
    private readonly ServiceProvider _provider;

    public PodcastTestKit(string podcastSettingsJson = "{}")
    {
        Root = Path.Combine(Path.GetTempPath(), $"podcast-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        SettingsPath = Path.Combine(Root, "appsettings.json");
        File.WriteAllText(SettingsPath, $$"""{ "Podcasts": {{podcastSettingsJson}} }""");
        Settings = new AppSettingsFile(SettingsPath);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(PodcastHttp.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All });
        services.AddHttpClient(PodcastDownloadManager.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = true });
        _provider = services.BuildServiceProvider();
        var clients = _provider.GetRequiredService<IHttpClientFactory>();

        Store = new SqlitePodcastStore(Path.Combine(Root, "data"));
        Locations = new PodcastLocations(Path.Combine(Root, "data"), Settings);
        Http = new PodcastHttp(clients);
        Audio = new PodcastAudioProcessor(new ConfigurationBuilder().Build(), NullLogger<PodcastAudioProcessor>.Instance);
        Downloads = new PodcastDownloadManager(Store, clients, Audio, Locations, Settings, NullLogger<PodcastDownloadManager>.Instance);
        Transcriber = new PodcastTranscriber(Store, Settings, Whisper, Audio, Locations, Downloads, NullLogger<PodcastTranscriber>.Instance);
        Service = new PodcastService(Store, Http, Downloads, Transcriber, Notifier, Settings, NullLogger<PodcastService>.Instance);
    }

    public string Root { get; }
    public string SettingsPath { get; }
    public AppSettingsFile Settings { get; }
    public SqlitePodcastStore Store { get; }
    public PodcastLocations Locations { get; }
    public PodcastHttp Http { get; }
    public PodcastAudioProcessor Audio { get; }
    public PodcastDownloadManager Downloads { get; }
    public FakeWhisperRunner Whisper { get; } = new();
    public PodcastTranscriber Transcriber { get; }
    public RecordingPodcastNotifier Notifier { get; } = new();
    public PodcastService Service { get; }

    public Task<PodcastView> ViewAsync(string userKey = "") => PodcastView.LoadAsync(Store, Downloads, userKey, true, CancellationToken.None);

    public static async Task WaitForAsync(Func<Task<bool>> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    public static async Task<bool> FfmpegAvailableAsync() =>
        (await PodcastProcess.RunAsync("ffmpeg", ["-version"], TimeSpan.FromSeconds(10), CancellationToken.None)).ExitCode == 0;

    // 49 seconds of MP3: a 2 s tone, 3 s of silence, a 2 s tone, 2 s of silence, a 40 s tone (long
    // enough that a position in the middle is not "within 30 s of the end"). Optionally with ID3
    // chapters at 0 s ("Opening") and 5 s ("Second half").
    public async Task<byte[]> MakeEpisodeAudioAsync(bool chapters = false)
    {
        var path = Path.Combine(Root, $"tone-{Guid.NewGuid():N}.mp3");
        var result = await PodcastProcess.RunAsync("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "sine=f=440:d=2,apad=pad_dur=3[a];sine=f=660:d=2,apad=pad_dur=2[b];sine=f=880:d=40[c];[a][b][c]concat=n=3:v=0:a=1",
            "-c:a", "libmp3lame", "-b:a", "64k", path
        ], TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, "ffmpeg could not make the test episode: " + result.Stderr);
        if (!chapters) return await File.ReadAllBytesAsync(path);

        var metadata = Path.Combine(Root, $"chapters-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(metadata, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=5000\ntitle=Opening\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=5000\nEND=49000\ntitle=Second half\n");
        var withChapters = Path.Combine(Root, $"tone-ch-{Guid.NewGuid():N}.mp3");
        result = await PodcastProcess.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-i", metadata, "-map", "0", "-map_chapters", "1", "-c", "copy", "-id3v2_version", "3", withChapters],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, "ffmpeg could not add chapters: " + result.Stderr);
        return await File.ReadAllBytesAsync(withChapters);
    }

    public async Task SetPodcastSettingsAsync(string json)
    {
        await File.WriteAllTextAsync(SettingsPath, $$"""{ "Podcasts": {{json}} }""");
    }

    public void Dispose()
    {
        Transcriber.Stop();
        Store.Dispose();
        _provider.Dispose();
        TempDirectories.Delete(Root);
    }
}

// Feed text for tests: an RSS channel with the itunes and podcast namespaces, and items.
internal static class PodcastFeeds
{
    public static string Rss(string title, string items, string channelExtra = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:podcast="https://podcastindex.org/namespace/1.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>{title}</title>
            <link>https://example.com/show</link>
            <description>A show about tests.</description>
            <itunes:author>Test Author</itunes:author>
            <itunes:image href="https://example.com/art.jpg"/>
            <itunes:category text="Technology"/>
            {channelExtra}
            {items}
          </channel>
        </rss>
        """;

    public static string Item(string guid, string title, DateTimeOffset published, string enclosureUrl, string extra = "", string duration = "600") => $"""
        <item>
          <guid isPermaLink="false">{guid}</guid>
          <title>{title}</title>
          <description>About {title}.</description>
          <pubDate>{published.ToUniversalTime():R}</pubDate>
          <itunes:duration>{duration}</itunes:duration>
          <enclosure url="{enclosureUrl}" length="1000" type="audio/mpeg"/>
          {extra}
        </item>
        """;
}
