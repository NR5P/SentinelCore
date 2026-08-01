using SentinelCore;

namespace SentinelCore.Web;

// Why a trigger happened, for the log line and the alert's occurredAt provenance. Not persisted -
// a TriggerEvent row looks the same however it was caused.
public sealed record CameraTriggerRequest(string Source, string? AlertProfile, int CooldownSeconds);

public enum CameraTriggerStatus
{
    Triggered,

    // Still inside this camera's cooldown window - the one outcome where trying again later works,
    // which is why callers can tell it apart from an outright failure.
    Cooldown,
    Failed
}

public sealed record CameraTriggerResult(CameraTriggerStatus Status, string Detail, Guid? TriggerEventId = null)
{
    public bool Triggered => Status == CameraTriggerStatus.Triggered;

    public static CameraTriggerResult Suppressed(string detail) => new(CameraTriggerStatus.Cooldown, detail);
    public static CameraTriggerResult Failed(string detail) => new(CameraTriggerStatus.Failed, detail);
}

// Turns "something outside the motion detector says this camera should look right now" into a
// real trigger: grab the camera's current frame, record it as a TriggerEvent so it shows up in the
// Triggers tab and the day timeline, and hand it to the main app's analysis + notification
// pipeline the same way a zone trigger does. Both external sources go through here - the HTTP
// trigger API (CameraTriggerEndpoints) and a Device Hub sensor firing (SensorTriggerWatcher) - so
// the cooldown, the image on disk, and the resulting Feed entry are identical either way.
//
// TriggerZoneMonitor deliberately keeps its own copy of this flow rather than calling in here: it
// can also cut clips from a rolling buffer, capture companion cameras in a group, and pull burst
// frames out of an ffmpeg session it already owns, none of which a one-off external trigger has.
// What would genuinely have been duplicated - the snapshot path convention and reading the latest
// frame - is shared through TriggerImagePaths instead.
public sealed class CameraTriggerService(
    ICameraGroupStore groups,
    ITriggerConfigStore configStore,
    ITriggerEventStore eventStore,
    HlsStreamManager streams,
    AlertForwarder alertForwarder,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<CameraTriggerService> logger)
{
    // A camera with an enabled zone has ffmpeg writing _latest.jpg once a second or better, so
    // anything older than this means no session is running and the file is a leftover - fall back
    // to a live capture rather than answering with a stale frame.
    private static readonly TimeSpan LatestFrameMaxAge = TimeSpan.FromSeconds(5);

    private readonly string _imagesRoot = TriggerImagePaths.ResolveRoot(configuration, environment);

    // Plain dictionary under a lock rather than a ConcurrentDictionary: check-then-reserve has to
    // be atomic, and this is only touched a handful of times a minute at most.
    private readonly object _cooldownLock = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastTriggered = [];

    public async Task<CameraTriggerResult> TriggerAsync(Camera camera, CameraTriggerRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (request.CooldownSeconds > 0)
        {
            lock (_cooldownLock)
            {
                if (_lastTriggered.TryGetValue(camera.Id, out var last) &&
                    (now - last).TotalSeconds < request.CooldownSeconds)
                {
                    var remaining = request.CooldownSeconds - (int)(now - last).TotalSeconds;
                    return CameraTriggerResult.Suppressed($"{camera.Name} was triggered recently - {remaining}s of cooldown left.");
                }

                // Reserve the slot before the slow part, so two triggers arriving together can't
                // both get through. Released again below if the capture fails, since a trigger
                // that never produced an image shouldn't hold the camera down for the full window.
                _lastTriggered[camera.Id] = now;
            }
        }

        try
        {
            var bytes = await TriggerImagePaths.TryReadLatestAsync(_imagesRoot, camera.Id, LatestFrameMaxAge, cancellationToken)
                ?? await streams.CaptureSnapshotAsync(camera, cancellationToken);

            if (bytes is null)
            {
                ReleaseCooldown(camera.Id, now);
                logger.LogWarning("Trigger for camera {CameraId} ({Name}) from {Source} could not grab a frame", camera.Id, camera.Name, request.Source);
                return CameraTriggerResult.Failed($"Could not grab a frame from {camera.Name} - check its stream URL/credentials.");
            }

            var cameraDirectory = TriggerImagePaths.CameraDirectory(_imagesRoot, camera.Id);
            Directory.CreateDirectory(cameraDirectory);
            var imagePath = Path.Combine(cameraDirectory, $"{now:yyyyMMdd-HHmmss-fff}.jpg");
            await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken);

            // Join a camera-group incident that's already open, so a sensor-driven capture sits
            // alongside whatever else the group recorded in the same few seconds. Unlike a zone
            // trigger this never captures the other members itself - the caller asked for one
            // specific camera, and quietly firing four more isn't what an external trigger means.
            var group = await groups.GetGroupForCameraAsync(camera.Id, cancellationToken);
            var incidentId = group is null
                ? null
                : await eventStore.GetMostRecentIncidentIdAsync(group.CameraIds, now.AddSeconds(-group.CorrelationWindowSeconds), cancellationToken);

            // Area 0 means "not self-detected", the same convention companion captures use - there
            // was no motion blob involved in deciding to fire this one.
            var trigger = await eventStore.AddAsync(camera.Id, imagePath, 0, incidentId, cancellationToken);

            var profile = await ResolveProfileAsync(camera.Id, request.AlertProfile, cancellationToken);
            logger.LogInformation(
                "Trigger fired for camera {CameraId} ({Name}) from {Source}, profile {Profile}",
                camera.Id, camera.Name, request.Source, profile);

            // Awaited, not fire-and-forget: /api/blueiris/alert only queues the alert and answers
            // immediately, so this costs a loopback round-trip and lets the caller be told the
            // difference between "queued for analysis" and "the pipeline refused it".
            var forwarded = await alertForwarder.ForwardAsync(
                bytes, [], camera.Name, profile, now, null, incidentId, cancellationToken);

            return new CameraTriggerResult(
                CameraTriggerStatus.Triggered,
                forwarded
                    ? $"{camera.Name} captured and queued for analysis."
                    : $"{camera.Name} captured, but the alert pipeline refused it - check the log and the main app's API key.",
                trigger.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReleaseCooldown(camera.Id, now);
            logger.LogWarning(ex, "Trigger for camera {CameraId} ({Name}) from {Source} failed", camera.Id, camera.Name, request.Source);
            return CameraTriggerResult.Failed($"Triggering {camera.Name} failed - see the server log.");
        }
    }

    // Only clears the reservation this call made: a second trigger that got in behind it owns the
    // window now, and dropping that one's timestamp would let a third through immediately.
    private void ReleaseCooldown(Guid cameraId, DateTimeOffset reservedAt)
    {
        lock (_cooldownLock)
        {
            if (_lastTriggered.TryGetValue(cameraId, out var current) && current == reservedAt)
                _lastTriggered.Remove(cameraId);
        }
    }

    // An explicit profile wins; otherwise reuse whatever this camera's trigger zone is already
    // configured to analyse with, so an API or sensor trigger reads the same way in the Feed as a
    // motion trigger from the same camera.
    private async Task<string> ResolveProfileAsync(Guid cameraId, string? requested, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();

        var config = await configStore.GetAsync(cameraId, cancellationToken);
        return string.IsNullOrWhiteSpace(config?.AlertProfile) ? "default" : config.AlertProfile;
    }
}
