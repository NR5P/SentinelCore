using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed class HubDeviceDto
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "light";
    public string Room { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string CommandTopic { get; set; } = string.Empty;
    public string StateTopic { get; set; } = string.Empty;
    public string AvailabilityTopic { get; set; } = string.Empty;
    public string PayloadOn { get; set; } = "ON";
    public string PayloadOff { get; set; } = "OFF";
    public string PayloadAvailable { get; set; } = "online";
    public string PayloadNotAvailable { get; set; } = "offline";
    public string Schema { get; set; } = DeviceTopics.SchemaRaw;
    public string StateValueTemplate { get; set; } = string.Empty;
    public bool Optimistic { get; set; }
    public bool SupportsBrightness { get; set; }
    public string BrightnessCommandTopic { get; set; } = string.Empty;
    public string BrightnessStateTopic { get; set; } = string.Empty;
    public int BrightnessScale { get; set; } = 255;
    public string DiscoverySource { get; set; } = DeviceTopics.SourceManual;
}

public sealed class DeviceCommandDto
{
    // "on", "off", or "toggle". Optional - a brightness-only request leaves the switch alone.
    public string? Action { get; set; }

    // 0-100 regardless of what the device's own scale is; DeviceTopics does the conversion.
    public int? Brightness { get; set; }
}

// /api/devices is protected the same way as every other /api/* route in this project - see
// ApiKeyMiddleware - so there's no per-endpoint auth here. The user-facing role split (viewers
// can switch a device, only admins can add or edit one) lives in the dashboard project's
// DeviceHubProxyEndpoints, which is where real accounts exist.
public static class DeviceHubEndpoints
{
    public static void MapDeviceHubEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/devices");

        // Ahead of the /{id:guid} routes so these names don't get parsed as device ids.
        group.MapGet("/metadata", (IOptionsMonitor<DeviceHubOptions> hubOptions, IOptionsMonitor<MqttOptions> mqttOptions) =>
            Results.Ok(new
            {
                kinds = DeviceTopics.KnownKinds,
                schemas = DeviceTopics.KnownSchemas,
                enabled = hubOptions.CurrentValue.Enabled,
                mqttEnabled = mqttOptions.CurrentValue.Enabled,
                discoverySeconds = hubOptions.CurrentValue.DiscoverySeconds
            }));

        group.MapGet("", async (IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
        {
            await bus.EnsureStateSubscriptionsReadyAsync(ct);
            return Results.Ok((await store.GetAllAsync(ct)).Select(device => ToResponse(device, bus)));
        });

        // Deliberately separate from the list above: the clients poll this every few seconds
        // while the Device Hub tab is open, and it doesn't need to carry every topic and payload
        // string along with it each time.
        group.MapGet("/state", async (IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
        {
            await bus.EnsureStateSubscriptionsReadyAsync(ct);
            var devices = await store.GetAllAsync(ct);
            return Results.Ok(new
            {
                connected = bus.IsConnected,
                devices = devices.Select(device =>
                {
                    var state = bus.GetState(device);
                    return new
                    {
                        id = device.Id,
                        power = state.Power.ToString().ToLowerInvariant(),
                        brightness = state.BrightnessPercent,
                        available = state.Available,
                        lastSeen = state.LastSeen
                    };
                })
            });
        });

        group.MapGet("/{id:guid}", async (Guid id, IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } device ? Results.Ok(ToResponse(device, bus)) : Results.NotFound());

        group.MapPost("", async (HubDeviceDto dto, IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = DateTimeOffset.UtcNow;
            var device = await store.CreateAsync(ToDevice(dto, Guid.NewGuid(), now, now), ct);
            await bus.ResubscribeAsync(ct);
            return Results.Ok(ToResponse(device, bus));
        });

        group.MapPut("/{id:guid}", async (Guid id, HubDeviceDto dto, IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
        {
            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var existing = await store.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var updated = await store.UpdateAsync(id, ToDevice(dto, id, existing.CreatedAt, DateTimeOffset.UtcNow), ct);
            if (updated is null) return Results.NotFound();

            await bus.ResubscribeAsync(ct);
            return Results.Ok(ToResponse(updated, bus));
        });

        group.MapDelete("/{id:guid}", async (Guid id, IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
        {
            if (!await store.DeleteAsync(id, ct)) return Results.NotFound();

            await bus.ResubscribeAsync(ct);
            return Results.Ok(new { deleted = true });
        });

        group.MapPost("/{id:guid}/command", async (
            Guid id, DeviceCommandDto dto, IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
        {
            var device = await store.GetAsync(id, ct);
            if (device is null) return Results.NotFound();
            if (!device.Enabled) return Results.BadRequest(new { error = "This device is turned off in the Device Hub." });

            DevicePower? power = dto.Action?.Trim().ToLowerInvariant() switch
            {
                "on" => DevicePower.On,
                "off" => DevicePower.Off,
                // Toggling is resolved here rather than in DeviceTopics because this is the only
                // layer that knows what the device is currently doing. An unknown state toggles
                // to on, which is what someone pressing a light switch expects.
                "toggle" => bus.GetState(device).Power == DevicePower.On ? DevicePower.Off : DevicePower.On,
                null or "" => null,
                _ => throw new BadHttpRequestException("Action must be 'on', 'off', or 'toggle'.")
            };

            if (dto.Brightness is int brightness && brightness is < 0 or > 100)
                return Results.BadRequest(new { error = "Brightness must be between 0 and 100." });
            if (dto.Brightness is not null && !device.SupportsBrightness)
                return Results.BadRequest(new { error = $"{device.Name} does not support brightness." });

            var result = await bus.SendCommandAsync(device, power, dto.Brightness, ct);
            if (!result.Published)
                return Results.Json(new { sent = false, error = result.Error }, statusCode: 502);

            var state = bus.GetState(device);
            return Results.Ok(new
            {
                sent = true,
                acknowledged = result.Acknowledged,
                power = state.Power.ToString().ToLowerInvariant(),
                brightness = state.BrightnessPercent
            });
        });

        group.MapPost("/discover", (
            MqttDeviceDiscovery discovery, IOptionsMonitor<MqttOptions> mqttOptions, IOptionsMonitor<DeviceHubOptions> hubOptions) =>
        {
            if (!mqttOptions.CurrentValue.Enabled || !hubOptions.CurrentValue.Enabled)
                return Results.BadRequest(new { error = "Turn on MQTT and the Device Hub in Settings before scanning for devices." });

            return Results.Ok(new
            {
                scanId = discovery.StartScan(),
                scanSeconds = hubOptions.CurrentValue.DiscoverySeconds
            });
        });

        group.MapGet("/discover/{scanId:guid}", (Guid scanId, MqttDeviceDiscovery discovery) =>
            discovery.GetScanStatus(scanId) is { } snapshot
                ? Results.Ok(new
                {
                    status = snapshot.Status.ToString().ToLowerInvariant(),
                    results = snapshot.Results
                })
                : Results.NotFound());
    }

    private static object ToResponse(HubDevice device, MqttDeviceBus bus)
    {
        var state = bus.GetState(device);
        return new
        {
            device.Id,
            device.Name,
            device.Kind,
            device.Room,
            device.Enabled,
            device.CommandTopic,
            device.StateTopic,
            device.AvailabilityTopic,
            device.PayloadOn,
            device.PayloadOff,
            device.PayloadAvailable,
            device.PayloadNotAvailable,
            device.Schema,
            device.StateValueTemplate,
            device.Optimistic,
            device.SupportsBrightness,
            device.BrightnessCommandTopic,
            device.BrightnessStateTopic,
            device.BrightnessScale,
            device.DiscoverySource,
            device.CreatedAt,
            device.UpdatedAt,
            Power = state.Power.ToString().ToLowerInvariant(),
            Brightness = state.BrightnessPercent,
            state.Available,
            state.LastSeen,
            Commandable = DeviceTopics.IsCommandable(device.Kind)
        };
    }

    private static HubDevice ToDevice(HubDeviceDto dto, Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt) => new(
        id,
        dto.Name.Trim(),
        dto.Kind.Trim().ToLowerInvariant(),
        dto.Room.Trim(),
        dto.Enabled,
        dto.CommandTopic.Trim(),
        dto.StateTopic.Trim(),
        dto.AvailabilityTopic.Trim(),
        string.IsNullOrEmpty(dto.PayloadOn) ? "ON" : dto.PayloadOn,
        string.IsNullOrEmpty(dto.PayloadOff) ? "OFF" : dto.PayloadOff,
        dto.PayloadAvailable,
        dto.PayloadNotAvailable,
        DeviceTopics.IsKnownSchema(dto.Schema) ? dto.Schema.Trim().ToLowerInvariant() : DeviceTopics.SchemaRaw,
        dto.StateValueTemplate.Trim(),
        dto.Optimistic,
        dto.SupportsBrightness,
        dto.BrightnessCommandTopic.Trim(),
        dto.BrightnessStateTopic.Trim(),
        dto.BrightnessScale <= 0 ? 255 : dto.BrightnessScale,
        string.IsNullOrWhiteSpace(dto.DiscoverySource) ? DeviceTopics.SourceManual : dto.DiscoverySource.Trim(),
        createdAt,
        updatedAt);

    private static List<string> Validate(HubDeviceDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Name is required.");
        if (!DeviceTopics.IsKnownKind(dto.Kind))
            errors.Add($"Kind must be one of: {string.Join(", ", DeviceTopics.KnownKinds)}.");
        if (!DeviceTopics.IsKnownSchema(dto.Schema))
            errors.Add($"Schema must be one of: {string.Join(", ", DeviceTopics.KnownSchemas)}.");

        var commandable = DeviceTopics.IsCommandable(dto.Kind);
        if (commandable && string.IsNullOrWhiteSpace(dto.CommandTopic))
            errors.Add("Command Topic is required for a device that can be switched.");
        if (!commandable && string.IsNullOrWhiteSpace(dto.StateTopic))
            errors.Add("State Topic is required for a read-only sensor.");

        // A wildcard in a topic this app publishes to would broadcast every command to every
        // device on the broker, so it's refused here rather than discovered the hard way.
        foreach (var (label, topic) in new[]
                 {
                     ("Command Topic", dto.CommandTopic),
                     ("State Topic", dto.StateTopic),
                     ("Availability Topic", dto.AvailabilityTopic),
                     ("Brightness Command Topic", dto.BrightnessCommandTopic),
                     ("Brightness State Topic", dto.BrightnessStateTopic)
                 })
        {
            if (string.IsNullOrWhiteSpace(topic)) continue;
            if (!DeviceTopics.IsValidConcreteTopic(topic))
                errors.Add($"{label} must name a single topic - no '+' or '#' wildcards, and no leading or trailing spaces.");
        }

        if (dto.SupportsBrightness)
        {
            // A raw payload carries one value, so brightness has nowhere to go unless it has a
            // topic of its own. JSON devices can carry it alongside the state on one topic.
            var isJson = string.Equals(dto.Schema, DeviceTopics.SchemaJson, StringComparison.OrdinalIgnoreCase);
            if (!isJson && string.IsNullOrWhiteSpace(dto.BrightnessCommandTopic))
                errors.Add("A raw-payload device needs a Brightness Command Topic of its own to support brightness.");
            if (dto.BrightnessScale is < 1 or > 65535)
                errors.Add("Brightness Scale must be between 1 and 65535.");
        }

        if (string.Equals(dto.Schema, DeviceTopics.SchemaJson, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(dto.StateTopic) &&
            string.IsNullOrWhiteSpace(dto.StateValueTemplate))
        {
            errors.Add("A JSON device needs a State Value Template naming the key that holds ON/OFF, e.g. 'state'.");
        }

        return errors;
    }
}
