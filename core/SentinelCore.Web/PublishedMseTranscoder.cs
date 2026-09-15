using System.Diagnostics;
using System.Text.Json;

namespace SentinelCore.Web;

// Gives a browser-published camera a second go2rtc stream that an MSE client can actually decode.
//
// The problem this exists for is not corruption in the general sense - it is parameter sets. A
// browser's WebRTC encoder emits SPS/PPS in band, and what comes back out of go2rtc's loopback
// RTSP repackaging references picture parameter sets the decoder was never given:
//
//     [h264] non-existing PPS 27 referenced
//     [h264] no frame!
//
// A Reolink pulled by go2rtc over RTSP decodes the same way with zero errors, which is what makes
// this a property of the publish path and not of the sidecar. The consequence on a client is
// worse than a clean failure. MSE hands the fMP4 init segment's avcC to the decoder as its format
// description; when the slices reference a PPS that is not in it, VideoToolbox decodes nothing at
// all, while the transport happily reports media delivered. So the app logs
//
//     Live tile for living room is playing over mse, 2241ms to first frame
//
// and shows a black rectangle forever - it never fails, so it never falls back to the HLS rung
// that HlsStreamManager's transcode already fixed. Measured on the real macOS app: four Reolink
// tiles playing, the published camera black, and no further telemetry for ninety seconds.
//
// The fix is to decode and re-encode, exactly as the HLS path does, because only a decode/encode
// round trip resynchronises parameter sets that are wrong upstream of us. This class runs that
// encode once per published camera and pushes the result into go2rtc as "{id:N}-mse", which
// ConnectMseAsync then serves instead of the raw publisher stream. Every MSE client is fixed at
// once by this - iPhone, Mac and Apple TV - with no app build, which is the whole reason it lives
// on the server rather than in LiveStreamPlayer.
//
// Why a pushed stream rather than asking go2rtc to transcode for us: its own "ffmpeg:" source
// template copies video when the input is already H.264 (verified - "#video=h264", "#width" and
// "#scale" all still produced the undecodable stream), "#raw=" is rejected by its API, and
// "exec:" sources are refused outright ("source from insecure producer"). Declaring the stream
// and pushing into it is the same shape the publishing browser itself uses, and it leaves the
// encoder's arguments under our control.
public sealed class PublishedMseTranscoder(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PublishedMseTranscoder> logger) : IAsyncDisposable
{
    // Two sweeps, not one, before an encode is stopped. A tile that is being dragged around the
    // grid, or an app moving between its grid and a fullscreen view, drops its consumer and takes
    // a new one immediately; stopping on the first idle sweep would spend more time starting and
    // stopping ffmpeg than encoding.
    public const int IdleSweepsBeforeStop = 2;

    private readonly string _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
    private readonly int _apiPort = configuration.GetValue("Go2Rtc:ApiPort", 11984);
    private readonly int _rtspPort = configuration.GetValue("Go2Rtc:RtspPort", Go2RtcManager.DefaultRtspPort);

    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private sealed class Session
    {
        public required Process Process { get; init; }
        public required CancellationTokenSource Supervision { get; init; }
        public required Task Supervisor { get; set; }
        public int ConsecutiveIdleSweeps { get; set; }
    }

    // The go2rtc stream name an MSE viewer of this camera should be pointed at. The suffix follows
    // the "-sub" convention Go2RtcManager.StreamName already uses, so a name in a go2rtc log line
    // still says which camera and which rendition it belongs to.
    public static string StreamNameFor(Guid cameraId) => $"{cameraId:N}-mse";

    // Whether a stream that has been idle this many sweeps in a row should be torn down. Pulled
    // out so the rule can be tested without a process or a sidecar.
    public static bool ShouldStop(int consecutiveIdleSweeps) => consecutiveIdleSweeps >= IdleSweepsBeforeStop;

    // The encoder. Deliberately the same shape as HlsStreamManager.BuildFfmpegArguments' transcode
    // branch, and for the same reasons:
    //
    //  - -g 30 -keyint_min 30 -sc_threshold 0 gives a closed one-second GOP. The publisher's own
    //    keyframes are ~30s apart, so without this a viewer joining mid-stream waits that long for
    //    a picture even once the parameter sets are right.
    //  - -bf 0 and -tune zerolatency keep the encoder from reordering, which is what an MSE client
    //    wants: it is fed fragment by fragment and has no buffer to reorder into.
    //  - audio is re-encoded to AAC rather than passed through. The publisher's track is Opus and
    //    an iOS MSE handshake offers only mp4a.40.2, so Opus reaches it as silence at best. This
    //    is the one place it can be fixed for every Apple client at once.
    //
    // There is no -skip_frame here, and there must not be: it is a decoder option, and on a path
    // whose output is an encode it would feed the encoder keyframes only - about 1fps of video.
    // HlsStreamManager carries the same warning next to the same trap.
    public static List<string> BuildFfmpegArguments(string inputUrl, string outputUrl, bool includeAudio)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-fflags", "+genpts",
            "-rtsp_transport", "tcp",
            "-i", inputUrl,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-profile:v", "high",
            "-pix_fmt", "yuv420p",
            "-g", "30", "-keyint_min", "30", "-sc_threshold", "0",
            "-bf", "0",
            "-crf", "23", "-maxrate", "2M", "-bufsize", "4M",
        };

        if (includeAudio)
            args.AddRange(["-c:a", "aac", "-ar", "48000", "-ac", "2"]);
        else
            args.Add("-an");

        args.AddRange(["-f", "rtsp", "-rtsp_transport", "tcp", outputUrl]);
        return args;
    }

    // Make sure this camera's decodable stream exists and is being fed, and give back the name to
    // connect to. Returns null if the encode could not be started, so the caller can fall back to
    // the raw stream rather than serving nothing - a black tile is bad, but no tile is worse, and
    // a client that gets the raw stream is no worse off than it was before this class existed.
    public async Task<string?> EnsureRunningAsync(Camera camera, CancellationToken cancellationToken)
    {
        if (_disposed) return null;

        var name = StreamNameFor(camera.Id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(camera.Id, out var existing))
            {
                existing.ConsecutiveIdleSweeps = 0;
                if (!existing.Process.HasExited) return name;
                await StopLockedAsync(camera.Id);
            }

            if (!await DeclareStreamAsync(name, cancellationToken))
                return null;

            var session = StartLocked(camera, name);
            return session is null ? null : name;
        }
        finally
        {
            _gate.Release();
        }
    }

    // go2rtc will not create a stream from an RTSP push to an unknown name - the push is simply
    // refused and the stream never appears - so it has to be declared first. This is the same
    // permanently-dead placeholder source Go2RtcManager uses to bring a publishable stream into
    // existence, and for the same reason: the media arrives by being pushed, so there is nothing
    // to pull, and go2rtc has no way to declare an empty stream.
    private async Task<bool> DeclareStreamAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(Go2RtcManager));
            var source = Uri.EscapeDataString("rtsp://127.0.0.1:1/awaiting-publisher");
            using var response = await client.PutAsync(
                $"http://127.0.0.1:{_apiPort}/api/streams?name={name}&src={source}", content: null, cancellationToken);
            if (response.IsSuccessStatusCode) return true;

            logger.LogWarning("go2rtc refused the MSE stream declaration for {Stream} ({Status})", name, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not declare the MSE stream {Stream}", name);
            return false;
        }
    }

    private Session? StartLocked(Camera camera, string name)
    {
        var input = $"rtsp://127.0.0.1:{_rtspPort}/{camera.Id:N}";
        var output = $"rtsp://127.0.0.1:{_rtspPort}/{name}";
        var arguments = BuildFfmpegArguments(input, output, camera.IncludeAudio);

        var info = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            var process = Process.Start(info);
            if (process is null)
            {
                logger.LogWarning("Could not start the MSE transcode for {Camera}", camera.Name);
                return null;
            }

            var supervision = new CancellationTokenSource();
            var session = new Session { Process = process, Supervision = supervision, Supervisor = Task.CompletedTask };
            session.Supervisor = SuperviseAsync(camera, session, supervision.Token);
            _sessions[camera.Id] = session;

            logger.LogInformation(
                "Transcoding {Camera} into {Stream} for MSE viewers (pid {Pid}) - the published stream's parameter sets cannot be decoded as-is",
                camera.Name, name, process.Id);
            return session;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "Could not start the MSE transcode for {Camera}", camera.Name);
            return null;
        }
    }

    // ffmpeg's stderr is drained rather than ignored: a full pipe buffer would wedge the encoder,
    // and its complaints about the input ("non-existing PPS") are exactly the damage this process
    // exists to absorb, so they are logged at debug and not treated as failures.
    private async Task SuperviseAsync(Camera camera, Session session, CancellationToken cancellationToken)
    {
        try
        {
            var stderr = DrainAsync(session.Process.StandardError, camera, cancellationToken);
            var stdout = DrainAsync(session.Process.StandardOutput, camera, cancellationToken);
            await session.Process.WaitForExitAsync(cancellationToken);
            await Task.WhenAny(Task.WhenAll(stderr, stdout), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

            if (!cancellationToken.IsCancellationRequested)
                logger.LogWarning(
                    "The MSE transcode for {Camera} exited with code {Code}; the next viewer will start a new one",
                    camera.Name, session.Process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The MSE transcode supervisor for {Camera} failed", camera.Name);
        }
    }

    private async Task DrainAsync(StreamReader reader, Camera camera, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                if (line.Length > 0)
                    logger.LogDebug("MSE transcode {Camera}: {Line}", camera.Name, line);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The process went away; WaitForExitAsync is what reports that.
        }
    }

    // Stop encoding for cameras nobody is watching. An encode is ~20% of a core, which is cheap
    // enough to run for a viewer and far too expensive to run for an empty room, and unlike the
    // HLS sessions there is no natural request to hang an idle timer off - an MSE viewer holds one
    // WebSocket open for as long as it watches and makes no further requests. So idleness is asked
    // of go2rtc, which knows exactly who is attached to the stream.
    public async Task SweepIdleAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (cameraId, session) in _sessions.ToList())
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (session.Process.HasExited)
                {
                    await StopLockedAsync(cameraId);
                    continue;
                }

                var consumers = await CountConsumersAsync(StreamNameFor(cameraId), cancellationToken);

                // A sidecar that cannot be asked is not evidence of idleness - leave the encode be
                // and look again next sweep.
                if (consumers is null) continue;

                session.ConsecutiveIdleSweeps = consumers.Value > 0 ? 0 : session.ConsecutiveIdleSweeps + 1;
                if (ShouldStop(session.ConsecutiveIdleSweeps))
                {
                    logger.LogInformation("No MSE viewers left on {Stream}; stopping its transcode", StreamNameFor(cameraId));
                    await StopLockedAsync(cameraId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int?> CountConsumersAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(Go2RtcManager));
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{_apiPort}/api/streams?src={name}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return CountConsumers(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // Our own pushing ffmpeg shows up on this stream as a producer, never a consumer, so every
    // consumer here is a real viewer.
    public static int CountConsumers(JsonElement stream) =>
        stream.TryGetProperty("consumers", out var consumers) && consumers.ValueKind == JsonValueKind.Array
            ? consumers.GetArrayLength()
            : 0;

    // Called when a camera is edited or deleted, alongside Go2RtcManager.ForgetStream: the encode
    // is reading a URL that may no longer mean the same thing, and the next viewer should start a
    // fresh one rather than inherit it.
    public async Task StopAsync(Guid cameraId)
    {
        await _gate.WaitAsync();
        try
        {
            await StopLockedAsync(cameraId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopLockedAsync(Guid cameraId)
    {
        if (!_sessions.Remove(cameraId, out var session)) return;

        try
        {
            await session.Supervision.CancelAsync();
            if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
            await session.Process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or NotSupportedException)
        {
            // Already gone, which is the outcome this wanted.
        }
        finally
        {
            try { await session.Supervisor; } catch { /* reported by the supervisor itself */ }
            session.Process.Dispose();
            session.Supervision.Dispose();
        }

        await DeleteStreamAsync(StreamNameFor(cameraId));
    }

    private async Task DeleteStreamAsync(string name)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(Go2RtcManager));
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{_apiPort}/api/streams?src={name}");
            using var response = await client.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A stream left declared with a dead placeholder source costs nothing and is
            // overwritten by the next declaration.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _gate.WaitAsync();
        try
        {
            foreach (var cameraId in _sessions.Keys.ToList())
                await StopLockedAsync(cameraId);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

// The loop that drives SweepIdleAsync. Separate from PublisherWatchdog because it answers a
// different question on a different clock - that one asks whether video is arriving, this one asks
// whether anyone is watching - and folding them together would tie an encode's lifetime to an
// alerting policy.
public sealed class PublishedMseTranscodeSweeper(
    PublishedMseTranscoder transcoder,
    ILogger<PublishedMseTranscodeSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
                await transcoder.SweepIdleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The MSE transcode sweep failed a pass");
            }
        }
    }
}
