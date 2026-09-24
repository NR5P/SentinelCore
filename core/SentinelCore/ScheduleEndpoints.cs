using System.ComponentModel.DataAnnotations;

namespace SentinelCore;

public sealed class ScheduleDto
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public string Kind { get; set; } = Schedule.KindDaily;
    public string TimeOfDay { get; set; } = string.Empty;
    public List<int> DaysOfWeek { get; set; } = [];
    public int IntervalSeconds { get; set; } = 900;
    public string WindowStart { get; set; } = string.Empty;
    public string WindowEnd { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public DateTimeOffset? RunAtUtc { get; set; }
    public string SolarEvent { get; set; } = Schedule.SolarDusk;
    public int SolarOffsetMinutes { get; set; }
    public double SolarElevationDegrees { get; set; } = SolarCalculator.CivilTwilightDegrees;

    public string ActionType { get; set; } = AutomationAction.TypeDevice;
    public Guid? DeviceId { get; set; }
    public string DeviceAction { get; set; } = string.Empty;
    public int? DeviceBrightness { get; set; }
    public string MqttTopic { get; set; } = string.Empty;
    public string MqttPayload { get; set; } = string.Empty;
    public bool MqttRetain { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public string WebhookMethod { get; set; } = "POST";
    public string WebhookHeaders { get; set; } = string.Empty;
    public string WebhookBody { get; set; } = string.Empty;
    public string WebhookContentType { get; set; } = "application/json";

    public string CatchUpPolicy { get; set; } = Schedule.CatchUpSkip;
}

// /api/schedules is protected the same way as every other /api/* route here - see
// ApiKeyMiddleware - so there's no separate auth attribute per endpoint below.
public static class ScheduleEndpoints
{
    // How many upcoming firings the preview returns. Enough to see a weekly pattern, few enough
    // that computing them costs nothing.
    private const int PreviewCount = 5;

    public static void MapScheduleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/schedules");

        // Ahead of the /{id:guid} routes so these names aren't parsed as schedule ids.
        group.MapGet("/metadata", async (IDeviceStore devices, ScheduleContext context, CancellationToken ct) =>
        {
            var location = context.Location;

            return Results.Ok(new
            {
                kinds = Schedule.KnownKinds,
                solarEvents = Schedule.KnownSolarEvents,
                darknessLevels = DarknessLevel.Presets,
                actionTypes = AutomationAction.KnownTypes,
                catchUpPolicies = Schedule.KnownCatchUpPolicies,
                deviceActions = new[] { "on", "off", "toggle", "open", "close", "stop", "press", "lock", "unlock" },
                tokens = AutomationTemplate.ScheduleTokens,
                minimumIntervalSeconds = ScheduleTiming.MinimumIntervalSeconds,

                // So a client can grey out the sun-following options and say why, rather than
                // offering a choice that would silently never fire.
                locationSet = location is not null,
                timeZone = context.TimeZone.Id,

                // Only commandable devices: a sensor is display-only and there is nothing a
                // schedule could tell it to do.
                devices = (await devices.GetAllAsync(ct))
                    .Where(device => DeviceTopics.IsCommandable(device.Kind))
                    .Select(device => new
                    {
                        id = device.Id,
                        name = device.Name,
                        kind = device.Kind,
                        room = device.Room,
                        enabled = device.Enabled,
                        supportsBrightness = device.SupportsBrightness,
                        momentary = DeviceTopics.IsMomentary(device),
                        coverLike = DeviceTopics.IsCoverLike(device.Kind),
                        hasStop = !string.IsNullOrWhiteSpace(device.PayloadStop)
                    })
            });
        });

        // Next few firings for a schedule that has not been saved - the only practical way to
        // check that a cron expression, a day-of-week set, or a darkness level says what was
        // meant before committing to it.
        group.MapPost("/preview", (ScheduleDto dto, ScheduleContext context) =>
        {
            var errors = Validate(dto, context);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = DateTimeOffset.UtcNow;
            var schedule = ToSchedule(dto, Guid.Empty, now, now);
            var occurrences = ScheduleTiming.NextOccurrences(
                schedule, now, context.TimeZone, context.Location, PreviewCount);

            return Results.Ok(new
            {
                occurrences = occurrences.Select(o => new
                {
                    utc = o.ToUniversalTime(),
                    local = TimeZoneInfo.ConvertTime(o, context.TimeZone).ToString("yyyy-MM-dd HH:mm:ss")
                }),
                timeZone = context.TimeZone.Id,
                explanation = Explain(schedule, context)
            });
        });

        group.MapGet("", async (IScheduleStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, IScheduleStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } schedule ? Results.Ok(schedule) : Results.NotFound());

        group.MapGet("/{id:guid}/runs", async (Guid id, int? limit, IScheduleStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is null
                ? Results.NotFound()
                : Results.Ok(await store.GetRunsAsync(id, limit ?? 50, ct)));

        group.MapPost("", async (ScheduleDto dto, IScheduleStore store, ScheduleContext context, CancellationToken ct) =>
        {
            var errors = Validate(dto, context);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = DateTimeOffset.UtcNow;
            var schedule = ToSchedule(dto, Guid.NewGuid(), now, now);

            // Worked out here rather than left for the worker so the response can show when it
            // will next run, straight after saving.
            schedule = schedule with { NextDueUtc = context.NextOccurrence(schedule, now) };

            return Results.Ok(await store.CreateAsync(schedule, ct));
        });

        group.MapPut("/{id:guid}", async (
            Guid id, ScheduleDto dto, IScheduleStore store, ScheduleContext context, CancellationToken ct) =>
        {
            var errors = Validate(dto, context);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var existing = await store.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var now = DateTimeOffset.UtcNow;
            var schedule = ToSchedule(dto, id, existing.CreatedAt, now);

            // Recomputed from scratch: the edit may have changed the timing entirely, and the
            // old next-due time could be anything from stale to nonsensical for the new rule.
            schedule = schedule with { NextDueUtc = context.NextOccurrence(schedule, now) };

            var updated = await store.UpdateAsync(id, schedule, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        group.MapDelete("/{id:guid}", async (Guid id, IScheduleStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        // Enable/disable on its own, so the apps can offer a switch per row without having to
        // send back a whole schedule they may not have every field of.
        group.MapPost("/{id:guid}/enabled", async (
            Guid id, ScheduleEnabledDto dto, IScheduleStore store, ScheduleContext context, CancellationToken ct) =>
        {
            var existing = await store.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            await store.SetEnabledAsync(id, dto.Enabled, ct);

            // Re-enabling recomputes the next firing; an old one from before it was switched off
            // would otherwise be in the past and fire immediately.
            await store.SetNextDueAsync(
                id, dto.Enabled ? context.NextOccurrence(existing, DateTimeOffset.UtcNow) : null, ct);

            return Results.Ok(await store.GetAsync(id, ct));
        });

        // Fire now, ignoring the timing entirely - the schedules equivalent of the automation
        // rules' test button, and equally the way to shut the coop early on a given evening.
        group.MapPost("/{id:guid}/run", async (Guid id, IScheduleStore store, ScheduleRunner runner, CancellationToken ct) =>
        {
            var schedule = await store.GetAsync(id, ct);
            if (schedule is null) return Results.NotFound();

            var run = await runner.ExecuteAsync(schedule, DateTimeOffset.UtcNow, ct);

            return run.Status == ScheduleRun.StatusOk
                ? Results.Ok(new { fired = true, detail = run.Detail })
                : Results.Json(new { fired = false, error = run.Detail }, statusCode: 502);
        });
    }

    // A plain-English restatement of what a schedule does, so every client can show one without
    // four copies of the same sentence-building logic.
    private static string Explain(Schedule schedule, ScheduleContext context)
    {
        var days = schedule.DaysOfWeek.Count == 0
            ? "every day"
            : "on " + string.Join(", ", schedule.DaysOfWeek.OrderBy(d => d).Select(d => ((DayOfWeek)d).ToString()));

        return schedule.Kind?.ToLowerInvariant() switch
        {
            Schedule.KindDaily => $"At {schedule.TimeOfDay}, {days}.",
            Schedule.KindInterval => DescribeInterval(schedule),
            Schedule.KindCron => $"On the cron expression \"{schedule.CronExpression}\".",
            Schedule.KindOnce => schedule.RunAtUtc is { } runAt
                ? $"Once, at {TimeZoneInfo.ConvertTime(runAt, context.TimeZone):yyyy-MM-dd HH:mm}."
                : "Once, at a time that hasn't been set.",
            Schedule.KindSolar => DescribeSolar(schedule, days),
            _ => "This schedule's timing isn't set up yet."
        };
    }

    private static string DescribeInterval(Schedule schedule)
    {
        var every = schedule.IntervalSeconds % 3600 == 0
            ? $"{schedule.IntervalSeconds / 3600} hour(s)"
            : schedule.IntervalSeconds % 60 == 0
                ? $"{schedule.IntervalSeconds / 60} minute(s)"
                : $"{schedule.IntervalSeconds} seconds";

        var window = ScheduleTiming.TryParseTimeOfDay(schedule.WindowStart, out _) &&
                     ScheduleTiming.TryParseTimeOfDay(schedule.WindowEnd, out _)
            ? $", between {schedule.WindowStart} and {schedule.WindowEnd}"
            : "";

        return $"Every {every}{window}.";
    }

    private static string DescribeSolar(Schedule schedule, string days)
    {
        var offset = schedule.SolarOffsetMinutes switch
        {
            0 => "at",
            > 0 => $"{schedule.SolarOffsetMinutes} minutes after",
            _ => $"{Math.Abs(schedule.SolarOffsetMinutes)} minutes before"
        };

        if (!Schedule.UsesElevation(schedule.SolarEvent))
            return $"{Capitalise(offset)} {schedule.SolarEvent}, {days}.";

        var level = DarknessLevel.Presets
            .FirstOrDefault(preset => Math.Abs(preset.ElevationDegrees - schedule.SolarElevationDegrees) < 0.01);

        var described = level is not null
            ? level.Label.ToLowerInvariant()
            : $"the sun {Math.Abs(schedule.SolarElevationDegrees):0.#} degrees below the horizon";

        var whichEnd = Schedule.IsEveningSolarEvent(schedule.SolarEvent) ? "in the evening" : "in the morning";
        return $"{Capitalise(offset)} {described} {whichEnd}, {days}. The clock time follows the seasons on its own.";
    }

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    internal static Schedule ToSchedule(ScheduleDto dto, Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt) => new(
        id,
        dto.Name.Trim(),
        dto.Enabled,
        Normalise(dto.Kind, Schedule.KnownKinds, Schedule.KindDaily),
        dto.TimeOfDay.Trim(),
        // Deduplicated and sorted so two schedules meaning the same thing store the same thing,
        // and an out-of-range number can never make it into the timing loop.
        dto.DaysOfWeek.Where(day => day is >= 0 and <= 6).Distinct().Order().ToList(),
        Math.Max(dto.IntervalSeconds, ScheduleTiming.MinimumIntervalSeconds),
        dto.WindowStart.Trim(),
        dto.WindowEnd.Trim(),
        dto.CronExpression.Trim(),
        dto.RunAtUtc,
        Normalise(dto.SolarEvent, Schedule.KnownSolarEvents, Schedule.SolarDusk),
        Math.Clamp(dto.SolarOffsetMinutes, -720, 720),
        Math.Clamp(dto.SolarElevationDegrees, SolarCalculator.MinimumElevationDegrees, SolarCalculator.MaximumElevationDegrees),
        new AutomationAction(
            Normalise(dto.ActionType, AutomationAction.KnownTypes, AutomationAction.TypeDevice),
            dto.DeviceId,
            dto.DeviceAction.Trim().ToLowerInvariant(),
            dto.DeviceBrightness,
            dto.MqttTopic.Trim(),
            dto.MqttPayload,
            dto.MqttRetain,
            dto.WebhookUrl.Trim(),
            string.IsNullOrWhiteSpace(dto.WebhookMethod) ? "POST" : dto.WebhookMethod.Trim().ToUpperInvariant(),
            dto.WebhookHeaders,
            dto.WebhookBody,
            string.IsNullOrWhiteSpace(dto.WebhookContentType) ? "application/json" : dto.WebhookContentType.Trim()),
        Normalise(dto.CatchUpPolicy, Schedule.KnownCatchUpPolicies, Schedule.CatchUpSkip),
        LastRunUtc: null,
        LastStatus: string.Empty,
        LastDetail: string.Empty,
        NextDueUtc: null,
        RunCount: 0,
        createdAt,
        updatedAt);

    private static string Normalise(string? value, IReadOnlyList<string> known, string fallback) =>
        known.FirstOrDefault(k => string.Equals(k, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    // Everything that would otherwise become a schedule that quietly never fires. A schedule
    // that looks saved and does nothing is the worst outcome here, so each case is refused with
    // a message saying what to do about it.
    internal static List<string> Validate(ScheduleDto dto, ScheduleContext context, bool forRestore = false)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Name is required.");

        if (!Schedule.IsKnownKind(dto.Kind))
            errors.Add($"Timing must be one of: {string.Join(", ", Schedule.KnownKinds)}.");

        switch (dto.Kind?.ToLowerInvariant())
        {
            case Schedule.KindDaily:
                if (!ScheduleTiming.TryParseTimeOfDay(dto.TimeOfDay, out _))
                    errors.Add("Time of day must be in 24-hour HH:mm form, e.g. 18:00.");
                break;

            case Schedule.KindInterval:
                if (dto.IntervalSeconds < ScheduleTiming.MinimumIntervalSeconds)
                    errors.Add($"The interval must be at least {ScheduleTiming.MinimumIntervalSeconds} seconds.");

                var hasStart = ScheduleTiming.TryParseTimeOfDay(dto.WindowStart, out _);
                var hasEnd = ScheduleTiming.TryParseTimeOfDay(dto.WindowEnd, out _);
                var startBlank = string.IsNullOrWhiteSpace(dto.WindowStart);
                var endBlank = string.IsNullOrWhiteSpace(dto.WindowEnd);

                if (!startBlank && !hasStart) errors.Add("Window start must be in 24-hour HH:mm form, or left empty.");
                if (!endBlank && !hasEnd) errors.Add("Window end must be in 24-hour HH:mm form, or left empty.");

                // Half a window would be ignored entirely, which reads as the window not working.
                if (startBlank != endBlank)
                    errors.Add("Set both ends of the time window, or leave both empty to run all day.");
                break;

            case Schedule.KindCron:
                if (!ScheduleTiming.TryParseCron(dto.CronExpression, out _))
                    errors.Add("That isn't a cron expression this server understands. Use five fields (minute hour day-of-month month day-of-week), e.g. 0 6 * * 1-5.");
                break;

            case Schedule.KindOnce:
                // A restored one-off that has already run is still a schedule the user made - it
                // comes back as it was and simply never fires again, rather than failing the file.
                if (dto.RunAtUtc is null)
                    errors.Add("Pick the date and time to run at.");
                else if (dto.RunAtUtc <= DateTimeOffset.UtcNow && !forRestore)
                    errors.Add("That date and time has already passed.");
                break;

            case Schedule.KindSolar:
                // Not on restore: the location arrives in the same backup, in the settings half,
                // which is written moments before this and may not have been re-read yet. Should
                // it really be missing, the worker logs the schedule as unschedulable and keeps
                // it, which is the same thing that happens when a location is cleared later.
                if (context.Location is null && !forRestore)
                    errors.Add("Set your location in Settings first - sunrise and sunset can't be worked out without it.");

                if (dto.SolarElevationDegrees is < SolarCalculator.MinimumElevationDegrees or > SolarCalculator.MaximumElevationDegrees)
                    errors.Add($"The darkness level must be between {SolarCalculator.MinimumElevationDegrees} and {SolarCalculator.MaximumElevationDegrees} degrees.");
                break;
        }

        switch (dto.ActionType?.ToLowerInvariant())
        {
            case AutomationAction.TypeDevice:
                if (dto.DeviceId is null) errors.Add("Choose which device this schedule controls.");
                if (string.IsNullOrWhiteSpace(dto.DeviceAction) && dto.DeviceBrightness is null)
                    errors.Add("Choose what to do with the device.");
                if (dto.DeviceBrightness is < 0 or > 100)
                    errors.Add("Brightness must be between 0 and 100.");
                break;

            case AutomationAction.TypeMqtt:
                if (string.IsNullOrWhiteSpace(dto.MqttTopic)) errors.Add("MQTT topic is required.");
                break;

            case AutomationAction.TypeWebhook:
                if (string.IsNullOrWhiteSpace(dto.WebhookUrl)) errors.Add("Webhook URL is required.");
                break;

            default:
                errors.Add($"Action must be one of: {string.Join(", ", AutomationAction.KnownTypes)}.");
                break;
        }

        return errors;
    }
}

public sealed class ScheduleEnabledDto
{
    [Required]
    public bool Enabled { get; set; }
}
