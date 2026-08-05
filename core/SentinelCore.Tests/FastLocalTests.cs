using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

public sealed class FastLocalTests
{
    private static readonly FastLocalOptions DefaultOptions = new();

    private static FastLocalDetection Detect(int classId, double score = 0.9) =>
        new(classId, score, 0.5, 0.5, 0.2, 0.4);

    // --- MapToAnalysis: the whole contract of the fast backend --------------------------------

    [Fact]
    public void MapToAnalysis_EmptyDetections_ReportsNothingAndNoNotification()
    {
        var analysis = FastLocalVisionClient.MapToAnalysis([], DefaultOptions);

        Assert.Equal("none", analysis.EventType);
        Assert.False(analysis.Person.Visible);
        Assert.False(analysis.Vehicle.Visible);
        Assert.False(analysis.Animal.Visible);
        Assert.False(analysis.NotificationRecommended);
    }

    [Fact]
    public void MapToAnalysis_Person_SetsFlagsAndRecommendsNotification()
    {
        var analysis = FastLocalVisionClient.MapToAnalysis([Detect(1, 0.91)], DefaultOptions);

        Assert.True(analysis.Person.Visible);
        Assert.Equal("unknown", analysis.Person.Location);
        Assert.False(analysis.Vehicle.Visible);
        Assert.True(analysis.NotificationRecommended);
        Assert.Equal(0.91, analysis.Confidence, 3);
        Assert.Contains("person", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3, "car")]
    [InlineData(4, "motorcycle")]
    [InlineData(6, "other")]   // bus isn't in the schema's type list
    [InlineData(8, "unknown")] // COCO "truck" can't distinguish pickup/box/semi
    public void MapToAnalysis_VehicleClasses_MapToSchemaTypes(int classId, string expectedType)
    {
        var analysis = FastLocalVisionClient.MapToAnalysis([Detect(classId)], DefaultOptions);

        Assert.True(analysis.Vehicle.Visible);
        Assert.Equal(expectedType, analysis.Vehicle.Type);
        Assert.True(analysis.NotificationRecommended);
    }

    [Fact]
    public void MapToAnalysis_AnimalOnly_UsesAnimalEventTypeAndHonorsNotifyOptOut()
    {
        // NotifyOnAnimal defaults to false - an animal alone must not recommend notification.
        var analysis = FastLocalVisionClient.MapToAnalysis([Detect(17, 0.8)], DefaultOptions);

        Assert.Equal("animal", analysis.EventType);
        Assert.True(analysis.Animal.Visible);
        Assert.Contains("cat", analysis.Animal.Description);
        Assert.False(analysis.NotificationRecommended);

        var optedIn = FastLocalVisionClient.MapToAnalysis(
            [Detect(17, 0.8)], new FastLocalOptions { NotifyOnAnimal = true });
        Assert.True(optedIn.NotificationRecommended);
    }

    [Fact]
    public void MapToAnalysis_IgnoresNonSubjectCocoClasses()
    {
        // 63 = couch, 75 = remote: background objects a COCO model names but a security camera
        // doesn't care about. They must not surface as subjects or drive Confidence.
        var analysis = FastLocalVisionClient.MapToAnalysis(
            [Detect(63, 0.95), Detect(75, 0.9), Detect(1, 0.6)], DefaultOptions);

        Assert.True(analysis.Person.Visible);
        Assert.False(analysis.Vehicle.Visible);
        Assert.False(analysis.Animal.Visible);
        Assert.Equal(0.6, analysis.Confidence, 3);
    }

    [Fact]
    public void MapToAnalysis_PersonAndVehicle_SummaryNamesBoth()
    {
        var analysis = FastLocalVisionClient.MapToAnalysis(
            [Detect(1, 0.9), Detect(3, 0.85)], DefaultOptions);

        Assert.True(analysis.Person.Visible);
        Assert.True(analysis.Vehicle.Visible);
        Assert.Equal("car", analysis.Vehicle.Type);
        Assert.Contains("person", analysis.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("car", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // --- FailoverVisionClient: fastlocal routing and escalation -------------------------------

    private static VisionAnalysis SubjectAnalysis(double confidence = 0.9) => new()
    {
        EventType = "unknown",
        Summary = "Fast detection: a person.",
        Person = new PersonAnalysis { Visible = true, Location = "unknown", Activity = "unknown" },
        Confidence = confidence,
        NotificationRecommended = true
    };

    [Fact]
    public async Task Escalates_ToCloud_WhenFastLocalFindsSubject()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini") { Analysis = SubjectAnalysis() };
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gpt-4o-mini", model);
        Assert.Equal(1, fastLocal.CallCount);
        Assert.Equal(0, ollama.CallCount);
    }

    // Escalation writes better prose about a subject the detector already found - it is not a
    // second opinion on whether the subject is there. A VLM shrugging at a dark IR frame must not
    // overwrite a confident detection with an empty one, because "no visible subject" is what
    // files the motion trigger as Rejected and hides the footage.
    [Fact]
    public async Task Escalation_FindingNothing_DoesNotOverwriteTheDetectorsSubject()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini"); // empty analysis: sees nothing
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.True(analysis.Person.Visible);
        Assert.Equal(1, cloud.CallCount);
    }

    [Fact]
    public async Task DoesNotEscalate_WhenNothingDetected()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano"); // empty analysis: no subject
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.Equal(0, cloud.CallCount);
        Assert.Equal(0, ollama.CallCount);
    }

    [Fact]
    public async Task EscalationFailure_KeepsFastLocalResult()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini") { ThrowOnAnalyze = true };
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.True(analysis.Person.Visible);
        Assert.Equal(1, cloud.CallCount);
    }

    [Fact]
    public async Task EscalationTargetCloud_SkipsEscalation_WhenCloudNotConfigured()
    {
        // "cloud" (not "auto") must never silently escalate to Ollama - that surprise
        // multi-minute local analysis is what the fastlocal mode exists to avoid.
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true, EscalationTarget = "cloud" },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.Equal(0, ollama.CallCount);
        Assert.Equal(0, cloud.CallCount);
    }

    [Fact]
    public async Task EscalationTargetAuto_UsesOllama_WhenCloudNotConfigured()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b") { Analysis = SubjectAnalysis() };
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gemma3:4b", model);
    }

    [Fact]
    public async Task FastLocalPrimary_FallsBackToVlm_WhenDetectorFails()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { ThrowOnAnalyze = true };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", FallbackEnabled = true },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gemma3:4b", model);
    }

    [Fact]
    public async Task GateChecks_BypassFastLocal_EvenWhenItIsPrimary()
    {
        // A class-label detector can never answer "is the gate open" - routing a gate check to
        // it would return "unknown" forever and the check would silently stop updating state.
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano");
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal" },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var gateCheckAlert = new IncomingAlert(
            Guid.NewGuid(), "gateCamera", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            GateCheckDecision.ProfileName, "image/jpeg", "check.jpg", []);

        var (_, _, model) = await client.AnalyzeAsync(gateCheckAlert, MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gemma3:4b", model);
        Assert.Equal(0, fastLocal.CallCount);
    }

    // --- FailoverVisionClient: EscalationVerdictWins -------------------------------------------

    // The opt-in inverse of Escalation_FindingNothing_DoesNotOverwriteTheDetectorsSubject above.
    // Asserted through AlertNotificationPolicy rather than Person.Visible alone because that is the
    // rule the Events tab and the trigger confirmation actually apply.
    [Fact]
    public async Task EscalationVerdictWins_EmptyEscalation_ReplacesTheDetectorsSubject()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini"); // empty analysis: sees nothing
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true, EscalationVerdictWins = true },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gpt-4o-mini", model);
        Assert.False(AlertNotificationPolicy.HasVisibleSubject(analysis));
    }

    // The load-bearing one: "the full AI said nothing is there" and "the full AI never answered"
    // are different facts, and only the first is a verdict this flag can act on.
    [Fact]
    public async Task EscalationVerdictWins_EscalationFailure_StillKeepsFastLocalResult()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini") { ThrowOnAnalyze = true };
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true, EscalationVerdictWins = true },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.True(analysis.Person.Visible);
        Assert.Equal(1, cloud.CallCount);
    }

    // The other way escalation can produce no verdict: no target to escalate to at all.
    [Fact]
    public async Task EscalationVerdictWins_NoEscalationTarget_KeepsFastLocalResult()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions
            {
                Primary = "fastlocal",
                EscalationEnabled = true,
                EscalationTarget = "cloud",
                EscalationVerdictWins = true
            },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.True(analysis.Person.Visible);
        Assert.Equal(0, ollama.CallCount);
        Assert.Equal(0, cloud.CallCount);
    }

    // --- FailoverVisionClient: MinimumConfidence ----------------------------------------------

    [Fact]
    public async Task MinimumConfidence_BelowFloor_TreatsVlmAnalysisAsNoSubject()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano");
        var ollama = new FakeVisionClient("gemma3:4b") { Analysis = SubjectAnalysis(0.4) };
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "ollama", MinimumConfidence = 0.6 },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (analysis, _, _) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.False(AlertNotificationPolicy.HasVisibleSubject(analysis));
        Assert.False(analysis.NotificationRecommended);
        Assert.NotEmpty(analysis.NotificationReason);

        // The verdict changes; the evidence does not. The event still has to read correctly in the
        // dashboard, the API and semantic search.
        Assert.Equal("Fast detection: a person.", analysis.Summary);
        Assert.Equal(0.4, analysis.Confidence);
    }

    [Fact]
    public async Task MinimumConfidence_AtFloor_LeavesAnalysisAlone()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano");
        var ollama = new FakeVisionClient("gemma3:4b") { Analysis = SubjectAnalysis(0.6) };
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "ollama", MinimumConfidence = 0.6 },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (analysis, _, _) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.True(analysis.Person.Visible);
    }

    // The off-by-default no-op, and a guard against the floor being written as `<=` - a genuinely
    // 0.0-confidence analysis must still pass when the feature is off.
    [Fact]
    public async Task MinimumConfidence_DefaultZero_ChangesNothing()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano");
        var ollama = new FakeVisionClient("gemma3:4b") { Analysis = SubjectAnalysis(0.0) };
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "ollama" },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (analysis, _, _) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.True(analysis.Person.Visible);
    }

    // The detector scores an empty frame 0.8 by convention and a real detection by its own max
    // score, so its numbers aren't on this floor's scale. FastLocal:ScoreThreshold is that tier's
    // gate, and it runs per-detection before anything is mapped.
    [Fact]
    public async Task MinimumConfidence_DoesNotApplyToFastLocalResults()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis(0.5) };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", MinimumConfidence = 0.9 },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.True(analysis.Person.Visible);
    }

    [Fact]
    public async Task MinimumConfidence_AppliesToFallbackResult()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano");
        var ollama = new FakeVisionClient("gemma3:4b") { Analysis = SubjectAnalysis(0.3) };
        var cloud = new FakeVisionClient("gpt-4o-mini") { ThrowOnAnalyze = true };
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "cloud", FallbackEnabled = true, MinimumConfidence = 0.6 },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gemma3:4b", model);
        Assert.False(AlertNotificationPolicy.HasVisibleSubject(analysis));
    }

    // A gate check's subject is ambient state rather than anything in frame, so there is no
    // presence claim to withdraw and a hesitant reading is still a usable answer.
    [Fact]
    public async Task MinimumConfidence_DoesNotApplyToGateChecks()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano");
        var ollama = new FakeVisionClient("gemma3:4b")
        {
            Analysis = new VisionAnalysis
            {
                Gate = new GateAnalysis { State = "open" },
                Person = new PersonAnalysis { Visible = true },
                Confidence = 0.3
            }
        };
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", MinimumConfidence = 0.6 },
            new CloudVisionOptions { Enabled = false, ApiKey = "" });

        var gateCheckAlert = new IncomingAlert(
            Guid.NewGuid(), "gateCamera", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            GateCheckDecision.ProfileName, "image/jpeg", "check.jpg", []);

        var (analysis, _, _) = await client.AnalyzeAsync(gateCheckAlert, MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("open", analysis.Gate.State);
        Assert.True(analysis.Person.Visible);
    }

    // --- FailoverVisionClient: the two options together ---------------------------------------

    // The floor alone can't delete a detection: it collapses the escalation to "found nothing",
    // which then hits the default detector-wins path.
    [Fact]
    public async Task MinimumConfidence_LowConfidenceEscalation_StillKeepsDetectorResult_ByDefault()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini") { Analysis = SubjectAnalysis(0.2) };
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions { Primary = "fastlocal", EscalationEnabled = true, MinimumConfidence = 0.6 },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("fastlocal/rf-detr-nano", model);
        Assert.True(analysis.Person.Visible);
    }

    [Fact]
    public async Task MinimumConfidence_WithEscalationVerdictWins_RejectsTheEvent()
    {
        var fastLocal = new FakeVisionClient("fastlocal/rf-detr-nano") { Analysis = SubjectAnalysis() };
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini") { Analysis = SubjectAnalysis(0.2) };
        var client = MakeClient(ollama, cloud, fastLocal,
            new VisionRoutingOptions
            {
                Primary = "fastlocal",
                EscalationEnabled = true,
                EscalationVerdictWins = true,
                MinimumConfidence = 0.6
            },
            new CloudVisionOptions { Enabled = true, ApiKey = "key" });

        var (analysis, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gpt-4o-mini", model);
        Assert.False(AlertNotificationPolicy.HasVisibleSubject(analysis));
        Assert.False(analysis.NotificationRecommended);
    }

    // Package.Status is the branch easiest to get wrong: HasVisibleSubject counts a delivery status
    // as a subject in its own right, regardless of Package.Visible.
    [Fact]
    public void WithoutVisibleSubject_ClearsPackageStatus_AndKeepsTheDescription()
    {
        var analysis = new VisionAnalysis
        {
            Summary = "A parcel by the gate.",
            Package = new PackageAnalysis { Visible = false, Status = "delivered", Location = "at_gate" },
            Confidence = 0.3,
            NotificationRecommended = true
        };

        var suppressed = FailoverVisionClient.WithoutVisibleSubject(analysis, "below the floor");

        Assert.False(AlertNotificationPolicy.HasVisibleSubject(suppressed));
        Assert.Equal("none", suppressed.Package.Status);
        Assert.False(suppressed.NotificationRecommended);
        Assert.Equal("below the floor", suppressed.NotificationReason);
        Assert.Equal("A parcel by the gate.", suppressed.Summary);
        Assert.Equal(0.3, suppressed.Confidence);
    }

    // --- helpers ------------------------------------------------------------------------------

    private static FailoverVisionClient MakeClient(
        FakeVisionClient ollama, FakeVisionClient cloud, FakeVisionClient fastLocal,
        VisionRoutingOptions routing, CloudVisionOptions cloudOptions) =>
        new(ollama, cloud, fastLocal,
            new StaticOptionsMonitor<VisionRoutingOptions>(routing),
            new StaticOptionsMonitor<CloudVisionOptions>(cloudOptions),
            NullLogger<FailoverVisionClient>.Instance);

    private static IncomingAlert MakeAlert() => new(
        Guid.NewGuid(), "gateCamera", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "gate", "image/jpeg", "alert.jpg", []);

    private static PromptProfile MakeProfile() => new() { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"] };

    private sealed class FakeVisionClient(string model) : IVisionAnalysisClient
    {
        public int CallCount { get; private set; }
        public bool ThrowOnAnalyze { get; init; }
        public VisionAnalysis Analysis { get; init; } = new();

        public Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
            IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowOnAnalyze)
                throw new InvalidOperationException($"{model} is unavailable.");
            return Task.FromResult((Analysis, "{}", model));
        }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
