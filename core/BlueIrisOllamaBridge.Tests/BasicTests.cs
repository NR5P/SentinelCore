using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlueIrisOllamaBridge.Licensing;
using BlueIrisOllamaBridge.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BlueIrisOllamaBridge.Tests;

public sealed class BasicTests
{
    [Fact]
    public void VisionAnalysis_Defaults_AreSafe()
    {
        var analysis = new VisionAnalysis();
        Assert.False(analysis.NotificationRecommended);
        Assert.False(analysis.Person.Visible);
        Assert.False(analysis.Vehicle.Visible);
        Assert.False(analysis.Package.Visible);
        Assert.Equal("unknown", analysis.EventType);
    }

    [Fact]
    public void ClosedGate_PersonInside_IsSuppressed()
    {
        var result = GateResult("inside_driveway");

        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_PersonOutside_IsNotSuppressed()
    {
        var result = GateResult("outside_gate");

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleSettledInUsualSpot_NoPerson_IsSuppressed()
    {
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "parked_inside_property", Activity = "parked" });

        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleFreshlyInDriveway_IsNotSuppressed()
    {
        // A vehicle that just arrived looks the same, in a single still frame, as one that has
        // been sitting there a while: the model can't confirm motion, so it reports "unknown"
        // activity rather than "entering". Location alone (inside_driveway, not the more settled
        // parked_inside_property) must be enough to avoid dropping real arrivals.
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "inside_driveway", Activity = "unknown" });

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleAtOutsideEntrance_IsNotSuppressed()
    {
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "outside_gate_entrance", Activity = "stopped" });

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    private static AlertResult GateResult(string? personLocation = null, VehicleAnalysis? vehicle = null) => new()
    {
        Profile = "gate",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Gate = new GateAnalysis { State = "closed" },
            Person = personLocation is null
                ? new PersonAnalysis { Visible = false }
                : new PersonAnalysis { Visible = true, Location = personLocation },
            Vehicle = vehicle ?? new VehicleAnalysis()
        }
    };

    [Fact]
    public void QuietHoursPolicy_IsActive_HandlesOvernightWraparound()
    {
        var options = new QuietHoursOptions { Enabled = true, StartHour = 22, EndHour = 6 };

        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(23)));
        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(2)));
        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(22)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(6)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(12)));
    }

    [Fact]
    public void QuietHoursPolicy_IsActive_HandlesSameDayWindow()
    {
        var options = new QuietHoursOptions { Enabled = true, StartHour = 9, EndHour = 17 };

        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(9)));
        Assert.True(QuietHoursPolicy.IsActive(options, AtHour(12)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(17)));
        Assert.False(QuietHoursPolicy.IsActive(options, AtHour(3)));
    }

    [Fact]
    public void QuietHoursPolicy_IsActive_FalseWhenDisabledOrZeroWidth()
    {
        Assert.False(QuietHoursPolicy.IsActive(new QuietHoursOptions { Enabled = false, StartHour = 22, EndHour = 6 }, AtHour(23)));
        Assert.False(QuietHoursPolicy.IsActive(new QuietHoursOptions { Enabled = true, StartHour = 5, EndHour = 5 }, AtHour(5)));
    }

    [Fact]
    public void QuietHoursPolicy_EffectiveMinimumConfidence_OnlyRaisesNeverLowers()
    {
        var options = new QuietHoursOptions { Enabled = true, StartHour = 22, EndHour = 6, MinimumConfidence = 0.85 };

        // Outside the window: untouched.
        Assert.Equal(0.6, QuietHoursPolicy.EffectiveMinimumConfidence(options, 0.6, AtHour(12)));

        // Inside the window: raised because the channel's own floor (0.6) is lower.
        Assert.Equal(0.85, QuietHoursPolicy.EffectiveMinimumConfidence(options, 0.6, AtHour(2)));

        // Inside the window, but the channel already requires more than quiet hours do: unchanged.
        Assert.Equal(0.95, QuietHoursPolicy.EffectiveMinimumConfidence(options, 0.95, AtHour(2)));
    }

    // QuietHoursPolicy compares against the local wall-clock hour (via ToLocalTime()), so this
    // builds the DateTimeOffset using the test machine's own local offset - not a fixed one -
    // so the resulting .ToLocalTime().Hour is always exactly the requested hour regardless of
    // which timezone the tests happen to run in.
    private static DateTimeOffset AtHour(int hour)
    {
        var local = new DateTime(2026, 7, 24, hour, 0, 0, DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

    [Fact]
    public void TelegramAlertAction_BuildCaption_EscapesHtmlAndIncludesReason()
    {
        var result = new AlertResult
        {
            Camera = "Gate <front>",
            Analysis = new VisionAnalysis
            {
                Summary = "A car pulled into the driveway.",
                NotificationReason = "Vehicle stopped at the gate entrance."
            }
        };

        var caption = TelegramAlertAction.BuildCaption(result);

        Assert.Contains("Gate &lt;front&gt;", caption);
        Assert.Contains("A car pulled into the driveway.", caption);
        Assert.Contains("Vehicle stopped at the gate entrance.", caption);
    }

    [Fact]
    public void BlueIrisSnapshotClient_BuildSnapshotUrl_WithoutCredentials_OmitsQueryString()
    {
        var options = new BlueIrisOptions { BaseUrl = "http://192.168.50.7:777", Camera = "gateCamera" };

        var url = BlueIrisSnapshotClient.BuildSnapshotUrl(options);

        Assert.Equal("http://192.168.50.7:777/image/gateCamera", url);
    }

    [Fact]
    public void BlueIrisSnapshotClient_BuildSnapshotUrl_WithCredentials_IncludesUserAndPw()
    {
        var options = new BlueIrisOptions
        {
            BaseUrl = "http://192.168.50.7:777/",
            Camera = "gateCamera",
            Username = "admin",
            Password = "p@ss w/ord"
        };

        var url = BlueIrisSnapshotClient.BuildSnapshotUrl(options);

        Assert.Equal("http://192.168.50.7:777/image/gateCamera?user=admin&pw=p%40ss%20w%2Ford", url);
    }

    [Fact]
    public void KnownVehicleMatcher_Match_IsCaseAndFormatInsensitive()
    {
        var known = new List<KnownVehicle>
        {
            new() { Plate = "ABC-1234", Label = "Nathan's Truck" },
            new() { Plate = "XYZ 9999", Label = "Delivery Van" }
        };

        Assert.Equal("Nathan's Truck", KnownVehicleMatcher.Match(known, "abc1234"));
        Assert.Equal("Nathan's Truck", KnownVehicleMatcher.Match(known, "ABC 1234"));
        Assert.Equal("Delivery Van", KnownVehicleMatcher.Match(known, "xyz-9999"));
    }

    [Fact]
    public void KnownVehicleMatcher_Match_ReturnsNullForUnknownOrEmptyPlate()
    {
        var known = new List<KnownVehicle> { new() { Plate = "ABC1234", Label = "Nathan's Truck" } };

        Assert.Null(KnownVehicleMatcher.Match(known, "ZZZ9999"));
        Assert.Null(KnownVehicleMatcher.Match(known, ""));
        Assert.Null(KnownVehicleMatcher.Match(known, null));
        Assert.Null(KnownVehicleMatcher.Match([], "ABC1234"));
    }

    [Fact]
    public void AlertCategorizer_DescribeVehiclePlate_ReflectsKnownVsUnrecognized()
    {
        Assert.Null(AlertCategorizer.DescribeVehiclePlate(new VehicleAnalysis { LicensePlateText = "" }));

        var unknown = AlertCategorizer.DescribeVehiclePlate(new VehicleAnalysis { LicensePlateText = "ZZZ9999" });
        Assert.Equal("Plate: ZZZ9999 (unrecognized)", unknown);

        var known = AlertCategorizer.DescribeVehiclePlate(
            new VehicleAnalysis { LicensePlateText = "ABC1234", KnownVehicleLabel = "Nathan's Truck" });
        Assert.Equal("Plate: ABC1234 (known: Nathan's Truck)", known);
    }

    [Fact]
    public void OllamaVisionClient_BuildPrompt_OnlyIncludesLicensePlateWhenProfileOptsIn()
    {
        var alert = new IncomingAlert(
            Guid.NewGuid(), "gateCamera", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "gate", "image/jpeg", "alert.jpg", []);
        var disabledProfile = new PromptProfile { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"] };
        var enabledProfile = new PromptProfile { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"], LicensePlateRecognitionEnabled = true };

        var withoutPlate = OllamaVisionClient.BuildPrompt(alert, disabledProfile);
        Assert.DoesNotContain("license_plate", withoutPlate);

        var withPlate = OllamaVisionClient.BuildPrompt(alert, enabledProfile);
        Assert.Contains("\"license_plate\": \"\"", withPlate);
        Assert.Contains("transcribe it into vehicle.license_plate", withPlate);

        // Disabled (the default for all 103 shipped profiles) must be byte-identical to what
        // every existing profile already gets - no accidental prompt drift from adding this field.
        var otherDefaultProfile = OllamaVisionClient.BuildPrompt(alert, new PromptProfile { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"] });
        Assert.Equal(withoutPlate, otherDefaultProfile);
    }

    [Fact]
    public void OpenAiVisionClient_BuildJsonSchema_OnlyIncludesLicensePlateWhenProfileOptsIn()
    {
        var disabledProfile = new PromptProfile { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"] };
        var enabledProfile = new PromptProfile { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"], LicensePlateRecognitionEnabled = true };

        var withoutPlate = OpenAiVisionClient.BuildJsonSchema(disabledProfile);
        var vehicleWithout = withoutPlate["properties"]!["vehicle"]!;
        Assert.Null(vehicleWithout["properties"]!["license_plate"]);
        Assert.DoesNotContain("license_plate", vehicleWithout["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        var withPlate = OpenAiVisionClient.BuildJsonSchema(enabledProfile);
        var vehicleWith = withPlate["properties"]!["vehicle"]!;
        Assert.NotNull(vehicleWith["properties"]!["license_plate"]);
        Assert.Contains("license_plate", vehicleWith["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void OpenAiVisionClient_BuildJsonSchema_MarksEveryPropertyRequiredAndForbidsExtras()
    {
        var schema = OpenAiVisionClient.BuildJsonSchema(new PromptProfile { SystemPrompt = "Test.", Questions = ["Q"] });

        void AssertStrict(JsonObject obj)
        {
            Assert.Equal("object", obj["type"]!.GetValue<string>());
            Assert.False(obj["additionalProperties"]!.GetValue<bool>());
            var propertyNames = obj["properties"]!.AsObject().Select(p => p.Key).ToList();
            var requiredNames = obj["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Equal(propertyNames.OrderBy(n => n), requiredNames.OrderBy(n => n));
        }

        AssertStrict(schema);
        AssertStrict(schema["properties"]!["gate"]!.AsObject());
        AssertStrict(schema["properties"]!["person"]!.AsObject());
        AssertStrict(schema["properties"]!["vehicle"]!.AsObject());
        AssertStrict(schema["properties"]!["package"]!.AsObject());
        AssertStrict(schema["properties"]!["animal"]!.AsObject());
    }

    [Fact]
    public void OpenAiVisionClient_ParseResponse_ExtractsAnalysisFromChoicesMessageContent()
    {
        const string analysisJson = """
            {"event_type":"vehicle_at_gate","summary":"A car is at the gate.",
             "gate":{"state":"closed"},
             "person":{"visible":false,"location":"none","activity":"none","outside_vehicle":false,"description":""},
             "vehicle":{"visible":true,"location":"outside_gate_entrance","orientation":"facing_gate","activity":"stopped","type":"car","color":"blue","description":"","delivery_company":"none","delivery_company_confidence":0.0},
             "package":{"visible":false,"status":"none","location":"none"},
             "animal":{"visible":false,"description":""},
             "confidence":0.9,"notification_recommended":true,"notification_reason":"Vehicle waiting at gate."}
            """;
        var responseText = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = analysisJson } } }
        });

        var (analysis, raw) = OpenAiVisionClient.ParseResponse(responseText);

        Assert.Equal("vehicle_at_gate", analysis.EventType);
        Assert.True(analysis.Vehicle.Visible);
        Assert.Equal(0.9, analysis.Confidence);
        Assert.Equal(analysisJson, raw);
    }

    [Fact]
    public void OpenAiVisionClient_ParseResponse_ThrowsWhenContentIsMissing()
    {
        var responseText = JsonSerializer.Serialize(new { choices = Array.Empty<object>() });
        Assert.Throws<IndexOutOfRangeException>(() => OpenAiVisionClient.ParseResponse(responseText));
    }

    [Fact]
    public void AnthropicVisionClient_ParseResponse_ExtractsAnalysisFromContentBlock()
    {
        const string analysisJson = """
            {"event_type":"vehicle_at_gate","summary":"A car is at the gate.",
             "gate":{"state":"closed"},
             "person":{"visible":false,"location":"none","activity":"none","outside_vehicle":false,"description":""},
             "vehicle":{"visible":true,"location":"outside_gate_entrance","orientation":"facing_gate","activity":"stopped","type":"car","color":"blue","description":"","delivery_company":"none","delivery_company_confidence":0.0},
             "package":{"visible":false,"status":"none","location":"none"},
             "animal":{"visible":false,"description":""},
             "confidence":0.9,"notification_recommended":true,"notification_reason":"Vehicle waiting at gate."}
            """;
        var responseText = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = analysisJson } }
        });

        var (analysis, raw) = AnthropicVisionClient.ParseResponse(responseText);

        Assert.Equal("vehicle_at_gate", analysis.EventType);
        Assert.True(analysis.Vehicle.Visible);
        Assert.Equal(analysisJson, raw);
    }

    [Fact]
    public void AnthropicVisionClient_ParseResponse_StripsCodeFenceIfPresent()
    {
        // Anthropic isn't given a strict JSON-schema mode like OpenAI's, so - like Ollama - it
        // occasionally wraps its answer in a markdown code fence despite being asked not to.
        const string analysisJson = """{"event_type":"none","summary":"Nothing notable.","gate":{"state":"unknown"},"person":{"visible":false,"location":"none","activity":"none","outside_vehicle":false,"description":""},"vehicle":{"visible":false,"location":"none","orientation":"unknown","activity":"none","type":"none","color":"unknown","description":"","delivery_company":"none","delivery_company_confidence":0.0},"package":{"visible":false,"status":"none","location":"none"},"animal":{"visible":false,"description":""},"confidence":0.1,"notification_recommended":false,"notification_reason":"Nothing happening."}""";
        var fenced = $"```json\n{analysisJson}\n```";
        var responseText = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = fenced } }
        });

        var (analysis, _) = AnthropicVisionClient.ParseResponse(responseText);

        Assert.Equal("none", analysis.EventType);
    }

    [Fact]
    public void AnthropicVisionClient_BuildRequest_UsesXApiKeyStyleFieldsNotBearerShape()
    {
        var profile = MakeProfile();
        var request = AnthropicVisionClient.BuildRequest("claude-3-5-sonnet-latest", profile, "prompt text", [1, 2, 3]);
        var json = JsonSerializer.Serialize(request);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Distinguishing shape from OpenAI's request: "system" is a top-level string, not a
        // message with role "system", and max_tokens is required.
        Assert.Equal("claude-3-5-sonnet-latest", root.GetProperty("model").GetString());
        Assert.Equal(profile.SystemPrompt, root.GetProperty("system").GetString());
        Assert.True(root.GetProperty("max_tokens").GetInt32() > 0);
        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("base64", content[1].GetProperty("source").GetProperty("type").GetString());
    }

    [Fact]
    public void GoogleVisionClient_ParseResponse_ExtractsAnalysisFromCandidateParts()
    {
        const string analysisJson = """
            {"event_type":"none","summary":"Empty driveway.",
             "gate":{"state":"unknown"},
             "person":{"visible":false,"location":"none","activity":"none","outside_vehicle":false,"description":""},
             "vehicle":{"visible":false,"location":"none","orientation":"unknown","activity":"none","type":"none","color":"unknown","description":"","delivery_company":"none","delivery_company_confidence":0.0},
             "package":{"visible":false,"status":"none","location":"none"},
             "animal":{"visible":false,"description":""},
             "confidence":0.2,"notification_recommended":false,"notification_reason":"Nothing happening."}
            """;
        var responseText = JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = analysisJson } } } } }
        });

        var (analysis, raw) = GoogleVisionClient.ParseResponse(responseText);

        Assert.Equal("none", analysis.EventType);
        Assert.Equal(analysisJson, raw);
    }

    [Fact]
    public void GoogleVisionClient_BuildJsonSchema_UsesUppercaseTypesAndNoAdditionalProperties()
    {
        var schema = GoogleVisionClient.BuildJsonSchema(MakeProfile());

        Assert.Equal("OBJECT", schema["type"]!.GetValue<string>());
        Assert.Null(schema["additionalProperties"]);
        Assert.Equal("STRING", schema["properties"]!["event_type"]!["type"]!.GetValue<string>());
        Assert.Equal("BOOLEAN", schema["properties"]!["vehicle"]!["properties"]!["visible"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void GoogleVisionClient_BuildJsonSchema_OnlyIncludesLicensePlateWhenProfileOptsIn()
    {
        var withoutPlate = GoogleVisionClient.BuildJsonSchema(MakeProfile());
        Assert.Null(withoutPlate["properties"]!["vehicle"]!["properties"]!["license_plate"]);

        var enabledProfile = new PromptProfile { SystemPrompt = "Test.", Questions = ["Q"], LicensePlateRecognitionEnabled = true };
        var withPlate = GoogleVisionClient.BuildJsonSchema(enabledProfile);
        Assert.NotNull(withPlate["properties"]!["vehicle"]!["properties"]!["license_plate"]);
    }

    [Fact]
    public async Task CloudVisionClient_Resolve_DispatchesToConfiguredProvider()
    {
        var openAi = new FakeVisionClient("gpt-4o-mini");
        var anthropic = new FakeVisionClient("claude-3-5-sonnet-latest");
        var google = new FakeVisionClient("gemini-1.5-flash");
        var client = new CloudVisionClient(
            openAi, anthropic, google,
            new StaticOptionsMonitor<CloudVisionOptions>(new CloudVisionOptions { Provider = "anthropic" }),
            NullLogger<CloudVisionClient>.Instance);

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("claude-3-5-sonnet-latest", model);
        Assert.Equal(1, anthropic.CallCount);
        Assert.Equal(0, openAi.CallCount);
        Assert.Equal(0, google.CallCount);
    }

    [Fact]
    public async Task CloudVisionClient_Resolve_DefaultsToOpenAiForUnrecognizedProvider()
    {
        var openAi = new FakeVisionClient("gpt-4o-mini");
        var anthropic = new FakeVisionClient("claude-3-5-sonnet-latest");
        var google = new FakeVisionClient("gemini-1.5-flash");
        var client = new CloudVisionClient(
            openAi, anthropic, google,
            new StaticOptionsMonitor<CloudVisionOptions>(new CloudVisionOptions { Provider = "not-a-real-provider" }),
            NullLogger<CloudVisionClient>.Instance);

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gpt-4o-mini", model);
    }

    [Fact]
    public async Task FailoverVisionClient_UsesPrimaryOnly_WhenItSucceeds()
    {
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        var client = new FailoverVisionClient(
            ollama, cloud,
            new StaticOptionsMonitor<VisionRoutingOptions>(new VisionRoutingOptions { Primary = "ollama", FallbackEnabled = true }),
            new StaticOptionsMonitor<CloudVisionOptions>(new CloudVisionOptions { Enabled = true, ApiKey = "key" }),
            NullLogger<FailoverVisionClient>.Instance);

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gemma3:4b", model);
        Assert.Equal(0, cloud.CallCount);
    }

    [Fact]
    public async Task FailoverVisionClient_FallsBackToOtherBackend_WhenPrimaryFailsAndFallbackEnabled()
    {
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini") { ThrowOnAnalyze = true };
        // Primary is cloud, which fails - fallback should land on Ollama.
        var client = new FailoverVisionClient(
            ollama, cloud,
            new StaticOptionsMonitor<VisionRoutingOptions>(new VisionRoutingOptions { Primary = "cloud", FallbackEnabled = true }),
            new StaticOptionsMonitor<CloudVisionOptions>(new CloudVisionOptions { Enabled = true, ApiKey = "key" }),
            NullLogger<FailoverVisionClient>.Instance);

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gemma3:4b", model);
        Assert.Equal(1, cloud.CallCount);
        Assert.Equal(1, ollama.CallCount);
    }

    [Fact]
    public async Task FailoverVisionClient_PropagatesFailure_WhenFallbackDisabled()
    {
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini") { ThrowOnAnalyze = true };
        var client = new FailoverVisionClient(
            ollama, cloud,
            new StaticOptionsMonitor<VisionRoutingOptions>(new VisionRoutingOptions { Primary = "cloud", FallbackEnabled = false }),
            new StaticOptionsMonitor<CloudVisionOptions>(new CloudVisionOptions { Enabled = true, ApiKey = "key" }),
            NullLogger<FailoverVisionClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken));
        Assert.Equal(0, ollama.CallCount);
    }

    [Fact]
    public async Task FailoverVisionClient_UsesOllama_WhenCloudPrimaryButNotConfigured()
    {
        var ollama = new FakeVisionClient("gemma3:4b");
        var cloud = new FakeVisionClient("gpt-4o-mini");
        // Primary says cloud, but no API key - a settings mistake shouldn't fail every alert.
        var client = new FailoverVisionClient(
            ollama, cloud,
            new StaticOptionsMonitor<VisionRoutingOptions>(new VisionRoutingOptions { Primary = "cloud", FallbackEnabled = false }),
            new StaticOptionsMonitor<CloudVisionOptions>(new CloudVisionOptions { Enabled = false, ApiKey = "" }),
            NullLogger<FailoverVisionClient>.Instance);

        var (_, _, model) = await client.AnalyzeAsync(MakeAlert(), MakeProfile(), TestContext.Current.CancellationToken);

        Assert.Equal("gemma3:4b", model);
        Assert.Equal(0, cloud.CallCount);
    }

    [Fact]
    public async Task FailoverVisionClient_IsReadyAsync_TrueIfEitherBackendIsReady()
    {
        var ollama = new FakeVisionClient("gemma3:4b") { Ready = false };
        var cloud = new FakeVisionClient("gpt-4o-mini") { Ready = true };
        var client = new FailoverVisionClient(
            ollama, cloud,
            new StaticOptionsMonitor<VisionRoutingOptions>(new VisionRoutingOptions { Primary = "ollama", FallbackEnabled = true }),
            new StaticOptionsMonitor<CloudVisionOptions>(new CloudVisionOptions { Enabled = true, ApiKey = "key" }),
            NullLogger<FailoverVisionClient>.Instance);

        Assert.True(await client.IsReadyAsync(TestContext.Current.CancellationToken));

        ollama.Ready = false;
        cloud.Ready = false;
        Assert.False(await client.IsReadyAsync(TestContext.Current.CancellationToken));
    }

    private static IncomingAlert MakeAlert() => new(
        Guid.NewGuid(), "gateCamera", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "gate", "image/jpeg", "alert.jpg", []);

    private static PromptProfile MakeProfile() => new() { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"] };

    private sealed class FakeVisionClient(string model) : IVisionAnalysisClient
    {
        public int CallCount { get; private set; }
        public bool ThrowOnAnalyze { get; init; }
        public bool Ready { get; set; } = true;

        public Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
            IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowOnAnalyze)
                throw new InvalidOperationException($"{model} is unavailable.");
            return Task.FromResult((new VisionAnalysis(), "{}", model));
        }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Ready);
    }

    [Fact]
    public void MqttTopics_Slug_HandlesSpacesPunctuationAndCase()
    {
        Assert.Equal("gatecamera", MqttTopics.Slug("gateCamera"));
        Assert.Equal("front_door_cam", MqttTopics.Slug("Front Door Cam"));
        Assert.Equal("cam__1", MqttTopics.Slug("Cam #1"));
        Assert.Equal("unknown", MqttTopics.Slug(""));
    }

    [Fact]
    public void MqttTopics_BuildsExpectedTopicStrings()
    {
        Assert.Equal("sentinelcore/status", MqttTopics.Availability("sentinelcore"));
        Assert.Equal("sentinelcore/gatecamera/event/state", MqttTopics.EventState("sentinelcore", "gateCamera"));
        Assert.Equal("sentinelcore/gatecamera/event/attributes", MqttTopics.EventAttributes("sentinelcore", "gateCamera"));
        Assert.Equal("sentinelcore/gatecamera/notified/state", MqttTopics.NotifiedState("sentinelcore", "gateCamera"));
        Assert.Equal(
            "homeassistant/binary_sensor/sentinelcore_gatecamera/notified/config",
            MqttTopics.DiscoveryConfigTopic("homeassistant", "binary_sensor", "sentinelcore", "gateCamera", "notified"));
    }

    [Fact]
    public void MqttTopics_DiscoveryPayloads_IncludeHomeAssistantRequiredFields()
    {
        var options = new MqttOptions { TopicPrefix = "sentinelcore", HomeAssistantDiscoveryPrefix = "homeassistant" };

        var notifiedJson = MqttTopics.BuildNotifiedDiscoveryPayload(options, "gateCamera");
        using (var doc = JsonDocument.Parse(notifiedJson))
        {
            var root = doc.RootElement;
            Assert.Equal("sentinelcore_gatecamera_notified", root.GetProperty("unique_id").GetString());
            Assert.Equal("sentinelcore/gatecamera/notified/state", root.GetProperty("state_topic").GetString());
            Assert.Equal(30, root.GetProperty("off_delay").GetInt32());
            Assert.Equal("sentinelcore/status", root.GetProperty("availability_topic").GetString());
            Assert.Equal("sentinelcore_gatecamera", root.GetProperty("device").GetProperty("identifiers")[0].GetString());
        }

        var eventJson = MqttTopics.BuildLastEventDiscoveryPayload(options, "gateCamera");
        using (var doc = JsonDocument.Parse(eventJson))
        {
            var root = doc.RootElement;
            Assert.Equal("sentinelcore/gatecamera/event/state", root.GetProperty("state_topic").GetString());
            Assert.Equal("sentinelcore/gatecamera/event/attributes", root.GetProperty("json_attributes_topic").GetString());
        }
    }

    [Fact]
    public void MqttTopics_BuildEventAttributesPayload_IncludesPlateAndOmitsNulls()
    {
        var result = new AlertResult
        {
            Camera = "gateCamera",
            OccurredAt = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.FromHours(-5)),
            Profile = "gate",
            Analysis = new VisionAnalysis
            {
                Summary = "A car pulled into the driveway.",
                Confidence = 0.8,
                Vehicle = new VehicleAnalysis { Visible = true, LicensePlateText = "ABC1234", KnownVehicleLabel = "Nathan's Truck" }
            }
        };

        var json = MqttTopics.BuildEventAttributesPayload(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("gateCamera", root.GetProperty("camera").GetString());
        Assert.Equal("ABC1234", root.GetProperty("vehicle_license_plate").GetString());
        Assert.Equal("Nathan's Truck", root.GetProperty("vehicle_known_label").GetString());
        Assert.False(root.TryGetProperty("nonexistent_field", out _));
    }

    [Fact]
    public void BugReportEmail_Build_ProducesExpectedSubjectRecipientAndBody()
    {
        var options = new EmailOptions { Username = "a@b.com", FromAddress = "", ToAddress = "a@b.com" };
        var report = new BugReport(
            "BlueIrisOllamaBridge.Web", "BlueIrisOllamaBridge.Web.TriggerZoneMonitor", "Error",
            "Failed to save trigger snapshot", "System.IO.IOException: disk full",
            new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.FromHours(-5)));

        using var message = BugReportEmail.Build(options, report);

        Assert.Equal("nathan@nr5p.com", Assert.Single(message.To).Address);
        Assert.Equal("a@b.com", message.From!.Address);
        Assert.Contains("BlueIrisOllamaBridge.Web", message.Subject);
        Assert.Contains("TriggerZoneMonitor", message.Subject);
        Assert.Contains("Failed to save trigger snapshot", message.Body);
        Assert.Contains("System.IO.IOException: disk full", message.Body);
        Assert.False(message.IsBodyHtml);
    }

    [Fact]
    public void BugReportEmail_ExceptionTypeName_ExtractsTypeOrFallsBackToNoException()
    {
        var withException = new BugReport("p", "c", "Error", "m", "System.InvalidOperationException: bad state", DateTimeOffset.UtcNow);
        var withoutException = new BugReport("p", "c", "Error", "m", null, DateTimeOffset.UtcNow);

        Assert.Equal("System.InvalidOperationException", BugReportEmail.ExceptionTypeName(withException));
        Assert.Equal("NoException", BugReportEmail.ExceptionTypeName(withoutException));
    }

    [Fact]
    public async Task BugReportLoggerProvider_OnlyForwardsErrorLevelFromOurOwnNamespaces()
    {
        var sender = new RecordingBugReportSender();
        var services = new ServiceCollection()
            .AddSingleton<IBugReportSender>(sender)
            .BuildServiceProvider();
        var provider = new BugReportLoggerProvider("TestProcess", services);

        var ourLogger = provider.CreateLogger("BlueIrisOllamaBridge.Web.TriggerZoneMonitor");
        var frameworkLogger = provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");

        ourLogger.LogInformation("routine info, should not report");
        ourLogger.LogWarning("a warning, should not report");
        frameworkLogger.LogError(new InvalidOperationException("framework noise"), "framework error, should not report");
        ourLogger.LogError(new InvalidOperationException("boom"), "our real error, should report");

        await sender.WaitForCallAsync(TestContext.Current.CancellationToken);

        var report = Assert.Single(sender.Reports);
        Assert.Equal("TestProcess", report.Process);
        Assert.Equal("BlueIrisOllamaBridge.Web.TriggerZoneMonitor", report.Category);
        Assert.Contains("boom", report.ExceptionDetails);
    }

    private sealed class RecordingBugReportSender : IBugReportSender
    {
        public List<BugReport> Reports { get; } = [];
        private readonly TaskCompletionSource _received = new();

        public Task SendAsync(BugReport report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            _received.TrySetResult();
            return Task.CompletedTask;
        }

        public Task WaitForCallAsync(CancellationToken cancellationToken) =>
            _received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    [Fact]
    public void TelegramChatWorker_IsPhotoCommand_RecognizesCommandVariants()
    {
        Assert.True(TelegramChatWorker.IsPhotoCommand("/photo"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/PHOTO"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/snapshot"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/photo@GateAlert332bot"));
        Assert.True(TelegramChatWorker.IsPhotoCommand("/photo please"));
        Assert.False(TelegramChatWorker.IsPhotoCommand("can I get a photo?"));
        Assert.False(TelegramChatWorker.IsPhotoCommand("what happened today"));
    }

    [Fact]
    public void DegradationTracker_AlertsOnlyAfterConsecutiveFailures_NotOnFirstBlip()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 3);

        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.Alert, tracker.Observe(isHealthy: false));
        Assert.True(tracker.IsDegraded);
    }

    [Fact]
    public void DegradationTracker_DoesNotReAlertWhileAlreadyDegraded()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 1);

        Assert.Equal(DegradationTransition.Alert, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
    }

    [Fact]
    public void DegradationTracker_RecoversOnFirstHealthyObservation()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 1);

        tracker.Observe(isHealthy: false);
        Assert.True(tracker.IsDegraded);

        Assert.Equal(DegradationTransition.Recovered, tracker.Observe(isHealthy: true));
        Assert.False(tracker.IsDegraded);
    }

    [Fact]
    public void DegradationTracker_BriefBlipBelowThreshold_NeverAlertsAndResetsCounter()
    {
        var tracker = new DegradationTracker(consecutiveBeforeAlert: 3);

        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: true));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.Equal(DegradationTransition.NoChange, tracker.Observe(isHealthy: false));
        Assert.False(tracker.IsDegraded);
    }

    [Fact]
    public void TelegramChatWorker_IsAuthorizedChat_OnlyMatchesConfiguredChatId()
    {
        Assert.True(TelegramChatWorker.IsAuthorizedChat("123456789", "123456789"));
        Assert.False(TelegramChatWorker.IsAuthorizedChat("999999999", "123456789"));
        Assert.False(TelegramChatWorker.IsAuthorizedChat("123456789", ""));
    }

    [Fact]
    public void TelegramChatWorker_BuildEventLog_EmptyList_ReturnsPlaceholder()
    {
        var log = TelegramChatWorker.BuildEventLog([]);

        Assert.Equal("No camera events have been recorded in this period.", log);
    }

    [Fact]
    public void TelegramChatWorker_BuildEventLog_IncludesEachEventAndNotificationFlag()
    {
        var events = new List<AlertEventRecord>
        {
            new(Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "vehicle_entering_property", "A car pulled into the driveway.", true, "Vehicle entering", 0.8, null),
            new(Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "none", "Nothing of note.", false, "No activity", 0.5, null)
        };

        var log = TelegramChatWorker.BuildEventLog(events);

        Assert.Contains("A car pulled into the driveway.", log);
        Assert.Contains("notified=yes", log);
        Assert.Contains("Nothing of note.", log);
        Assert.Contains("notified=no", log);
    }

    [Fact]
    public void GetNextRun_BeforeMorningHour_FiresThisMorning()
    {
        var now = new DateTimeOffset(2026, 7, 24, 6, 0, 0, TimeSpan.FromHours(-5));

        var (fireAt, kind) = DailySummaryWorker.GetNextRun(now, morningHour: 7, eveningHour: 20);

        Assert.Equal(DigestKind.Morning, kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 7, 0, 0, TimeSpan.FromHours(-5)), fireAt);
    }

    [Fact]
    public void GetNextRun_BetweenMorningAndEvening_FiresThisEvening()
    {
        var now = new DateTimeOffset(2026, 7, 24, 7, 30, 0, TimeSpan.FromHours(-5));

        var (fireAt, kind) = DailySummaryWorker.GetNextRun(now, morningHour: 7, eveningHour: 20);

        Assert.Equal(DigestKind.Evening, kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 20, 0, 0, TimeSpan.FromHours(-5)), fireAt);
    }

    [Fact]
    public void GetNextRun_AfterEveningHour_FiresTomorrowMorning()
    {
        var now = new DateTimeOffset(2026, 7, 24, 20, 30, 0, TimeSpan.FromHours(-5));

        var (fireAt, kind) = DailySummaryWorker.GetNextRun(now, morningHour: 7, eveningHour: 20);

        Assert.Equal(DigestKind.Morning, kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 7, 0, 0, TimeSpan.FromHours(-5)), fireAt);
    }

    [Fact]
    public async Task SqliteEventStore_RecordAndQuery_RoundTripsWithinWindow()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteEventStore(options, new FakeHostEnvironment(tempDir));

            var inWindow = MakeResult(
                new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.FromHours(-5)),
                blueIrisAlertId: "alert-record-42");
            var outsideWindow = MakeResult(new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.FromHours(-5)));

            await store.RecordAsync(inWindow, TestContext.Current.CancellationToken);
            await store.RecordAsync(outsideWindow, TestContext.Current.CancellationToken);

            var results = await store.GetEventsAsync(
                new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.FromHours(-5)),
                new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.FromHours(-5)),
                TestContext.Current.CancellationToken);

            var record = Assert.Single(results);
            Assert.Equal(inWindow.Id, record.Id);
            Assert.Equal("completed", record.Status);
            Assert.Equal("vehicle_entering_property", record.EventType);
            Assert.True(record.NotificationRecommended);
            Assert.Equal("alert-record-42", record.BlueIrisAlertId);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteEventStore_GetPendingAlerts_ReturnsOnlyQueuedRowsWithResumeDetails()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteEventStore(options, new FakeHostEnvironment(tempDir));
            var occurredAt = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.FromHours(-5));

            var queued = MakeResult(occurredAt);
            var stillQueued = new AlertResult
            {
                Id = queued.Id, Camera = "gateCamera", BlueIrisAlertId = "bi-42",
                OccurredAt = occurredAt, ReceivedAt = occurredAt,
                Profile = "gate", Model = "gemma3:4b", Status = "queued",
                ImagePath = "/images/2026-07-25/shot.jpg"
            };

            await store.RecordAsync(stillQueued, TestContext.Current.CancellationToken);
            await store.RecordAsync(MakeResult(occurredAt), TestContext.Current.CancellationToken);

            var pending = await store.GetPendingAlertsAsync(TestContext.Current.CancellationToken);

            var record = Assert.Single(pending);
            Assert.Equal(stillQueued.Id, record.Id);
            Assert.Equal("gateCamera", record.Camera);
            Assert.Equal("bi-42", record.BlueIrisAlertId);
            Assert.Equal("gate", record.Profile);
            Assert.Equal("gemma3:4b", record.Model);
            Assert.Equal("/images/2026-07-25/shot.jpg", record.ImagePath);
            Assert.Equal(occurredAt, record.OccurredAt);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AlertRecoveryService_ResumesAlertsWithAnImageAndAbandonsOnesWithout()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var imagePath = Path.Combine(tempDir, "shot.jpg");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            var occurredAt = DateTimeOffset.UtcNow;

            var resumable = new PendingAlertRecord(
                Guid.NewGuid(), "gateCamera", "bi-1", occurredAt, occurredAt, "gate", "gemma3:4b", imagePath);
            // Image saving was off, so there's nothing on disk to retry with.
            var noImage = new PendingAlertRecord(
                Guid.NewGuid(), "sideCamera", null, occurredAt, occurredAt, "default", "gemma3:4b", null);
            // Row points at a file retention has since deleted.
            var missingImage = new PendingAlertRecord(
                Guid.NewGuid(), "backCamera", null, occurredAt, occurredAt, "default", "gemma3:4b",
                Path.Combine(tempDir, "deleted.jpg"));

            var eventStore = new PendingStubEventStore([resumable, noImage, missingImage]);
            var resultStore = new RecordingResultStore();
            var queue = new AlertQueue(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", QueueCapacity = 10 }));
            var service = new AlertRecoveryService(eventStore, resultStore, queue, NullLogger<AlertRecoveryService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, queue.ApproximateCount);
            Assert.Equal(2, resultStore.Saved.Count);
            Assert.All(resultStore.Saved, r => Assert.Equal("failed", r.Status));
            Assert.All(resultStore.Saved, r => Assert.Contains("Interrupted by a restart", r.Error));
            Assert.DoesNotContain(resultStore.Saved, r => r.Id == resumable.Id);

            // The resumed alert carries the bytes back off disk, so analysis can actually retry.
            IncomingAlert? requeued = null;
            await foreach (var item in queue.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                requeued = item;
                break;
            }

            Assert.NotNull(requeued);
            Assert.Equal(resumable.Id, requeued.Id);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, requeued.ImageBytes);
            Assert.Equal(imagePath, requeued.ImagePath);
            Assert.Equal("image/jpeg", requeued.ContentType);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("shot.png", "image/png")]
    [InlineData("shot.webp", "image/webp")]
    [InlineData("shot.jpg", "image/jpeg")]
    [InlineData("shot.JPEG", "image/jpeg")]
    public void AlertRecoveryService_ContentTypeFor_MapsExtensionBackToContentType(string fileName, string expected) =>
        Assert.Equal(expected, AlertRecoveryService.ContentTypeFor(fileName));

    private sealed class PendingStubEventStore(IReadOnlyList<PendingAlertRecord> pending) : StubEventStore
    {
        public override Task<IReadOnlyList<PendingAlertRecord>> GetPendingAlertsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(pending);
    }

    private sealed class RecordingResultStore : IResultStore
    {
        public List<AlertResult> Saved { get; } = [];

        public Task SaveAsync(AlertResult result, CancellationToken cancellationToken)
        {
            Saved.Add(result);
            return Task.CompletedTask;
        }

        public Task<AlertResult?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<AlertResult?>(null);

        public Task<string?> SaveImageAsync(IncomingAlert alert, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private static AlertResult MakeResult(
        DateTimeOffset occurredAt,
        string camera = "gateCamera",
        string eventType = "vehicle_entering_property",
        string summary = "A car pulled into the driveway.",
        bool notified = true,
        string? blueIrisAlertId = null) => new()
    {
        Id = Guid.NewGuid(),
        Camera = camera,
        BlueIrisAlertId = blueIrisAlertId,
        OccurredAt = occurredAt,
        ReceivedAt = occurredAt,
        CompletedAt = occurredAt,
        Profile = "gate",
        Model = "gemma3:4b",
        Status = "completed",
        Analysis = new VisionAnalysis
        {
            EventType = eventType,
            Summary = summary,
            NotificationRecommended = notified,
            Confidence = 0.8
        }
    };

    [Fact]
    public async Task SqliteEventStore_SearchEvents_FiltersByCameraEventTypeNotifiedAndSearchText()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteEventStore(options, new FakeHostEnvironment(tempDir));

            var now = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.FromHours(-5));
            var gateVehicle = MakeResult(now, camera: "gateCamera", eventType: "vehicle_entering_property", summary: "A car pulled into the driveway.", notified: true);
            var gateQuiet = MakeResult(now, camera: "gateCamera", eventType: "none", summary: "Nothing of note.", notified: false);
            var coopPredator = MakeResult(now, camera: "coopCamera", eventType: "predator_present", summary: "A fox was seen near the coop.", notified: true);

            await store.RecordAsync(gateVehicle, TestContext.Current.CancellationToken);
            await store.RecordAsync(gateQuiet, TestContext.Current.CancellationToken);
            await store.RecordAsync(coopPredator, TestContext.Current.CancellationToken);

            var byCamera = await store.SearchEventsAsync(new EventFilter(Camera: "coopCamera"), TestContext.Current.CancellationToken);
            Assert.Equal(coopPredator.Id, Assert.Single(byCamera).Id);

            var byEventType = await store.SearchEventsAsync(new EventFilter(EventType: "vehicle_entering_property"), TestContext.Current.CancellationToken);
            Assert.Equal(gateVehicle.Id, Assert.Single(byEventType).Id);

            var notifiedOnly = await store.SearchEventsAsync(new EventFilter(Camera: "gateCamera", NotifiedOnly: true), TestContext.Current.CancellationToken);
            Assert.Equal(gateVehicle.Id, Assert.Single(notifiedOnly).Id);

            var bySearchText = await store.SearchEventsAsync(new EventFilter(SearchText: "fox"), TestContext.Current.CancellationToken);
            Assert.Equal(coopPredator.Id, Assert.Single(bySearchText).Id);

            var everything = await store.SearchEventsAsync(new EventFilter(), TestContext.Current.CancellationToken);
            Assert.Equal(3, everything.Count);

            var options2 = await store.GetFilterOptionsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["coopCamera", "gateCamera"], options2.Cameras);
            Assert.Equal(["none", "predator_present", "vehicle_entering_property"], options2.EventTypes);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DailySummaryWorker_BuildMessageAsync_EmbedsNotableImagesAndListsAllEvents()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var imagePath = Path.Combine(tempDir, "notable.jpg");
            using (var image = new Image<Rgba32>(320, 240))
                await image.SaveAsync(imagePath, new JpegEncoder(), TestContext.Current.CancellationToken);

            var notableEvent = new AlertEventRecord(
                Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "vehicle_entering_property", "A car pulled into the driveway.",
                true, "Vehicle entering", 0.8, imagePath);
            var quietEvent = new AlertEventRecord(
                Guid.NewGuid(), "gateCamera", DateTimeOffset.Now, "completed",
                "none", "Nothing of note.", false, "No activity", 0.5, null);

            var worker = new DailySummaryWorker(
                eventStore: new StubEventStore(),
                imageOptimizer: new ImageOptimizer(new StaticOptionsMonitor<ImageProcessingOptions>(new()), NullLogger<ImageOptimizer>.Instance),
                options: new StaticOptionsMonitor<DailySummaryOptions>(new()),
                emailOptions: new StaticOptionsMonitor<EmailOptions>(new EmailOptions { Username = "a@b.com", ToAddress = "a@b.com", FromName = "Gate Camera" }),
                logger: NullLogger<DailySummaryWorker>.Instance);

            using var message = await worker.BuildMessageAsync(
                "Today's activity summary",
                DateTimeOffset.Now.AddHours(-13),
                DateTimeOffset.Now,
                [notableEvent, quietEvent],
                TestContext.Current.CancellationToken);

            Assert.Contains("2 events", message.Subject);
            var view = Assert.Single(message.AlternateViews);
            Assert.Single(view.LinkedResources);

            var html = DailySummaryWorker.BuildHtml(
                "Today's activity summary",
                DateTimeOffset.Now.AddHours(-13),
                DateTimeOffset.Now,
                [notableEvent, quietEvent],
                [notableEvent],
                []);
            Assert.Contains("A car pulled into the driveway.", html);
            Assert.Contains("Nothing of note.", html);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private class StubEventStore : IEventStore
    {
        public Task RecordAsync(AlertResult result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AlertEventRecord>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertEventRecord>>([]);

        public Task<AlertEventRecord?> GetEventAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<AlertEventRecord?>(null);

        public virtual Task<IReadOnlyList<PendingAlertRecord>> GetPendingAlertsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingAlertRecord>>([]);

        public Task<IReadOnlyList<AlertEventRecord>> SearchEventsAsync(EventFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertEventRecord>>([]);

        public Task<EventFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EventFilterOptions([], []));
    }

    [Fact]
    public void LicenseVerifier_RoundTrips_ASignedLicense()
    {
        using var rsa = RSA.Create(2048);
        var info = new LicenseInfo("Jane Doe", "jane@example.com", "SentinelCore", Guid.NewGuid(), DateTimeOffset.UtcNow);

        var licenseText = LicenseSigner.Sign(info, rsa);
        var verified = LicenseVerifier.Verify(licenseText, rsa);

        Assert.NotNull(verified);
        Assert.Equal(info, verified);
    }

    [Fact]
    public void LicenseVerifier_RejectsATamperedPayload()
    {
        using var rsa = RSA.Create(2048);
        var info = new LicenseInfo("Jane Doe", "jane@example.com", "SentinelCore", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var licenseText = LicenseSigner.Sign(info, rsa);

        // Swap in a different (validly base64, validly JSON) payload without re-signing -
        // simulates hand-editing the file to change who it's licensed to. Mutated via JsonNode
        // rather than string-replace, since the signed payload's exact formatting (indentation,
        // property order) is an implementation detail this test shouldn't depend on.
        var envelope = JsonNode.Parse(licenseText)!.AsObject();
        var forgedInfo = info with { CustomerName = "Someone Else" };
        envelope["payload"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(forgedInfo));
        var tampered = envelope.ToJsonString();

        Assert.Null(LicenseVerifier.Verify(tampered, rsa));
    }

    [Fact]
    public void LicenseVerifier_RejectsATamperedSignature()
    {
        using var rsa = RSA.Create(2048);
        var info = new LicenseInfo("Jane Doe", "jane@example.com", "SentinelCore", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var licenseText = LicenseSigner.Sign(info, rsa);

        // Flip the signature's own bytes (re-encode as different-but-still-valid base64) rather
        // than touch the payload - this is specifically testing that a corrupted signature fails
        // cryptographic verification, distinct from the malformed-envelope cases below.
        var envelope = JsonNode.Parse(licenseText)!.AsObject();
        var signatureBytes = Convert.FromBase64String(envelope["signature"]!.GetValue<string>());
        signatureBytes[0] ^= 0xFF;
        envelope["signature"] = Convert.ToBase64String(signatureBytes);
        var tampered = envelope.ToJsonString();

        Assert.Null(LicenseVerifier.Verify(tampered, rsa));
    }

    [Fact]
    public void LicenseVerifier_RejectsALicenseSignedByADifferentKey()
    {
        using var issuerKey = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        var info = new LicenseInfo("Jane Doe", "jane@example.com", "SentinelCore", Guid.NewGuid(), DateTimeOffset.UtcNow);

        var licenseText = LicenseSigner.Sign(info, issuerKey);

        Assert.Null(LicenseVerifier.Verify(licenseText, wrongKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"payload": "not-valid-base64!!!", "signature": "also-not-base64!!!"}""")]
    public void LicenseVerifier_ReturnsNullRatherThanThrowing_ForMalformedInput(string malformed)
    {
        using var rsa = RSA.Create(2048);
        Assert.Null(LicenseVerifier.Verify(malformed, rsa));
    }

    [Fact]
    public void LicenseStatus_Check_ReturnsNullWhenTheFileDoesNotExist()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.lic");
        Assert.Null(LicenseStatus.Check(missingPath));
    }

    [Fact]
    public void LicenseStatus_Verify_ReturnsNullForContentNotSignedByTheEmbeddedKey()
    {
        // LicenseStatus always verifies against the real embedded production public key, so this
        // can only exercise the "doesn't verify" path - a genuine round trip needs the matching
        // private key, which never touches this repo.
        Assert.Null(LicenseStatus.Verify("not a license file"));
    }

    [Fact]
    public void PasswordHasher_Verify_AcceptsTheCorrectPassword()
    {
        var stored = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", stored));
    }

    [Fact]
    public void PasswordHasher_Verify_RejectsTheWrongPassword()
    {
        var stored = PasswordHasher.Hash("correct horse battery staple");

        Assert.False(PasswordHasher.Verify("wrong password", stored));
    }

    [Fact]
    public void PasswordHasher_Hash_ProducesADifferentStoredValueEachTime()
    {
        // Different random salts each call - two hashes of the same password should never be
        // byte-identical, even though both verify correctly against that same password.
        var first = PasswordHasher.Hash("correct horse battery staple");
        var second = PasswordHasher.Hash("correct horse battery staple");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", first));
        Assert.True(PasswordHasher.Verify("correct horse battery staple", second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash at all")]
    [InlineData("PBKDF2.SHA256.not-a-number.c2FsdA==.aGFzaA==")]
    [InlineData("PBKDF2.SHA256.210000.not-valid-base64!!!.aGFzaA==")]
    public void PasswordHasher_Verify_ReturnsFalseRatherThanThrowing_ForMalformedStoredHash(string malformed)
    {
        Assert.False(PasswordHasher.Verify("anything", malformed));
    }

    [Fact]
    public void SessionStore_CreatedSession_ValidatesUntilInvalidated()
    {
        var sessions = new SessionStore();
        var token = sessions.CreateSession();

        Assert.True(sessions.Validate(token));

        sessions.Invalidate(token);

        Assert.False(sessions.Validate(token));
    }

    [Fact]
    public void SessionStore_Validate_RejectsAnUnknownToken()
    {
        var sessions = new SessionStore();

        Assert.False(sessions.Validate("this-token-was-never-issued"));
        Assert.False(sessions.Validate(null));
        Assert.False(sessions.Validate(""));
    }

    [Fact]
    public void SessionStore_InvalidateAll_ClearsEverySession()
    {
        var sessions = new SessionStore();
        var first = sessions.CreateSession();
        var second = sessions.CreateSession();

        sessions.InvalidateAll();

        Assert.False(sessions.Validate(first));
        Assert.False(sessions.Validate(second));
    }

    [Fact]
    public async Task ChatAnswerService_AnswerAsync_BuildsAPromptFromRecentEventsAndReturnsTheModelsAnswer()
    {
        var events = new List<AlertEventRecord>
        {
            new(Guid.NewGuid(), "Gate Camera", DateTimeOffset.UtcNow.AddHours(-1), "completed",
                "vehicle_at_gate", "A car arrived.", true, "Vehicle at gate.", 0.9, null)
        };
        var eventStore = new FakeEventStore(events);
        var chatClient = new FakeOllamaChatClient { ResponseToReturn = "A car arrived an hour ago." };
        var options = new StaticOptionsMonitor<TelegramChatOptions>(
            new TelegramChatOptions { Enabled = true, LookbackDays = 3, MaxEventsInContext = 100 });
        var service = new ChatAnswerService(
            eventStore, chatClient, options, new FakeHostEnvironment(Path.GetTempPath()), NullLogger<ChatAnswerService>.Instance);

        var answer = await service.AnswerAsync("What happened recently?", CancellationToken.None);

        Assert.Equal("A car arrived an hour ago.", answer);
        Assert.True(chatClient.WasCalled);
        Assert.Contains("Gate Camera", chatClient.LastSystemPrompt);
        Assert.Equal("What happened recently?", chatClient.LastUserMessage);
    }

    [Fact]
    public async Task ChatAnswerService_AnswerAsync_ReturnsTurnedOffMessage_WithoutCallingOllama_WhenDisabled()
    {
        var eventStore = new FakeEventStore([]);
        var chatClient = new FakeOllamaChatClient();
        var options = new StaticOptionsMonitor<TelegramChatOptions>(new TelegramChatOptions { Enabled = false });
        var service = new ChatAnswerService(
            eventStore, chatClient, options, new FakeHostEnvironment(Path.GetTempPath()), NullLogger<ChatAnswerService>.Instance);

        var answer = await service.AnswerAsync("Anything?", CancellationToken.None);

        Assert.False(chatClient.WasCalled);
        Assert.Contains("turned off", answer, StringComparison.OrdinalIgnoreCase);
    }

    // Stuffing the full app documentation into every single question - even ones purely about
    // camera history - proved slow enough on a small local model to blow past the request
    // timeout, and sometimes made the model lose track of the event log entirely. These two
    // tests guard the fix: a question routes to exactly one context source, never both.
    [Theory]
    [InlineData("what happened at the gate last night?")]
    [InlineData("did anyone come to the door today?")]
    public async Task ChatAnswerService_AnswerAsync_RoutesEventQuestions_ToTheEventLogOnly(string question)
    {
        var eventStore = new FakeEventStore([]);
        var chatClient = new FakeOllamaChatClient();
        var options = new StaticOptionsMonitor<TelegramChatOptions>(new TelegramChatOptions { Enabled = true });
        var service = new ChatAnswerService(
            eventStore, chatClient, options, new FakeHostEnvironment(Path.GetTempPath()), NullLogger<ChatAnswerService>.Instance);

        await service.AnswerAsync(question, CancellationToken.None);

        Assert.True(eventStore.GetEventsAsyncWasCalled);
        Assert.Contains("Event log:", chatClient.LastSystemPrompt);
        Assert.DoesNotContain("App documentation", chatClient.LastSystemPrompt);
    }

    [Theory]
    [InlineData("how do I turn on quiet hours?")]
    [InlineData("what is the known vehicles feature?")]
    public async Task ChatAnswerService_AnswerAsync_RoutesHowToQuestions_ToTheDocsOnly(string question)
    {
        var eventStore = new FakeEventStore([]);
        var chatClient = new FakeOllamaChatClient();
        var options = new StaticOptionsMonitor<TelegramChatOptions>(new TelegramChatOptions { Enabled = true });
        var service = new ChatAnswerService(
            eventStore, chatClient, options, new FakeHostEnvironment(Path.GetTempPath()), NullLogger<ChatAnswerService>.Instance);

        await service.AnswerAsync(question, CancellationToken.None);

        Assert.False(eventStore.GetEventsAsyncWasCalled);
        Assert.Contains("App documentation", chatClient.LastSystemPrompt);
        Assert.DoesNotContain("Event log:", chatClient.LastSystemPrompt);
    }

    private sealed class FakeEventStore(IReadOnlyList<AlertEventRecord> events) : IEventStore
    {
        public bool GetEventsAsyncWasCalled { get; private set; }

        public Task RecordAsync(AlertResult result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AlertEventRecord>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            GetEventsAsyncWasCalled = true;
            return Task.FromResult(events);
        }

        public Task<AlertEventRecord?> GetEventAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<AlertEventRecord?>(null);

        public Task<IReadOnlyList<PendingAlertRecord>> GetPendingAlertsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingAlertRecord>>([]);

        public Task<IReadOnlyList<AlertEventRecord>> SearchEventsAsync(EventFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertEventRecord>>([]);

        public Task<EventFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EventFilterOptions([], []));
    }

    private sealed class FakeOllamaChatClient : IOllamaChatClient
    {
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserMessage { get; private set; }
        public string ResponseToReturn { get; set; } = "a canned answer";
        public bool WasCalled { get; private set; }

        public Task<string> AskAsync(string systemPrompt, string userMessage, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastSystemPrompt = systemPrompt;
            LastUserMessage = userMessage;
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task SqliteCameraStore_RestoreAsync_UpsertsByExplicitId()
    {
        using var store = new SqliteCameraStore(Path.Combine(Path.GetTempPath(), $"cam-{Guid.NewGuid():N}"));
        var id = Guid.NewGuid();
        var original = new Camera(id, "Front Gate", "rtsp://example/1", null, null, false, DateTimeOffset.UtcNow);

        await store.RestoreAsync(original, CancellationToken.None);
        var restored = await store.GetAsync(id, CancellationToken.None);
        Assert.Equal("Front Gate", restored?.Name);

        // Restoring the same Id again updates in place rather than inserting a duplicate row -
        // otherwise a repeated import would pile up ghost cameras every time.
        var updated = original with { Name = "Back Gate" };
        await store.RestoreAsync(updated, CancellationToken.None);
        var all = await store.GetAllAsync(CancellationToken.None);

        Assert.Single(all);
        Assert.Equal("Back Gate", all[0].Name);
        Assert.Equal(id, all[0].Id);
    }

    [Fact]
    public async Task SqliteTriggerStore_GetAllAsync_IncludesDisabledConfigsUnlikeGetEnabledAsync()
    {
        using var store = new SqliteTriggerStore(Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}"));
        var cameraId = Guid.NewGuid();
        await store.SaveAsync(TriggerConfig.Disabled(cameraId), CancellationToken.None);

        var enabled = await store.GetEnabledAsync(CancellationToken.None);
        var all = await store.GetAllAsync(CancellationToken.None);

        Assert.Empty(enabled);
        Assert.Single(all);
        Assert.Equal(cameraId, all[0].CameraId);
    }

    [Fact]
    public void HttpsSettings_NotConfigured_StaysOffSilently()
    {
        // The shipped default. Must not warn - plain HTTP is a legitimate choice, not a fault.
        var settings = new HttpsSettings(null, null, 3443);

        Assert.False(settings.CanEnable(out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void HttpsSettings_ConfiguredButFileMissing_StaysOffWithAReason()
    {
        // The important case: a wrong path must not stop the dashboard booting, which is what
        // Kestrel's own certificate configuration would do.
        var settings = new HttpsSettings("/nonexistent/server.crt", "/nonexistent/server.key", 3443);

        Assert.False(settings.CanEnable(out var reason));
        Assert.Contains("not found", reason);
    }

    [Fact]
    public void HttpsSettings_OnlyOneHalfConfigured_StaysOffWithAReason()
    {
        var settings = new HttpsSettings("/tmp/server.crt", null, 3443);

        Assert.False(settings.CanEnable(out var reason));
        Assert.Contains("both", reason);
    }

    [Fact]
    public void HttpsSettings_WhenBothFilesExist_Enables()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"https-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var certificate = Path.Combine(directory, "server.crt");
        var key = Path.Combine(directory, "server.key");
        File.WriteAllText(certificate, "x");
        File.WriteAllText(key, "x");

        var settings = new HttpsSettings(certificate, key, 3443);

        Assert.True(settings.CanEnable(out var reason));
        Assert.Null(reason);
        Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData("http://0.0.0.0:3320", 3320)]
    [InlineData("http://0.0.0.0:8080", 8080)]
    [InlineData("http://+:5000", 5000)]
    [InlineData("https://0.0.0.0:3443;http://0.0.0.0:3320", 3320)] // picks the HTTP one
    [InlineData("", HttpsSettings.DefaultHttpPort)]
    [InlineData(null, HttpsSettings.DefaultHttpPort)]
    [InlineData("nonsense", HttpsSettings.DefaultHttpPort)]
    public void HttpsSettings_ParseHttpPort_KeepsTheExistingHttpListener(string? urls, int expected)
    {
        // Enabling HTTPS forces every endpoint to be declared explicitly, so getting this wrong
        // would silently stop HTTP listening and break Blue Iris and both mobile apps at once.
        Assert.Equal(expected, HttpsSettings.ParseHttpPort(urls));
    }

    [Theory]
    // Loopback and every private range - the normal case, must never warn.
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.50.8", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]     // CGNAT, and what Tailscale hands out
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]        // unique-local
    [InlineData("::ffff:192.168.1.5", true)] // IPv4-mapped IPv6, as Kestrel often reports it
    // Public addresses - these are the evidence of real exposure.
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("172.32.0.1", false)]    // just past the 172.16/12 block
    [InlineData("172.15.255.255", false)] // just before it
    [InlineData("192.169.1.1", false)]   // adjacent to 192.168/16 but public
    [InlineData("100.128.0.1", false)]   // just past the CGNAT block
    [InlineData("2606:4700::1111", false)]
    public void ExposureTracker_IsLocalNetwork_ClassifiesAddressesCorrectly(string address, bool expectedLocal)
    {
        Assert.Equal(expectedLocal, ExposureTracker.IsLocalNetwork(System.Net.IPAddress.Parse(address)));
    }

    [Fact]
    public void ExposureTracker_IsLocalNetwork_TreatsUnknownAddressAsLocal()
    {
        // Kestrel reports null for some transports (Unix sockets). Staying quiet is right -
        // an unknown source is not evidence of exposure.
        Assert.True(ExposureTracker.IsLocalNetwork(null));
    }

    [Fact]
    public void ExposureTracker_StaysQuietUntilSomethingExternalArrives()
    {
        var tracker = new ExposureTracker();
        Assert.False(tracker.HasSeenExternalRequest);

        tracker.Record(System.Net.IPAddress.Parse("203.0.113.7"));

        Assert.True(tracker.HasSeenExternalRequest);
        Assert.Equal("203.0.113.7", tracker.LastExternalAddress);
    }

    [Fact]
    public void RetentionWorker_PruneExpiredFiles_NeverTouchesTheDatabaseInTheDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "images", "2020-01-01"));
        Directory.CreateDirectory(Path.Combine(root, "results"));

        // Everything backdated well past any cutoff - this is the quiet-period scenario where a
        // root-wide sweep would decide the database had expired and unlink it.
        var stale = DateTime.UtcNow.AddDays(-400);
        var database = Path.Combine(root, "events.db");
        var journal = Path.Combine(root, "events.db-journal");
        var image = Path.Combine(root, "images", "2020-01-01", "old.jpg");
        var result = Path.Combine(root, "results", "old.json");

        foreach (var path in new[] { database, journal, image, result })
        {
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, stale);
        }

        RetentionWorker.PruneExpiredFiles(root, DateTime.UtcNow.AddDays(-30));

        Assert.True(File.Exists(database), "events.db must survive retention.");
        Assert.True(File.Exists(journal), "events.db-journal must survive retention - deleting it mid-write corrupts.");
        Assert.False(File.Exists(image), "expired images should still be pruned.");
        Assert.False(File.Exists(result), "expired results should still be pruned.");

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void RetentionWorker_PruneExpiredFiles_KeepsFilesNewerThanTheCutoff()
    {
        var root = Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "images"));

        var fresh = Path.Combine(root, "images", "recent.jpg");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-1));

        RetentionWorker.PruneExpiredFiles(root, DateTime.UtcNow.AddDays(-30));

        Assert.True(File.Exists(fresh));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task HomeKitAlertAction_WhenEnabledAndNotificationRecommended_TriggersSidecar()
    {
        var handler = new RecordingHandler();
        var action = BuildHomeKitAction(handler, new HomeKitOptions { Enabled = true, SidecarBaseUrl = "http://127.0.0.1:3321" });

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:3321/api/trigger", handler.Requests[0].Url);
        Assert.Contains("gate camera front door", handler.Requests[0].Body);
    }

    [Fact]
    public async Task HomeKitAlertAction_WhenDisabled_DoesNotCallSidecar()
    {
        var handler = new RecordingHandler();
        var action = BuildHomeKitAction(handler, new HomeKitOptions { Enabled = false });

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HomeKitAlertAction_WhenNotificationNotRecommended_DoesNotCallSidecar()
    {
        var handler = new RecordingHandler();
        var action = BuildHomeKitAction(handler, new HomeKitOptions { Enabled = true });

        // A completed analysis that simply isn't alert-worthy - HomeKit motion is a push
        // notification on someone's phone, so this must stay silent.
        var result = new AlertResult
        {
            Camera = "gate camera front door",
            Analysis = new VisionAnalysis { NotificationRecommended = false }
        };

        await action.ExecuteAsync(result, CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HomeKitAlertAction_WhenSidecarIsDown_DoesNotThrow()
    {
        var handler = new RecordingHandler { Throw = new HttpRequestException("connection refused") };
        var action = BuildHomeKitAction(handler, new HomeKitOptions { Enabled = true });

        // The sidecar is a separate, optional process. A dead one must never break the alert
        // pipeline for the actions queued behind it.
        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);
    }

    [Fact]
    public async Task PushDeviceStore_UpsertsAndRemovesRegistrations()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions
            {
                ApiKey = "test-key-1234567890",
                DataDirectory = tempDir
            });
            var store = new SqlitePushDeviceStore(options, new FakeHostEnvironment(tempDir));

            var first = await store.UpsertAsync("ANDROID", "fid-123", "First name", CancellationToken.None);
            var updated = await store.UpsertAsync("android", "fid-123", "Updated name", CancellationToken.None);
            await store.UpsertAsync("apple", "apns-456", "iPhone", CancellationToken.None);

            Assert.Equal(first.Id, updated.Id);
            Assert.Equal("android", updated.Platform);
            Assert.Equal("Updated name", updated.DeviceName);
            Assert.Equal(2, await store.CountAsync(CancellationToken.None));

            await store.RemoveAsync("ANDROID", "fid-123", CancellationToken.None);
            var remaining = await store.GetAllAsync(CancellationToken.None);
            Assert.Single(remaining);
            Assert.Equal("apple", remaining[0].Platform);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PushMessage_FromAlertCarriesEventDeepLinkData()
    {
        var result = new AlertResult
        {
            Id = Guid.NewGuid(),
            Camera = "Front gate",
            Analysis = new VisionAnalysis
            {
                Summary = "A person entered the driveway.",
                EventType = "person"
            }
        };

        var message = PushMessage.FromAlert(result);

        Assert.Equal("Front gate", message.Title);
        Assert.Equal(result.Id.ToString(), message.EventId);
        Assert.Equal("person", message.EventType);
        Assert.Contains("driveway", message.Body);
    }

    [Fact]
    public async Task TimelineReviewStore_SharesReviewedStateAndCanClearIt()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions
            {
                ApiKey = "test-key-1234567890",
                DataDirectory = tempDir
            });
            var store = new SqliteTimelineReviewStore(options, new FakeHostEnvironment(tempDir));
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            await store.SetReviewedAsync(first, true, CancellationToken.None);
            var reviewed = await store.GetReviewedAsync([first, second], CancellationToken.None);
            Assert.Contains(first, reviewed);
            Assert.DoesNotContain(second, reviewed);

            await store.SetReviewedAsync(first, false, CancellationToken.None);
            Assert.Empty(await store.GetReviewedAsync([first], CancellationToken.None));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("alert-record.jpg", "alert-record")]
    [InlineData("alert-record", "alert-record")]
    [InlineData("  alert-record.jpeg  ", "alert-record")]
    [InlineData(null, "")]
    public void BlueIrisTimelineClient_NormalizeRecord_MatchesAlertDatabaseIdentifiers(
        string? input,
        string expected)
    {
        Assert.Equal(expected, BlueIrisTimelineClient.NormalizeRecord(input));
    }

    private static HomeKitAlertAction BuildHomeKitAction(RecordingHandler handler, HomeKitOptions options) =>
        new(new HttpClient(handler),
            new StaticOptionsMonitor<HomeKitOptions>(options),
            NullLogger<HomeKitAlertAction>.Instance);

    private static AlertResult NotifiableResult(string camera) => new()
    {
        Camera = camera,
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Summary = "A person is standing outside the gate."
        }
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }

    // A fixed-value IOptionsMonitor<T> for tests that construct a class directly rather than
    // through DI. OnChange is never invoked since the value never changes in a test.
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
