extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Organizer over HTTP: who is who (login on vs off), the rebuilt-server handshake, and the
// attachment and calendar-feed endpoints whose authorization lives here rather than in the store.
public sealed class OrganizerEndpointsTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public OrganizerEndpointsTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"organizer-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Clock(string device = "web") =>
        $"{DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds():D13}-0000-{device}";

    private static object Change(string id, string kind, Dictionary<string, object?> fields)
    {
        var clock = Clock();
        return new { id, kind, fields, clocks = fields.Keys.ToDictionary(key => key, _ => clock) };
    }

    private static async Task<JsonElement> SyncAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/organizer/sync", body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task LoginOff_EveryoneSharesTheFamilySpace_AndNothingElse()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/organizer/status", Ct);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("personalSpaces").GetBoolean());

        var reply = await SyncAsync(client, new
        {
            cursor = 0,
            changes = new[]
            {
                Change("n1", "note", new() { ["spaceId"] = "family", ["title"] = "Hello" }),
                Change("n2", "note", new() { ["spaceId"] = "personal:someone", ["title"] = "Nope" }),
            },
        });

        Assert.Equal(["family"], reply.GetProperty("spaces").EnumerateArray().Select(space => space.GetProperty("id").GetString()));
        Assert.Equal("n2", Assert.Single(reply.GetProperty("rejected").EnumerateArray()).GetProperty("id").GetString());
        var item = Assert.Single(reply.GetProperty("items").EnumerateArray());
        Assert.Equal("Hello", item.GetProperty("fields").GetProperty("title").GetString());
    }

    [Fact]
    public async Task LoginOn_EachPersonHasTheirOwnPersonalSpace()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "nathan", "terry");
        await EnableLoginAsync();

        using var nathan = factory.CreateClient();
        await SignInAsync(nathan, "nathan");
        await SyncAsync(nathan, new { cursor = 0, changes = new[] { Change("secret", "note", new() { ["spaceId"] = "personal:nathan", ["title"] = "Mine" }) } });

        using var terry = factory.CreateClient();
        await SignInAsync(terry, "terry");
        var reply = await SyncAsync(terry, new { cursor = 0 });

        Assert.Equal(["personal:terry", "family"], reply.GetProperty("spaces").EnumerateArray().Select(space => space.GetProperty("id").GetString()));
        Assert.Empty(reply.GetProperty("items").EnumerateArray());

        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.PostAsJsonAsync("/api/organizer/sync", new { cursor = 0 }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Groups_NeedRealAccounts_AndOnlyKnownMembers()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "nathan", "terry");

        using (var loginOff = factory.CreateClient())
        {
            using var refused = await loginOff.PostAsJsonAsync("/api/organizer/spaces", new { name = "Parents", members = new[] { "terry" } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        await EnableLoginAsync();
        using var nathan = factory.CreateClient();
        await SignInAsync(nathan, "nathan");

        using (var unknown = await nathan.PostAsJsonAsync("/api/organizer/spaces", new { name = "Parents", members = new[] { "nobody" } }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var created = await nathan.PostAsJsonAsync("/api/organizer/spaces", new { name = "Parents", members = new[] { "Terry" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var group = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["nathan", "terry"], group.GetProperty("members").EnumerateArray().Select(m => m.GetString()));

        var people = await nathan.GetFromJsonAsync<JsonElement>("/api/organizer/people", Ct);
        Assert.Equal(2, people.GetProperty("people").GetArrayLength());
    }

    [Fact]
    public async Task ARebuiltServer_SaysSo_AndAppliesNothingUntilTheDeviceConfirms()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var change = Change("n1", "note", new() { ["spaceId"] = "family", ["title"] = "From the old server" });
        var first = await SyncAsync(client, new { serverId = "the-old-server", cursor = 42, changes = new[] { change } });

        Assert.True(first.GetProperty("reset").GetBoolean());
        Assert.Empty(first.GetProperty("items").EnumerateArray());
        var serverId = first.GetProperty("serverId").GetString();

        // The device asked its owner, who said yes: it resends everything against the new id.
        var second = await SyncAsync(client, new { serverId, cursor = 0, changes = new[] { change } });
        Assert.False(second.GetProperty("reset").GetBoolean());
        Assert.Single(second.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Attachments_AreOnlyReadableThroughAnItemYouCanSee_AndMissingOnesAreListed()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "nathan", "terry");
        await EnableLoginAsync();
        using var nathan = factory.CreateClient();
        await SignInAsync(nathan, "nathan");

        var bytes = "a picture, honestly"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var reply = await SyncAsync(nathan, new
        {
            cursor = 0,
            changes = new[]
            {
                Change("n", "note", new() { ["spaceId"] = "personal:nathan", ["title"] = "With photo" }),
                Change("a", "attachment", new() { ["parentId"] = "n", ["hash"] = hash, ["name"] = "photo.txt", ["contentType"] = "text/plain" }),
            },
        });
        Assert.Empty(reply.GetProperty("rejected").EnumerateArray());

        // Item arrived first (an offline phone), bytes not yet - the server asks for them.
        Assert.Equal([hash], reply.GetProperty("missingBlobs").EnumerateArray().Select(h => h.GetString()));

        using (var wrong = await nathan.PutAsync($"/api/organizer/blobs/{hash}", new ByteArrayContent("something else"u8.ToArray()), Ct))
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using (var upload = await nathan.PutAsync($"/api/organizer/blobs/{hash}", new ByteArrayContent(bytes), Ct))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        Assert.Empty((await SyncAsync(nathan, new { cursor = 0 })).GetProperty("missingBlobs").EnumerateArray());
        Assert.Equal(bytes, await nathan.GetByteArrayAsync($"/api/organizer/blobs/{hash}", Ct));

        using var terry = factory.CreateClient();
        await SignInAsync(terry, "terry");
        using var forbidden = await terry.GetAsync($"/api/organizer/blobs/{hash}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Fact]
    public async Task CalendarFeed_WorksWithoutASession_OnlyWithTheRightToken()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "nathan");
        await EnableLoginAsync();
        using var nathan = factory.CreateClient();
        await SignInAsync(nathan, "nathan");

        await SyncAsync(nathan, new
        {
            cursor = 0,
            changes = new[] { Change("e", "event", new() { ["spaceId"] = "personal:nathan", ["title"] = "Dentist", ["start"] = "2026-10-01T09:00:00Z" }) },
        });

        var feed = await nathan.GetFromJsonAsync<JsonElement>("/api/organizer/calendar-feed", Ct);
        var path = feed.GetProperty("path").GetString()!;

        // A calendar app has no cookie.
        using var calendarApp = factory.CreateClient();
        var ics = await calendarApp.GetStringAsync(path, Ct);
        Assert.Contains("SUMMARY:Dentist", ics);

        using var reset = await nathan.PostAsync("/api/organizer/calendar-feed/reset", null, Ct);
        using var stale = await calendarApp.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
    }

    [Fact]
    public async Task TurnedOff_HidesEverythingButStatus()
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, Ct))!.AsObject();
        root["Organizer"] = new JsonObject { ["Enabled"] = false };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(), Ct);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/organizer/status", Ct);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        using var sync = await client.PostAsJsonAsync("/api/organizer/sync", new { cursor = 0 }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, sync.StatusCode);
    }

    [Fact]
    public async Task Backup_CarriesOrganizerItems()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await SyncAsync(client, new { cursor = 0, changes = new[] { Change("r", "reminder", new() { ["spaceId"] = "family", ["title"] = "Trash day" }) } });

        var backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export", Ct);
        Assert.Equal(3, backup.GetProperty("schemaVersion").GetInt32());
        var items = backup.GetProperty("organizer").GetProperty("items");
        Assert.Equal("Trash day", Assert.Single(items.EnumerateArray()).GetProperty("fields").GetProperty("title").GetString());

        // And it restores onto a fresh install.
        var otherDirectory = Path.Combine(Path.GetTempPath(), $"organizer-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherDirectory);
        var otherSettings = Path.Combine(otherDirectory, "appsettings.json");
        await File.WriteAllTextAsync(otherSettings, WebHostTestSettings.LoginOffSettings, Ct);
        try
        {
            using var otherFactory = new WebApplicationFactory<WebProgram>()
                .WithWebHostBuilder(builder => builder.UseTestSettings(otherSettings, otherDirectory));
            using var other = otherFactory.CreateClient();
            using var import = await other.PostAsJsonAsync("/api/backup/import", backup, Ct);
            Assert.Equal(HttpStatusCode.OK, import.StatusCode);

            var reply = await SyncAsync(other, new { cursor = 0 });
            Assert.Single(reply.GetProperty("items").EnumerateArray());
        }
        finally
        {
            try { TempDirectories.Delete(otherDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task CreateAccountsAsync(WebApplicationFactory<WebProgram> factory, params string[] usernames)
    {
        using var scope = factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        foreach (var username in usernames)
            await accounts.CreateAsync(username, PasswordHasher.Hash("correct horse battery"), AccountRole.Viewer, null, Ct);
    }

    private static async Task SignInAsync(HttpClient client, string username)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = "correct horse battery" }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    private async Task EnableLoginAsync()
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath, Ct))!.AsObject();
        root["Auth"] = new JsonObject { ["Enabled"] = true };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(), Ct);
    }
}
