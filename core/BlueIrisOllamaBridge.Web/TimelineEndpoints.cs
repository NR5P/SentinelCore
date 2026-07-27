using System.Globalization;
using BlueIrisOllamaBridge;

namespace BlueIrisOllamaBridge.Web;

public sealed record SetReviewedRequest(bool Reviewed);

public static class TimelineEndpoints
{
    public static void MapTimelineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/timeline/cameras", async (
            IEventStore events,
            AppSettingsFile settings,
            CancellationToken ct) =>
        {
            var options = await events.GetFilterOptionsAsync(ct);
            var blueIris = await settings.GetSectionAsync<BlueIrisOptions>(
                BlueIrisOptions.SectionName,
                ct) ?? new BlueIrisOptions();
            var cameras = options.Cameras.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var friendlyName in blueIris.CameraMappings.Keys)
                cameras.Add(friendlyName);
            if (cameras.Count == 0 &&
                blueIris.Enabled &&
                !string.IsNullOrWhiteSpace(blueIris.Camera))
                cameras.Add(blueIris.Camera);
            return Results.Ok(cameras.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }).RequireAuthorization();

        app.MapGet("/api/timeline/day", async (
            string camera,
            string date,
            IEventStore events,
            ITimelineReviewStore reviews,
            BlueIrisTimelineClient blueIris,
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
            var recordings = await blueIris.GetAlertClipsAsync(camera, start, end, ct);
            var clipsByEvent = MatchClips(dayEvents, recordings.Clips);
            var segments = dayEvents
                .Where(item => clipsByEvent.ContainsKey(item.Id))
                .Select(item => new
                {
                    eventId = item.Id,
                    start = Max(start, item.OccurredAt.AddSeconds(-recordings.PreSeconds)),
                    end = Min(end, item.OccurredAt.AddSeconds(recordings.PostSeconds))
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
                    recordings.Configured,
                    recordings.Available,
                    sourceCamera = recordings.Camera,
                    recordings.Error,
                    mode = "triggerClips",
                    preSeconds = recordings.PreSeconds,
                    postSeconds = recordings.PostSeconds,
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

        app.MapGet("/api/timeline/frame", async (
            Guid eventId,
            DateTimeOffset at,
            IEventStore events,
            AppSettingsFile settings,
            BlueIrisTimelineClient blueIris,
            ILogger<BlueIrisTimelineClient> logger,
            CancellationToken ct,
            int width = 1280,
            int height = 720) =>
        {
            var cameraEvent = await events.GetEventAsync(eventId, ct);
            if (cameraEvent is null)
                return Results.NotFound(new { error = "Event not found." });
            if (string.IsNullOrWhiteSpace(cameraEvent.BlueIrisAlertId))
                return Results.NotFound(new { error = "This event has no Blue Iris trigger clip." });

            var options = await settings.GetSectionAsync<BlueIrisOptions>(
                BlueIrisOptions.SectionName,
                ct) ?? new BlueIrisOptions();
            var clipStart = cameraEvent.OccurredAt.AddSeconds(-options.EventClipPreSeconds);
            var clipEnd = cameraEvent.OccurredAt.AddSeconds(options.EventClipPostSeconds);
            if (at < clipStart || at > clipEnd)
                return Results.BadRequest(new { error = "The requested time is outside this trigger clip." });

            try
            {
                var frame = await blueIris.GetFrameAsync(cameraEvent.Camera, at, width, height, ct);
                return frame is null
                    ? Results.NotFound(new { error = "No recorded frame is available at this time." })
                    : Results.File(frame.Bytes, frame.ContentType);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not load a Blue Iris recording frame for {Camera} at {At}",
                    cameraEvent.Camera,
                    at);
                return Results.NotFound(new { error = "No recorded frame is available at this time." });
            }
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

    private static IReadOnlyDictionary<Guid, BlueIrisAlertClip> MatchClips(
        IReadOnlyList<AlertEventRecord> events,
        IReadOnlyList<BlueIrisAlertClip> clips)
    {
        var byRecord = clips
            .GroupBy(clip => clip.AlertRecord, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var matches = new Dictionary<Guid, BlueIrisAlertClip>();

        foreach (var item in events)
        {
            var record = BlueIrisTimelineClient.NormalizeRecord(item.BlueIrisAlertId);
            if (!string.IsNullOrWhiteSpace(record) && byRecord.TryGetValue(record, out var exact))
            {
                matches[item.Id] = exact;
                continue;
            }

            // Old Sentinel rows may predate &ALERT_DB being supplied. A close timestamp match
            // preserves playback for those rows without ever opening unrelated parts of the day.
            if (!string.IsNullOrWhiteSpace(record)) continue;
            var nearest = clips
                .Select(clip => new
                {
                    Clip = clip,
                    Distance = Math.Abs((clip.OccurredAt - item.OccurredAt).TotalSeconds)
                })
                .Where(candidate => candidate.Distance <= 5)
                .OrderBy(candidate => candidate.Distance)
                .FirstOrDefault();
            if (nearest is not null) matches[item.Id] = nearest.Clip;
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
