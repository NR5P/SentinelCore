extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AccountRole = SentinelCore.Web.AccountRole;
using IAccountStore = SentinelCore.Web.IAccountStore;
using ICameraStore = SentinelCore.Web.ICameraStore;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Live grid's tile order. The whole point of the feature is that it's *per person*, so the
// isolation test below is the one that matters most - a regression there would mean one household
// member silently rearranging everyone else's screen. The rest cover what has to happen when the
// camera list moves underneath a saved order.
public sealed class LiveOrderEndpointsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public LiveOrderEndpointsTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"live-order-{Guid.NewGuid():N}");
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

    private sealed record LiveOrderResponse(List<Guid> CameraIds);

    [Fact]
    public async Task TwoAccounts_EachKeepTheirOwnOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();

        var cameraIds = await AddCamerasAsync(factory, ct, "gate", "driveway", "front door");
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
            await accounts.CreateAsync("viewer", SentinelCore.Web.PasswordHasher.Hash("watch-only-please"), AccountRole.Viewer, null, ct);
        }
        await EnableLoginAsync(ct);

        // One person wants the front door first; the other wants the driveway first.
        var adminOrder = new[] { cameraIds[2], cameraIds[0], cameraIds[1] };
        var viewerOrder = new[] { cameraIds[1], cameraIds[2], cameraIds[0] };

        using (var admin = factory.CreateClient())
        {
            await SignInAsync(admin, "admin", "correct horse battery", ct);
            using var saved = await admin.PutAsJsonAsync("/api/live-order", new { CameraIds = adminOrder }, ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using (var viewer = factory.CreateClient())
        {
            // A Viewer account arranging its own live view is deliberately allowed - watching live
            // video is what the role is for.
            await SignInAsync(viewer, "viewer", "watch-only-please", ct);
            using var saved = await viewer.PutAsJsonAsync("/api/live-order", new { CameraIds = viewerOrder }, ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            Assert.Equal(viewerOrder, await ReadOrderAsync(viewer, ct));
        }

        using (var admin = factory.CreateClient())
        {
            await SignInAsync(admin, "admin", "correct horse battery", ct);
            Assert.Equal(adminOrder, await ReadOrderAsync(admin, ct));
        }
    }

    [Fact]
    public async Task SavedOrder_DropsDeletedCamerasAndPutsNewOnesLast()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();

        var cameraIds = await AddCamerasAsync(factory, ct, "gate", "driveway", "front door");
        using var client = factory.CreateClient();

        // Reverse of the order they were added in, so "falls back to the camera store's order"
        // can't pass by coincidence.
        var chosen = new[] { cameraIds[2], cameraIds[1], cameraIds[0] };
        using (var saved = await client.PutAsJsonAsync("/api/live-order", new { CameraIds = chosen }, ct))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        Guid added;
        using (var scope = factory.Services.CreateScope())
        {
            var cameras = scope.ServiceProvider.GetRequiredService<ICameraStore>();
            await cameras.DeleteAsync(cameraIds[1], ct);
            var camera = await cameras.AddAsync("side gate", "rtsp://camera/side", null, null, false, false, null, null, false, ct);
            added = camera.Id;
        }

        // The deleted camera's placement goes with it; the new camera lands at the end rather than
        // displacing tiles that were deliberately placed.
        Assert.Equal(new[] { cameraIds[2], cameraIds[0], added }, await ReadOrderAsync(client, ct));
    }

    [Fact]
    public async Task NeverArranged_FallsBackToTheCameraStoresOwnOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();

        var cameraIds = await AddCamerasAsync(factory, ct, "gate", "driveway");
        using var client = factory.CreateClient();

        // ICameraStore lists alphabetically, so "driveway" leads - the point being that an
        // account which has never dragged anything gets that untouched, not an empty list.
        Assert.Equal(new[] { cameraIds[1], cameraIds[0] }, await ReadOrderAsync(client, ct));
    }

    [Fact]
    public async Task Put_RejectsUnknownAndDuplicateCameras()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();

        var cameraIds = await AddCamerasAsync(factory, ct, "gate", "driveway");
        using var client = factory.CreateClient();

        using var unknown = await client.PutAsJsonAsync("/api/live-order", new { CameraIds = new[] { cameraIds[0], Guid.NewGuid() } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var duplicate = await client.PutAsJsonAsync("/api/live-order", new { CameraIds = new[] { cameraIds[0], cameraIds[0] } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        // Neither refusal may leave a half-applied order behind - this is still the camera
        // store's own alphabetical order, not anything the rejected requests wrote.
        Assert.Equal(new[] { cameraIds[1], cameraIds[0] }, await ReadOrderAsync(client, ct));
    }

    [Fact]
    public async Task AuthEnabled_NoCookie_CannotReadOrWriteAnOrder()
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

        using var read = await client.GetAsync("/api/live-order", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);

        using var write = await client.PutAsJsonAsync("/api/live-order", new { CameraIds = Array.Empty<Guid>() }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    private static async Task<List<Guid>> AddCamerasAsync(
        WebApplicationFactory<WebProgram> factory, CancellationToken ct, params string[] names)
    {
        using var scope = factory.Services.CreateScope();
        var cameras = scope.ServiceProvider.GetRequiredService<ICameraStore>();

        var ids = new List<Guid>();
        foreach (var name in names)
        {
            var camera = await cameras.AddAsync(name, $"rtsp://camera/{name}", null, null, false, false, null, null, false, ct);
            ids.Add(camera.Id);
        }

        return ids;
    }

    private static async Task<List<Guid>> ReadOrderAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("/api/live-order", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LiveOrderResponse>(ct);
        return body!.CameraIds;
    }

    private static async Task SignInAsync(HttpClient client, string username, string password, CancellationToken ct)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password }, ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private async Task EnableLoginAsync(CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(_settingsPath, ct);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root["Auth"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(), ct);
    }
}
