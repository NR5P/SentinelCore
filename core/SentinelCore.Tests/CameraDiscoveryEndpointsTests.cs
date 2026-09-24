extern alias WebHost;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

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

    // The field is labelled "Stream URL or address", and for a long time only the second half was
    // true - pasting a stream URL answered "Enter a valid IP address", which is bad advice for
    // someone who knows their camera's URL exactly and merely wants it checked.
    [Theory]
    [InlineData("rtsp://127.0.0.1:554/Preview_01_main")]
    [InlineData("rtsps://127.0.0.1:322/stream")]
    [InlineData("http://127.0.0.1:1/flv?app=bcs&stream=channel0_main.bcs")]
    public async Task Probe_WithAFullStreamUrl_IsAccepted(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/cameras/discovery/probe",
            new { Address = url }, ct);

        // Loopback with nothing listening, so the verdict is NotFound - but it must be a verdict
        // about the stream, not a 400 refusing to look at the input at all.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"NotFound\"", await response.Content.ReadAsStringAsync(ct));
    }

    // Whatever survives validation is handed to ffprobe as an input, and ffprobe opens far more
    // than cameras - a file:// path would read the server's own disk. Only the schemes a camera
    // streams over get through.
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://192.168.1.50/stream")]
    [InlineData("rtsp://")]
    [InlineData("definitely not an ip address")]
    [InlineData("")]
    public async Task Probe_WithAnInputThatIsNeitherAnIpNorAStreamUrl_IsRejected(string input)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/cameras/discovery/probe",
            new { Address = input }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The distinction the whole "wrong password" message rests on. These are ffprobe's real words:
    // the RTSP line came from a Reolink RLC-510A dialed without the ':' its blank password needs,
    // the HTTP one is what an HTTP-FLV camera answers.
    [Theory]
    [InlineData("[rtsp @ 0x7f] method DESCRIBE failed: 401 Unauthorized", true)]
    [InlineData("[http @ 0x7f] HTTP error 401 Unauthorized", true)]
    // Captured verbatim from ffprobe 7.x on the deployment box, dialing a live Reolink at .146
    // anonymously and then with a wrong password. Note "401Unauthorized" with no space - ffmpeg
    // concatenates the RTSP status code and reason phrase, so the spaced form asserted above is
    // not in fact what this build emits. Both are kept: the two independent Contains checks accept
    // either, and pinning the real string stops anyone "tidying" this into a single `401\s+
    // Unauthorized` regex that would silently stop recognising a rejected login.
    [InlineData("[rtsp @ 0x5a1a] method DESCRIBE failed: 401Unauthorized", true)]
    [InlineData("rtsp://admin:pw@192.168.50.146:554/Preview_01_sub: "
        + "Server returned 401 Unauthorized (authorization failed)", true)]
    // The wrong-path answer from the same camera, which must stay distinguishable from the above.
    [InlineData("[rtsp @ 0x5e08] method DESCRIBE failed: 404Stream Not Found", false)]
    [InlineData("[rtsp @ 0x7f] method DESCRIBE failed: 404 Not Found", false)]
    [InlineData("[tcp @ 0x7f] Connection to tcp://10.0.0.9:554 failed: Connection refused", false)]
    // Neither half is enough alone: a 401 with no word, or the word with no code.
    [InlineData("Stream #0:0: Video: h264, 401x226", false)]
    [InlineData("unauthorized", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUnauthorized_SeparatesARejectedLoginFromEveryOtherFailure(string? stderr, bool expected)
    {
        Assert.Equal(expected, WebHost::SentinelCore.Web.CameraDiscoveryService.IsUnauthorized(stderr));
    }
}
