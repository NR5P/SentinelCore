namespace SentinelCore.Web;

public sealed class PtzMoveRequestDto
{
    // Standard ONVIF continuous-move velocity space: -1.0 (full speed one direction) to 1.0
    // (full speed the other), 0 meaning "don't move on this axis." Clamped rather than rejected -
    // this is meant to be driven by a live joystick/drag gesture, where an occasional
    // slightly-out-of-range value from client-side rounding shouldn't surface as an error.
    public double Pan { get; set; }
    public double Tilt { get; set; }
    public double Zoom { get; set; }
}

// A physical-actuation action, not just a view - gated the same as camera CRUD (AdminOnly)
// rather than the plain-authenticated bar Live/view endpoints use.
public static class CameraPtzEndpoints
{
    public static void MapCameraPtzEndpoints(this WebApplication app)
    {
        app.MapPost("/api/cameras/{id:guid}/ptz/move", async (
            Guid id, PtzMoveRequestDto dto, ICameraStore store, OnvifClient onvif,
            PtzActivityTracker activity, CancellationToken ct) =>
        {
            var (camera, error) = await ResolvePtzCameraAsync(id, store, ct);
            if (error is not null) return error;

            // Before the command, not after: detection has to be suspended by the time the camera
            // starts moving, and a slow ONVIF round trip would otherwise leave a window of
            // full-frame "motion" that fires a trigger.
            activity.RecordMoveStarted(camera!.Id);

            var ok = await onvif.ContinuousMoveAsync(
                camera.OnvifPtzServiceUrl!, camera.OnvifProfileToken!,
                Math.Clamp(dto.Pan, -1, 1), Math.Clamp(dto.Tilt, -1, 1), Math.Clamp(dto.Zoom, -1, 1),
                camera.Username, camera.Password, ct);

            return ok ? Results.Ok(new { success = true })
                      : Results.Problem("The camera did not respond to the move command.", statusCode: 502);
        }).RequireAuthorization("AdminOnly");

        app.MapPost("/api/cameras/{id:guid}/ptz/stop", async (
            Guid id, ICameraStore store, OnvifClient onvif,
            PtzActivityTracker activity, CancellationToken ct) =>
        {
            var (camera, error) = await ResolvePtzCameraAsync(id, store, ct);
            if (error is not null) return error;

            var ok = await onvif.StopAsync(camera!.OnvifPtzServiceUrl!, camera.OnvifProfileToken!, camera.Username, camera.Password, ct);

            // After the command, and regardless of whether it worked: this is what starts the idle
            // clock that eventually sends the camera home, and a stop the camera refused is exactly
            // the case where that backstop matters most.
            activity.RecordStopped(camera.Id);
            return ok ? Results.Ok(new { success = true })
                      : Results.Problem("The camera did not respond to the stop command.", statusCode: 502);
        }).RequireAuthorization("AdminOnly");

        // "Home" is a saved preset first and the camera's own home position second, in that order,
        // because plenty of PTZ hardware has no home position at all. The camera this was built
        // against reports HomeSupported=false and answers GotoHomePosition with a SOAP Fault - so
        // the button sent a command that could never work, and PtzControlsView discarded the error,
        // which is why it read as doing nothing rather than as being unsupported.
        //
        // Deliberately keyed on the preset's *name* rather than a token in our database. It costs
        // one extra round trip per press, and in exchange the home position lives entirely on the
        // camera: re-adding the camera here, or pointing a second install at it, finds the same
        // home, and anyone looking at the camera's own preset list can see what put it there.
        app.MapPost("/api/cameras/{id:guid}/ptz/home", async (
            Guid id, ICameraStore store, PtzHomeService home, CancellationToken ct) =>
        {
            var (camera, error) = await ResolvePtzCameraAsync(id, store, ct);
            if (error is not null) return error;

            return await home.GoHomeAsync(camera!, ct) switch
            {
                PtzHomeService.HomeOutcome.Moved => Results.Ok(new { success = true }),
                PtzHomeService.HomeOutcome.NoHomeSaved => Results.Problem(
                    "This camera has no home position. Point it where you want home to be, then press and hold Home to save it.",
                    statusCode: 409),
                _ => Results.Problem("The camera did not move to its home position.", statusCode: 502)
            };
        }).RequireAuthorization("AdminOnly");

        // Save wherever the camera is pointing right now as home. PUT rather than a second POST
        // path: it is idempotent and it overwrites one named slot, which is exactly what it does.
        app.MapPut("/api/cameras/{id:guid}/ptz/home", async (
            Guid id, ICameraStore store, PtzHomeService home, CancellationToken ct) =>
        {
            var (camera, error) = await ResolvePtzCameraAsync(id, store, ct);
            if (error is not null) return error;

            return await home.SaveHomeAsync(camera!, ct)
                ? Results.Ok(new { success = true })
                : Results.Problem("The camera would not save this position as a preset.", statusCode: 502);
        }).RequireAuthorization("AdminOnly");
    }

    // Shared validation for all three PTZ commands: camera has to exist, have PTZ turned on, and
    // actually carry the ONVIF service URL/profile token a real command needs to be sent
    // somewhere - the last of which can be missing even with PtzEnabled true, since that flag is
    // independently user-togglable (see CameraInputDto).
    private static async Task<(Camera? Camera, IResult? Error)> ResolvePtzCameraAsync(
        Guid id, ICameraStore store, CancellationToken ct)
    {
        var camera = await store.GetAsync(id, ct);
        if (camera is null) return (null, Results.NotFound());

        if (!camera.PtzEnabled)
            return (null, Results.BadRequest(new { errors = new[] { "PTZ is not enabled for this camera." } }));

        if (string.IsNullOrWhiteSpace(camera.OnvifPtzServiceUrl) || string.IsNullOrWhiteSpace(camera.OnvifProfileToken))
        {
            return (null, Results.BadRequest(new
            {
                errors = new[] { "This camera has no ONVIF PTZ service on record - re-run discovery, or confirm the camera actually supports PTZ." }
            }));
        }

        return (camera, null);
    }
}
