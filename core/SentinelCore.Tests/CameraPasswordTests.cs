extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AccountRole = SentinelCore.Web.AccountRole;
using IAccountStore = SentinelCore.Web.IAccountStore;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// A camera's password is the login to a device on the network, not a detail of this app's own
// configuration: on most cameras it is the ONVIF admin account, and it is very often the same
// password the owner used somewhere else. GET /api/cameras used to return it in full, to anyone
// the dashboard would talk to at all - including a Viewer, whose entire purpose is watching live
// video, and who is refused /api/settings precisely because settings contain secrets.
//
// So it is masked in the list and fetched one camera at a time, by an admin, from an endpoint
// that exists to do only that. These tests are the contract: masked going out, "unchanged" coming
// back, still readable when an admin genuinely asks, and still real inside a backup - a restore
// whose cameras could not reconnect would not be a restore.
public sealed class CameraPasswordTests : IDisposable
{
    private const string Mask = "••••••••";
    private const string RealPassword = "hunter2-the-real-camera-password";

    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public CameraPasswordTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"camera-secrets-{Guid.NewGuid():N}");
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
            .UseSetting("Bridge:DataDirectory", _dataDirectory)
            .UseSetting("InternetExposed:Enabled", "false"));

    [Fact]
    public async Task TheCameraList_CarriesTheMask_NotThePassword()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AddCameraAsync(client, ct);

        var cameras = await client.GetFromJsonAsync<JsonElement>("/api/cameras", ct);

        Assert.Equal(Mask, cameras[0].GetProperty("password").GetString());
        // Not merely absent from that one field - the real value is nowhere in the response.
        Assert.DoesNotContain(RealPassword, cameras.GetRawText(), StringComparison.Ordinal);
    }

    // The difference that matters: a camera with no password must still read as having none, or a
    // form cannot tell "not set" from "set, and not being shown to you".
    [Fact]
    public async Task ACameraWithNoPassword_StaysEmpty_RatherThanLookingLikeOneIsSet()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using (var made = await client.PostAsJsonAsync("/api/cameras",
            new { Name = "Open Camera", StreamUrl = "rtsp://10.0.0.9/main", IncludeAudio = false }, ct))
            made.EnsureSuccessStatusCode();

        var cameras = await client.GetFromJsonAsync<JsonElement>("/api/cameras", ct);
        var password = cameras[0].GetProperty("password").GetString();

        Assert.True(string.IsNullOrEmpty(password), $"Expected no password, got '{password}'.");
    }

    [Fact]
    public async Task AnAdminCanStillReadThePassword_ByAskingForThatOneCamera()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        var revealed = await client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}/password", ct);

        Assert.Equal(RealPassword, revealed.GetProperty("password").GetString());
    }

    [Fact]
    public async Task RevealingAPasswordForACameraThatDoesNotExist_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/cameras/{Guid.NewGuid()}/password", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The case this whole change exists for.
    [Fact]
    public async Task AViewer_CanWatchTheCameras_ButCannotLearnTheirPasswords()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();

        Guid id;
        using (var admin = factory.CreateClient()) id = await AddCameraAsync(admin, ct);

        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", SentinelCore.Web.PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
            await accounts.CreateAsync("viewer", SentinelCore.Web.PasswordHasher.Hash("watch-only-please"), AccountRole.Viewer, null, ct);
        }
        await EnableLoginAsync(ct);

        using var viewer = factory.CreateClient();
        using (var login = await viewer.PostAsJsonAsync("/api/auth/login",
            new { Username = "viewer", Password = "watch-only-please" }, ct))
            login.EnsureSuccessStatusCode();

        // Still allowed to do the thing a Viewer is for.
        using var list = await viewer.GetAsync("/api/cameras", ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(RealPassword, await list.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // And not the thing it is not for.
        using var reveal = await viewer.GetAsync($"/api/cameras/{id}/password", ct);
        Assert.Equal(HttpStatusCode.Forbidden, reveal.StatusCode);
    }

    [Fact]
    public async Task SavingTheMaskBack_KeepsTheStoredPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        // Exactly what an edit form does when someone changes the name and nothing else: it sends
        // back the masked password it was given.
        using (var saved = await client.PutAsJsonAsync($"/api/cameras/{id}",
            new { Name = "Renamed Gate", StreamUrl = "rtsp://10.0.0.5/main", Username = "admin", Password = Mask, IncludeAudio = false }, ct))
            saved.EnsureSuccessStatusCode();

        var revealed = await client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}/password", ct);
        Assert.Equal(RealPassword, revealed.GetProperty("password").GetString());

        var cameras = await client.GetFromJsonAsync<JsonElement>("/api/cameras", ct);
        Assert.Equal("Renamed Gate", cameras[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SavingARealPassword_StillChangesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        using (var saved = await client.PutAsJsonAsync($"/api/cameras/{id}",
            new { Name = "Front Gate", StreamUrl = "rtsp://10.0.0.5/main", Username = "admin", Password = "a-brand-new-one", IncludeAudio = false }, ct))
            saved.EnsureSuccessStatusCode();

        var revealed = await client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}/password", ct);
        Assert.Equal("a-brand-new-one", revealed.GetProperty("password").GetString());
    }

    // Clearing the password has to remain possible - it is how a camera that no longer needs one
    // is corrected, and an empty string is not the mask.
    [Fact]
    public async Task ClearingThePassword_StillClearsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        using (var saved = await client.PutAsJsonAsync($"/api/cameras/{id}",
            new { Name = "Front Gate", StreamUrl = "rtsp://10.0.0.5/main", Username = "admin", Password = "", IncludeAudio = false }, ct))
            saved.EnsureSuccessStatusCode();

        var revealed = await client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}/password", ct);
        Assert.Equal(string.Empty, revealed.GetProperty("password").GetString());
    }

    // The masking must not reach the backup. A restored install has to be able to dial its own
    // cameras, and a file full of bullet characters could not.
    [Fact]
    public async Task TheConfigBackup_StillCarriesTheRealPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AddCameraAsync(client, ct);

        var backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export", ct);

        Assert.Equal(RealPassword, backup.GetProperty("cameras")[0].GetProperty("password").GetString());
    }

    // Why every client has to send the password back on an edit: the PUT overwrites the whole
    // record rather than merging, so a save that omits the field entirely reads as "this camera
    // has no password" and silently breaks the stream. Both phone apps did exactly that until
    // their Camera models learned to carry it.
    [Fact]
    public async Task SavingWithNoPasswordField_ClearsIt_WhichIsWhyClientsMustRoundTripTheMask()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var id = await AddCameraAsync(client, ct);

        // No Password property at all - exactly what a client that forgot the field sends.
        using (var saved = await client.PutAsJsonAsync($"/api/cameras/{id}",
            new { Name = "Front Gate", StreamUrl = "rtsp://10.0.0.5/main", IncludeAudio = false }, ct))
            saved.EnsureSuccessStatusCode();

        var revealed = await client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}/password", ct);
        Assert.Equal(string.Empty, revealed.GetProperty("password").GetString());
    }

    private static async Task<Guid> AddCameraAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/cameras",
            new { Name = "Front Gate", StreamUrl = "rtsp://10.0.0.5/main", Username = "admin", Password = RealPassword, IncludeAudio = false }, ct);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        // Even the echo of a camera you just created comes back masked - there is no reason for a
        // create response to be the one place the value still travels.
        Assert.Equal(Mask, created.GetProperty("password").GetString());
        return created.GetProperty("id").GetGuid();
    }

    // Mirrors what AuthEndpoints writes when accounts already exist, so each test can set up its
    // own accounts first. Same helper as WebAuthorizationTests.
    private async Task EnableLoginAsync(CancellationToken ct)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, ct))!.AsObject();
        root["Auth"] = new JsonObject { ["Enabled"] = true };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(), ct);
    }
}
