extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The three config sections that used to be editable only by hand in appsettings.json:
// PushNotifications, Logging, and the root "Urls" scalar. Each got its own endpoint quirk -
// nested sub-objects, a dotted category name that has to survive round-trips, and a bare string
// where every other save writes an object - so each gets its own proof here.
public sealed class SettingsGapSectionsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public SettingsGapSectionsTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"settings-gaps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            .UseSetting("Bridge:DataDirectory", _dataDirectory));

    [Fact]
    public async Task PushNotifications_SaveRoundTripsThroughTheSnapshot_NestedBlocksIncluded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/push-notifications",
            new
            {
                Enabled = true,
                OnlyWhenNotificationRecommended = true,
                MinimumConfidence = 0.7,
                DuplicateCooldownMinutes = 10,
                Apple = new
                {
                    Enabled = true,
                    TeamId = "G4GK5372G8",
                    KeyId = "ABC123DEFG",
                    BundleId = "com.sentinelcore.client",
                    TvBundleId = "com.sentinelcore.client.tv",
                    PrivateKeyPath = "/etc/sentinel/apns.p8",
                    UseSandbox = true
                },
                Firebase = new { Enabled = false, ProjectId = "", ServiceAccountPath = "" },
                Relay = new { Enabled = true, Url = "https://push.example.test", ApnsSandbox = true }
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var snapshot = await client.GetAsync("/api/settings", ct);
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        using var document = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync(ct));
        var push = document.RootElement.GetProperty("pushNotifications");

        Assert.True(push.GetProperty("enabled").GetBoolean());
        Assert.Equal("G4GK5372G8", push.GetProperty("apple").GetProperty("teamId").GetString());
        // TvBundleId is the field most at risk of being silently wiped: it exists on the Options
        // class but not in the shipped appsettings.json, so a DTO that forgot it would drop it
        // from the file on every save (MergeSectionAsync replaces the Apple block wholesale).
        Assert.Equal("com.sentinelcore.client.tv", push.GetProperty("apple").GetProperty("tvBundleId").GetString());
        Assert.Equal("https://push.example.test", push.GetProperty("relay").GetProperty("url").GetString());
        Assert.True(push.GetProperty("relay").GetProperty("apnsSandbox").GetBoolean());
    }

    // The shipped default: nothing filled in but the master switch, relay on. This is what a
    // customer's save looks like, and it has to be accepted as a complete configuration.
    [Fact]
    public async Task PushNotifications_EnabledWithOnlyTheRelay_IsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/push-notifications",
            new
            {
                Enabled = true,
                MinimumConfidence = 0.6,
                DuplicateCooldownMinutes = 5,
                Apple = new { Enabled = false },
                Firebase = new { Enabled = false },
                Relay = new { Enabled = true, Url = "https://push.nr5p.com" }
            },
            ct);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
    }

    [Fact]
    public async Task PushNotifications_RelayWithABareHostname_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/push-notifications",
            new
            {
                Enabled = true,
                MinimumConfidence = 0.6,
                DuplicateCooldownMinutes = 5,
                Apple = new { Enabled = false },
                Firebase = new { Enabled = false },
                Relay = new { Enabled = true, Url = "push.nr5p.com" }
            },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Contains("https://", await save.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task PushNotifications_EnabledWithNoDeliveryService_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/push-notifications",
            new
            {
                Enabled = true,
                MinimumConfidence = 0.6,
                DuplicateCooldownMinutes = 5,
                Apple = new { Enabled = false },
                Firebase = new { Enabled = false },
                Relay = new { Enabled = false }
            },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        var body = await save.Content.ReadAsStringAsync(ct);
        Assert.Contains("delivery service", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logging_Save_UpdatesTheTwoKnownCategories_AndPreservesHandAddedOnes()
    {
        var ct = TestContext.Current.CancellationToken;

        // A file with a hand-added category, exactly what someone chasing one component's logs
        // would have. The save below must not clobber it.
        File.WriteAllText(_settingsPath, """
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning",
                  "SentinelCore.MqttDeviceBus": "Debug"
                }
              }
            }
            """);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/logging",
            new { Default = "Debug", MicrosoftAspNetCore = "Error" },
            ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, ct))!.AsObject();
        var levels = root["Logging"]!["LogLevel"]!.AsObject();
        Assert.Equal("Debug", levels["Default"]!.GetValue<string>());
        Assert.Equal("Error", levels["Microsoft.AspNetCore"]!.GetValue<string>());
        Assert.Equal("Debug", levels["SentinelCore.MqttDeviceBus"]!.GetValue<string>());

        // And the flat DTO shape comes back out through the snapshot.
        using var snapshot = await client.GetAsync("/api/settings", ct);
        using var document = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync(ct));
        var logging = document.RootElement.GetProperty("logging");
        Assert.Equal("Debug", logging.GetProperty("default").GetString());
        Assert.Equal("Error", logging.GetProperty("microsoftAspNetCore").GetString());
    }

    [Fact]
    public async Task Logging_AMadeUpLevel_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/logging",
            new { Default = "Verbose", MicrosoftAspNetCore = "Warning" },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
    }

    [Fact]
    public async Task Server_Save_WritesTheRootUrlsScalar_AndReportsRestartRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/server",
            new { Urls = "http://0.0.0.0:5080;https://*:5443" },
            ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var saveBody = JsonDocument.Parse(await save.Content.ReadAsStringAsync(ct));
        Assert.True(saveBody.RootElement.GetProperty("restartRequired").GetBoolean());

        // Written as a bare string at the root - the shape the main app's configuration loader
        // actually reads - not wrapped in an object the way every section save is.
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, ct))!.AsObject();
        Assert.Equal("http://0.0.0.0:5080;https://*:5443", root["Urls"]!.GetValue<string>());

        using var snapshot = await client.GetAsync("/api/settings", ct);
        using var document = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync(ct));
        Assert.Equal(
            "http://0.0.0.0:5080;https://*:5443",
            document.RootElement.GetProperty("server").GetProperty("urls").GetString());
    }

    [Fact]
    public async Task Server_AnAddressKestrelCouldNotBind_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var bad in new[] { "not a url", "ftp://0.0.0.0:21", ";;;" })
        {
            using var save = await client.PutAsJsonAsync("/api/settings/server", new { Urls = bad }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        }
    }
}
