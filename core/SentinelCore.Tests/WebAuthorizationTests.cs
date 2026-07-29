extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
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
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
    public async Task AuthDisabled_AnEndpointThatWouldOtherwiseRequireAdmin_IsFullyOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // /api/settings is Admin-only once login is required, but Auth:Enabled defaults to
        // false (the shipped default) - the whole point of that default is that nobody is
        // ever locked out of a fresh install before they've set anything up.
        using var response = await client.GetAsync("/api/settings", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
