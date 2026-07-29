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
            Guid id, PtzMoveRequestDto dto, ICameraStore store, OnvifClient onvif, CancellationToken ct) =>
        {
            var (camera, error) = await ResolvePtzCameraAsync(id, store, ct);
            if (error is not null) return error;

            var ok = await onvif.ContinuousMoveAsync(
                camera!.OnvifPtzServiceUrl!, camera.OnvifProfileToken!,
                Math.Clamp(dto.Pan, -1, 1), Math.Clamp(dto.Tilt, -1, 1), Math.Clamp(dto.Zoom, -1, 1),
                camera.Username, camera.Password, ct);

            return ok ? Results.Ok(new { success = true })
                      : Results.Problem("The camera did not respond to the move command.", statusCode: 502);
        }).RequireAuthorization("AdminOnly");

        app.MapPost("/api/cameras/{id:guid}/ptz/stop", async (
            Guid id, ICameraStore store, OnvifClient onvif, CancellationToken ct) =>
        {
            var (camera, error) = await ResolvePtzCameraAsync(id, store, ct);
            if (error is not null) return error;

            var ok = await onvif.StopAsync(camera!.OnvifPtzServiceUrl!, camera.OnvifProfileToken!, camera.Username, camera.Password, ct);
            return ok ? Results.Ok(new { success = true })
                      : Results.Problem("The camera did not respond to the stop command.", statusCode: 502);
        }).RequireAuthorization("AdminOnly");

        app.MapPost("/api/cameras/{id:guid}/ptz/home", async (
            Guid id, ICameraStore store, OnvifClient onvif, CancellationToken ct) =>
        {
            var (camera, error) = await ResolvePtzCameraAsync(id, store, ct);
            if (error is not null) return error;

            var ok = await onvif.GotoHomePositionAsync(camera!.OnvifPtzServiceUrl!, camera.OnvifProfileToken!, camera.Username, camera.Password, ct);
            return ok ? Results.Ok(new { success = true })
                      : Results.Problem("The camera did not respond to the home command.", statusCode: 502);
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
