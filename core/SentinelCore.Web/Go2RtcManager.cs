using System.Diagnostics;
using System.Text;

namespace SentinelCore.Web;

// Thrown when WebRTC simply isn't available on this install - go2rtc not installed, or its
// process won't start. The dashboard treats the resulting 503 as "use HLS instead", so this is
// an expected condition, not an error worth a stack trace.
public sealed class Go2RtcUnavailableException(string message) : Exception(message);

// Runs go2rtc as a supervised sidecar, the same way HlsStreamManager supervises ffmpeg. go2rtc
// is what turns the camera's RTSP into WebRTC for the dashboard's live grid: WebRTC pushes
// frames continuously instead of in keyframe-aligned segments, so the live view lands within a
// few hundred milliseconds of the camera instead of the several seconds HLS structurally
// requires (an HLS player can only download a segment that has finished being written, and a
// copied stream can only cut segments on keyframes - the camera's GOP is a hard latency floor).
//
// HLS stays fully in place: it's the fallback when go2rtc isn't installed, when the browser's
// WebRTC handshake can't complete (e.g. through a tunnel that only forwards HTTP), and the only
// path for the AVPlayer/ExoPlayer apps. Everything here therefore degrades softly - a missing
// go2rtc binary means Go2RtcUnavailableException, which the endpoint maps to 503 and the
// dashboard answers by attaching hls.js exactly as before this class existed.
//
// Security shape: go2rtc's own HTTP API is bound to loopback only and is never exposed - the
// dashboard talks to /api/cameras/{id}/webrtc on this server, which authenticates the request
// and forwards just the SDP exchange. Only the WebRTC media port (which speaks DTLS-SRTP,
// encrypted and useless without the keys from a signaled session) listens on the LAN.
public sealed class Go2RtcManager : IDisposable
{
    // How long to wait for a freshly-started go2rtc to answer its API before giving up. It's a
    // single static binary that binds a few ports on startup - normally ready in well under a
    // second, so this is generous.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    // After a failed start, how long to stop trying. Every live tile probes WebRTC once per page
    // load, so without this a dashboard with six cameras would attempt six doomed process spawns
    // per visit.
    //
    // Split in two, because the two failures have nothing in common but their symptom. A binary
    // that isn't there stays not-there until somebody installs it, so backing off for minutes
    // costs nothing. A sidecar that started and then died, or never answered, is usually the
    // previous run's process still holding the ports across a restart - which clears in seconds.
    // Both used to take the five-minute penalty, so a restart could park every live tile on HLS
    // for five minutes over a port that was free again after two, and that is exactly the window
    // in which somebody opens the app to check what just happened.
    private static readonly TimeSpan MissingBinaryRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TransientFailureRetry = TimeSpan.FromSeconds(20);

    // Shared with CameraSource, which builds the loopback RTSP URL that browser-published cameras
    // are read back from and so must agree with the port the sidecar actually listens on.
    public const int DefaultRtspPort = 18554;

    private readonly string _binaryPath;
    private readonly string _ffmpegPath;
    private readonly int _apiPort;
    private readonly int _rtspPort;
    private readonly int _webRtcPort;
    private readonly string _workingRoot;
    private readonly ILogger<Go2RtcManager> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    // Which source string each camera is currently registered under in the running go2rtc, so a
    // camera edit re-registers but an unchanged camera doesn't - re-PUTting the same stream
    // recreates it inside go2rtc and would drop every viewer currently connected to it.
    private readonly Dictionary<(Guid Camera, StreamQuality Quality), string> _registeredSources = new();

    private Process? _process;
    private DateTimeOffset _unavailableUntil = DateTimeOffset.MinValue;
    private readonly object _logLock = new();
    private readonly Queue<string> _logLines = new();

    // Bounds the sidecar's startup independently of whichever viewer's request happened to
    // trigger it - see EnsureStartedAsync. Cancelled (deliberately not disposed, so that a late
    // caller gets a cancelled token rather than ObjectDisposedException) when this manager is.
    private readonly CancellationTokenSource _shutdown = new();

    public Go2RtcManager(IConfiguration configuration, IHostEnvironment environment,
        IHttpClientFactory httpClientFactory, ILogger<Go2RtcManager> logger)
    {
        _binaryPath = configuration["Go2Rtc:Path"] ?? "go2rtc";
        _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
        // Deliberately not go2rtc's stock ports (1984/8554/8555): anyone already running another
        // go2rtc on this box - Frigate bundles one - would otherwise have our sidecar fail to
        // bind and die on every start attempt.
        _apiPort = configuration.GetValue("Go2Rtc:ApiPort", 11984);
        _rtspPort = configuration.GetValue("Go2Rtc:RtspPort", DefaultRtspPort);
        _webRtcPort = configuration.GetValue("Go2Rtc:WebRtcPort", 18555);

        var configured = configuration["CamerasDataDirectory"] ?? "data";
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _workingRoot = Path.Combine(root, "go2rtc");
        Directory.CreateDirectory(_workingRoot);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Bring the sidecar up before anyone asks for a stream, so the first viewer after a restart
    // doesn't pay for it. That bill used to land squarely on them: the dashboard gives an entire
    // WebRTC attempt six seconds (WEBRTC_CONNECT_TIMEOUT_MS in cameras.js) while a cold start here
    // is allowed ten, so the browser could give up on a sidecar that was still coming up, twice,
    // and then latch itself onto HLS for the rest of the tab. Whoever opened the app first after a
    // restart got the stuttering feed - which is precisely when someone opens it.
    //
    // Deliberately starts the process only, and registers no streams: go2rtc dials a camera when a
    // viewer actually asks for it, and pre-registering would hold connections open against cameras
    // nobody is watching. On a Reolink, whose concurrent-connection budget is small enough that the
    // detection pipeline already competes with the live view, that would trade one problem for a
    // worse one.
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
    }

    // Make this camera readable from the sidecar's loopback RTSP server, for a consumer that isn't a
    // browser - currently ScheduledRecorder, which then pulls the address CameraSource builds.
    //
    // Going through go2rtc rather than dialling the camera directly is the point of it. A scheduled
    // recording is a second consumer, and on a Reolink the concurrent-connection budget is tight
    // enough that the detection pipeline already competes with the live view - a direct pull would
    // spend another connection unconditionally. Registered here, the recorder shares whatever
    // upstream connection go2rtc already holds for anyone watching the same camera, and only causes
    // a dial of its own when nobody is.
    //
    // A browser-published camera is deliberately NOT registered: it is already registered, by the
    // browser that is publishing into it (ExchangePublishOfferAsync). Registering it again would at
    // best be a no-op against the cached source key and at worst re-PUT the stream, which recreates
    // it inside go2rtc - dropping the publisher, and with it the very feed being recorded.
    public async Task EnsureRecordableAsync(Camera camera, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        if (camera.IsBrowserPublished) return;

        await RegisterStreamAsync(camera, cancellationToken);
    }

    // The whole signaling exchange for one viewer: make sure the sidecar is up and this camera
    // is registered with it, hand go2rtc the browser's SDP offer, return go2rtc's answer. The
    // answer contains go2rtc's ICE candidates (it gathers before responding), after which media
    // flows browser<->go2rtc directly and this server is out of the path.
    public async Task<string> ExchangeOfferAsync(
        Camera camera, string sdpOffer, StreamQuality quality, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        var effective = EffectiveQuality(camera, quality);
        await RegisterStreamAsync(camera, effective, cancellationToken);

        var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
        using var content = new StringContent(sdpOffer, Encoding.UTF8, "application/sdp");
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{_apiPort}/api/webrtc?src={StreamName(camera.Id, effective)}", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"go2rtc rejected the WebRTC offer ({(int)response.StatusCode}): {body}");

        return body;
    }

    // The mirror image of ExchangeOfferAsync: instead of a viewer asking for this camera's video,
    // a browser is offering to *be* this camera. Same SDP exchange, but go2rtc is told ?dst= rather
    // than ?src=, so it attaches the browser as a producer of the stream instead of a consumer.
    //
    // Once this returns, the publisher's frames land in the sidecar and everything else reads them
    // back over loopback RTSP (see CameraSource) - live view, clips, trigger zones and analysis all
    // treat the camera as ordinary from here on.
    public async Task<string> ExchangePublishOfferAsync(Camera camera, string sdpOffer, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        await RegisterStreamAsync(camera, cancellationToken);

        var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
        using var content = new StringContent(sdpOffer, Encoding.UTF8, "application/sdp");
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{_apiPort}/api/webrtc?dst={StreamName(camera.Id)}", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"go2rtc rejected the publish offer ({(int)response.StatusCode}): {body}");

        return body;
    }

    // A single JPEG straight from the sidecar, used for browser-published cameras instead of
    // pointing ffmpeg at them.
    //
    // The reason is keyframes. A browser's WebRTC encoder emits one only every ~30 seconds, and
    // nothing can decode video until it does - so an ffmpeg snapshot against a published camera
    // waits that long. go2rtc asks the publisher for a keyframe when it needs one itself, which
    // makes this path a second or two instead. (Asking for a frame this way does not help a
    // separate RTSP consumer: go2rtc serves this from its own consumer, and the keyframe it
    // requests doesn't reach the others. Measured, not assumed.)
    public async Task<byte[]?> CaptureFrameAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{_apiPort}/api/frame.jpeg?src={StreamName(cameraId)}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Nothing is publishing, or the sidecar is busy - the caller reports "no frame" the
            // same way it would for an unreachable camera.
            return null;
        }
    }

    // Whether a browser is currently pushing video into this camera's stream, used to tell a
    // publisher that dropped off from one that never started - see CameraPublishEndpoints and
    // PublisherWatchdog. Asks go2rtc rather than tracking it here: the publisher's connection is
    // between the browser and the sidecar, and this process never sees it end.
    public async Task<bool> HasLivePublisherAsync(Guid cameraId, CancellationToken cancellationToken)
    {
        if (_process is not { HasExited: false }) return false;

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{_apiPort}/api/streams?src={StreamName(cameraId)}", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return HasWebRtcProducer(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // The sidecar being unreachable is not evidence the publisher is gone, but it is also
            // not evidence it is alive - and the watchdog's job is to notice absence of video.
            return false;
        }
    }

    // A stream go2rtc knows about lists every attached producer; the placeholder source shows up
    // as one with no format, so only a producer that actually negotiated WebRTC counts as a live
    // publisher.
    private static bool HasWebRtcProducer(System.Text.Json.JsonElement stream)
    {
        if (!stream.TryGetProperty("producers", out var producers) ||
            producers.ValueKind != System.Text.Json.JsonValueKind.Array)
            return false;

        foreach (var producer in producers.EnumerateArray())
        {
            if (producer.TryGetProperty("format_name", out var format) &&
                format.ValueKind == System.Text.Json.JsonValueKind.String &&
                format.GetString() == "webrtc")
                return true;
        }

        return false;
    }

    // The MSE counterpart of ExchangeOfferAsync: make sure the sidecar is up and the camera
    // registered, then open a WebSocket to go2rtc's MSE endpoint for this stream. The caller
    // (CameraEndpoints) pumps frames between this socket and the browser's; unlike WebRTC the
    // media itself flows through this server, which is exactly the point - it rides the same
    // origin and port as the dashboard, so it works wherever the dashboard itself is reachable
    // (VPNs, tunnels, proxies) where WebRTC's separate media port often isn't.
    public async Task<System.Net.WebSockets.ClientWebSocket> ConnectMseAsync(
        Camera camera, StreamQuality quality, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        var effective = EffectiveQuality(camera, quality);
        await RegisterStreamAsync(camera, effective, cancellationToken);

        var socket = new System.Net.WebSockets.ClientWebSocket();
        try
        {
            await socket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{_apiPort}/api/ws?src={StreamName(camera.Id, effective)}"), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Called alongside HlsStreamManager.StopStream when a camera is edited or deleted, so the
    // next viewer registers the new URL instead of streaming from the old one.
    public void ForgetStream(Guid cameraId)
    {
        List<StreamQuality> dropped;
        lock (_registeredSources)
        {
            // Both renditions, not just Main: a camera edit that changes the URL invalidates the
            // low-rendition registration derived from it just as much, and leaving that one behind
            // meant every tile kept streaming the old address while the full-screen view moved on.
            dropped = _registeredSources.Keys
                .Where(key => key.Camera == cameraId)
                .Select(key => key.Quality)
                .ToList();
            foreach (var quality in dropped) _registeredSources.Remove((cameraId, quality));
        }

        if (dropped.Count == 0) return;

        // Best effort: if the sidecar isn't running there's nothing to unregister anyway.
        var process = _process;
        if (process is null || process.HasExited) return;
        foreach (var quality in dropped) _ = DeleteStreamAsync(cameraId, quality);
    }

    private async Task DeleteStreamAsync(Guid cameraId, StreamQuality quality)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
            using var response = await client.DeleteAsync(
                $"http://127.0.0.1:{_apiPort}/api/streams?src={StreamName(cameraId, quality)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The stale registration is already forgotten locally; a re-register overwrites it.
        }
    }

    private static string StreamName(Guid cameraId) => StreamName(cameraId, StreamQuality.Main);

    // go2rtc knows a camera's two renditions as two streams, so they need two names. The suffix
    // matches the one HlsStreamManager gives its low-rendition session directory, so a name seen
    // in a go2rtc log line and one seen in a stream path mean the same thing.
    private static string StreamName(Guid cameraId, StreamQuality quality) =>
        quality == StreamQuality.Main ? cameraId.ToString("N") : $"{cameraId:N}-sub";

    // Whether asking for Sub can actually be honoured. A camera whose URL yields no low-res
    // companion is served Main - and, importantly, is served it under the *Main* stream name
    // rather than a second stream pointing at the same address. Two go2rtc streams on one URL
    // means two connections to the camera, and on these Reolinks the concurrent-connection budget
    // is already tight enough that the detection pipeline competes with the live view.
    private static StreamQuality EffectiveQuality(Camera camera, StreamQuality requested)
    {
        if (requested == StreamQuality.Main) return StreamQuality.Main;
        if (camera.IsBrowserPublished) return StreamQuality.Main;
        return CameraSource.TryDeriveLowResUrl(HlsStreamManager.BuildAuthenticatedUrl(camera)) is null
            ? StreamQuality.Main
            : StreamQuality.Sub;
    }

    private Task RegisterStreamAsync(Camera camera, CancellationToken cancellationToken) =>
        RegisterStreamAsync(camera, StreamQuality.Main, cancellationToken);

    private async Task RegisterStreamAsync(Camera camera, StreamQuality quality, CancellationToken cancellationToken)
    {
        var sources = BuildStreamSources(camera, quality);
        var sourceKey = string.Join('\n', sources);
        lock (_registeredSources)
        {
            if (_registeredSources.TryGetValue((camera.Id, quality), out var existing) && existing == sourceKey)
                return;
        }

        var query = new StringBuilder($"name={StreamName(camera.Id, quality)}");
        foreach (var source in sources)
            query.Append("&src=").Append(Uri.EscapeDataString(source));

        var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
        using var response = await client.PutAsync(
            $"http://127.0.0.1:{_apiPort}/api/streams?{query}", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"go2rtc refused the stream registration ({(int)response.StatusCode}): {body}");
        }

        lock (_registeredSources)
        {
            _registeredSources[(camera.Id, quality)] = sourceKey;
        }
    }

    // What go2rtc is told to pull for this camera. The camera's own stream is passed through
    // untouched - go2rtc, like our HLS pipeline, never re-encodes video.
    //
    // Audio is the one place WebRTC is pickier than HLS: browsers accept Opus and the G.711
    // variants but not the AAC most cameras produce. The second source handles that - it
    // references the first by name (so credentials aren't embedded twice) and has go2rtc run an
    // audio-only Opus transcode, which it only actually starts if a viewer's offer wants audio
    // the direct source can't satisfy. Video in that source is explicitly absent: it comes from
    // the direct source, matched up by go2rtc.
    public static List<string> BuildStreamSources(Camera camera) =>
        BuildStreamSources(camera, StreamQuality.Main);

    public static List<string> BuildStreamSources(Camera camera, StreamQuality quality)
    {
        if (camera.IsBrowserPublished) return [PublishPlaceholderSource];

        var mainUrl = HlsStreamManager.BuildAuthenticatedUrl(camera);

        // The low rendition is video for a tile a few hundred points wide, and nothing else. No
        // Opus companion: the transcode exists for browsers that can't take the camera's AAC over
        // WebRTC, and a grid tile is muted, so registering it here would put an ffmpeg process
        // behind every tile for audio nobody is listening to.
        if (quality == StreamQuality.Sub)
            return [CameraSource.TryDeriveLowResUrl(mainUrl) ?? mainUrl];

        var sources = new List<string> { mainUrl };
        if (camera.IncludeAudio)
            sources.Add($"ffmpeg:{StreamName(camera.Id)}#audio=opus");
        return sources;
    }

    // A browser-published camera's stream has to exist in go2rtc before a browser can publish
    // into it - a POST to ?dst= for an unknown stream is a 404 - but there is nothing to pull,
    // because the media arrives by being pushed. go2rtc has no "empty stream" the API can create:
    // PUT /api/streams with no src, or with an empty one, returns 200 and creates nothing, and a
    // stream naming its own RTSP URL would be self-referential. So the stream is declared with a
    // source that is syntactically valid and permanently dead. It exists to make the stream exist.
    //
    // Nothing ever dials it: go2rtc only starts a source when a consumer needs one and there is no
    // live producer, and for these cameras the publishing browser is always the producer. Port 1
    // is reserved (tcpmux) and nothing on this box listens there, so even an attempt fails at once
    // rather than hanging.
    private const string PublishPlaceholderSource = "rtsp://127.0.0.1:1/awaiting-publisher";

    // The config go2rtc runs with. YAML, but assembled by hand - it's a dozen fixed lines and
    // taking a YAML dependency for it would be the tail wagging the dog.
    public static string BuildConfigYaml(int apiPort, int rtspPort, int webRtcPort, string ffmpegPath)
    {
        var sb = new StringBuilder();
        // API on loopback only: signaling goes through this server's authenticated endpoint,
        // never straight to go2rtc, whose API has no auth of its own.
        sb.AppendLine("api:");
        sb.AppendLine($"  listen: \"127.0.0.1:{apiPort}\"");
        // The internal RTSP server also stays on loopback - it exists only as the path ffmpeg
        // uses to feed transcoded audio back in (the ffmpeg: source above publishes to it).
        sb.AppendLine("rtsp:");
        sb.AppendLine($"  listen: \"127.0.0.1:{rtspPort}\"");
        // The one listener that must be reachable from the LAN: the media port browsers connect
        // to after signaling. Carries DTLS-SRTP only.
        sb.AppendLine("webrtc:");
        sb.AppendLine($"  listen: \":{webRtcPort}\"");
        sb.AppendLine("ffmpeg:");
        sb.AppendLine($"  bin: \"{ffmpegPath}\"");
        // go2rtc logs to stdout; keep it terse, the tail is only read when something breaks.
        sb.AppendLine("log:");
        sb.AppendLine("  level: warn");
        return sb.ToString();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        // Fast path: already running.
        var running = _process;
        if (running is { HasExited: false }) return;

        // Queuing behind someone else's start still honours the caller's token - a viewer who
        // gives up while waiting costs nothing. Past this point it deliberately does not: see
        // startupToken below.
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            running = _process;
            if (running is { HasExited: false }) return;

            // The sidecar is a process-wide singleton serving every live tile, so one viewer
            // closing a tab must not abort a start the others are waiting on - and, worse, must
            // not abandon a go2rtc that is already running but not yet recorded in _process.
            // That orphan would hold the API, RTSP and WebRTC ports, so every later start attempt
            // would fail to bind and WebRTC would stay dead behind a 5-minute backoff until
            // someone killed the stray process by hand. StartupTimeout below is what bounds this
            // instead; _shutdown is what stops it outliving the app.
            var startupToken = _shutdown.Token;

            // How long the sidecar actually takes to become usable. Worth a number rather than a
            // shrug: the dashboard gives a whole WebRTC attempt six seconds, so a cold start that
            // creeps past that is the difference between a smooth tile and one that falls back to
            // HLS and visibly rebuffers - and there is no way to tell those apart after the fact
            // without this figure in the log.
            var startup = Stopwatch.StartNew();

            if (DateTimeOffset.UtcNow < _unavailableUntil)
                throw new Go2RtcUnavailableException(
                    "WebRTC is off because go2rtc could not be started. Install go2rtc (https://github.com/AlexxIT/go2rtc) " +
                    "and make sure it's on the PATH or set Go2Rtc:Path - live view is using HLS meanwhile.");

            // A previous process died - its stream registrations died with it.
            lock (_registeredSources) _registeredSources.Clear();

            var configPath = Path.Combine(_workingRoot, "go2rtc.yaml");
            await File.WriteAllTextAsync(configPath, BuildConfigYaml(_apiPort, _rtspPort, _webRtcPort, _ffmpegPath), startupToken);

            var psi = new ProcessStartInfo
            {
                FileName = _binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workingRoot
            };
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(configPath);

            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _unavailableUntil = DateTimeOffset.UtcNow + MissingBinaryRetry;
                _logger.LogInformation(
                    "go2rtc is not installed (looked for '{Path}') - live view will use HLS. Install go2rtc for lower-latency WebRTC.",
                    _binaryPath);
                throw new Go2RtcUnavailableException(
                    $"go2rtc could not be started from '{_binaryPath}' - install it (https://github.com/AlexxIT/go2rtc) " +
                    "or set Go2Rtc:Path to its location. Live view is using HLS meanwhile.");
            }

            // Everything from here to the _process assignment runs under a guard: until that
            // assignment lands, this local is the only reference to a live process, so any way out
            // of this block other than success has to take the process with it. Cancellation,
            // startup timeout, the sidecar exiting on its own, an unexpected throw - all of them
            // previously left go2rtc running with nothing able to reach it, including Dispose.
            try
            {
                _ = DrainAsync(process.StandardOutput);
                _ = DrainAsync(process.StandardError);

                // Wait for the API to answer before declaring the sidecar up - a process that starts
                // and then dies binding a port would otherwise pass here and fail on the first real
                // request with a much less useful error.
                var deadline = DateTimeOffset.UtcNow + StartupTimeout;
                var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
                while (true)
                {
                    if (process.HasExited)
                    {
                        _unavailableUntil = DateTimeOffset.UtcNow + TransientFailureRetry;
                        _logger.LogWarning(
                            "go2rtc exited {ElapsedMs}ms into startup; WebRTC and MSE live view are off for {BackoffSeconds:n0}s "
                            + "and tiles will use HLS meanwhile. Recent log:\n{Log}",
                            startup.ElapsedMilliseconds, TransientFailureRetry.TotalSeconds, RecentLog);
                        throw new Go2RtcUnavailableException(
                            $"go2rtc exited during startup (often a port already in use - it needs {_apiPort}, {_rtspPort} and {_webRtcPort} free, " +
                            $"all configurable under Go2Rtc: in appsettings.json). Recent log:\n{RecentLog}");
                    }

                    try
                    {
                        using var response = await client.GetAsync($"http://127.0.0.1:{_apiPort}/api", startupToken);
                        if (response.IsSuccessStatusCode) break;
                    }
                    catch (HttpRequestException)
                    {
                        // Not listening yet.
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        _unavailableUntil = DateTimeOffset.UtcNow + TransientFailureRetry;
                        _logger.LogWarning(
                            "go2rtc started but its API never answered within {TimeoutSeconds:n0}s; WebRTC and MSE live view are off "
                            + "for {BackoffSeconds:n0}s and tiles will use HLS meanwhile. Recent log:\n{Log}",
                            StartupTimeout.TotalSeconds, TransientFailureRetry.TotalSeconds, RecentLog);
                        throw new Go2RtcUnavailableException($"go2rtc started but its API never came up. Recent log:\n{RecentLog}");
                    }

                    await Task.Delay(200, startupToken);
                }
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                process.Dispose();
                throw;
            }

            _logger.LogInformation(
                "Started go2rtc for WebRTC live view in {ElapsedMs}ms, pid {Pid} (api :{ApiPort}, media :{MediaPort})",
                startup.ElapsedMilliseconds, process.Id, _apiPort, _webRtcPort);
            _process = process;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                lock (_logLock)
                {
                    _logLines.Enqueue(line);
                    while (_logLines.Count > 20) _logLines.Dequeue();
                }
            }
        }
        catch
        {
            // process exited; nothing to do
        }
    }

    private string RecentLog
    {
        get
        {
            lock (_logLock) return string.Join('\n', _logLines);
        }
    }

    public void Dispose()
    {
        // First, so that a start still inside its wait loop unblocks and its guard kills the
        // process it is holding. Without this the sidecar would survive the app that spawned it,
        // and go on holding the ports the next run needs.
        try { _shutdown.Cancel(); } catch { /* best effort */ }

        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            process.Dispose();
        }
    }
}
