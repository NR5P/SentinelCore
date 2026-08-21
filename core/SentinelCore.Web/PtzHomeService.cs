namespace SentinelCore.Web;

// "Home" for a PTZ camera, in one place, because two callers need it: the Home button in the app
// (CameraPtzEndpoints) and the idle worker that sends the camera back by itself (PtzHomingWorker).
//
// Home is a saved preset first and the camera's own home position second. Plenty of PTZ hardware
// has none - the camera this was built against reports HomeSupported=false and answers
// GotoHomePosition with a SOAP Fault - but presets are close to universal, and this camera has 64
// slots. See CameraPtzEndpoints for why the preset is keyed on its name rather than on a token
// stored here.
public sealed class PtzHomeService(OnvifClient onvif, PtzActivityTracker activity, ILogger<PtzHomeService> logger)
{
    // What the app's home position is called in the camera's own preset list. Matched
    // case-insensitively so a camera that normalises the name it was given still resolves.
    public const string HomePresetName = "SentinelCore Home";

    public enum HomeOutcome
    {
        Moved,
        // The camera has neither a saved preset nor a home position of its own. The one failure a
        // user can act on, so callers say what to do about it.
        NoHomeSaved,
        Failed
    }

    public async Task<HomeOutcome> GoHomeAsync(Camera camera, CancellationToken cancellationToken)
    {
        activity.RecordHomingStarted(camera.Id);

        try
        {
            var saved = await FindHomePresetAsync(camera, cancellationToken);
            if (saved is not null)
            {
                var went = await onvif.GotoPresetAsync(
                    camera.OnvifPtzServiceUrl!, camera.OnvifProfileToken!, saved.Token,
                    camera.Username, camera.Password, cancellationToken);

                if (went)
                {
                    activity.RecordHomeReached(camera.Id);
                    return HomeOutcome.Moved;
                }

                activity.RecordHomingFailed(camera.Id);
                return HomeOutcome.Failed;
            }

            if (await onvif.GotoHomePositionAsync(
                    camera.OnvifPtzServiceUrl!, camera.OnvifProfileToken!,
                    camera.Username, camera.Password, cancellationToken))
            {
                activity.RecordHomeReached(camera.Id);
                return HomeOutcome.Moved;
            }

            activity.RecordHomingFailed(camera.Id);
            return HomeOutcome.NoHomeSaved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leaving the moving flag set would wedge detection off for good, so a thrown home is
            // still a completed one as far as the tracker is concerned.
            activity.RecordHomingFailed(camera.Id);
            logger.LogWarning(ex, "Sending camera {CameraId} ({Name}) home failed", camera.Id, camera.Name);
            return HomeOutcome.Failed;
        }
    }

    // Stores wherever the camera is pointing right now as home, reusing the slot it already owns so
    // a repeatedly-pressed save doesn't walk through the camera's finite preset list.
    public async Task<bool> SaveHomeAsync(Camera camera, CancellationToken cancellationToken)
    {
        var existing = await FindHomePresetAsync(camera, cancellationToken);
        var token = await onvif.SetPresetAsync(
            camera.OnvifPtzServiceUrl!, camera.OnvifProfileToken!, HomePresetName, existing?.Token,
            camera.Username, camera.Password, cancellationToken);

        if (token is null) return false;

        // Saving home means the camera is, by definition, at home - and that is worth recording,
        // because it is the one way detection resumes without a journey first.
        activity.RecordHomeReached(camera.Id);
        return true;
    }

    private async Task<OnvifClient.OnvifPreset?> FindHomePresetAsync(Camera camera, CancellationToken cancellationToken)
    {
        var presets = await onvif.GetPresetsAsync(
            camera.OnvifPtzServiceUrl!, camera.OnvifProfileToken!,
            camera.Username, camera.Password, cancellationToken);

        return presets.FirstOrDefault(
            preset => string.Equals(preset.Name, HomePresetName, StringComparison.OrdinalIgnoreCase));
    }
}
