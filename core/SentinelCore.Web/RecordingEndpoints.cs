using SentinelCore;

namespace SentinelCore.Web;

// What the editor sends and receives for a recording window.
//
// StartTime/StopTime are two clock times because that is how the request arrives - "record from
// 8pm to 8:30pm" - even though the stored row keeps a start and a *duration*. The conversion
// happens here, once, on the way in. See RecordingSchedule.DurationSeconds for why the storage
// shape differs from the editing shape.
public sealed record RecordingScheduleDto(
    Guid? Id,
    string Name,
    bool Enabled,
    string Kind,
    // "HH:mm" local. The start for a "daily" window; ignored by the other kinds, which get their
    // start from a cron expression, an absolute instant, or the sun.
    string StartTime,
    // "HH:mm" local. Optional: a client may send DurationSeconds instead, and must for the kinds
    // that have no StartTime to measure from.
    string StopTime,
    IReadOnlyList<int> DaysOfWeek,
    string CronExpression,
    DateTimeOffset? RunAtUtc,
    string SolarEvent,
    int SolarOffsetMinutes,
    double SolarElevationDegrees,
    int? DurationSeconds,
    bool IncludeAudio);

public sealed record RecordingScheduleView(
    Guid Id,
    Guid CameraId,
    string Name,
    bool Enabled,
    string Kind,
    string StartTime,
    string StopTime,
    int DurationSeconds,
    IReadOnlyList<int> DaysOfWeek,
    string CronExpression,
    DateTimeOffset? RunAtUtc,
    string SolarEvent,
    int SolarOffsetMinutes,
    double SolarElevationDegrees,
    bool IncludeAudio,
    DateTimeOffset? LastStartedUtc,
    string LastStatus,
    string LastDetail,
    IReadOnlyList<DateTimeOffset> NextOccurrences);

public static class RecordingEndpoints
{
    // How many upcoming windows the editor previews. The same count the schedules editor uses, and
    // for the same reason: a cron expression or a day-of-week set is close to unreadable, and
    // seeing the next few actual dates is the only practical way to tell whether it says what
    // someone meant.
    private const int PreviewCount = 5;

    public static void MapRecordingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cameras/{cameraId:guid}/recording-schedules", async (
            Guid cameraId, IRecordingScheduleStore store, AppSettingsFile settings, CancellationToken ct) =>
        {
            var schedules = await store.GetForCameraAsync(cameraId, ct);
            var (zone, location) = await ResolveClockAsync(settings, ct);
            return Results.Ok(schedules.Select(schedule => ToView(schedule, zone, location)));
        }).RequireAuthorization();

        app.MapPost("/api/cameras/{cameraId:guid}/recording-schedules", async (
            Guid cameraId, RecordingScheduleDto dto, ICameraStore cameras, IRecordingScheduleStore store,
            AppSettingsFile settings, CancellationToken ct) =>
        {
            if (await cameras.GetAsync(cameraId, ct) is null) return Results.NotFound();

            var errors = Validate(dto, out var durationSeconds);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var now = DateTimeOffset.UtcNow;
            var schedule = FromDto(dto, cameraId, dto.Id ?? Guid.NewGuid(), durationSeconds, now, now);
            await store.SaveAsync(schedule, ct);

            var (zone, location) = await ResolveClockAsync(settings, ct);
            return Results.Ok(ToView(schedule, zone, location));
        }).RequireAuthorization("AdminOnly");

        app.MapPut("/api/cameras/{cameraId:guid}/recording-schedules/{id:guid}", async (
            Guid cameraId, Guid id, RecordingScheduleDto dto, IRecordingScheduleStore store,
            AppSettingsFile settings, CancellationToken ct) =>
        {
            var existing = await store.GetAsync(id, ct);
            if (existing is null || existing.CameraId != cameraId) return Results.NotFound();

            var errors = Validate(dto, out var durationSeconds);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            // LastStarted* is deliberately carried across an edit rather than reset. It is what
            // stops the worker from re-firing a window it has already recorded today, so clearing
            // it on every save would mean editing a schedule at 8:15 records 8:00 all over again.
            var schedule = FromDto(dto, cameraId, id, durationSeconds, existing.CreatedAt, DateTimeOffset.UtcNow) with
            {
                LastStartedUtc = existing.LastStartedUtc,
                LastStatus = existing.LastStatus,
                LastDetail = existing.LastDetail
            };
            await store.SaveAsync(schedule, ct);

            var (zone, location) = await ResolveClockAsync(settings, ct);
            return Results.Ok(ToView(schedule, zone, location));
        }).RequireAuthorization("AdminOnly");

        app.MapDelete("/api/cameras/{cameraId:guid}/recording-schedules/{id:guid}", async (
            Guid cameraId, Guid id, IRecordingScheduleStore store, CancellationToken ct) =>
        {
            var existing = await store.GetAsync(id, ct);
            if (existing is null || existing.CameraId != cameraId) return Results.NotFound();

            // Only the rule is deleted. Recordings it already produced keep their files and their
            // rows - footage outlives the schedule that asked for it, and deleting a rule should
            // never be a way to lose video by accident.
            await store.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        // Answers "when would this actually run?" for a window being filled in but not yet saved.
        // Changes nothing, so a signed-in user is enough - it is how the editor explains itself.
        app.MapPost("/api/recording-schedules/preview", async (
            RecordingScheduleDto dto, AppSettingsFile settings, CancellationToken ct) =>
        {
            var errors = Validate(dto, out var durationSeconds);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var (zone, location) = await ResolveClockAsync(settings, ct);
            var schedule = FromDto(dto, Guid.Empty, Guid.Empty, durationSeconds, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var occurrences = ScheduleTiming.NextOccurrences(
                schedule.ToSchedule(), DateTimeOffset.UtcNow, zone, location, PreviewCount);

            return Results.Ok(new
            {
                durationSeconds,
                occurrences = occurrences.Select(start => new { start, end = start + TimeSpan.FromSeconds(durationSeconds) })
            });
        }).RequireAuthorization();

        app.MapGet("/api/recordings", async (
            Guid? cameraId, int? limit, IRecordingStore store, CancellationToken ct) =>
        {
            var recordings = await store.GetRecentAsync(Math.Clamp(limit ?? 50, 1, 500), cameraId, ct);
            return Results.Ok(recordings);
        }).RequireAuthorization();

        // Streamed from disk with range support so the browser can seek without pulling the whole
        // file first. That matters far more here than it does for a trigger clip: these run to
        // hours, and without ranges seeking would mean downloading gigabytes.
        app.MapGet("/api/recordings/{id:guid}/video", async (
            Guid id, IRecordingStore store, CancellationToken ct) =>
        {
            var recording = await store.GetAsync(id, ct);
            if (recording?.FilePath is null || !File.Exists(recording.FilePath)) return Results.NotFound();

            return Results.File(recording.FilePath, "video/mp4", enableRangeProcessing: true);
        }).RequireAuthorization();
    }

    private static async Task<(TimeZoneInfo Zone, GeoCoordinate? Location)> ResolveClockAsync(
        AppSettingsFile settings, CancellationToken cancellationToken)
    {
        var scheduleOptions = await settings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, cancellationToken);
        var locationOptions = await settings.GetSectionAsync<LocationOptions>(LocationOptions.SectionName, cancellationToken);

        return (RecordingScheduleWorker.ResolveTimeZone(scheduleOptions),
                RecordingScheduleWorker.ResolveLocation(locationOptions));
    }

    private static RecordingSchedule FromDto(
        RecordingScheduleDto dto, Guid cameraId, Guid id, int durationSeconds,
        DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        new(
            id,
            cameraId,
            dto.Name.Trim(),
            dto.Enabled,
            dto.Kind.Trim().ToLowerInvariant(),
            dto.StartTime?.Trim() ?? string.Empty,
            dto.DaysOfWeek ?? [],
            dto.CronExpression?.Trim() ?? string.Empty,
            dto.RunAtUtc,
            dto.SolarEvent?.Trim() ?? string.Empty,
            dto.SolarOffsetMinutes,
            dto.SolarElevationDegrees,
            durationSeconds,
            dto.IncludeAudio,
            LastStartedUtc: null,
            LastStatus: string.Empty,
            LastDetail: string.Empty,
            createdAt,
            updatedAt);

    private static RecordingScheduleView ToView(
        RecordingSchedule schedule, TimeZoneInfo zone, GeoCoordinate? location) =>
        new(
            schedule.Id,
            schedule.CameraId,
            schedule.Name,
            schedule.Enabled,
            schedule.Kind,
            schedule.TimeOfDay,
            StopTimeOf(schedule),
            schedule.DurationSeconds,
            schedule.DaysOfWeek,
            schedule.CronExpression,
            schedule.RunAtUtc,
            schedule.SolarEvent,
            schedule.SolarOffsetMinutes,
            schedule.SolarElevationDegrees,
            schedule.IncludeAudio,
            schedule.LastStartedUtc,
            schedule.LastStatus,
            schedule.LastDetail,
            ScheduleTiming.NextOccurrences(schedule.ToSchedule(), DateTimeOffset.UtcNow, zone, location, PreviewCount));

    // The stop time the editor should show, derived back out of the duration. Empty for a kind with
    // no clock start to add it to - there is no honest "8:30pm" to show for a cron expression that
    // fires at four different times.
    private static string StopTimeOf(RecordingSchedule schedule)
    {
        if (!ScheduleTiming.TryParseTimeOfDay(schedule.TimeOfDay, out var start)) return string.Empty;

        return start.Add(TimeSpan.FromSeconds(schedule.DurationSeconds)).ToString("HH\\:mm");
    }

    public static List<string> Validate(RecordingScheduleDto dto, out int durationSeconds)
    {
        var errors = new List<string>();
        durationSeconds = 0;

        if (string.IsNullOrWhiteSpace(dto.Name))
            errors.Add("Give this recording a name.");

        var kind = dto.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RecordingSchedule.IsKnownKind(kind))
        {
            errors.Add($"'{dto.Kind}' is not a kind of recording schedule.");
            return errors;
        }

        durationSeconds = ResolveDuration(dto, errors);

        switch (kind)
        {
            case RecordingSchedule.KindDaily:
                if (!ScheduleTiming.TryParseTimeOfDay(dto.StartTime, out _))
                    errors.Add("Start time must be a time of day like 20:00.");
                break;

            case RecordingSchedule.KindCron:
                if (!ScheduleTiming.TryParseCron(dto.CronExpression, out _))
                    errors.Add("That is not a valid cron expression.");
                break;

            case RecordingSchedule.KindOnce:
                if (dto.RunAtUtc is null)
                    errors.Add("A one-off recording needs a date and time to run at.");
                break;

            case RecordingSchedule.KindSolar:
                if (!Schedule.KnownSolarEvents.Contains(dto.SolarEvent?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    errors.Add("Choose sunrise, sunset, dusk or dawn.");
                break;
        }

        if (dto.DaysOfWeek is not null && dto.DaysOfWeek.Any(day => day is < 0 or > 6))
            errors.Add("Days of the week must be 0 (Sunday) to 6 (Saturday).");

        return errors;
    }

    // A duration either stated outright or measured between two clock times. The clock-time path is
    // what makes "23:00 to 01:00" mean two hours rather than minus twenty-two: a stop at or before
    // the start is a window that crosses midnight, which is a perfectly ordinary thing to record
    // and needs no special case anywhere else because the stored value is a length.
    private static int ResolveDuration(RecordingScheduleDto dto, List<string> errors)
    {
        int seconds;

        if (dto.DurationSeconds is { } stated)
        {
            seconds = stated;
        }
        else if (ScheduleTiming.TryParseTimeOfDay(dto.StartTime, out var start) &&
                 ScheduleTiming.TryParseTimeOfDay(dto.StopTime, out var stop))
        {
            var span = stop.ToTimeSpan() - start.ToTimeSpan();
            if (span <= TimeSpan.Zero) span += TimeSpan.FromDays(1);
            seconds = (int)span.TotalSeconds;
        }
        else
        {
            errors.Add("Set a stop time, or say how long the recording should last.");
            return 0;
        }

        if (seconds < RecordingSchedule.MinimumDurationSeconds)
            errors.Add($"A recording must last at least {RecordingSchedule.MinimumDurationSeconds / 60} minute(s).");
        else if (seconds > RecordingSchedule.MaximumDurationSeconds)
            errors.Add($"A recording cannot last longer than {RecordingSchedule.MaximumDurationSeconds / 3600} hours.");

        return seconds;
    }
}
