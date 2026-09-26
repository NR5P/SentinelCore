extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The two Weather endpoints through the real pipeline: that they are wired up at all, that a
// fresh install with no location gets a plain "unconfigured" answer without the server reaching
// for the internet, and that the radar proxy refuses to fetch anything it wasn't designed for.
public sealed class WeatherEndpointsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public WeatherEndpointsTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"weather-endpoints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

    [Fact]
    public async Task Report_WithNoLocation_SaysSoWithoutTouchingTheNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/weather", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("unconfigured", report.GetProperty("state").GetString());
        Assert.Contains("LOCATION", report.GetProperty("message").GetString());
        // camelCase like every other /api answer, so the tab's field names are the DTO's.
        Assert.True(report.TryGetProperty("fetchedAt", out _));
    }

    [Fact]
    public async Task Radar_WithNoLocation_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/weather/radar", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Radar_WithAnUnknownScope_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/weather/radar?scope=../etc&kind=loop", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
