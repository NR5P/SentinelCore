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
    // Optional third command for cover-like devices (cover/gate/garage) that can halt
    // mid-travel. Empty means no Stop control is offered.
    public string PayloadStop { get; set; } = string.Empty;
    // A pulse device - one press publishes PayloadOn and nothing more is expected back.
    public bool Momentary { get; set; }
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
    // "on", "off", or "toggle", plus the kind-flavoured aliases every client speaks: "open",
    // "press", "lock" mean on; "close" and "unlock" mean off; "stop" publishes PayloadStop.
    // Optional - a brightness-only request leaves the switch alone.
    public string? Action { get; set; }

    // 0-100 regardless of what the device's own scale is; DeviceTopics does the conversion.
    public int? Brightness { get; set; }
}

// PUT /api/devices/order - the complete arrangement, first device first.
public sealed class DeviceOrderDto
{
    public List<Guid> DeviceIds { get; set; } = [];
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
                        lastSeen = state.LastSeen,
                        measurements = ToMeasurements(state)
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

            // The DTO doesn't carry SortOrder - editing a device must not throw away where the
            // user dragged it.
            var updated = await store.UpdateAsync(
                id, ToDevice(dto, id, existing.CreatedAt, DateTimeOffset.UtcNow) with { SortOrder = existing.SortOrder }, ct);
            if (updated is null) return Results.NotFound();

            await bus.ResubscribeAsync(ct);
            return Results.Ok(ToResponse(updated, bus));
        });

        // The complete arrangement in one PUT, mirroring /api/live-order for cameras. Ids that
        // don't exist are ignored by the UPDATE; devices left out keep their old position values.
        group.MapPut("/order", async (DeviceOrderDto dto, IDeviceStore store, MqttDeviceBus bus, CancellationToken ct) =>
        {
            if (dto.DeviceIds.Count == 0)
                return Results.BadRequest(new { error = "deviceIds must name at least one device." });
            if (dto.DeviceIds.Count != dto.DeviceIds.Distinct().Count())
                return Results.BadRequest(new { error = "deviceIds must not repeat a device." });

            await store.UpdateOrderAsync(dto.DeviceIds, ct);
            return Results.Ok((await store.GetAllAsync(ct)).Select(device => ToResponse(device, bus)));
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

            var action = dto.Action?.Trim().ToLowerInvariant();

            // Stop is its own path: it publishes PayloadStop rather than a power state, and only
            // exists for devices that were given one.
            if (action == "stop")
            {
                var stopResult = await bus.SendStopAsync(device, ct);
                if (!stopResult.Published)
                    return Results.Json(new { sent = false, error = stopResult.Error }, statusCode: 502);
                var stopState = bus.GetState(device);
                return Results.Ok(new
                {
                    sent = true,
                    acknowledged = stopResult.Acknowledged,
                    power = stopState.Power.ToString().ToLowerInvariant(),
                    brightness = stopState.BrightnessPercent
                });
            }

            // The kind-flavoured aliases all collapse onto the same two payloads - a gate's
            // "open" is its PayloadOn, a lock's "unlock" is its PayloadOff - so any client can
            // speak in the device's own vocabulary without the wire format growing.
            DevicePower? power = action switch
            {
                "on" or "open" or "lock" or "press" => DevicePower.On,
                "off" or "close" or "unlock" => DevicePower.Off,
                // Toggling is resolved here rather than in DeviceTopics because this is the only
                // layer that knows what the device is currently doing. An unknown state toggles
                // to on, which is what someone pressing a light switch expects. A momentary
                // device has no state to toggle, so toggle means press.
                "toggle" => DeviceTopics.IsMomentary(device) || bus.GetState(device).Power != DevicePower.On
                    ? DevicePower.On
                    : DevicePower.Off,
                null or "" => null,
                _ => throw new BadHttpRequestException(
                    "Action must be one of: on, off, toggle, open, close, stop, press, lock, unlock.")
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
            device.PayloadStop,
            // The stored flag ORed with the kind rule, so clients render a button kind as
            // momentary without re-implementing IsMomentary.
            Momentary = DeviceTopics.IsMomentary(device),
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
            device.SortOrder,
            device.CreatedAt,
            device.UpdatedAt,
            Power = state.Power.ToString().ToLowerInvariant(),
            Brightness = state.BrightnessPercent,
            state.Available,
            state.LastSeen,
            Measurements = ToMeasurements(state),
            Commandable = DeviceTopics.IsCommandable(device.Kind)
        };
    }

    // Sorted by key rather than handed over in dictionary order: the clients render these in the
    // order they arrive, and a set of readings that reshuffles itself between polls would be a
    // tile that never sits still. Values are the raw numbers the device published - naming and
    // units are a display concern that no device agrees on well enough to belong here yet.
    private static object[] ToMeasurements(DeviceLiveState state) =>
        state.Measurements
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (object)new { key = pair.Key, value = pair.Value })
            .ToArray();

    internal static HubDevice ToDevice(HubDeviceDto dto, Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt) => new(
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
        dto.PayloadStop.Trim(),
        dto.Momentary,
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
        SortOrder: 0,
        createdAt,
        updatedAt);

    // Public for the same reason MqttDeviceBus.SetStateForTesting is - this project has no
    // InternalsVisibleTo, and these rules decide which real devices can be saved at all.
    public static List<string> Validate(HubDeviceDto dto)
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

        // A pulse has no brightness to hold and no meaning for "off" - refusing here beats a
        // slider that publishes to a gate relay.
        if ((dto.Momentary || string.Equals(dto.Kind, "button", StringComparison.OrdinalIgnoreCase)) && dto.SupportsBrightness)
            errors.Add("A momentary (press-only) device cannot support brightness.");

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

        // A sensor is exempt: it has no ON/OFF to name, and a numeric one - a thermometer, a
        // power meter - has no such key in its payload at all. Requiring the template anyway
        // meant the only way to save one was to invent a key that doesn't exist.
        if (commandable &&
            string.Equals(dto.Schema, DeviceTopics.SchemaJson, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(dto.StateTopic) &&
            string.IsNullOrWhiteSpace(dto.StateValueTemplate))
        {
            errors.Add("A JSON device needs a State Value Template naming the key that holds ON/OFF, e.g. 'state'.");
        }

        return errors;
    }
}
