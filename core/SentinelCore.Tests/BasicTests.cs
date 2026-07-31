using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SentinelCore.Licensing;
using SentinelCore.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
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

namespace SentinelCore.Tests;

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

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/token", true)]
    [InlineData("https://discord.com/api/v10/webhooks/123/token", true)]
    [InlineData("https://canary.discord.com/api/webhooks/123/token", true)]
    [InlineData("http://discord.com/api/webhooks/123/token", false)]
    [InlineData("https://example.com/api/webhooks/123/token", false)]
    [InlineData("", false)]
    public void DiscordOptions_ValidatesOfficialHttpsWebhookUrls(string url, bool expected)
    {
        Assert.Equal(expected, DiscordOptions.IsValidWebhookUrl(url));
    }

    [Fact]
    public void DiscordAlertAction_BuildPayload_DisablesMentionsAndIncludesAlertDetails()
    {
        var result = new AlertResult
        {
            Camera = "Gate front door",
            OccurredAt = DateTimeOffset.Parse("2026-07-27T12:00:00Z"),
            Analysis = new VisionAnalysis
            {
                Summary = "@everyone A person is at the gate.",
                NotificationReason = "Person remained near the entrance.",
                Confidence = 0.91,
                EventType = "person"
            }
        };

        using var document = JsonDocument.Parse(DiscordAlertAction.BuildPayloadJson(result, "snapshot.jpg"));
        var root = document.RootElement;
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        var embed = root.GetProperty("embeds")[0];
        Assert.Equal("Gate front door", embed.GetProperty("title").GetString());
        Assert.Contains("@everyone", embed.GetProperty("description").GetString());
        Assert.Equal("attachment://snapshot.jpg", embed.GetProperty("image").GetProperty("url").GetString());
    }

    [Fact]
    public async Task DiscordAlertAction_WhenConfigured_PostsNotifiableAlert()
    {
        var handler = new RecordingHandler();
        var action = BuildDiscordAction(handler, new DiscordOptions
        {
            Enabled = true,
            WebhookUrl = "https://discord.com/api/webhooks/123/token",
            MinimumConfidence = 0
        });

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal("https://discord.com/api/webhooks/123/token", handler.Requests[0].Url);
        Assert.Contains("gate camera front door", handler.Requests[0].Body);
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
    public void AlertCategorizer_DescribeIncident_OnlyMentionsLinkedCamerasWhenPresent()
    {
        Assert.Null(AlertCategorizer.DescribeIncident(new AlertResult { Camera = "gate" }));
        Assert.Null(AlertCategorizer.DescribeIncident(new AlertResult { Camera = "gate", LinkedCameras = "" }));

        var described = AlertCategorizer.DescribeIncident(
            new AlertResult { Camera = "gate", LinkedCameras = "Driveway, Front Door" });
        Assert.Equal("Also recorded: Driveway, Front Door", described);
    }

    [Fact]
    public void AlertCategorizer_DescribePackageTheft_OnlyMentionsWhenSuspected()
    {
        Assert.Null(AlertCategorizer.DescribePackageTheft(null));
        Assert.Null(AlertCategorizer.DescribePackageTheft(new VisionAnalysis()));

        var described = AlertCategorizer.DescribePackageTheft(
            new VisionAnalysis { PackageTheftSuspected = true, PackageTheftReason = "gone without a recognized pickup" });
        Assert.Equal("⚠ Possible package theft: gone without a recognized pickup", described);
    }

    [Fact]
    public void AlertNotificationPolicy_PackageTheftSuspected_BypassesVisibleSubjectGuard()
    {
        var result = new AlertResult
        {
            Camera = "front-door",
            Analysis = new VisionAnalysis
            {
                NotificationRecommended = true,
                PackageTheftSuspected = true,
                Person = new PersonAnalysis { Visible = false },
                Vehicle = new VehicleAnalysis { Visible = false },
                Package = new PackageAnalysis { Visible = false, Status = "none" },
                Animal = new AnimalAnalysis { Visible = false }
            }
        };

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    // Regression: IsReadyAsync used to return true for any 200 from api/tags, so /health/ready
    // (and SelfMonitoringWorker, and the dashboard's system status) reported healthy while the
    // configured model wasn't pulled and every single alert was failing with a 404 from Ollama.
    [Theory]
    [InlineData("gemma3:4b", """{"models":[{"name":"gemma3:4b"}]}""", true)]
    [InlineData("gemma3:4b", """{"models":[{"name":"gemma3:12b"},{"name":"llama3.1:8b"}]}""", false)]
    [InlineData("gemma3:4b", """{"models":[]}""", false)]
    // An untagged configured value means the "latest" tag to Ollama.
    [InlineData("gemma3", """{"models":[{"name":"gemma3:latest"}]}""", true)]
    [InlineData("gemma3:4b", """{"models":[{"name":"GEMMA3:4B"}]}""", true)]
    public async Task OllamaVisionClient_IsReadyAsync_RequiresConfiguredModelToBeInstalled(
        string configuredModel,
        string tagsJson,
        bool expectedReady)
    {
        using var handler = new StubTagsHandler(tagsJson);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var client = new OllamaVisionClient(
            httpClient,
            new StaticOptionsMonitor<OllamaOptions>(new OllamaOptions { Model = configuredModel }),
            new ImageOptimizer(new StaticOptionsMonitor<ImageProcessingOptions>(new ImageProcessingOptions()), NullLogger<ImageOptimizer>.Instance),
            NullLogger<OllamaVisionClient>.Instance);

        Assert.Equal(expectedReady, await client.IsReadyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OllamaVisionClient_IsReadyAsync_FalseWhenOllamaIsUnreachable()
    {
        using var handler = new StubTagsHandler("") { Throw = new HttpRequestException("connection refused") };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var client = new OllamaVisionClient(
            httpClient,
            new StaticOptionsMonitor<OllamaOptions>(new OllamaOptions { Model = "gemma3:4b" }),
            new ImageOptimizer(new StaticOptionsMonitor<ImageProcessingOptions>(new ImageProcessingOptions()), NullLogger<ImageOptimizer>.Instance),
            NullLogger<OllamaVisionClient>.Instance);

        Assert.False(await client.IsReadyAsync(TestContext.Current.CancellationToken));
    }

    private sealed class StubTagsHandler(string tagsJson) : HttpMessageHandler
    {
        public Exception? Throw { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(tagsJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
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
    public void OllamaVisionClient_BuildPrompt_OnlyIncludesTemporalConsistencyNoticeWhenExtraFramesPresent()
    {
        var profile = new PromptProfile { SystemPrompt = "Test.", Questions = ["Is a vehicle visible?"] };
        var occurredAt = DateTimeOffset.UtcNow;
        var singleFrameAlert = new IncomingAlert(
            Guid.NewGuid(), "gateCamera", null, occurredAt, occurredAt,
            "gate", "image/jpeg", "alert.jpg", []);

        var singleFramePrompt = OllamaVisionClient.BuildPrompt(singleFrameAlert, profile);
        Assert.DoesNotContain("frames from this alert", singleFramePrompt);
        Assert.Contains("Analyze the attached image using", singleFramePrompt);

        // Blue Iris alerts never populate ExtraFrames (see IncomingAlert), so this - the vast
        // majority of alerts - must be unaffected by this feature existing at all.
        var otherSingleFrameAlert = new IncomingAlert(
            Guid.NewGuid(), "gateCamera", null, occurredAt, occurredAt,
            "gate", "image/jpeg", "alert.jpg", []);
        Assert.Equal(singleFramePrompt, OllamaVisionClient.BuildPrompt(otherSingleFrameAlert, profile));

        var burstAlert = singleFrameAlert with { ExtraFrames = [[1, 2, 3], [4, 5, 6]] };
        var burstPrompt = OllamaVisionClient.BuildPrompt(burstAlert, profile);
        Assert.Contains("You are being shown 3 frames from this alert", burstPrompt);
        Assert.Contains("Analyze the attached images together using", burstPrompt);
        Assert.DoesNotContain("Analyze the attached image using", burstPrompt);
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
        var request = AnthropicVisionClient.BuildRequest("claude-3-5-sonnet-latest", profile, "prompt text", [[1, 2, 3]]);
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
            "SentinelCore.Web", "SentinelCore.Web.TriggerZoneMonitor", "Error",
            "Failed to save trigger snapshot", "System.IO.IOException: disk full",
            new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.FromHours(-5)));

        using var message = BugReportEmail.Build(options, report);

        Assert.Equal("nathan@nr5p.com", Assert.Single(message.To).Address);
        Assert.Equal("a@b.com", message.From!.Address);
        Assert.Contains("SentinelCore.Web", message.Subject);
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

        var ourLogger = provider.CreateLogger("SentinelCore.Web.TriggerZoneMonitor");
        var frameworkLogger = provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel");

        ourLogger.LogInformation("routine info, should not report");
        ourLogger.LogWarning("a warning, should not report");
        frameworkLogger.LogError(new InvalidOperationException("framework noise"), "framework error, should not report");
        ourLogger.LogError(new InvalidOperationException("boom"), "our real error, should report");

        await sender.WaitForCallAsync(TestContext.Current.CancellationToken);

        var report = Assert.Single(sender.Reports);
        Assert.Equal("TestProcess", report.Process);
        Assert.Equal("SentinelCore.Web.TriggerZoneMonitor", report.Category);
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

        public Task<IReadOnlyList<AlertEventRecord>> SearchBySimilarityAsync(EventFilter filter, float[] queryEmbedding, double minimumSimilarity, CancellationToken cancellationToken) =>
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
    public async Task SqliteAccountStore_LastAdminGuard_PreventsDeletingOrDemotingTheLastAdmin()
    {
        using var store = new SqliteAccountStore(Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}"));
        var solo = await store.CreateAsync("solo-admin", "hash", AccountRole.Admin, null, CancellationToken.None);

        Assert.False(await store.UpdateRoleAsync(solo.Id, AccountRole.Viewer, CancellationToken.None));
        Assert.False(await store.DeleteAsync(solo.Id, CancellationToken.None));

        // A second admin makes both operations safe again - the guard is about "zero admins
        // left", not about protecting any particular account.
        var second = await store.CreateAsync("second-admin", "hash", AccountRole.Admin, null, CancellationToken.None);
        Assert.True(await store.UpdateRoleAsync(solo.Id, AccountRole.Viewer, CancellationToken.None));

        // Now `second` is the only admin again - deleting it should be refused just like before.
        Assert.False(await store.DeleteAsync(second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SqliteAccountStore_Sessions_RoundTripReflectsCurrentAccountRole_NotWhatWasTrueAtSignIn()
    {
        using var store = new SqliteAccountStore(Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}"));
        var account = await store.CreateAsync("viewer-turned-admin", "hash", AccountRole.Viewer, null, CancellationToken.None);

        var ticket = new AuthenticationTicket(
            AccountPrincipal.Create(account),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var token = await store.StoreAsync(ticket);

        var retrieved = await store.RetrieveAsync(token);
        Assert.Equal(nameof(AccountRole.Viewer), retrieved?.Principal.FindFirstValue(ClaimTypes.Role));

        // A role change takes effect on the very next retrieval - RetrieveAsync re-derives the
        // principal from the live account, it doesn't trust whatever was baked in at sign-in.
        await store.UpdateRoleAsync(account.Id, AccountRole.Admin, CancellationToken.None);
        var afterPromotion = await store.RetrieveAsync(token);
        Assert.Equal(nameof(AccountRole.Admin), afterPromotion?.Principal.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task SqliteAccountStore_Sessions_RetrieveAsync_ReturnsNullForUnknownOrRemovedToken()
    {
        using var store = new SqliteAccountStore(Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}"));
        var account = await store.CreateAsync("someone", "hash", AccountRole.Viewer, null, CancellationToken.None);
        var ticket = new AuthenticationTicket(
            AccountPrincipal.Create(account),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) },
            CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Null(await store.RetrieveAsync("this-token-was-never-issued"));

        var token = await store.StoreAsync(ticket);
        await store.RemoveAsync(token);

        Assert.Null(await store.RetrieveAsync(token));
    }

    [Fact]
    public async Task SqliteAccountStore_InvalidateForAccountAsync_LeavesOtherAccountsSessionsAlone()
    {
        using var store = new SqliteAccountStore(Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}"));
        var alice = await store.CreateAsync("alice", "hash", AccountRole.Admin, null, CancellationToken.None);
        var bob = await store.CreateAsync("bob", "hash", AccountRole.Viewer, null, CancellationToken.None);

        var aliceToken = await store.StoreAsync(new AuthenticationTicket(
            AccountPrincipal.Create(alice),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) },
            CookieAuthenticationDefaults.AuthenticationScheme));
        var bobToken = await store.StoreAsync(new AuthenticationTicket(
            AccountPrincipal.Create(bob),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) },
            CookieAuthenticationDefaults.AuthenticationScheme));

        await store.InvalidateForAccountAsync(alice.Id, CancellationToken.None);

        Assert.Null(await store.RetrieveAsync(aliceToken));
        Assert.NotNull(await store.RetrieveAsync(bobToken));

        await store.InvalidateAllAsync(CancellationToken.None);
        Assert.Null(await store.RetrieveAsync(bobToken));
    }

    [Fact]
    public async Task AccountMigration_LegacyAuthEnabled_CreatesFirstAdminFromExistingCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "appsettings.json");
        var legacyHash = PasswordHasher.Hash("the-old-shared-password");
        await File.WriteAllTextAsync(settingsPath, $$"""
            { "Auth": { "Enabled": true, "Username": "household", "PasswordHash": "{{legacyHash}}" } }
            """, ct);
        var settings = new AppSettingsFile(settingsPath);
        using var accounts = new SqliteAccountStore(directory);

        await AccountMigration.MigrateLegacyAuthIfNeededAsync(accounts, settings, ct);

        var all = await accounts.GetAllAsync(ct);
        var migrated = Assert.Single(all);
        Assert.Equal("household", migrated.Username);
        Assert.Equal(AccountRole.Admin, migrated.Role);
        // Copied as-is, not re-hashed - the old hash must still verify against the original password.
        Assert.True(PasswordHasher.Verify("the-old-shared-password", migrated.PasswordHash));
    }

    [Fact]
    public async Task AccountMigration_LegacyAuthDisabled_DoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "appsettings.json");
        await File.WriteAllTextAsync(settingsPath, """{ "Auth": { "Enabled": false } }""", ct);
        var settings = new AppSettingsFile(settingsPath);
        using var accounts = new SqliteAccountStore(directory);

        await AccountMigration.MigrateLegacyAuthIfNeededAsync(accounts, settings, ct);

        Assert.False(await accounts.AnyAccountsExistAsync(ct));
    }

    [Fact]
    public async Task AccountMigration_AccountsAlreadyExist_IsANoOpEvenIfLegacyAuthWasEnabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "appsettings.json");
        await File.WriteAllTextAsync(settingsPath, $$"""
            { "Auth": { "Enabled": true, "Username": "household", "PasswordHash": "{{PasswordHasher.Hash("whatever")}}" } }
            """, ct);
        var settings = new AppSettingsFile(settingsPath);
        using var accounts = new SqliteAccountStore(directory);
        await accounts.CreateAsync("already-here", "hash", AccountRole.Admin, null, ct);

        await AccountMigration.MigrateLegacyAuthIfNeededAsync(accounts, settings, ct);

        var all = await accounts.GetAllAsync(ct);
        var only = Assert.Single(all);
        Assert.Equal("already-here", only.Username);
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

        public Task<IReadOnlyList<AlertEventRecord>> SearchBySimilarityAsync(EventFilter filter, float[] queryEmbedding, double minimumSimilarity, CancellationToken cancellationToken) =>
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
        var original = new Camera(id, "Front Gate", "rtsp://example/1", null, null, false, false, null, null, DateTimeOffset.UtcNow);

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
    public async Task SqliteTriggerStore_GetEventsInRangeAsync_FiltersByCameraAndOccurredAtWindow()
    {
        using var store = new SqliteTriggerStore(Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}"));
        var cameraId = Guid.NewGuid();
        var trigger = await store.AddAsync(cameraId, "image.jpg", 0.1, null, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        var matched = await store.GetEventsInRangeAsync(
            cameraId, now.AddMinutes(-5), now.AddMinutes(5), CancellationToken.None);
        var beforeWindow = await store.GetEventsInRangeAsync(
            cameraId, now.AddHours(-2), now.AddHours(-1), CancellationToken.None);
        var otherCamera = await store.GetEventsInRangeAsync(
            Guid.NewGuid(), now.AddMinutes(-5), now.AddMinutes(5), CancellationToken.None);

        Assert.Single(matched);
        Assert.Equal(trigger.Id, matched[0].Id);
        Assert.Empty(beforeWindow);
        Assert.Empty(otherCamera);
    }

    // --- Timeline trigger-clip correlation (TimelineEndpoints.MatchTriggerEvents) -------------
    //
    // The AI event and its local trigger row are written a few statements apart from the same
    // snapshot (TriggerZoneMonitor.MaybeTriggerAsync), so their timestamps are always close -
    // this matches nearest-timestamp within a tight tolerance, not two independently-clocked
    // systems.

    private static AlertEventRecord MakeAlertEvent(DateTimeOffset occurredAt) =>
        new(Guid.NewGuid(), "GateCam", occurredAt, "Completed", "vehicle", "A car pulled in.", true, null, 0.9, null);

    private static TriggerEvent MakeTriggerEvent(DateTimeOffset occurredAt, string? clipPath) =>
        new(Guid.NewGuid(), Guid.NewGuid(), occurredAt, "image.jpg", 0.12, clipPath);

    [Fact]
    public void MatchTriggerEvents_PicksNearestTriggerWithAClipWithinTolerance()
    {
        var now = DateTimeOffset.UtcNow;
        var alertEvent = MakeAlertEvent(now);
        // Closest in time but has no clip yet (post-roll hasn't finished) - must be skipped in
        // favor of the next-nearest one that actually has a clip.
        var noClipYet = MakeTriggerEvent(now.AddSeconds(0.2), null);
        var withClip = MakeTriggerEvent(now.AddSeconds(1), "clip.mp4");
        var tooFarWithClip = MakeTriggerEvent(now.AddSeconds(30), "far-clip.mp4");

        var matches = TimelineEndpoints.MatchTriggerEvents(
            [alertEvent], [noClipYet, withClip, tooFarWithClip]);

        Assert.Single(matches);
        Assert.Equal(withClip.Id, matches[alertEvent.Id].Id);
    }

    [Fact]
    public void MatchTriggerEvents_NoMatchWhenNearestClipExceedsTolerance()
    {
        var now = DateTimeOffset.UtcNow;
        var alertEvent = MakeAlertEvent(now);
        var farTrigger = MakeTriggerEvent(now.AddSeconds(10), "clip.mp4");

        var matches = TimelineEndpoints.MatchTriggerEvents([alertEvent], [farTrigger]);

        Assert.Empty(matches);
    }

    [Fact]
    public void MatchTriggerEvents_MatchesAtExactlyFiveSecondTolerance()
    {
        var now = DateTimeOffset.UtcNow;
        var alertEvent = MakeAlertEvent(now);
        var boundaryTrigger = MakeTriggerEvent(now.AddSeconds(5), "clip.mp4");

        var matches = TimelineEndpoints.MatchTriggerEvents([alertEvent], [boundaryTrigger]);

        Assert.Single(matches);
        Assert.Equal(boundaryTrigger.Id, matches[alertEvent.Id].Id);
    }

    [Fact]
    public void HttpsSettings_ExplicitlyDisabled_StaysOffSilently()
    {
        // Https:Enabled: false is a deliberate user choice, not a fault - must not warn.
        var settings = new HttpsSettings(Enabled: false, null, null, 3443);

        Assert.False(settings.CanEnable(out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void HttpsSettings_EnabledButNoCertificateAvailable_StaysOffWithAReason()
    {
        // Shouldn't normally happen in production - Program.cs auto-provisions a certificate
        // whenever Enabled is true (the shipped default) - but if that provisioning itself
        // failed, this must not crash the dashboard, just explain why HTTPS didn't come up.
        var settings = new HttpsSettings(Enabled: true, null, null, 3443);

        Assert.False(settings.CanEnable(out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void HttpsSettings_ConfiguredButFileMissing_StaysOffWithAReason()
    {
        // The important case: a wrong path must not stop the dashboard booting, which is what
        // Kestrel's own certificate configuration would do.
        var settings = new HttpsSettings(true, "/nonexistent/server.crt", "/nonexistent/server.key", 3443);

        Assert.False(settings.CanEnable(out var reason));
        Assert.Contains("not found", reason);
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

        var settings = new HttpsSettings(true, certificate, key, 3443);

        Assert.True(settings.CanEnable(out var reason));
        Assert.Null(reason);
        Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CHANGE-ME-this-is-a-placeholder")]
    public void AutoApiKeyProvisioner_MissingOrPlaceholder_GeneratesAndPersistsAKey(string configured)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"apikey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(settingsPath, """{ "Bridge": { "ApiKey": "will-be-replaced" } }""");
        try
        {
            var generated = AutoApiKeyProvisioner.EnsureApiKey(settingsPath, configured);

            Assert.True(generated.Length >= 16);
            Assert.DoesNotContain("CHANGE-ME", generated, StringComparison.OrdinalIgnoreCase);

            var persisted = JsonNode.Parse(File.ReadAllText(settingsPath))!["Bridge"]!["ApiKey"]!.GetValue<string>();
            Assert.Equal(generated, persisted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AutoApiKeyProvisioner_AlreadyConfigured_ReturnsItUnchangedAndLeavesFileAlone()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"apikey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "appsettings.json");
        const string original = """{ "Bridge": { "ApiKey": "a-real-configured-key-1234567890" } }""";
        File.WriteAllText(settingsPath, original);
        try
        {
            var result = AutoApiKeyProvisioner.EnsureApiKey(settingsPath, "a-real-configured-key-1234567890");

            Assert.Equal("a-real-configured-key-1234567890", result);
            Assert.Equal(original, File.ReadAllText(settingsPath)); // untouched, not even reformatted
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AutoCertificateProvisioner_FirstRun_CreatesAUsableCertificateAndAuthority()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"autocert-{Guid.NewGuid():N}");
        try
        {
            var result = AutoCertificateProvisioner.EnsureCertificate(directory);

            Assert.True(result.Reissued);
            Assert.True(File.Exists(result.CertificatePath));
            Assert.True(File.Exists(result.KeyPath));
            Assert.True(File.Exists(result.AuthorityCertificatePath));

            var settings = new HttpsSettings(true, result.CertificatePath, result.KeyPath, 3443);
            Assert.True(settings.CanEnable(out var reason));
            Assert.Null(reason);

            using var certificate = settings.LoadCertificate();
            Assert.True(certificate.NotAfter > DateTime.UtcNow.AddDays(1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AutoCertificateProvisioner_SecondRun_ReusesTheSameAuthorityAndSkipsReissue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"autocert-{Guid.NewGuid():N}");
        try
        {
            var first = AutoCertificateProvisioner.EnsureCertificate(directory);
            var authorityBefore = File.ReadAllText(first.AuthorityCertificatePath);
            var serverCertBefore = File.ReadAllText(first.CertificatePath);

            // Re-running is what happens on every restart - devices that already trust the
            // authority must stay trusted, and a certificate nowhere near expiry shouldn't churn.
            var second = AutoCertificateProvisioner.EnsureCertificate(directory);

            Assert.False(second.Reissued);
            Assert.Equal(authorityBefore, File.ReadAllText(second.AuthorityCertificatePath));
            Assert.Equal(serverCertBefore, File.ReadAllText(second.CertificatePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    public async Task PackageWatchStore_StartsExtendsAndResolvesWatches()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions
            {
                ApiKey = "test-key-1234567890",
                DataDirectory = tempDir
            });
            var store = new SqlitePackageWatchStore(options, new FakeHostEnvironment(tempDir));

            Assert.Null(await store.GetActiveAsync("front-door", CancellationToken.None));

            var deliveredAt = DateTimeOffset.UtcNow;
            var started = await store.StartOrExtendAsync("front-door", deliveredAt, TimeSpan.FromHours(12), CancellationToken.None);
            Assert.Equal(PackageWatchStatus.Watching, started.Status);

            // A second delivery-status alert on the same camera extends the same watch rather
            // than creating a second open one.
            var extended = await store.StartOrExtendAsync("front-door", deliveredAt.AddMinutes(5), TimeSpan.FromHours(12), CancellationToken.None);
            Assert.Equal(started.Id, extended.Id);

            var active = await store.GetActiveAsync("front-door", CancellationToken.None);
            Assert.NotNull(active);
            Assert.Equal(started.Id, active!.Id);

            await store.SetStatusAsync(started.Id, PackageWatchStatus.TheftSuspected, CancellationToken.None);

            // A resolved watch is no longer "active" - a later alert on this camera must not
            // resolve against a watch that's already been closed out.
            Assert.Null(await store.GetActiveAsync("front-door", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EventStore_SearchBySimilarity_RanksByCosineSimilarityAndAppliesThreshold()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions
            {
                ApiKey = "test-key-1234567890",
                DataDirectory = tempDir
            });
            using var store = new SqliteEventStore(options, new FakeHostEnvironment(tempDir));

            var carResult = new AlertResult
            {
                Id = Guid.NewGuid(),
                Camera = "driveway",
                OccurredAt = DateTimeOffset.UtcNow,
                Status = "completed",
                Analysis = new VisionAnalysis { EventType = "vehicle", Summary = "a car" },
                Embedding = [1f, 0f, 0f, 0f]
            };
            var catResult = new AlertResult
            {
                Id = Guid.NewGuid(),
                Camera = "backyard",
                OccurredAt = DateTimeOffset.UtcNow,
                Status = "completed",
                Analysis = new VisionAnalysis { EventType = "animal", Summary = "a cat" },
                Embedding = [0f, 1f, 0f, 0f]
            };
            var unembeddedResult = new AlertResult
            {
                Id = Guid.NewGuid(),
                Camera = "driveway",
                OccurredAt = DateTimeOffset.UtcNow,
                Status = "completed",
                Analysis = new VisionAnalysis { EventType = "vehicle", Summary = "a truck, embedded before the feature was on" }
            };

            await store.RecordAsync(carResult, CancellationToken.None);
            await store.RecordAsync(catResult, CancellationToken.None);
            await store.RecordAsync(unembeddedResult, CancellationToken.None);

            // Close to the car's embedding, far from the cat's - a stand-in for "car"'s CLIP text
            // encoding scoring high against the car photo and low against the cat photo.
            var queryEmbedding = new[] { 0.9f, 0.1f, 0f, 0f };
            var results = await store.SearchBySimilarityAsync(
                new EventFilter(Limit: 10), queryEmbedding, minimumSimilarity: 0.5, CancellationToken.None);

            var resultIds = results.Select(r => r.Id).ToList();
            Assert.Contains(carResult.Id, resultIds);
            // Below the 0.5 threshold and never embedded at all - neither belongs in the results.
            Assert.DoesNotContain(catResult.Id, resultIds);
            Assert.DoesNotContain(unembeddedResult.Id, resultIds);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
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
            var tvDevice = await store.UpsertAsync("apple", "apns-789", "Living room Apple TV", CancellationToken.None, appVariant: "tvos");

            Assert.Equal(first.Id, updated.Id);
            Assert.Equal("android", updated.Platform);
            Assert.Equal("Updated name", updated.DeviceName);
            // Untouched callers never mention AppVariant - it must still default to "ios" rather
            // than come back null/empty, since PushNotificationDispatcher's topic lookup assumes
            // a value is always present.
            Assert.Equal("ios", updated.AppVariant);
            Assert.Equal("tvos", tvDevice.AppVariant);
            Assert.Equal(3, await store.CountAsync(CancellationToken.None));

            await store.RemoveAsync("ANDROID", "fid-123", CancellationToken.None);
            var remaining = await store.GetAllAsync(CancellationToken.None);
            Assert.Equal(2, remaining.Count);
            Assert.Contains(remaining, d => d.Platform == "apple" && d.AppVariant == "ios");
            Assert.Contains(remaining, d => d.Platform == "apple" && d.AppVariant == "tvos");
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

    [Theory]
    [InlineData("ios", "com.sentinelcore.client", "com.sentinelcore.client.tv", "com.sentinelcore.client")]
    [InlineData("tvos", "com.sentinelcore.client", "com.sentinelcore.client.tv", "com.sentinelcore.client.tv")]
    [InlineData("TVOS", "com.sentinelcore.client", "com.sentinelcore.client.tv", "com.sentinelcore.client.tv")]
    // A tvOS device on a Core install that hasn't set TvBundleId yet must still get a push
    // (even if APNs ultimately rejects the wrong topic) rather than the app silently dropping it.
    [InlineData("tvos", "com.sentinelcore.client", "", "com.sentinelcore.client")]
    public void ApplePushOptions_ResolveApnsTopic_PicksBundleIdByAppVariant(
        string appVariant, string bundleId, string tvBundleId, string expectedTopic)
    {
        var options = new ApplePushOptions { BundleId = bundleId, TvBundleId = tvBundleId };

        Assert.Equal(expectedTopic, options.ResolveApnsTopic(appVariant));
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

    // --- Outage alerts (StartupNotificationWorker) -------------------------------------------
    //
    // The decision is a pure function precisely so these can cover every branch without a
    // filesystem, a clock, or an SMTP server.

    private static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    [Fact]
    public void StartupDiagnosis_WithNoPreviousHeartbeat_ReportsNothing()
    {
        // Fresh install. Greeting a new customer with a fault report would be a bad first run.
        var diagnosis = StartupDiagnosis.Evaluate(null, DateTimeOffset.UtcNow, TwoMinutes);

        Assert.Equal(OutageKind.None, diagnosis.Kind);
        Assert.False(diagnosis.ShouldNotify);
    }

    [Fact]
    public void StartupDiagnosis_WhenRestartIsQuick_ReportsNothing()
    {
        // The case that matters most for noise: this app is deployed by restarting in place, so
        // without the threshold every deploy would email.
        var now = DateTimeOffset.UtcNow;
        var previous = new HeartbeatState(now.AddSeconds(-8), now.AddHours(-3), CleanShutdown: true);

        var diagnosis = StartupDiagnosis.Evaluate(previous, now, TwoMinutes);

        Assert.Equal(OutageKind.None, diagnosis.Kind);
    }

    [Fact]
    public void StartupDiagnosis_WhenDownLongWithoutCleanShutdown_ReportsUnexpected()
    {
        // The power-cut case this feature exists for.
        var now = DateTimeOffset.UtcNow;
        var lastSeen = now.AddHours(-3);
        var previous = new HeartbeatState(lastSeen, now.AddHours(-9), CleanShutdown: false);

        var diagnosis = StartupDiagnosis.Evaluate(previous, now, TwoMinutes);

        Assert.Equal(OutageKind.Unexpected, diagnosis.Kind);
        Assert.True(diagnosis.ShouldNotify);
        Assert.Equal(lastSeen, diagnosis.DownSinceUtc);
        Assert.Equal(3, diagnosis.Downtime.TotalHours, precision: 2);
    }

    [Fact]
    public void StartupDiagnosis_WhenDownLongAfterCleanShutdown_ReportsClean()
    {
        // Still worth an email - the cameras weren't being watched either way - but it must not
        // be described as a power cut when somebody stopped it deliberately.
        var now = DateTimeOffset.UtcNow;
        var previous = new HeartbeatState(now.AddHours(-1), now.AddHours(-5), CleanShutdown: true);

        var diagnosis = StartupDiagnosis.Evaluate(previous, now, TwoMinutes);

        Assert.Equal(OutageKind.Clean, diagnosis.Kind);
        Assert.True(diagnosis.ShouldNotify);
    }

    [Fact]
    public void StartupDiagnosis_WhenClockMovedBackwards_ReportsNothing()
    {
        // A flat RTC battery makes a machine boot with a wrong clock until NTP syncs, and losing
        // power is exactly when that surfaces. A backwards clock yields a negative gap, which is
        // below any threshold and so reports nothing.
        var now = DateTimeOffset.UtcNow;
        var previous = new HeartbeatState(now.AddHours(2), now.AddHours(1), CleanShutdown: false);

        var diagnosis = StartupDiagnosis.Evaluate(previous, now, TwoMinutes);

        Assert.Equal(OutageKind.None, diagnosis.Kind);
    }

    [Fact]
    public void StartupDiagnosis_WhenClockJumpedFarForward_ReportsNothing()
    {
        // The dangerous direction. Without the sanity bound this sends a confident email saying
        // the server was down for years, which reads as a bug rather than an outage.
        var now = DateTimeOffset.UtcNow;
        var previous = new HeartbeatState(now.AddDays(-500), now.AddDays(-500), CleanShutdown: false);

        var diagnosis = StartupDiagnosis.Evaluate(previous, now, TwoMinutes);

        Assert.Equal(OutageKind.None, diagnosis.Kind);
    }

    [Fact]
    public void StartupDiagnosis_WhenDownForWeeks_StillReports()
    {
        // The sanity bound must not swallow a long but entirely believable outage - a holiday
        // home, or a box that sat unplugged for a month.
        var now = DateTimeOffset.UtcNow;
        var previous = new HeartbeatState(now.AddDays(-30), now.AddDays(-31), CleanShutdown: false);

        var diagnosis = StartupDiagnosis.Evaluate(previous, now, TwoMinutes);

        Assert.Equal(OutageKind.Unexpected, diagnosis.Kind);
    }

    [Fact]
    public void StartupDiagnosis_AtExactlyTheThreshold_Notifies()
    {
        // Pins the boundary so the comparison can't be loosened to > without a test noticing.
        var now = DateTimeOffset.UtcNow;
        var previous = new HeartbeatState(now - TwoMinutes, now.AddHours(-1), CleanShutdown: false);

        var diagnosis = StartupDiagnosis.Evaluate(previous, now, TwoMinutes);

        Assert.Equal(OutageKind.Unexpected, diagnosis.Kind);
    }

    [Fact]
    public async Task StartupNotificationWorker_WritesAndReadsBackHeartbeat()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var worker = BuildStartupWorker(directory);
            await worker.StartAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);

            var path = Path.Combine(directory, "heartbeat.json");
            Assert.True(File.Exists(path));

            var state = JsonSerializer.Deserialize<HeartbeatState>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(state);
            // Stopping through the host is the only path that sets this, which is what makes it a
            // usable signal for "was this shutdown deliberate".
            Assert.True(state!.CleanShutdown);

            // Nothing half-written left behind for the next start to trip over.
            Assert.Empty(Directory.GetFiles(directory, "heartbeat.json*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartupNotificationWorker_WithCorruptHeartbeat_StartsWithoutThrowing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "heartbeat.json"), "{ this is not json");

        try
        {
            var worker = BuildStartupWorker(directory);

            // A truncated file is a plausible outcome of the very power cut being detected. It
            // must degrade to "no outage report", never to a crash loop on startup.
            await worker.StartAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);

            var state = JsonSerializer.Deserialize<HeartbeatState>(
                File.ReadAllText(Path.Combine(directory, "heartbeat.json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(state);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OutageEmail_GoesToTheAlertAddressAndStatesTheDuration()
    {
        var email = new EmailOptions
        {
            Enabled = true,
            Username = "sender@example.com",
            AppPassword = "app-password",
            ToAddress = "owner@example.com",
            FromName = "Gate Camera"
        };
        var diagnosis = new StartupDiagnosis(OutageKind.Unexpected, TimeSpan.FromHours(3), DateTimeOffset.UtcNow.AddHours(-3));

        using var message = OutageEmail.Build(email, diagnosis);

        // The whole point is that it lands in the same inbox as the alerts.
        Assert.Equal("owner@example.com", Assert.Single(message.To).Address);
        Assert.Contains("3 hours", message.Body);
        Assert.Contains("3 hours", message.Subject);
        // An unexpected outage must not be described as a planned stop.
        Assert.Contains("power cut", message.Body);
        Assert.DoesNotContain("planned", message.Subject);
    }

    [Theory]
    [InlineData(45, "45 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(120, "2 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(5400, "1 hour 30 minutes")]
    [InlineData(86400, "1 day")]
    [InlineData(180000, "2 days 2 hours")]
    public void OutageEmail_DescribesDurationsReadably(int seconds, string expected) =>
        Assert.Equal(expected, OutageEmail.Describe(TimeSpan.FromSeconds(seconds)));

    private static StartupNotificationWorker BuildStartupWorker(string dataDirectory) =>
        new(new StaticOptionsMonitor<StartupNotificationOptions>(new StartupNotificationOptions()),
            // Deliberately unconfigured: these tests exercise the heartbeat and the decision, and
            // must never attempt a real SMTP connection.
            new StaticOptionsMonitor<EmailOptions>(new EmailOptions()),
            new StaticOptionsMonitor<BridgeOptions>(new BridgeOptions { DataDirectory = dataDirectory }),
            new FakeHostEnvironment(dataDirectory),
            NullLogger<StartupNotificationWorker>.Instance);

    private static HomeKitAlertAction BuildHomeKitAction(
        RecordingHandler handler, HomeKitOptions options, QuietHoursOptions? quietHours = null) =>
        new(new HttpClient(handler),
            new StaticOptionsMonitor<HomeKitOptions>(options),
            new StaticOptionsMonitor<QuietHoursOptions>(quietHours ?? new QuietHoursOptions()),
            NullLogger<HomeKitAlertAction>.Instance);

    private static DiscordAlertAction BuildDiscordAction(RecordingHandler handler, DiscordOptions options) =>
        new(new HttpClient(handler),
            new StaticOptionsMonitor<DiscordOptions>(options),
            new StaticOptionsMonitor<QuietHoursOptions>(new QuietHoursOptions()),
            NullLogger<DiscordAlertAction>.Instance);

    // --- Phantom alerts: nothing visible must never notify ------------------------------------
    //
    // Real events on this system reported "a person is present on the outside side of the gate"
    // with person.visible false and every other subject absent - the frame was a spiderweb across
    // the lens lit by the IR illuminator. These pin the guard that stops that reaching a phone.

    private static AlertResult NothingVisible(string gateState, string profile = "gate") => new()
    {
        Camera = "gate camera front door",
        Profile = profile,
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Confidence = 0.3,
            EventType = "none",
            Gate = new GateAnalysis { State = gateState },
            // Exactly what the model returned on the real 5:35am events: the positional field is
            // filled in even though nothing is visible.
            Person = new PersonAnalysis { Visible = false, Location = "outside_gate" },
            Summary = "A person is present on the outside side of the gate."
        }
    };

    // A genuinely notifiable event - person actually visible outside the gate - with the
    // confidence and timestamp varied, since AlertResult/VisionAnalysis are classes rather than
    // records and so can't be copied with `with`.
    private static AlertResult VisiblePersonResult(double confidence, DateTimeOffset? occurredAt = null) => new()
    {
        Camera = "gate camera front door",
        Profile = "gate",
        OccurredAt = occurredAt ?? DateTimeOffset.Now,
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Confidence = confidence,
            EventType = "person_at_gate",
            Gate = new GateAnalysis { State = "closed" },
            Person = new PersonAnalysis { Visible = true, Location = "outside_gate", Activity = "standing" },
            Summary = "A person is standing outside the gate."
        }
    };

    [Theory]
    [InlineData("unknown")]   // what an unreadable frame actually returns - the case that leaked
    [InlineData("closed")]
    [InlineData("open")]
    public void ShouldNotify_WhenNothingIsVisible_IsFalseWhateverTheGateState(string gateState) =>
        Assert.False(AlertNotificationPolicy.ShouldNotify(NothingVisible(gateState)));

    [Fact]
    public void ShouldNotify_WhenNothingIsVisible_IsFalseForNonGateProfilesToo()
    {
        // The old suppression only ran for the gate profile, so every other camera had no
        // protection at all. The guard is deliberately profile-independent.
        Assert.False(AlertNotificationPolicy.ShouldNotify(NothingVisible("unknown", profile: "back-door")));
    }

    [Fact]
    public void ShouldNotify_WhenPersonIsActuallyVisibleOutside_StillNotifies()
    {
        // The guard must not cost a real detection - this is the event you want waking you up.
        var result = NotifiableResult("gate camera front door");
        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ShouldNotify_WhenOnlyAPackageWasDelivered_StillNotifies()
    {
        // A parcel left at the gate with the courier already gone is a real event with no person
        // or vehicle in frame, so package status has to count as a subject in its own right.
        var result = new AlertResult
        {
            Camera = "gate camera front door",
            Profile = "gate",
            Analysis = new VisionAnalysis
            {
                NotificationRecommended = true,
                Confidence = 0.9,
                Gate = new GateAnalysis { State = "closed" },
                Package = new PackageAnalysis { Visible = true, Status = "delivered", Location = "at_gate" },
                Summary = "A package has been left at the gate."
            }
        };

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ShouldNotify_GateCheckProfile_BypassesTheSubjectRequirement()
    {
        // The whole reason GateCheckAnalyzer uses its own reserved profile: a gate left open
        // with nobody around would never satisfy HasVisibleSubject, and that's exactly the event
        // this feature exists to catch.
        var result = NothingVisible("open", profile: GateCheckDecision.ProfileName);
        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ShouldNotify_GateCheckProfile_StillRequiresNotificationRecommended()
    {
        // The reserved-profile bypass only skips the subject check below it - the top-level
        // NotificationRecommended gate still applies, so GateCheckAnalyzer can't accidentally
        // notify by forgetting to set it.
        var notRecommended = new AlertResult
        {
            Camera = "front gate",
            Profile = GateCheckDecision.ProfileName,
            Analysis = new VisionAnalysis { NotificationRecommended = false, Gate = new GateAnalysis { State = "open" } }
        };
        Assert.False(AlertNotificationPolicy.ShouldNotify(notRecommended));
    }

    [Fact]
    public async Task HomeKitAlertAction_BelowTheConfidenceFloor_DoesNotTrigger()
    {
        // The channel that was waking the phone: Email and Telegram floored at 0.6, HomeKit had
        // no floor at all, so a 0.3-confidence reading still rang.
        var handler = new RecordingHandler();
        var result = VisiblePersonResult(confidence: 0.3);

        var action = BuildHomeKitAction(handler, new HomeKitOptions { Enabled = true, MinimumConfidence = 0.6 });
        await action.ExecuteAsync(result, CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HomeKitAlertAction_AboveTheConfidenceFloor_Triggers()
    {
        var handler = new RecordingHandler();
        var action = BuildHomeKitAction(handler, new HomeKitOptions { Enabled = true, MinimumConfidence = 0.6 });

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HomeKitAlertAction_DuringQuietHours_UsesTheRaisedFloor()
    {
        // Quiet hours already raise the bar for Email and Telegram; HomeKit ignored them entirely,
        // which is backwards for the one channel that makes a noise next to your bed.
        var handler = new RecordingHandler();
        var quiet = new QuietHoursOptions { Enabled = true, StartHour = 22, EndHour = 6, MinimumConfidence = 0.95 };

        var atThreeAm = new DateTimeOffset(2026, 7, 27, 3, 0, 0, DateTimeOffset.Now.Offset);
        var result = VisiblePersonResult(confidence: 0.9, occurredAt: atThreeAm);

        var action = BuildHomeKitAction(handler, new HomeKitOptions { Enabled = true, MinimumConfidence = 0.6 }, quiet);
        await action.ExecuteAsync(result, CancellationToken.None);

        // 0.9 clears the normal 0.6 bar but not the 0.95 overnight one.
        Assert.Empty(handler.Requests);
    }

    // The summary and the structured flags have to agree. This fixture used to assert a person in
    // prose while leaving person.visible false - the same contradiction that produced the phantom
    // gate alerts in production - which meant it no longer described a notifiable event once the
    // visibility guard landed. Confidence is set because every channel now has a floor.
    private static AlertResult NotifiableResult(string camera, string? imagePath = null) => new()
    {
        Camera = camera,
        ImagePath = imagePath,
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Confidence = 0.9,
            Person = new PersonAnalysis { Visible = true, Location = "outside_gate", Activity = "standing" },
            Summary = "A person is standing outside the gate."
        }
    };

    // --- Alert emails: each picture appears exactly once ---------------------------------------
    //
    // Every image was being embedded inline (a LinkedResource the HTML references via cid:) and
    // then attached again as a separate file with the same bytes - so an alert with both the
    // snapshot and the analysed-region crop arrived with four pictures, the two real ones each
    // shown twice. Mail clients don't dedupe by content, so nothing hid this from the recipient.

    private static EmailOptions EmailOptionsForTest(bool attachImage) => new()
    {
        AttachImage = attachImage,
        Username = "sender@example.com",
        FromName = "Gate Camera",
        ToAddress = "owner@example.com"
    };

    private static string CreateFakeJpeg(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), $"email-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "snapshot.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        return path;
    }

    [Fact]
    public void EmailAlertAction_BuildMessage_NeverAttachesFiles()
    {
        // The fix in one assertion: LinkedResources display inline via cid: and Attachments show
        // up as separate files a client has to open. There is now exactly one place a picture can
        // appear, so Attachments must always be empty.
        var imagePath = CreateFakeJpeg(out var directory);
        try
        {
            var result = NotifiableResult("gate camera front door", imagePath);
            var notification = new EmailAlertAction.EmailNotification("person", "Person at gate");

            using var message = EmailAlertAction.BuildMessage(
                EmailOptionsForTest(attachImage: true), result, notification, cropOverlay: [1, 2, 3]);

            Assert.Empty(message.Attachments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmailAlertAction_BuildMessage_EmbedsBothPicturesExactlyOnce()
    {
        var imagePath = CreateFakeJpeg(out var directory);
        try
        {
            var result = NotifiableResult("gate camera front door", imagePath);
            var notification = new EmailAlertAction.EmailNotification("person", "Person at gate");

            using var message = EmailAlertAction.BuildMessage(
                EmailOptionsForTest(attachImage: true), result, notification, cropOverlay: [1, 2, 3]);

            var resources = message.AlternateViews.Single().LinkedResources;
            Assert.Equal(2, resources.Count);
            Assert.Contains(resources, r => r.ContentId == "gate-alert-image");
            Assert.Contains(resources, r => r.ContentId == "gate-alert-crop");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmailAlertAction_BuildMessage_WhenAttachImageIsOff_SendsNoPicturesAtAll()
    {
        // The setting is named and described as one on/off switch for whether a picture appears
        // ("Include the snapshot in the email rather than text only"). Previously the inline
        // copies ignored it entirely, so turning it off still sent pictures - just without the
        // second, attached copy.
        var imagePath = CreateFakeJpeg(out var directory);
        try
        {
            var result = NotifiableResult("gate camera front door", imagePath);
            var notification = new EmailAlertAction.EmailNotification("person", "Person at gate");

            using var message = EmailAlertAction.BuildMessage(
                EmailOptionsForTest(attachImage: false), result, notification, cropOverlay: [1, 2, 3]);

            Assert.Empty(message.AlternateViews.Single().LinkedResources);
            Assert.Empty(message.Attachments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

    // --- Camera discovery: WS-UsernameToken digest --------------------------------------------
    //
    // These pin down the byte-level details flagged as the highest-risk part of the whole
    // camera-discovery feature: a digest that's computed slightly wrong still looks like a valid
    // digest (right length, valid base64) and only shows up as every real camera silently
    // rejecting auth. Testable with no camera, no socket, no HTTP - it's a pure function.

    [Fact]
    public void OnvifUsernameToken_ComputeDigest_OfEmptyInputs_MatchesKnownSha1OfEmptyString()
    {
        // SHA1("") = da39a3ee5e6b4b0d3255bfef95601890afd80709 is a widely-known constant. Using
        // an empty nonce, created, and password means the concatenated hash input is also empty,
        // so this pins the digest function against a value that isn't just re-deriving itself
        // with .NET's own SHA1 API under a different name.
        var digest = OnvifUsernameToken.ComputeDigest(nonceBytes: [], created: "", password: "");
        Assert.Equal("DA39A3EE5E6B4B0D3255BFEF95601890AFD80709", Convert.ToHexString(digest));
    }

    [Fact]
    public void OnvifUsernameToken_ComputeDigest_ConcatenatesRawNonceBytesDirectly()
    {
        // An independent reimplementation, not a round-trip through ComputeDigest itself - the
        // expected hash is computed here by hand-concatenating the raw nonce bytes and UTF-8
        // strings and calling SHA1 directly, so this only passes if ComputeDigest's internals
        // genuinely match the spec. The classic implementation bug - hashing the base64 TEXT of
        // the nonce instead of its raw bytes - produces a still-plausible-looking digest (right
        // length, valid base64), so a same-function comparison wouldn't catch it; this would.
        byte[] nonceBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        const string created = "2026-01-01T00:00:00.000Z";
        const string password = "password";

        var expected = SHA1.HashData([
            .. nonceBytes,
            .. System.Text.Encoding.UTF8.GetBytes(created),
            .. System.Text.Encoding.UTF8.GetBytes(password)
        ]);

        Assert.Equal(expected, OnvifUsernameToken.ComputeDigest(nonceBytes, created, password));
    }

    [Fact]
    public void OnvifUsernameToken_ComputeDigest_ConcatenationOrderIsNonceThenCreatedThenPassword()
    {
        byte[] nonce = [1, 2, 3];
        var a = OnvifUsernameToken.ComputeDigest(nonce, "created-value", "password-value");
        var b = OnvifUsernameToken.ComputeDigest(nonce, "password-value", "created-value"); // swapped
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OnvifUsernameToken_Build_NonceInTokenMatchesTheBytesActuallyHashed()
    {
        // Guards against computing the digest from one nonce/created pair and then serializing a
        // DIFFERENT nonce/created into the token that goes out on the wire - the camera would
        // recompute a different digest from what it received and reject every call.
        var token = OnvifUsernameToken.Build("admin", "secret");
        var expectedDigest = OnvifUsernameToken.ComputeDigest(Convert.FromBase64String(token.Nonce), token.Created, "secret");

        Assert.Equal(Convert.ToBase64String(expectedDigest), token.PasswordDigest);
    }

    [Fact]
    public void OnvifUsernameToken_Build_GeneratesADifferentNoncePerCall()
    {
        // Some camera firmware keeps a replay cache and rejects a second SOAP call reusing the
        // same nonce - Build() must be called fresh per call, never memoized/reused.
        var first = OnvifUsernameToken.Build("admin", "secret");
        var second = OnvifUsernameToken.Build("admin", "secret");
        Assert.NotEqual(first.Nonce, second.Nonce);
    }

    // --- Camera discovery: ONVIF SOAP envelope shape -------------------------------------------

    [Fact]
    public void OnvifClient_BuildEnvelope_WithCredentials_IncludesUsernameTokenHeader()
    {
        var envelope = OnvifClient.BuildEnvelope(new System.Xml.Linq.XElement("Probe"), "admin", "secret");

        var ns = envelope.GetDefaultNamespace();
        System.Xml.Linq.XNamespace wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        var usernameToken = envelope.Descendants(wsse + "UsernameToken").SingleOrDefault();

        Assert.NotNull(usernameToken);
        Assert.Equal("admin", usernameToken!.Element(wsse + "Username")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(usernameToken.Element(wsse + "Password")?.Value));
        Assert.False(string.IsNullOrWhiteSpace(usernameToken.Element(wsse + "Nonce")?.Value));
    }

    [Fact]
    public void OnvifClient_BuildEnvelope_WithoutCredentials_OmitsUsernameTokenHeader()
    {
        // Some cameras answer GetDeviceInformation without any auth at all - a security header
        // must not be sent when the caller supplied no username, rather than sending an empty
        // or garbage one.
        var envelope = OnvifClient.BuildEnvelope(new System.Xml.Linq.XElement("Probe"), null, null);

        System.Xml.Linq.XNamespace wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        Assert.Empty(envelope.Descendants(wsse + "UsernameToken"));
    }

    // --- Camera discovery: WS-Discovery probe/parse --------------------------------------------

    [Fact]
    public void WsDiscoveryClient_TryParseProbeMatch_ParsesAGenuineOnvifReply()
    {
        var messageId = "uuid:11111111-1111-1111-1111-111111111111";
        var reply = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
              <e:Header>
                <w:MessageID>uuid:22222222-2222-2222-2222-222222222222</w:MessageID>
                <w:RelatesTo>{{messageId}}</w:RelatesTo>
                <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</w:Action>
              </e:Header>
              <e:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <w:EndpointReference>
                      <w:Address>urn:uuid:9c8f1a2b-0000-0000-0000-000000000001</w:Address>
                    </w:EndpointReference>
                    <d:Types>dn:NetworkVideoTransmitter</d:Types>
                    <d:XAddrs>http://192.168.1.50/onvif/device_service</d:XAddrs>
                    <d:MetadataVersion>1</d:MetadataVersion>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </e:Body>
            </e:Envelope>
            """;

        var parsed = WsDiscoveryClient.TryParseProbeMatch(System.Text.Encoding.UTF8.GetBytes(reply), messageId, out var match);

        Assert.True(parsed);
        Assert.Equal("urn:uuid:9c8f1a2b-0000-0000-0000-000000000001", match!.EndpointAddress);
        Assert.Equal(["http://192.168.1.50/onvif/device_service"], match.XAddrs);
    }

    [Fact]
    public void WsDiscoveryClient_TryParseProbeMatch_RejectsAReplyToADifferentProbe()
    {
        // Multiple WS-Discovery clients can share the multicast group; a reply carrying someone
        // else's MessageID in RelatesTo must be ignored rather than misattributed to this probe.
        var reply = """
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <e:Header><w:RelatesTo>uuid:not-ours</w:RelatesTo></e:Header>
              <e:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <w:EndpointReference><w:Address>urn:uuid:x</w:Address></w:EndpointReference>
                    <d:XAddrs>http://192.168.1.50/onvif/device_service</d:XAddrs>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </e:Body>
            </e:Envelope>
            """;

        var parsed = WsDiscoveryClient.TryParseProbeMatch(
            System.Text.Encoding.UTF8.GetBytes(reply), "uuid:11111111-1111-1111-1111-111111111111", out var match);

        Assert.False(parsed);
        Assert.Null(match);
    }

    [Fact]
    public void WsDiscoveryClient_TryParseProbeMatch_RejectsGarbage()
    {
        var parsed = WsDiscoveryClient.TryParseProbeMatch(
            System.Text.Encoding.UTF8.GetBytes("not xml at all"), "uuid:anything", out var match);

        Assert.False(parsed);
        Assert.Null(match);
    }

    [Fact]
    public void WsDiscoveryClient_BuildProbeMessage_TargetsNetworkVideoTransmitters()
    {
        var probe = WsDiscoveryClient.BuildProbeMessage(out var messageId);
        var text = System.Text.Encoding.UTF8.GetString(probe);

        Assert.Contains(messageId, text);
        Assert.Contains("NetworkVideoTransmitter", text);
        Assert.Contains("http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe", text);
    }

    // --- Camera discovery: ffprobe JSON parsing -------------------------------------------------

    [Fact]
    public void CameraDiscoveryService_ParseFfprobeOutput_ExtractsResolutionFromTheVideoStream()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "audio", "codec_name": "aac" },
                { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 }
              ]
            }
            """;

        var result = CameraDiscoveryService.ParseFfprobeOutput(json);

        Assert.NotNull(result);
        Assert.Equal("h264", result!.Codec);
        Assert.Equal("1920x1080", result.Resolution);
    }

    [Fact]
    public void CameraDiscoveryService_ParseFfprobeOutput_WhenNoVideoStreamPresent_ReturnsNull()
    {
        // An audio-only source (or a URL that answered with something else entirely) is not a
        // usable camera stream even though ffprobe itself succeeded.
        const string json = """{ "streams": [ { "codec_type": "audio", "codec_name": "aac" } ] }""";
        Assert.Null(CameraDiscoveryService.ParseFfprobeOutput(json));
    }

    [Fact]
    public void CameraDiscoveryService_ParseFfprobeOutput_RejectsGarbage()
    {
        // A candidate URL that answers with an HTML login page or a 404 body rather than JSON
        // must degrade to "not found", not throw and take the whole probe down with it.
        Assert.Null(CameraDiscoveryService.ParseFfprobeOutput("<html>not json</html>"));
    }

    // --- Camera discovery: network scan ---------------------------------------------------------

    [Theory]
    [InlineData("docker0", "", true)]
    [InlineData("veth3a1b2c", "", true)]
    [InlineData("", "Hyper-V Virtual Switch", true)]
    [InlineData("wg0", "WireGuard Tunnel", true)] // caught incidentally - "Tunnel" contains the "tun" hint, not because "wg0" itself is recognized
    [InlineData("Ethernet 5", "ASUS USB-AC56 Wireless Adapter", false)] // a name the (necessarily incomplete) hint list won't catch - the point of LooksVirtual being advisory, not a hard filter
    [InlineData("eth0", "Realtek PCIe GbE Family Controller", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX200", false)]
    public void CameraDiscoveryService_IsLikelyVirtualAdapter_MatchesKnownVirtualAdapterNaming(
        string name, string description, bool expected) =>
        Assert.Equal(expected, CameraDiscoveryService.IsLikelyVirtualAdapter(name, description));

    [Fact]
    public async Task CameraDiscoveryService_GetScanStatus_ForAnUnknownJob_ReturnsNull()
    {
        var service = BuildDiscoveryService();
        Assert.Null(service.GetScanStatus(Guid.NewGuid()));
    }

    [Fact]
    public async Task CameraDiscoveryService_StartScan_WithNoInterfaces_CompletesWithNoResults()
    {
        // The one part of the scan pipeline testable without real hardware or a socket: with
        // nothing to probe, WS-Discovery's Task.WhenAll over an empty list resolves immediately,
        // so this also stands as a regression guard that the job reaches Completed at all rather
        // than getting stuck at Running forever.
        var ct = TestContext.Current.CancellationToken;
        var service = BuildDiscoveryService();
        var jobId = service.StartScan([]);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        ScanJobSnapshot? snapshot;
        do
        {
            snapshot = service.GetScanStatus(jobId);
            if (snapshot!.Status == ScanStatus.Completed) break;
            await Task.Delay(20, ct);
        } while (DateTimeOffset.UtcNow < deadline);

        Assert.NotNull(snapshot);
        Assert.Equal(ScanStatus.Completed, snapshot.Status);
        Assert.Empty(snapshot.Results);
    }

    [Fact]
    public void CameraDiscoveryService_GetCandidateInterfaces_DoesNotThrow()
    {
        // A thin sanity check against the real machine's actual network interfaces - .NET gives
        // no supported way to fabricate a NetworkInterface for a real unit test, so this only
        // proves the enumeration/filtering code runs cleanly, not any particular result.
        var interfaces = CameraDiscoveryService.GetCandidateInterfaces();
        Assert.NotNull(interfaces);
    }

    private static CameraDiscoveryService BuildDiscoveryService()
    {
        var onvif = new OnvifClient(new HttpClient());
        var cameraStore = new EmptyCameraStore();
        var configuration = new ConfigurationBuilder().Build();
        return new CameraDiscoveryService(onvif, cameraStore, configuration, NullLogger<CameraDiscoveryService>.Instance);
    }

    private sealed class EmptyCameraStore : ICameraStore
    {
        public Task<IReadOnlyList<Camera>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Camera>>([]);
        public Task<Camera?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Camera?>(null);
        public Task<Camera> AddAsync(
            string name, string streamUrl, string? username, string? password, bool includeAudio,
            bool ptzEnabled, string? onvifPtzServiceUrl, string? onvifProfileToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> UpdateAsync(
            Guid id, string name, string streamUrl, string? username, string? password, bool includeAudio,
            bool ptzEnabled, string? onvifPtzServiceUrl, string? onvifProfileToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreAsync(Camera camera, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // --- Alert queue: clearing a backlog -------------------------------------------------------

    [Fact]
    public async Task AlertQueue_Clear_OnAnEmptyQueue_ReturnsEmpty()
    {
        var queue = new AlertQueue(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", QueueCapacity = 10 }));
        Assert.Empty(queue.Clear());
        Assert.Equal(0, queue.ApproximateCount);
    }

    [Fact]
    public async Task AlertQueue_Clear_DrainsEverythingWaitingAndResetsTheCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var queue = new AlertQueue(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", QueueCapacity = 10 }));

        var alerts = Enumerable.Range(0, 5).Select(i => MakeAlert($"camera-{i}")).ToList();
        foreach (var alert in alerts) Assert.True(await queue.TryEnqueueAsync(alert, ct));
        Assert.Equal(5, queue.ApproximateCount);

        var cleared = queue.Clear();

        Assert.Equal(5, cleared.Count);
        Assert.Equal(alerts.Select(a => a.Id), cleared.Select(a => a.Id));
        Assert.Equal(0, queue.ApproximateCount);
        Assert.Empty(queue.Clear()); // nothing left to drain a second time
    }

    [Fact]
    public async Task AlertQueue_TryEnqueueAsync_WhenFull_ReturnsFalseAndDoesNotCountTheRejectedAlert()
    {
        // Found while writing Clear()'s own tests: the channel was configured with
        // FullMode.DropWrite, under which Writer.TryWrite returns true even when full - the
        // channel "handles" an over-capacity write by silently discarding it rather than
        // rejecting it. That meant a saturated queue kept reporting every alert as successfully
        // queued (Program.cs would return 202 and write a "queued" database row nothing would
        // ever pick up) while _count kept incrementing for alerts that were never actually added -
        // and the "queue is full" 503 branch in Program.cs was effectively unreachable. This
        // pins the fix: TryEnqueueAsync must genuinely fail once the queue is full, and the count
        // must not include what it rejected.
        var ct = TestContext.Current.CancellationToken;
        var queue = new AlertQueue(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", QueueCapacity = 2 }));

        Assert.True(await queue.TryEnqueueAsync(MakeAlert("a"), ct));
        Assert.True(await queue.TryEnqueueAsync(MakeAlert("b"), ct));
        Assert.False(await queue.TryEnqueueAsync(MakeAlert("rejected"), ct));

        Assert.Equal(2, queue.ApproximateCount);
        Assert.Equal(2, queue.Clear().Count); // exactly what was really added, not 3
    }

    [Fact]
    public async Task AlertQueue_Clear_LeavesRoomToEnqueueAgainAfterward()
    {
        // A cleared queue has to be a genuinely usable queue afterward, not a broken/closed
        // channel - this is what makes it safe to clear a backlog while alerts keep arriving.
        var ct = TestContext.Current.CancellationToken;
        var queue = new AlertQueue(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", QueueCapacity = 2 }));

        await queue.TryEnqueueAsync(MakeAlert("a"), ct);
        await queue.TryEnqueueAsync(MakeAlert("b"), ct);
        Assert.False(await queue.TryEnqueueAsync(MakeAlert("c"), ct)); // full

        queue.Clear();

        Assert.True(await queue.TryEnqueueAsync(MakeAlert("d"), ct));
        Assert.Equal(1, queue.ApproximateCount);
    }

    [Fact]
    public async Task AlertQueue_Clear_DoesNotAffectAnAlertAlreadyDequeuedForProcessing()
    {
        // Clear() must only discard what's still waiting - not reach back and un-deliver
        // something a reader already pulled out to analyze. That item is "in progress", not
        // "queued", and finishing it normally is the whole point of leaving it alone.
        var ct = TestContext.Current.CancellationToken;
        var queue = new AlertQueue(Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", QueueCapacity = 10 }));

        await queue.TryEnqueueAsync(MakeAlert("in-progress"), ct);
        await queue.TryEnqueueAsync(MakeAlert("still-waiting"), ct);

        IncomingAlert? dequeued = null;
        await foreach (var item in queue.ReadAllAsync(ct))
        {
            dequeued = item;
            break;
        }

        var cleared = queue.Clear();

        Assert.Equal("in-progress", dequeued!.Camera);
        Assert.Single(cleared);
        Assert.Equal("still-waiting", cleared[0].Camera);
    }

    private static IncomingAlert MakeAlert(string camera) => new(
        Guid.NewGuid(), camera, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "default", "image/jpeg", "shot.jpg", [1, 2, 3], null);

    // --- Gate/door open check ------------------------------------------------------------

    [Fact]
    public async Task SqliteGateCheckStore_SaveAsync_RoundTripsConfigAndUpdateStateAsyncPatchesOnlyStateColumns()
    {
        using var store = new SqliteGateCheckStore(Path.Combine(Path.GetTempPath(), $"gatechk-{Guid.NewGuid():N}"));
        var cameraId = Guid.NewGuid();
        var config = new GateCheckConfig(cameraId, true, "front gate", true, 10, 4, "unknown", null, null, false);

        await store.SaveAsync(config, CancellationToken.None);
        var saved = await store.GetAsync(cameraId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal("front gate", saved!.CheckLabel);
        Assert.Equal(10, saved.IntervalMinutes);
        Assert.Equal(4, saved.RemindIfStillOpenAfterHours);
        Assert.Equal("unknown", saved.LastKnownState);

        var changedAt = DateTimeOffset.UtcNow;
        await store.UpdateStateAsync(cameraId, "open", changedAt, changedAt, true, CancellationToken.None);
        var updated = await store.GetAsync(cameraId, CancellationToken.None);
        Assert.Equal("open", updated!.LastKnownState);
        Assert.True(updated.LastKnownBlocked);
        // The config columns (label, interval, reminder) must survive a state-only patch.
        Assert.Equal("front gate", updated.CheckLabel);
        Assert.Equal(10, updated.IntervalMinutes);
    }

    [Fact]
    public async Task SqliteGateCheckStore_GetEnabledAsync_ExcludesDisabledChecks()
    {
        using var store = new SqliteGateCheckStore(Path.Combine(Path.GetTempPath(), $"gatechk-{Guid.NewGuid():N}"));
        await store.SaveAsync(GateCheckConfig.Disabled(Guid.NewGuid()), CancellationToken.None);
        var enabledId = Guid.NewGuid();
        await store.SaveAsync(GateCheckConfig.Disabled(enabledId) with { Enabled = true }, CancellationToken.None);

        var enabled = await store.GetEnabledAsync(CancellationToken.None);
        var all = await store.GetAllAsync(CancellationToken.None);

        Assert.Single(enabled);
        Assert.Equal(enabledId, enabled[0].CameraId);
        Assert.Equal(2, all.Count);
    }

    [Theory]
    [InlineData("closed", "open", true)]
    [InlineData("unknown", "open", true)]
    [InlineData("closed", "closed", false)]
    [InlineData("open", "closed", false)]
    public void GateCheckDecision_ShouldNotifyOpen_TransitionCases(string previous, string next, bool expected)
    {
        var result = GateCheckDecision.ShouldNotifyOpen(previous, next, null, null, DateTimeOffset.UtcNow);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GateCheckDecision_ShouldNotifyOpen_UnknownReadingNeverNotifies()
    {
        // Neither direction - a single unreadable frame shouldn't fire on its own, whether the
        // gate was previously open or closed.
        Assert.False(GateCheckDecision.ShouldNotifyOpen("open", "unknown", null, null, DateTimeOffset.UtcNow));
        Assert.False(GateCheckDecision.ShouldNotifyOpen("closed", "unknown", null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GateCheckDecision_ShouldNotifyOpen_StaysOpenWithoutReminderConfigured_DoesNotRenotify()
    {
        var lastNotified = DateTimeOffset.UtcNow.AddDays(-2);
        var result = GateCheckDecision.ShouldNotifyOpen("open", "open", lastNotified, null, DateTimeOffset.UtcNow);
        Assert.False(result);
    }

    [Fact]
    public void GateCheckDecision_ShouldNotifyOpen_StaysOpenWithReminderConfigured_RenotifiesOnlyOnceElapsed()
    {
        var now = DateTimeOffset.UtcNow;

        var tooSoon = GateCheckDecision.ShouldNotifyOpen("open", "open", now.AddHours(-2), 4, now);
        var elapsed = GateCheckDecision.ShouldNotifyOpen("open", "open", now.AddHours(-5), 4, now);

        Assert.False(tooSoon);
        Assert.True(elapsed);
    }

    [Fact]
    public void GateCheckDecision_ShouldNotifyOpen_StaysOpenWithReminderConfiguredButNeverNotified_Renotifies()
    {
        // Defensive: if a reminder is configured but LastNotifiedAt is somehow missing, don't
        // silently stay quiet forever - treat it as due.
        var result = GateCheckDecision.ShouldNotifyOpen("open", "open", null, 4, DateTimeOffset.UtcNow);
        Assert.True(result);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void GateCheckDecision_ShouldNotifyBlocked_OnlyTheUnblockedToBlockedTransitionNotifies(bool previousBlocked, bool newBlocked, bool expected)
    {
        Assert.Equal(expected, GateCheckDecision.ShouldNotifyBlocked(previousBlocked, newBlocked));
    }

    // GateCheckAnalyzer end-to-end, without a real AI backend or a real gate to photograph -
    // proves the full save-and-dispatch path (the one seam the unit tests above can't reach)
    // fires correctly on a transition and stays silent otherwise.
    private sealed class FakeGateStateVisionClient(string state, bool blocked) : IVisionAnalysisClient
    {
        public Task<(VisionAnalysis Analysis, string Raw, string Model)> AnalyzeAsync(
            IncomingAlert alert, PromptProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult((
                new VisionAnalysis { Gate = new GateAnalysis { State = state }, CameraBlocked = blocked, Confidence = 0.9 },
                "{}",
                "fake-model"));

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RecordingAlertAction : IAlertAction
    {
        public List<AlertResult> Calls { get; } = [];

        public Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
        {
            Calls.Add(result);
            return Task.CompletedTask;
        }
    }

    private static GateCheckRequest MakeGateCheckRequest(string previousState = "closed", bool previousBlocked = false, bool checkBlocked = true) => new(
        Camera: "front gate camera",
        CheckLabel: "the front gate",
        CheckBlocked: checkBlocked,
        ImageBytes: [1, 2, 3],
        ContentType: "image/jpeg",
        FileName: "snapshot.jpg",
        PreviousState: previousState,
        PreviousBlocked: previousBlocked,
        LastStateChangedAt: null,
        LastNotifiedAt: null,
        RemindIfStillOpenAfterHours: null);

    [Fact]
    public async Task GateCheckAnalyzer_OnClosedToOpenTransition_SavesAGateCheckResultAndDispatchesActions()
    {
        var resultStore = new RecordingResultStore();
        var action = new RecordingAlertAction();
        var analyzer = new GateCheckAnalyzer(
            new FakeGateStateVisionClient("open", blocked: false), resultStore, [action],
            NullLogger<GateCheckAnalyzer>.Instance);

        var outcome = await analyzer.AnalyzeAsync(MakeGateCheckRequest(previousState: "closed"), CancellationToken.None);

        Assert.True(outcome.NotifiedOpen);
        Assert.False(outcome.NotifiedBlocked);
        Assert.Equal("open", outcome.State);
        Assert.NotNull(outcome.NotifiedAt);

        var saved = Assert.Single(resultStore.Saved);
        Assert.Equal(GateCheckDecision.ProfileName, saved.Profile);
        Assert.True(saved.Analysis!.NotificationRecommended);
        Assert.Equal("front gate camera", saved.Camera);

        var dispatched = Assert.Single(action.Calls);
        Assert.Same(saved, dispatched);
        // The whole point of the reserved profile - a real downstream policy check, not just
        // trusting that GateCheckAnalyzer set NotificationRecommended correctly.
        Assert.True(AlertNotificationPolicy.ShouldNotify(dispatched));
    }

    [Fact]
    public async Task GateCheckAnalyzer_WhenStillClosed_NeitherSavesNorDispatchesAnything()
    {
        var resultStore = new RecordingResultStore();
        var action = new RecordingAlertAction();
        var analyzer = new GateCheckAnalyzer(
            new FakeGateStateVisionClient("closed", blocked: false), resultStore, [action],
            NullLogger<GateCheckAnalyzer>.Instance);

        var outcome = await analyzer.AnalyzeAsync(MakeGateCheckRequest(previousState: "closed"), CancellationToken.None);

        Assert.False(outcome.NotifiedOpen);
        Assert.False(outcome.NotifiedBlocked);
        Assert.Empty(resultStore.Saved);
        Assert.Empty(action.Calls);
    }

    [Fact]
    public async Task GateCheckAnalyzer_OnBlockedTransition_NotifiesEvenWhileGateStaysClosed()
    {
        var resultStore = new RecordingResultStore();
        var action = new RecordingAlertAction();
        var analyzer = new GateCheckAnalyzer(
            new FakeGateStateVisionClient("closed", blocked: true), resultStore, [action],
            NullLogger<GateCheckAnalyzer>.Instance);

        var outcome = await analyzer.AnalyzeAsync(
            MakeGateCheckRequest(previousState: "closed", previousBlocked: false), CancellationToken.None);

        Assert.False(outcome.NotifiedOpen);
        Assert.True(outcome.NotifiedBlocked);
        Assert.True(outcome.Blocked);
        Assert.Single(resultStore.Saved);
        Assert.Single(action.Calls);
    }

    [Fact]
    public async Task GateCheckAnalyzer_WhenCheckBlockedIsOff_IgnoresAModelBlockedReadingEntirely()
    {
        // The model might still report camera_blocked (the field is always present in the
        // schema once requested), but a camera whose owner never turned this on shouldn't ever
        // notify because of it.
        var resultStore = new RecordingResultStore();
        var action = new RecordingAlertAction();
        var analyzer = new GateCheckAnalyzer(
            new FakeGateStateVisionClient("closed", blocked: true), resultStore, [action],
            NullLogger<GateCheckAnalyzer>.Instance);

        var outcome = await analyzer.AnalyzeAsync(
            MakeGateCheckRequest(previousState: "closed", previousBlocked: false, checkBlocked: false), CancellationToken.None);

        Assert.False(outcome.NotifiedBlocked);
        Assert.False(outcome.Blocked);
        Assert.Empty(resultStore.Saved);
    }

    // These particular flags are what keep a live tile close to real time, and every one of them
    // is the kind of thing that gets dropped by accident while editing an argument list nobody
    // reads closely. Losing temp_file serves half-written segments; losing the delete threshold
    // 404s viewers who fall a second behind; losing the probe limits puts five seconds back onto
    // every cold start. None of those break a test that only checks the stream plays.
    [Fact]
    public void FfmpegArguments_KeepTheLowLatencyHlsSettings()
    {
        var args = HlsStreamManager.BuildFfmpegArguments("rtsp://cam/live", includeAudio: false, "/tmp/out");
        var line = string.Join(' ', args);

        // Input-side buffering and probing, which only count if they precede -i.
        Assert.Contains("-fflags nobuffer", line);
        Assert.True(args.IndexOf("-fflags") < args.IndexOf("-i"));
        Assert.True(args.IndexOf("-probesize") < args.IndexOf("-i"));

        var flags = args[args.IndexOf("-hls_flags") + 1].Split('+');
        Assert.Contains("temp_file", flags);
        Assert.Contains("independent_segments", flags);
        Assert.Contains("delete_segments", flags);
        Assert.Contains("omit_endlist", flags);
        Assert.Equal("3", args[args.IndexOf("-hls_delete_threshold") + 1]);
        Assert.Equal("1", args[args.IndexOf("-hls_time") + 1]);
    }
}
