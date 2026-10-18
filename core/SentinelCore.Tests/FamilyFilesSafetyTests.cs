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

// The ways a family could lose files that aren't "someone pressed delete": a backup drive emptied
// because the library looked empty (moved, unmounted, unreadable), a drive file of the owner's
// overwritten, a half-finished deletion that vanishes from the trash, a drive that backs the
// library up into itself. Each test sets up the accident and checks every byte is still somewhere.
public sealed class FamilyFilesSafetyTests : IDisposable
{
    private readonly List<string> _directories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                foreach (var locked in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                TempDirectories.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"family-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    private WebApplicationFactory<WebProgram> CreateWebFactory(string dataDirectory)
    {
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");
        File.WriteAllText(settingsPath, WebHostTestSettings.LoginOffSettings);
        return new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(settingsPath, dataDirectory));
    }

    private static async Task UploadAsync(HttpClient client, string path, string content)
    {
        using var put = await client.PutAsync($"/api/family-files/files/{path}", new StringContent(content), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private static async Task<string> AddDriveAsync(HttpClient client, string folder)
    {
        using var add = await client.PostAsJsonAsync("/api/family-files/drives", new { name = "USB", kind = "folder", folderPath = folder }, Ct);
        Assert.True(add.StatusCode == HttpStatusCode.OK, await add.Content.ReadAsStringAsync(Ct));
        return (await add.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private static async Task UseLibraryAsync(HttpClient client, string location)
    {
        using var use = await client.PutAsJsonAsync("/api/family-files/library", new { location }, Ct);
        Assert.True(use.StatusCode == HttpStatusCode.OK, await use.Content.ReadAsStringAsync(Ct));
    }

    private static DateTimeOffset Night(int day) => new(2026, 10, day, 4, 0, 0, TimeSpan.Zero);

    // The one that matters most: point the library somewhere new and empty, and the nightly
    // backup must not "mirror" that emptiness onto the drive that holds the old library's copy.
    [Fact]
    public async Task MovingTheLibraryToAnEmptyFolder_LeavesTheDrivesBackupAlone_AndItCanBeBroughtBack()
    {
        var dataDirectory = TempDirectory();
        var driveFolder = TempDirectory();
        var newLibrary = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();

        await UploadAsync(client, "Photos/wedding.jpg", "the wedding");
        await UploadAsync(client, "Docs/will.pdf", "the will");
        await AddDriveAsync(client, driveFolder);
        Assert.Equal("ok", (await jobs.RunNightlyAsync(Night(20), Ct)).LastNightlyResult);
        Assert.True(File.Exists(Path.Combine(driveFolder, "Photos", "wedding.jpg")));

        await UseLibraryAsync(client, newLibrary);
        await UploadAsync(client, "Photos/new.jpg", "something new");
        var night = await jobs.RunNightlyAsync(Night(21), Ct);

        Assert.Equal("the wedding", await File.ReadAllTextAsync(Path.Combine(driveFolder, "Photos", "wedding.jpg"), Ct));
        Assert.Equal("the will", await File.ReadAllTextAsync(Path.Combine(driveFolder, "Docs", "will.pdf"), Ct));
        Assert.True(File.Exists(Path.Combine(driveFolder, "Photos", "new.jpg")));
        Assert.Contains("left on it", Assert.Single(night.Drives.Values).LastMessage);

        // And the old files come back into the new library from the drive.
        var drive = Assert.Single(await factory.Services.GetRequiredService<FamilyDriveStore>().GetAllAsync(Ct));
        var restored = await jobs.RestoreFromDriveAsync(drive, Ct);
        Assert.True(restored.Ok, restored.Message);
        Assert.Equal("the wedding", await File.ReadAllTextAsync(Path.Combine(newLibrary, "Photos", "wedding.jpg"), Ct));
        Assert.Equal("the will", await File.ReadAllTextAsync(Path.Combine(newLibrary, "Docs", "will.pdf"), Ct));
    }

    // A library on a disk that didn't mount tonight is not an empty library.
    [Fact]
    public async Task ALibraryFolderThatIsMissing_FailsTheNight_AndTouchesNoDrive()
    {
        var dataDirectory = TempDirectory();
        var driveFolder = TempDirectory();
        var library = Path.Combine(TempDirectory(), "big-disk", "family");
        Directory.CreateDirectory(library);
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();

        await UseLibraryAsync(client, library);
        await UploadAsync(client, "a.txt", "aaa");
        await AddDriveAsync(client, driveFolder);
        Assert.Equal("ok", (await jobs.RunNightlyAsync(Night(20), Ct)).LastNightlyResult);

        Directory.Delete(Path.GetDirectoryName(library)!, recursive: true);
        var night = await jobs.RunNightlyAsync(Night(21), Ct);

        Assert.Equal("failed", night.LastNightlyResult);
        Assert.Contains("isn't there", night.LastNightlyMessage);
        Assert.Equal("aaa", await File.ReadAllTextAsync(Path.Combine(driveFolder, "a.txt"), Ct));
        Assert.False(Directory.Exists(library));
    }

    // Most of the library gone at once - the nightly pass holds the removal for a person; a
    // person pressing "back up now" goes ahead.
    [Fact]
    public async Task MostOfTheLibraryVanishingOvernight_IsHeldBack_UntilAPersonBacksUpByHand()
    {
        var dataDirectory = TempDirectory();
        var driveFolder = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();
        var library = Path.Combine(dataDirectory, "family-files", "library");

        for (var n = 0; n < 20; n++) await UploadAsync(client, $"Photos/{n:00}.jpg", $"photo {n}");
        var id = await AddDriveAsync(client, driveFolder);
        Assert.Equal("ok", (await jobs.RunNightlyAsync(Night(20), Ct)).LastNightlyResult);

        for (var n = 0; n < 15; n++) File.Delete(Path.Combine(library, "Photos", $"{n:00}.jpg"));
        var night = await jobs.RunNightlyAsync(Night(21), Ct);
        Assert.Equal("incomplete", night.LastNightlyResult);
        var held = Assert.Single(night.Drives.Values);
        Assert.Equal("held", held.LastResult);
        Assert.Contains("back up now", held.LastMessage);
        Assert.Equal(20, Directory.GetFiles(Path.Combine(driveFolder, "Photos")).Length);

        // A few at a time is normal life and goes through on its own.
        var drive = Assert.Single(await factory.Services.GetRequiredService<FamilyDriveStore>().GetAllAsync(Ct));
        var byHand = await jobs.SyncDriveAsync(drive, null, FamilyDriveRemovals.Allowed, Ct);
        Assert.Equal("ok", byHand.LastResult);
        Assert.Equal(5, Directory.GetFiles(Path.Combine(driveFolder, "Photos")).Length);

        File.Delete(Path.Combine(library, "Photos", "15.jpg"));
        var quiet = await jobs.RunNightlyAsync(Night(22), Ct);
        Assert.Equal("ok", Assert.Single(quiet.Drives.Values).LastResult);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(driveFolder, "Photos")).Length);
        Assert.NotNull(id);
    }

    // A folder the server can't read tonight looks, to a naive walk, like a folder that was deleted.
    [Fact]
    public async Task AFolderThatCantBeReadTonight_KeepsItsBackups_EvenWhenBackedUpByHand()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var dataDirectory = TempDirectory();
        var driveFolder = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();
        var library = Path.Combine(dataDirectory, "family-files", "library");

        await UploadAsync(client, "Private/taxes.pdf", "taxes");
        await UploadAsync(client, "Photos/dog.jpg", "dog");
        await AddDriveAsync(client, driveFolder);
        Assert.Equal("ok", (await jobs.RunNightlyAsync(Night(20), Ct)).LastNightlyResult);

        File.SetUnixFileMode(Path.Combine(library, "Private"), UnixFileMode.None);
        try
        {
            var night = await jobs.RunNightlyAsync(Night(21), Ct);
            Assert.Contains("couldn't be read", night.LastNightlyMessage);
            var drive = Assert.Single(await factory.Services.GetRequiredService<FamilyDriveStore>().GetAllAsync(Ct));
            var byHand = await jobs.SyncDriveAsync(drive, null, FamilyDriveRemovals.Allowed, Ct);
            Assert.Equal("held", byHand.LastResult);
            Assert.Equal("taxes", await File.ReadAllTextAsync(Path.Combine(driveFolder, "Private", "taxes.pdf"), Ct));
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(library, "Private"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The drive already has a file of the owner's at the same name: never overwritten. The
    // same file is simply taken as backed up.
    [Fact]
    public async Task AFileOnTheDriveThatTheLibraryDidntPutThere_IsNeverOverwritten()
    {
        var dataDirectory = TempDirectory();
        var driveFolder = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        var jobs = factory.Services.GetRequiredService<FamilyFilesJobs>();

        Directory.CreateDirectory(Path.Combine(driveFolder, "Photos"));
        await File.WriteAllTextAsync(Path.Combine(driveFolder, "Photos", "beach.jpg"), "the owner's own beach photo", Ct);
        await File.WriteAllTextAsync(Path.Combine(driveFolder, "Photos", "same.jpg"), "identical", Ct);
        await UploadAsync(client, "Photos/beach.jpg", "the library's beach photo");
        await UploadAsync(client, "Photos/same.jpg", "identical");
        await AddDriveAsync(client, driveFolder);

        var drive = Assert.Single(await factory.Services.GetRequiredService<FamilyDriveStore>().GetAllAsync(Ct));
        var outcome = await jobs.SyncDriveAsync(drive, null, FamilyDriveRemovals.Allowed, Ct);
        Assert.Equal("failed", outcome.LastResult);
        Assert.Contains("Photos/beach.jpg", outcome.LastMessage);
        Assert.Equal("the owner's own beach photo", await File.ReadAllTextAsync(Path.Combine(driveFolder, "Photos", "beach.jpg"), Ct));

        // The identical one is now counted as backed up; deleting it from the library (to the
        // trash) later removes it from the drive like any other.
        using (var delete = await client.DeleteAsync("/api/family-files/files/Photos/same.jpg", Ct)) Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        await File.WriteAllTextAsync(Path.Combine(driveFolder, "Photos", "beach.jpg"), "the library's beach photo", Ct);
        Assert.Equal("ok", (await jobs.SyncDriveAsync(drive, null, FamilyDriveRemovals.Allowed, Ct)).LastResult);
        Assert.False(File.Exists(Path.Combine(driveFolder, "Photos", "same.jpg")));
    }

    [Fact]
    public async Task ADriveInsideTheLibrary_OrTheLibraryInsideADrive_IsRefused()
    {
        var dataDirectory = TempDirectory();
        using var factory = CreateWebFactory(dataDirectory);
        using var client = factory.CreateClient();
        var library = Path.Combine(dataDirectory, "family-files", "library");
        await UploadAsync(client, "a.txt", "a");

        using (var inside = await client.PostAsJsonAsync("/api/family-files/drives", new { name = "Oops", kind = "folder", folderPath = Path.Combine(library, "backup") }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, inside.StatusCode);
            Assert.Contains("inside the library", await inside.Content.ReadAsStringAsync(Ct));
        }

        var driveFolder = TempDirectory();
        await AddDriveAsync(client, driveFolder);
        using var into = await client.PutAsJsonAsync("/api/family-files/library", new { location = Path.Combine(driveFolder, "Library") }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, into.StatusCode);
        Assert.Contains("inside a backup drive", await into.Content.ReadAsStringAsync(Ct));
    }

    // The connection to the library's computer drops halfway through deleting a folder: what
    // already reached the trash must be listed there, and what didn't stays in the library.
    [Fact]
    public async Task ADeletionThatFailsHalfway_StillShowsWhatReachedTheTrash()
    {
        var library = TempDirectory();
        var trashRoot = TempDirectory();
        Directory.CreateDirectory(Path.Combine(library, "Docs"));
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            await File.WriteAllTextAsync(Path.Combine(library, "Docs", name), name, Ct);

        var store = new FailingStore(new LocalFamilyLibraryStore(library), failOnMove: 2);
        var trash = new FamilyFilesTrash(trashRoot);
        await Assert.ThrowsAsync<IOException>(() => trash.MoveAsync(store, ["Docs/a.txt", "Docs/b.txt", "Docs/c.txt"], FamilyFilesTrash.ReasonDeleted, "test", Ct));

        var batch = Assert.Single(await trash.ListAsync(Ct));
        Assert.Equal("Docs/a.txt", Assert.Single(batch.Files).Path);
        Assert.True(File.Exists(Path.Combine(library, "Docs", "b.txt")));
        Assert.True(File.Exists(Path.Combine(library, "Docs", "c.txt")));
        Assert.Equal(1, await trash.RestoreAsync(store.Inner, batch.Id, null, Ct));
        Assert.Equal("a.txt", await File.ReadAllTextAsync(Path.Combine(library, "Docs", "a.txt"), Ct));
    }

    private sealed class FailingStore(LocalFamilyLibraryStore inner, int failOnMove) : IFamilyLibraryStore
    {
        private int _moves;
        public LocalFamilyLibraryStore Inner => inner;
        public string Root => inner.Root;
        public bool IsNetwork => true;
        public FamilyStoreEntry? Stat(string relative) => inner.Stat(relative);
        public IReadOnlyList<FamilyStoreEntry>? List(string folder) => inner.List(folder);
        public IEnumerable<FamilyStoreEntry> Walk(string folder, Action<string>? unreadable = null) => inner.Walk(folder, unreadable);
        public void CreateFolder(string relative) => inner.CreateFolder(relative);
        public Stream OpenRead(string relative) => inner.OpenRead(relative);
        public Stream CreateNew(string relative) => inner.CreateNew(relative);
        public void Move(string from, string to) => inner.Move(from, to);
        public void SetModified(string relative, DateTime utc) => inner.SetModified(relative, utc);
        public void DeleteFile(string relative) => inner.DeleteFile(relative);
        public void DeleteEmptyFolders(string relative) => inner.DeleteEmptyFolders(relative);
        public string? LocalPath(string relative) => null;
        public Task MoveInAsync(string localPath, string relative, CancellationToken ct) => inner.MoveInAsync(localPath, relative, ct);

        public Task MoveOutAsync(string relative, string localPath, CancellationToken ct) =>
            ++_moves == failOnMove ? throw new IOException("The connection to the share was lost.") : inner.MoveOutAsync(relative, localPath, ct);
    }
}
