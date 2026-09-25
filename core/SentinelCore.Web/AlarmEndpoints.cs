using System.Security.Claims;

namespace SentinelCore.Web;

public sealed record AlarmArmRequest(string Mode, bool Force = false, string? Pin = null);
public sealed record AlarmDisarmRequest(string? Pin = null);
public sealed record AlarmPinRequest(string? Pin, string? CurrentPin = null);
public sealed record AlarmOrderRequest(List<Guid> Ids);

// /api/alarm - the home alarm. Anyone signed in can see it, arm it and disarm it (with the PIN,
// if one is set): that is what the household does with an alarm. Sensors, delays, sirens and
// the PIN are configuration, held to the same admin bar as the Device Hub's devices.
public static class AlarmEndpoints
{
    public static void MapAlarmEndpoints(this WebApplication app)
    {
        var alarm = app.MapGroup("/api/alarm").RequireAuthorization();

        // Always answers, even when the alarm is off, so clients know whether to show the tab.
        alarm.MapGet("/status", async (HttpContext context, AlarmEngine engine, AppSettingsFile settings, CancellationToken ct) =>
        {
            var options = await ReadOptionsAsync(settings, ct);
            var hub = await settings.GetSectionAsync<DeviceHubSettingsDto>("DeviceHub", ct);
            var mqtt = await settings.GetSectionAsync<MqttSettingsDto>("Mqtt", ct);
            var hubEnabled = (hub?.Enabled ?? false) && (mqtt?.Enabled ?? false);
            if (!options.Enabled) return Results.Ok(new { enabled = false, hubEnabled });

            var snapshot = await engine.GetStatusAsync(ct);
            return Results.Ok(new
            {
                enabled = true,
                hubEnabled,
                canEdit = IsAdmin(context),
                snapshot.Mode,
                snapshot.Status,
                snapshot.ChangedAt,
                snapshot.ChangedBy,
                snapshot.ExitUntil,
                snapshot.PendingUntil,
                snapshot.TriggeredAt,
                snapshot.TriggeredBy,
                snapshot.SirenOn,
                snapshot.PinSet,
                snapshot.BrokerConnected,
                snapshot.Ready,
                snapshot.Issues,
                snapshot.Sensors,
                snapshot.Config,
                snapshot.Now,
                modes = AlarmModes.All,
                zones = AlarmZones.All
            });
        });

        var enabled = alarm.MapGroup("").AddEndpointFilter(async (invocation, next) =>
        {
            var settings = invocation.HttpContext.RequestServices.GetRequiredService<AppSettingsFile>();
            var options = await ReadOptionsAsync(settings, invocation.HttpContext.RequestAborted);
            return options.Enabled
                ? await next(invocation)
                : Results.NotFound(new { error = "The alarm is turned off in Settings." });
        });

        enabled.MapPost("/arm", async (AlarmArmRequest request, HttpContext context, AlarmEngine engine, CancellationToken ct) =>
            Reply(await engine.ArmAsync(request.Mode?.Trim().ToLowerInvariant() ?? "", request.Force, request.Pin, Actor(context), ct)));

        enabled.MapPost("/disarm", async (AlarmDisarmRequest? request, HttpContext context, AlarmEngine engine, CancellationToken ct) =>
            Reply(await engine.DisarmAsync(request?.Pin, Actor(context), ct)));

        enabled.MapPost("/panic", async (HttpContext context, AlarmEngine engine, CancellationToken ct) =>
            Reply(await engine.PanicAsync(Actor(context), ct)));

        enabled.MapGet("/events", async (int? limit, AlarmEngine engine, CancellationToken ct) =>
            Results.Ok(await engine.GetEventsAsync(limit ?? 200, ct)));

        // Configuration from here down.
        var admin = enabled.MapGroup("").RequireAuthorization("AdminOnly");

        admin.MapPost("/test", async (HttpContext context, AlarmEngine engine, CancellationToken ct) =>
            Reply(await engine.TestAsync(Actor(context), ct)));

        admin.MapGet("/sensors", async (AlarmEngine engine, CancellationToken ct) => Results.Ok(await engine.GetSensorsAsync(ct)));

        admin.MapPost("/sensors", async (AlarmSensor input, HttpContext context, AlarmEngine engine, CancellationToken ct) =>
        {
            input.Id = Guid.Empty;
            var (sensor, error) = await engine.SaveSensorAsync(input, Actor(context), ct);
            return error is null ? Results.Created($"/api/alarm/sensors/{sensor!.Id}", sensor) : Results.BadRequest(new { error });
        });

        admin.MapPut("/sensors/order", async (AlarmOrderRequest request, AlarmEngine engine, CancellationToken ct) =>
        {
            await engine.ReorderAsync(request.Ids ?? [], ct);
            return Results.NoContent();
        });

        admin.MapPut("/sensors/{id:guid}", async (Guid id, AlarmSensor input, HttpContext context, AlarmEngine engine, CancellationToken ct) =>
        {
            input.Id = id;
            if ((await engine.GetSensorsAsync(ct)).All(s => s.Id != id)) return Results.NotFound();
            var (sensor, error) = await engine.SaveSensorAsync(input, Actor(context), ct);
            return error is null ? Results.Ok(sensor) : Results.BadRequest(new { error });
        });

        admin.MapDelete("/sensors/{id:guid}", async (Guid id, HttpContext context, AlarmEngine engine, CancellationToken ct) =>
            await engine.DeleteSensorAsync(id, Actor(context), ct) ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/config", async (AlarmEngine engine, CancellationToken ct) => Results.Ok(await engine.GetConfigAsync(ct)));

        admin.MapPut("/config", async (AlarmConfigView input, HttpContext context, AlarmEngine engine, CancellationToken ct) =>
        {
            var error = await engine.SaveConfigAsync(input with
            {
                SirenDeviceIds = input.SirenDeviceIds ?? [],
                CameraIds = input.CameraIds ?? []
            }, Actor(context), ct);
            return error is null ? Results.Ok(await engine.GetConfigAsync(ct)) : Results.BadRequest(new { error });
        });

        admin.MapPut("/pin", async (AlarmPinRequest request, HttpContext context, AlarmEngine engine, CancellationToken ct) =>
        {
            var error = await engine.SetPinAsync(request.Pin, request.CurrentPin, IsAdmin(context), Actor(context), ct);
            return error is null ? Results.NoContent() : Results.BadRequest(new { error });
        });
    }

    private static IResult Reply(AlarmCommandResult result) => result.Ok
        ? Results.Ok(new { ok = true, note = result.Error })
        : Results.Json(new { ok = false, error = result.Error, openSensors = result.OpenSensors }, statusCode: result.StatusCode);

    private static string? Actor(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } && context.User.Identity?.Name is { Length: > 0 } name
            ? name.ToLowerInvariant()
            : null;

    private static bool IsAdmin(HttpContext context) =>
        string.Equals(context.User.FindFirstValue(ClaimTypes.Role), "Admin", StringComparison.OrdinalIgnoreCase);

    public static async Task<AlarmSettingsDto> ReadOptionsAsync(AppSettingsFile settings, CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<AlarmSettingsDto>(AlarmSettingsDto.SectionName, cancellationToken) ?? new AlarmSettingsDto();
}
