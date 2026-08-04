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
public sealed class ActivityMergeTests
{
    private static readonly Guid CameraId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static IReadOnlyDictionary<Guid, string> Cameras() =>
        new Dictionary<Guid, string> { [CameraId] = "Driveway" };

    private static AlertEventRecord Alert(Guid id, DateTimeOffset occurredAt, string summary = "a person at the gate") =>
        new(id, "Driveway", occurredAt, "completed", "person", summary, true, "someone is here", 0.9, null);

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
            [Alert(alertId, now)], [Trigger(now, alertId)], Cameras(), false, null, null, 50);

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
            [Alert(alertId, now)], [Trigger(now, alertId)], Cameras(), false, null, null, 50);

        Assert.DoesNotContain(items, i => i.Kind == "motion");
    }

    // A trigger whose alert is older than this page still must not be emitted separately -
    // otherwise scrolling back far enough starts showing duplicates of the same moment.
    [Fact]
    public void BuildActivity_WhenTheMatchingAlertIsNotOnThisPage_StillDoesNotListTheTriggerTwice()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now, Guid.NewGuid())], Cameras(), false, null, null, 50);

        Assert.Empty(items);
    }

    [Fact]
    public void BuildActivity_ATriggerThatWasNeverForwarded_IsListedAsMotionOnly()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now)], Cameras(), false, null, null, 50);

        var item = Assert.Single(items);
        Assert.Equal("motion", item.Kind);
        Assert.Equal("Driveway", item.Camera);
        Assert.Null(item.Summary);
        Assert.Equal("front zone", item.ZoneName);
    }

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
            Cameras(), true, null, null, 50);

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
            Cameras(), false, null, null, 50);

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
            false, now.AddDays(-1), null, 50);

        Assert.Single(items);
    }

    [Fact]
    public void BuildActivity_CapsTheMergedListRatherThanEachSource()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [Alert(Guid.NewGuid(), now), Alert(Guid.NewGuid(), now.AddMinutes(-1))],
            [Trigger(now.AddMinutes(-2)), Trigger(now.AddMinutes(-3))],
            Cameras(), false, null, null, 3);

        Assert.Equal(3, items.Count);
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
                [Alert(alertId, now)], [Trigger(now, alertId, clipPath)], Cameras(), false, null, null, 50);

            var item = Assert.Single(items);
            Assert.NotNull(item.ClipUrl);
            Assert.Contains("/clip", item.ClipUrl);
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
            Cameras(), false, null, null, 50);

        Assert.Null(Assert.Single(items).ClipUrl);
    }

    [Fact]
    public void BuildActivity_SurfacesTheConfirmationVerdictOnMotionRows()
    {
        var now = DateTimeOffset.UtcNow;

        var items = ActivityEndpoints.BuildActivity(
            [], [Trigger(now, confirmation: TriggerConfirmation.Rejected)], Cameras(), false, null, null, 50);

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
}
