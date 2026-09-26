using System.Net.Http;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

public class AppModelTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "sentinel-tests", Guid.NewGuid().ToString("N"));

    private AppModel NewModel()
    {
        Directory.CreateDirectory(_directory);
        return new AppModel(
            new PreferenceStore(Path.Combine(_directory, "settings.json")),
            new CredentialStore(Path.Combine(_directory, "credentials")));
    }

    [Fact]
    public void A_blank_address_is_not_a_configured_server()
    {
        using var model = NewModel();

        Assert.False(model.IsConfigured);
        Assert.False(model.HasRemoteServerUrl);

        model.ServerUrl = "http://192.168.1.50:3320";
        Assert.True(model.IsConfigured);

        // A half-typed address is not one either - which is what keeps the setup screen up rather
        // than showing an empty Events tab that will never fill.
        model.ServerUrl = "192.168.1.50";
        Assert.False(model.IsConfigured);
    }

    [Fact]
    public void The_server_address_survives_a_restart()
    {
        var preferencesPath = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);

        using (var first = new AppModel(new PreferenceStore(preferencesPath), new CredentialStore(_directory)))
        {
            first.ServerUrl = "http://192.168.1.50:3320";
            first.RemoteServerUrl = "https://sentinel.example.com";
            first.DesktopAlertsEnabled = false;
        }

        using var second = new AppModel(new PreferenceStore(preferencesPath), new CredentialStore(_directory));

        Assert.Equal("http://192.168.1.50:3320", second.ServerUrl);
        Assert.Equal("https://sentinel.example.com", second.RemoteServerUrl);
        Assert.False(second.DesktopAlertsEnabled);
    }

    [Fact]
    public void An_absent_role_reads_as_Admin()
    {
        // Matches the server's own AuthGateMiddleware treatment of "no login required" as full
        // access for everyone - and every pre-upgrade single-account login effectively was Admin.
        using var model = NewModel();

        Assert.True(model.IsAdmin);
    }

    [Fact]
    public void The_live_grid_puts_saved_placements_first_and_new_cameras_last()
    {
        using var model = NewModel();
        var a = NewCamera("A");
        var b = NewCamera("B");
        var c = NewCamera("C");
        foreach (var camera in new[] { a, b, c }) model.Cameras.Add(camera);

        model.MoveLiveCamera(c.Id, a.Id);

        // C was dragged onto A's tile, so it takes that place and the rest close up behind it.
        Assert.Equal(new[] { "C", "A", "B" }, model.LiveCameras.Select(camera => camera.Name));
    }

    [Fact]
    public void A_camera_the_saved_order_has_never_seen_is_appended_rather_than_dropped()
    {
        using var model = NewModel();
        var a = NewCamera("A");
        var b = NewCamera("B");
        model.Cameras.Add(a);
        model.Cameras.Add(b);
        model.MoveLiveCamera(b.Id, a.Id);

        // A camera added since the order was saved: it has to appear, at the end.
        var c = NewCamera("C");
        model.Cameras.Add(c);

        Assert.Equal(new[] { "B", "A", "C" }, model.LiveCameras.Select(camera => camera.Name));
    }

    [Fact]
    public void Reordering_ignores_a_camera_that_is_no_longer_there()
    {
        using var model = NewModel();
        model.Cameras.Add(NewCamera("A"));

        // Neither id is in the grid; this must be a no-op, not an exception out of a drop handler.
        model.MoveLiveCamera(Guid.NewGuid(), Guid.NewGuid());

        Assert.Single(model.LiveCameras);
    }

    [Fact]
    public void The_first_refresh_only_seeds_the_notified_set()
    {
        // Connecting to a server with a day of history behind it must not fire a hundred desktop
        // notifications at once.
        using var model = NewModel();
        var history = new[] { Notified("1"), Notified("2"), NotNotified("3") };

        Assert.Empty(model.NewlyNotified(history));
    }

    [Fact]
    public void Only_notified_alerts_that_are_new_since_the_last_refresh_are_raised()
    {
        using var model = NewModel();
        model.NewlyNotified(new[] { Notified("1") });

        var fresh = model.NewlyNotified(new[] { Notified("2"), Notified("1"), NotNotified("3") });

        var raised = Assert.Single(fresh);
        Assert.Equal("2", raised.Id);
    }

    [Fact]
    public void An_alert_already_raised_is_not_raised_again_on_the_next_refresh()
    {
        using var model = NewModel();
        model.NewlyNotified(new[] { Notified("1") });
        model.NewlyNotified(new[] { Notified("2"), Notified("1") });

        Assert.Empty(model.NewlyNotified(new[] { Notified("2"), Notified("1") }));
    }

    [Fact]
    public void Motion_the_AI_never_looked_at_never_counts_as_an_alert()
    {
        using var model = NewModel();
        model.NewlyNotified(Array.Empty<ActivityItem>());

        Assert.Empty(model.NewlyNotified(new[] { NotNotified("motion-1") }));
    }

    [Fact]
    public void Events_are_the_analyzed_half_of_the_activity_list()
    {
        using var model = NewModel();
        model.Activity.Add(new ActivityItem
        {
            Id = "8f14e45f-ceea-467a-9575-9f9a4b3d1f2c",
            Kind = "analyzed",
            Camera = "Front Door",
            Summary = "A courier left a parcel.",
        });
        model.Activity.Add(new ActivityItem { Id = "trigger:abc", Kind = "motion", Camera = "Garden" });

        // Motion rows have no alert id and correctly never appear here.
        var alert = Assert.Single(model.Events);
        Assert.Equal("Front Door", alert.Camera);
    }

    [Fact]
    public void A_connection_attempt_with_no_server_configured_says_so_rather_than_failing()
    {
        using var model = NewModel();

        model.ConnectAsync().GetAwaiter().GetResult();

        Assert.Equal(ConnectionState.NotConfigured, model.ConnectionState);
    }

    [Fact]
    public async Task Connecting_to_a_server_that_needs_a_login_stops_at_the_sign_in_gate()
    {
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":true,"configured":true,"authenticated":false}""")
            .OnGet("/api/health", """{"status":"ok","build":{"version":"1.0.0","commit":"abc","isDirty":false,"display":"1.0.0"}}""");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ConnectionState.AuthenticationRequired, model.ConnectionState);
        Assert.True(model.RequiresLogin);
        // The events list was never asked for - there is no session to ask with.
        Assert.DoesNotContain(server.Requests, request => request.StartsWith("GET /api/activity"));
    }

    [Fact]
    public async Task A_full_connect_loads_the_lists_every_screen_renders()
    {
        using var server = FullyStockedServer();
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ConnectionState.Online, model.ConnectionState);
        Assert.Single(model.Activity);
        Assert.Single(model.Cameras);
        Assert.True(model.DeviceHubEnabled);
        Assert.Single(model.Devices);
        Assert.True(model.MoviesEnabled);
        Assert.NotNull(model.LastUpdated);
        Assert.Null(model.LastError);
    }

    [Fact]
    public async Task A_server_without_a_Device_Hub_is_not_a_failed_connection()
    {
        // An older server has no such endpoint, and a 404 there must not read as a failed
        // connection - it just means no Device Hub.
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":false,"configured":false,"authenticated":true,"role":"Admin"}""")
            .OnGet("/api/health", """{"status":"ok"}""")
            .OnGet("/api/activity", "[]")
            .OnGet("/api/cameras", "[]");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ConnectionState.Online, model.ConnectionState);
        Assert.False(model.DeviceHubEnabled);
        Assert.Empty(model.Devices);
    }

    [Fact]
    public async Task A_server_without_Movies_is_not_a_failed_connection()
    {
        // A server older than the Movies tab answers /api/media/enabled with a 404. That means "no
        // Movies tab", never a failed connection.
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":false,"configured":false,"authenticated":true,"role":"Admin"}""")
            .OnGet("/api/health", """{"status":"ok"}""")
            .OnGet("/api/activity", "[]")
            .OnGet("/api/cameras", "[]");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ConnectionState.Online, model.ConnectionState);
        Assert.False(model.MoviesEnabled);
        Assert.Null(model.LastError);
    }

    [Fact]
    public async Task Movies_switched_off_on_the_server_hides_the_tab()
    {
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":false,"configured":false,"authenticated":true,"role":"Admin"}""")
            .OnGet("/api/health", """{"status":"ok"}""")
            .OnGet("/api/activity", "[]")
            .OnGet("/api/cameras", "[]")
            .OnGet("/api/media/enabled", """{"enabled":false}""");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ConnectionState.Online, model.ConnectionState);
        Assert.False(model.MoviesEnabled);
    }

    [Fact]
    public async Task A_server_without_Family_Files_is_not_a_failed_connection()
    {
        // Same rule as the Device Hub: no /api/family-files/status on an older server means no
        // Files section, and nothing more.
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":false,"configured":false,"authenticated":true,"role":"Admin"}""")
            .OnGet("/api/health", """{"status":"ok"}""")
            .OnGet("/api/activity", "[]")
            .OnGet("/api/cameras", "[]");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ConnectionState.Online, model.ConnectionState);
        Assert.False(model.FamilyFilesEnabled);
        Assert.Null(model.LastError);
    }

    [Fact]
    public async Task A_server_with_Family_Files_switched_on_gets_the_Files_section()
    {
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":false,"configured":false,"authenticated":true,"role":"Viewer"}""")
            .OnGet("/api/health", """{"status":"ok"}""")
            .OnGet("/api/activity", "[]")
            .OnGet("/api/cameras", "[]")
            .OnGet("/api/family-files/status", """
                {"enabled":true,"isAdmin":false,"username":"kid","maxUploadBytes":1,
                 "library":{"files":0,"bytes":0},"drives":[],"nightly":{"hour":2,"minute":0,"removeDuplicates":false,
                 "trashKeepDays":30,"lastResult":"","lastMessage":""},"duplicates":null,"trash":{"batches":0,"files":0,"bytes":0},"job":null}
                """);
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.True(model.FamilyFilesEnabled);
    }

    [Fact]
    public async Task A_licence_check_that_could_not_be_made_leaves_the_watermark_off()
    {
        // An older server has no such endpoint, and a request that simply failed says nothing about
        // the licence. Accusing a paying customer over a dropped request would be the worse bug.
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":false,"configured":false,"authenticated":true,"role":"Admin"}""")
            .OnGet("/api/health", """{"status":"ok"}""")
            .OnGet("/api/activity", "[]")
            .OnGet("/api/cameras", "[]");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.True(model.IsLicensed);
    }

    [Fact]
    public async Task An_unlicensed_server_turns_the_watermark_on()
    {
        using var server = FullyStockedServer(license: """{"valid":false}""");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.False(model.IsLicensed);

        // And installing one clears it straight away rather than at the next full refresh.
        model.ApplyLicenseState(new LicenseState { Valid = true });
        Assert.True(model.IsLicensed);
    }

    [Fact]
    public async Task A_server_too_old_to_report_its_version_raises_an_advisory_but_still_connects()
    {
        using var server = FullyStockedServer(health: """{"status":"ok"}""");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        // Advisory, never blocking - the app works normally behind it.
        Assert.Equal(ConnectionState.Online, model.ConnectionState);
        Assert.Equal(ServerCompatibilityKind.PredatesVersionReporting, model.ServerCompatibility.Kind);
        Assert.NotNull(model.CompatibilityAdvisory);
    }

    [Fact]
    public async Task A_current_server_raises_no_advisory_at_all()
    {
        using var model = NewModel();
        using var server = FullyStockedServer();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ServerCompatibilityKind.Ok, model.ServerCompatibility.Kind);
        Assert.Null(model.CompatibilityAdvisory);
    }

    [Fact]
    public async Task A_health_endpoint_that_did_not_answer_stays_silent_rather_than_guessing()
    {
        // A transient blip on a working connection must not raise a scary banner.
        using var server = new FakeServer()
            .OnGet("/api/auth/status", """{"enabled":false,"configured":false,"authenticated":true,"role":"Admin"}""")
            .OnGet("/api/activity", "[]")
            .OnGet("/api/cameras", "[]");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.ConnectAsync();

        Assert.Equal(ServerCompatibilityKind.Unknown, model.ServerCompatibility.Kind);
        Assert.Null(model.CompatibilityAdvisory);
    }

    [Fact]
    public async Task An_unreachable_server_goes_offline_with_a_message_that_names_a_fix()
    {
        // A port that is definitely closed on this machine, obtained by taking one and giving it
        // back. A hard-coded address instead (127.0.0.2:9) is refused on Windows but silently
        // dropped on macOS and Linux, so the same test measured two different things depending on
        // where it ran - and the slow one took the client's full request timeout to do it.
        string address;
        using (var server = new FakeServer()) address = server.BaseUrl;

        using var model = NewModel();
        model.ServerUrl = address;

        await model.ConnectAsync();

        Assert.Equal(ConnectionState.Offline, model.ConnectionState);
        Assert.NotNull(model.LastError);
        Assert.Contains("firewall", model.LastError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A request that ran out of time must fail the connection, not be mistaken for the app
    /// shutting down.
    /// </summary>
    /// <remarks>
    /// These arrive as the same exception type. HttpClient reports its own timeout as a
    /// TaskCanceledException, and treating every cancellation as a lifecycle stop left the app
    /// sitting on CONNECTING for ever - no error, no Offline badge - whenever a server was
    /// reachable but silent, which is exactly what a firewall that drops rather than refuses looks
    /// like. Tested against the predicate directly because reproducing it end to end costs the
    /// full request timeout in wall-clock, and because a closed port (the usual stand-in for an
    /// unreachable server) answers with a reset and never exercises this path at all.
    /// </remarks>
    [Fact]
    public void A_request_that_timed_out_is_not_mistaken_for_the_app_shutting_down()
    {
        var live = CancellationToken.None;

        // What HttpClient throws when its own timeout elapses.
        var timedOut = new TaskCanceledException("timed out", new TimeoutException());
        Assert.False(AppModel.IsLifecycleCancellation(timedOut, live));

        // Even without the inner TimeoutException, nothing asked this to stop.
        Assert.False(AppModel.IsLifecycleCancellation(new TaskCanceledException(), live));

        // A genuine shutdown: the caller's token is what says so.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.True(AppModel.IsLifecycleCancellation(new OperationCanceledException(), cancelled.Token));

        // And a timeout stays a timeout even while shutting down, so it is still reported.
        Assert.False(AppModel.IsLifecycleCancellation(timedOut, cancelled.Token));

        // Anything that is not a cancellation at all is simply not one.
        Assert.False(AppModel.IsLifecycleCancellation(new HttpRequestException("nope"), cancelled.Token));
    }

    [Fact]
    public async Task Signing_in_saves_the_login_so_the_next_launch_renews_itself()
    {
        using var server = FullyStockedServer(
            authStatus: """{"enabled":true,"configured":true,"authenticated":true,"username":"admin","role":"Admin"}""")
            .OnSetsCookie("POST", "/api/auth/login", "SentinelAuth=abc; Path=/");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        await model.SignInAsync("admin", "hunter2");

        Assert.Equal(ConnectionState.Online, model.ConnectionState);

        var store = new CredentialStore(Path.Combine(_directory, "credentials"));
        var saved = store.Load(server.BaseUrl);
        Assert.Equal("admin", saved?.Username);
        Assert.Equal("hunter2", saved?.Password);
    }

    [Fact]
    public async Task Signing_out_forgets_the_saved_login()
    {
        using var server = FullyStockedServer(
            authStatus: """{"enabled":true,"configured":true,"authenticated":false}""")
            .OnSetsCookie("POST", "/api/auth/login", "SentinelAuth=abc; Path=/")
            .On("POST", "/api/auth/logout", "{}");
        using var model = NewModel();
        model.ServerUrl = server.BaseUrl;

        var store = new CredentialStore(Path.Combine(_directory, "credentials"));
        store.Save(new StoredCredential("admin", "hunter2"), server.BaseUrl);

        await model.SignOutAsync();

        Assert.Null(store.Load(server.BaseUrl));
        Assert.Equal(ConnectionState.AuthenticationRequired, model.ConnectionState);
    }

    [Fact]
    public void The_diagnostics_log_keeps_the_most_recent_hundred_lines()
    {
        using var model = NewModel();

        for (var i = 0; i < 150; i++) model.Log($"line {i}");

        Assert.Equal(100, model.Diagnostics.Count);
        Assert.Equal("line 149", model.Diagnostics[0].Message);   // newest first
        Assert.Equal("line 50", model.Diagnostics[^1].Message);

        model.ClearDiagnostics();
        Assert.Empty(model.Diagnostics);
    }

    private static FakeServer FullyStockedServer(
        string? authStatus = null,
        string? health = null,
        string? license = null)
    {
        return new FakeServer()
            .OnGet("/api/auth/status",
                authStatus ?? """{"enabled":false,"configured":false,"authenticated":true,"role":"Admin"}""")
            .OnGet("/api/health",
                health ?? """{"status":"ok","build":{"version":"1.0.0","commit":"abc","isDirty":false,"display":"1.0.0"}}""")
            .OnGet("/api/activity", """
                [{"id":"8f14e45f-ceea-467a-9575-9f9a4b3d1f2c","kind":"analyzed","camera":"Front Door",
                  "occurredAt":"2026-08-25T09:14:03Z","status":"ok","notified":true,"summary":"A courier."}]
                """)
            .OnGet("/api/cameras", """
                [{"id":"1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed","name":"Front Door","streamUrl":"rtsp://x"}]
                """)
            .OnGet("/api/live-order", """{"cameraIds":["1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed"]}""")
            .OnGet("/api/device-hub/enabled", """{"enabled":true}""")
            .OnGet("/api/devices", """
                [{"id":"3f2504e0-4f89-41d3-9a0c-0305e82c3301","name":"Porch light","kind":"light","room":"Outside",
                  "enabled":true,"commandTopic":"a","stateTopic":"b","availabilityTopic":"","payloadOn":"ON",
                  "payloadOff":"OFF","payloadAvailable":"online","payloadNotAvailable":"offline","schema":"raw",
                  "stateValueTemplate":"","optimistic":false,"supportsBrightness":true,"brightnessCommandTopic":"c",
                  "brightnessStateTopic":"d","brightnessScale":255,"discoverySource":"manual","power":"off",
                  "available":true,"commandable":true}]
                """)
            .OnGet("/api/devices/state", """
                {"connected":true,"devices":[{"id":"3f2504e0-4f89-41d3-9a0c-0305e82c3301","power":"on",
                 "brightness":128,"available":true}]}
                """)
            .OnGet("/api/media/enabled", """{"enabled":true}""")
            .OnGet("/api/license/status", license ?? """{"valid":true,"customerName":"Acme"}""")
            .OnGet("/api/push/status",
                """{"enabled":false,"appleConfigured":false,"firebaseConfigured":false,"registeredDevices":0}""");
    }

    private static Models.Camera NewCamera(string name) => new() { Id = Guid.NewGuid(), Name = name };

    private static ActivityItem Notified(string id) => new()
    {
        Id = id,
        Kind = "analyzed",
        Camera = "Front Door",
        Notified = true,
    };

    private static ActivityItem NotNotified(string id) => new()
    {
        Id = id,
        Kind = "motion",
        Camera = "Garden",
        Notified = false,
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
