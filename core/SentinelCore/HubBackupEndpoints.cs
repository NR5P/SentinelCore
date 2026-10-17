namespace SentinelCore;

// The main app's share of a configuration backup: the device hub's devices, the automation
// rules, and the device schedules. All three live in this process's SQLite files, which the
// dashboard project can't reach, so its BackupEndpoints fetches this over HTTP and carries it in
// the backup file as one section - and hands it back here on restore. Before this existed a
// restored install came back with every camera and none of the lights, rules or schedules.
//
// Validate and restore are separate calls because the dashboard's restore is all-or-nothing: it
// asks this side whether the section would be accepted before it writes anything of its own,
// and only then applies it. Both go through the same validators as the edit screens, so a backup
// can't put a device or schedule into the store that the endpoints would have refused.
//
// Ids are kept, as everywhere else in restore, because schedules name a device by Id, and so
// that restoring the same file twice updates what the first restore created instead of
// duplicating it. Nothing here deletes: restore overlays.
public sealed record HubBackup(
    IReadOnlyList<HubDevice>? Devices,
    IReadOnlyList<AutomationRule>? AutomationRules,
    IReadOnlyList<Schedule>? Schedules);

public static class HubBackupEndpoints
{
    public static void MapHubBackupEndpoints(this WebApplication app)
    {
        // Behind ApiKeyMiddleware like every /api route here; the dashboard's admin-only backup
        // endpoints are the only caller.
        var group = app.MapGroup("/api/backup/hub");

        group.MapGet("", async (
            IDeviceStore devices, IAutomationRuleStore rules, IScheduleStore schedules, CancellationToken ct) =>
            Results.Ok(new HubBackup(
                await devices.GetAllAsync(ct),
                await rules.GetAllAsync(ct),
                [.. (await schedules.GetAllAsync(ct)).Select(WithoutRunMemory)])));

        group.MapPost("/validate", (HubBackup backup, ScheduleContext context) =>
            Results.Ok(new { errors = Validate(backup, context) }));

        group.MapPost("/restore", async (
            HubBackup backup, IDeviceStore devices, IAutomationRuleStore rules, IScheduleStore schedules,
            ScheduleContext context, MqttDeviceBus bus, CancellationToken ct) =>
        {
            var errors = Validate(backup, context);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = DateTimeOffset.UtcNow;

            var devicesRestored = 0;
            foreach (var device in backup.Devices ?? [])
            {
                // Normalised through the same mapper a PUT uses, then given back the position the
                // user dragged it to, which the DTO doesn't carry.
                var normalised = DeviceHubEndpoints.ToDevice(ToDto(device), device.Id, device.CreatedAt, now)
                    with { SortOrder = device.SortOrder };
                if (await devices.GetAsync(device.Id, ct) is null)
                    await devices.CreateAsync(normalised, ct);
                else
                    await devices.UpdateAsync(device.Id, normalised, ct);
                devicesRestored++;
            }
            if (devicesRestored > 0) await bus.ResubscribeAsync(ct);

            var rulesRestored = 0;
            foreach (var rule in backup.AutomationRules ?? [])
            {
                var normalised = AutomationRuleEndpoints.ToRule(ToDto(rule), rule.Id, rule.CreatedAt, now);
                if (await rules.GetAsync(rule.Id, ct) is null)
                    await rules.CreateAsync(normalised, ct);
                else
                    await rules.UpdateAsync(rule.Id, normalised, ct);
                rulesRestored++;
            }

            // NextDueUtc is left null on purpose: ScheduleWorker works out the first due time of
            // a schedule it hasn't seen, and never fires one retroactively - so a restore can't
            // set off everything that "should" have run while the machine was down. An existing
            // schedule keeps its own run history; only its configuration is replaced.
            var schedulesRestored = 0;
            foreach (var schedule in backup.Schedules ?? [])
            {
                var normalised = ScheduleEndpoints.ToSchedule(ToDto(schedule), schedule.Id, schedule.CreatedAt, now);
                if (await schedules.GetAsync(schedule.Id, ct) is { } existing)
                {
                    await schedules.UpdateAsync(schedule.Id, normalised with
                    {
                        LastRunUtc = existing.LastRunUtc,
                        LastStatus = existing.LastStatus,
                        LastDetail = existing.LastDetail,
                        RunCount = existing.RunCount
                    }, ct);
                }
                else
                {
                    await schedules.CreateAsync(normalised, ct);
                }
                schedulesRestored++;
            }

            return Results.Ok(new { devicesRestored, rulesRestored, schedulesRestored });
        });
    }

    public static List<string> Validate(HubBackup backup, ScheduleContext context)
    {
        var errors = new List<string>();

        foreach (var device in backup.Devices ?? [])
        foreach (var problem in DeviceHubEndpoints.Validate(ToDto(device)))
            errors.Add($"Device \"{device.Name}\": {problem}");

        foreach (var rule in backup.AutomationRules ?? [])
        foreach (var problem in AutomationRuleEndpoints.Validate(ToDto(rule)))
            errors.Add($"Automation rule \"{rule.Name}\": {problem}");

        foreach (var schedule in backup.Schedules ?? [])
        foreach (var problem in ScheduleEndpoints.Validate(ToDto(schedule), context, forRestore: true))
            errors.Add($"Schedule \"{schedule.Name}\": {problem}");

        return errors;
    }

    // When it last ran, what happened and when it's next due are this install's memory, not the
    // user's configuration - the same line BackupEndpoints draws for a gate check's last state.
    private static Schedule WithoutRunMemory(Schedule schedule) => schedule with
    {
        LastRunUtc = null,
        LastStatus = string.Empty,
        LastDetail = string.Empty,
        NextDueUtc = null,
        RunCount = 0
    };

    private static HubDeviceDto ToDto(HubDevice device) => new()
    {
        Name = device.Name,
        Kind = device.Kind,
        Room = device.Room,
        Enabled = device.Enabled,
        CommandTopic = device.CommandTopic,
        StateTopic = device.StateTopic,
        AvailabilityTopic = device.AvailabilityTopic,
        PayloadOn = device.PayloadOn,
        PayloadOff = device.PayloadOff,
        PayloadStop = device.PayloadStop,
        Momentary = device.Momentary,
        PayloadAvailable = device.PayloadAvailable,
        PayloadNotAvailable = device.PayloadNotAvailable,
        Schema = device.Schema,
        StateValueTemplate = device.StateValueTemplate,
        Optimistic = device.Optimistic,
        SupportsBrightness = device.SupportsBrightness,
        BrightnessCommandTopic = device.BrightnessCommandTopic,
        BrightnessStateTopic = device.BrightnessStateTopic,
        BrightnessScale = device.BrightnessScale,
        DiscoverySource = device.DiscoverySource
    };

    private static AutomationRuleDto ToDto(AutomationRule rule) => new()
    {
        Name = rule.Name,
        Enabled = rule.Enabled,
        Cameras = [.. rule.Cameras],
        Categories = [.. rule.Categories],
        MinimumConfidence = rule.MinimumConfidence,
        OnlyWhenNotificationRecommended = rule.OnlyWhenNotificationRecommended,
        DuplicateCooldownMinutes = rule.DuplicateCooldownMinutes,
        ActionType = rule.ActionType,
        MqttTopic = rule.MqttTopic,
        MqttPayload = rule.MqttPayload,
        MqttRetain = rule.MqttRetain,
        WebhookUrl = rule.WebhookUrl,
        WebhookMethod = rule.WebhookMethod,
        WebhookHeaders = rule.WebhookHeaders,
        WebhookBody = rule.WebhookBody,
        WebhookContentType = rule.WebhookContentType
    };

    private static ScheduleDto ToDto(Schedule schedule) => new()
    {
        Name = schedule.Name,
        Enabled = schedule.Enabled,
        Kind = schedule.Kind,
        TimeOfDay = schedule.TimeOfDay,
        DaysOfWeek = [.. schedule.DaysOfWeek],
        IntervalSeconds = schedule.IntervalSeconds,
        WindowStart = schedule.WindowStart,
        WindowEnd = schedule.WindowEnd,
        CronExpression = schedule.CronExpression,
        RunAtUtc = schedule.RunAtUtc,
        SolarEvent = schedule.SolarEvent,
        SolarOffsetMinutes = schedule.SolarOffsetMinutes,
        SolarElevationDegrees = schedule.SolarElevationDegrees,
        ActionType = schedule.Action.Type,
        DeviceId = schedule.Action.DeviceId,
        DeviceAction = schedule.Action.DeviceAction,
        DeviceBrightness = schedule.Action.DeviceBrightness,
        MqttTopic = schedule.Action.MqttTopic,
        MqttPayload = schedule.Action.MqttPayload,
        MqttRetain = schedule.Action.MqttRetain,
        WebhookUrl = schedule.Action.WebhookUrl,
        WebhookMethod = schedule.Action.WebhookMethod,
        WebhookHeaders = schedule.Action.WebhookHeaders,
        WebhookBody = schedule.Action.WebhookBody,
        WebhookContentType = schedule.Action.WebhookContentType,
        CatchUpPolicy = schedule.CatchUpPolicy,
        WeatherTests = [.. schedule.WeatherTests]
    };
}
