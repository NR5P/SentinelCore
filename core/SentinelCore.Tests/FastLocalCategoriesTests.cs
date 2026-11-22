using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Per-camera fast-detector categories (TriggerConfig.FastLocalClasses). The gate camera is the
// reason: every trigger there goes through the detector, and a cat or a bird on the driveway used
// to escalate to a two-minute full-model analysis that could only ever say "an animal, don't
// alert". With the gate on person + vehicles those stop at the detector.
public sealed class FastLocalCategoriesTests
{
    private static readonly FastLocalOptions Options = new();
    private static readonly IReadOnlyList<string> GateCategories = ["person", "car", "truck", "motorcycle", "bicycle", "bus"];

    private static FastLocalDetection Detection(int classId, double score = 0.9) => new(classId, score, 0.5, 0.5, 0.1, 0.1);

    // --- The filter -----------------------------------------------------------------------------

    [Fact]
    public void ACatAtTheGate_IsNotASubject_WhenTheGateOnlyWatchesPeopleAndVehicles()
    {
        var analysis = FastLocalVisionClient.MapToAnalysis(
            FastLocalCategories.Filter([Detection(17)], GateCategories), Options);

        // "none" is what FailoverVisionClient reads as "nothing to escalate".
        Assert.Equal("none", analysis.EventType);
        Assert.False(analysis.Animal.Visible);
        Assert.False(analysis.NotificationRecommended);
    }

    [Fact]
    public void AMotorcycleAtTheGate_StillCounts()
    {
        var analysis = FastLocalVisionClient.MapToAnalysis(
            FastLocalCategories.Filter([Detection(4), Detection(17)], GateCategories), Options);

        Assert.True(analysis.Vehicle.Visible);
        Assert.Equal("motorcycle", analysis.Vehicle.Type);
        Assert.False(analysis.Animal.Visible);
        Assert.True(analysis.NotificationRecommended);
    }

    [Fact]
    public void NoList_KeepsEveryDetection()
    {
        IReadOnlyList<FastLocalDetection> all = [Detection(1), Detection(16), Detection(8)];
        Assert.Same(all, FastLocalCategories.Filter(all, null));
    }

    [Fact]
    public void EveryKeyMapsToTheClassTheDetectorAlreadyActsOn()
    {
        // A key whose class id the mapping ignores would be a box someone ticks that does nothing.
        foreach (var (key, classId, _) in FastLocalCategories.All)
        {
            var analysis = FastLocalVisionClient.MapToAnalysis([Detection(classId)], Options);
            Assert.True(analysis.EventType != "none", $"{key} ({classId}) is not mapped");
        }
    }

    [Fact]
    public void Normalize_LowercasesDedupesDropsUnknownsAndUsesPickerOrder()
    {
        Assert.Equal(["person", "truck", "dog"], FastLocalCategories.Normalize([" DOG", "truck", "person", "dog", "raccoon"]));
        Assert.Null(FastLocalCategories.Normalize(null));
    }

    // --- The API's merge and validation ---------------------------------------------------------

    [Fact]
    public void AnOldClientOmittingTheKey_KeepsTheStoredList()
    {
        var stored = TriggerConfig.Disabled(Guid.NewGuid()) with { FastLocalClasses = GateCategories };
        Assert.Equal(GateCategories, TriggerEndpoints.ResolveFastLocalClasses(new TriggerConfigDto(), stored));
    }

    [Fact]
    public void EveryCategoryTicked_IsStoredAsAll()
    {
        var dto = new TriggerConfigDto { FastLocalClasses = [.. FastLocalCategories.Keys] };
        Assert.Null(TriggerEndpoints.ResolveFastLocalClasses(dto, null));
    }

    [Fact]
    public void ASubset_IsStoredNormalized()
    {
        var dto = new TriggerConfigDto { FastLocalClasses = ["Truck", "person"] };
        Assert.Equal(["person", "truck"], TriggerEndpoints.ResolveFastLocalClasses(dto, null));
    }

    [Fact]
    public void AnEmptyList_IsRefused()
    {
        var dto = new TriggerConfigDto { MinTriggerArea = 0.02, MaxTriggersPerMinute = 6, FastLocalClasses = [] };
        Assert.Contains(TriggerEndpoints.Validate(dto, false, 21, 7), e => e.Contains("at least one"));
    }

    [Fact]
    public void AnUnknownCategory_IsRefused()
    {
        var dto = new TriggerConfigDto { MinTriggerArea = 0.02, MaxTriggersPerMinute = 6, FastLocalClasses = ["person", "raccoon"] };
        Assert.Contains(TriggerEndpoints.Validate(dto, false, 21, 7), e => e.Contains("raccoon"));
    }

    // --- Storage and live reload ----------------------------------------------------------------

    [Fact]
    public async Task TheListSurvivesAReopen_AndGoesBackToAll()
    {
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        var store = new SqliteTriggerStore(root);
        var cameraId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(TriggerConfig.Disabled(cameraId) with { FastLocalClasses = GateCategories }, ct);
        Assert.Equal(GateCategories, (await new SqliteTriggerStore(root).GetAsync(cameraId, ct))!.FastLocalClasses);

        await store.SaveAsync(TriggerConfig.Disabled(cameraId) with { FastLocalClasses = null }, ct);
        Assert.Null((await new SqliteTriggerStore(root).GetAsync(cameraId, ct))!.FastLocalClasses);
    }

    [Fact]
    public void ChangingTheList_IsAConfigChange_ButAnEqualCopyIsNot()
    {
        var a = TriggerConfig.Disabled(Guid.NewGuid());
        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(a, a with { FastLocalClasses = GateCategories }));
        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(a with { FastLocalClasses = ["person"] }, a with { FastLocalClasses = GateCategories }));
        Assert.True(TriggerZoneMonitor.ConfigsEquivalent(
            a with { FastLocalClasses = GateCategories }, a with { FastLocalClasses = [.. GateCategories] }));
    }
}
