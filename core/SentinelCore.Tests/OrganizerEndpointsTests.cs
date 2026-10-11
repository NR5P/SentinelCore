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
        await CreateAccountsAsync(factory, "morgan", "terry");
        await EnableLoginAsync();

        using var morgan = factory.CreateClient();
        await SignInAsync(morgan, "morgan");
        await SyncAsync(morgan, new { cursor = 0, changes = new[] { Change("secret", "note", new() { ["spaceId"] = "personal:morgan", ["title"] = "Mine" }) } });

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
        await CreateAccountsAsync(factory, "morgan", "terry");

        using (var loginOff = factory.CreateClient())
        {
            using var refused = await loginOff.PostAsJsonAsync("/api/organizer/spaces", new { name = "Parents", members = new[] { "terry" } }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        await EnableLoginAsync();
        using var morgan = factory.CreateClient();
        await SignInAsync(morgan, "morgan");

        using (var unknown = await morgan.PostAsJsonAsync("/api/organizer/spaces", new { name = "Parents", members = new[] { "nobody" } }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var created = await morgan.PostAsJsonAsync("/api/organizer/spaces", new { name = "Parents", members = new[] { "Terry" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var group = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["morgan", "terry"], group.GetProperty("members").EnumerateArray().Select(m => m.GetString()));

        var people = await morgan.GetFromJsonAsync<JsonElement>("/api/organizer/people", Ct);
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
        await CreateAccountsAsync(factory, "morgan", "terry");
        await EnableLoginAsync();
        using var morgan = factory.CreateClient();
        await SignInAsync(morgan, "morgan");

        var bytes = "a picture, honestly"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var reply = await SyncAsync(morgan, new
        {
            cursor = 0,
            changes = new[]
            {
                Change("n", "note", new() { ["spaceId"] = "personal:morgan", ["title"] = "With photo" }),
                Change("a", "attachment", new() { ["parentId"] = "n", ["hash"] = hash, ["name"] = "photo.txt", ["contentType"] = "text/plain" }),
            },
        });
        Assert.Empty(reply.GetProperty("rejected").EnumerateArray());

        // Item arrived first (an offline phone), bytes not yet - the server asks for them.
        Assert.Equal([hash], reply.GetProperty("missingBlobs").EnumerateArray().Select(h => h.GetString()));

        using (var wrong = await morgan.PutAsync($"/api/organizer/blobs/{hash}", new ByteArrayContent("something else"u8.ToArray()), Ct))
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using (var upload = await morgan.PutAsync($"/api/organizer/blobs/{hash}", new ByteArrayContent(bytes), Ct))
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        Assert.Empty((await SyncAsync(morgan, new { cursor = 0 })).GetProperty("missingBlobs").EnumerateArray());
        Assert.Equal(bytes, await morgan.GetByteArrayAsync($"/api/organizer/blobs/{hash}", Ct));

        using var terry = factory.CreateClient();
        await SignInAsync(terry, "terry");
        using var forbidden = await terry.GetAsync($"/api/organizer/blobs/{hash}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Fact]
    public async Task CalendarFeed_WorksWithoutASession_OnlyWithTheRightToken()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "morgan");
        await EnableLoginAsync();
        using var morgan = factory.CreateClient();
        await SignInAsync(morgan, "morgan");

        await SyncAsync(morgan, new
        {
            cursor = 0,
            changes = new[] { Change("e", "event", new() { ["spaceId"] = "personal:morgan", ["title"] = "Dentist", ["start"] = "2026-10-01T09:00:00Z" }) },
        });

        var feed = await morgan.GetFromJsonAsync<JsonElement>("/api/organizer/calendar-feed", Ct);
        var path = feed.GetProperty("path").GetString()!;

        // A calendar app has no cookie.
        using var calendarApp = factory.CreateClient();
        var ics = await calendarApp.GetStringAsync(path, Ct);
        Assert.Contains("SUMMARY:Dentist", ics);

        using var reset = await morgan.PostAsync("/api/organizer/calendar-feed/reset", null, Ct);
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
        Assert.Equal(BackupService.CurrentSchemaVersion, backup.GetProperty("schemaVersion").GetInt32());
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

    // ---- board automations --------------------------------------------------------------------

    private static async Task MakeBoardAsync(HttpClient client) =>
        await SyncAsync(client, new
        {
            cursor = 0,
            changes = new[]
            {
                Change("b1", "board", new() { ["spaceId"] = "family", ["title"] = "Home" }),
                Change("col-new", "column", new() { ["parentId"] = "b1", ["title"] = "New items", ["position"] = 1 }),
            },
        });

    [Fact]
    public async Task Automation_SavedOverHttp_RunNow_PutsTheCardOnTheBoardForEveryDevice()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await MakeBoardAsync(client);

        using var created = await client.PostAsJsonAsync("/api/organizer/automations", new
        {
            name = "Eggs every morning",
            boardId = "b1",
            trigger = new { type = "schedule", schedule = new { repeat = "daily", time = "07:00", timeZone = "America/Chicago" } },
            actions = new[] { new { type = "createCard", columnId = "col-new", title = "pick up eggs" } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var rule = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.String, rule.GetProperty("nextRunAt").ValueKind);
        var id = rule.GetProperty("id").GetString();

        // The engine starts in the background with the host; give it a moment.
        HttpResponseMessage? ran = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            ran?.Dispose();
            ran = await client.PostAsync($"/api/organizer/automations/{id}/run", null, Ct);
            if (ran.StatusCode != HttpStatusCode.ServiceUnavailable) break;
            await Task.Delay(100, Ct);
        }
        Assert.Equal(HttpStatusCode.OK, ran!.StatusCode);
        Assert.True((await ran.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ok").GetBoolean());
        ran.Dispose();

        var reply = await SyncAsync(client, new { cursor = 0 });
        Assert.Contains(reply.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("kind").GetString() == "card"
            && item.GetProperty("parentId").GetString() == "col-new"
            && item.GetProperty("fields").GetProperty("title").GetString() == "pick up eggs");

        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/organizer/automations/{id}/runs", Ct);
        Assert.Single(runs.GetProperty("runs").EnumerateArray());
    }

    [Fact]
    public async Task Automation_Validation_ExplainsWhatIsWrong_InPlainWords()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await MakeBoardAsync(client);

        using var response = await client.PostAsJsonAsync("/api/organizer/automations", new
        {
            name = "",
            boardId = "b1",
            trigger = new { type = "schedule", schedule = new { repeat = "daily", time = "25:00" } },
            actions = new[] { new { type = "addLabel", name = "x" } },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Give the rule a name.", errors);
        Assert.Contains("Pick a time of day.", errors);
        Assert.Contains(errors, e => e!.Contains("Create a card"));
    }

    // ---- notifying people -----------------------------------------------------------------------

    [Fact]
    public async Task APhoneRegisteringForPush_IsTiedToWhoeverIsSignedIn()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "terry");
        await EnableLoginAsync();
        using var terry = factory.CreateClient();
        await SignInAsync(terry, "terry");

        using var register = await terry.PostAsJsonAsync("/api/push/devices",
            new { platform = "apple", token = new string('a', 64), deviceName = "Terry's iPhone" }, Ct);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        using var scope = factory.Services.CreateScope();
        var devices = await scope.ServiceProvider.GetRequiredService<SentinelCore.IPushDeviceStore>().GetAllAsync(Ct);
        Assert.Equal("terry", Assert.Single(devices).Username);
    }

    [Fact]
    public async Task People_SaysWhoCanBeEmailed_WithoutGivingOutTheAddress()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "morgan", "terry");
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.UpdateEmailAsync((await accounts.GetByUsernameAsync("terry", Ct))!.Id, "terry@example.com", Ct);
        }
        await EnableLoginAsync();
        using var morgan = factory.CreateClient();
        await SignInAsync(morgan, "morgan");

        using var response = await morgan.GetAsync("/api/organizer/people", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("terry@example.com", text);
        var people = JsonDocument.Parse(text).RootElement.GetProperty("people").EnumerateArray().ToDictionary(
            p => p.GetProperty("username").GetString()!, p => p.GetProperty("hasEmail").GetBoolean());
        Assert.True(people["terry"]);
        Assert.False(people["morgan"]);
    }

    [Fact]
    public async Task Email_AnyoneCanSetTheirOwn_OnlyAnAdminCanSetSomeoneElses()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "terry");
        Guid terryId;
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("boss", PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, Ct);
            terryId = (await accounts.GetByUsernameAsync("terry", Ct))!.Id;
        }
        await EnableLoginAsync();

        using var terry = factory.CreateClient();
        await SignInAsync(terry, "terry");
        using (var own = await terry.PutAsJsonAsync("/api/accounts/me/email", new { email = "terry@example.com" }, Ct))
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("terry@example.com", (await terry.GetFromJsonAsync<JsonElement>("/api/accounts/me", Ct)).GetProperty("email").GetString());
        using (var bad = await terry.PutAsJsonAsync("/api/accounts/me/email", new { email = "not an address" }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var others = await terry.PutAsJsonAsync($"/api/accounts/{terryId}/email", new { email = "x@example.com" }, Ct))
            Assert.Equal(HttpStatusCode.Forbidden, others.StatusCode);

        using var boss = factory.CreateClient();
        await SignInAsync(boss, "boss");
        using (var set = await boss.PutAsJsonAsync($"/api/accounts/{terryId}/email", new { email = "new@example.com" }, Ct))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var listed = await boss.GetFromJsonAsync<JsonElement>("/api/accounts", Ct);
        Assert.Contains(listed.EnumerateArray(), a => a.GetProperty("username").GetString() == "terry" && a.GetProperty("email").GetString() == "new@example.com");
    }

    [Fact]
    public async Task Automation_SendSteps_AreCheckedInPlainWords()
    {
        using var factory = CreateFactory();
        await CreateAccountsAsync(factory, "morgan", "terry");
        await EnableLoginAsync();
        using var morgan = factory.CreateClient();
        await SignInAsync(morgan, "morgan");
        await MakeBoardAsync(morgan);

        async Task<List<string>> ErrorsFor(object action, object? trigger = null)
        {
            using var response = await morgan.PostAsJsonAsync("/api/organizer/automations", new
            {
                name = "x",
                boardId = "b1",
                trigger = trigger ?? new { type = "cardAdded" },
                actions = new[] { action },
            }, Ct);
            if (response.StatusCode == HttpStatusCode.OK) return [];
            return [.. (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateArray().Select(e => e.GetString()!)];
        }

        Assert.Empty(await ErrorsFor(new { type = "notify", people = new[] { "terry" }, text = "hi" }));
        Assert.Empty(await ErrorsFor(new { type = "notify", everyone = true, text = "hi" }));
        Assert.Empty(await ErrorsFor(new { type = "email", people = new[] { "terry" }, addresses = new[] { "gran@example.com" }, text = "hi" }));
        Assert.Contains("There's no account called \"ghost\".", await ErrorsFor(new { type = "notify", people = new[] { "ghost" }, text = "hi" }));
        Assert.Contains("Pick who gets the notification, or send it to everyone.", await ErrorsFor(new { type = "notify", text = "hi" }));
        Assert.Contains("The notification has no message.", await ErrorsFor(new { type = "notify", everyone = true, text = " " }));
        Assert.Contains("\"not-an-address\" doesn't look like an email address.", await ErrorsFor(new { type = "email", addresses = new[] { "not-an-address" }, text = "hi" }));
        Assert.Contains("Pick who gets the email, or type an address.", await ErrorsFor(new { type = "email", text = "hi" }));
        Assert.Contains("An email can go to at most 20 people.", await ErrorsFor(new { type = "email", addresses = Enumerable.Range(0, 21).Select(i => $"p{i}@example.com").ToArray(), text = "hi" }));
        // Sending needs no card, so a schedule can do it on its own.
        Assert.Empty(await ErrorsFor(new { type = "notify", everyone = true, text = "Bins out" },
            new { type = "schedule", schedule = new { repeat = "daily", time = "18:00", days = new[] { 0 } } }));
        Assert.Empty(await ErrorsFor(new { type = "notify", everyone = true, text = "{card} is due {due}" }, new { type = "cardDue", minutesBefore = 60 }));
        Assert.Contains("A due-date reminder can be at most two weeks before.",
            await ErrorsFor(new { type = "notify", everyone = true, text = "x" }, new { type = "cardDue", minutesBefore = 60 * 24 * 30 }));
    }

    [Fact]
    public async Task Automation_WithLoginOff_ANotificationCanOnlyGoToEveryone()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await MakeBoardAsync(client);

        using var response = await client.PostAsJsonAsync("/api/organizer/automations", new
        {
            name = "x",
            boardId = "b1",
            trigger = new { type = "cardAdded" },
            actions = new[] { new { type = "notify", people = Array.Empty<string>(), text = "hi" } },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("only go to everyone", await response.Content.ReadAsStringAsync(Ct));

        // And the list says whether sending can work here at all - it can't on this bare test server.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/organizer/automations", Ct);
        Assert.False(list.GetProperty("pushReady").GetBoolean());
        Assert.False(list.GetProperty("emailReady").GetBoolean());
    }

    [Fact]
    public async Task Backup_CarriesBoardAutomations_AndARestoredOneIsScheduledAgain()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await MakeBoardAsync(client);
        using (var created = await client.PostAsJsonAsync("/api/organizer/automations", new
        {
            name = "Eggs every morning",
            boardId = "b1",
            trigger = new { type = "schedule", schedule = new { repeat = "daily", time = "07:00", timeZone = "America/Chicago" } },
            actions = new[] { new { type = "createCard", columnId = "col-new", title = "pick up eggs" } },
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export", Ct);
        var rule = Assert.Single(backup.GetProperty("organizerAutomations").EnumerateArray());
        var id = rule.GetProperty("id").GetString();

        var otherDirectory = Path.Combine(Path.GetTempPath(), $"organizer-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherDirectory);
        var otherSettings = Path.Combine(otherDirectory, "appsettings.json");
        await File.WriteAllTextAsync(otherSettings, WebHostTestSettings.LoginOffSettings, Ct);
        try
        {
            using var otherFactory = new WebApplicationFactory<WebProgram>()
                .WithWebHostBuilder(builder => builder.UseTestSettings(otherSettings, otherDirectory));
            using var other = otherFactory.CreateClient();
            for (var round = 0; round < 2; round++)
            {
                using var import = await other.PostAsJsonAsync("/api/backup/import", backup, Ct);
                Assert.Equal(HttpStatusCode.OK, import.StatusCode);
                Assert.Equal(1, (await import.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("organizerAutomationsRestored").GetInt32());
            }

            // Importing twice replaced rather than duplicated, kept the id, and it has a next run.
            var list = await other.GetFromJsonAsync<JsonElement>("/api/organizer/automations", Ct);
            var restored = Assert.Single(list.GetProperty("automations").EnumerateArray());
            Assert.Equal(id, restored.GetProperty("id").GetString());
            Assert.Equal("Eggs every morning", restored.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.String, restored.GetProperty("nextRunAt").ValueKind);
        }
        finally
        {
            try { TempDirectories.Delete(otherDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
