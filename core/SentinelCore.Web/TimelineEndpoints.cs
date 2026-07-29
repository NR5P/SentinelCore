using System.Globalization;
using SentinelCore;

namespace SentinelCore.Web;

public sealed record SetReviewedRequest(bool Reviewed);

public static class TimelineEndpoints
{
    public static void MapTimelineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/timeline/cameras", async (
            IEventStore events,
            ICameraStore cameraStore,
            ITriggerConfigStore triggerConfigStore,
            CancellationToken ct) =>
        {
            var options = await events.GetFilterOptionsAsync(ct);
            var cameras = options.Cameras.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A camera newly set up to record trigger clips should show up here immediately,
            // even before its first AI-confirmed event lands.
            var recordingCameraIds = (await triggerConfigStore.GetEnabledAsync(ct))
                .Where(config => config.RecordClips)
                .Select(config => config.CameraId)
                .ToHashSet();
            if (recordingCameraIds.Count > 0)
            {
                foreach (var localCamera in await cameraStore.GetAllAsync(ct))
                {
                    if (recordingCameraIds.Contains(localCamera.Id))
                        cameras.Add(localCamera.Name);
                }
            }

            return Results.Ok(cameras.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }).RequireAuthorization();

        app.MapGet("/api/timeline/day", async (
            string camera,
            string date,
            IEventStore events,
            ITimelineReviewStore reviews,
            ICameraStore cameraStore,
            ITriggerConfigStore triggerConfigStore,
            ITriggerEventStore triggerEventStore,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(camera))
                return Results.BadRequest(new { error = "A camera is required." });
            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day))
                return Results.BadRequest(new { error = "Date must use yyyy-MM-dd." });

            var start = AtLocalMidnight(day);
            var end = AtLocalMidnight(day.AddDays(1));
            var dayEvents = (await events.GetEventsAsync(start, end, ct))
                .Where(item => string.Equals(item.Camera, camera, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.OccurredAt)
                .ToArray();
            var reviewed = await reviews.GetReviewedAsync(dayEvents.Select(item => item.Id), ct);

            // The AI events above are keyed by whatever free-text camera name the alert carried
            // (Blue Iris or any other external sender); local trigger clips belong to a specific
            // dashboard Camera (Guid-keyed). Matching by name is the only link between the two.
            var localCamera = (await cameraStore.GetAllAsync(ct))
                .FirstOrDefault(c => string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase));
            var triggerConfig = localCamera is not null
                ? await triggerConfigStore.GetAsync(localCamera.Id, ct)
                : null;
            var recordingConfigured = triggerConfig is { Enabled: true, RecordClips: true };
            var preSeconds = triggerConfig?.ClipPreSeconds ?? 5;
            var postSeconds = triggerConfig?.ClipPostSeconds ?? 10;

            IReadOnlyList<TriggerEvent> localTriggerEvents = recordingConfigured
                ? await triggerEventStore.GetEventsInRangeAsync(localCamera!.Id, start, end, ct)
                : [];
            var clipsByEvent = MatchTriggerEvents(dayEvents, localTriggerEvents);
            var segments = dayEvents
                .Where(item => clipsByEvent.ContainsKey(item.Id))
                .Select(item =>
                {
                    var trigger = clipsByEvent[item.Id];
                    return new
                    {
                        eventId = item.Id,
                        triggerEventId = trigger.Id,
                        start = Max(start, trigger.OccurredAt.AddSeconds(-preSeconds)),
                        end = Min(end, trigger.OccurredAt.AddSeconds(postSeconds))
                    };
                })
                .ToArray();

            return Results.Ok(new
            {
                camera,
                date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                start,
                end,
                recording = new
                {
                    configured = recordingConfigured,
                    available = recordingConfigured,
                    sourceCamera = localCamera?.Name ?? camera,
                    error = recordingConfigured
                        ? null
                        : $"No camera in this dashboard has trigger-clip recording enabled for '{camera}'.",
                    mode = "localTriggerClips",
                    preSeconds,
                    postSeconds,
                    segments
                },
                events = dayEvents.Select(item => new
                {
                    item.Id,
                    item.Camera,
                    occurredAt = item.OccurredAt,
                    eventType = item.EventType ?? "unknown",
                    category = Category(item),
                    severity = Severity(item),
                    summary = item.Summary ?? string.Empty,
                    confidence = item.Confidence ?? 0,
                    notified = item.NotificationRecommended == true,
                    reviewed = reviewed.Contains(item.Id),
                    hasImage = !string.IsNullOrWhiteSpace(item.ImagePath) && File.Exists(item.ImagePath),
                    hasClip = clipsByEvent.ContainsKey(item.Id)
                })
            });
        }).RequireAuthorization();

        app.MapPut("/api/events/{id:guid}/reviewed", async (
            Guid id,
            SetReviewedRequest request,
            IEventStore events,
            ITimelineReviewStore reviews,
            CancellationToken ct) =>
        {
            if (await events.GetEventAsync(id, ct) is null)
                return Results.NotFound(new { error = "Event not found." });
            await reviews.SetReviewedAsync(id, request.Reviewed, ct);
            return Results.Ok(new { id, request.Reviewed });
        }).RequireAuthorization();
    }

    private static DateTimeOffset AtLocalMidnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    // The two rows come from the same trigger a few statements apart (see
    // TriggerZoneMonitor.MaybeTriggerAsync: the local TriggerEvent is written, then - only when
    // SendToAlerts is on for that zone - the same snapshot is forwarded into the AI alert
    // pipeline that produces the AlertEventRecord), so their timestamps are always close. This
    // is nearest-timestamp matching between two clocks on the *same* process, not an attempt to
    // reconcile independently-timestamped systems.
    public static IReadOnlyDictionary<Guid, TriggerEvent> MatchTriggerEvents(
        IReadOnlyList<AlertEventRecord> events,
        IReadOnlyList<TriggerEvent> triggerEvents)
    {
        var withClips = triggerEvents.Where(item => !string.IsNullOrWhiteSpace(item.ClipPath)).ToArray();
        var matches = new Dictionary<Guid, TriggerEvent>();

        foreach (var item in events)
        {
            var nearest = withClips
                .Select(trigger => new
                {
                    Trigger = trigger,
                    Distance = Math.Abs((trigger.OccurredAt - item.OccurredAt).TotalSeconds)
                })
                .Where(candidate => candidate.Distance <= 5)
                .OrderBy(candidate => candidate.Distance)
                .FirstOrDefault();
            if (nearest is not null) matches[item.Id] = nearest.Trigger;
        }

        return matches;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) =>
        first > second ? first : second;

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
        first < second ? first : second;

    private static string Category(AlertEventRecord item)
    {
        var value = $"{item.EventType} {item.Summary}".ToLowerInvariant();
        if (ContainsAny(value, "plate", "alpr", "license")) return "plate";
        if (ContainsAny(value, "package", "parcel", "delivery")) return "package";
        if (ContainsAny(value, "animal", "dog", "cat", "bird", "wildlife", "deer")) return "animal";
        if (ContainsAny(value, "vehicle", "car", "truck", "van", "motorcycle", "bicycle")) return "vehicle";
        if (ContainsAny(value, "person", "people", "human")) return "person";
        return "other";
    }

    private static string Severity(AlertEventRecord item)
    {
        var confidence = item.Confidence ?? 0;
        if (item.NotificationRecommended == true && confidence >= 0.85) return "critical";
        if (item.NotificationRecommended == true || confidence >= 0.75) return "high";
        if (confidence >= 0.50) return "medium";
        return "low";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(value.Contains);
}
