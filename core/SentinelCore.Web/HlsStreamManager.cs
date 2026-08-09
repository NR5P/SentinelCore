using System.Collections.Concurrent;
using System.Diagnostics;

namespace SentinelCore.Web;

// Starts an ffmpeg process per camera on first request, remuxing whatever RTSP/HTTP source
// the camera speaks into HLS segments a browser can actually play (browsers have no native
// RTSP support). Video is copied, not re-encoded, so this is cheap as long as the source is
// H.264 - true of the overwhelming majority of IP cameras. Streams nobody is watching are
// killed after a short idle period so this doesn't burn CPU on cameras left unwatched.
public sealed class HlsStreamManager : IDisposable
{
    // Was 30s, but that meant switching away from the Live tab for half a minute (or the app
    // backgrounding briefly) killed ffmpeg, so the next visit paid a full cold RTSP-reconnect +
    // keyframe-wait every time. A few minutes covers normal tab-switching/app-backgrounding while
    // still reclaiming genuinely-abandoned cameras.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    // ffmpeg can only start an HLS segment on a keyframe boundary when copying video
    // (not re-encoding), so startup latency depends on the camera's keyframe interval -
    // some cameras/encoders use long GOPs, so this stays generous rather than flaky.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    // Browser-published cameras are the extreme case of that long-GOP problem, and predictably so:
    // a browser's WebRTC encoder emits a keyframe roughly every 30 seconds, and ffmpeg produces
    // nothing at all until one arrives. Measured at ~30s, so the ordinary budget would fail more
    // often than not. Only the first request pays it - the session then stays warm.
    private static readonly TimeSpan PublishedStartupTimeout = TimeSpan.FromSeconds(75);

    private static TimeSpan StartupBudgetFor(Camera camera) =>
        camera.IsBrowserPublished ? PublishedStartupTimeout : StartupTimeout;

    // How long a session has to have survived before its death is treated as worth retrying
    // rather than as a camera that simply isn't working - see GetOrStartLiveSession.
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(10);

    // Keyed by camera *and* rendition: a grid tile watching the low stream and a full-screen
    // viewer watching the main one are two different ffmpeg processes writing two different
    // segment directories, and neither may reuse or evict the other's.
    private readonly record struct SessionKey(Guid CameraId, StreamQuality Quality);

    private readonly ConcurrentDictionary<SessionKey, StreamSession> _sessions = new();

    // Cameras whose stream died on startup, and when it becomes worth trying them again. Keeps a
    // broken camera from having a fresh ffmpeg spawned for it on every request - see
    // GetOrStartLiveSession.
    private readonly ConcurrentDictionary<SessionKey, DateTimeOffset> _restartBlockedUntil = new();

    // Cameras that turned out not to serve the low-resolution stream their URL implied. ffmpeg
    // refuses an input it can't open and quits immediately, so a Sub session that exits without
    // ever writing a playlist is that camera telling us the rendition isn't enabled. Latching it
    // sends every later request for that camera to the main stream instead of crash-looping on the
    // same guess - the same fallback TriggerZoneMonitor keeps for detection, for the same reason.
    private readonly ConcurrentDictionary<Guid, bool> _noLowResStream = new();
    private readonly CameraSource _cameraSource;
    private readonly Go2RtcManager _go2rtc;
    private readonly string _ffmpegPath;
    private readonly string _workingRoot;
    private readonly ILogger<HlsStreamManager> _logger;
    private readonly Timer _cleanupTimer;

    public HlsStreamManager(
        IConfiguration configuration, IHostEnvironment environment, CameraSource cameraSource,
        Go2RtcManager go2rtc, ILogger<HlsStreamManager> logger)
    {
        _cameraSource = cameraSource;
        _go2rtc = go2rtc;
        _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
        var configured = configuration["CamerasDataDirectory"] ?? "data";
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _workingRoot = Path.Combine(root, "hls");
        Directory.CreateDirectory(_workingRoot);
        _logger = logger;
        _cleanupTimer = new Timer(_ => CleanupIdleSessions(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public Task<string> EnsureStreamStartedAsync(Camera camera, CancellationToken cancellationToken) =>
        EnsureStreamStartedAsync(camera, StreamQuality.Main, cancellationToken);

    // Asking for Sub is a preference, not a demand: a camera that doesn't serve one still has to
    // show a picture. So a Sub request that can't be satisfied degrades to the main stream rather
    // than failing, and the camera is remembered so the next viewer doesn't pay the same discovery
    // twice. Main requests keep the original behaviour exactly - they have nothing to fall back to.
    public async Task<string> EnsureStreamStartedAsync(
        Camera camera, StreamQuality quality, CancellationToken cancellationToken)
    {
        var effective = EffectiveQuality(camera, quality);
        try
        {
            return await StartAndWaitAsync(camera, effective, cancellationToken);
        }
        catch (Exception ex) when (effective == StreamQuality.Sub && ex is not OperationCanceledException)
        {
            if (_noLowResStream.TryAdd(camera.Id, true))
                _logger.LogWarning(
                    "Camera {CameraId} ({Name}) never produced a playlist on its derived low-resolution stream, so " +
                    "that stream probably isn't enabled on the camera. Falling back to the main stream for live view. {Reason}",
                    camera.Id, camera.Name, ex.Message);

            // The failed attempt left a backoff behind for the rendition we're abandoning; clearing
            // it keeps that from blocking the retry we're about to make on a different key.
            _restartBlockedUntil.TryRemove(new SessionKey(camera.Id, StreamQuality.Sub), out _);
            return await StartAndWaitAsync(camera, StreamQuality.Main, cancellationToken);
        }
    }

    // Which rendition this camera can actually serve for the one that was asked for. Resolved
    // before a session key is built, so a camera with no low stream runs one process rather than
    // two identical ones - warming Sub and Main separately would double both the ffmpeg count and
    // the connections against a Reolink's small budget, which is the opposite of the point.
    private StreamQuality EffectiveQuality(Camera camera, StreamQuality requested)
    {
        if (requested == StreamQuality.Main) return StreamQuality.Main;
        if (camera.IsBrowserPublished) return StreamQuality.Main;
        if (_noLowResStream.ContainsKey(camera.Id)) return StreamQuality.Main;
        return CameraSource.TryDeriveLowResUrl(HlsStreamManager.BuildAuthenticatedUrl(camera)) is null
            ? StreamQuality.Main
            : StreamQuality.Sub;
    }

    private async Task<string> StartAndWaitAsync(
        Camera camera, StreamQuality quality, CancellationToken cancellationToken)
    {
        var key = new SessionKey(camera.Id, quality);
        var session = GetOrStartLiveSession(camera, key);

        var playlistPath = Path.Combine(session.OutputDirectory, "stream.m3u8");
        var deadline = DateTimeOffset.UtcNow + StartupBudgetFor(camera);
        while (!File.Exists(playlistPath) && DateTimeOffset.UtcNow < deadline)
        {
            if (session.Process.HasExited)
            {
                _sessions.TryRemove(key, out _);
                throw new InvalidOperationException(
                    $"ffmpeg exited before producing a stream - check the camera URL/credentials. Recent log:\n{session.RecentLog}");
            }

            await Task.Delay(200, cancellationToken);
        }

        if (!File.Exists(playlistPath))
        {
            _sessions.TryRemove(key, out _);
            KillSession(session);
            throw new TimeoutException($"Timed out waiting for the stream to start. Recent log:\n{session.RecentLog}");
        }

        return playlistPath;
    }

    // A session whose ffmpeg has died is worse than having no session at all: its playlist and
    // segments stay on disk, so EnsureStreamStartedAsync's "wait for the playlist to appear"
    // loop is skipped entirely and every later request is answered 200 with a stream frozen at
    // the instant ffmpeg died. Nothing recovers from that on its own either - each request
    // refreshes LastAccessed, so the idle sweep never reclaims it - which makes a camera that
    // drops out once (a reboot, a network blip, an RTSP connection limit reached because
    // something else grabbed a snapshot) stay broken on every client until the server restarts.
    //
    // So the liveness check belongs here, before the playlist is trusted, rather than only
    // inside that loop. A dead session is dropped and replaced with a fresh one; if the camera
    // really is unreachable the new process exits too and the caller gets a real error with
    // ffmpeg's log, which is the outcome that tells the owner something is wrong.
    private StreamSession GetOrStartLiveSession(Camera camera, SessionKey key)
    {
        // Reap a session whose ffmpeg has gone. Compare-and-remove, so that when two viewers
        // arrive together only the one that actually takes this session out of the dictionary
        // disposes it - neither can double-kill a process or tear down the other's replacement.
        if (_sessions.TryGetValue(key, out var existing) && existing.Process.HasExited)
        {
            var log = existing.RecentLog;
            var ranFor = DateTimeOffset.UtcNow - existing.StartedAt;
            if (_sessions.TryRemove(new KeyValuePair<SessionKey, StreamSession>(key, existing)))
                KillSession(existing);

            // Dying almost immediately means the camera itself isn't working - wrong URL, wrong
            // credentials, or refusing another connection because something already holds the one
            // it allows. Restarting on sight would then spawn a doomed process per request, and
            // /api/cameras warms every camera at once, so that could outpace the dying. Report it
            // instead and hold off briefly: the owner gets a real error, and a player retrying on
            // its own finds the camera again as soon as it recovers.
            if (ranFor < RestartBackoff)
            {
                _restartBlockedUntil[key] = DateTimeOffset.UtcNow + RestartBackoff;
                throw new InvalidOperationException(
                    $"The stream for this camera keeps stopping - check its URL and credentials, and that it isn't refusing extra connections. Recent log:\n{log}");
            }

            _logger.LogWarning(
                "HLS session for camera {CameraId} ({Name}, {Quality}) died after {Seconds:n0}s; starting a fresh one. Recent log:\n{Log}",
                camera.Id, camera.Name, key.Quality, ranFor.TotalSeconds, log);
        }

        if (_restartBlockedUntil.TryGetValue(key, out var blockedUntil))
        {
            if (DateTimeOffset.UtcNow < blockedUntil)
            {
                throw new InvalidOperationException(
                    "The stream for this camera keeps stopping - check its URL and credentials, and that it isn't refusing extra connections.");
            }

            _restartBlockedUntil.TryRemove(key, out _);
        }

        var session = _sessions.GetOrAdd(key, k => StartSession(camera, k.Quality));
        session.LastAccessed = DateTimeOffset.UtcNow;
        return session;
    }

    public void Touch(Guid cameraId) => Touch(cameraId, StreamQuality.Main);

    public void Touch(Guid cameraId, StreamQuality quality)
    {
        if (TryGetServingSession(cameraId, quality, out var session))
            session.LastAccessed = DateTimeOffset.UtcNow;
    }

    // The session actually serving a rendition, which is not always the one named: a camera with
    // no low stream has its Sub requests served by the Main session (see EffectiveQuality), and a
    // player following relative segment names out of the playlist it was handed knows nothing
    // about that. Resolving the fallback here is what keeps those segment URLs pointing at video.
    private bool TryGetServingSession(Guid cameraId, StreamQuality quality, out StreamSession session)
    {
        if (_sessions.TryGetValue(new SessionKey(cameraId, quality), out session!)) return true;
        return _sessions.TryGetValue(new SessionKey(cameraId, StreamQuality.Main), out session!);
    }

    public string? GetSegmentPath(Guid cameraId, string fileName) =>
        GetSegmentPath(cameraId, StreamQuality.Main, fileName);

    public string? GetSegmentPath(Guid cameraId, StreamQuality quality, string fileName)
    {
        if (!TryGetServingSession(cameraId, quality, out var session)) return null;
        var safeName = Path.GetFileName(fileName); // guards against path traversal
        var path = Path.Combine(session.OutputDirectory, safeName);

        // An empty segment means ffmpeg created the file but hasn't finished writing it (or died
        // mid-write). Serving that as a 200 hands the player zero bytes where it expects video,
        // which most players treat as a fatal decode error rather than something to retry -
        // reporting it as missing instead gets the segment refetched a moment later.
        try
        {
            if (new FileInfo(path).Length == 0) return null;
        }
        catch (IOException)
        {
            return null;
        }

        return path;
    }

    // Poster frame for a camera that already has a warm session - ffmpeg overwrites this file
    // every few seconds as a side output of the same process (see BuildFfmpegArguments), so
    // reading it is just a file read, not another camera round-trip. Null if there's no running
    // session or it hasn't written a first frame yet; callers should fall back to
    // CaptureSnapshotAsync in that case.
    // Any warm session's poster frame will do - both renditions show the same scene, and a tile
    // covered by an 896x672 still while its video starts is no worse off than one covered by a
    // 2560x1920 one. Main first only because it is the likelier one to exist.
    public string? GetThumbnailPath(Guid cameraId)
    {
        foreach (var quality in new[] { StreamQuality.Main, StreamQuality.Sub })
        {
            if (!_sessions.TryGetValue(new SessionKey(cameraId, quality), out var session)) continue;
            var path = Path.Combine(session.OutputDirectory, ThumbnailFileName);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    // Every rendition of the camera, not just the main one - this runs when a camera is edited or
    // deleted, and a low-stream session left behind would go on dialling a camera that no longer
    // exists, or serving video from the URL the edit replaced.
    public void StopStream(Guid cameraId)
    {
        foreach (var quality in new[] { StreamQuality.Main, StreamQuality.Sub })
        {
            if (_sessions.TryRemove(new SessionKey(cameraId, quality), out var session))
                KillSession(session);
        }

        // An edit may well be the fix for the missing low stream, so let the camera prove itself
        // again rather than staying latched to the main stream for the life of the process.
        _noLowResStream.TryRemove(cameraId, out _);
    }

    private StreamSession StartSession(Camera camera, StreamQuality quality)
    {
        var outputDir = Path.Combine(
            _workingRoot,
            quality == StreamQuality.Main ? camera.Id.ToString("N") : $"{camera.Id:N}-sub");
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var sourceUrl = _cameraSource.ResolveUrl(camera, quality);
        var args = BuildFfmpegArguments(sourceUrl, camera.IncludeAudio, outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _logger.LogInformation("Started HLS stream for camera {CameraId} ({Name}), pid {Pid}", camera.Id, camera.Name, process.Id);

        var session = new StreamSession(process, outputDir) { LastAccessed = DateTimeOffset.UtcNow };
        _ = DrainAsync(process.StandardOutput, session);
        _ = DrainAsync(process.StandardError, session);

        return session;
    }

    // ffmpeg writes its progress/diagnostic log to stderr; if nothing reads the pipe it fills
    // up and blocks the process. This drains it and keeps a bounded tail for error messages.
    private static async Task DrainAsync(StreamReader reader, StreamSession session)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                session.AppendLog(line);
        }
        catch
        {
            // process exited; nothing to do
        }
    }

    // Same pipe-draining need as DrainAsync, but for short-lived single-frame captures with no
    // session to log against.
    private static async Task DiscardAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() != null) { }
        }
        catch
        {
            // process exited; nothing to do
        }
    }

    public const string ThumbnailFileName = "thumb.jpg";

    public static List<string> BuildFfmpegArguments(string sourceUrl, bool includeAudio, string outputDir)
    {
        var args = new List<string>();
        // -rtsp_transport is an RTSP-demuxer-only option; ffmpeg errors out ("Option not found")
        // if it's passed ahead of a non-RTSP input like an http:// MJPEG/snapshot URL.
        if (IsRtspUrl(sourceUrl))
            args.AddRange(["-rtsp_transport", "tcp"]);
        // Every frame the demuxer holds onto is a frame the viewer sees late, and the default 5s
        // analyze window before ffmpeg writes anything is pure startup latency on a camera whose
        // format never changes between sessions. 1s is still ample to identify an H.264 stream.
        args.AddRange(["-fflags", "nobuffer", "-analyzeduration", "1000000", "-probesize", "1000000"]);
        // The HLS output copies video, so it never touches the decoder - but the poster-frame
        // output below is a filtered output, and a filter graph needs decoded frames. That alone
        // used to make ffmpeg decode every single frame of every camera, full frame rate, around
        // the clock, to produce one 480px JPEG every three seconds.
        //
        // -skip_frame is a decoder option, so it applies only to that path and leaves the copied
        // segments byte-identical. nokey drops everything but keyframes, which is exactly what a
        // poster frame wants anyway: one I-frame per GOP is already more often than fps=1/3 asks
        // for. Measured on 30s of 1080p30 here: 1.38s of CPU before, 0.07s after, same JPEG out.
        // That is per camera, continuously - on a small always-on box it was a large share of the
        // budget ffmpeg needs to keep segments landing on time.
        args.AddRange(["-skip_frame", "nokey"]);
        args.AddRange(["-i", sourceUrl]);

        // Primary output: HLS segments, video copied untouched so this stays cheap.
        //
        // -hls_time is a ceiling ffmpeg can only honour on a keyframe while copying video, so the
        // real segment length is the camera's GOP: ask a camera with a 4s keyframe interval for 1s
        // segments and you get 4s ones. Since a player sits a small multiple of the segment length
        // behind live, the camera's own I-frame-interval setting sets the floor on end-to-end
        // latency for this whole pipeline - see docs/ for how to check and change it.
        //
        // This asked for 2s for a while, to halve the HTTP round trips a live grid makes - every
        // segment is one request per viewer per camera. Measured against this install's gate
        // camera, that trade was the wrong way round:
        //
        //   -hls_time 2  ->  playlist available 5.00s after ffmpeg starts
        //   -hls_time 1  ->  playlist available 2.29s
        //
        // and the playlist is only the first half of the wait, because a player then sits idle
        // until enough segments have been PRODUCED to fill its buffer - three segments is six
        // seconds of waiting at 2s each and three at 1s. The saving was half a request per second
        // per camera, which no LAN server has ever noticed; the cost was several seconds of black
        // rectangle every time somebody opened the Live tab, which is the first thing anyone sees.
        //
        // On a camera with a GOP of 2s or more this changes nothing either way - copying video can
        // only cut on a keyframe, so the GOP wins and hls_time is only ever a floor.
        args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
        if (includeAudio)
            args.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-ac", "1"]);
        else
            args.Add("-an");
        args.AddRange([
            "-f", "hls",
            "-hls_time", "1",
            // The playlist window, in segments. This is the margin a viewer has to fall behind by
            // before the segment it is asking for has rolled off the playlist entirely - at which
            // point its only move is to jump back to the live edge, which is a visible rebuffer.
            // Six segments was thin: on a camera with a 2s GOP that is a twelve-second window, and
            // a player deliberately sitting near the live edge spends most of it already used up.
            // Counted in segments rather than seconds, so halving the segment length above would
            // have quietly halved this window too - twenty keeps the same ~20s of margin, and the
            // same disk footprint, since each segment is now half the size.
            "-hls_list_size", "20",
            // temp_file: write each segment under a scratch name and rename it into place when
            // it's complete, so a viewer can never fetch a half-written .ts and stall on it.
            // independent_segments: every segment starts on a keyframe (guaranteed here, since
            // copying video can only cut there), which lets a player start on any segment rather
            // than hunting backwards for one it can decode from.
            "-hls_flags", "delete_segments+omit_endlist+independent_segments+temp_file",
            // Keep a few segments on disk past the point they roll off the playlist. A viewer
            // that is momentarily behind - a phone coming back from a locked screen, a slow
            // cellular fetch - then gets the segment it asks for instead of a 404 that costs it
            // a full reconnect. Costs a few MB per camera.
            "-hls_delete_threshold", "5",
            "-hls_segment_filename", Path.Combine(outputDir, "seg_%03d.ts"),
            Path.Combine(outputDir, "stream.m3u8")
        ]);

        // Secondary output: a small JPEG this same process overwrites every few seconds, so a
        // viewer opening this camera gets an instant poster frame instead of a blank tile while
        // the HLS player buffers - see GetThumbnailPath. -update rewrites one file in place
        // instead of numbering a new one per frame. The only output that needs decoded frames,
        // hence -skip_frame nokey on the input above; on a camera whose GOP is longer than 3s
        // this refreshes once per GOP rather than once per 3s, which is fine for a poster.
        args.AddRange([
            "-map", "0:v:0",
            "-vf", "fps=1/3,scale=480:-2",
            "-update", "1",
            "-y",
            Path.Combine(outputDir, ThumbnailFileName)
        ]);

        return args;
    }

    // Public because every ffmpeg launch site that might feed a camera URL needs the same guard:
    // -rtsp_transport is an RTSP-demuxer-only option, and ffmpeg refuses to start at all if it's
    // passed ahead of an http:// input (e.g. a Reolink's FLV stream - see TriggerZoneMonitor).
    public static bool IsRtspUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase);

    public static string BuildAuthenticatedUrl(Camera camera)
    {
        if (string.IsNullOrWhiteSpace(camera.Username)) return camera.StreamUrl;

        var builder = new UriBuilder(camera.StreamUrl)
        {
            UserName = camera.Username,
            Password = camera.Password ?? string.Empty
        };
        return builder.Uri.ToString();
    }

    // Single-frame grab, independent of any running HLS session - used both for the trigger-zone
    // editor's reference image and for saving a full-res snapshot when a trigger zone fires.
    public async Task<byte[]?> CaptureSnapshotAsync(Camera camera, CancellationToken cancellationToken)
    {
        // A browser-published camera is asked through the sidecar rather than with ffmpeg. Pointing
        // ffmpeg at one works, but takes ~30s: it has to wait for the publisher's next keyframe,
        // and browsers emit those far apart. go2rtc can ask the publisher for one - see
        // Go2RtcManager.CaptureFrameAsync.
        if (camera.IsBrowserPublished)
            return await _go2rtc.CaptureFrameAsync(camera.Id, cancellationToken);

        var tempPath = Path.Combine(Path.GetTempPath(), $"snapshot-{Guid.NewGuid():N}.jpg");
        var sourceUrl = _cameraSource.ResolveUrl(camera);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var snapshotArgs = new List<string>();
        if (IsRtspUrl(sourceUrl))
            snapshotArgs.AddRange(["-rtsp_transport", "tcp"]);
        snapshotArgs.AddRange(["-i", sourceUrl, "-frames:v", "1", "-y", tempPath]);
        foreach (var arg in snapshotArgs)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _ = DiscardAsync(process.StandardOutput);
        _ = DiscardAsync(process.StandardError);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }

        try
        {
            if (!File.Exists(tempPath)) return null;
            var bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken);
            return bytes;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    private void CleanupIdleSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;
        foreach (var (key, session) in _sessions)
        {
            if (session.LastAccessed < cutoff && _sessions.TryRemove(key, out var removed))
            {
                _logger.LogInformation(
                    "Stopping idle HLS stream for camera {CameraId} ({Quality})", key.CameraId, key.Quality);
                KillSession(removed);
            }
        }
    }

    private void KillSession(StreamSession session)
    {
        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
                session.Process.WaitForExit(3000);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            session.Process.Dispose();
            try { Directory.Delete(session.OutputDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var session in _sessions.Values) KillSession(session);
        _sessions.Clear();
    }

    private sealed class StreamSession(Process process, string outputDirectory)
    {
        private const int MaxLogLines = 20;
        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();

        public Process Process { get; } = process;
        public string OutputDirectory { get; } = outputDirectory;
        public DateTimeOffset LastAccessed { get; set; }

        // When ffmpeg was launched, so a session that dies immediately can be told apart from one
        // that ran for a while and then dropped - only the latter is worth restarting on sight.
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public void AppendLog(string line)
        {
            lock (_logLock)
            {
                _log.Enqueue(line);
                while (_log.Count > MaxLogLines) _log.Dequeue();
            }
        }

        public string RecentLog
        {
            get
            {
                lock (_logLock) return string.Join('\n', _log);
            }
        }
    }
}
