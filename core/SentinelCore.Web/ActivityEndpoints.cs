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
//
// WHAT THIS TAB IS FOR: confirmed events, and nothing else. Every row in the default list has
// passed the AI check - either the AI looked at the frame and found a person/vehicle/package/
// animal, or there was never an AI to ask because that camera has analysis switched off (those
// are kept, but flagged, so a camera that has quietly stopped being analyzed is visible rather
// than mysterious). Everything else - rejected, still being judged, or never judged because the
// analysis failed - is filtered out and only reachable through "show hidden", which exists
// precisely so a filter you can't inspect can still be inspected.
public static class ActivityEndpoints
{
    // Beyond the post-roll and the segment the clip builder itself waits for (see
    // TriggerZoneMonitor.BuildClipAsync), the margin that concatenating the segments is allowed to
    // take before a clip that hasn't appeared is reported as missing rather than still recording.
    private const int ClipBuildGraceSeconds = 20;

    // How recent an event has to be before a missing clip is reported as a fault rather than
    // shrugged off. Beyond this the camera's settings may simply have been different at the time,
    // and there's nothing on the row to tell us either way.
    private static readonly TimeSpan ClipFaultWindow = TimeSpan.FromHours(6);

    // Slack on top of `capped` when the confirmed-only filter is on, to absorb the rows that pass
    // each store's own filter and are then demoted by the merge: an alert whose SubjectConfirmed is
    // true but whose trigger came back Pending or Rejected (see VerdictFor). That is the only
    // remaining source of post-query shrinkage, and it's a trickle.
    //
    // It used to be doing much more work than that. Both stores were queried unfiltered and every
    // verdict was applied here, after the fact, so this multiplier was the ONLY thing standing
    // between a noisy night and an empty tab - and being a multiplier on a limit that clamps at
    // 500, it couldn't stretch far enough. A camera watching a spider web produced 566 consecutive
    // rejected frames, which is more than the entire fetch window: the query returned 500 rejected
    // rows, the filter below discarded all 500, and every confirmed event older than that run was
    // simply never fetched. The tab showed nothing and looked date-filtered. Both stores now filter
    // in SQL instead, which is bounded by the number of confirmed rows rather than by how quiet the
    // camera happened to be.
    //
    // Left at 4 even though the remaining job is much smaller than the old one. The demotion it
    // now absorbs is on the trigger side - a row that passes `Confirmation = 'confirmed'` in SQL
    // and is still demoted to Unverified by the merge, which happens when a camera is set to
    // forward to AI and the forward failed. That is the same shape of problem as the one above,
    // just narrower, and shrinking the only cushion against it to save a few hundred rows of a
    // query that already clamps at 500 would be trading a real risk for nothing.
    private const int ConfirmedOnlyOverFetch = 4;

    // What links the two rows is TriggerEvent.AlertId, written when the forward is accepted (see
    // TriggerZoneMonitor.ForwardToAlertPipelineAsync). Rows written before that column existed
    // have no key and stay motion-only - matching them up by camera and timestamp after the fact
    // would be a guess, and a wrong guess would staple someone else's analysis to a clip.
    public static void MapActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/activity", async (
            IEventStore eventStore,
            ITriggerEventStore triggerStore,
            ITriggerConfigStore triggerConfigs,
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
            var fetch = includeUnconfirmed ? capped : Math.Clamp(capped * ConfirmedOnlyOverFetch, capped, 500);
            var filter = new EventFilter(
                camera, eventType, notifiedOnly, search, from, to, fetch,
                ConfirmedOnly: !includeUnconfirmed);

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
                : await triggerStore.GetRecentAsync(fetch, cameraId, includeUnconfirmed, ct);

            // The rows above are the motion-only page and are filtered to confirmed unless "show
            // hidden" is on. That is right for listing, and wrong for the join: an alert whose
            // trigger the AI rejected would not find it here, and would then be judged by its own
            // SubjectConfirmed alone - which says "confirmed" for a chicken, a branch, anything
            // that is a subject without being worth showing. Fetched by alert id so the verdict is
            // always available, without diluting the page budget above with rows about to be
            // filtered out.
            var joinTriggers = cameraFilterMatchedNothing
                ? []
                : await triggerStore.GetByAlertIdsAsync([.. alerts.Select(a => a.Id)], ct);

            // Whether a camera forwards to AI analysis at all, and what clip window it records,
            // both live on the trigger config rather than on any event row - GetAllAsync rather
            // than GetEnabledAsync so a camera whose zone was since switched off still explains
            // its own historical rows instead of reading as "no config, unknown".
            var configs = await triggerConfigs.GetAllAsync(ct);
            var cameraInfo = configs.ToDictionary(
                c => c.CameraId,
                c => new CameraActivityInfo(c.SendToAlerts, c.RecordClips, c.ClipPreSeconds, c.ClipPostSeconds));

            // eventType/notifiedOnly/search only exist on an analysis, so a motion-only row can
            // never satisfy them - including them would mean a filter for "person" silently
            // returning frames nothing ever looked at. With none of those set, motion-only rows
            // belong in the list: they're the whole content of the old Triggers tab.
            var analysisOnlyFilter = !string.IsNullOrWhiteSpace(eventType)
                || notifiedOnly == true
                || !string.IsNullOrWhiteSpace(search);

            return Results.Ok(BuildActivity(
                alerts, triggers, cameraNames, analysisOnlyFilter, from, to, capped,
                cameraInfo, includeUnconfirmed, DateTimeOffset.UtcNow, joinTriggers));
        }).RequireAuthorization();
    }

    // Pure, and public for the tests: the join rules here are the whole point of this endpoint,
    // and they're the part that would silently double-list events or drop clips if they broke.
    //
    // cameraInfo may be null or missing an entry, which reads as "this camera has no trigger
    // config we can consult". That deliberately falls on the side of showing the row (flagged as
    // un-analyzed) rather than hiding it: a row we can't explain is a row worth seeing.
    public static IReadOnlyList<ActivityItem> BuildActivity(
        IReadOnlyList<AlertEventRecord> alerts,
        IReadOnlyList<TriggerEvent> triggers,
        IReadOnlyDictionary<Guid, string> cameraNames,
        bool analysisOnlyFilter,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int capped,
        IReadOnlyDictionary<Guid, CameraActivityInfo>? cameraInfo = null,
        bool includeUnconfirmed = false,
        DateTimeOffset? asOf = null,
        // The trigger rows to JOIN the alerts against, which is not the same set as the motion-only
        // page in `triggers` - see the call site. Null means "use the page", which is the old
        // behaviour and what the tests that don't care about verdicts still pass.
        IReadOnlyList<TriggerEvent>? joinTriggers = null)
    {
        var now = asOf ?? DateTimeOffset.UtcNow;

        // First wins: two triggers claiming one alert can't happen through the write path, and if
        // a hand-edited row ever made it so, listing the moment twice would be worse than picking
        // one of them.
        var triggersByAlertId = (joinTriggers ?? triggers)
            .Where(t => t.AlertId is not null)
            .GroupBy(t => t.AlertId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<ActivityItem>(alerts.Count + triggers.Count);
        foreach (var alert in alerts)
        {
            var trigger = triggersByAlertId.GetValueOrDefault(alert.Id);
            items.Add(Merge(alert, trigger, InfoFor(cameraInfo, trigger?.CameraId), now));
        }

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
                items.Add(MotionOnly(
                    trigger, cameraNames.GetValueOrDefault(trigger.CameraId, "deleted camera"),
                    InfoFor(cameraInfo, trigger.CameraId), now));
            }
        }

        // The tab's whole promise: what's listed here got past the AI. "show hidden" is the escape
        // hatch and passes includeUnconfirmed, which keeps every verdict so the badges on the cards
        // can say which one each row got and why it was being held back.
        IEnumerable<ActivityItem> visible = items;
        if (!includeUnconfirmed)
            visible = visible.Where(i => i.Confirmation == ConfirmationStates.Confirmed);

        // Each store was asked for its own page of rows, so trimming the merged list back to
        // `capped` can leave an item just off the end of one store's page missing from this page.
        // It reappears as the newer rows age out; paging both stores in lockstep would need a
        // shared cursor across two databases, which this list doesn't warrant.
        return visible
            .OrderByDescending(i => i.OccurredAt)
            .Take(capped)
            .ToList();
    }

    private static CameraActivityInfo? InfoFor(IReadOnlyDictionary<Guid, CameraActivityInfo>? cameraInfo, Guid? cameraId) =>
        cameraInfo is not null && cameraId is { } id ? cameraInfo.GetValueOrDefault(id) : null;

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

    // Two independent records of the same verdict have to agree before a card counts as confirmed.
    // The trigger's own Confirmation is fail-open by design (see TriggerConfirmation) - it says
    // "confirmed" for a poll that timed out or a main app that was unreachable, because losing
    // footage to an outage is worse than one extra card. That's the right default for the Triggers
    // tab; it isn't good enough for a tab whose promise is "the AI agreed something was here". So
    // the alert's own SubjectConfirmed gets the final say whenever it exists, and its absence -
    // analysis failed, never ran - reads as unverified rather than as a pass.
    private static string VerdictFor(AlertEventRecord alert, TriggerEvent? trigger)
    {
        if (trigger?.Confirmation == TriggerConfirmation.Pending) return ConfirmationStates.Pending;
        if (trigger?.Confirmation == TriggerConfirmation.Rejected) return ConfirmationStates.Rejected;

        return alert.SubjectConfirmed switch
        {
            true => ConfirmationStates.Confirmed,
            false => ConfirmationStates.Rejected,
            _ => string.Equals(alert.Status, "queued", StringComparison.OrdinalIgnoreCase)
                ? ConfirmationStates.Pending
                : ConfirmationStates.Unverified
        };
    }

    // A motion row is one nothing ever analyzed. That's expected and fine when the camera has AI
    // analysis switched off - it's the documented behaviour of that setting, and the card carries a
    // flag saying so. It is NOT fine when the camera is supposed to forward and this row has no
    // alert to show for it: that means the forward was refused or blew up, so the frame was never
    // judged and doesn't belong in a confirmed-only list.
    //
    // A null ZoneName is the third case and has to be carved out, because it means no zone detected
    // this frame at all: it's a Camera Group companion capture, or a sensor/API trigger (see
    // TriggerEvent.ZoneName). A companion capture in particular is footage of an incident that was
    // itself confirmed - the group only recorded it because a sibling camera fired - so judging it
    // by whether IT has an alert would delete the other half of every multi-camera incident from
    // this tab. Those rows come in on the strength of the event that pulled them in.
    private static string VerdictFor(TriggerEvent trigger, bool aiAnalysisOff) => trigger.Confirmation switch
    {
        TriggerConfirmation.Pending => ConfirmationStates.Pending,
        TriggerConfirmation.Rejected => ConfirmationStates.Rejected,
        _ => aiAnalysisOff || trigger.ZoneName is null
            ? ConfirmationStates.Confirmed
            : ConfirmationStates.Unverified
    };

    // "ready" once the file is on disk; "recording" while the post-roll and concatenation are still
    // in their expected window; "missing" once that window has passed with nothing to show, which
    // is a real fault worth seeing rather than a spinner that never resolves; "off" when this
    // camera simply isn't set to record clips; "none" for a row with no trigger behind it at all
    // (a Blue Iris alert, a gate check), where there was never a camera buffer to cut from.
    private static string ClipStateFor(TriggerEvent? trigger, CameraActivityInfo? info, bool hasClip, DateTimeOffset now)
    {
        if (hasClip) return ClipStates.Ready;
        if (trigger is null) return ClipStates.None;

        // No config to consult means no basis for claiming a clip was ever expected - saying
        // "missing" there would invent a fault out of missing information.
        if (info is null || !info.RecordClips) return info is null ? ClipStates.None : ClipStates.Off;

        // A rejected trigger with no clip is the system having done its job, not a fault. Since
        // 7f29718 the clip is deleted the moment the AI says there was nothing worth keeping, so
        // badging it "missing" tells the user to go and investigate a file we removed on purpose -
        // and the web tooltip for that state says, in as many words, to check the logs.
        //
        // Rare until KeepOnlyAlertable, which makes it every row on a camera that has it on: the
        // chicken coop showed an amber fault triangle for six hours after every single trigger.
        //
        // "none" rather than a new state so the shipped mobile builds render it as the nothing it
        // is instead of falling down an unknown-value branch. A rejected trigger whose clip DID
        // survive - a shared file, a protected one, discarding switched off - never reaches here:
        // hasClip is checked first and returns "ready".
        if (trigger.Confirmation == TriggerConfirmation.Rejected) return ClipStates.None;

        var readyBy = trigger.OccurredAt
            .AddSeconds(info.ClipPostSeconds + ClipRecorder.SegmentSeconds + ClipBuildGraceSeconds);
        if (now < readyBy) return ClipStates.Recording;

        // info is the camera's CURRENT config, and an old row predating the day clip recording was
        // switched on would otherwise be badged as a fault forever. Only claim a clip is missing
        // while the event is recent enough that recording must already have been on for it.
        return now - trigger.OccurredAt < ClipFaultWindow ? ClipStates.Missing : ClipStates.None;
    }

    private static ActivityItem Merge(AlertEventRecord alert, TriggerEvent? trigger, CameraActivityInfo? info, DateTimeOffset now)
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
            Confirmation: VerdictFor(alert, trigger),
            CameraId: trigger?.CameraId,
            TriggerAreaFraction: trigger?.TriggerAreaFraction,
            AiAnalysisOff: false,
            ClipState: ClipStateFor(trigger, info, hasClip, now),
            ClipPreSeconds: info?.ClipPreSeconds,
            ClipPostSeconds: info?.ClipPostSeconds);
    }

    private static ActivityItem MotionOnly(TriggerEvent trigger, string cameraName, CameraActivityInfo? info, DateTimeOffset now)
    {
        // No config to consult reads the same as "not forwarding": either way nothing analyzed this
        // frame, and the card should say so rather than imply a verdict that was never given.
        var aiAnalysisOff = info is null || !info.SendToAlerts;
        var hasClip = !string.IsNullOrWhiteSpace(trigger.ClipPath) && File.Exists(trigger.ClipPath);

        return new ActivityItem(
            // Prefixed so a trigger id and an alert id can never collide in the same list, and so the
            // client can tell which endpoint a row's media hangs off without a second field.
            Id: $"trigger:{trigger.Id:D}",
            Kind: "motion",
            Camera: cameraName,
            OccurredAt: trigger.OccurredAt,
            ImageUrl: File.Exists(trigger.ImagePath) ? $"/api/triggers/{trigger.Id:D}/image" : null,
            ClipUrl: hasClip ? $"/api/triggers/{trigger.Id:D}/clip" : null,
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
            Confirmation: VerdictFor(trigger, aiAnalysisOff),
            CameraId: trigger.CameraId,
            TriggerAreaFraction: trigger.TriggerAreaFraction,
            AiAnalysisOff: aiAnalysisOff,
            ClipState: ClipStateFor(trigger, info, hasClip, now),
            ClipPreSeconds: info?.ClipPreSeconds,
            ClipPostSeconds: info?.ClipPostSeconds);
    }
}

// The strings the client switches on. Constants rather than an enum because they go over the wire
// as JSON and the dashboard compares them literally - a rename here has to be a deliberate act
// that touches app.js too, not something a refactor tool can do silently.
public static class ConfirmationStates
{
    public const string Confirmed = "confirmed";
    public const string Pending = "pending";
    public const string Rejected = "rejected";

    // No verdict was ever reached: the analysis failed, the forward was refused, or the camera was
    // meant to forward and no alert exists for the frame. Distinct from Rejected on purpose - one
    // is the AI saying no, the other is nobody having asked.
    public const string Unverified = "unverified";
}

public static class ClipStates
{
    public const string Ready = "ready";
    public const string Recording = "recording";
    public const string Missing = "missing";
    public const string Off = "off";
    public const string None = "none";
}

// Per-camera facts the Events tab needs that live on the trigger config rather than on any event
// row: whether this camera's frames are analyzed at all, and the clip window it records around a
// trigger (the "start the video this many seconds before/after" pair in the zone editor).
public sealed record CameraActivityInfo(bool SendToAlerts, bool RecordClips, int ClipPreSeconds, int ClipPostSeconds);

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
    double? TriggerAreaFraction,
    // This camera has AI analysis switched off, so nothing judged this frame and nothing ever
    // will. The row is kept - that setting is a legitimate choice - but flagged, because a camera
    // silently dropping out of the AI filter is exactly the kind of thing you only notice months
    // later. Appended with defaults so the native clients decoding this shape keep working.
    bool AiAnalysisOff = false,
    string? ClipState = null,
    int? ClipPreSeconds = null,
    int? ClipPostSeconds = null);
