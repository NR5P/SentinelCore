using SentinelCore;

namespace SentinelCore.Web;

// One list for the dashboard's Events tab, joining the two records that a single moment of
// motion produces: the TriggerEvent (the frame that motion saved, and the clip cut around it)
// and the AlertEvent (what the AI made of that frame, and whether it notified).
//
// Those were two separate tabs - "triggers" and "feed" - which made sense when Blue Iris was a
// second, camera-less source of alerts. Once a camera that forwards to the alert pipeline
// started producing both rows for the same event, the split was showing the storage layout
// rather than anything a user cares about: the motion and its analysis are one thing that
// happened, and belong on one card.
//
// Deliberately a NEW endpoint rather than a reshaped /api/events. The shipped iOS, tvOS, and
// Android builds all consume /api/events and /api/triggers in their current shapes (see
// TriggerEndpoints for how load-bearing that is), so both stay exactly as they are and this
// serves the web dashboard only.
public static class ActivityEndpoints
{
    // What links the two rows is TriggerEvent.AlertId, written when the forward is accepted (see
    // TriggerZoneMonitor.ForwardToAlertPipelineAsync). Rows written before that column existed
    // have no key and stay motion-only - matching them up by camera and timestamp after the fact
    // would be a guess, and a wrong guess would staple someone else's analysis to a clip.
    public static void MapActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/activity", async (
            IEventStore eventStore,
            ITriggerEventStore triggerStore,
            ICameraStore cameraStore,
            IImageEmbeddingService imageEmbeddings,
            AppSettingsFile appSettings,
            CancellationToken ct,
            int limit = 50,
            string? camera = null,
            string? eventType = null,
            bool? notifiedOnly = null,
            string? search = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            bool visualSearch = false,
            bool includeUnconfirmed = false) =>
        {
            var capped = Math.Clamp(limit, 1, 500);
            var filter = new EventFilter(camera, eventType, notifiedOnly, search, from, to, capped);

            var alerts = await SearchAlertsAsync(eventStore, imageEmbeddings, appSettings, filter, visualSearch, ct);

            // A camera filter names a camera; triggers are keyed by id. No match means the name
            // belongs to a source with no local camera behind it (Blue Iris, a deleted camera),
            // so there are no triggers to merge in and the query is skipped rather than run
            // unfiltered - which would leak every other camera's motion into a filtered view.
            var cameras = await cameraStore.GetAllAsync(ct);
            var cameraNames = cameras.ToDictionary(c => c.Id, c => c.Name);
            Guid? cameraId = null;
            var cameraFilterMatchedNothing = false;
            if (!string.IsNullOrWhiteSpace(camera))
            {
                var match = cameras.FirstOrDefault(c => string.Equals(c.Name, camera, StringComparison.OrdinalIgnoreCase));
                if (match is null) cameraFilterMatchedNothing = true;
                else cameraId = match.Id;
            }

            var triggers = cameraFilterMatchedNothing
                ? []
                : await triggerStore.GetRecentAsync(capped, cameraId, includeUnconfirmed, ct);

            // eventType/notifiedOnly/search only exist on an analysis, so a motion-only row can
            // never satisfy them - including them would mean a filter for "person" silently
            // returning frames nothing ever looked at. With none of those set, motion-only rows
            // belong in the list: they're the whole content of the old Triggers tab.
            var analysisOnlyFilter = !string.IsNullOrWhiteSpace(eventType)
                || notifiedOnly == true
                || !string.IsNullOrWhiteSpace(search);

            return Results.Ok(BuildActivity(alerts, triggers, cameraNames, analysisOnlyFilter, from, to, capped));
        }).RequireAuthorization();
    }

    // Pure, and public for the tests: the join rules here are the whole point of this endpoint,
    // and they're the part that would silently double-list events or drop clips if they broke.
    public static IReadOnlyList<ActivityItem> BuildActivity(
        IReadOnlyList<AlertEventRecord> alerts,
        IReadOnlyList<TriggerEvent> triggers,
        IReadOnlyDictionary<Guid, string> cameraNames,
        bool analysisOnlyFilter,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int capped)
    {
        // First wins: two triggers claiming one alert can't happen through the write path, and if
        // a hand-edited row ever made it so, listing the moment twice would be worse than picking
        // one of them.
        var triggersByAlertId = triggers
            .Where(t => t.AlertId is not null)
            .GroupBy(t => t.AlertId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<ActivityItem>(alerts.Count + triggers.Count);
        foreach (var alert in alerts)
            items.Add(Merge(alert, triggersByAlertId.GetValueOrDefault(alert.Id)));

        if (!analysisOnlyFilter)
        {
            foreach (var trigger in triggers)
            {
                // A trigger that names an alert is already represented by that alert's card, even
                // when the alert itself fell outside this page - emitting it again here would
                // show the same moment twice, once with the analysis and once without.
                if (trigger.AlertId is not null) continue;
                if (from is not null && trigger.OccurredAt < from) continue;
                if (to is not null && trigger.OccurredAt >= to) continue;
                items.Add(MotionOnly(trigger, cameraNames.GetValueOrDefault(trigger.CameraId, "deleted camera")));
            }
        }

        // Each store was asked for its own page of `capped` rows, so trimming the merged list
        // back to `capped` can leave an item just off the end of one store's page missing from
        // this page. It reappears as the newer rows age out; paging both stores in lockstep
        // would need a shared cursor across two databases, which this list doesn't warrant.
        return items
            .OrderByDescending(i => i.OccurredAt)
            .Take(capped)
            .ToList();
    }

    private static async Task<IReadOnlyList<AlertEventRecord>> SearchAlertsAsync(
        IEventStore eventStore, IImageEmbeddingService imageEmbeddings, AppSettingsFile appSettings,
        EventFilter filter, bool visualSearch, CancellationToken ct)
    {
        // Mirrors /api/events, including reading the settings file live rather than through an
        // IOptionsMonitor this project doesn't bind - see the note there.
        var semanticSearch = visualSearch && !string.IsNullOrWhiteSpace(filter.SearchText)
            ? await appSettings.GetSectionAsync<SemanticSearchSettingsDto>("SemanticSearch", ct)
            : null;

        if (semanticSearch is not { Enabled: true })
            return await eventStore.SearchEventsAsync(filter, ct);

        var queryEmbedding = await imageEmbeddings.EmbedTextAsync(filter.SearchText!, ct);
        return queryEmbedding is not null
            ? await eventStore.SearchBySimilarityAsync(filter, queryEmbedding, semanticSearch.MinimumSimilarity, ct)
            : [];
    }

    private static ActivityItem Merge(AlertEventRecord alert, TriggerEvent? trigger)
    {
        var hasClip = trigger is not null && !string.IsNullOrWhiteSpace(trigger.ClipPath) && File.Exists(trigger.ClipPath);

        // The alert's own copy is the frame the summary is describing, so it wins. The trigger's
        // copy is the same capture and stands in when the alert's image is gone (retention runs
        // over the two image directories independently).
        var imageUrl = !string.IsNullOrWhiteSpace(alert.ImagePath) && File.Exists(alert.ImagePath)
            ? $"/api/events/{alert.Id:D}/image"
            : trigger is not null && File.Exists(trigger.ImagePath)
                ? $"/api/triggers/{trigger.Id:D}/image"
                : null;

        return new ActivityItem(
            Id: alert.Id.ToString("D"),
            Kind: "analyzed",
            Camera: alert.Camera,
            OccurredAt: alert.OccurredAt,
            ImageUrl: imageUrl,
            ClipUrl: hasClip ? $"/api/triggers/{trigger!.Id:D}/clip" : null,
            Status: alert.Status,
            EventType: alert.EventType ?? "unknown",
            Summary: alert.Summary ?? string.Empty,
            Notified: alert.NotificationRecommended == true,
            NotificationReason: alert.NotificationReason ?? string.Empty,
            Confidence: alert.Confidence ?? 0,
            LinkedCameras: alert.LinkedCameras,
            PackageTheftReason: alert.PackageTheftReason,
            IncidentId: alert.IncidentId ?? trigger?.IncidentId,
            TriggerId: trigger?.Id.ToString("D"),
            ZoneName: trigger?.ZoneName,
            Confirmation: trigger?.Confirmation.ToString().ToLowerInvariant(),
            CameraId: trigger?.CameraId,
            TriggerAreaFraction: trigger?.TriggerAreaFraction);
    }

    private static ActivityItem MotionOnly(TriggerEvent trigger, string cameraName) => new(
        // Prefixed so a trigger id and an alert id can never collide in the same list, and so the
        // client can tell which endpoint a row's media hangs off without a second field.
        Id: $"trigger:{trigger.Id:D}",
        Kind: "motion",
        Camera: cameraName,
        OccurredAt: trigger.OccurredAt,
        ImageUrl: File.Exists(trigger.ImagePath) ? $"/api/triggers/{trigger.Id:D}/image" : null,
        ClipUrl: !string.IsNullOrWhiteSpace(trigger.ClipPath) && File.Exists(trigger.ClipPath)
            ? $"/api/triggers/{trigger.Id:D}/clip"
            : null,
        Status: "motion",
        EventType: null,
        Summary: null,
        Notified: false,
        NotificationReason: null,
        Confidence: null,
        LinkedCameras: null,
        PackageTheftReason: null,
        IncidentId: trigger.IncidentId,
        TriggerId: trigger.Id.ToString("D"),
        ZoneName: trigger.ZoneName,
        Confirmation: trigger.Confirmation.ToString().ToLowerInvariant(),
        CameraId: trigger.CameraId,
        TriggerAreaFraction: trigger.TriggerAreaFraction);
}

// Kind is what the card renders from: "analyzed" rows have a summary and a notify decision,
// "motion" rows have neither and never will - nothing looked at them, either because the camera
// doesn't forward to the alert pipeline or because the forward was refused.
public sealed record ActivityItem(
    string Id,
    string Kind,
    string Camera,
    DateTimeOffset OccurredAt,
    string? ImageUrl,
    string? ClipUrl,
    string Status,
    string? EventType,
    string? Summary,
    bool Notified,
    string? NotificationReason,
    double? Confidence,
    string? LinkedCameras,
    string? PackageTheftReason,
    string? IncidentId,
    string? TriggerId,
    string? ZoneName,
    string? Confirmation,
    // Both come from the trigger side and are null on an item with no trigger behind it (a Blue
    // Iris alert, a gate check). They exist so the native clients can open their existing detail
    // screens straight from a row in this list, rather than re-fetching the event or the trigger
    // to recover two fields the merge already had in hand.
    Guid? CameraId,
    double? TriggerAreaFraction);
