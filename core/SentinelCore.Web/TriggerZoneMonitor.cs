using System.Collections.Concurrent;
using System.Diagnostics;
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
    AlertForwarder alertForwarder,
    TriggerConfirmationService triggerConfirmation,
    AppSettingsFile mainAppSettings,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<TriggerZoneMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath = configuration["FfmpegPath"] ?? "ffmpeg";
    private readonly string _imagesRoot = ResolveImagesRoot(configuration, environment);
    private readonly ConcurrentDictionary<Guid, DetectionSession> _sessions = new();
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
            if (_sessions.TryGetValue(config.CameraId, out var existing))
            {
                // A dead ffmpeg means no detection, no fresh _latest.jpg and no clip segments for
                // this camera until something restarts it - and nothing else does: this reconcile
                // pass used to only restart on config changes, so a camera dropping the connection
                // once (reboot, encoder settings applied, network blip) silently disabled its
                // trigger zones until the whole service was restarted.
                if (ConfigsEquivalent(existing.Config, config) && !existing.Process.HasExited)
                {
                    // Nothing to restart, but the rolling buffer still needs trimming back to the
                    // pre-roll window or it would grow without bound.
                    if (config.RecordClips) clips.PruneBuffer(config.CameraId, config.ClipPreSeconds);
                    continue;
                }

                if (existing.Process.HasExited)
                    logger.LogWarning(
                        "Trigger zone detection ffmpeg for camera {CameraId} had exited; starting a fresh session.",
                        config.CameraId);

                _sessions.TryRemove(config.CameraId, out _);
                StopSession(existing);
                if (!config.RecordClips) clips.DiscardBuffer(config.CameraId);
            }

            var camera = await cameras.GetAsync(config.CameraId, stoppingToken);
            if (camera is null) continue;

            var session = StartSession(camera, config, multiFrame);
            _sessions[config.CameraId] = session;
        }
    }

    // The camera's low-resolution companion stream for a known main-stream URL, or null when no
    // safe derivation exists (a camera whose URL doesn't follow a recognized pattern simply
    // keeps decoding its main stream for detection, exactly as before). Reolink's FLV naming:
    // channel0_main.bcs / channel0_ext.bcs (mid) / channel0_sub.bcs (low) - ext is preferred
    // because detection downscales to a 320-wide grid either way and ext keeps a little more
    // detail for it. Reolink RTSP naming: h264Preview_01_main / h264Preview_01_sub.
    public static string? TryDeriveDetectionUrl(string mainUrl)
    {
        if (mainUrl.Contains("_main.bcs", StringComparison.OrdinalIgnoreCase))
            return mainUrl.Replace("_main.bcs", "_ext.bcs", StringComparison.OrdinalIgnoreCase);
        if (mainUrl.Contains("Preview_01_main", StringComparison.OrdinalIgnoreCase))
            return mainUrl.Replace("Preview_01_main", "Preview_01_sub", StringComparison.OrdinalIgnoreCase);
        return null;
    }

    private DetectionSession StartSession(Camera camera, TriggerConfig config, MultiFrameOptions multiFrame)
    {
        var sourceUrl = HlsStreamManager.BuildAuthenticatedUrl(camera);
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
        // decoded keyframes-only for the snapshot. Decoding 5MP at full frame rate around the
        // clock isn't just wasted CPU - it's what made triggers save empty images: any moment
        // the box falls behind real time (a trigger being handled, the vision model pegging the
        // CPU) backs up into the camera's TCP send buffer, and a reliable transport like
        // Reolink's HTTP-FLV never drops a frame, so the backlog becomes STANDING latency on
        // the detection feed. The detector then sees a car seconds after it was really there,
        // and the (perfectly live) snapshot it grabs shows an empty road - which the vision
        // model then rightly rejects. A substream decodes an order of magnitude faster than
        // real time, so the pipeline reabsorbs any hiccup in moments instead of never.
        // The snapshot output only runs faster than 1fps when this camera can actually use the
        // extra frames (MultiFrameAnalysis on, and this zone forwards to the alert pipeline) -
        // in that case the main stream is decoded in full, since keyframes alone can't provide
        // 3fps; detection still rides the substream either way.
        var snapshotFps = multiFrame.Enabled && config.SendToAlerts ? 3 : 1;
        var detectUrl = TryDeriveDetectionUrl(sourceUrl);
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
        // Keyframes-only is a decoder option scoped to this input; the copied clip output below
        // is untouched by it, and with a substream carrying detection the snapshot is the only
        // consumer of decoded main-stream frames. Not applicable when multi-frame needs real
        // snapshot cadence (see snapshotFps above).
        if (detectUrl is not null && snapshotFps == 1)
            args.AddRange(["-skip_frame", "nokey"]);
        args.AddRange(["-i", sourceUrl]);

        // Input 0 feeds detection and input 1 the snapshot when a substream exists; with a
        // single connection both come from input 0, exactly as before substreams were used.
        var mainInput = detectUrl is null ? "0" : "1";
        args.AddRange([
            "-map", "0:v:0",
            "-an",
            "-filter:v", $"fps=2,scale={MotionDetector.GridWidth}:{MotionDetector.GridHeight},format=rgb24",
            "-f", "rawvideo",
            "-",
            "-map", $"{mainInput}:v:0",
            "-vf", $"fps={snapshotFps}",
            "-update", "1",
            "-y", LatestSnapshotPath(camera.Id)
        ]);

        if (config.RecordClips)
        {
            clips.PruneBuffer(camera.Id, config.ClipPreSeconds);
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
        var session = new DetectionSession(process, config, cts, new MotionDetector(config.ResolvedZones, config.ShadowSensitivity), multiFrame);
        _ = RunDetectionLoopAsync(camera, session, cts.Token);
        _ = DrainAsync(process.StandardError);

        return session;
    }

    private async Task RunDetectionLoopAsync(Camera camera, DetectionSession session, CancellationToken cancellationToken)
    {
        var stream = session.Process.StandardOutput.BaseStream;
        var buffer = new byte[MotionDetector.FrameSizeBytes];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, buffer, cancellationToken)) break;

                var blobFractions = session.Detector.ProcessFrame((byte[])buffer.Clone());
                if (blobFractions is null) continue;

                // Each zone is judged against its own minimum size - that's the entire point of
                // having several. When more than one is over its own floor (an object crossing a
                // boundary, or overlapping zones), the one that cleared its threshold by the
                // largest margin wins, so a frame records one trigger rather than one per zone.
                var zones = session.Config.ResolvedZones;
                var winner = -1;
                var bestOvershoot = 0.0;
                for (var i = 0; i < zones.Count && i < blobFractions.Length; i++)
                {
                    if (zones[i].MinTriggerArea <= 0 || blobFractions[i] < zones[i].MinTriggerArea) continue;

                    var overshoot = blobFractions[i] / zones[i].MinTriggerArea;
                    if (overshoot <= bestOvershoot) continue;
                    bestOvershoot = overshoot;
                    winner = i;
                }

                if (winner < 0) continue;

                await MaybeTriggerAsync(camera, session, blobFractions[winner], zones[winner].Name, cancellationToken);
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

    private async Task MaybeTriggerAsync(Camera camera, DetectionSession session, double areaFraction, string zoneName, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

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
            var bytes = await TryReadLatestSnapshotAsync(camera.Id, cancellationToken)
                ?? await streams.CaptureSnapshotAsync(camera, cancellationToken);
            if (bytes is null) return;

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
                gated ? TriggerConfirmation.Pending : TriggerConfirmation.Confirmed, cancellationToken, zoneName);
            logger.LogInformation(
                "Trigger zone fired for camera {CameraId} ({Name}) in zone {Zone}, area {Area:P1}{Held}",
                camera.Id, camera.Name, zoneName, areaFraction, gated ? " - held for AI confirmation" : "");

            if (session.Config.RecordClips)
                _ = BuildClipAsync(camera, trigger, session.Config, now);

            // Fast (file reads/writes and SQLite inserts, no network/wait), so it's fine to await
            // inline here like the rest of this method - only each companion's actual clip build
            // (comparatively slow, waits out the post-roll) is fire-and-forget, same as the
            // triggering camera's own clip above.
            var linkedCameraNames = group is { RecordCompanionClips: true } or { AnalyzeCompanionCaptures: true }
                ? await RecordCompanionCamerasAsync(camera, group, incidentId!, now, cancellationToken)
                : [];

            if (session.Config.SendToAlerts)
                _ = ForwardToAlertPipelineAsync(
                    camera, bytes, session.MultiFrame, session.Config.AlertProfile, now, linkedCameraNames, incidentId,
                    trigger.Id, gated);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save trigger snapshot for camera {CameraId}", camera.Id);
        }
    }

    // Captures each OTHER group member's current frame and records it as its own TriggerEvent
    // under the shared incident, so the Triggers tab shows footage from the whole group even
    // though only one camera actually detected motion. A member with no active buffer (no
    // enabled trigger zone of its own) is silently skipped - nothing to pull from. Returns the
    // names of cameras actually recorded, for the "Also recorded: ..." notification annotation.
    private async Task<List<string>> RecordCompanionCamerasAsync(
        Camera triggeringCamera, CameraGroup group, string incidentId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var recorded = new List<string>();
        foreach (var memberId in group.CameraIds)
        {
            if (memberId == triggeringCamera.Id) continue;

            try
            {
                var memberCamera = await cameras.GetAsync(memberId, cancellationToken);
                if (memberCamera is null) continue;

                var snapshot = await TryReadLatestSnapshotAsync(memberId, cancellationToken);
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
                    companionGated ? TriggerConfirmation.Pending : TriggerConfirmation.Confirmed, cancellationToken);
                recorded.Add(memberCamera.Name);
                logger.LogInformation(
                    "Recorded companion capture for camera {CameraId} ({Name}), incident {IncidentId}",
                    memberId, memberCamera.Name, incidentId);

                var memberConfig = await configStore.GetAsync(memberId, cancellationToken);
                if (group.RecordCompanionClips && memberConfig is { RecordClips: true })
                    _ = BuildClipAsync(memberCamera, companionTrigger, memberConfig, occurredAt);

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
        IReadOnlyList<string> linkedCameraNames, string? incidentId, Guid triggerId, bool gated)
    {
        try
        {
            var extraFrames = new List<byte[]>();
            if (multiFrame.Enabled)
            {
                for (var i = 0; i < multiFrame.ExtraFrameCount; i++)
                {
                    await Task.Delay(multiFrame.FrameIntervalMs, _stoppingToken);
                    var frame = await TryReadLatestSnapshotAsync(camera.Id, _stoppingToken);
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

    // Deliberately not tied to the detection session's token: the post-roll wait outlives a config
    // change or a camera being disabled, and the clip is still worth writing when that happens.
    private async Task BuildClipAsync(Camera camera, TriggerEvent trigger, TriggerConfig config, DateTimeOffset triggeredAt)
    {
        try
        {
            // Wait for the post-roll itself plus one segment, so the segment covering the end of
            // the window has been closed off by ffmpeg before we try to concatenate it.
            var wait = TimeSpan.FromSeconds(config.ClipPostSeconds + ClipRecorder.SegmentSeconds + 1);
            await Task.Delay(wait, _stoppingToken);

            var clipPath = await clips.BuildClipAsync(camera.Id, triggeredAt, config.ClipPreSeconds, config.ClipPostSeconds, _stoppingToken);
            if (clipPath is null) return;

            await eventStore.SetClipPathAsync(trigger.Id, clipPath, _stoppingToken);
            logger.LogInformation("Saved trigger clip for camera {CameraId} ({Name})", camera.Id, camera.Name);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build trigger clip for camera {CameraId}", camera.Id);
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

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() != null) { }
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
            if (Math.Abs(zonesA[zone].MinTriggerArea - zonesB[zone].MinTriggerArea) > 0.0001) return false;

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

    private sealed class DetectionSession(Process process, TriggerConfig config, CancellationTokenSource cts, MotionDetector detector, MultiFrameOptions multiFrame)
    {
        public Process Process { get; } = process;
        public TriggerConfig Config { get; } = config;
        public CancellationTokenSource Cts { get; } = cts;
        public MotionDetector Detector { get; } = detector;
        public MultiFrameOptions MultiFrame { get; } = multiFrame;

        public readonly object TriggerLock = new();
        public DateTimeOffset? LastTriggerAt;
        public readonly Queue<DateTimeOffset> RecentTriggers = new();
    }
}
