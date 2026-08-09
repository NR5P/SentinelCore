extern alias WebHost;

using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// go2rtc is a sidecar process, and the failure mode that matters for one is leaking it.
//
// The sidecar is started lazily by whichever live tile asks for WebRTC first, which means it is
// started inside an HTTP request - and the token that request carries is RequestAborted. It used
// to be passed all the way into the startup wait loop, so a viewer who closed the tab during the
// second or so go2rtc takes to come up cancelled the wait *after* Process.Start had already
// returned but before the process was recorded in _process. Nothing then referenced a running
// go2rtc: it kept holding the API, RTSP and WebRTC ports, so every subsequent start attempt died
// unable to bind, WebRTC stayed off behind its 5-minute backoff, and Dispose could not clean up
// on the way out either - the stray process outlived the server.
//
// These drive the manager with a stub standing in for go2rtc (one that starts and then never
// answers its API, exactly like a sidecar mid-startup), so they assert the process bookkeeping
// without needing go2rtc installed.
public sealed class Go2RtcManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "go2rtc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _stubPath;
    private readonly string _pidFile;

    public Go2RtcManagerTests()
    {
        Directory.CreateDirectory(_root);
        _stubPath = Path.Combine(_root, "go2rtc-stub.sh");
        _pidFile = Path.Combine(_root, "stub.pid");

        // Starts, records its pid, never binds the API port, stays alive - a sidecar frozen in
        // the exact window this test is about.
        File.WriteAllText(_stubPath, $"""
            #!/bin/sh
            echo $$ > "{_pidFile}"
            while true; do sleep 1; done
            """);
        using var chmod = Process.Start("/bin/chmod", $"+x {_stubPath}")!;
        chmod.WaitForExit();
    }

    private Go2RtcManager CreateManager()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Go2Rtc:Path"] = _stubPath,
            ["CamerasDataDirectory"] = _root,
        }).Build();

        return new Go2RtcManager(
            configuration, new StubEnvironment(_root), new PlainHttpClientFactory(),
            NullLogger<Go2RtcManager>.Instance);
    }

    private static Camera TestCamera() => new(
        Guid.NewGuid(), "Test Camera", "rtsp://127.0.0.1/stream", null, null, false, false, null, null,
        false, DateTimeOffset.UtcNow);

    // The regression this file exists for.
    [Fact]
    public async Task AViewerDisconnectingDuringStartup_DoesNotAbandonTheSidecar()
    {
        var manager = CreateManager();
        using var viewerGoneAway = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        // Whatever the caller sees, it must not be at the cost of a stray process. The request
        // token no longer cancels the start at all, so this ends at the startup timeout.
        await Assert.ThrowsAnyAsync<Exception>(
            () => manager.ExchangeOfferAsync(TestCamera(), "v=0", viewerGoneAway.Token));

        Assert.True(File.Exists(_pidFile), "the stub sidecar never started - this test is not measuring what it thinks");
        var pid = ReadStubPid();

        manager.Dispose();
        await WaitForExitAsync(pid);

        Assert.False(IsAlive(pid), $"go2rtc (pid {pid}) outlived the request that started it and the manager that owned it");
    }

    // Shutting the server down mid-startup is the same leak by a different route: the process is
    // running but not yet recorded, so Dispose's own _process kill cannot see it.
    //
    // Asserts promptness, not just eventual death, and that is the whole point of the test. The
    // unfixed code did eventually kill this process - but only when the startup wait hit
    // StartupTimeout, because Dispose had no way to interrupt a wait it could not see. A server
    // stopping would hang on to the sidecar for the remainder of those ten seconds, which is
    // long enough for systemd or launchd to start the replacement and for that replacement to
    // find the ports still taken. Dispose has to cut the wait short, so the margin here is well
    // inside StartupTimeout.
    [Fact]
    public async Task DisposingWhileTheSidecarIsStillComingUp_KillsItPromptly()
    {
        var manager = CreateManager();
        var exchange = manager.ExchangeOfferAsync(TestCamera(), "v=0", CancellationToken.None);

        // Let it get as far as a running process that hasn't answered its API yet.
        while (!File.Exists(_pidFile))
            await Task.Delay(50, TestContext.Current.CancellationToken);
        var pid = ReadStubPid();
        Assert.True(IsAlive(pid));

        var stopwatch = Stopwatch.StartNew();
        manager.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => exchange);
        await WaitForExitAsync(pid);
        stopwatch.Stop();

        Assert.False(IsAlive(pid), $"go2rtc (pid {pid}) survived the shutdown of the server that spawned it");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Dispose took {stopwatch.Elapsed.TotalSeconds:F1}s to release the sidecar - it is waiting out the startup timeout rather than interrupting it");
    }

    private int ReadStubPid() => int.Parse(File.ReadAllText(_pidFile).Trim());

    private static async Task WaitForExitAsync(int pid)
    {
        for (var i = 0; i < 40 && IsAlive(pid); i++)
            await Task.Delay(50);
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    public void Dispose()
    {
        // Belt and braces: a failing test must not leave the stub running on the build machine.
        if (File.Exists(_pidFile) && int.TryParse(File.ReadAllText(_pidFile).Trim(), out var pid))
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(2) };
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
