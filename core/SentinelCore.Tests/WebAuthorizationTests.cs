extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AccountRole = SentinelCore.Web.AccountRole;
using IAccountStore = SentinelCore.Web.IAccountStore;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// End-to-end coverage of the multi-account authorization refactor - the piece with the highest
// blast radius if wrong (a bug here could mean either "everyone locked out" or "no gating at
// all"). Both project references are top-level-statement executables and each therefore has its
// own global::Program, hence the extern alias (see the Aliases="global,WebHost" on the .Web
// ProjectReference in this project's .csproj).
public sealed class WebAuthorizationTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public WebAuthorizationTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"web-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            // The committed appsettings.json bakes in this developer's own machine path for the
            // main bridge's data directory - override it so SqliteEventStore doesn't try to
            // create a directory under someone else's home folder during tests.
            .UseSetting("Bridge:DataDirectory", _dataDirectory));

    [Fact]
    public async Task AuthTurnedOff_AnEndpointThatWouldOtherwiseRequireAdmin_IsFullyOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_settingsPath, WebHostTestSettings.LoginOffSettings, ct);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // /api/settings is Admin-only once login is required. An explicit Auth:Enabled=false -
        // what turning the login off in SETTINGS writes - is the one way to open it to everyone.
        using var response = await client.GetAsync("/api/settings", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A fresh install ships with no Auth section at all. That has to mean "locked", or anyone on
    // the network - or any web page someone there opens - administers the server until its owner
    // gets round to setting a password.
    [Fact]
    public async Task FreshInstall_RequiresLogin_AndOffersOnlyTheFirstAdminAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using (var settings = await client.GetAsync("/api/settings", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);
        using (var events = await client.GetAsync("/api/events", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, events.StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status", ct);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("authenticated").GetBoolean());
    }

    // The apps can't create the first account, so signing in to an empty server has to say where
    // to go rather than "wrong password".
    [Fact]
    public async Task FreshInstall_SigningIn_SaysToCreateTheAdminAccountInTheDashboard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = "anyone", Password = "whatever-it-is" }, ct);

        // Not 401: every client swaps a 401 for its own "sign in" text and would hide this one.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(SentinelCore.Web.AuthEndpoints.NoAdminYetMessage, payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task FreshInstall_CreatingTheFirstAdmin_SignsThemIn_AndClosesTheDoorBehindThem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var owner = factory.CreateClient();

        using (var created = await owner.PostAsJsonAsync("/api/auth/enable", new { Username = "owner", Password = "correct horse battery" }, ct))
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using (var settings = await owner.GetAsync("/api/settings", ct))
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        // A second anonymous caller can no longer mint an admin for themselves.
        using var stranger = factory.CreateClient();
        using (var second = await stranger.PostAsJsonAsync("/api/auth/enable", new { Username = "intruder", Password = "correct horse battery" }, ct))
            Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        using (var intruderLogin = await stranger.PostAsJsonAsync("/api/auth/login", new { Username = "intruder", Password = "correct horse battery" }, ct))
            Assert.Equal(HttpStatusCode.Unauthorized, intruderLogin.StatusCode);

        using var scope = factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        Assert.NotNull(await accounts.GetByUsernameAsync("owner", ct));
        Assert.Null(await accounts.GetByUsernameAsync("intruder", ct));
    }

    [Fact]
    public async Task FreshInstall_TheFirstAdminCannotBeCreatedFromOutsideTheLocalNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(IPAddress.Parse("203.0.113.9")))));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/auth/enable", new { Username = "owner", Password = "correct horse battery" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IAccountStore>().AnyAccountsExistAsync(ct));
    }

    // TestServer leaves RemoteIpAddress unset, which counts as local; this pins it to a public one.
    private sealed class RemoteAddressStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task AuthEnabled_NoCookie_GetsUnauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/events", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The WebRTC signaling endpoint is a new way to open a live media session, so it must sit
    // behind exactly the same login as the HLS playlist it replaces - and stay reachable for a
    // Viewer, since watching live video is the one thing a Viewer account is for.
    [Fact]
    public async Task WebRtcSignaling_IsGatedByLoginExactlyLikeTheStreamItReplaces()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_settingsPath, WebHostTestSettings.LoginOffSettings, ct);
        using var factory = CreateFactory();

        // Login turned off: open, like every other camera endpoint. 404 is the proof
        // authorization let the request through - the camera genuinely doesn't exist.
        using (var client = factory.CreateClient())
        {
            using var open = await client.PostAsync($"/api/cameras/{Guid.NewGuid()}/webrtc", new StringContent("v=0"), ct);
            Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        using (var client = factory.CreateClient())
        {
            using var anonymous = await client.PostAsync($"/api/cameras/{Guid.NewGuid()}/webrtc", new StringContent("v=0"), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    [Fact]
    public async Task AuthEnabled_ViewerSession_CanMonitorButNotAdminister()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
            await accounts.CreateAsync("viewer", SentinelCore.Web.PasswordHasher.Hash("watch-only-please"), AccountRole.Viewer, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = "viewer", Password = "watch-only-please" }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var monitoring = await client.GetAsync("/api/events", ct);
        Assert.Equal(HttpStatusCode.OK, monitoring.StatusCode);

        using var administering = await client.GetAsync("/api/settings", ct);
        Assert.Equal(HttpStatusCode.Forbidden, administering.StatusCode);
    }

    [Fact]
    public async Task AuthEnabled_AdminSession_CanReachBothMonitoringAndAdminRoutes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "correct horse battery" }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var monitoring = await client.GetAsync("/api/events", ct);
        Assert.Equal(HttpStatusCode.OK, monitoring.StatusCode);

        using var administering = await client.GetAsync("/api/settings", ct);
        Assert.Equal(HttpStatusCode.OK, administering.StatusCode);
    }

    // The Device Hub deliberately splits differently from every other feature: a Viewer can
    // switch a light on (that's the point of the tab for a household member), but only an Admin
    // can point a device at an arbitrary MQTT topic or sweep the whole broker.
    [Fact]
    public async Task AuthEnabled_ViewerSession_CanReadAndCommandDevicesButNotConfigureThem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
            await accounts.CreateAsync("viewer", SentinelCore.Web.PasswordHasher.Hash("watch-only-please"), AccountRole.Viewer, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = "viewer", Password = "watch-only-please" }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Allowed for a Viewer. These forward to the main app, which isn't running under this
        // test host, so the proxy's own 502/503 is the success signal here - what matters is
        // that authorization let the request through rather than short-circuiting to 403.
        foreach (var path in new[] { "/api/device-hub/enabled", "/api/devices", "/api/devices/state" })
        {
            using var allowed = await client.GetAsync(path, ct);
            Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        }

        using var command = await client.PostAsJsonAsync($"/api/devices/{Guid.NewGuid()}/command", new { Action = "on" }, ct);
        Assert.NotEqual(HttpStatusCode.Forbidden, command.StatusCode);

        // Refused for a Viewer.
        using var create = await client.PostAsJsonAsync("/api/devices", new { Name = "Sneaky" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        using var update = await client.PutAsJsonAsync($"/api/devices/{Guid.NewGuid()}", new { Name = "Sneaky" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);

        using var delete = await client.DeleteAsync($"/api/devices/{Guid.NewGuid()}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        using var discover = await client.PostAsync("/api/devices/discover", content: null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, discover.StatusCode);

        using var discoverStatus = await client.GetAsync($"/api/devices/discover/{Guid.NewGuid()}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, discoverStatus.StatusCode);
    }

    // Every client draws the UNLICENSED watermark over camera images and live video, so every
    // signed-in account has to be able to read the licence state - otherwise the nag would appear
    // on the Admin's devices and silently vanish on a household Viewer's phone. Installing a
    // licence is a different matter and stays Admin-only.
    [Fact]
    public async Task AuthEnabled_ViewerSession_CanReadLicenseStatusButNotInstallALicense()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
            await accounts.CreateAsync("viewer", SentinelCore.Web.PasswordHasher.Hash("watch-only-please"), AccountRole.Viewer, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = "viewer", Password = "watch-only-please" }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var status = await client.GetAsync("/api/license/status", ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        using var install = await client.PostAsync("/api/license/install", new StringContent("not a licence"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, install.StatusCode);
    }

    [Fact]
    public async Task AuthEnabled_NoCookie_CannotReadLicenseStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/license/status", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthEnabled_NoCookie_CannotCommandADevice()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/devices/{Guid.NewGuid()}/command", new { Action = "on" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthEnabled_SoleAdmin_CannotDemoteOrDeleteThemselves()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        Guid adminId;
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            var admin = await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
            adminId = admin.Id;
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = "correct horse battery" }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var demote = await client.PutAsJsonAsync($"/api/accounts/{adminId}/role", new { Role = "Viewer" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);

        using var delete = await client.DeleteAsync($"/api/accounts/{adminId}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    }

    // Account creation stores the username trimmed, so signing in has to trim too - otherwise a
    // phone keyboard that leaves a trailing space in the field reports the password as wrong,
    // which is unguessable from the user's side. The password is deliberately not trimmed: a
    // leading or trailing space there is a real character someone may have chosen.
    [Fact]
    public async Task AuthEnabled_UsernameWithSurroundingWhitespace_StillSignsIn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = " admin ", Password = "correct horse battery" }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var padded = await client.PostAsJsonAsync("/api/auth/login", new { Username = "admin", Password = " correct horse battery " }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, padded.StatusCode);
    }

    // Changing your password is what someone does when they think another person is in their
    // account, so it has to remove that person. Sessions last 30 days and slide, so a session
    // that survives a password change survives indefinitely as long as it keeps being used.
    [Fact]
    public async Task ChangingAPassword_EndsEverySessionForThatAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("morgan", SentinelCore.Web.PasswordHasher.Hash("original-password"), AccountRole.Admin, null, ct);
        }
        await EnableLoginAsync(ct);

        // Two sessions for the same account: the owner's, and one somebody else is holding.
        using var owner = factory.CreateClient();
        using var stolen = factory.CreateClient();
        foreach (var client in new[] { owner, stolen })
        {
            using var login = await client.PostAsJsonAsync("/api/auth/login",
                new { Username = "morgan", Password = "original-password" }, ct);
            login.EnsureSuccessStatusCode();
        }

        using (var before = await stolen.GetAsync("/api/settings", ct))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var changed = await owner.PostAsJsonAsync("/api/accounts/change-password",
            new { CurrentPassword = "original-password", NewPassword = "a-brand-new-password" }, ct))
        {
            changed.EnsureSuccessStatusCode();
            // The client needs to know to go back to the login screen rather than sit on a page
            // whose next request will 401 out of nowhere.
            var body = await changed.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.True(body.GetProperty("signedOut").GetBoolean());
        }

        // The other session is gone.
        using (var after = await stolen.GetAsync("/api/settings", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // So is the caller's own - the price of not having to re-establish one mid-request.
        using (var self = await owner.GetAsync("/api/settings", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, self.StatusCode);

        // And the new password is the one that works now.
        using var fresh = factory.CreateClient();
        using (var stale = await fresh.PostAsJsonAsync("/api/auth/login",
            new { Username = "morgan", Password = "original-password" }, ct))
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        using var relogin = await fresh.PostAsJsonAsync("/api/auth/login",
            new { Username = "morgan", Password = "a-brand-new-password" }, ct);
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
    }

    private async Task EnableLoginAsync(CancellationToken ct)
    {
        // Mirrors what AuthEndpoints.MapPost("/api/auth/enable") writes when accounts already
        // exist - flip the config flag directly rather than going through the HTTP endpoint, so
        // each test can set up whichever accounts it needs first.
        var json = await File.ReadAllTextAsync(_settingsPath, ct);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root["Auth"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(), ct);
    }
}
