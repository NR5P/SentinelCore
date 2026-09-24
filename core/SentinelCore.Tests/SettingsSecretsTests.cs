extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The masked-secret contract: GET /api/settings never returns a stored secret, and a save that
// sends the mask back untouched keeps what's stored. This is what makes it safe for phones and
// TVs to render the same settings forms the dashboard does.
public sealed class SettingsSecretsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public SettingsSecretsTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"settings-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
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
    public async Task Get_MasksStoredSecrets_AndLeavesUnsetOnesEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(_settingsPath, """
            { "Auth": { "Enabled": false },
              "Telegram": { "Enabled": true, "BotToken": "1234567:real-bot-token", "ChatId": "9876" },
              "Email": { "Enabled": false, "AppPassword": "" }
            }
            """);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/settings", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The real token appears nowhere in the payload; the mask stands in for it.
        Assert.DoesNotContain("real-bot-token", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(SettingsSecrets.Mask, document.RootElement.GetProperty("telegram").GetProperty("botToken").GetString());

        // Unset secrets stay empty rather than masked - the form must be able to show the
        // difference between "configured" and "never set".
        Assert.Equal("", document.RootElement.GetProperty("email").GetProperty("appPassword").GetString());

        // Non-secret fields are untouched.
        Assert.Equal("9876", document.RootElement.GetProperty("telegram").GetProperty("chatId").GetString());
    }

    [Fact]
    public async Task Save_WithTheMask_KeepsTheStoredSecret_AndStillSavesTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(_settingsPath, """
            { "Auth": { "Enabled": false }, "Telegram": { "Enabled": true, "BotToken": "1234567:real-bot-token", "ChatId": "9876" } }
            """);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // What a client that edited only ChatId sends back: the mask it was given, unchanged.
        using var save = await client.PutAsJsonAsync(
            "/api/settings/telegram",
            new
            {
                Enabled = true,
                BotToken = SettingsSecrets.Mask,
                ChatId = "5555",
                OnlyWhenNotificationRecommended = true,
                MinimumConfidence = 0.6,
                AttachImage = true,
                DuplicateCooldownMinutes = 10
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, ct))!.AsObject();
        Assert.Equal("1234567:real-bot-token", root["Telegram"]!["BotToken"]!.GetValue<string>());
        Assert.Equal("5555", root["Telegram"]!["ChatId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Save_WithANewValue_ReplacesTheStoredSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(_settingsPath, """
            { "Auth": { "Enabled": false }, "Telegram": { "Enabled": true, "BotToken": "1234567:old-token", "ChatId": "9876" } }
            """);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/telegram",
            new
            {
                Enabled = true,
                BotToken = "7654321:new-token",
                ChatId = "9876",
                OnlyWhenNotificationRecommended = true,
                MinimumConfidence = 0.6,
                AttachImage = true,
                DuplicateCooldownMinutes = 10
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, ct))!.AsObject();
        Assert.Equal("7654321:new-token", root["Telegram"]!["BotToken"]!.GetValue<string>());
    }

    // Bridge.ApiKey has MinLength(16) - shorter than the 8-character mask - so this only passes
    // if the unmask runs before validation, which is the ordering the whole design depends on.
    [Fact]
    public async Task Save_WithTheMask_IsValidatedAgainstTheStoredValue_NotTheMaskItself()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(_settingsPath, """
            { "Auth": { "Enabled": false }, "Bridge": { "ApiKey": "a-long-enough-stored-api-key", "MaximumImageBytes": 10485760, "RetentionDays": 30, "SaveImages": true, "AllowedImageContentTypes": ["image/jpeg"] } }
            """);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var save = await client.PutAsJsonAsync(
            "/api/settings/bridge",
            new
            {
                ApiKey = SettingsSecrets.Mask,
                MaximumImageBytes = 20971520,
                RetentionDays = 60,
                SaveImages = true,
                AllowedImageContentTypes = new[] { "image/jpeg", "image/png" }
            },
            ct);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, ct))!.AsObject();
        Assert.Equal("a-long-enough-stored-api-key", root["Bridge"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal(60, root["Bridge"]!["RetentionDays"]!.GetValue<int>());
    }

    // Someone will inevitably save a masked GET /api/settings response to a file and feed it to
    // the restore endpoint. That must keep the stored secrets, not overwrite them with mask
    // characters - the same contract a per-section save has.
    [Fact]
    public async Task Secrets_SurviveARestoreOfAMaskedExport()
    {
        var ct = TestContext.Current.CancellationToken;
        File.WriteAllText(_settingsPath, """
            { "Auth": { "Enabled": false }, "Mqtt": { "Enabled": true, "RunEmbeddedBroker": true, "EmbeddedBrokerPort": 1883, "BrokerHost": "127.0.0.1", "BrokerPort": 1883, "Username": "sentinel", "Password": "real-broker-password", "TopicPrefix": "sentinelcore", "HomeAssistantDiscoveryPrefix": "homeassistant" } }
            """);

        var settings = new AppSettingsFile(_settingsPath);
        var masked = await SettingsEndpoints.BuildSnapshotAsync(settings, ct);
        SettingsSecrets.MaskInPlace(masked);
        Assert.Equal(SettingsSecrets.Mask, masked.Mqtt!.Password);

        await SettingsEndpoints.ApplySnapshotAsync(masked, settings, ct);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, ct))!.AsObject();
        Assert.Equal("real-broker-password", root["Mqtt"]!["Password"]!.GetValue<string>());
    }
}
