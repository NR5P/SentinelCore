extern alias WebHost;

using SentinelCore;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The Events tab shows one card per real-world moment, which means joining two rows written by
// two different projects into two different SQLite files: the TriggerEvent that motion saved
// (and its clip) and the AlertEvent the AI produced from that same frame.
//
// The failure modes here are quiet ones. Get the join wrong in one direction and every forwarded
// trigger is listed twice - once as motion, once as analysis - which reads as the cameras firing
// twice as often as they do. Get it wrong in the other and clips stop appearing on the cards
// that have them, which is indistinguishable from clip recording being broken. Neither throws,
// and neither shows up in a build.
//
// The confirmed-only filter has the same shape of risk in a sharper form: too strict and real
// events silently vanish from the only list anyone looks at, too loose and the tab goes back to
// being a list of moths. Both directions are asserted below.
public sealed class ActivityMergeTests
{
    private static readonly Guid CameraId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static IReadOnlyDictionary<Guid, string> Cameras() =>
        new Dictionary<Guid, string> { [CameraId] = "Driveway" };

    // A camera that forwards to AI analysis and records clips - the ordinary configuration, and
    // the one the confirmed-only rules are written against.
    private static IReadOnlyDictionary<Guid, CameraActivityInfo> AnalyzedCamera(bool recordClips = true) =>
        new Dictionary<Guid, CameraActivityInfo> { [CameraId] = new(true, recordClips, 5, 10) };

    private static IReadOnlyDictionary<Guid, CameraActivityInfo> UnanalyzedCamera() =>
        new Dictionary<Guid, CameraActivityInfo> { [CameraId] = new(false, true, 5, 10) };

    private static AlertEventRecord Alert(
        Guid id, DateTimeOffset occurredAt, string summary = "a person at the gate",
        bool? subjectConfirmed = true, string status = "completed") =>
        new(id, "Driveway", occurredAt, status, "person", summary, true, "someone is here", 0.9, null,
            SubjectConfirmed: subjectConfirmed);

    private static TriggerEvent Trigger(
        DateTimeOffset occurredAt, Guid? alertId = null, string? clipPath = null,
        TriggerConfirmation confirmation = TriggerConfirmation.Confirmed) =>
        new(Guid.NewGuid(), CameraId, occurredAt, "/tmp/does-not-exist.jpg", 0.05, clipPath,
            null, confirmation, "front zone", alertId);

    [Fact]
    public void BuildActivity_AForwardedTrigger_IsListedOnceAsItsAnalysis()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now)], [Trigger(now, alertId)], Cameras(), false, null, null, 50, AnalyzedCamera());

        var item = Assert.Single(items);
        Assert.Equal("analyzed", item.Kind);
        Assert.Equal("a person at the gate", item.Summary);
        Assert.Equal("front zone", item.ZoneName);
    }

    // The whole reason the AlertId column exists. Before it, these were two rows nothing could
    // match up, and the dashboard showed both.
    [Fact]
    public void BuildActivity_DoesNotAlsoListAForwardedTriggerAsMotion()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now)], [Trigger(now, alertId)], Cameras(), false, null, null, 50, AnalyzedCamera());

        Assert.DoesNotContain(items, i => i.Kind == "motion");
    }

    // A trigger whose alert is older than this page still must not be emitted separately -
    // otherwise scrolling back far enough starts showing duplicates of the same moment.
    [Fact]
    public void BuildActivity_WhenTheMatchingAlertIsNotOnThisPage_StillDoesNotListTheTriggerTwice()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now, Guid.NewGuid())], Cameras(), false, null, null, 50, AnalyzedCamera());

        Assert.Empty(items);
    }

    // ---------------- The confirmed-only rule ----------------

    // The tab's promise. An alert the AI looked at and found nothing in is exactly what the user
    // does not want to scroll past, and it is what the previous version of this list showed.
    [Fact]
    public void BuildActivity_AnAlertTheAiFoundNothingIn_IsNotListed()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: false)], [Trigger(now, alertId)],
            Cameras(), false, null, null, 50, AnalyzedCamera());

        Assert.Empty(items);
    }

    // The bug this join fix exists for. The Events tab pages triggers with the confirmed-only
    // filter on, so a REJECTED trigger is absent from that page - and an alert that cannot find
    // its trigger falls back to its own SubjectConfirmed, which says true for anything that is a
    // subject. At the chicken coop, where the whole point is that chickens are subjects and not
    // news, that put a picture of chickens on the tab for every trigger whose video had just been
    // deleted for being unremarkable.
    [Fact]
    public void BuildActivity_ARejectedTriggerMissingFromThePage_StillSuppressesItsAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();
        var rejected = Trigger(now, alertId, confirmation: TriggerConfirmation.Rejected);

        // What the endpoint really passes: the motion-only page has been filtered to confirmed, so
        // the rejected trigger is not in it - but it IS in the join set, fetched by alert id.
        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: true)], [],
            Cameras(), false, null, null, 50, AnalyzedCamera(), joinTriggers: [rejected]);

        Assert.Empty(items);
    }

    [Fact]
    public void BuildActivity_ThatSameRejectedAlert_IsStillListedUnderShowHidden()
    {
        // Held back, not erased - the snapshot is the only way to tell a well-aimed zone from one
        // pointed at the road.
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();
        var rejected = Trigger(now, alertId, confirmation: TriggerConfirmation.Rejected);

        var shown = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: true)], [rejected],
            Cameras(), false, null, null, 50, AnalyzedCamera(), includeUnconfirmed: true,
            joinTriggers: [rejected]);

        Assert.Equal("rejected", Assert.Single(shown).Confirmation);
    }

    [Fact]
    public void BuildActivity_WithNoJoinSetGiven_FallsBackToThePage()
    {
        // The default keeps every existing caller and test meaning what it did before.
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: true)], [Trigger(now, alertId)],
            Cameras(), false, null, null, 50, AnalyzedCamera());

        Assert.Equal("confirmed", Assert.Single(items).Confirmation);
    }

    // Rejected is the AI saying no. Unverified is nobody having asked. Both are held back from the
    // default list, but they have to stay distinguishable or "show hidden" can't explain itself.
    [Fact]
    public void BuildActivity_AnAlertWhoseAnalysisNeverRan_IsHeldBackAsUnverifiedRatherThanRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var hidden = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: null, status: "failed")], [Trigger(now, alertId)],
            Cameras(), false, null, null, 50, AnalyzedCamera());
        Assert.Empty(hidden);

        var shown = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: null, status: "failed")], [Trigger(now, alertId)],
            Cameras(), false, null, null, 50, AnalyzedCamera(), includeUnconfirmed: true);
        Assert.Equal("unverified", Assert.Single(shown).Confirmation);
    }

    // The trigger's own verdict is fail-open by design - it says "confirmed" when the poll timed
    // out or the main app was unreachable. That's right for the Triggers tab and not good enough
    // here, so the alert's own verdict overrules it.
    [Fact]
    public void BuildActivity_WhenTheTriggerFailedOpenButTheAiFoundNothing_TheAiWins()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: false)],
            [Trigger(now, alertId, confirmation: TriggerConfirmation.Confirmed)],
            Cameras(), false, null, null, 50, AnalyzedCamera(), includeUnconfirmed: true);

        Assert.Equal("rejected", Assert.Single(items).Confirmation);
    }

    // A camera with AI analysis switched off never gets a verdict, so gating it on one would hide
    // that camera entirely and forever. Those rows stay, flagged.
    [Fact]
    public void BuildActivity_MotionFromACameraWithAiOff_IsListedAndFlagged()
    {
        var now = DateTimeOffset.UtcNow;

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [], [Trigger(now)], Cameras(), false, null, null, 50, UnanalyzedCamera()));

        Assert.Equal("motion", item.Kind);
        Assert.True(item.AiAnalysisOff);
        Assert.Equal("confirmed", item.Confirmation);
    }

    // ...but a camera that IS meant to forward and produced a motion row anyway means the forward
    // never happened, so nothing judged that frame. It doesn't belong in a confirmed-only list.
    [Fact]
    public void BuildActivity_MotionFromACameraThatShouldHaveForwarded_IsHeldBack()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now)], Cameras(), false, null, null, 50, AnalyzedCamera());

        Assert.Empty(items);
    }

    // A Camera Group companion capture has no zone (nothing detected it - a sibling camera fired)
    // and normally no alert of its own, on a camera that does forward. Judging it by the same rule
    // as a self-detected trigger would delete the other half of every multi-camera incident from
    // this tab, which is the opposite of why groups exist.
    [Fact]
    public void BuildActivity_ACompanionCapture_IsNotMistakenForAnUnjudgedTrigger()
    {
        var now = DateTimeOffset.UtcNow;
        var companion = new TriggerEvent(
            Guid.NewGuid(), CameraId, now, "/tmp/does-not-exist.jpg", 0, ClipPath: null,
            IncidentId: "incident-1", Confirmation: TriggerConfirmation.Confirmed,
            ZoneName: null, AlertId: null);

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [], [companion], Cameras(), false, null, null, 50, AnalyzedCamera()));

        Assert.Equal("confirmed", item.Confirmation);
        Assert.False(item.AiAnalysisOff);
    }

    // No trigger config to consult is not evidence that the frame was analyzed. Showing it flagged
    // beats hiding a row we can't explain.
    [Fact]
    public void BuildActivity_MotionFromACameraWithNoConfig_IsListedAsUnanalyzed()
    {
        var now = DateTimeOffset.UtcNow;

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [], [Trigger(now)], Cameras(), false, null, null, 50));

        Assert.True(item.AiAnalysisOff);
    }

    [Fact]
    public void BuildActivity_WithShowHidden_KeepsEveryVerdictAndBadgesIt()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [],
            [Trigger(now, confirmation: TriggerConfirmation.Rejected),
             Trigger(now.AddSeconds(-1), confirmation: TriggerConfirmation.Pending)],
            Cameras(), false, null, null, 50, AnalyzedCamera(), includeUnconfirmed: true);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Confirmation == "rejected");
        Assert.Contains(items, i => i.Confirmation == "pending");
    }

    // ---------------- Clip state ----------------

    // A clip isn't written until the post-roll has elapsed, so for the first several seconds of an
    // event's life there is genuinely no video. Saying "recording" is the difference between a card
    // that explains itself and a card that looks broken.
    [Fact]
    public void BuildActivity_WithinThePostRollWindow_ReportsTheClipAsStillRecording()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [Alert(alertId, now)], [Trigger(now, alertId)], Cameras(), false, null, null, 50,
            AnalyzedCamera(), asOf: now.AddSeconds(3)));

        Assert.Equal("recording", item.ClipState);
        Assert.Equal(5, item.ClipPreSeconds);
        Assert.Equal(10, item.ClipPostSeconds);
    }

    // Once that window has passed with nothing on disk, the clip is not coming. That's a fault
    // worth surfacing rather than a spinner that never resolves.
    [Fact]
    public void BuildActivity_LongAfterThePostRollWithNoClip_ReportsItMissing()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [Alert(alertId, now)], [Trigger(now, alertId)], Cameras(), false, null, null, 50,
            AnalyzedCamera(), asOf: now.AddMinutes(10)));

        Assert.Equal("missing", item.ClipState);
    }

    // ...unless the AI rejected the frame, in which case we deleted the clip ourselves the moment
    // the verdict landed. Badging that a fault sends the user to look for a file the system removed
    // on purpose - and the tooltip for "missing" tells them to go and read the logs. Rare before
    // KeepOnlyAlertable; every single row on a camera that has it on.
    [Fact]
    public void BuildActivity_ARejectedTriggerWhoseClipWasDiscarded_IsNotAFault()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();
        var rejected = Trigger(now, alertId, confirmation: TriggerConfirmation.Rejected);

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [Alert(alertId, now, subjectConfirmed: true)], [rejected], Cameras(), false, null, null, 50,
            AnalyzedCamera(), includeUnconfirmed: true, asOf: now.AddMinutes(10), joinTriggers: [rejected]));

        Assert.Equal("rejected", item.Confirmation);
        Assert.Equal("none", item.ClipState);
    }

    [Fact]
    public void BuildActivity_WhenTheCameraDoesNotRecordClips_SaysSoRatherThanReportingAFault()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [Alert(alertId, now)], [Trigger(now, alertId)], Cameras(), false, null, null, 50,
            AnalyzedCamera(recordClips: false), asOf: now.AddMinutes(10)));

        Assert.Equal("off", item.ClipState);
    }

    // An alert with no trigger behind it - Blue Iris, a gate check - never had a camera buffer to
    // cut from, so there is no clip to be missing.
    [Fact]
    public void BuildActivity_AnAlertWithNoTriggerBehindIt_ReportsNoClipRatherThanAMissingOne()
    {
        var now = DateTimeOffset.UtcNow;

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [Alert(Guid.NewGuid(), now)], [], Cameras(), false, null, null, 50,
            AnalyzedCamera(), asOf: now.AddMinutes(10)));

        Assert.Equal("none", item.ClipState);
    }

    // ---------------- Filters, ordering and paging ----------------

    // A filter on the AI's own output can't be satisfied by a frame nothing analyzed, so those
    // rows drop out entirely rather than appearing to match.
    [Fact]
    public void BuildActivity_WithAnAnalysisOnlyFilter_ExcludesMotionOnlyRows()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now)],
            [Trigger(now, alertId), Trigger(now.AddSeconds(-5))],
            Cameras(), true, null, null, 50, UnanalyzedCamera());

        Assert.Single(items);
        Assert.DoesNotContain(items, i => i.Kind == "motion");
    }

    [Fact]
    public void BuildActivity_OrdersNewestFirstAcrossBothSources()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now.AddMinutes(-1))],
            [Trigger(now, alertId: null), Trigger(now.AddMinutes(-2))],
            Cameras(), false, null, null, 50, UnanalyzedCamera());

        Assert.Equal(3, items.Count);
        Assert.Equal(now, items[0].OccurredAt);
        Assert.Equal(now.AddMinutes(-1), items[1].OccurredAt);
        Assert.Equal(now.AddMinutes(-2), items[2].OccurredAt);
    }

    [Fact]
    public void BuildActivity_AppliesTheDateRangeToMotionRows()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now), Trigger(now.AddDays(-3))], Cameras(),
            false, now.AddDays(-1), null, 50, UnanalyzedCamera());

        Assert.Single(items);
    }

    [Fact]
    public void BuildActivity_CapsTheMergedListRatherThanEachSource()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [Alert(Guid.NewGuid(), now), Alert(Guid.NewGuid(), now.AddMinutes(-1))],
            [Trigger(now.AddMinutes(-2)), Trigger(now.AddMinutes(-3))],
            Cameras(), false, null, null, 3, UnanalyzedCamera());

        Assert.Equal(3, items.Count);
    }

    // The cap is applied AFTER the confirmed-only filter, so a page whose rows were mostly
    // rejected still comes back full rather than mostly empty.
    [Fact]
    public void BuildActivity_FiltersBeforeCapping()
    {
        var now = DateTimeOffset.UtcNow;
        var keep = Guid.NewGuid();

        var alerts = new List<AlertEventRecord>
        {
            Alert(Guid.NewGuid(), now, subjectConfirmed: false),
            Alert(Guid.NewGuid(), now.AddSeconds(-1), subjectConfirmed: false),
            Alert(keep, now.AddSeconds(-2))
        };

        var items = ActivityEndpoints.BuildActivity(
            alerts, [], Cameras(), false, null, null, 2, AnalyzedCamera());

        Assert.Equal(keep.ToString("D"), Assert.Single(items).Id);
    }

    // The clip lives on the trigger row, so an analyzed card can only offer playback through the
    // join. This is the assertion that fails if the join quietly stops matching.
    [Fact]
    public void BuildActivity_AnAnalyzedItem_CarriesTheClipFromItsTrigger()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();
        var clipPath = Path.Combine(Path.GetTempPath(), $"activity-merge-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(clipPath, [0x00]);

        try
        {
            var items = ActivityEndpoints.BuildActivity(
                [Alert(alertId, now)], [Trigger(now, alertId, clipPath)], Cameras(), false, null, null, 50,
                AnalyzedCamera());

            var item = Assert.Single(items);
            Assert.NotNull(item.ClipUrl);
            Assert.Contains("/clip", item.ClipUrl);
            Assert.Equal("ready", item.ClipState);
        }
        finally
        {
            File.Delete(clipPath);
        }
    }

    // A path in the database whose file is gone must not produce a play button that 404s.
    [Fact]
    public void BuildActivity_WhenTheClipFileIsMissing_OffersNoClipUrl()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now)],
            [Trigger(now, alertId, "/tmp/never-written.mp4")],
            Cameras(), false, null, null, 50, AnalyzedCamera());

        Assert.Null(Assert.Single(items).ClipUrl);
    }

    [Fact]
    public void BuildActivity_SurfacesTheConfirmationVerdictOnMotionRows()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now, confirmation: TriggerConfirmation.Rejected)], Cameras(), false, null, null, 50,
            UnanalyzedCamera(), includeUnconfirmed: true);

        Assert.Equal("rejected", Assert.Single(items).Confirmation);
    }

    [Fact]
    public void BuildActivity_ATriggerForADeletedCamera_StillLists()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now)], new Dictionary<Guid, string>(), false, null, null, 50);

        Assert.Equal("deleted camera", Assert.Single(items).Camera);
    }

    // ---------------------------------------------------------------- Recordings

    private static Recording ManualRecording(DateTimeOffset startedAt, int seconds, string status = "complete", string? filePath = null) =>
        new(Guid.NewGuid(), CameraId, null, "Side of house", startedAt, startedAt.AddMinutes(10),
            startedAt, startedAt.AddSeconds(seconds), filePath, 1234, status, "", IsManual: true);

    private static Recording TimedRecording(DateTimeOffset startedAt, int seconds, string status = "complete") =>
        new(Guid.NewGuid(), CameraId, Guid.NewGuid(), "Evening driveway", startedAt, startedAt.AddSeconds(seconds),
            startedAt, startedAt.AddSeconds(seconds), null, 1234, status, "");

    [Fact]
    public void BuildActivity_ARecordingStartedByHand_IsListedAsAManualRow()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [], Cameras(), false, null, null, 50, AnalyzedCamera(),
            recordings: [ManualRecording(now.AddMinutes(-5), 16)]);

        var item = Assert.Single(items);
        Assert.Equal("recording", item.Kind);
        Assert.Equal("manual", item.Origin);
        Assert.Equal("confirmed", item.Confirmation);
        Assert.Equal("Driveway", item.Camera);
        Assert.Equal(16, item.DurationSeconds);
        Assert.StartsWith("recording:", item.Id);
        Assert.Contains("record button", item.Summary);
        Assert.Contains("16s", item.Summary);
        // The file for this test does not exist, so no clip URL - and the badge says so rather
        // than leaving a card that silently refuses to play.
        Assert.Null(item.ClipUrl);
        Assert.Equal("missing", item.ClipState);
    }

    [Fact]
    public void BuildActivity_ATimedRecording_IsListedAsAScheduledRow()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [], Cameras(), false, null, null, 50, AnalyzedCamera(),
            recordings: [TimedRecording(now.AddHours(-1), 1800)]);

        var item = Assert.Single(items);
        Assert.Equal("scheduled", item.Origin);
        Assert.Contains("Evening driveway", item.Summary);
        Assert.Contains("30m 00s", item.Summary);
        Assert.Equal(1800, item.DurationSeconds);
    }

    [Fact]
    public void BuildActivity_ARecordingStillBeingWritten_HasNoClipYet()
    {
        var now = DateTimeOffset.UtcNow;
        var started = now.AddSeconds(-40);
        var recording = new Recording(Guid.NewGuid(), CameraId, null, "Side of house", started, started.AddMinutes(10),
            started, null, null, 0, Recording.StatusRecording, "", IsManual: true);

        var item = Assert.Single(ActivityEndpoints.BuildActivity(
            [], [], Cameras(), false, null, null, 50, AnalyzedCamera(), asOf: now, recordings: [recording]));

        Assert.Equal("recording", item.ClipState);
        Assert.Null(item.ClipUrl);
        Assert.Null(item.DurationSeconds);
        Assert.Contains("recording now", item.Summary);
    }

    [Fact]
    public void BuildActivity_AnalysisOnlyFilters_LeaveRecordingsOut()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [], Cameras(), analysisOnlyFilter: true, null, null, 50, AnalyzedCamera(),
            recordings: [ManualRecording(now, 10)]);

        Assert.Empty(items);
    }

    [Fact]
    public void BuildActivity_RecordingsRespectTheDateWindow_AndSortWithEverythingElse()
    {
        var now = DateTimeOffset.UtcNow;
        var alertId = Guid.NewGuid();

        var items = ActivityEndpoints.BuildActivity(
            [Alert(alertId, now.AddMinutes(-2))], [Trigger(now.AddMinutes(-2), alertId)], Cameras(), false,
            from: now.AddMinutes(-3), to: now, 50, AnalyzedCamera(),
            recordings: [ManualRecording(now.AddMinutes(-1), 9), ManualRecording(now.AddHours(-2), 9)]);

        Assert.Equal(2, items.Count);
        Assert.Equal("recording", items[0].Kind);
        Assert.Equal("analyzed", items[1].Kind);
        // Every row says where it came from, the old two included.
        Assert.Equal("alert", items[1].Origin);
    }

    // The two rows that existed before recordings joined the list keep reading as what they were.
    [Fact]
    public void BuildActivity_MotionAndAnalysisRows_CarryTheAlertOrigin()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now)], Cameras(), false, null, null, 50, UnanalyzedCamera());

        Assert.Equal("alert", Assert.Single(items).Origin);
    }
}
