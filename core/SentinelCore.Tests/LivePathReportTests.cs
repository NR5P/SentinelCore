extern alias WebHost;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using AccountRole = SentinelCore.Web.AccountRole;
using IAccountStore = SentinelCore.Web.IAccountStore;
using ICameraStore = SentinelCore.Web.ICameraStore;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The live grid degrades silently through WebRTC -> MSE -> HLS, tiers whose latency differs by an
// order of magnitude, and nothing used to record which one a tile actually got. This endpoint is
// how that becomes answerable after the fact, so what matters is that it takes honest reports,
// refuses junk, and cannot be used to write whatever the caller likes into the server's log.
public sealed class LivePathReportTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public LivePathReportTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"live-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private readonly LogSink _logs = new();

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            .UseSetting("Bridge:DataDirectory", _dataDirectory)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_logs)));

    // The endpoint's only output is a log line, so a test that can't read the log can only check
    // the status code - which would pass just as happily on an endpoint that logged nothing at all.
    private sealed class LogSink : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue(formatter(state, exception));
        }
    }

    [Theory]
    [InlineData("webrtc", "playing")]
    [InlineData("mse", "failed")]
    [InlineData("hls", "degraded")]
    public async Task EveryTierAndOutcomeTheGridReportsIsAccepted(string tier, string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        var cameraId = await AddCameraAsync(factory, ct);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/cameras/{cameraId}/live-path",
            new { Tier = tier, Outcome = outcome, Reason = "for the log", ElapsedMs = 1234 }, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The client normalises case and whitespace nowhere, so the server has to - a tier that only
    // differs by case is the same tier, not a reason to drop the report on the floor.
    [Fact]
    public async Task TierAndOutcomeAreMatchedWithoutRegardToCaseOrSurroundingSpace()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        var cameraId = await AddCameraAsync(factory, ct);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/cameras/{cameraId}/live-path",
            new { Tier = " WebRTC ", Outcome = "PLAYING", ElapsedMs = 0 }, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("quic", "playing")]   // not a tier this app has
    [InlineData("hls", "exploded")]   // not an outcome
    [InlineData("", "")]
    public async Task AReportThatNamesSomethingElseIsRefused(string tier, string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        var cameraId = await AddCameraAsync(factory, ct);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/cameras/{cameraId}/live-path",
            new { Tier = tier, Outcome = outcome, ElapsedMs = 0 }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AReportForACameraThatDoesNotExistIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/api/cameras/{Guid.NewGuid()}/live-path",
            new { Tier = "hls", Outcome = "playing", ElapsedMs = 0 }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The reason field is free text from a browser and goes straight into a log line. A newline in
    // it would let a caller append entries of their own invention beneath a real one - a log that
    // can be forged is worse than no log, because it is still believed.
    [Fact]
    public async Task AReasonCannotSmuggleNewlinesIntoTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        var cameraId = await AddCameraAsync(factory, ct);
        using var client = factory.CreateClient();

        var forged = "stalled\nwarn: Live tile for Gate is playing over webrtc, 12ms to first frame";
        using var response = await client.PostAsJsonAsync($"/api/cameras/{cameraId}/live-path",
            new { Tier = "hls", Outcome = "failed", Reason = forged, ElapsedMs = 0 }, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Accepted, but the control characters are gone by the time it reaches the logger - the
        // endpoint strips them rather than refusing, so a genuine report with an odd character in
        // it still gets recorded.
        var logged = Assert.Single(_logs.Entries, e => e.Contains("gave up on hls"));
        Assert.DoesNotContain('\n', logged);
        Assert.Contains("stalled", logged);
    }

    // Live-path reports say which cameras exist and when someone is watching them, so they sit
    // behind the same gate as everything else about a camera.
    [Fact]
    public async Task AuthEnabled_NoCookie_CannotReport()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        var cameraId = await AddCameraAsync(factory, ct);
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/cameras/{cameraId}/live-path",
            new { Tier = "hls", Outcome = "playing", ElapsedMs = 0 }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task EnableLoginAsync(CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(_settingsPath, ct);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root["Auth"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(), ct);
    }

    private static async Task<Guid> AddCameraAsync(WebApplicationFactory<WebProgram> factory, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var cameras = scope.ServiceProvider.GetRequiredService<ICameraStore>();
        var camera = await cameras.AddAsync("gate", "rtsp://camera/gate", null, null, false, false, null, null, false, ct);
        return camera.Id;
    }
}
