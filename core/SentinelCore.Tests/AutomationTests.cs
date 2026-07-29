using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

public sealed class AutomationTests
{
    [Fact]
    public void AutomationTemplate_Render_SubstitutesKnownTokensAndEscapesSpecialCharacters()
    {
        var result = new AlertResult
        {
            Id = Guid.NewGuid(),
            Camera = "gate camera front door",
            Analysis = new VisionAnalysis
            {
                NotificationRecommended = true,
                Confidence = 0.9,
                Person = new PersonAnalysis { Visible = true, Location = "outside_gate", Activity = "standing" },
                Summary = "A \"visitor\" arrived\nlate."
            }
        };

        var rendered = AutomationTemplate.Render(
            """{"camera":"{{camera}}","summary":"{{summary}}","category":"{{category}}"}""",
            result, "person-at-gate");

        Assert.Equal(
            """{"camera":"gate camera front door","summary":"A \"visitor\" arrived\nlate.","category":"person-at-gate"}""",
            rendered);
    }

    [Fact]
    public void AutomationTemplate_Render_LeavesUnknownTokensUntouched()
    {
        var result = NotifiableResult("gate camera front door");

        var rendered = AutomationTemplate.Render("{{camera}} / {{not_a_real_token}}", result, null);

        Assert.Equal("gate camera front door / {{not_a_real_token}}", rendered);
    }

    [Fact]
    public void AutomationTemplate_Render_CategoryFallsBackToNoneWhenUncategorized()
    {
        var result = NotifiableResult("gate camera front door");

        Assert.Equal("none", AutomationTemplate.Render("{{category}}", result, null));
    }

    [Fact]
    public async Task SqliteAutomationRuleStore_CreateGetUpdateDelete_RoundTrips()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteAutomationRuleStore(options, new FakeHostEnvironment(tempDir));

            var now = DateTimeOffset.UtcNow;
            var created = await store.CreateAsync(new AutomationRule(
                Guid.NewGuid(), "Gate MQTT", true, ["gate camera"], ["person-at-gate"],
                0.6, true, 5, "mqtt", "home/{{camera}}", "{{summary}}", false,
                "", "POST", "", "", "application/json", now, now), TestContext.Current.CancellationToken);

            var fetched = await store.GetAsync(created.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(fetched);
            Assert.Equal("Gate MQTT", fetched.Name);
            Assert.Equal(["gate camera"], fetched.Cameras);
            Assert.Equal(["person-at-gate"], fetched.Categories);

            var enabledBefore = await store.GetEnabledAsync(TestContext.Current.CancellationToken);
            Assert.Single(enabledBefore);

            var updated = await store.UpdateAsync(created.Id, created with { Enabled = false, Name = "Gate MQTT (off)" }, TestContext.Current.CancellationToken);
            Assert.NotNull(updated);
            Assert.False(updated.Enabled);

            var enabledAfter = await store.GetEnabledAsync(TestContext.Current.CancellationToken);
            Assert.Empty(enabledAfter);

            Assert.True(await store.DeleteAsync(created.Id, TestContext.Current.CancellationToken));
            Assert.Null(await store.GetAsync(created.Id, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AutomationAlertAction_Mqtt_FiresOnMatchingCameraAndPublishesRenderedTopicAndPayload()
    {
        var publisher = new FakeMqttPublisher();
        var store = new FakeAutomationRuleStore([MqttRule(cameras: ["gate camera front door"], categories: [])]);
        var action = BuildAction(store, publisher, mqttEnabled: true);

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        var publish = Assert.Single(publisher.Published);
        Assert.Equal("home/gate camera front door", publish.Topic);
        Assert.Contains("standing outside the gate", publish.Payload);
    }

    [Fact]
    public async Task AutomationAlertAction_SkipsCamerasNotInTheRulesList()
    {
        var publisher = new FakeMqttPublisher();
        var store = new FakeAutomationRuleStore([MqttRule(cameras: ["back door camera"], categories: [])]);
        var action = BuildAction(store, publisher, mqttEnabled: true);

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AutomationAlertAction_SkipsWhenCategoryDoesNotMatch()
    {
        var publisher = new FakeMqttPublisher();
        // NotifiableResult's person is at the gate, standing - AlertCategorizer maps that to
        // "person-at-gate", not "vehicle-entering", so a vehicle-only rule must not fire.
        var store = new FakeAutomationRuleStore([MqttRule(cameras: [], categories: ["vehicle-entering"])]);
        var action = BuildAction(store, publisher, mqttEnabled: true);

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AutomationAlertAction_SkipsWhenBelowMinimumConfidence()
    {
        var publisher = new FakeMqttPublisher();
        var rule = MqttRule(cameras: [], categories: []) with { MinimumConfidence = 0.95 };
        var store = new FakeAutomationRuleStore([rule]);
        var action = BuildAction(store, publisher, mqttEnabled: true);

        // NotifiableResult's confidence is 0.9, below the rule's 0.95 floor.
        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AutomationAlertAction_Mqtt_DoesNotPublishWhenMqttIntegrationIsDisabled()
    {
        var publisher = new FakeMqttPublisher();
        var store = new FakeAutomationRuleStore([MqttRule(cameras: [], categories: [])]);
        var action = BuildAction(store, publisher, mqttEnabled: false);

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AutomationAlertAction_RespectsPerRuleCooldown()
    {
        var publisher = new FakeMqttPublisher();
        var rule = MqttRule(cameras: [], categories: []) with { DuplicateCooldownMinutes = 30 };
        var store = new FakeAutomationRuleStore([rule]);
        var action = BuildAction(store, publisher, mqttEnabled: true);

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);
        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task AutomationAlertAction_Webhook_PostsRenderedBodyMethodAndHeaders()
    {
        var handler = new RecordingHandler();
        var rule = WebhookRule() with
        {
            WebhookHeaders = "X-Camera: {{camera}}",
            WebhookBody = """{"summary":"{{summary}}"}"""
        };
        var store = new FakeAutomationRuleStore([rule]);
        var action = BuildAction(store, new FakeMqttPublisher(), mqttEnabled: true, handler);

        await action.ExecuteAsync(NotifiableResult("gate camera front door"), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://example.com/hook", request.Url);
        Assert.Equal("POST", request.Method);
        Assert.Contains("standing outside the gate", request.Body);
        Assert.Equal("gate camera front door", request.Headers["X-Camera"]);
    }

    [Fact]
    public async Task AutomationAlertAction_FireTestAsync_BypassesMatchingAndFiresImmediately()
    {
        var publisher = new FakeMqttPublisher();
        // A rule scoped to a camera/category the synthetic test alert would never satisfy on its
        // own (min confidence above what a real low-confidence reading could hit isn't the point
        // here - the point is FireTestAsync skips Matches() entirely).
        var rule = MqttRule(cameras: ["some other camera"], categories: ["vehicle-entering"]) with { MinimumConfidence = 0.99 };
        var action = BuildAction(new FakeAutomationRuleStore([rule]), publisher, mqttEnabled: true);

        var (success, detail) = await action.FireTestAsync(rule, CancellationToken.None);

        Assert.True(success);
        Assert.Contains("published to", detail);
        Assert.Single(publisher.Published);
    }

    private static AutomationAlertAction BuildAction(
        FakeAutomationRuleStore store, FakeMqttPublisher publisher, bool mqttEnabled, RecordingHandler? handler = null) =>
        new(new HttpClient(handler ?? new RecordingHandler()),
            store,
            publisher,
            new StaticOptionsMonitor<MqttOptions>(new MqttOptions { Enabled = mqttEnabled }),
            NullLogger<AutomationAlertAction>.Instance);

    private static AutomationRule MqttRule(IReadOnlyList<string> cameras, IReadOnlyList<string> categories)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationRule(
            Guid.NewGuid(), "Test rule", true, cameras, categories,
            0.5, false, 0, "mqtt", "home/{{camera}}", "{{summary}}", false,
            "", "POST", "", "", "application/json", now, now);
    }

    private static AutomationRule WebhookRule()
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationRule(
            Guid.NewGuid(), "Test webhook rule", true, [], [],
            0.5, false, 0, "webhook", "", "", false,
            "https://example.com/hook", "POST", "", "", "application/json", now, now);
    }

    // Person visible, standing outside the gate - AlertCategorizer maps this to "person-at-gate".
    private static AlertResult NotifiableResult(string camera) => new()
    {
        Id = Guid.NewGuid(),
        Camera = camera,
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Confidence = 0.9,
            Person = new PersonAnalysis { Visible = true, Location = "outside_gate", Activity = "standing" },
            Summary = "A person is standing outside the gate."
        }
    };

    private sealed class FakeAutomationRuleStore(IReadOnlyList<AutomationRule> rules) : IAutomationRuleStore
    {
        public Task<IReadOnlyList<AutomationRule>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rules);

        public Task<IReadOnlyList<AutomationRule>> GetEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationRule>>(rules.Where(r => r.Enabled).ToList());

        public Task<AutomationRule?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(rules.FirstOrDefault(r => r.Id == id));

        public Task<AutomationRule> CreateAsync(AutomationRule rule, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AutomationRule?> UpdateAsync(Guid id, AutomationRule rule, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMqttPublisher : IMqttPublisher
    {
        public List<(string Topic, string Payload, bool Retain)> Published { get; } = [];

        public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
        {
            Published.Add((topic, payload, retain));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string Method, string Body, Dictionary<string, string> Headers)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Requests.Add((request.RequestUri!.ToString(), request.Method.Method, body, headers));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
