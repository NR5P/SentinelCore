extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Device Hub settings section is the one section whose validity depends on another one - it
// controls devices over the MQTT connection the Mqtt section configures, so enabling it without
// MQTT would produce a tab whose every button fails. Same WebApplicationFactory setup as
// WebAuthorizationTests; see the note there on the extern alias.
public sealed class DeviceHubSettingsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public DeviceHubSettingsTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"device-hub-settings-{Guid.NewGuid():N}");
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
    public async Task EnablingTheDeviceHubWhileMqttIsOff_IsRefusedWithAnActionableMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            "/api/settings/device-hub",
            new { Enabled = true, StateTimeoutSeconds = 90, DiscoverySeconds = 10, CommandTimeoutSeconds = 5 },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("MQTT", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnablingTheDeviceHubWithMqttOn_SavesAndRoundTripsThroughTheSettingsSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var mqtt = await client.PutAsJsonAsync(
            "/api/settings/mqtt",
            new
            {
                Enabled = true,
                RunEmbeddedBroker = true,
                EmbeddedBrokerPort = 1883,
                BrokerHost = "127.0.0.1",
                BrokerPort = 1883,
                TopicPrefix = "sentinelcore",
                HomeAssistantDiscoveryPrefix = "homeassistant"
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, mqtt.StatusCode);

        using var save = await client.PutAsJsonAsync(
            "/api/settings/device-hub",
            new { Enabled = true, StateTimeoutSeconds = 120, DiscoverySeconds = 15, CommandTimeoutSeconds = 8 },
            ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Read back through the same snapshot the dashboard and config backup both use - a
        // section missing from SettingsSnapshot would save fine here but silently vanish from
        // both, which is exactly the failure this asserts against.
        using var snapshot = await client.GetAsync("/api/settings", ct);
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);

        using var document = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync(ct));
        var deviceHub = document.RootElement.GetProperty("deviceHub");
        Assert.True(deviceHub.GetProperty("enabled").GetBoolean());
        Assert.Equal(120, deviceHub.GetProperty("stateTimeoutSeconds").GetInt32());
        Assert.Equal(15, deviceHub.GetProperty("discoverySeconds").GetInt32());
    }

    // The flag every client gates its Device Hub tab on. It has to reflect both switches, since
    // the hub is useless without the broker connection the Mqtt section configures.
    [Fact]
    public async Task DeviceHubEnabledFlag_IsOnlyTrueWhenBothMqttAndTheHubAreOn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        Assert.False(await ReadEnabledFlagAsync(client, ct));

        using var mqtt = await client.PutAsJsonAsync(
            "/api/settings/mqtt",
            new
            {
                Enabled = true,
                BrokerHost = "127.0.0.1",
                BrokerPort = 1883,
                EmbeddedBrokerPort = 1883,
                TopicPrefix = "sentinelcore",
                HomeAssistantDiscoveryPrefix = "homeassistant"
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, mqtt.StatusCode);

        // MQTT on, hub still off.
        Assert.False(await ReadEnabledFlagAsync(client, ct));

        using var hub = await client.PutAsJsonAsync(
            "/api/settings/device-hub",
            new { Enabled = true, StateTimeoutSeconds = 90, DiscoverySeconds = 10, CommandTimeoutSeconds = 5 },
            ct);
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);

        Assert.True(await ReadEnabledFlagAsync(client, ct));
    }

    private static async Task<bool> ReadEnabledFlagAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("/api/device-hub/enabled", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("enabled").GetBoolean();
    }
}
