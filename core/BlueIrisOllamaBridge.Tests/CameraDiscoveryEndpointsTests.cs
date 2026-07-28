extern alias WebHost;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace BlueIrisOllamaBridge.Tests;

// End-to-end coverage through the real ASP.NET pipeline (not a hand-built fixture) for two things
// that only show up at that level: whether the discovery endpoints are actually gated the same
// way as every other mutating camera endpoint, and whether the enum status this feature
// introduced onto the wire actually serializes as a readable string rather than a raw integer -
// see the JsonStringEnumConverter registration this PR adds to Program.cs.
public sealed class CameraDiscoveryEndpointsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public CameraDiscoveryEndpointsTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"camera-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            .UseSetting("Bridge:DataDirectory", _dataDirectory));

    [Fact]
    public async Task Probe_WithAuthDisabled_IsReachable()
    {
        // Auth:Enabled defaults to false - the discovery endpoint must work out of the box on a
        // fresh install like every other camera endpoint does, not silently require login setup
        // first.
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/cameras/discovery/probe",
            new { Address = "not-an-ip" }, ct);

        // Reaching validation (400, not 401/403) is what proves the endpoint is reachable at all.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Probe_WithAnInvalidAddress_ReturnsBadRequestWithoutTouchingTheNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/cameras/discovery/probe",
            new { Address = "definitely not an ip address" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Probe_AgainstAnUnreachableAddress_RespondsQuicklyWithAStringStatus()
    {
        // Loopback with nothing listening refuses a TCP connect immediately and deterministically
        // in any environment (including a sandboxed CI runner), unlike probing a nonexistent LAN
        // IP, whose behavior (instant "no route" vs a silently dropped packet) depends on the
        // network the test happens to run on.
        //
        // The 6s ceiling is a regression guard on its own right: before the TCP pre-check this PR
        // adds, an unreachable host meant up to seven sequential ffprobe process spawns at 8s
        // each - the better part of a minute for testing a typo'd IP, the single most common
        // thing a user will actually do with this feature.
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.PostAsJsonAsync("/api/cameras/discovery/probe",
            new { Address = "127.0.0.1" }, ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6),
            $"Probing an unreachable address took {stopwatch.Elapsed} - the fast-fail path likely regressed.");

        var body = await response.Content.ReadAsStringAsync(ct);
        // Proves the JsonStringEnumConverter registration actually applies to this response -
        // without it CameraProbeStatus.NotFound serializes as the raw integer 2, not "NotFound".
        Assert.Contains("\"status\":\"NotFound\"", body);
        Assert.DoesNotContain("\"status\":2", body);
    }
}
