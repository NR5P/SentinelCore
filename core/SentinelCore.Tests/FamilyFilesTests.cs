extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The family file library, its trash, the nightly duplicate check and the backup drives. The
// promises that matter: nothing anyone put in the library is ever overwritten or deleted outright,
// a drive only ever loses files this feature wrote to it, and a library can be rebuilt from a drive.
public sealed class FamilyFilesTests : IDisposable
{
    private readonly List<string> _directories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try { TempDirectories.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"family-files-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    private WebApplicationFactory<WebProgram> CreateWebFactory(string dataDirectory, string? settings = null)
    {
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");
        if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, settings ?? WebHostTestSettings.LoginOffSettings);
        return new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath, dataDirectory));
    }

    private static string Library(string dataDirectory) => Path.Combine(dataDirectory, "family-files", "library");

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, string content, DateTimeOffset? modified = null)
    {
        var query = modified is { } stamp ? $"?modified={Uri.EscapeDataString(stamp.ToString("O"))}" : "";
        return await client.PutAsync($"/api/family-files/files/{path}{query}", new StringContent(content), Ct);
    }

    // ---- paths ------------------------------------------------------------------------------

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("Photos/../../x")]
    [InlineData(".hidden/photo.jpg")]
    [InlineData("Photos/.DS_Store")]
    [InlineData("bad:name.jpg")]
    [InlineData("what?.jpg")]
    [InlineData("trailing./x")]
    [InlineData("Photos/ leading space.jpg")]
    [InlineData("Photos/tab\there.jpg")]
    public void APathThatWouldEscapeTheLibrary_OrBreakADrive_IsRefused(string path) =>
        Assert.Null(FamilyFilePaths.Normalize(path));

    // Leading slashes and stray spaces around the whole thing are tidied rather than refused:
    // they can't reach outside the library, and a phone's share sheet produces them.
    [Theory]
    [InlineData("Photos\\2026\\beach.jpg", "Photos/2026/beach.jpg")]
    [InlineData("/Photos//2026/", "Photos/2026")]
    [InlineData("/etc/passwd", "etc/passwd")]
    [InlineData("  Photos/beach.jpg ", "Photos/beach.jpg")]
    [InlineData("", "")]
    [InlineData("Tax 2025/return (final).pdf", "Tax 2025/return (final).pdf")]
    public void AReasonablePath_IsTidiedToForwardSlashes(string raw, string expected) =>
        Assert.Equal(expected, FamilyFilePaths.Normalize(raw));

    // ---- the library over HTTP ---------------------------------------------------------------

    // Any account can upload, and files come back from the dashboard's own origin: a scriptable
    // type opened directly must download, never render, or a Viewer's SVG runs as the Admin who
    // opens it. Photos still display inline.
    [Fact]
    public async Task AScriptableUpload_IsServedAsADownload_WhileAPhotoStaysInline()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        await UploadAsync(client, "Shared/evil.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        await UploadAsync(client, "Shared/page.html", "<script>alert(1)</script>");
        await UploadAsync(client, "Shared/photo.jpg", "jpeg bytes");

        foreach (var path in new[] { "Shared/evil.svg", "Shared/page.html" })
        {
            using var get = await client.GetAsync($"/api/family-files/files/{path}", Ct);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("attachment", get.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        }

        using var photo = await client.GetAsync("/api/family-files/files/Shared/photo.jpg", Ct);
        Assert.Null(photo.Content.Headers.ContentDisposition);
        Assert.Equal("nosniff", photo.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", photo.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task AFile_GoesIn_ListsAndComesBackOut_AndIsNeverOverwritten()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();

        using (var put = await UploadAsync(client, "Photos/2026/beach.jpg", "first beach"))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var saved = await put.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("Photos/2026/beach.jpg", saved.GetProperty("path").GetString());
            Assert.False(saved.GetProperty("alreadyThere").GetBoolean());
        }

        var listing = await client.GetFromJsonAsync<JsonElement>("/api/family-files/list?path=Photos/2026", Ct);
        var file = Assert.Single(listing.GetProperty("files").EnumerateArray());
        Assert.Equal("beach.jpg", file.GetProperty("name").GetString());
        Assert.Equal("first beach".Length, file.GetProperty("size").GetInt64());
        Assert.Equal("image/jpeg", file.GetProperty("contentType").GetString());

        var root = await client.GetFromJsonAsync<JsonElement>("/api/family-files/list", Ct);
        Assert.Equal("Photos", Assert.Single(root.GetProperty("folders").EnumerateArray()).GetProperty("name").GetString());

        using (var get = await client.GetAsync("/api/family-files/files/Photos/2026/beach.jpg?download=true", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("first beach", await get.Content.ReadAsStringAsync(Ct));
            Assert.Equal("beach.jpg", get.Content.Headers.ContentDisposition?.FileNameStar ?? get.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        }

        // The same bytes again cost nothing and change nothing.
        using (var again = await UploadAsync(client, "Photos/2026/beach.jpg", "first beach"))
            Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("alreadyThere").GetBoolean());

        // Different bytes under the same name never replace what was there.
        using (var different = await UploadAsync(client, "Photos/2026/beach.jpg", "a different beach"))
            Assert.Equal("Photos/2026/beach (2).jpg", (await different.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("path").GetString());
        Assert.Equal("first beach", await File.ReadAllTextAsync(Path.Combine(Library(dataDirectory), "Photos", "2026", "beach.jpg"), Ct));

        // ".." never reaches the server (every HTTP client collapses it); a name a Windows drive
        // would choke on is the refusal that does.
        using (var escape = await UploadAsync(client, "Photos/bad%3Aname.txt", "nope"))
            Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(Library(dataDirectory), "Photos"), "*", SearchOption.AllDirectories), f => f.Contains("bad"));

        var status = await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, status.GetProperty("library").GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task ADeletedFile_WaitsInTheTrash_AndCanBePutBack()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();

        using (var put = await UploadAsync(client, "Docs/lease.pdf", "the lease")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using (var delete = await client.DeleteAsync("/api/family-files/files/Docs/lease.pdf", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
            Assert.Equal(1, (await delete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("trashed").GetInt32());
        }
        Assert.False(File.Exists(Path.Combine(Library(dataDirectory), "Docs", "lease.pdf")));

        var trash = await client.GetFromJsonAsync<JsonElement>("/api/family-files/trash", Ct);
        var batch = Assert.Single(trash.GetProperty("batches").EnumerateArray());
        Assert.Equal("deleted", batch.GetProperty("reason").GetString());
        Assert.Equal("Docs/lease.pdf", Assert.Single(batch.GetProperty("files").EnumerateArray()).GetProperty("path").GetString());

        using var restore = await client.PostAsJsonAsync("/api/family-files/trash/restore", new { batchId = batch.GetProperty("id").GetString() }, Ct);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("the lease", await File.ReadAllTextAsync(Path.Combine(Library(dataDirectory), "Docs", "lease.pdf"), Ct));
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/family-files/trash", Ct)).GetProperty("batches").EnumerateArray());
    }

    [Fact]
    public async Task AFolder_CanBeMade_RenamedAndDeleted_WithEverythingInIt()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();

        using (var make = await client.PostAsJsonAsync("/api/family-files/folders", new { path = "Scans" }, Ct)) Assert.Equal(HttpStatusCode.OK, make.StatusCode);
        using (var put = await UploadAsync(client, "Scans/passport.jpg", "passport")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using (var move = await client.PostAsJsonAsync("/api/family-files/move", new { from = "Scans", to = "Documents/Scans" }, Ct))
            Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.True(File.Exists(Path.Combine(Library(dataDirectory), "Documents", "Scans", "passport.jpg")));

        using (var into = await client.PostAsJsonAsync("/api/family-files/move", new { from = "Documents", to = "Documents/Scans/x" }, Ct))
            Assert.Equal(HttpStatusCode.BadRequest, into.StatusCode);

        using (var delete = await client.DeleteAsync("/api/family-files/files/Documents", Ct))
            Assert.Equal(1, (await delete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("trashed").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(Library(dataDirectory), "Documents")));
    }

    // ---- duplicates --------------------------------------------------------------------------

    [Fact]
    public async Task TheDuplicateCheck_KeepsTheOldestCopy_AndRetiresTheRestToTheTrash()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        var library = Library(dataDirectory);
        Directory.CreateDirectory(Path.Combine(library, "Photos", "copy of copy"));
        Directory.CreateDirectory(Path.Combine(library, "Photos", "originals"));

        var original = Path.Combine(library, "Photos", "originals", "sunset.jpg");
        var copy1 = Path.Combine(library, "Photos", "sunset (1).jpg");
        var copy2 = Path.Combine(library, "Photos", "copy of copy", "sunset.jpg");
        var unrelated = Path.Combine(library, "Photos", "sunrise.jpg");
        foreach (var path in new[] { original, copy1, copy2 }) await File.WriteAllTextAsync(path, "same sunset bytes", Ct);
        await File.WriteAllTextAsync(unrelated, "different bytes", Ct);
        File.SetLastWriteTimeUtc(original, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(copy1, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(copy2, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();

        // Report only first: nothing moves.
        var listed = await jobs.CheckDuplicatesAsync(remove: false, "test", Ct);
        Assert.False(listed.Removed);
        Assert.Equal(1, listed.Groups);
        Assert.Equal(2, listed.Copies);
        Assert.Equal("Photos/originals/sunset.jpg", Assert.Single(listed.Sample).Kept);
        Assert.True(File.Exists(copy1));

        var removed = await jobs.CheckDuplicatesAsync(remove: true, "test", Ct);
        Assert.True(removed.Removed);
        Assert.Equal(2, removed.Copies);
        Assert.Equal("same sunset bytes".Length * 2, removed.Bytes);
        Assert.True(File.Exists(original));
        Assert.True(File.Exists(unrelated));
        Assert.False(File.Exists(copy1));
        Assert.False(File.Exists(copy2));
        Assert.False(Directory.Exists(Path.Combine(library, "Photos", "copy of copy")));

        var trash = factory.Services.GetRequiredService<FamilyFilesTrash>();
        var batch = Assert.Single(await trash.ListAsync(Ct));
        Assert.Equal(FamilyFilesTrash.ReasonDuplicate, batch.Reason);
        Assert.All(batch.Files, f => Assert.Equal("Photos/originals/sunset.jpg", f.KeptCopy));

        // Nothing left to find, and the report says so.
        var clean = await jobs.CheckDuplicatesAsync(remove: true, "test", Ct);
        Assert.Equal(0, clean.Groups);
        Assert.Equal(2, clean.FilesChecked);

        using var client = factory.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct);
        Assert.Equal(0, status.GetProperty("duplicates").GetProperty("groups").GetInt32());
        Assert.Equal(2, status.GetProperty("trash").GetProperty("files").GetInt32());
    }

    // ---- drives -------------------------------------------------------------------------------

    [Fact]
    public async Task AFolderDrive_GetsAMirror_LosesOnlyWhatThisFeatureWrote_AndCanRebuildTheLibrary()
    {
        var library = TempDirectory();
        var driveFolder = TempDirectory();
        var work = TempDirectory();
        var index = new FamilyFilesIndex(Path.Combine(TempDirectory(), "index.json"));
        var drive = new FamilyDrive { Id = "d1", Name = "USB", Kind = FamilyDrive.KindFolder, FolderPath = driveFolder };
        var store = new LocalFamilyLibraryStore(library);

        Directory.CreateDirectory(Path.Combine(library, "Photos"));
        await File.WriteAllTextAsync(Path.Combine(library, "Photos", "a.jpg"), "aaa", Ct);
        await File.WriteAllTextAsync(Path.Combine(library, "b.txt"), "bbbb", Ct);
        // Something of the owner's already on the drive - never ours to touch.
        await File.WriteAllTextAsync(Path.Combine(driveFolder, "keep-me.txt"), "not yours", Ct);

        var files = await index.RescanAsync(store, null, Ct);
        var first = await FamilyDriveSync.MirrorAsync(drive, store, files, null, NullLogger.Instance, Ct);
        Assert.True(first.Ok, first.Message);
        Assert.Equal(2, first.Copied);
        Assert.Equal(0, first.Removed);
        Assert.Equal("aaa", await File.ReadAllTextAsync(Path.Combine(driveFolder, "Photos", "a.jpg"), Ct));
        Assert.True(File.Exists(Path.Combine(driveFolder, FamilyDriveManifest.FileName)));

        // A second pass with nothing changed copies nothing.
        var second = await FamilyDriveSync.MirrorAsync(drive, store, files, null, NullLogger.Instance, Ct);
        Assert.Equal(0, second.Copied);

        // A file that left the library leaves the drive; the owner's own file stays.
        File.Delete(Path.Combine(library, "b.txt"));
        files = await index.RescanAsync(store, null, Ct);
        var third = await FamilyDriveSync.MirrorAsync(drive, store, files, null, NullLogger.Instance, Ct);
        Assert.Equal(1, third.Removed);
        Assert.False(File.Exists(Path.Combine(driveFolder, "b.txt")));
        Assert.Equal("not yours", await File.ReadAllTextAsync(Path.Combine(driveFolder, "keep-me.txt"), Ct));

        // A drive that was wiped behind the manifest's back is noticed and refilled.
        File.Delete(Path.Combine(driveFolder, "Photos", "a.jpg"));
        var fourth = await FamilyDriveSync.MirrorAsync(drive, store, files, null, NullLogger.Instance, Ct);
        Assert.Equal(1, fourth.Copied);

        // The library dies; the drive brings it back - the owner's file included, since it was
        // there, and nothing already in the library touched.
        Directory.Delete(library, recursive: true);
        Directory.CreateDirectory(library);
        await File.WriteAllTextAsync(Path.Combine(library, "new.txt"), "made after the loss", Ct);
        var restore = await FamilyDriveSync.RestoreAsync(drive, store, work, null, NullLogger.Instance, Ct);
        Assert.True(restore.Ok, restore.Message);
        Assert.Equal(2, restore.Copied);
        Assert.Equal("aaa", await File.ReadAllTextAsync(Path.Combine(library, "Photos", "a.jpg"), Ct));
        Assert.Equal("made after the loss", await File.ReadAllTextAsync(Path.Combine(library, "new.txt"), Ct));
    }

    [Fact]
    public async Task ADrive_IsAddedTestedBackedUpTo_AndRemoved_OverHttp()
    {
        var dataDirectory = TempDirectory();
        var driveFolder = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();

        using (var put = await UploadAsync(client, "Photos/one.jpg", "one")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using (var relative = await client.PostAsJsonAsync("/api/family-files/drives", new { name = "USB", kind = "folder", folderPath = "backups" }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
            Assert.Contains("full path", (await relative.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors")[0].GetString());
        }

        string id;
        using (var add = await client.PostAsJsonAsync("/api/family-files/drives", new { name = "USB", kind = "folder", folderPath = driveFolder }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            var drive = await add.Content.ReadFromJsonAsync<JsonElement>(Ct);
            id = drive.GetProperty("id").GetString()!;
            Assert.Equal(driveFolder, drive.GetProperty("location").GetString());
        }

        using (var test = await client.PostAsJsonAsync("/api/family-files/drives/test", new { id, name = "USB", kind = "folder", folderPath = driveFolder }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            Assert.Contains("nothing from this library", (await test.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
        }

        using (var sync = await client.PostAsync($"/api/family-files/drives/{id}/sync", null, Ct))
            Assert.Equal(HttpStatusCode.Accepted, sync.StatusCode);

        JsonElement driveStatus = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct);
            driveStatus = Assert.Single(status.GetProperty("drives").EnumerateArray());
            if (status.GetProperty("job").ValueKind == JsonValueKind.Null && driveStatus.GetProperty("lastResult").GetString() is { Length: > 0 }) break;
            await Task.Delay(100, Ct);
        }
        Assert.Equal("ok", driveStatus.GetProperty("lastResult").GetString());
        Assert.Equal(1, driveStatus.GetProperty("lastFileCount").GetInt32());
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(driveFolder, "Photos", "one.jpg"), Ct));

        using (var remove = await client.DeleteAsync($"/api/family-files/drives/{id}", Ct)) Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/family-files/drives", Ct)).GetProperty("drives").EnumerateArray());
        // Removing the drive from the list leaves the copy on it alone.
        Assert.True(File.Exists(Path.Combine(driveFolder, "Photos", "one.jpg")));
    }

    [Fact]
    public void ADrive_ThatCantWork_IsRefusedInWordsTheOwnerCanActOn()
    {
        static List<string> Problems(FamilyDrive drive)
        {
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                drive.Normalized(), new System.ComponentModel.DataAnnotations.ValidationContext(drive), results, validateAllProperties: true);
            return results.Select(r => r.ErrorMessage!).ToList();
        }

        Assert.Contains(Problems(new FamilyDrive { Name = "", Kind = "folder", FolderPath = "/mnt/usb" }), p => p.Contains("name"));
        Assert.Contains(Problems(new FamilyDrive { Name = "NAS", Kind = "sftp", SftpHost = "nas", SftpUsername = "me", SftpPassword = "pw", SftpHostKeyFingerprint = "not a fingerprint" }),
            p => p.Contains("fingerprint"));
        Assert.Contains(Problems(new FamilyDrive { Name = "NAS", Kind = "sftp", SftpHost = "", SftpUsername = "me", SftpPassword = "pw" }), p => p.Contains("address"));
        Assert.Empty(Problems(new FamilyDrive { Name = "NAS", Kind = "sftp", SftpHost = "nas.local", SftpUsername = "me", SftpPassword = "pw", SftpFolder = "family" }));
        Assert.Empty(Problems(new FamilyDrive { Name = "USB", Kind = "folder", FolderPath = "/Volumes/USB/Family" }));
    }

    // ---- the nightly schedule -----------------------------------------------------------------

    private static readonly FamilyFilesSettingsDto AtFour = new() { Enabled = true, Hour = 4, Minute = 0 };
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void SwitchingItOnInTheAfternoon_WaitsForTheNight()
    {
        var afternoon = new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);
        var state = new FamilyFilesState { ArmedAtUtc = afternoon };
        Assert.Null(FamilyFilesSchedule.Due(AtFour, state, afternoon.AddHours(1), Utc));
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 4, 0, 0, TimeSpan.Zero), FamilyFilesSchedule.Due(AtFour, state, afternoon.AddHours(15), Utc));
    }

    [Fact]
    public void ANightWithAProblem_IsRetriedHourly_AFewTimes_ThenLeftForTomorrow()
    {
        var slot = new DateTimeOffset(2026, 9, 26, 4, 0, 0, TimeSpan.Zero);
        var state = new FamilyFilesState
        {
            ArmedAtUtc = slot.AddDays(-1),
            LastOccurrenceUtc = slot,
            LastNightlyUtc = slot.AddMinutes(2),
            LastNightlyResult = "incomplete",
            AttemptsForOccurrence = 1
        };
        Assert.Null(FamilyFilesSchedule.Due(AtFour, state, slot.AddMinutes(30), Utc));
        Assert.Equal(slot, FamilyFilesSchedule.Due(AtFour, state, slot.AddMinutes(90), Utc));
        Assert.Null(FamilyFilesSchedule.Due(AtFour, state with { AttemptsForOccurrence = FamilyFilesSchedule.MaxAttemptsPerNight }, slot.AddMinutes(90), Utc));
        Assert.Null(FamilyFilesSchedule.Due(AtFour, state with { LastNightlyResult = "ok" }, slot.AddMinutes(90), Utc));
        Assert.Null(FamilyFilesSchedule.Due(new FamilyFilesSettingsDto { Enabled = false, Hour = 4, Minute = 0 }, state, slot.AddMinutes(90), Utc));
    }

    [Fact]
    public async Task TheNightlyPass_EmptiesOldTrash_RetiresDuplicates_AndBacksUpEveryDrive()
    {
        var dataDirectory = TempDirectory();
        var driveA = TempDirectory();
        var driveB = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();

        using (var save = await client.PutAsJsonAsync("/api/settings/family-files", new
        {
            enabled = true, libraryFolder = "", maxUploadMegabytes = 100, hour = 4, minute = 0, removeDuplicates = true, trashKeepDays = 1
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using (var put = await UploadAsync(client, "Photos/x.jpg", "same")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var put = await UploadAsync(client, "Photos/y.jpg", "same")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var put = await UploadAsync(client, "Docs/z.txt", "unique")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        foreach (var folder in new[] { driveA, driveB })
            using (var add = await client.PostAsJsonAsync("/api/family-files/drives", new { name = Path.GetFileName(folder), kind = "folder", folderPath = folder }, Ct))
                Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        // A trash batch old enough to be emptied, planted directly.
        var trash = factory.Services.GetRequiredService<FamilyFilesTrash>();
        var oldBatch = await trash.MoveAsync(new LocalFamilyLibraryStore(Library(dataDirectory)), ["Docs/z.txt"], FamilyFilesTrash.ReasonDeleted, "test", Ct);
        var sidecar = Path.Combine(trash.Root, oldBatch.Id + ".json");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var planted = JsonSerializer.Deserialize<FamilyFilesTrash.Batch>(await File.ReadAllTextAsync(sidecar, Ct), web)!;
        await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(planted with { DeletedUtc = planted.DeletedUtc.AddDays(-3) }, web), Ct);

        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();
        var state = await jobs.RunNightlyAsync(new DateTimeOffset(2026, 9, 26, 4, 0, 0, TimeSpan.Zero), Ct);
        Assert.Equal("ok", state.LastNightlyResult);
        Assert.Contains("mptied 1 file", state.LastNightlyMessage);
        Assert.Contains("moved 1 duplicate", state.LastNightlyMessage);
        Assert.Contains("2 of 2 drive(s)", state.LastNightlyMessage);
        Assert.Equal(1, state.AttemptsForOccurrence);

        Assert.Empty(await trash.ListAsync(Ct) is { } batches ? batches.Where(b => b.Id == oldBatch.Id) : []);
        Assert.Single(await trash.ListAsync(Ct));
        foreach (var folder in new[] { driveA, driveB })
        {
            Assert.True(File.Exists(Path.Combine(folder, "Photos", "x.jpg")));
            Assert.False(File.Exists(Path.Combine(folder, "Photos", "y.jpg")));
        }
        Assert.All(state.Drives.Values, d => Assert.Equal("ok", d.LastResult));
    }

    // ---- who may do what ------------------------------------------------------------------------

    [Fact]
    public async Task AViewer_CanAddAndFetchFiles_ButNotTouchTheDrives()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory, "{}");
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, Ct);
            await accounts.CreateAsync("kid", PasswordHasher.Hash("watch-only-please"), AccountRole.Viewer, null, Ct);
        }
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");
        await File.WriteAllTextAsync(settingsPath, """{ "Auth": { "Enabled": true } }""", Ct);

        using var client = factory.CreateClient();
        using (var anonymous = await client.GetAsync("/api/family-files/status", Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Username = "kid", Password = "watch-only-please" }, Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct);
        Assert.False(status.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("libraryFolder").ValueKind);

        using (var put = await UploadAsync(client, "Homework/essay.txt", "my essay")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var get = await client.GetAsync("/api/family-files/files/Homework/essay.txt", Ct)) Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using (var drives = await client.PostAsJsonAsync("/api/family-files/drives", new { name = "USB", kind = "folder", folderPath = TempDirectory() }, Ct))
            Assert.Equal(HttpStatusCode.Forbidden, drives.StatusCode);
        using (var empty = await client.DeleteAsync("/api/family-files/trash", Ct))
            Assert.Equal(HttpStatusCode.Forbidden, empty.StatusCode);
        using (var sync = await client.PostAsync("/api/family-files/sync", null, Ct))
            Assert.Equal(HttpStatusCode.Forbidden, sync.StatusCode);
    }

    [Fact]
    public async Task TurnedOff_TheTabIsHidden_AndTheLibraryAnswers404_ButNothingIsDeleted()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        using (var put = await UploadAsync(client, "a.txt", "kept")) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using (var save = await client.PutAsJsonAsync("/api/settings/family-files", new
        {
            enabled = false, libraryFolder = "", maxUploadMegabytes = 100, hour = 4, minute = 0, removeDuplicates = true, trashKeepDays = 30
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/family-files/status", Ct);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        using (var list = await client.GetAsync("/api/family-files/list", Ct)) Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.True(File.Exists(Path.Combine(Library(dataDirectory), "a.txt")));
    }

    // ---- the backup file ------------------------------------------------------------------------

    [Fact]
    public async Task TheDrives_TravelInTheBackup_PasswordsIncluded_AndRestoreOnlyOnce()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();

        using (var add = await client.PostAsJsonAsync("/api/family-files/drives", new
        {
            name = "NAS", kind = "sftp", sftpHost = "nas.local", sftpPort = 22, sftpUsername = "family", sftpPassword = "s3cret", sftpFolder = "Family"
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        var export = await client.GetFromJsonAsync<JsonElement>("/api/backup/export", Ct);
        Assert.Equal(BackupService.CurrentSchemaVersion, export.GetProperty("schemaVersion").GetInt32());
        var exported = Assert.Single(export.GetProperty("familyDrives").EnumerateArray());
        Assert.Equal("s3cret", exported.GetProperty("sftpPassword").GetString());

        var otherDirectory = TempDirectory();
        using var other = CreateWebFactory(otherDirectory);
        using var otherClient = other.CreateClient();
        for (var n = 0; n < 2; n++)
        {
            using var import = await otherClient.PostAsync("/api/backup/import", new StringContent(export.GetRawText(), System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.OK, import.StatusCode);
            Assert.Equal(1, (await import.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("familyDrivesRestored").GetInt32());
        }

        var restored = Assert.Single((await otherClient.GetFromJsonAsync<JsonElement>("/api/family-files/drives", Ct)).GetProperty("drives").EnumerateArray());
        Assert.Equal("NAS", restored.GetProperty("name").GetString());
        Assert.True(restored.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(JsonValueKind.Undefined, restored.TryGetProperty("sftpPassword", out var leaked) ? leaked.ValueKind : JsonValueKind.Undefined);
    }
}
