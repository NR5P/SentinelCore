using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SentinelCore.Web;

// Why a stream never started, in a form the caller can act on rather than only print.
//
// The message is the short, credential-masked sentence that reaches the client; the ffmpeg tail
// behind it is what lets EnsureStreamStartedAsync tell a refused login apart from a rendition that
// isn't there. Before this, the whole ffmpeg log - banner, stream mapping and all - was interpolated
// into the message, which went two places it should never have gone: the journal, and a 502 body
// (see CameraEndpoints), each carrying the camera password ffmpeg echoes back inside the input URL.
public interface IFfmpegFailure
{
    IReadOnlyList<string> FfmpegLog { get; }
}

// InvalidOperationException and TimeoutException respectively, because those are the types this
// used to throw and what callers still catch - the log is added, nothing is taken away. The two
// stay distinct because the difference matters to the caller: on this hardware a genuinely absent
// substream BLOCKS rather than exiting, so a timeout is evidence about the rendition and an
// immediate exit usually isn't (see FfmpegExitReason).
public sealed class StreamStartFailedException(string message, IReadOnlyList<string> ffmpegLog)
    : InvalidOperationException(message), IFfmpegFailure
{
    public IReadOnlyList<string> FfmpegLog { get; } = ffmpegLog;
}

public sealed class StreamStartTimedOutException(string message, IReadOnlyList<string> ffmpegLog)
    : TimeoutException(message), IFfmpegFailure
{
    public IReadOnlyList<string> FfmpegLog { get; } = ffmpegLog;
}

// Starts an ffmpeg process per camera on first request, remuxing whatever RTSP/HTTP source
// the camera speaks into HLS segments a browser can actually play (browsers have no native
// RTSP support). Video is copied, not re-encoded, so this is cheap whatever the camera sends -
// but not every codec may be packaged the same way, and an H.265 camera has to be segmented as
// fragmented MP4 rather than MPEG-TS to be playable at all (see BuildFfmpegArguments). Streams
// nobody is watching are killed after a short idle period so this doesn't burn CPU on cameras
// left unwatched.
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
    // The reason travels with the backoff. Without it, only the request that actually watched
    // ffmpeg die can classify the failure: every request during the following ten seconds is
    // answered from this dictionary instead, and a blank log reads as an unexplained failure - so
    // a rejected password would still reach the low-resolution latch below, one request later.
    private readonly record struct RestartBlock(DateTimeOffset Until, IReadOnlyList<string> FfmpegLog);

    private readonly ConcurrentDictionary<SessionKey, RestartBlock> _restartBlockedUntil = new();

    // Cameras that turned out not to serve the low-resolution stream their URL implied, and when
    // it becomes worth asking them again. ffmpeg refuses an input it can't open and quits
    // immediately, so a Sub session that exits without ever writing a playlist is usually that
    // camera telling us the rendition isn't enabled. Remembering that sends every later request
    // for the camera to the main stream instead of crash-looping on the same guess - the same
    // fallback TriggerZoneMonitor keeps for detection, for the same reason.
    //
    // It expires rather than latching for the life of the process, because the failure it keys on
    // is not only ever "the rendition is off". A Reolink refusing one more connection, a startup
    // that overran its budget under load, or a momentary network drop all look identical from
    // here, and on these cameras the first of those is likely: the gate camera already carries a
    // detection consumer on the same ext stream and a clip-buffer consumer on the main one, so a
    // Sub session is the third connection against a small budget. Latching permanently on one of
    // those would put every grid tile back on the 2560x1920 stream for good - silently undoing
    // the whole point of the split, with nothing but a single warning an hour earlier to show for
    // it. Re-probing costs one ffmpeg that exits immediately, which is the cheaper thing to be
    // wrong about.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lowResRetryAfter = new();

    // Cameras already warned about a fault that has no fallback - see WarnOncePerCamera.
    private readonly ConcurrentDictionary<Guid, bool> _warnedCameras = new();

    // Long enough that a camera with no low stream isn't respawning doomed processes (the camera
    // list warms every camera on each fetch), short enough that a transient failure doesn't cost
    // a viewer the low rendition for the rest of the day.
    private static readonly TimeSpan LowResRetryInterval = TimeSpan.FromMinutes(10);

    // Renditions last seen carrying H.265, which have to be segmented as fragmented MP4 rather
    // than MPEG-TS. Keyed per rendition, not per camera, because the two commonly differ: the
    // camera this was written for serves H.265 on its main stream and H.264 on its sub.
    //
    // This is a cache of an observation, never a setting, and it is allowed to be wrong - a
    // session whose ffmpeg reports a codec that contradicts it corrects the entry and restarts
    // itself (see StartAndWaitAsync). That is what keeps the packaging right when someone changes
    // the encoder in the camera's own web UI, which is a thing that happens to this install and
    // which nothing here would otherwise hear about.
    private readonly ConcurrentDictionary<SessionKey, bool> _hevcSources = new();

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
    // show a picture. So a Sub request the *camera* won't satisfy degrades to the main stream rather
    // than failing, and the camera is remembered so the next viewer doesn't pay the same discovery
    // twice. Main requests keep the original behaviour exactly - they have nothing to fall back to.
    //
    // The exception is a failure the main stream shares: a refused login and an unreachable camera
    // are facts about the camera, not about the rendition, and falling back to a stream reached
    // with the same credentials over the same network only fails twice as slowly. Those report what
    // actually happened - see the catch below.
    public async Task<string> EnsureStreamStartedAsync(
        Camera camera, StreamQuality quality, CancellationToken cancellationToken)
    {
        var effective = EffectiveQuality(camera, quality);
        try
        {
            var playlistPath = await StartAndWaitAsync(camera, effective, cancellationToken);

            // A camera that works again is allowed to report its next fault. Otherwise the one-shot
            // warning is one per process, and the second time someone breaks a password is silent.
            _warnedCameras.TryRemove(camera.Id, out _);
            return playlistPath;
        }
        catch (Exception ex) when (effective == StreamQuality.Sub && ex is not OperationCanceledException)
        {
            var ffmpegLog = (ex as IFfmpegFailure)?.FfmpegLog ?? [];

            // Two failures say nothing whatever about the low rendition, and the main stream cannot
            // repair either of them: it is reached over the same network with the same login. Trying
            // it anyway buys a second failure, a second startup budget, and - because the fallback
            // latches - ten minutes of every grid tile decoding the camera's main stream, which on
            // the camera that prompted this is 4K at 25fps. So these report the real cause and stop.
            //
            // Held off for seconds rather than latched for minutes. The difference is what the wait
            // is made of: a latch waits out a fixed interval before it will even look again, while
            // this only rations attempts, so the tile is back the moment the password is right.
            if (FfmpegExitReason.IndicatesAuthenticationFailure(ffmpegLog))
            {
                RationLowResAttempts(camera, ffmpegLog);
                WarnOncePerCamera(camera,
                    "Camera {CameraId} ({Name}) rejected the credentials on its low-resolution stream: {Reason}. " +
                    "Its main stream uses the same login, so that is not being tried as a fallback - check the " +
                    "camera's username and password.",
                    FfmpegExitReason.Describe(ffmpegLog) ?? "authentication failed");
                throw;
            }

            if (FfmpegExitReason.IndicatesSourceUnreachable(ffmpegLog))
            {
                RationLowResAttempts(camera, ffmpegLog);
                WarnOncePerCamera(camera,
                    "Camera {CameraId} ({Name}) could not be reached on its low-resolution stream: {Reason}. " +
                    "Nothing was learned about that rendition, so it stays the preferred one and the main stream " +
                    "is not being tried as a fallback.",
                    FfmpegExitReason.Describe(ffmpegLog) ?? "the camera did not answer");
                throw;
            }

            // What is left is either the camera answering and refusing this rendition, or a session
            // that never produced a playlist inside its budget - and a genuinely absent substream is
            // the case that blocks rather than exiting (see FfmpegExitReason), so a timeout is the
            // strongest evidence there is. Both are about the rendition, so both still fall back.
            //
            // Warn the first time only. A camera whose low stream is genuinely switched off would
            // otherwise reprint this every retry interval for as long as the dashboard is open.
            var alreadyKnown = _lowResRetryAfter.ContainsKey(camera.Id);
            _lowResRetryAfter[camera.Id] = DateTimeOffset.UtcNow + LowResRetryInterval;
            if (!alreadyKnown)
                _logger.LogWarning(
                    "Camera {CameraId} ({Name}) never produced a playlist on its derived low-resolution stream, so " +
                    "that stream probably isn't enabled on the camera. Falling back to the main stream for live view, " +
                    "and trying the low one again in {RetryMinutes:n0} minutes. ffmpeg said: {Reason}",
                    camera.Id, camera.Name, LowResRetryInterval.TotalMinutes,
                    FfmpegExitReason.Describe(ffmpegLog) ?? ex.Message);

            // The failed attempt left a backoff behind for the rendition we're abandoning; clearing
            // it keeps that from blocking the retry we're about to make on a different key.
            _restartBlockedUntil.TryRemove(new SessionKey(camera.Id, StreamQuality.Sub), out _);
            return await StartAndWaitAsync(camera, StreamQuality.Main, cancellationToken);
        }
    }

    // Neither no-fallback path may spawn an ffmpeg per request: /api/cameras warms every camera on
    // each fetch, and a wrong password would otherwise put a doomed process behind every one of
    // those, every few seconds, for as long as it stayed wrong. The ordinary restart backoff is the
    // right tool and already carries the reason forward - but it is only ever *set* by a session
    // that died after being established, and one that never starts at all is removed before it can
    // leave one behind. So this puts it there.
    //
    // Only when there isn't a live one already. Refreshing it on each request would push the
    // deadline forward faster than it could expire under a dashboard that polls, and a hold-off
    // that never lifts is the latch this exists to avoid.
    private void RationLowResAttempts(Camera camera, IReadOnlyList<string> ffmpegLog)
    {
        var key = new SessionKey(camera.Id, StreamQuality.Sub);
        if (_restartBlockedUntil.TryGetValue(key, out var current) && DateTimeOffset.UtcNow < current.Until)
            return;

        _restartBlockedUntil[key] = new RestartBlock(DateTimeOffset.UtcNow + RestartBackoff, ffmpegLog);
    }

    // Both no-fallback paths are reached by every request for as long as the fault lasts, and
    // /api/cameras warms every camera on each fetch - so an unthrottled warning here is the
    // "every ten seconds for nine hours" journal that FfmpegExitReason was written about. Keyed on
    // the camera and cleared when it next works, so the next occurrence is reported afresh.
    private void WarnOncePerCamera(Camera camera, string message, string reason)
    {
        if (!_warnedCameras.TryAdd(camera.Id, true)) return;
        _logger.LogWarning(message, camera.Id, camera.Name, reason);
    }

    // Which rendition this camera can actually serve for the one that was asked for. Resolved
    // before a session key is built, so a camera with no low stream runs one process rather than
    // two identical ones - warming Sub and Main separately would double both the ffmpeg count and
    // the connections against a Reolink's small budget, which is the opposite of the point.
    private StreamQuality EffectiveQuality(Camera camera, StreamQuality requested)
    {
        if (requested == StreamQuality.Main) return StreamQuality.Main;
        if (camera.IsBrowserPublished) return StreamQuality.Main;
        if (_lowResRetryAfter.TryGetValue(camera.Id, out var retryAfter) && DateTimeOffset.UtcNow < retryAfter)
            return StreamQuality.Main;
        return CameraSource.TryDeriveLowResUrl(HlsStreamManager.BuildAuthenticatedUrl(camera)) is null
            ? StreamQuality.Main
            : StreamQuality.Sub;
    }

    // The packaging a session was started with is a guess on the first sighting of a rendition,
    // and a stale cache entry on every later one. ffmpeg settles it: it names the input's video
    // codec on stderr while opening the stream, which the session parses out of the log it is
    // already keeping. If that contradicts the guess, the entry is corrected and the session
    // restarted with the right muxer.
    //
    // Deliberately done this way round rather than by probing the camera with ffprobe before every
    // cold start. A probe would be simpler, but it would put an RTSP round-trip in front of every
    // camera on every cold start - measured elsewhere in this file, the whole budget from launch
    // to a playlist a player can use is about 2.3s, and that is the number anyone notices when
    // they open the Live tab. Reading a line ffmpeg prints anyway costs nothing, and the cost of
    // being wrong falls only on H.265 cameras, only on a cold start, and only once per change.
    private async Task<string> StartAndWaitAsync(
        Camera camera, StreamQuality quality, CancellationToken cancellationToken)
    {
        var key = new SessionKey(camera.Id, quality);
        var playlistPath = await StartAndWaitOnceAsync(camera, key, cancellationToken);

        if (!_sessions.TryGetValue(key, out var started) || !NeedsRepackaging(started))
            return playlistPath;

        var actuallyHevc = IsHevc(started.SourceVideoCodec!);
        _hevcSources[key] = actuallyHevc;
        _logger.LogInformation(
            "Camera {CameraId} ({Name}, {Quality}) sends {Codec}; restarting its stream as {Packaging} " +
            "so players can decode it.",
            camera.Id, camera.Name, key.Quality, started.SourceVideoCodec,
            actuallyHevc ? "fragmented MP4" : "MPEG-TS");

        // Compare-and-remove for the same reason the death path uses one: only the caller that
        // actually takes this session out of the dictionary may kill it.
        if (_sessions.TryRemove(new KeyValuePair<SessionKey, StreamSession>(key, started)))
            KillSession(started);

        // At most one correction per request: the replacement is started from a now-correct entry,
        // so its own codec line agrees with it and this cannot bounce.
        return await StartAndWaitOnceAsync(camera, key, cancellationToken);
    }

    // Only ever true once ffmpeg has actually said what the codec is - an unparsed log (a session
    // that died before printing one, a future ffmpeg that words the line differently) leaves the
    // guess in place rather than flip-flopping the packaging on no evidence.
    private static bool NeedsRepackaging(StreamSession session) =>
        session.SourceVideoCodec is { } codec && IsHevc(codec) != session.HevcSource;

    private static bool IsHevc(string codec) =>
        codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) ||
        codec.Equals("h265", StringComparison.OrdinalIgnoreCase);

    private async Task<string> StartAndWaitOnceAsync(
        Camera camera, SessionKey key, CancellationToken cancellationToken)
    {
        var session = GetOrStartLiveSession(camera, key);

        var playlistPath = Path.Combine(session.OutputDirectory, "stream.m3u8");
        var deadline = DateTimeOffset.UtcNow + StartupBudgetFor(camera);
        while (!File.Exists(playlistPath) && DateTimeOffset.UtcNow < deadline)
        {
            if (session.Process.HasExited)
            {
                _sessions.TryRemove(key, out _);
                var log = session.RecentLogLines;
                throw new StreamStartFailedException(
                    "ffmpeg exited before producing a stream - check the camera URL/credentials. " +
                    $"ffmpeg said: {FfmpegExitReason.Describe(log) ?? "nothing at all"}",
                    log);
            }

            await Task.Delay(200, cancellationToken);
        }

        if (!File.Exists(playlistPath))
        {
            _sessions.TryRemove(key, out _);
            var stalledLog = session.RecentLogLines;
            KillSession(session);
            throw new StreamStartTimedOutException(
                "Timed out waiting for the stream to start. " +
                $"ffmpeg said: {FfmpegExitReason.Describe(stalledLog) ?? "nothing at all"}",
                stalledLog);
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
            var log = existing.RecentLogLines;
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
                _restartBlockedUntil[key] = new RestartBlock(DateTimeOffset.UtcNow + RestartBackoff, log);
                throw new StreamStartFailedException(
                    "The stream for this camera keeps stopping - check its URL and credentials, and that it " +
                    $"isn't refusing extra connections. ffmpeg said: {FfmpegExitReason.Describe(log) ?? "nothing at all"}",
                    log);
            }

            _logger.LogWarning(
                "HLS session for camera {CameraId} ({Name}, {Quality}) died after {Seconds:n0}s; starting a fresh one. ffmpeg said: {Reason}",
                camera.Id, camera.Name, key.Quality, ranFor.TotalSeconds,
                FfmpegExitReason.Describe(log) ?? "nothing at all");
        }

        if (_restartBlockedUntil.TryGetValue(key, out var blocked))
        {
            if (DateTimeOffset.UtcNow < blocked.Until)
            {
                throw new StreamStartFailedException(
                    "The stream for this camera keeps stopping - check its URL and credentials, and that it " +
                    $"isn't refusing extra connections. ffmpeg said: {FfmpegExitReason.Describe(blocked.FfmpegLog) ?? "nothing at all"}",
                    blocked.FfmpegLog);
            }

            _restartBlockedUntil.TryRemove(key, out _);
        }

        var session = _sessions.GetOrAdd(
            key, k => StartSession(camera, k.Quality, _hevcSources.TryGetValue(k, out var hevc) && hevc));
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

    // The live stream's segments currently on disk, oldest first - a rolling window about
    // hls_list_size seconds wide (see BuildFfmpegArguments), kept for any camera somebody is
    // watching. The manual record button builds its clip out of these.
    //
    // Start times come from the file's write time rather than its name, because these segments are
    // numbered (seg_%03d) rather than stamped: ffmpeg finishes writing a segment when it ends, so
    // the write time is its end and its start is one -hls_time earlier. Good enough for choosing
    // which segments cover a window, which is all SelectSegmentsCovering needs.
    //
    // Only the MPEG-TS renditions. An H.265 camera is packaged as fMP4 (see the hevc branch in
    // BuildFfmpegArguments), and the concat demuxer cannot splice those into a playable MP4 without
    // re-muxing work this doesn't do - so those cameras get the trigger event without a clip rather
    // than a corrupt file.
    public IReadOnlyList<(string Path, DateTimeOffset StartsAt)> BufferedSegments(Guid cameraId)
    {
        foreach (var quality in new[] { StreamQuality.Main, StreamQuality.Sub })
        {
            if (!_sessions.TryGetValue(new SessionKey(cameraId, quality), out var session)) continue;

            var files = Directory.Exists(session.OutputDirectory)
                ? Directory.EnumerateFiles(session.OutputDirectory, "seg_*.ts").ToList()
                : [];
            if (files.Count == 0) continue;

            return files
                .Select(path => (Path: path, StartsAt: (DateTimeOffset)File.GetLastWriteTimeUtc(path) - TimeSpan.FromSeconds(1)))
                .OrderBy(segment => segment.StartsAt)
                .ToList();
        }

        return [];
    }

    // Poster frame for a camera that already has a warm session - ffmpeg overwrites this file
    // every few seconds as a side output of the same process (see BuildFfmpegArguments), so
    // reading it is just a file read, not another camera round-trip. Null if there's no running
    // session or it hasn't written a first frame yet; callers should fall back to
    // CaptureSnapshotAsync in that case.
    //
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
        // again immediately rather than serving main until the retry interval is up.
        _lowResRetryAfter.TryRemove(cameraId, out _);

        // Same reasoning for the observed codec: an edit can repoint the camera at a different
        // stream entirely, and a remembered packaging from the old one is worse than no memory at
        // all. Dropping it costs one self-correcting restart if the guess comes back wrong.
        foreach (var quality in new[] { StreamQuality.Main, StreamQuality.Sub })
            _hevcSources.TryRemove(new SessionKey(cameraId, quality), out _);
    }

    private StreamSession StartSession(Camera camera, StreamQuality quality, bool hevcSource)
    {
        var outputDir = Path.Combine(
            _workingRoot,
            quality == StreamQuality.Main ? camera.Id.ToString("N") : $"{camera.Id:N}-sub");
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var sourceUrl = _cameraSource.ResolveUrl(camera, quality);
        var args = BuildFfmpegArguments(sourceUrl, camera.IncludeAudio, outputDir, hevcSource);

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

        var session = new StreamSession(process, outputDir, hevcSource) { LastAccessed = DateTimeOffset.UtcNow };
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

    public static List<string> BuildFfmpegArguments(
        string sourceUrl, bool includeAudio, string outputDir, bool hevcSource = false)
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
        // Apple will not decode an H.265 track tagged hev1, which is what ffmpeg's MP4 muxer
        // writes by default; hvc1 is the tag its HLS authoring rules require and the only one
        // AVPlayer accepts. Wrong for H.264, hence only on this branch.
        if (hevcSource)
            args.AddRange(["-tag:v", "hvc1"]);
        if (includeAudio)
            args.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-ac", "1"]);
        else
            args.Add("-an");
        args.AddRange([
            "-f", "hls",
            // One-second segments, made real by split_by_time below - without that flag this
            // number is only a floor and the camera's keyframe interval wins, which is what used
            // to make these 2s (and 4s on a camera set to 2x). Segment length is the single
            // biggest term in live latency, because a plain HLS player parks three target
            // durations behind the live edge and there is no way to talk it out of that:
            // configuredTimeOffsetFromLive is a low-latency-HLS control and does nothing to a
            // playlist like this one (measured 2026-08-19 - setting it moved the picture not at
            // all). Measured end to end in the app against the PTZ camera's own burned-in clock:
            // 7.4s behind before, 4.6s after.
            //
            // **Do not lower this to get closer.** It buys nothing and breaks cameras. What the
            // player actually parks against is TARGETDURATION, which is an integer ffmpeg writes
            // as lrint(longest segment) - so anything from ~0.5s to ~1.4s produces the same
            // TARGETDURATION:1 and the same 3s of parking. Half-second segments were tried first
            // and cost a whole camera: sources that hit exactly 0.500000 (the gate camera, an
            // HTTP-FLV input rather than RTSP) round to **TARGETDURATION:0**, which is invalid
            // HLS, and AVPlayer refuses the playlist outright - the tile sits on "Starting
            // stream..." forever. Measured 2026-08-19: gate at 0.5 -> TARGETDURATION:0, gate at
            // 1 -> 1, PTZ at 1 -> 1. One second sits in the middle of the rounding bucket
            // instead of exactly on its edge, which is the whole reason to prefer it.
            //
            // 3 x 1s is the floor for this transport. Below it needs WebRTC, not a smaller
            // number here.
            "-hls_time", "1",
            // The playlist window, in segments. This is the margin a viewer has to fall behind by
            // before the segment it is asking for has rolled off the playlist entirely - at which
            // point its only move is to jump back to the live edge, which is a visible rebuffer.
            // Six segments was thin: on a camera with a 2s GOP that is a twelve-second window, and
            // a player deliberately sitting near the live edge spends most of it already used up.
            // Counted in segments rather than seconds, so shortening the segment length above
            // would have quietly shortened this window too - twenty one-second segments is the
            // ~20s of margin that made the live grid stop stuttering.
            "-hls_list_size", "20",
            // temp_file: write each segment under a scratch name and rename it into place when
            // it's complete, so a viewer can never fetch a half-written .ts and stall on it.
            //
            // split_by_time: cut on the clock rather than only on keyframes. Copying video can
            // normally only cut where the camera put a keyframe, which is what made -hls_time a
            // floor and pinned segment length to the GOP - 2s on the PTZ camera here, 4s on a
            // camera set to 2x. With this flag the segments come out at -hls_time regardless, and
            // the latency above follows.
            //
            // independent_segments is therefore gone, and its removal is the point rather than an
            // oversight: it promises every segment begins at a keyframe, which is exactly what
            // split_by_time stops being true. Leaving it in would be a lie told to the player
            // about video it is about to decode. The cost of dropping it is that a player joining
            // mid-stream may need to wait for the next keyframe before it has a decodable
            // starting point - at most one GOP, and unnoticeable next to the seconds this saves.
            "-hls_flags", "delete_segments+omit_endlist+split_by_time+temp_file",
            // Keep a few segments on disk past the point they roll off the playlist. A viewer
            // that is momentarily behind - a phone coming back from a locked screen, a slow
            // cellular fetch - then gets the segment it asks for instead of a 404 that costs it
            // a full reconnect. Costs a few MB per camera.
            "-hls_delete_threshold", "5",
        ]);

        // Segment container. MPEG-TS is HLS's original and is what every H.264 camera here has
        // always used, but HEVC in MPEG-TS is not something Apple ever taught AVFoundation to
        // play: HEVC arrived in HLS together with fragmented MP4 and is only supported there.
        // The failure is silent and total - iOS and macOS render a black tile, log nothing, and
        // report no error - so this is not a quality tradeoff to make lazily, it is the difference
        // between video and no video. Browsers are less strict about the container but have their
        // own patchy HEVC decoding, which is a separate matter and unaffected by this choice.
        if (hevcSource)
        {
            args.AddRange([
                "-hls_segment_type", "fmp4",
                // Bare filename on purpose. ffmpeg writes the init segment next to the playlist
                // and copies this string verbatim into the playlist's #EXT-X-MAP URI, which a
                // player then resolves relative to the playlist's own URL - so it comes back as a
                // sibling request and is served by the same route as the segments. An absolute
                // path here would end up in the playlist as a server path no client can fetch.
                "-hls_fmp4_init_filename", "init.mp4",
                "-hls_segment_filename", Path.Combine(outputDir, "seg_%03d.m4s")
            ]);
        }
        else
        {
            args.AddRange(["-hls_segment_filename", Path.Combine(outputDir, "seg_%03d.ts")]);
        }

        args.Add(Path.Combine(outputDir, "stream.m3u8"));

        // Secondary output: a small JPEG this same process overwrites every few seconds, so a
        // viewer opening this camera gets an instant poster frame instead of a blank tile while
        // the HLS player buffers - see GetThumbnailPath. -update rewrites one file in place
        // instead of numbering a new one per frame. The only output that needs decoded frames,
        // hence -skip_frame nokey on the input above; on a camera whose GOP is longer than 3s
        // this refreshes once per GOP rather than once per 3s, which is fine for a poster.
        args.AddRange([
            "-map", "0:v:0",
            "-vf", "fps=1/3,scale=480:-2",
            // Same reasoning as TriggerZoneMonitor's snapshot output: mjpeg refuses a frame that is
            // not full-range, and the refusal kills the process rather than the output - so a
            // camera that starts signalling limited range would lose its live stream, not just its
            // poster frame. yuvj420p is full-range by definition and is already what the format
            // negotiation picks here anyway; saying so removes the failure rather than the picture.
            "-pix_fmt", "yuvj420p",
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
        // -pix_fmt yuvj420p for the same reason as every other JPEG this app writes: mjpeg refuses a
        // frame that is not full-range and exits without an image. This is the fallback a trigger
        // falls back TO when the rewind can't produce a frame, so a failure here is a trigger with
        // no picture at all.
        snapshotArgs.AddRange(["-i", sourceUrl, "-frames:v", "1", "-pix_fmt", "yuvj420p", "-y", tempPath]);
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

    // ffmpeg names the input's streams as it opens them, one per line:
    //   Stream #0:0: Video: hevc (Main), yuv420p(tv), 3840x2160 [SAR 1:1 DAR 16:9], 25 fps
    // Public so a test can pin the parsing against real ffmpeg output without running ffmpeg.
    public static string? TryParseSourceVideoCodec(string logLine)
    {
        var match = SourceVideoCodecPattern.Match(logLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static readonly Regex SourceVideoCodecPattern =
        new(@"^\s*Stream #\d+:\d+.*?: Video: ([A-Za-z0-9]+)", RegexOptions.Compiled);

    private sealed class StreamSession(Process process, string outputDirectory, bool hevcSource)
    {
        private const int MaxLogLines = 20;
        private readonly object _logLock = new();
        private readonly Queue<string> _log = new();
        private string? _sourceVideoCodec;

        public Process Process { get; } = process;
        public string OutputDirectory { get; } = outputDirectory;
        public DateTimeOffset LastAccessed { get; set; }

        // What this session's arguments assumed, so a contradiction is detectable.
        public bool HevcSource { get; } = hevcSource;

        // What ffmpeg said the camera is actually sending, or null until it says so.
        public string? SourceVideoCodec
        {
            get { lock (_logLock) return _sourceVideoCodec; }
        }

        // When ffmpeg was launched, so a session that dies immediately can be told apart from one
        // that ran for a while and then dropped - only the latter is worth restarting on sight.
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public void AppendLog(string line)
        {
            lock (_logLock)
            {
                // First match only. ffmpeg describes the input's streams before the output's, and
                // the outputs include a filtered MJPEG poster frame whose own "Video: mjpeg" line
                // would otherwise overwrite the answer with the codec of the thumbnail.
                _sourceVideoCodec ??= TryParseSourceVideoCodec(line);
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

        // A snapshot rather than the queue itself: FfmpegExitReason reads it more than once, and
        // the writer thread is still appending to the live buffer while it does.
        public IReadOnlyList<string> RecentLogLines
        {
            get
            {
                lock (_logLock) return _log.ToArray();
            }
        }
    }
}
