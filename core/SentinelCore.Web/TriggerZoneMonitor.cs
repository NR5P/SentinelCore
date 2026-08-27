using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using SentinelCore;

namespace SentinelCore.Web;

// Runs one continuous low-res ffmpeg capture per camera that has an enabled trigger zone,
// independent of whether anyone is watching the LIVE tab. Every ~2 frames/sec gets diffed
// (see MotionDetector); a big-enough blob inside the zone writes a TriggerEvent and saves a
// full-res snapshot, subject to per-camera cooldown/rate-limit settings.
public sealed class TriggerZoneMonitor(
    ICameraStore cameras,
    ICameraGroupStore groupStore,
    ITriggerConfigStore configStore,
    ITriggerEventStore eventStore,
    HlsStreamManager streams,
    ClipRecorder clips,
    TriggerRewindService rewind,
    AlertForwarder alertForwarder,
    TriggerConfirmationService triggerConfirmation,
    AppSettingsFile mainAppSettings,
    CameraSource cameraSource,
    PtzActivityTracker ptzActivity,
    PtzTracker ptzTracking,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<TriggerZoneMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(10);

    // How long after a PTZ camera stops moving before its detection resumes. It covers the gap
    // between the camera saying it stopped and the picture actually being still: these cameras
    // overshoot slightly and coast, so frames taken immediately after a stop are still full-frame
    // motion. Long enough to cover that coast, short enough that a quick look-and-return doesn't
    // leave a hole in coverage.
    private static readonly TimeSpan PtzSettleDelay = TimeSpan.FromSeconds(2);

    // A camera parked away from home logs its reason at most this often. Detection runs at 2fps, so
    // an evening spent pointed elsewhere would otherwise write tens of thousands of identical lines.
    private static readonly TimeSpan PtzSuppressionLogInterval = TimeSpan.FromMinutes(5);

    // Detection frames arrive twice a second (fps=2 below). Anything approaching this long without
    // one means the feed is gone, whatever the ffmpeg process claims - see ReconcileCameraAsync.
    // Generous enough that a camera stuttering under load isn't torn down for it.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    // Browser-published cameras need a far longer rope, and would otherwise never work at all. A
    // browser's WebRTC encoder emits a keyframe only every ~30 seconds and ffmpeg decodes nothing
    // until one arrives, so a freshly-started session for one is legitimately frameless for about
    // as long as the ordinary timeout allows - the reconcile pass would kill it just before its
    // first frame, restart it, and repeat forever, leaving the camera permanently blind while
    // looking busy. Once running, frames arrive at the usual rate; this only has to cover the
    // wait for that first keyframe, plus room for a slow device.
    private static readonly TimeSpan PublishedStallTimeout = TimeSpan.FromSeconds(90);

    private static TimeSpan StallTimeoutFor(Camera camera) =>
        camera.IsBrowserPublished ? PublishedStallTimeout : StallTimeout;

    // Motion that reached at least this share of a zone's minimum size is worth telling the user
    // about (see LogNearMiss); below it, it's ordinary scene noise. TriggerRewind reuses it as the
    // floor under a rewound frame, on the same reasoning.
    public const double NearMissRatio = 0.4;
    private static readonly TimeSpan NearMissLogInterval = TimeSpan.FromMinutes(1);

    // Longer than the near-miss interval: this one is a reminder that a setting is on, not a
    // tuning aid, so a handful of lines across a night is plenty.
    private static readonly TimeSpan NightSuppressionLogInterval = TimeSpan.FromMinutes(30);

    // Detection frames per second. Every zone's MinTriggerArea is implicitly calibrated against
    // this, because a blob's size depends on how far apart the two differenced frames are.
    public const int DetectionFps = 2;

    // The one description of what a frame must look like before MotionDetector sees it. Public and
    // shared because TriggerRewind decodes recorded video through the very same chain: the zone
    // masks are rasterized against a fixed 320x180 grid, so the non-uniform squash here is what
    // lets a 640x480 substream and a 2560x1920 main stream produce identical masks. Two copies of
    // this string would eventually disagree, and the symptom would be zones quietly landing
    // somewhere else in rewound frames.
    public static string BuildDetectionFilter(int fps) =>
        $"fps={fps},scale={MotionDetector.GridWidth}:{MotionDetector.GridHeight},format=rgb24";

    private readonly string _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
    private readonly string _imagesRoot = ResolveImagesRoot(configuration, environment);
    private readonly ConcurrentDictionary<Guid, DetectionSession> _sessions = new();

    // Cameras whose derived low-resolution stream turned out not to exist. Learned the only way
    // available without probing every camera on every start - by watching a session die without
    // ever producing a frame - and remembered for the life of the process so the restart doesn't
    // make the same guess again.
    private readonly ConcurrentDictionary<Guid, bool> _noSubstream = new();
    private CancellationToken _stoppingToken = CancellationToken.None;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_imagesRoot);
        _stoppingToken = stoppingToken;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trigger zone reconcile failed");
            }

            try
            {
                await Task.Delay(ReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var session in _sessions.Values) StopSession(session);
    }

    private async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        var enabled = await configStore.GetEnabledAsync(stoppingToken);
        var enabledIds = enabled.Select(c => c.CameraId).ToHashSet();

        // Read once per reconcile pass (every ~10s), not per camera - a change here takes effect
        // the next time a camera's detection session (re)starts, same as any other setting that
        // sizes something at process/session start (e.g. MQTT's embedded broker port).
        var multiFrame = await mainAppSettings.GetSectionAsync<MultiFrameOptions>("MultiFrameAnalysis", stoppingToken) ?? new();
        var rewindOptions = await mainAppSettings.GetSectionAsync<TriggerRewindOptions>("TriggerRewind", stoppingToken) ?? new();

        foreach (var (cameraId, session) in _sessions)
        {
            if (!enabledIds.Contains(cameraId))
            {
                _sessions.TryRemove(cameraId, out _);
                StopSession(session);
                clips.DiscardBuffer(cameraId);
            }
        }

        foreach (var config in enabled)
        {
            // Per camera, so one camera that can't be reconciled - a permissions error creating its
            // buffer directory, a camera row that's mid-delete, ffmpeg refusing to start - doesn't
            // abort the pass and leave every camera after it in the list unstarted. That failure
            // was invisible and permanent: the enabled list comes back in a stable order, so the
            // same camera threw on every pass and the same cameras stayed dark forever.
            try
            {
                await ReconcileCameraAsync(config, multiFrame, rewindOptions, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not reconcile trigger zone detection for camera {CameraId}", config.CameraId);
            }
        }
    }

    private async Task ReconcileCameraAsync(
        TriggerConfig config, MultiFrameOptions multiFrame, TriggerRewindOptions rewindOptions, CancellationToken stoppingToken)
    {
        if (_sessions.TryGetValue(config.CameraId, out var existing))
        {
            // A dead ffmpeg means no detection, no fresh _latest.jpg and no clip segments for
            // this camera until something restarts it - and nothing else does: this reconcile
            // pass used to only restart on config changes, so a camera dropping the connection
            // once (reboot, encoder settings applied, network blip) silently disabled its
            // trigger zones until the whole service was restarted.
            //
            // HasExited alone isn't enough, though. The worse failure is an ffmpeg that is very
            // much alive and simply never delivers another frame: a half-open TCP connection (the
            // AP roamed, the router dropped the NAT entry, the camera's firmware hiccuped) leaves
            // it blocked in read() indefinitely. Nothing exits, no config changed, so this pass hit
            // `continue` every ten seconds forever while the camera was silently blind - no
            // triggers, no snapshots, no clips, and not one log line to say so. Frames are expected
            // twice a second, so a gap of StallTimeout means the feed is gone whatever the process
            // says about itself.
            var stalledFor = existing.SinceLastFrame(DateTimeOffset.UtcNow);
            var stalled = stalledFor > existing.StallTimeout;

            // Whether this camera should be buffering video at all is part of its session's ffmpeg
            // argument list, so a change to it has to restart the session the same way a config edit
            // does - otherwise turning rewind on leaves every existing camera with nothing to search
            // until something else happens to restart it.
            var wantsBuffer = WantsSegmentBuffer(config, rewindOptions);

            if (ConfigsEquivalent(existing.Config, config) && existing.WantsSegmentBuffer == wantsBuffer
                && !existing.Process.HasExited && !stalled)
            {
                // Nothing to restart, but the rolling buffer still needs trimming or it would grow
                // without bound.
                PruneBufferFor(config, rewindOptions);
                existing.Rewind = rewindOptions;
                ReportSkippedFrames(config.CameraId, existing);
                return;
            }

            // Read before StopSession disposes the Process: afterwards HasExited throws
            // "No process is associated with this object", which used to abort the whole reconcile
            // for this camera - so the session was stopped, never restarted, and the camera stayed
            // dark until the next pass. It alternated blind and live every ten seconds, and the only
            // trace was one generic "Could not reconcile" line.
            var hadExited = existing.Process.HasExited;
            var stderrTail = existing.StderrTail;

            // A session that ever delivered a frame proves the camera is reachable, whatever has
            // happened since, so it clears any backoff accumulated by earlier failures.
            if (existing.EverDeliveredAFrame) _failedStarts.TryRemove(config.CameraId, out _);

            if (hadExited)
                LogExit(config.CameraId, stderrTail);
            else if (stalled)
                logger.LogWarning(
                    "Trigger zone detection for camera {CameraId} has delivered no frames for {Seconds:F0}s " +
                    "even though ffmpeg is still running - the stream has most likely gone half-open. " +
                    "Restarting the session.",
                    config.CameraId, stalledFor.TotalSeconds);

            _sessions.TryRemove(config.CameraId, out _);
            StopSession(existing);
            if (!wantsBuffer) clips.DiscardBuffer(config.CameraId);

            // A session that EXITED without ever producing a frame is the signature of a detection
            // URL this camera doesn't actually serve - see TryDeriveDetectionUrl - because ffmpeg
            // refuses an input it can't open and quits immediately. Remember that so the restart
            // decodes the main stream instead of crash-looping on the same guess.
            //
            // HasExited is load-bearing here, not decoration: a camera that is merely unreachable
            // at startup leaves ffmpeg alive and connecting, and the stall watchdog above restarts
            // it after 30s having learned nothing about its substream. Latching on that would
            // condemn a perfectly good camera to decoding its main stream at full rate for the life
            // of the process - exactly the standing-latency problem the substream exists to avoid.
            //
            // ...but only when ffmpeg's own account of the failure supports that reading. HasExited
            // was doing this job alone, and on this network it cannot: a camera that has simply
            // dropped off the LAN also exits immediately and also never delivers a frame, so an
            // outage was indistinguishable from a missing substream. It latched on the gate camera
            // at 08:53 on 2026-08-26 for exactly that reason - a transient failure twelve hours
            // before the camera actually went away - and quietly moved detection onto the 5MP main
            // stream for the rest of the process's life. See FfmpegExitReason for what each failure
            // really looks like.
            if (hadExited && existing.UsedDerivedDetectionUrl && !existing.EverDeliveredAFrame
                && FfmpegExitReason.IndicatesSubstreamMissing(stderrTail))
            {
                if (_noSubstream.TryAdd(config.CameraId, true))
                    logger.LogWarning(
                        "Camera {CameraId} never delivered a frame on its derived low-resolution stream and ffmpeg " +
                        "reported \"{Reason}\", so that stream probably isn't enabled on the camera. Falling back to " +
                        "the main stream for detection.",
                        config.CameraId, FfmpegExitReason.Describe(stderrTail));
            }

            // Count this as a failed start unless the session had actually got going. Done after the
            // latch above so that decision still sees the failure that produced it.
            if (!existing.EverDeliveredAFrame) RecordFailedStart(config.CameraId);
        }

        if (!MayStart(config.CameraId)) return;

        var camera = await cameras.GetAsync(config.CameraId, stoppingToken);
        if (camera is null) return;

        var session = StartSession(camera, config, multiFrame, rewindOptions);
        _sessions[config.CameraId] = session;
    }

    // How long to wait before restarting a camera whose sessions keep dying without ever producing a
    // frame. The first retry is immediate, so an ordinary blip - a camera rebooting, an encoder
    // settings change being applied, a brief network drop - recovers exactly as fast as it always
    // did. Only a camera that keeps failing is slowed, and never past a minute, so one that comes
    // back is picked up within a minute rather than needing the service restarted.
    //
    // Without this a camera that is simply gone is restarted every ReconcileInterval for as long as
    // it stays gone. Measured on 2026-08-27, with the gate camera off the network overnight: 3,338
    // ffmpeg processes and 3,338 identical warnings in 24 hours. The processes are wasted work and
    // the warnings are worse - that volume buries every unrelated fault in the journal.
    public static TimeSpan RestartDelayFor(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(20),
        3 => TimeSpan.FromSeconds(40),
        _ => TimeSpan.FromSeconds(60),
    };

    // A camera whose session keeps dying still has to say so, but not 360 times an hour. After the
    // first report it repeats at this interval with a count, which is enough to see the fault is
    // ongoing without drowning everything else.
    private static readonly TimeSpan RepeatFailureLogInterval = TimeSpan.FromMinutes(5);

    private sealed class FailedStarts
    {
        public int Count;
        public DateTimeOffset LastAttemptAt;
        public DateTimeOffset? LastLoggedAt;
        public int AttemptsSinceLastLog;
    }

    private readonly ConcurrentDictionary<Guid, FailedStarts> _failedStarts = new();

    // Says why the session died, which is the whole point: ffmpeg knew, and this used to throw the
    // answer away and report only that something had happened.
    private void LogExit(Guid cameraId, IReadOnlyList<string> stderrTail)
    {
        var record = _failedStarts.GetOrAdd(cameraId, _ => new FailedStarts());
        var now = DateTimeOffset.UtcNow;
        record.AttemptsSinceLastLog++;

        var due = record.LastLoggedAt is null || now - record.LastLoggedAt >= RepeatFailureLogInterval;
        if (!due) return;

        var reason = FfmpegExitReason.Describe(stderrTail) ?? "no output from ffmpeg";
        var attempts = record.AttemptsSinceLastLog;
        record.LastLoggedAt = now;
        record.AttemptsSinceLastLog = 0;

        if (attempts > 1)
            logger.LogWarning(
                "Trigger zone detection ffmpeg for camera {CameraId} has exited {Attempts} time(s) since the last " +
                "report, most recently: {Reason}. Starting a fresh session.",
                cameraId, attempts, reason);
        else
            logger.LogWarning(
                "Trigger zone detection ffmpeg for camera {CameraId} had exited ({Reason}); starting a fresh session.",
                cameraId, reason);
    }

    private void RecordFailedStart(Guid cameraId)
    {
        var record = _failedStarts.GetOrAdd(cameraId, _ => new FailedStarts());
        record.Count++;
        record.LastAttemptAt = DateTimeOffset.UtcNow;
    }

    private bool MayStart(Guid cameraId) =>
        !_failedStarts.TryGetValue(cameraId, out var record)
        || DateTimeOffset.UtcNow - record.LastAttemptAt >= RestartDelayFor(record.Count);

    // Frames the processing loop never saw, because capture had already replaced them - see
    // DetectionFrameSlot. Dropping is by design and costs sensitivity, never freshness, but it has
    // to be visible: a camera quietly discarding half its frames and one comfortably keeping up
    // behave identically from outside, and only one of them is worth investigating.
    //
    // Reported as a rate since the last pass rather than a running total, so the number describes
    // now instead of everything since the process started.
    private void ReportSkippedFrames(Guid cameraId, DetectionSession session)
    {
        var total = session.FramesSkipped;
        var since = total - session.SkippedAtLastReport;
        session.SkippedAtLastReport = total;

        if (since == 0) return;

        logger.LogInformation(
            "Trigger zone detection for camera {CameraId} skipped {Skipped} frame(s) in the last {Seconds:0}s to stay " +
            "on the live edge of the stream ({Total} in total for this session).",
            cameraId, since, ReconcileInterval.TotalSeconds, total);
    }

    // Clip recording and rewind both need the rolling segment buffer, for different reasons and on
    // their own schedules. Rewind only ever has to reach back over its lookback window; a clip has
    // to still hold its pre-roll when the build finally runs, a whole post-roll later.
    private static bool WantsSegmentBuffer(TriggerConfig config, TriggerRewindOptions rewind) =>
        config.RecordClips || (rewind.Enabled && rewind.BufferEveryCamera);

    private void PruneBufferFor(TriggerConfig config, TriggerRewindOptions rewind)
    {
        if (config.RecordClips)
        {
            // A clip being extended reaches back further than pre + post, and every clip in flight
            // needs its footage until its own concat has read it - so the oldest of them is the
            // floor. See StartOrExtendClip and ClipRecorder.
            DateTimeOffset? openClipFrom = null;
            lock (_openClipLock)
            {
                if (_clipsInFlight.TryGetValue(config.CameraId, out var inFlight))
                    foreach (var clip in inFlight)
                        if (openClipFrom is null || clip.From < openClipFrom) openClipFrom = clip.From;
            }

            clips.PruneBuffer(config.CameraId, config.ClipPreSeconds, config.ClipPostSeconds, openClipFrom);
        }
        else if (rewind.Enabled && rewind.BufferEveryCamera)
            clips.PruneBuffer(config.CameraId, rewind.LookbackSeconds + RewindBufferSlackSeconds);
    }

    // Enough slack over the lookback window to cover the wait for the segment holding the trigger to
    // close, plus the pass-to-pass gap between prunes.
    private const int RewindBufferSlackSeconds = 30;

    // The camera's low-resolution companion stream, or null when no safe derivation exists (a
    // camera whose URL doesn't follow a recognized pattern simply keeps decoding its main stream
    // for detection, exactly as before). Live view now downshifts the same way for its grid tiles,
    // so the rule itself lives in CameraSource and both callers read it from there.
    public static string? TryDeriveDetectionUrl(string mainUrl) => CameraSource.TryDeriveLowResUrl(mainUrl);

    private DetectionSession StartSession(
        Camera camera, TriggerConfig config, MultiFrameOptions multiFrame, TriggerRewindOptions rewindOptions)
    {
        var sourceUrl = cameraSource.ResolveUrl(camera);
        var cameraDir = Path.Combine(_imagesRoot, camera.Id.ToString("N"));
        Directory.CreateDirectory(cameraDir);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // The detector reads a downscaled raw feed; a second output continuously overwrites a
        // full-resolution JPEG from an already-open connection, so a trigger can grab an
        // up-to-date frame instantly (see MaybeTriggerAsync) instead of paying for a brand-new
        // camera connection per trigger; and when clips are enabled a third output copies the
        // original stream into a rolling segment buffer.
        //
        // When the camera offers a substream, detection decodes THAT and the main stream is
        // decoded at snapshot rate only. Decoding 5MP at full frame rate around the
        // clock isn't just wasted CPU - it's what made triggers save empty images: any moment
        // the box falls behind real time (a trigger being handled, the vision model pegging the
        // CPU) backs up into the camera's TCP send buffer, and a reliable transport like
        // Reolink's HTTP-FLV never drops a frame, so the backlog becomes STANDING latency on
        // the detection feed. The detector then sees a car seconds after it was really there,
        // and the (perfectly live) snapshot it grabs shows an empty road - which the vision
        // model then rightly rejects. A substream decodes an order of magnitude faster than
        // real time, so the pipeline reabsorbs any hiccup in moments instead of never.
        // The snapshot output only runs faster than 1fps when this camera can actually use the
        // extra frames (MultiFrameAnalysis on, and this zone forwards to the alert pipeline);
        // detection rides the substream either way.
        var snapshotFps = multiFrame.Enabled && config.SendToAlerts ? 3 : 1;
        var detectUrl = _noSubstream.ContainsKey(camera.Id) ? null : TryDeriveDetectionUrl(sourceUrl);
        var args = new List<string>();

        // RTSP-only option: passing it ahead of an http:// source (a Reolink's FLV stream, say)
        // makes ffmpeg exit immediately with "Option not found" - same guard as HlsStreamManager.
        if (detectUrl is not null)
        {
            if (HlsStreamManager.IsRtspUrl(detectUrl))
                args.AddRange(["-rtsp_transport", "tcp"]);
            args.AddRange(["-i", detectUrl]);
        }

        if (HlsStreamManager.IsRtspUrl(sourceUrl))
            args.AddRange(["-rtsp_transport", "tcp"]);
        args.AddRange(["-i", sourceUrl]);

        // -skip_frame nokey used to be applied to the main input whenever a substream carried
        // detection, on the reasoning that the snapshot only needs 1fps so keyframes would do. It
        // doesn't: the decoder then emits nothing but I-frames and -vf fps=1 pads the gaps by
        // repeating the last one, so _latest.jpg holds content up to a whole GOP old - commonly
        // 2-4 seconds, more on long-GOP encoders - while its mtime updates every second, which is
        // the only staleness signal anything downstream has. A person crossing the gate in three
        // seconds could therefore be detected correctly and still be handed to the AI as a
        // photograph of an empty driveway taken before they arrived, which the model then rightly
        // rejects. The trigger lands in the database as Rejected and never appears. Decoding the
        // main stream at 1fps costs a fraction of a core; a snapshot that doesn't match the moment
        // costs the entire event.
        //
        // Input 0 feeds detection and input 1 the snapshot when a substream exists; with a
        // single connection both come from input 0, exactly as before substreams were used.
        var mainInput = detectUrl is null ? "0" : "1";
        args.AddRange([
            "-map", "0:v:0",
            "-an",
            "-filter:v", BuildDetectionFilter(DetectionFps),
            "-f", "rawvideo",
            "-",
            "-map", $"{mainInput}:v:0",
            "-vf", $"fps={snapshotFps}",
            // Naming the JPEG's pixel format matters far more here than on a one-shot grab, and it
            // is not about the picture. mjpeg refuses a frame that is not full-range outright, and
            // when it does the encoder never opens - which fails THIS WHOLE PROCESS, taking
            // detection and the clip-segment buffer down with the snapshot, on a camera that was
            // working a frame earlier. The range comes from the SPS the camera emits, so a stream
            // that starts signalling limited range after re-initialising its encoder (a day/night
            // switch will do it) is enough. A yuvj format is full-range by definition, so the
            // conversion always happens and there is nothing left to refuse. Verified against the
            // real two-output command shape: forced to limited range without this flag, ffmpeg
            // exits 234 and writes neither output; with it, both are written unchanged.
            "-pix_fmt", "yuvj420p",
            "-update", "1",
            "-y", LatestSnapshotPath(camera.Id)
        ]);

        var wantsBuffer = WantsSegmentBuffer(config, rewindOptions);
        if (wantsBuffer)
        {
            PruneBufferFor(config, rewindOptions);
            args.AddRange([
                "-map", $"{mainInput}:v:0",
                "-map", $"{mainInput}:a?",
                "-c", "copy",
                "-f", "segment",
                "-segment_time", ClipRecorder.SegmentSeconds.ToString(),
                "-segment_format", "mpegts",
                "-reset_timestamps", "1",
                "-strftime", "1",
                clips.SegmentPattern(camera.Id)
            ]);
        }

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        logger.LogInformation("Started trigger zone detection for camera {CameraId} ({Name}), pid {Pid}", camera.Id, camera.Name, process.Id);

        var cts = new CancellationTokenSource();
        var session = new DetectionSession(
            process, config, cts, new MotionDetector(config.ResolvedZones, config.ShadowSensitivity), multiFrame,
            rewindOptions, wantsBuffer,
            detectUrl is not null)
        {
            StallTimeout = StallTimeoutFor(camera)
        };

        // A zone whose polygon has fewer than three points rasterizes to an empty mask, and an
        // empty mask can never contain a blob - that camera is silently blind for as long as the
        // config stands. It shouldn't get past validation, but a legacy row or a partially
        // migrated config can carry one, and the symptom (nothing ever triggers) looks identical
        // to a camera pointed at a quiet wall.
        foreach (var zone in config.ResolvedZones.Where(z => z.Polygon.Count < 3))
            logger.LogWarning(
                "Zone '{Zone}' on camera {CameraId} ({Name}) has fewer than 3 points, so it covers no pixels and " +
                "can never trigger. Redraw it in the trigger zone editor.",
                zone.Name, camera.Id, camera.Name);

        _ = RunDetectionLoopAsync(camera, session, cts.Token);
        _ = DrainAsync(process.StandardError, session);

        return session;
    }

    // Reads frames off ffmpeg's stdout and does nothing else with them, publishing each into a
    // one-deep slot for the processing loop below. Keeping this loop free of all work is the
    // difference between a camera that stays live and one that drifts permanently into the past.
    //
    // ffmpeg writes 172KB frames into a 64KB pipe, so it blocks the instant we stop reading - and
    // because it muxes every output from one thread, a blocked write also stops _latest.jpg being
    // refreshed and stops clip segments being written. Worse, the camera's transport (Reolink's
    // HTTP-FLV, and RTSP over TCP) never drops a frame, so whatever backs up during a stall is
    // still there afterwards: the queue is never paid back, and every frame from then on is late
    // by however long that one stall lasted. That is standing latency, and it is why a trigger
    // could fire three seconds after the car had gone.
    //
    // The slot is capacity 1 with DropOldest, so a processing loop that falls behind loses frames
    // instead of accumulating them. Dropping is the correct behaviour and not a compromise: a
    // motion detector wants the most recent view of the scene, never a queued stale one. This is
    // what ZoneMinder's analysis daemon does when it risks being overwritten by its capture daemon,
    // and what Frigate counts as skipped_fps - see MarkFrameSkipped.
    private async Task RunDetectionLoopAsync(Camera camera, DetectionSession session, CancellationToken cancellationToken)
    {
        var capture = CaptureFramesAsync(session, cancellationToken);
        var process = ProcessFramesAsync(camera, session, cancellationToken);
        await Task.WhenAll(capture, process);
    }

    private async Task CaptureFramesAsync(DetectionSession session, CancellationToken cancellationToken)
    {
        var stream = session.Process.StandardOutput.BaseStream;
        var buffer = session.RentFrameBuffer();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, buffer, cancellationToken)) break;

                // Stamped here, and this is the frame's capture time from now on. It is only
                // truthful because this loop never falls behind: with a queue in front of it the
                // same stamp would say when we got round to a frame, not when it was taken - which
                // is exactly the lie that made every trigger's saved image too late.
                var at = DateTimeOffset.UtcNow;

                // Also what tells the reconcile pass the feed is alive (see ReconcileCameraAsync):
                // "ffmpeg is still delivering", not "the last frame was fully handled".
                session.MarkFrameReceived(at);

                buffer = session.PublishFrame(buffer, at);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Trigger zone capture loop failed for camera {CameraId}", session.Config.CameraId);
        }
        finally
        {
            session.CompleteFrames();
        }
    }

    private async Task ProcessFramesAsync(Camera camera, DetectionSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (buffer, frameAt) in session.ReadFramesAsync(cancellationToken))
            {
                // A PTZ camera only detects while it is settled at home - see PtzActivityTracker.
                // The frame is still read and returned rather than skipped upstream, because the
                // capture loop hands over one frame at a time and would otherwise block.
                if (camera.PtzEnabled && !ptzActivity.IsSettledAtHome(camera.Id, PtzSettleDelay, out var suppressedBecause))
                {
                    session.ReturnFrameBuffer(buffer);
                    // Clearing the reference frame here rather than on the way back out means it is
                    // already gone by the time detection resumes, whichever frame that turns out to
                    // be - so the first live frame becomes the new baseline instead of being
                    // compared against the view from before the camera moved.
                    session.Detector.Rebaseline();
                    session.PtzSuppressed = true;
                    LogPtzSuppression(camera, session, suppressedBecause);
                    continue;
                }

                if (session.PtzSuppressed)
                {
                    session.PtzSuppressed = false;
                    session.PtzSuppressedFrames = 0;
                    logger.LogInformation(
                        "{Name} is back at its home position and settled; trigger zone detection has resumed.",
                        camera.Name);
                }

                double[]? blobFractions;
                try
                {
                    blobFractions = session.Detector.ProcessFrame(buffer);
                }
                finally
                {
                    session.ReturnFrameBuffer(buffer);
                }

                if (blobFractions is null) continue;

                // Each zone is judged against its own minimum size - that's the entire point of
                // having several. When more than one is over its own floor (an object crossing a
                // boundary, or overlapping zones), the one that cleared its threshold by the
                // largest margin wins, so a frame records one trigger rather than one per zone.
                var zones = session.Zones;
                var winner = -1;
                var bestOvershoot = 0.0;
                var bestNearMiss = 0.0;
                var nearMissZone = -1;
                for (var i = 0; i < zones.Count && i < blobFractions.Length; i++)
                {
                    if (zones[i].MinTriggerArea <= 0) continue;

                    var overshoot = blobFractions[i] / zones[i].MinTriggerArea;
                    if (blobFractions[i] < zones[i].MinTriggerArea)
                    {
                        // Remember the closest thing to a trigger this frame produced, for the
                        // near-miss log below.
                        if (overshoot > bestNearMiss) { bestNearMiss = overshoot; nearMissZone = i; }
                        continue;
                    }

                    if (overshoot <= bestOvershoot) continue;
                    bestOvershoot = overshoot;
                    winner = i;
                }

                if (winner < 0)
                {
                    LogNearMiss(camera, session, zones, blobFractions, nearMissZone, bestNearMiss);
                    continue;
                }

                // Read out before the hand-off below: ProcessFrame returns a scratch array it reuses
                // on the next frame, so anything running after this iteration must not be holding
                // a reference to it.
                var triggerArea = blobFractions[winner];
                var triggerZoneName = zones[winner].Name;

                // Still not awaited, though for a smaller reason now that capture is a separate
                // loop: handling a trigger takes seconds (SQLite writes behind a shared semaphore,
                // a companion capture per group member, a frame pulled out of the buffer), and
                // blocking here would stall detection for that whole time. The per-session gate
                // below is what stops slow handlers piling up.
                //
                session.HandleTriggerInBackground(() =>
                    MaybeTriggerAsync(camera, session, triggerArea, triggerZoneName, frameAt, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Trigger zone detection loop ended for camera {CameraId}", camera.Id);
        }
    }

    // Hands back the cooldown and the per-minute slot that MaybeTriggerAsync reserved before it
    // knew whether it could actually capture anything. Only ever called when nothing was recorded,
    // so there is no event for either limit to be protecting against.
    private static void ReleaseTriggerSlot(DetectionSession session, DateTimeOffset reservedAt)
    {
        lock (session.TriggerLock)
        {
            // Only if nothing else has triggered since - otherwise this would clear a cooldown that
            // a later, successful trigger is legitimately holding.
            if (session.LastTriggerAt == reservedAt) session.LastTriggerAt = null;

            // The queue is FIFO and this reservation was the most recent enqueue, so the last item
            // is ours if it's still there at all.
            if (session.RecentTriggers.Count > 0)
            {
                var kept = session.RecentTriggers.Where(t => t != reservedAt).ToList();
                if (kept.Count != session.RecentTriggers.Count)
                {
                    session.RecentTriggers.Clear();
                    foreach (var stamp in kept) session.RecentTriggers.Enqueue(stamp);
                }
            }
        }
    }

    // The single hardest thing to diagnose about this pipeline is a zone whose minimum size is set
    // just above what real subjects actually measure: nothing triggers, nothing is logged, and the
    // camera looks broken rather than mis-tuned. That threshold is genuinely hard to set by eye,
    // because the editor asks you to size the OBJECT while the detector measures the CHANGED
    // pixels between two frames half a second apart - which for a person walking is a good deal
    // smaller than their silhouette, and smaller again once shadow suppression has had its say.
    //
    // So: when motion gets close to a zone's floor without clearing it, say so, in the same units
    // the editor shows. Rate-limited to once a minute per camera because at 2fps an unlucky
    // afternoon would otherwise write thousands of lines.
    private void LogNearMiss(
        Camera camera, DetectionSession session, IReadOnlyList<TriggerZone> zones,
        double[] blobFractions, int zoneIndex, double ratio)
    {
        if (zoneIndex < 0 || ratio < NearMissRatio) return;
        if (!logger.IsEnabled(LogLevel.Information)) return;

        var now = DateTimeOffset.UtcNow;
        lock (session.TriggerLock)
        {
            if (session.LastNearMissLogAt is { } last && now - last < NearMissLogInterval) return;
            session.LastNearMissLogAt = now;
        }

        var measured = blobFractions[zoneIndex] * MotionDetector.PixelCount;
        var threshold = zones[zoneIndex].MinTriggerArea * MotionDetector.PixelCount;
        logger.LogInformation(
            "Camera {CameraId} ({Name}): motion in zone '{Zone}' measured {Measured:F0} detector pixels against a " +
            "minimum of {Threshold:F0} ({Ratio:P0} of the way there), so it did not trigger. If real subjects keep " +
            "landing here, lower this zone's minimum object size.",
            camera.Id, camera.Name, zones[zoneIndex].Name, measured, threshold, ratio);
    }

    // A camera that has stopped triggering is indistinguishable from a camera that has broken, and
    // this feature deliberately makes one look like the other for ten hours a night. So say so -
    // with the count held back and the hour it resumes, so the log answers "is it off or is it
    // dead?" without anyone having to remember the setting exists.
    //
    // Rate-limited like LogNearMiss, and for the same reason: the flood this suppresses is roughly
    // a hundred triggers a night, and a line each would bury everything else in the journal.
    // Rate-limited like LogNightSuppression, and for the same reason - but this one matters more,
    // because a camera left pointed away is not detecting and nothing else on screen says so.
    private void LogPtzSuppression(Camera camera, DetectionSession session, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        int skipped;
        lock (session.TriggerLock)
        {
            session.PtzSuppressedFrames++;
            if (session.LastPtzLogAt is { } last && now - last < PtzSuppressionLogInterval) return;
            session.LastPtzLogAt = now;
            skipped = session.PtzSuppressedFrames;
        }

        logger.LogInformation(
            "Trigger zone detection for {Name} is paused because {Reason}; {Skipped} frame(s) not examined since the last note.",
            camera.Name, reason, skipped);
    }

    private void LogNightSuppression(Camera camera, DetectionSession session, DateTimeOffset now)
    {
        int suppressed;
        lock (session.TriggerLock)
        {
            session.NightSuppressedCount++;
            if (session.LastNightLogAt is { } last && now - last < NightSuppressionLogInterval) return;
            session.LastNightLogAt = now;
            suppressed = session.NightSuppressedCount;
            session.NightSuppressedCount = 0;
        }

        logger.LogInformation(
            "Camera {CameraId} ({Name}): motion triggers are paused by the night window ({Start:00}:00-{End:00}:00) " +
            "and resume at {Resumes:HH:mm}. {Suppressed} trigger(s) held back since the last note.",
            camera.Id, camera.Name, session.Config.NightStartHour, session.Config.NightEndHour,
            TriggerNightWindow.EndsAt(session.Config, now).ToLocalTime(), suppressed);
    }

    // frameAt is when the frame that fired this was captured, not when we got round to handling it.
    // Everything downstream is anchored to it - the image pulled from the buffer, the trigger row,
    // the clip window, the alert - so a single moment describes the event no matter how busy the
    // machine was in between. Reserving the cooldown against it rather than against "now" also
    // keeps the rate limits measuring the scene rather than our own scheduling.
    private async Task MaybeTriggerAsync(
        Camera camera, DetectionSession session, double areaFraction, string zoneName,
        DateTimeOffset frameAt, CancellationToken cancellationToken)
    {
        var now = frameAt;

        // Before the cooldown reservation, not after: a suppressed trigger should not consume a
        // rate-limit slot that a real one might want the moment the window closes. This is also
        // the earliest point where dropping out costs nothing - the frame diff has already been
        // computed either way, but the snapshot, the clip, the database row and the forward to the
        // AI pipeline are all still ahead of us, and those are the expensive parts.
        if (TriggerNightWindow.IsActive(session.Config, now))
        {
            LogNightSuppression(camera, session, now);
            return;
        }

        lock (session.TriggerLock)
        {
            if (session.LastTriggerAt is { } last && (now - last).TotalSeconds < session.Config.CooldownSeconds)
                return;

            while (session.RecentTriggers.Count > 0 && (now - session.RecentTriggers.Peek()).TotalMinutes >= 1)
                session.RecentTriggers.Dequeue();

            if (session.RecentTriggers.Count >= session.Config.MaxTriggersPerMinute)
                return;

            session.LastTriggerAt = now;
            session.RecentTriggers.Enqueue(now);
        }

        try
        {
            // The rewind, when it's on, is the only source that can be OLDER than this moment - and
            // being older is the entire point, because the detection feed that produced this trigger
            // may be running seconds behind the camera. Both fallbacks below capture "now", which is
            // what every trigger used to get.
            var pick = session.Rewind.Enabled
                ? await rewind.TryFetchAsync(camera.Id, frameAt, session.Rewind.LookbackSeconds, cancellationToken)
                : null;

            var bytes = pick?.ImageBytes
                ?? await TryReadLatestSnapshotAsync(camera.Id, cancellationToken)
                ?? await streams.CaptureSnapshotAsync(camera, cancellationToken);
            if (bytes is null)
            {
                // Nothing threw, so the catch below never sees this - and before it was logged,
                // a camera that had stopped handing over frames produced no trigger, no row and
                // not one line anywhere saying why. Give the cooldown and rate-limit slots back
                // too: they were reserved for a trigger that never happened, and holding them
                // means the next few seconds of genuine motion are suppressed on the strength of
                // a failure.
                logger.LogWarning(
                    "Motion fired for camera {CameraId} ({Name}) in zone {Zone}, but no snapshot could be captured, " +
                    "so no event was recorded.",
                    camera.Id, camera.Name, zoneName);
                ReleaseTriggerSlot(session, now);
                return;
            }

            var cameraDir = Path.Combine(_imagesRoot, camera.Id.ToString("N"));
            Directory.CreateDirectory(cameraDir);
            var imagePath = Path.Combine(cameraDir, $"{now:yyyyMMdd-HHmmss-fff}.jpg");
            await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken);

            // A camera belongs to at most one group (enforced at save time - see
            // CameraGroupEndpoints). Reuse the group's already-open incident if another member
            // triggered within its correlation window, otherwise this is the start of a new one.
            var group = await groupStore.GetGroupForCameraAsync(camera.Id, cancellationToken);
            string? incidentId = null;
            if (group is not null)
            {
                var since = now.AddSeconds(-group.CorrelationWindowSeconds);
                incidentId = await eventStore.GetMostRecentIncidentIdAsync(group.CameraIds, since, cancellationToken)
                    ?? Guid.NewGuid().ToString("N");
            }

            // Held back from the Triggers tab until the analysis this is about to be forwarded for
            // says there was really something in frame - see TriggerConfirmationService. Only ever
            // gated when it's actually going to be analyzed; a camera that doesn't forward to
            // alerts records Confirmed and shows up straight away, exactly as before.
            var gated = await triggerConfirmation.ShouldGateAsync(session.Config.SendToAlerts, cancellationToken);
            var trigger = await eventStore.AddAsync(
                camera.Id, imagePath, areaFraction, incidentId,
                gated ? TriggerConfirmation.Pending : TriggerConfirmation.Confirmed, cancellationToken, zoneName,
                pick?.CaptureDelaySeconds, occurredAt: now);
            logger.LogInformation(
                "Trigger zone fired for camera {CameraId} ({Name}) in zone {Zone}, area {Area:P1}{Fetched}{Held}",
                camera.Id, camera.Name, zoneName, areaFraction,
                pick is null ? "" : $" - image is the detected frame, fetched {pick.CaptureDelaySeconds:0.0}s later",
                gated ? " - held for AI confirmation" : "");

            if (session.Config.RecordClips)
                StartOrExtendClip(camera, trigger, session.Config, now);

            // Something is in the zone, so there is something to follow. No-op unless PTZ tracking
            // is switched on, which it is not by default. Deliberately after the trigger has been
            // recorded: the still and the clip describe the camera's home view, which is the frame
            // the zone was drawn against.
            //
            // Not part of the branch above, despite once being indented as though it were: whether
            // to follow a subject has nothing to do with whether clips are being recorded.
            ptzTracking.Start(camera);

            // Fast (file reads/writes and SQLite inserts, no network/wait), so it's fine to await
            // inline here like the rest of this method - only each companion's actual clip build
            // (comparatively slow, waits out the post-roll) is fire-and-forget, same as the
            // triggering camera's own clip above.
            var companionAt = CompanionCaptureAt(pick, session.Rewind.ApplyToCompanions);
            var linkedCameraNames = group is { RecordCompanionClips: true } or { AnalyzeCompanionCaptures: true }
                ? await RecordCompanionCamerasAsync(camera, group, incidentId!, now, companionAt, cancellationToken)
                : [];

            if (session.Config.SendToAlerts)
                _ = ForwardToAlertPipelineAsync(
                    camera, bytes, session.MultiFrame, session.Config.AlertProfile, now, linkedCameraNames, incidentId,
                    trigger.Id, gated, pick);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save trigger snapshot for camera {CameraId}", camera.Id);
        }
    }

    // The instant a companion's frame is fetched for, or null to leave it on the live snapshot.
    //
    // A companion has no motion of its own to date the event by, so it borrows the instant from the
    // camera that did fire - and borrowing an instant means using it, not adjusting it. Both ends of
    // the lookup are stamped by this server rather than by the cameras (frameAt by the detection
    // capture loop, the segment boundaries by ffmpeg's strftime), so it is one clock throughout and
    // the instant carries from one camera to another unchanged. There is nothing to correct for.
    //
    // Reading it off the pick rather than recomputing it is the point: `pick.ChosenAt` IS the frame
    // the triggering camera's own image was taken from, so a companion is aligned to that same
    // frame by construction, with no arithmetic in between that could drift.
    //
    // It used to be `occurredAt - pick.CaptureDelaySeconds`, which was right when that number meant
    // "how far back this reached from now". Since a trigger's OccurredAt became the frame's own
    // capture time the number means how long the FETCH took - dominated by waiting for the muxer to
    // close the segment holding the frame - so subtracting it pulled every companion that far back
    // BEFORE the moment: across the 1,708 rewound triggers in the last week here, a mean of 2.32s
    // and 1-3s on 96% of them. The setting's own description ("Fetch the same moment from the other
    // cameras") described what was intended rather than what happened.
    //
    // Null when the triggering camera has no pick, and deliberately so even though `occurredAt`
    // would still name a real instant: TryFetchAsync is where a wildly wrong timestamp gets caught
    // (a clock step, a camera reconnecting - see its lookback check), and TryExtractAtAsync has no
    // such guard of its own. A pick is the proof that this instant was sane and the buffer could
    // serve it; without one, the companions keep the honest live snapshot.
    public static DateTimeOffset? CompanionCaptureAt(RewindPick? pick, bool applyToCompanions) =>
        applyToCompanions && pick is not null ? pick.ChosenAt : null;

    // Captures each OTHER group member's frame for that instant and records it as its own
    // TriggerEvent under the shared incident, so the Triggers tab shows footage from the whole
    // group even though only one camera actually detected motion. A member with no active buffer
    // (no enabled trigger zone of its own) is silently skipped - nothing to pull from. Returns the
    // names of cameras actually recorded, for the "Also recorded: ..." notification annotation.
    private async Task<List<string>> RecordCompanionCamerasAsync(
        Camera triggeringCamera, CameraGroup group, string incidentId, DateTimeOffset occurredAt,
        DateTimeOffset? rewindAt, CancellationToken cancellationToken)
    {
        var recorded = new List<string>();
        foreach (var memberId in group.CameraIds)
        {
            if (memberId == triggeringCamera.Id) continue;

            try
            {
                var memberCamera = await cameras.GetAsync(memberId, cancellationToken);
                if (memberCamera is null) continue;

                // The same instant the triggering camera's own image is of - see
                // CompanionCaptureAt. A member whose buffer cannot serve it (no segment covering
                // that moment, or the segment holding it is still being written) returns null and
                // falls through to the live snapshot, which is a picture of a later moment but an
                // honest one.
                var snapshot = (rewindAt is { } at
                        ? await rewind.TryExtractAtAsync(memberId, at, cancellationToken)
                        : null)
                    ?? await TryReadLatestSnapshotAsync(memberId, cancellationToken);
                if (snapshot is null) continue;

                var memberDir = Path.Combine(_imagesRoot, memberId.ToString("N"));
                Directory.CreateDirectory(memberDir);
                var imagePath = Path.Combine(memberDir, $"{occurredAt:yyyyMMdd-HHmmss-fff}.jpg");
                await File.WriteAllBytesAsync(imagePath, snapshot, cancellationToken);

                // A companion is only ever analyzed when the group asks for it, so that's the only
                // case where its capture can be AI-confirmed; otherwise it records Confirmed and
                // stays visible alongside the rest of its incident.
                var companionGated = group.AnalyzeCompanionCaptures &&
                    await triggerConfirmation.ShouldGateAsync(true, cancellationToken);

                // No self-detected motion area for a companion capture - 0 just means "not
                // self-triggered", not "no motion visible".
                var companionTrigger = await eventStore.AddAsync(
                    memberId, imagePath, 0, incidentId,
                    companionGated ? TriggerConfirmation.Pending : TriggerConfirmation.Confirmed, cancellationToken,
                    occurredAt: occurredAt);
                recorded.Add(memberCamera.Name);
                logger.LogInformation(
                    "Recorded companion capture for camera {CameraId} ({Name}), incident {IncidentId}",
                    memberId, memberCamera.Name, incidentId);

                var memberConfig = await configStore.GetAsync(memberId, cancellationToken);
                // Same merging as the triggering camera's own clip: a group that fires twice in
                // quick succession gives each companion one clip of the incident, not two.
                if (group.RecordCompanionClips && memberConfig is { RecordClips: true })
                    StartOrExtendClip(memberCamera, companionTrigger, memberConfig, occurredAt);

                // Off by default (see CameraGroupInputDto) - every companion also gets its own
                // full AI analysis/Feed entry/notification, tagged with the same incident id.
                // Uses the companion's own prompt profile (already exists per-camera regardless
                // of that camera's own SendToAlerts) - no burst/multi-frame capture for this
                // secondary path, to keep the extra cost predictable at exactly one call.
                if (group.AnalyzeCompanionCaptures)
                {
                    var alertProfile = memberConfig?.AlertProfile ?? "default";
                    _ = ForwardToAlertPipelineAsync(
                        memberCamera, snapshot, new MultiFrameOptions(), alertProfile, occurredAt, [], incidentId,
                        companionTrigger.Id, companionGated);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record a companion capture for camera {CameraId} in incident {IncidentId}", memberId, incidentId);
            }
        }

        return recorded;
    }

    // Fire-and-forget like BuildClipAsync, and for the same reason: not tied to the detection
    // session's token, so a config change or the camera being disabled a moment later doesn't
    // cancel an alert that's already in flight. Capturing extra frames here (rather than inline
    // in MaybeTriggerAsync) keeps the ~0.5-1s of extra delay off the critical path that saves the
    // trigger's primary image and records the TriggerEvent - the Feed/Timeline see no difference.
    //
    // triggerId is the trigger this forward came from - always set, so the alert it becomes can be
    // recorded against it and the Events tab can show the motion and its analysis as one card.
    // gated additionally says that trigger is sitting Pending waiting on this analysis, in which
    // case the verdict is followed up (and the trigger released, one way or the other) once the
    // alert has been queued.
    private async Task ForwardToAlertPipelineAsync(
        Camera camera, byte[] imageBytes, MultiFrameOptions multiFrame, string profile, DateTimeOffset occurredAt,
        IReadOnlyList<string> linkedCameraNames, string? incidentId, Guid triggerId, bool gated,
        RewindPick? pick = null)
    {
        try
        {
            var extraFrames = new List<byte[]>();
            if (multiFrame.Enabled)
            {
                for (var i = 0; i < multiFrame.ExtraFrameCount; i++)
                {
                    // Rewound: the chosen frame is seconds in the past, so its successors are
                    // already on disk. That both removes this loop's dead wait and makes the burst
                    // straddle the subject - reading the live snapshot instead walks forward from a
                    // moment that was already too late, which is the original bug, compounded.
                    byte[]? frame;
                    if (pick is { } rewound)
                    {
                        var at = rewound.ChosenAt + TimeSpan.FromMilliseconds(multiFrame.FrameIntervalMs * (i + 1));
                        frame = await rewind.TryExtractAtAsync(camera.Id, at, _stoppingToken);
                    }
                    else
                    {
                        await Task.Delay(multiFrame.FrameIntervalMs, _stoppingToken);
                        frame = await TryReadLatestSnapshotAsync(camera.Id, _stoppingToken);
                    }

                    if (frame is not null) extraFrames.Add(frame);
                }
            }

            var forwarded = await alertForwarder.ForwardAsync(
                imageBytes, extraFrames, camera.Name, profile, occurredAt, linkedCameraNames, incidentId, _stoppingToken);

            // Recorded before the verdict is waited on, and regardless of gating: the join is what
            // the Events tab renders from, and a refused forward simply leaves it null.
            if (forwarded.AlertId is { } alertId)
                await eventStore.SetAlertIdAsync(triggerId, alertId, _stoppingToken);

            if (gated)
                await triggerConfirmation.ResolveAsync(triggerId, forwarded, _stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to forward trigger to the alert pipeline for camera {CameraId}", camera.Id);

            // The forward itself blew up, so no verdict is coming for a trigger that's hidden
            // waiting on one. Release it rather than let the startup sweep find it minutes later.
            if (gated)
                await triggerConfirmation.ResolveAsync(triggerId, AlertForwardResult.Refused, CancellationToken.None);
        }
    }

    // A trigger while a clip is already recording extends that clip instead of starting a second
    // one. Somebody arriving fires the zone repeatedly - the vehicle stops outside, pulls in, a
    // door opens, they walk to the gate - and with a 5s cooldown against a 5s pre-roll and a 10s
    // post-roll, that produced three clips of a 30-second arrival that were mostly copies of each
    // other, and three cards in the Feed that each read like a separate visit. One clip of the
    // whole arrival is both the smaller thing on disk and the truer description of what happened.
    private void StartOrExtendClip(Camera camera, TriggerEvent trigger, TriggerConfig config, DateTimeOffset triggeredAt)
    {
        OpenClip fresh;

        lock (_openClipLock)
        {
            // Only the newest can still be extended - anything before it in the list is sealed and
            // is only still here because its footage has to survive until its build has read it.
            if (Newest(camera.Id) is { } open &&
                open.TryExtend(trigger.Id, triggeredAt, config.ClipPreSeconds, config.ClipPostSeconds, MaxExtendedClipSeconds))
            {
                logger.LogInformation(
                    "Trigger on camera {CameraId} ({Name}) landed inside the clip already recording, so that clip now " +
                    "runs to {End:HH:mm:ss} and covers {Count} trigger(s) instead of a second clip starting",
                    camera.Id, camera.Name, open.To.ToLocalTime(), open.TriggerCount);
                return;
            }

            fresh = new OpenClip(
                triggeredAt,
                triggeredAt.AddSeconds(-config.ClipPreSeconds),
                triggeredAt.AddSeconds(config.ClipPostSeconds),
                trigger.Id);

            if (!_clipsInFlight.TryGetValue(camera.Id, out var inFlight))
                _clipsInFlight[camera.Id] = inFlight = [];

            inFlight.Add(fresh);
        }

        // Started outside the lock: the build takes the same lock on its first line, and running
        // it inline here would have it reach that lock re-entrantly on this thread, which works
        // only by accident of Monitor's reentrancy.
        _ = BuildClipAsync(camera, fresh);
    }

    // The longest a clip may grow by extension. Motion at a gate can keep firing for minutes, and
    // without a ceiling "the clip" would become an open-ended recording whose start the rolling
    // buffer could no longer hold. Past this the next trigger opens a fresh clip, which is the old
    // behaviour and a reasonable place to break a long event in two.
    private const int MaxExtendedClipSeconds = 90;

    private readonly object _openClipLock = new();

    // Per camera, every clip whose footage still has to be on disk: the one currently open for
    // extension, plus any that have been sealed but whose ffmpeg concat has not finished reading
    // the buffer yet. A list rather than a single entry because the two are not the same lifetime,
    // and conflating them let PruneBufferFor drop its protection the moment a clip was sealed -
    // which for a clip extended past pre + post + slack meant the reconcile pass could delete the
    // front of it in the second between sealing and building, silently producing a short clip.
    private readonly Dictionary<Guid, List<OpenClip>> _clipsInFlight = [];

    // Caller must hold _openClipLock.
    private OpenClip? Newest(Guid cameraId) =>
        _clipsInFlight.TryGetValue(cameraId, out var inFlight) && inFlight.Count > 0
            ? inFlight[^1]
            : null;

    // Deliberately not tied to the detection session's token: the post-roll wait outlives a config
    // change or a camera being disabled, and the clip is still worth writing when that happens.
    private async Task BuildClipAsync(Camera camera, OpenClip open)
    {
        try
        {
            // Wait for the post-roll itself plus one segment, so the segment covering the end of
            // the window has been closed off by ffmpeg before we try to concatenate it. Looped
            // rather than a single delay because a trigger arriving during the wait pushes the end
            // of the window out, and waking up to build against the old end would cut the clip
            // short at exactly the moment something else was happening.
            while (true)
            {
                DateTimeOffset until;
                lock (_openClipLock) until = open.To;

                var wait = until.AddSeconds(ClipRecorder.SegmentSeconds + 1) - DateTimeOffset.UtcNow;
                if (wait <= TimeSpan.Zero) break;

                await Task.Delay(wait, _stoppingToken);
            }

            // From here the window is fixed, so a trigger arriving now opens the next clip rather
            // than moving footage out from under the concat about to read it.
            DateTimeOffset anchoredAt, from, to;
            Guid[] triggerIds;
            lock (_openClipLock)
            {
                open.Seal();
                anchoredAt = open.AnchoredAt;
                from = open.From;
                to = open.To;
                triggerIds = open.TriggerIds;
            }

            var clipPath = await clips.BuildClipAsync(camera.Id, anchoredAt, from, to, _stoppingToken);
            if (clipPath is null) return;

            // Every trigger that landed in the window points at the one file, so opening any of
            // their cards plays the whole arrival rather than that trigger's slice of it.
            foreach (var id in triggerIds)
                await eventStore.SetClipPathAsync(id, clipPath, _stoppingToken);

            logger.LogInformation(
                "Saved a {Seconds:F0}s trigger clip for camera {CameraId} ({Name}) covering {Count} trigger(s)",
                (to - from).TotalSeconds, camera.Id, camera.Name, triggerIds.Length);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build trigger clip for camera {CameraId}", camera.Id);
        }
        finally
        {
            // In a finally, and covering cancellation, because a clip left in here would hold this
            // camera's whole rolling buffer on disk forever and - while it was still the newest -
            // swallow every later trigger into a window nothing would ever build.
            lock (_openClipLock)
            {
                if (_clipsInFlight.TryGetValue(camera.Id, out var inFlight))
                {
                    inFlight.Remove(open);
                    if (inFlight.Count == 0) _clipsInFlight.Remove(camera.Id);
                }
            }
        }
    }

    // One clip being recorded, from the trigger that opened it until the post-roll after the last
    // trigger that extended it. Every member is guarded by TriggerZoneMonitor._openClipLock: the
    // detection loop extends it while the build task is asleep waiting on it.
    //
    // Public for the same reason ClipRecorder.SelectSegmentsCovering is: TryExtend is the rule
    // that decides whether an arrival becomes one clip or several, and it is worth pinning
    // directly rather than through a running ffmpeg.
    public sealed class OpenClip(DateTimeOffset anchoredAt, DateTimeOffset from, DateTimeOffset to, Guid firstTriggerId)
    {
        private readonly List<Guid> _triggerIds = [firstTriggerId];

        // Names the clip file, and stays the moment the clip opened however far the end moves.
        public DateTimeOffset AnchoredAt { get; } = anchoredAt;
        public DateTimeOffset From { get; } = from;
        public DateTimeOffset To { get; private set; } = to;

        public int TriggerCount => _triggerIds.Count;
        public Guid[] TriggerIds => [.. _triggerIds];

        // Set once the build has stopped waiting and is about to read the buffer. After that the
        // window is what gets concatenated, so it must not move.
        public bool IsSealed { get; private set; }

        public void Seal() => IsSealed = true;

        public bool TryExtend(Guid triggerId, DateTimeOffset at, int preSeconds, int postSeconds, int maxSeconds)
        {
            if (IsSealed) return false;

            // The union has to stay contiguous - a clip with a hole in the middle is worse than
            // two clips. What has to overlap is the footage, not the trigger moments: this
            // trigger's own pre-roll reaches backwards, so it can land after the current end and
            // still join on seamlessly. Testing `at` alone instead is what an earlier version did,
            // and it refused to merge the exact case this exists for - triggers at +0s, +5s and
            // +17s against a 10s post-roll, where the third is two seconds past an end its own
            // five-second pre-roll already covers.
            if (at.AddSeconds(-preSeconds) > To) return false;

            var end = at.AddSeconds(postSeconds);
            if ((end - From).TotalSeconds > maxSeconds) return false;

            if (end > To) To = end;
            _triggerIds.Add(triggerId);
            return true;
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    // ffmpeg's stderr still has to be read continuously whatever we do with it - it writes progress
    // there for the life of the session, and a full pipe would block the process that is feeding
    // detection. The difference now is that the last few lines are kept rather than dropped, so a
    // session that dies can be asked why.
    private static async Task DrainAsync(StreamReader reader, DetectionSession session)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) session.RecordStderr(line);
        }
        catch
        {
            // process exited
        }
    }

    private void StopSession(DetectionSession session)
    {
        try
        {
            session.Cts.Cancel();
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
            session.Cts.Dispose();
        }
    }

    // Half a detector pixel: small enough that two areas compare equal only when they round to the
    // same threshold, which is the only difference this monitor can act on. It used to be a flat
    // 0.0001, which is 5.8 detector pixels - fine when the editors couldn't express a size below
    // ~250 pixels, and quietly wrong now that they go down to 4: every edit in the small range fell
    // inside the tolerance, so the reconcile loop saw "no change", left the running session on its
    // old threshold, and the setting only took effect at the next restart.
    private const double MinTriggerAreaEpsilon = 0.5 / MotionDetector.PixelCount;

    // Public so the zone comparison can be tested directly - this project has no
    // InternalsVisibleTo, and a field missed in here is a silently ignored edit, not a crash.
    public static bool ConfigsEquivalent(TriggerConfig a, TriggerConfig b)
    {
        if (a.Enabled != b.Enabled) return false;
        if (a.CooldownSeconds != b.CooldownSeconds) return false;
        if (a.MaxTriggersPerMinute != b.MaxTriggersPerMinute) return false;
        if (a.ShadowSensitivity != b.ShadowSensitivity) return false;
        if (a.RecordClips != b.RecordClips) return false;
        if (a.ClipPreSeconds != b.ClipPreSeconds) return false;
        if (a.ClipPostSeconds != b.ClipPostSeconds) return false;
        if (a.SendToAlerts != b.SendToAlerts) return false;
        if (a.AlertProfile != b.AlertProfile) return false;

        // The running session holds its own copy of the config, so leaving these out would mean
        // editing the window in the UI did nothing until the next restart - and the symptom would
        // be a camera that keeps flooding at 3am after you just told it not to.
        if (a.NightWindowEnabled != b.NightWindowEnabled) return false;
        if (a.NightStartHour != b.NightStartHour) return false;
        if (a.NightEndHour != b.NightEndHour) return false;

        // The whole zone list, not just the primary polygon: this is what decides whether the
        // reconcile loop tears down and rebuilds the detection session, so a field missed here is
        // an edit the user makes in the UI that silently never takes effect until the next restart.
        // ResolvedZones on both sides so a legacy config compares equal to its migrated self.
        var zonesA = a.ResolvedZones;
        var zonesB = b.ResolvedZones;
        if (zonesA.Count != zonesB.Count) return false;

        for (var zone = 0; zone < zonesA.Count; zone++)
        {
            if (zonesA[zone].Name != zonesB[zone].Name) return false;
            if (Math.Abs(zonesA[zone].MinTriggerArea - zonesB[zone].MinTriggerArea) > MinTriggerAreaEpsilon) return false;

            var polygonA = zonesA[zone].Polygon;
            var polygonB = zonesB[zone].Polygon;
            if (polygonA.Count != polygonB.Count) return false;

            for (var i = 0; i < polygonA.Count; i++)
            {
                if (Math.Abs(polygonA[i].X - polygonB[i].X) > 0.0001) return false;
                if (Math.Abs(polygonA[i].Y - polygonB[i].Y) > 0.0001) return false;
            }
        }

        return true;
    }

    private string LatestSnapshotPath(Guid cameraId) =>
        TriggerImagePaths.LatestSnapshot(_imagesRoot, cameraId);

    // No maxAge: this session is the thing writing the file, so it can't be reading a leftover
    // from a zone that was switched off - see the note on TriggerImagePaths.TryReadLatestAsync.
    private Task<byte[]?> TryReadLatestSnapshotAsync(Guid cameraId, CancellationToken cancellationToken) =>
        TriggerImagePaths.TryReadLatestAsync(_imagesRoot, cameraId, maxAge: null, cancellationToken);

    private static string ResolveImagesRoot(IConfiguration configuration, IHostEnvironment environment) =>
        TriggerImagePaths.ResolveRoot(configuration, environment);

    private sealed class DetectionSession(
        Process process, TriggerConfig config, CancellationTokenSource cts, MotionDetector detector,
        MultiFrameOptions multiFrame, TriggerRewindOptions rewind, bool wantsSegmentBuffer,
        bool usedDerivedDetectionUrl)
    {
        public Process Process { get; } = process;
        public TriggerConfig Config { get; } = config;
        public CancellationTokenSource Cts { get; } = cts;
        public MotionDetector Detector { get; } = detector;
        public MultiFrameOptions MultiFrame { get; } = multiFrame;

        // Settable, unlike the rest: everything about rewind except whether to keep a buffer takes
        // effect on the next trigger, so the reconcile pass refreshes it in place rather than
        // tearing down a working ffmpeg to change a lookback window.
        public TriggerRewindOptions Rewind { get; set; } = rewind;

        // Whether this session's ffmpeg was started with the segment output. Compared on every
        // reconcile because it is baked into the argument list - see ReconcileCameraAsync.
        public bool WantsSegmentBuffer { get; } = wantsSegmentBuffer;

        // Whether this session's detection input was guessed from the main stream URL rather than
        // being the URL itself - see TryDeriveDetectionUrl and the fallback in ReconcileCameraAsync.
        public bool UsedDerivedDetectionUrl { get; } = usedDerivedDetectionUrl;

        // How long this session may go frameless before it is treated as dead. Carried here rather
        // than looked up per pass because the reconcile pass tests it before it has loaded the
        // camera - and because it is a property of the source this ffmpeg was started against.
        public TimeSpan StallTimeout { get; init; } = TriggerZoneMonitor.StallTimeout;

        // Resolved once rather than per frame: for a config predating multi-zone, ResolvedZones
        // builds a fresh list every time it's read, and the detection loop reads it twice a second
        // for the life of the session.
        public IReadOnlyList<TriggerZone> Zones { get; } = config.ResolvedZones;

        public readonly object TriggerLock = new();
        public DateTimeOffset? LastTriggerAt;
        public DateTimeOffset? LastNearMissLogAt;
        public DateTimeOffset? LastNightLogAt;
        public int NightSuppressedCount;

        // Whether the last frame was dropped because the camera is moving or parked away from home,
        // so the resumption can be logged exactly once rather than per frame.
        public bool PtzSuppressed;
        public DateTimeOffset? LastPtzLogAt;
        public int PtzSuppressedFrames;
        public readonly Queue<DateTimeOffset> RecentTriggers = new();

        // The one-deep hand-off between the capture loop and the processing loop. Everything about
        // why it drops rather than queues lives on DetectionFrameSlot.
        private readonly DetectionFrameSlot _slot = new(MotionDetector.FrameSizeBytes);

        public byte[] RentFrameBuffer() => _slot.Rent();
        public void ReturnFrameBuffer(byte[] buffer) => _slot.Return(buffer);
        public byte[] PublishFrame(byte[] buffer, DateTimeOffset at) => _slot.Publish(buffer, at);
        public void CompleteFrames() => _slot.Complete();
        public long FramesSkipped => _slot.Skipped;

        // Only ever touched by the reconcile pass, which is single-threaded.
        public long SkippedAtLastReport { get; set; }

        public IAsyncEnumerable<(byte[] Buffer, DateTimeOffset At)> ReadFramesAsync(CancellationToken cancellationToken) =>
            _slot.ReadAllAsync(cancellationToken);

        // Ticks rather than a DateTimeOffset field so the reconcile pass can read it without taking
        // a lock the detection loop would then contend on twice a second. Seeded at construction so
        // a session that has not yet produced its first frame gets the full stall timeout to
        // connect before the watchdog judges it.
        private long _lastFrameAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        private int _framesDelivered;
        private int _triggerHandlerBusy;

        public void MarkFrameReceived(DateTimeOffset at)
        {
            Interlocked.Exchange(ref _lastFrameAtTicks, at.UtcTicks);
            if (_framesDelivered == 0) Interlocked.Exchange(ref _framesDelivered, 1);
        }

        public bool EverDeliveredAFrame => Volatile.Read(ref _framesDelivered) != 0;

        // The tail of this session's ffmpeg stderr, kept so the reconcile pass can say why the
        // process died instead of only that it did - see FfmpegExitReason. Bounded to a fixed
        // handful of lines: a session that runs for weeks must not accumulate a log in memory, and
        // only the last lines carry the cause anyway.
        //
        // Written by the drain task and read by the reconcile pass, so both go through the lock.
        private readonly Queue<string> _stderrTail = new(FfmpegExitReason.LinesKept);
        private readonly object _stderrLock = new();

        public void RecordStderr(string line)
        {
            lock (_stderrLock)
            {
                if (_stderrTail.Count == FfmpegExitReason.LinesKept) _stderrTail.Dequeue();
                _stderrTail.Enqueue(line);
            }
        }

        public IReadOnlyList<string> StderrTail
        {
            get { lock (_stderrLock) return _stderrTail.ToArray(); }
        }

        public TimeSpan SinceLastFrame(DateTimeOffset now) =>
            now - new DateTimeOffset(Interlocked.Read(ref _lastFrameAtTicks), TimeSpan.Zero);

        // One trigger handler at a time per camera. Dropping a trigger that arrives while the
        // previous one is still being handled is the right call rather than queueing it: the
        // cooldown and per-minute limit already say roughly one event per few seconds is what this
        // camera wants, and an unbounded queue of handlers each opening camera connections is how a
        // busy scene turns into an outage. Whatever is dropped here is the same motion the handler
        // in flight is already recording.
        public void HandleTriggerInBackground(Func<Task> handler)
        {
            if (Interlocked.CompareExchange(ref _triggerHandlerBusy, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try { await handler(); }
                finally { Interlocked.Exchange(ref _triggerHandlerBusy, 0); }
            });
        }
    }
}
