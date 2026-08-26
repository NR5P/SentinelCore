extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// Rebooting the machine is the most destructive thing any client can ask this software to do, and
// unlike /api/system/restart it cannot lean on a supervisor to undo a mistake. So the tests that
// matter here are the refusals, and above all the one that re-checks the operating system at the
// moment of the request rather than trusting a setting saved months ago.
public sealed class HostRebootTests
{
    private static readonly string[] KnownReadinessValues =
        [HostRebootReadiness.Ready, HostRebootReadiness.SetupRequired, HostRebootReadiness.Unsupported];

    private sealed class StubMonitor(HostRebootOptions value) : IOptionsMonitor<HostRebootOptions>
    {
        public HostRebootOptions CurrentValue { get; } = value;
        public HostRebootOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<HostRebootOptions, string?> listener) => null;
    }

    private sealed class StubController(string readiness, string summary = "stub") : IHostRebootController
    {
        public int ExecuteCount { get; private set; }
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // For the case where the operating system takes the command and then doesn't go down.
        public HostRebootOutcome? ExecuteResult { get; init; }

        public Task<HostRebootProbe> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostRebootProbe
            {
                Platform = "linux",
                PlatformLabel = "Linux",
                Readiness = readiness,
                Enabled = true,
                RunningAs = "media",
                Summary = summary,
                DelaySeconds = 60
            });

        public Task<HostRebootOutcome> ExecuteAsync(CancellationToken cancellationToken)
        {
            ExecuteCount++;
            Executed.TrySetResult();
            return Task.FromResult(ExecuteResult ?? new HostRebootOutcome(true, "The machine is rebooting."));
        }
    }

    private static HostRebootScheduler CreateScheduler(
        IHostRebootController controller, bool enabled = true, int delaySeconds = 3600) =>
        new(controller,
            new StubMonitor(new HostRebootOptions { Enabled = enabled, DelaySeconds = delaySeconds }),
            NullLogger<HostRebootScheduler>.Instance);

    // Off by default is only meaningful if "off" is actually enforced somewhere other than by the
    // UI declining to draw a button.
    [Fact]
    public async Task Rebooting_IsRefusedWhileTheFeatureIsSwitchedOff()
    {
        var controller = new StubController(HostRebootReadiness.Ready);
        var scheduler = CreateScheduler(controller, enabled: false);

        var outcome = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Contains("switched off", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, controller.ExecuteCount);
    }

    // The pivotal one. A setting saved months ago says nothing about whether the sudoers file
    // survived the last OS upgrade, so readiness is re-established at the moment of the request -
    // and the operating system's own explanation is what comes back, not a generic failure.
    [Fact]
    public async Task Rebooting_IsRefusedWhenTheOperatingSystemNoLongerPermitsIt()
    {
        var controller = new StubController(
            HostRebootReadiness.SetupRequired,
            "media isn't allowed to run /usr/sbin/shutdown -r now without a password yet.");
        var scheduler = CreateScheduler(controller);

        var outcome = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Contains("without a password", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, controller.ExecuteCount);
        Assert.Null(scheduler.ScheduledFor);
    }

    [Fact]
    public async Task Rebooting_IsRefusedOnAPlatformThatCannotDoIt()
    {
        var controller = new StubController(HostRebootReadiness.Unsupported, "Running inside a container.");
        var scheduler = CreateScheduler(controller);

        var outcome = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Ok);
        Assert.Equal(0, controller.ExecuteCount);
    }

    // The undo window is the whole reason a delay exists, so cancelling inside it must genuinely
    // stop the machine going down - not merely stop the countdown being displayed.
    [Fact]
    public async Task Cancelling_InsideTheWindow_NeverReachesTheOperatingSystem()
    {
        var controller = new StubController(HostRebootReadiness.Ready);
        var scheduler = CreateScheduler(controller, delaySeconds: 3600);

        var scheduled = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);
        Assert.True(scheduled.Ok);
        Assert.NotNull(scheduler.ScheduledFor);

        var cancelled = scheduler.Cancel();

        Assert.True(cancelled.Ok);
        Assert.Null(scheduler.ScheduledFor);
        Assert.Equal(0, controller.ExecuteCount);
    }

    [Fact]
    public async Task TheWindowElapsing_IsWhatActuallyReboots()
    {
        var controller = new StubController(HostRebootReadiness.Ready);
        var scheduler = CreateScheduler(controller, delaySeconds: 0);

        var outcome = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);
        Assert.True(outcome.Ok);

        await controller.Executed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, controller.ExecuteCount);
    }

    // Once the command has gone to the operating system nothing in this process can call it back,
    // so a cancel that arrives late has to say so. Reporting "Reboot cancelled" here would be the
    // worst answer this feature could give: the machine goes down anyway, and the person who hit
    // cancel has been told it won't.
    [Fact]
    public async Task Cancelling_AfterTheCommandHasGoneOut_RefusesInsteadOfLying()
    {
        var controller = new StubController(HostRebootReadiness.Ready);
        var scheduler = CreateScheduler(controller, delaySeconds: 0);

        await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);
        await controller.Executed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var outcome = scheduler.Cancel();

        Assert.False(outcome.Ok);
        Assert.Contains("Too late", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The operating system can accept the request and still not go down - a revoked sudoers rule
    // between the probe and the deadline, say. The countdown has to end rather than hang there
    // forever, or the machine looks like it is permanently three seconds from rebooting and no
    // second attempt is possible.
    [Fact]
    public async Task ARebootThatDoesntHappen_ClearsTheCountdownSoItCanBeTriedAgain()
    {
        var controller = new StubController(HostRebootReadiness.Ready)
        {
            ExecuteResult = new HostRebootOutcome(false, "shutdown: not permitted")
        };
        var scheduler = CreateScheduler(controller, delaySeconds: 0);

        await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);
        await controller.Executed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The state is cleared just after ExecuteAsync returns, so allow the scheduler's task a
        // moment to finish rather than racing it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (scheduler.ScheduledFor is not null && DateTime.UtcNow < deadline)
            await Task.Delay(25, TestContext.Current.CancellationToken);

        Assert.Null(scheduler.ScheduledFor);

        // And a fresh request is a fresh countdown, not "already counting down".
        var retry = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);
        Assert.True(retry.Ok);
        Assert.DoesNotContain("already", retry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancelling_WithNothingScheduled_SaysSoRatherThanClaimingSuccess()
    {
        var scheduler = CreateScheduler(new StubController(HostRebootReadiness.Ready));

        var outcome = scheduler.Cancel();

        Assert.False(outcome.Ok);
        Assert.Contains("No reboot", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASecondRequest_JoinsTheCountdownInsteadOfStartingAnother()
    {
        var controller = new StubController(HostRebootReadiness.Ready);
        var scheduler = CreateScheduler(controller, delaySeconds: 3600);

        var first = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);
        var second = await scheduler.ScheduleAsync(TestContext.Current.CancellationToken);

        Assert.True(second.Ok);
        Assert.Equal(first.ScheduledFor, scheduler.ScheduledFor);
        Assert.Contains("already", second.Message, StringComparison.OrdinalIgnoreCase);

        scheduler.Cancel();
    }

    // Every client pulls the reason for a failure out of an "error" field; Message alone would
    // leave three of them showing a blank refusal.
    [Fact]
    public void AFailedOutcome_CarriesItsReasonWhereTheClientsLookForIt()
    {
        Assert.Equal("nope", new HostRebootOutcome(false, "nope").Error);
        Assert.Null(new HostRebootOutcome(true, "on its way").Error);
    }

    // Counted server-side precisely so a client with a skewed clock can't render a confidently
    // wrong countdown.
    [Fact]
    public void SecondsRemaining_IsDerivedFromTheDeadline_NotTheClientClock()
    {
        var probe = new HostRebootProbe
        {
            Platform = "linux",
            PlatformLabel = "Linux",
            Readiness = HostRebootReadiness.Ready,
            Enabled = true,
            RunningAs = "media",
            Summary = "ready",
            DelaySeconds = 60,
            ScheduledFor = DateTimeOffset.UtcNow.AddSeconds(42)
        };

        Assert.InRange(probe.SecondsRemaining!.Value, 40, 42);
        Assert.Null((probe with { ScheduledFor = null }).SecondsRemaining);

        // Already past is reported as zero rather than a negative countdown.
        var elapsed = probe with { ScheduledFor = DateTimeOffset.UtcNow.AddSeconds(-5) };
        Assert.Equal(0, elapsed.SecondsRemaining);
    }

    // The real platform implementation, on whatever this test run is happening to sit on. It has
    // no business throwing, and it must never reboot anything merely for being asked what it can
    // do - the probe being side-effect-free is what lets every client call it on every appearance.
    [Fact]
    public async Task TheRealProbe_AnswersWithoutThrowingAndWithoutRebootingAnything()
    {
        var controller = HostRebootControllerFactory.Create(
            new StubMonitor(new HostRebootOptions { Enabled = false }));

        var probe = await controller.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Contains(probe.Readiness, KnownReadinessValues);
        Assert.False(string.IsNullOrWhiteSpace(probe.Summary));
        Assert.False(string.IsNullOrWhiteSpace(probe.PlatformLabel));

        // Whenever it says setup is needed it must also say what to do about it - a dead end here
        // would leave the person with no way forward at all.
        if (probe.Readiness == HostRebootReadiness.SetupRequired)
            Assert.False(string.IsNullOrWhiteSpace(probe.SetupScript));
    }
}

// The dashboard's half: three Admin-only routes that forward to the main app. The main app isn't
// running in these tests, which is exactly the condition worth pinning - an unreachable bridge
// must surface as an error, never as a reassuring empty answer.
public sealed class HostRebootProxyTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public HostRebootProxyTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"host-reboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, """
            { "Bridge": { "ApiKey": "a-long-enough-stored-api-key" } }
            """);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

    [Fact]
    public async Task TheProbe_ReportsTheMainAppUnreachable_RatherThanPretending()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/system/host-reboot", ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task RebootAndCancel_AlsoReportTheMainAppUnreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var reboot = await client.PostAsync("/api/system/host-reboot", content: null, ct);
        Assert.Equal(HttpStatusCode.BadGateway, reboot.StatusCode);

        using var cancel = await client.PostAsync("/api/system/host-reboot/cancel", content: null, ct);
        Assert.Equal(HttpStatusCode.BadGateway, cancel.StatusCode);
    }

    // These three routes are AdminOnly, but on an install with the dashboard login switched off -
    // the shipped default - AuthGateMiddleware hands every caller a synthetic Admin, which is what
    // the tests above run under. So the policy being real is a separate fact needing its own test:
    // with login on and no session, rebooting the machine must be refused outright.
    //
    // This was found the hard way. The same tests passed here and failed on the deployed server,
    // because the server's config has Auth enabled and the test host was reading it - see
    // WebHostTestSettings.
    [Fact]
    public async Task WithLoginOn_AnUnauthenticatedCaller_CannotReachAnyOfThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_settingsPath, """
            {
              "Bridge": { "ApiKey": "a-long-enough-stored-api-key" },
              "Auth": { "Enabled": true, "Username": "someone", "PasswordHash": "irrelevant" }
            }
            """, ct);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var probe = await client.GetAsync("/api/system/host-reboot", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);

        using var reboot = await client.PostAsync("/api/system/host-reboot", content: null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, reboot.StatusCode);

        using var cancel = await client.PostAsync("/api/system/host-reboot/cancel", content: null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, cancel.StatusCode);
    }

    // The settings half round-trips like every other section - and comes back off by default on an
    // install whose appsettings.json predates the feature, rather than as an empty form.
    [Fact]
    public async Task TheSettingsSection_RoundTripsAndDefaultsToOff()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var initial = await client.GetAsync("/api/settings", ct);
        initial.EnsureSuccessStatusCode();
        using (var document = JsonDocument.Parse(await initial.Content.ReadAsStringAsync(ct)))
        {
            var section = document.RootElement.GetProperty("hostReboot");
            Assert.False(section.GetProperty("enabled").GetBoolean());
            Assert.Equal(60, section.GetProperty("delaySeconds").GetInt32());
        }

        using var save = await client.PutAsJsonAsync(
            "/api/settings/host-reboot", new { Enabled = true, DelaySeconds = 30 }, ct);
        save.EnsureSuccessStatusCode();

        using var reloaded = await client.GetAsync("/api/settings", ct);
        using var saved = JsonDocument.Parse(await reloaded.Content.ReadAsStringAsync(ct));
        var stored = saved.RootElement.GetProperty("hostReboot");
        Assert.True(stored.GetProperty("enabled").GetBoolean());
        Assert.Equal(30, stored.GetProperty("delaySeconds").GetInt32());
    }

    [Fact]
    public async Task AnOutOfRangeUndoWindow_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            "/api/settings/host-reboot", new { Enabled = true, DelaySeconds = 99999 }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
