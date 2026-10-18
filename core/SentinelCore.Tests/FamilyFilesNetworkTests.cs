extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The family library kept in a folder shared from another computer (Samba on Linux, a Windows
// share, a NAS), reached over SMB by the server itself. TestSmbServer stands in for that computer;
// `_share` is the disk behind its shared folder, so what lands there is what the other computer
// would see. The promises are the local library's: nothing overwritten, nothing deleted outright.
public sealed class FamilyFilesNetworkTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly string _share;
    private readonly TestSmbServer _server;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FamilyFilesNetworkTests()
    {
        _share = TempDirectory();
        _server = new TestSmbServer(new Dictionary<string, string> { ["Family"] = _share }, writable: true);
    }

    public void Dispose()
    {
        _server.Dispose();
        foreach (var directory in _directories)
        {
            try { TempDirectories.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"family-net-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    private string Location => $"smb://{_server.Host}/Family/Library";

    private string OnShare(params string[] parts) => Path.Combine([_share, "Library", .. parts]);

    private WebApplicationFactory<WebProgram> CreateWebFactory(string dataDirectory)
    {
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");
        File.WriteAllText(settingsPath, WebHostTestSettings.LoginOffSettings);
        return new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath, dataDirectory));
    }

    private async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<WebProgram> factory)
    {
        var client = factory.CreateClient();
        using var use = await client.PutAsJsonAsync("/api/family-files/library",
            new { location = Location, username = TestSmbServer.Username, password = TestSmbServer.Password }, Ct);
        Assert.True(use.StatusCode == HttpStatusCode.OK, await use.Content.ReadAsStringAsync(Ct));
        return client;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, string content) =>
        await client.PutAsync($"/api/family-files/files/{path}", new StringContent(content), Ct);

    [Fact]
    public async Task ALibraryOnAnotherComputer_TakesUploads_ListsServesRenamesAndTrashes()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = await SignedInClientAsync(factory);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct);
        Assert.Equal(Location, status.GetProperty("libraryFolder").GetString());
        Assert.True(status.GetProperty("libraryOnNetwork").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("libraryProblem").ValueKind);

        // Bigger than one SMB write, so the chunking is exercised.
        var big = new string('x', 3 * 1024 * 1024 + 17);
        using (var put = await UploadAsync(client, "Photos/2026/beach.jpg", "first beach")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var put = await UploadAsync(client, "Videos/big.mp4", big)) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("first beach", await File.ReadAllTextAsync(OnShare("Photos", "2026", "beach.jpg"), Ct));
        Assert.Equal(big.Length, new FileInfo(OnShare("Videos", "big.mp4")).Length);
        Assert.DoesNotContain(Directory.EnumerateFiles(_share, "*", SearchOption.AllDirectories), f => f.EndsWith(".part", StringComparison.Ordinal));

        // Same bytes: nothing new. Different bytes: never overwrites.
        using (var again = await UploadAsync(client, "Photos/2026/beach.jpg", "first beach"))
            Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("alreadyThere").GetBoolean());
        using (var different = await UploadAsync(client, "Photos/2026/beach.jpg", "another beach"))
            Assert.Equal("Photos/2026/beach (2).jpg", (await different.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("path").GetString());
        Assert.Equal("first beach", await File.ReadAllTextAsync(OnShare("Photos", "2026", "beach.jpg"), Ct));

        var listing = await client.GetFromJsonAsync<JsonElement>("/api/family-files/list?path=Photos/2026", Ct);
        Assert.Equal(2, listing.GetProperty("files").GetArrayLength());
        var root = await client.GetFromJsonAsync<JsonElement>("/api/family-files/list", Ct);
        Assert.Equal(["Photos", "Videos"], root.GetProperty("folders").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray());

        using (var get = await client.GetAsync("/api/family-files/files/Photos/2026/beach.jpg", Ct))
            Assert.Equal("first beach", await get.Content.ReadAsStringAsync(Ct));
        // A range, the way a video player asks.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/family-files/files/Videos/big.mp4"))
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1009);
            using var ranged = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal("xxxxxxxxxx", await ranged.Content.ReadAsStringAsync(Ct));
        }

        // Rename a folder on the other computer.
        using (var move = await client.PostAsJsonAsync("/api/family-files/move", new { from = "Photos", to = "Pictures/Family" }, Ct))
            Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.True(File.Exists(OnShare("Pictures", "Family", "2026", "beach.jpg")));
        Assert.False(Directory.Exists(OnShare("Photos")));
        using (var clash = await client.PostAsJsonAsync("/api/family-files/move", new { from = "Videos", to = "Pictures" }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);

        // Trash comes to this server; putting back sends it to the other computer again.
        using (var delete = await client.DeleteAsync("/api/family-files/files/Pictures", Ct))
            Assert.Equal(2, (await delete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("trashed").GetInt32());
        Assert.False(Directory.Exists(OnShare("Pictures")));
        var batch = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/family-files/trash", Ct)).GetProperty("batches").EnumerateArray());
        using (var restore = await client.PostAsJsonAsync("/api/family-files/trash/restore", new { batchId = batch.GetProperty("id").GetString() }, Ct))
            Assert.Equal(2, (await restore.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("restored").GetInt32());
        Assert.Equal("first beach", await File.ReadAllTextAsync(OnShare("Pictures", "Family", "2026", "beach.jpg"), Ct));
    }

    [Fact]
    public async Task ALibraryOnAnotherComputer_IsCheckedForDuplicates_BackedUp_AndRebuiltFromTheDrive()
    {
        var dataDirectory = TempDirectory();
        var driveFolder = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = await SignedInClientAsync(factory);

        using (var put = await UploadAsync(client, "Docs/lease.pdf", "the lease")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var put = await UploadAsync(client, "Docs/copy/lease.pdf", "the lease")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var put = await UploadAsync(client, "notes.txt", "notes")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        File.SetLastWriteTimeUtc(OnShare("Docs", "lease.pdf"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();
        var report = await jobs.CheckDuplicatesAsync(remove: true, "test", Ct);
        Assert.Equal(1, report.Copies);
        Assert.True(File.Exists(OnShare("Docs", "lease.pdf")));
        Assert.False(Directory.Exists(OnShare("Docs", "copy")));

        var drive = new FamilyDrive { Id = "usb", Name = "USB", Kind = FamilyDrive.KindFolder, FolderPath = driveFolder };
        var synced = await jobs.SyncDriveAsync(drive, null, FamilyDriveRemovals.Allowed, Ct);
        Assert.Equal("ok", synced.LastResult);
        Assert.Equal("the lease", await File.ReadAllTextAsync(Path.Combine(driveFolder, "Docs", "lease.pdf"), Ct));
        // The backup keeps the original's date, read from the other computer.
        Assert.Equal(2020, File.GetLastWriteTimeUtc(Path.Combine(driveFolder, "Docs", "lease.pdf")).Year);

        // The other computer's disk is lost; the drive puts the library back on it.
        Directory.Delete(OnShare(), recursive: true);
        var restored = await jobs.RestoreFromDriveAsync(drive, Ct);
        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(2, restored.Copied);
        Assert.Equal("notes", await File.ReadAllTextAsync(OnShare("notes.txt"), Ct));
    }

    [Fact]
    public async Task AWrongPassword_OrAReadOnlyShare_IsRefused_AndTheLibraryStaysPut()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        var before = (await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct)).GetProperty("libraryFolder").GetString();

        using (var wrong = await client.PutAsJsonAsync("/api/family-files/library",
            new { location = Location, username = TestSmbServer.Username, password = "not it" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.True((await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("needsLogin").GetBoolean());
        }

        var readOnlyFolder = TempDirectory();
        using var readOnly = new TestSmbServer(new Dictionary<string, string> { ["Movies"] = readOnlyFolder });
        using (var refused = await client.PostAsJsonAsync("/api/family-files/library/test",
            new { location = $"smb://{readOnly.Host}/Movies", username = TestSmbServer.Username, password = TestSmbServer.Password }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("change files", (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        }

        using (var computerOnly = await client.PostAsJsonAsync("/api/family-files/library/test", new { location = $"smb://{_server.Host}" }, Ct))
            Assert.Contains("shared folder", (await computerOnly.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        Assert.Equal(before, (await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct)).GetProperty("libraryFolder").GetString());
    }

    [Fact]
    public async Task WhenTheOtherComputerIsOff_TheTabSaysSo_AndTheListAnswers503()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = await SignedInClientAsync(factory);
        using (var put = await UploadAsync(client, "a.txt", "a")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        _server.Dispose();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Contains("Could not reach", status.GetProperty("libraryProblem").GetString());
        using var list = await client.GetAsync("/api/family-files/list", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
    }

    [Theory]
    [InlineData("smb://nas/Family/Files", "smb://nas/Family/Files")]
    [InlineData("  smb://nas/Family  ", "smb://nas/Family")]
    [InlineData("", "")]
    [InlineData("/mnt/big/family", "/mnt/big/family")]
    public void ALibraryLocation_IsTidied(string raw, string expected) =>
        Assert.Equal(expected, FamilyLibraryLocation.Normalize(raw, out _));

    [Theory]
    [InlineData("relative/folder")]
    [InlineData("smb://nas")]
    [InlineData("smb://")]
    public void ALibraryLocation_ThatCantWork_IsRefused(string raw)
    {
        Assert.Null(FamilyLibraryLocation.Normalize(raw, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }
}
