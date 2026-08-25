extern alias MainApp;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using MainProgram = MainApp::Program;

namespace SentinelCore.Tests;

// The main app had no end-to-end coverage at all. Every integration test in this project pointed
// at SentinelCore.Web, so the service that actually receives alerts, queues them and stores the
// results was only ever exercised through its own units - including /api/blueiris/alert, which is
// the one endpoint every camera and every Blue Iris install in the world talks to.
//
// Three things make hosting this one awkward, all of them worth knowing before adding a test here:
//
//   1. Both projects are top-level-statement executables, so `Program` is ambiguous. Hence the
//      extern alias, the same trick WebAuthorizationTests uses from the other side.
//   2. The app derives its own appsettings.json path from ContentRootPath and will WRITE to it
//      (AutoApiKeyProvisioner persists a generated key). Pointing the content root at a scratch
//      directory is what keeps a test run away from the developer's real configuration - without
//      it, this fixture would be reading, and potentially rewriting, the live config.
//   3. It refuses to start with no PromptProfiles, so the scratch config carries one.
//
// Everything that could reach the outside world is pinned off below. A test suite that emailed
// the developer, or bound the embedded MQTT broker's port, would be a bad trade for coverage.
public sealed class MainAppEndpointTests : IAsyncDisposable
{
    private const string TestApiKey = "test-key-0123456789abcdef0123456789abcdef";

    private readonly string _root;

    public MainAppEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mainapp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), """
        {
          "PromptProfiles": {
            "default": {
              "DisplayName": "Test",
              "SystemPrompt": "Analyze this security-camera image accurately.",
              "Questions": ["What is visible?"]
            }
          }
        }
        """);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<MainProgram> CreateFactory() => new WebApplicationFactory<MainProgram>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_root);
            // AddInMemoryCollection during ConfigureAppConfiguration is appended last, so these
            // outrank the appsettings.json loaded from the content root - the same reason
            // WebHostTestSettings uses this rather than UseSetting.
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bridge:ApiKey"] = TestApiKey,
                ["Bridge:DataDirectory"] = _root,
                ["Email:Enabled"] = "false",
                ["Telegram:Enabled"] = "false",
                ["TelegramChat:Enabled"] = "false",
                ["Discord:Enabled"] = "false",
                ["Webhook:Enabled"] = "false",
                ["Mqtt:Enabled"] = "false",
                ["Mqtt:RunEmbeddedBroker"] = "false",
                ["DeviceHub:Enabled"] = "false",
                ["StartupNotification:Enabled"] = "false",
                ["SelfMonitoring:Enabled"] = "false",
                ["DailySummary:Enabled"] = "false",
                ["InternetOutage:Enabled"] = "false",
                ["BugReports:Enabled"] = "false",
                ["PushNotifications:Enabled"] = "false",
            }));
        });

    private HttpClient Keyed(WebApplicationFactory<MainProgram> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
        return client;
    }

    // Also the regression test for shutdown: Host.StopAsync used to throw an AggregateException of
    // ObjectDisposedExceptions here, because three hosted services touched a CancellationTokenSource
    // or a SemaphoreSlim that disposal had already released. Disposing the factory is what asserts
    // it - a throw fails the test.
    [Fact]
    public async Task TheServiceStartsAnswersAndStopsCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory();
        using var client = Keyed(factory);

        using var live = await client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task EveryApiRoute_RefusesARequestWithNoApiKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory();
        using var anonymous = factory.CreateClient();

        foreach (var route in new[] { "/api/queue/status", "/api/system/status", "/api/devices", "/api/schedules" })
        {
            using var response = await anonymous.GetAsync(route, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Health is deliberately outside /api so a monitor can reach it without the key.
        using var health = await anonymous.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task TheReadRoutes_AnswerWithoutBlowingUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory();
        using var client = Keyed(factory);

        string[] routes =
        [
            "/health/live", "/api/queue/status", "/api/system/status",
            "/api/devices", "/api/devices/metadata", "/api/devices/state",
            "/api/schedules", "/api/schedules/metadata",
            "/api/automation-rules", "/api/automation-rules/metadata",
            "/api/system/host-reboot",
        ];

        foreach (var route in routes)
        {
            using var response = await client.GetAsync(route, ct);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"GET {route} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    [Fact]
    public async Task AnAlertIsAccepted_QueuedAndReadableBackByItsId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory();
        using var client = Keyed(factory);

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(OnePixelPng);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "image", "frame.png");
        form.Add(new StringContent("Front Gate"), "camera");

        using var accepted = await client.PostAsync("/api/blueiris/alert", form, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var queued = await accepted.Content.ReadFromJsonElementAsync(ct);
        var id = queued.GetProperty("id").GetGuid();

        using var stored = await client.GetAsync($"/api/results/{id}", ct);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);

        var result = await stored.Content.ReadFromJsonElementAsync(ct);
        Assert.Equal("Front Gate", result.GetProperty("camera").GetString());
    }

    // The sender is a camera or a Blue Iris instance across a home network, so a body that stops
    // halfway is an ordinary event, not an exotic one. Every one of these used to come back 500
    // with an unhandled-exception log line, for input the endpoint already had a considered 400 for.
    [Theory]
    [InlineData("/api/blueiris/alert")]
    [InlineData("/api/gate-check")]
    public async Task AMalformedMultipartBody_IsRejectedNotCrashedOn(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory();
        using var client = Keyed(factory);

        // A body that stops in the middle of its first part.
        using var truncated = new StringContent(
            "--abc\r\nContent-Disposition: form-data; name=\"image\"; filename=\"a.jpg\"\r\n\r\npartial");
        truncated.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=abc");

        using var response = await client.PostAsync(route, truncated, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // A multipart envelope with nothing in it at all.
        using var empty = new MultipartFormDataContent();
        using var emptyResponse = await client.PostAsync(route, empty, ct);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
    }

    [Fact]
    public async Task ARequestThatIsNotMultipartAtAll_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory();
        using var client = Keyed(factory);

        using var response = await client.PostAsync(
            "/api/blueiris/alert", new StringContent("{}"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AFormWithNoImageInIt_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = CreateFactory();
        using var client = Keyed(factory);

        using var fieldsOnly = new MultipartFormDataContent { { new StringContent("Front Gate"), "camera" } };
        using var response = await client.PostAsync("/api/blueiris/alert", fieldsOnly, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Smallest thing the content-type and length checks will accept; nothing here decodes it.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}

internal static class JsonElementResponseExtensions
{
    public static async Task<JsonElement> ReadFromJsonElementAsync(
        this HttpContent content, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
}
