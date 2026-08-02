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

    // After a failed start (binary missing, port taken), how long to stop trying. Every live
    // tile probes WebRTC once per page load, so without this a dashboard with six cameras would
    // attempt six doomed process spawns per visit.
    private static readonly TimeSpan UnavailableRetry = TimeSpan.FromMinutes(5);

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
    private readonly Dictionary<Guid, string> _registeredSources = new();

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
        _rtspPort = configuration.GetValue("Go2Rtc:RtspPort", 18554);
        _webRtcPort = configuration.GetValue("Go2Rtc:WebRtcPort", 18555);

        var configured = configuration["CamerasDataDirectory"] ?? "data";
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _workingRoot = Path.Combine(root, "go2rtc");
        Directory.CreateDirectory(_workingRoot);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // The whole signaling exchange for one viewer: make sure the sidecar is up and this camera
    // is registered with it, hand go2rtc the browser's SDP offer, return go2rtc's answer. The
    // answer contains go2rtc's ICE candidates (it gathers before responding), after which media
    // flows browser<->go2rtc directly and this server is out of the path.
    public async Task<string> ExchangeOfferAsync(Camera camera, string sdpOffer, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        await RegisterStreamAsync(camera, cancellationToken);

        var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
        using var content = new StringContent(sdpOffer, Encoding.UTF8, "application/sdp");
        using var response = await client.PostAsync(
            $"http://127.0.0.1:{_apiPort}/api/webrtc?src={StreamName(camera.Id)}", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"go2rtc rejected the WebRTC offer ({(int)response.StatusCode}): {body}");

        return body;
    }

    // Called alongside HlsStreamManager.StopStream when a camera is edited or deleted, so the
    // next viewer registers the new URL instead of streaming from the old one.
    public void ForgetStream(Guid cameraId)
    {
        lock (_registeredSources)
        {
            if (!_registeredSources.Remove(cameraId)) return;
        }

        // Best effort: if the sidecar isn't running there's nothing to unregister anyway.
        var process = _process;
        if (process is null || process.HasExited) return;
        _ = DeleteStreamAsync(cameraId);
    }

    private async Task DeleteStreamAsync(Guid cameraId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(Go2RtcManager));
            using var response = await client.DeleteAsync(
                $"http://127.0.0.1:{_apiPort}/api/streams?src={StreamName(cameraId)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The stale registration is already forgotten locally; a re-register overwrites it.
        }
    }

    private static string StreamName(Guid cameraId) => cameraId.ToString("N");

    private async Task RegisterStreamAsync(Camera camera, CancellationToken cancellationToken)
    {
        var sources = BuildStreamSources(camera);
        var sourceKey = string.Join('\n', sources);
        lock (_registeredSources)
        {
            if (_registeredSources.TryGetValue(camera.Id, out var existing) && existing == sourceKey)
                return;
        }

        var query = new StringBuilder($"name={StreamName(camera.Id)}");
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
            _registeredSources[camera.Id] = sourceKey;
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
    public static List<string> BuildStreamSources(Camera camera)
    {
        var sources = new List<string> { HlsStreamManager.BuildAuthenticatedUrl(camera) };
        if (camera.IncludeAudio)
            sources.Add($"ffmpeg:{StreamName(camera.Id)}#audio=opus");
        return sources;
    }

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
                _unavailableUntil = DateTimeOffset.UtcNow + UnavailableRetry;
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
                        _unavailableUntil = DateTimeOffset.UtcNow + UnavailableRetry;
                        _logger.LogWarning("go2rtc exited during startup. Recent log:\n{Log}", RecentLog);
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
                        _unavailableUntil = DateTimeOffset.UtcNow + UnavailableRetry;
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

            _logger.LogInformation("Started go2rtc for WebRTC live view, pid {Pid} (api :{ApiPort}, media :{MediaPort})",
                process.Id, _apiPort, _webRtcPort);
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
