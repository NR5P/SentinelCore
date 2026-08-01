extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// /api/system/restart is the step that turns "saved - restart required" from an SSH errand into
// a button. The pivotal behavior is the refusal: a process nobody is supervising must decline to
// exit, because nothing would bring it back.
public sealed class RestartEndpointTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public RestartEndpointTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory(bool forceAllowed = false)
    {
        var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder
                    .UseSetting("MainAppSettingsPath", _settingsPath)
                    .UseSetting("CamerasDataDirectory", _dataDirectory)
                    .UseSetting("Bridge:DataDirectory", _dataDirectory);
                if (forceAllowed) builder.UseSetting("Restart:ForceAllowed", "true");
            });
        return factory;
    }

    // The dev-box case: `dotnet run` in a terminal, no systemd, no INVOCATION_ID. Restarting
    // would just be an outage, so the endpoint must refuse rather than exit.
    [Fact]
    public async Task Dashboard_Restart_IsRefusedWhenNothingSupervisesTheProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/system/restart", new { Service = "dashboard" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("supervisor", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dashboard_Restart_IsAcceptedWhenSupervisionIsDeclared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(forceAllowed: true);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/system/restart", new { Service = "dashboard" }, ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.True(document.RootElement.GetProperty("restarting").GetBoolean());

        // The shutdown itself is delayed so this response could flush; the factory's disposal
        // will race it harmlessly - the host is either stopped by us or by the endpoint.
    }

    [Fact]
    public async Task Bridge_Restart_ReportsTheMainAppUnreachable_RatherThanPretending()
    {
        var ct = TestContext.Current.CancellationToken;

        // A configured API key gets the request past the 503 guard; the main app itself isn't
        // running in this test host, so the forward must come back 502, not 202.
        File.WriteAllText(_settingsPath, """
            { "Bridge": { "ApiKey": "a-long-enough-stored-api-key" } }
            """);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/system/restart", new { Service = "bridge" }, ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownService_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory(forceAllowed: true);
        using var client = factory.CreateClient();

        using var missing = await client.PostAsJsonAsync("/api/system/restart", new { }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using var unknown = await client.PostAsJsonAsync("/api/system/restart", new { Service = "toaster" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
