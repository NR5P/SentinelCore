extern alias WebHost;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// Every app build enables its Send Test button on appleConfigured - so on the shipped default
// (relay on, no Apple credentials) that flag has to say yes, or the button is dead on every
// customer's phone. Found the hard way on the first live relay test.
public sealed class PushStatusEndpointTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public PushStatusEndpointTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"push-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Status_RelayOnlyInstall_ReportsAppleAndAndroidAsConfiguredViaTheRelay()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(_settingsPath, """
            { "PushNotifications": { "Enabled": true,
                "Apple": { "Enabled": false }, "Firebase": { "Enabled": false },
                "Relay": { "Enabled": true, "Url": "https://push.example.test" } } }
            """);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/push/status", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.True(status.GetProperty("appleConfigured").GetBoolean());
        Assert.True(status.GetProperty("firebaseConfigured").GetBoolean());
        Assert.True(status.GetProperty("relayConfigured").GetBoolean());
        Assert.Equal("relay", status.GetProperty("appleProvider").GetString());
    }

    [Fact]
    public async Task Status_WithEveryProviderOff_ReportsNothingConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(_settingsPath, """
            { "PushNotifications": { "Enabled": true,
                "Apple": { "Enabled": false }, "Firebase": { "Enabled": false },
                "Relay": { "Enabled": false } } }
            """);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/push/status", ct);

        var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.False(status.GetProperty("appleConfigured").GetBoolean());
        Assert.False(status.GetProperty("firebaseConfigured").GetBoolean());
        Assert.False(status.GetProperty("relayConfigured").GetBoolean());
        Assert.Equal("none", status.GetProperty("appleProvider").GetString());
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            .UseSetting("Bridge:DataDirectory", _dataDirectory));
}
