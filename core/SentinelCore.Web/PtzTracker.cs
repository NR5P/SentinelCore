using Microsoft.Extensions.Options;

namespace SentinelCore.Web;

// Follows a subject with the PTZ camera, then sends it home.
//
// The loop is: read the newest full-resolution frame the detection session already writes, find the
// things worth following in it, and nudge the camera towards the most convincing one. It stops when
// the subject has been gone for a while, when it has been running too long, or the moment somebody
// picks up the controls themselves.
//
// It is deliberately a burst controller rather than a continuous one. A correction cannot be
// steered until the next frame arrives, and between frames there is inference and a command over
// the network - the better part of a second. Moving continuously across that gap overshoots by
// however long it takes; moving in short bursts and re-measuring does not. The visible result is
// stepped rather than smooth, which is the honest cost of driving a camera from the other end of a
// network instead of from inside it.
public sealed class PtzTracker(
    IFastLocalDetector detector,
    OnvifClient onvif,
    PtzHomeService home,
    PtzActivityTracker activity,
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptionsMonitor<PtzTrackingOptions> options,
    ILogger<PtzTracker> logger) : IDisposable
{
    private readonly string _imagesRoot = TriggerImagePaths.ResolveRoot(configuration, environment);
    private readonly Dictionary<Guid, Track> _sessions = [];
    // Every loop that has been started and not yet finished, which is not the same set as _sessions:
    // an aborted track leaves _sessions immediately (so the camera stops reading as tracking) but
    // keeps running for another moment while it notices. Shutdown has to wait for those too - they
    // are still holding the detector.
    private readonly HashSet<Track> _live = [];
    private readonly Dictionary<Guid, DateTimeOffset> _restingUntil = [];
    private readonly object _gate = new();
    private bool _disposed;

    // How long Dispose will wait for a track to leave the detector. Generous next to one inference
    // (~0.5s here) and next to the final home command (bounded by OnvifClient's 3s timeout), and
    // still short enough not to be the reason a restart looks hung.
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    // One running track. The completion latch exists from construction rather than being assigned
    // once the loop has been started, because Dispose has to be able to wait on a track the instant
    // it becomes visible - a window where a session is in the dictionary but its Task is not yet
    // assigned anywhere is the same race this is here to close, just narrower.
    private sealed class Track(CancellationTokenSource cts)
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Cts { get; } = cts;

        public Task Finished => _finished.Task;

        public void MarkFinished() => _finished.TrySetResult();
    }

    // Starts following whatever is in front of this camera, if tracking is on and it isn't already.
    // Called when a trigger zone fires: something entered the zone, so there is something to follow.
    // Returns quietly when tracking is off, which is the default.
    public void Start(Camera camera)
    {
        // CurrentValue, not a frozen snapshot: this is the switch that decides whether a camera is
        // allowed to move on its own, and turning that off should take effect when you turn it off
        // rather than at the next restart.
        var settings = options.CurrentValue;
        if (!settings.Enabled || !camera.PtzEnabled) return;
        if (string.IsNullOrWhiteSpace(camera.OnvifPtzServiceUrl) || string.IsNullOrWhiteSpace(camera.OnvifProfileToken)) return;

        Track track;
        lock (_gate)
        {
            if (_disposed || _sessions.ContainsKey(camera.Id)) return;
            if (_restingUntil.TryGetValue(camera.Id, out var until) && DateTimeOffset.UtcNow < until) return;
            track = new Track(new CancellationTokenSource());
            _sessions[camera.Id] = track;
            _live.Add(track);
        }

        _ = RunAsync(camera, settings, track);
    }

    // Somebody has taken the controls. Their input wins immediately - nothing about this feature is
    // worth fighting a person holding a button for.
    public void AbortForManualControl(Guid cameraId)
    {
        Track? track;
        lock (_gate)
        {
            if (!_sessions.Remove(cameraId, out track)) return;
            // Set here rather than left to the loop's own Finish: the loop is a background task and
            // may not have noticed the cancellation yet, so a trigger arriving in the meantime would
            // slip past the rest check. It is also the behaviour that makes sense - somebody has the
            // controls, and tracking should not elbow back in the moment they pause.
            Rest(cameraId);
        }

        logger.LogInformation("Manual PTZ control taken for camera {CameraId}; tracking stopped.", cameraId);
        // Cancel but do not dispose: the loop is still holding this token and will not have noticed
        // yet. Disposing a source out from under a live token is how you turn "somebody grabbed the
        // controls" into an ObjectDisposedException from somewhere unrelated. The loop's own Finish
        // owns the disposal, and it always runs.
        track.Cts.Cancel();
    }

    public bool IsTracking(Guid cameraId)
    {
        lock (_gate) return _sessions.ContainsKey(cameraId);
    }

    private async Task RunAsync(Camera camera, PtzTrackingOptions settings, Track track)
    {
        try
        {
            await TrackAsync(camera, settings, track.Cts.Token);
        }
        finally
        {
            // In a finally rather than on each exit path, because the one thing that must be true
            // when this method returns is that the camera is no longer marked as tracking. A path
            // that skipped it would leave the session in the dictionary forever, and Start's
            // "already tracking" check would then refuse every future track on this camera with no
            // error and nothing in the log to say why.
            Finish(camera.Id, track);
        }
    }

    private async Task TrackAsync(Camera camera, PtzTrackingOptions settings, CancellationToken token)
    {
        var (classIds, unrecognised) = PtzTrackClasses.Resolve(settings.Track);
        if (unrecognised.Count > 0)
        {
            logger.LogWarning(
                "PtzTracking:Track lists {Unknown} that is not a class this detector knows; ignoring it.",
                string.Join(", ", unrecognised));
        }

        if (classIds.Count == 0)
        {
            logger.LogWarning("PtzTracking is on but nothing trackable is configured, so tracking will not run.");
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var lastSeenAt = startedAt;
        var moved = false;

        logger.LogInformation("Following {Classes} with {Name}.", string.Join("/", classIds.Select(PtzTrackClasses.Describe)), camera.Name);

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!options.CurrentValue.Enabled)
                {
                    logger.LogInformation("PTZ tracking was switched off; {Name} is going home.", camera.Name);
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                if ((now - startedAt).TotalSeconds >= settings.MaxTrackSeconds)
                {
                    logger.LogInformation(
                        "Tracking {Name} hit its {Seconds}s limit; sending it home.", camera.Name, settings.MaxTrackSeconds);
                    break;
                }

                var target = await FindTargetAsync(camera, classIds, settings, token);
                if (target is not null)
                {
                    lastSeenAt = now;
                    // Keeps the idle homing worker from deciding this camera has been abandoned
                    // while it is actually busy following something.
                    activity.RecordTrackingTick(camera.Id);
                    if (await NudgeTowardsAsync(camera, target, settings, token)) moved = true;
                }
                else if ((now - lastSeenAt).TotalSeconds >= settings.LostSeconds)
                {
                    logger.LogInformation(
                        "Lost what {Name} was following for {Seconds}s; sending it home.", camera.Name, settings.LostSeconds);
                    break;
                }

                await Task.Delay(settings.PollMilliseconds, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Manual control, or shutdown. The manual path is deliberately left where the person
            // pointed it - going home would undo the thing they just did.
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tracking failed for camera {CameraId}", camera.Id);
        }

        // Always home at the end, even if nothing was ever followed: a session that moved the
        // camera and then gave up must not leave it staring somewhere its zones do not describe.
        // Not on the caller's token - this is the part that must happen.
        if (moved) await home.GoHomeAsync(camera, CancellationToken.None);
    }

    private async Task<FastLocalDetection?> FindTargetAsync(
        Camera camera, HashSet<int> classIds, PtzTrackingOptions settings, CancellationToken token)
    {
        // The detection session already writes a full-resolution _latest.jpg for this camera, so
        // tracking costs no extra decoding - and full resolution is what makes a person at the far
        // end of a driveway detectable at all.
        // Bounded by age: a stale _latest.jpg means the detection session has stopped writing, and
        // steering the camera by a frame from a minute ago is worse than not steering it at all.
        var frame = await TriggerImagePaths.TryReadLatestAsync(
            _imagesRoot, camera.Id, TimeSpan.FromSeconds(5), token);
        if (frame is null) return null;

        var detections = await detector.DetectAsync(frame, crop: null, token);

        // Largest qualifying box rather than highest score: when several people are in frame the
        // nearest one is the one worth keeping centred, and it is also the most stable to follow.
        return detections
            .Where(d => classIds.Contains(d.ClassId) && d.Score >= settings.MinimumScore)
            .OrderByDescending(d => d.Width * d.Height)
            .FirstOrDefault();
    }

    // The control law, kept static and side-effect free so its behaviour can be tested without a
    // camera. Returns null when the subject is close enough to centre to leave alone.
    public static (double Pan, double Tilt)? Correction(FastLocalDetection target, PtzTrackingOptions settings)
    {
        var offsetX = target.CenterX - 0.5;
        var offsetY = target.CenterY - 0.5;

        if (Math.Abs(offsetX) <= settings.Deadzone && Math.Abs(offsetY) <= settings.Deadzone) return null;

        // Proportional to how far off-centre the subject is, measured from the edge of the deadzone
        // so a correction starts gently instead of jumping to full speed the moment it applies.
        // Image Y grows downward and ONVIF tilt grows upward, hence the negation.
        return (Scale(offsetX, settings), -Scale(offsetY, settings));
    }

    private static double Scale(double offset, PtzTrackingOptions settings)
    {
        var magnitude = Math.Abs(offset);
        if (magnitude <= settings.Deadzone) return 0;

        var span = Math.Max(0.5 - settings.Deadzone, 1e-6);
        var beyond = Math.Clamp((magnitude - settings.Deadzone) / span, 0, 1);
        return Math.Sign(offset) * beyond * settings.Speed;
    }

    private async Task<bool> NudgeTowardsAsync(
        Camera camera, FastLocalDetection target, PtzTrackingOptions settings, CancellationToken token)
    {
        if (Correction(target, settings) is not var (pan, tilt)) return false;
        if (pan == 0 && tilt == 0) return false;

        activity.RecordMoveStarted(camera.Id);
        var started = await onvif.ContinuousMoveAsync(
            camera.OnvifPtzServiceUrl!, camera.OnvifProfileToken!, pan, tilt, 0,
            camera.Username, camera.Password, token);

        if (!started)
        {
            activity.RecordStopped(camera.Id);
            return false;
        }

        try
        {
            await Task.Delay(settings.NudgeMilliseconds, token);
        }
        finally
        {
            // The stop has to go out even when the wait was cancelled, or a camera mid-nudge when
            // somebody grabs the controls keeps turning. Same asymmetry PtzControlsView is built
            // around: a lost move is obvious, a lost stop is a camera that never stops.
            await onvif.StopAsync(
                camera.OnvifPtzServiceUrl!, camera.OnvifProfileToken!,
                camera.Username, camera.Password, CancellationToken.None);
            activity.RecordStopped(camera.Id);
        }

        return true;
    }

    private void Finish(Guid cameraId, Track track)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(cameraId, out var current) && current == track) _sessions.Remove(cameraId);
            _live.Remove(track);
            Rest(cameraId);
        }

        track.Cts.Dispose();
        // Last, and after the disposal: this is what Dispose waits on, and the promise it is making
        // is that the loop has finished touching everything it borrowed.
        track.MarkFinished();
    }

    // Callers hold _gate.
    private void Rest(Guid cameraId) =>
        _restingUntil[cameraId] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, options.CurrentValue.RestSeconds));

    // For tests, and for the diagnostics endpoint if one ever wants it: whether this camera is in
    // the quiet period after a track.
    public bool IsResting(Guid cameraId)
    {
        lock (_gate)
            return _restingUntil.TryGetValue(cameraId, out var until) && DateTimeOffset.UtcNow < until;
    }

    // Cancels every running track and WAITS for it, which is the whole point of this method.
    //
    // The tracking loop is a detached background task, not a hosted service, so nothing in the host
    // shutdown sequence stops it: it keeps polling the detector for as long as the process takes to
    // come down, which on this box is around 25 seconds while ffmpeg and go2rtc are drained. The
    // container then disposes this object and, immediately afterwards, FastLocalDetector - whose
    // Dispose frees the native ONNX InferenceSession. A loop still inside session.Run() at that
    // moment is reading memory that has just been freed by another thread, and the process dies
    // with SIGSEGV rather than exiting.
    //
    // That is not hypothetical. It happened once, on 2026-08-20 at 22:12:43 - the only core dump in
    // this service's history, on the first shutdown that ever landed while a track was running, with
    // the last inference logged in the same second as "Application is shutting down". The three
    // other restarts that evening had no inference in flight and came down cleanly.
    //
    // So: cancel, then wait for each loop to say it has let go, and only then let disposal carry on
    // to the detector. Bounded, because a shutdown that hangs is its own kind of broken - past the
    // grace period we accept the risk we were trying to avoid rather than refuse to exit.
    public void Dispose()
    {
        List<Track> running;
        lock (_gate)
        {
            _disposed = true;
            running = [.. _live];
            _sessions.Clear();
        }

        foreach (var track in running)
        {
            try { track.Cts.Cancel(); } catch { /* already gone */ }
        }

        if (running.Count == 0) return;

        try
        {
            Task.WaitAll([.. running.Select(track => track.Finished)], ShutdownGrace);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Waiting for PTZ tracking to stop failed; shutting down anyway.");
        }
    }
}
