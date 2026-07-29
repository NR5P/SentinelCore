using System.Collections.Concurrent;

namespace SentinelCore.Web;

// A gate/door left open with nobody around never trips a motion alert, so this periodically
// grabs one snapshot per opted-in camera and asks the main app's AI vision pipeline whether it's
// open (see GateCheckAnalyzer for why that's a separate endpoint from a normal Blue Iris alert).
//
// Deliberately a simple polling tick rather than TriggerZoneMonitor's continuous per-camera
// ffmpeg session - a gate check only needs one frame every few minutes, not a live motion feed,
// so there's no persistent process per camera to manage here.
public sealed class GateCheckMonitor(
    ICameraStore cameras,
    IGateCheckStore gateChecks,
    HlsStreamManager streams,
    GateCheckForwarder forwarder,
    ILogger<GateCheckMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    // When each camera was last actually checked, kept in memory only - unlike the open/closed
    // state itself, losing this on a restart just means every enabled camera gets checked again
    // on the first tick rather than waiting out its remaining interval, which is harmless.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastCheckedAt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Deliberately keyed on the token, not the exception type: an HTTP timeout
                // inside a check throws TaskCanceledException too (it *is* an
                // OperationCanceledException), and filtering on type alone let that escape
                // uncaught here once already - with BackgroundServiceExceptionBehavior=StopHost,
                // that took the entire dashboard down over one slow Ollama response, not just
                // this feature. The token is the only reliable way to tell "the service is
                // stopping" apart from "something else threw a cancellation-flavored exception".
                logger.LogError(ex, "Gate check tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        var enabled = await gateChecks.GetEnabledAsync(stoppingToken);
        if (enabled.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var config in enabled)
        {
            var due = !_lastCheckedAt.TryGetValue(config.CameraId, out var lastChecked) ||
                now - lastChecked >= TimeSpan.FromMinutes(config.IntervalMinutes);
            if (!due) continue;

            _lastCheckedAt[config.CameraId] = now;
            try
            {
                await CheckAsync(config, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // See the matching comment in ExecuteAsync's catch - keyed on the token, not the
                // exception type, so a slow-Ollama timeout on one camera can't be mistaken for
                // the service shutting down and escape uncaught.
                logger.LogError(ex, "Gate check failed for camera {CameraId}", config.CameraId);
            }
        }
    }

    private async Task CheckAsync(GateCheckConfig config, CancellationToken cancellationToken)
    {
        var camera = await cameras.GetAsync(config.CameraId, cancellationToken);
        if (camera is null)
        {
            // The camera was deleted but its GateChecks row wasn't cleaned up alongside it -
            // rather than erroring every tick forever, drop the orphaned row once and move on.
            await gateChecks.DeleteAsync(config.CameraId, cancellationToken);
            return;
        }

        var snapshot = await streams.CaptureSnapshotAsync(camera, cancellationToken);
        if (snapshot is null)
        {
            logger.LogWarning("Could not capture a snapshot for gate check on {Camera}", camera.Name);
            return;
        }

        var outcome = await forwarder.ForwardAsync(snapshot, camera, config, cancellationToken);
        if (outcome is null) return;

        await gateChecks.UpdateStateAsync(
            config.CameraId, outcome.State, outcome.StateChangedAt, outcome.NotifiedAt, outcome.Blocked, cancellationToken);

        if (outcome.NotifiedOpen || outcome.NotifiedBlocked)
            logger.LogInformation("Gate check notified for {Camera} ({Label})", camera.Name, config.CheckLabel);
    }
}
