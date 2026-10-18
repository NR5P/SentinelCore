using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;

namespace SentinelCore.Web;

public sealed class FamilyFolderRequest
{
    public string Path { get; set; } = "";
}

public sealed class FamilyMoveRequest
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

// Where the library should live. For a shared folder on another computer, a username and
// password for it; blank keeps the sign-in already saved for that computer (or tries as a guest).
public sealed class FamilyLibraryLocationRequest
{
    public string Location { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
}

public sealed class FamilyTrashRestoreRequest
{
    public string BatchId { get; set; } = "";
    public string? Path { get; set; }
}

// /api/family-files - see FamilyFiles.cs for what the library is. Every signed-in person can
// browse, add, fetch, rename and delete (to the trash); the drives, the trash bin's emptying and
// the jobs are the admin's.
public static class FamilyFilesEndpoints
{
    public static void MapFamilyFilesEndpoints(this WebApplication app)
    {
        var files = app.MapGroup("/api/family-files").RequireAuthorization();

        // Always answers, even when the feature is off, so clients know whether to show the tab.
        files.MapGet("/status", async (HttpContext context, FamilyFilesLocations locations, FamilyFilesLibrary library,
            FamilyDriveStore drives, FamilyFilesStateFile stateFile, FamilyFilesJobs jobs, FamilyFilesTrash trash, CancellationToken ct) =>
        {
            var options = await locations.OptionsAsync(ct);
            var admin = IsAdmin(context);
            if (!options.Enabled)
                return Results.Ok(new { enabled = false, isAdmin = admin, username = context.User.Identity?.Name });

            var state = await stateFile.LoadAsync(ct);
            var all = await drives.GetAllAsync(ct);
            var trashed = await trash.ListAsync(ct);
            var store = await library.StoreAsync(ct);
            // A library on another computer that is switched off still lets the tab open, saying
            // why nothing lists.
            string? problem = null;
            int count;
            long bytes;
            try
            {
                (count, bytes) = state.IndexedUtc is null ? await library.TotalsAsync(ct) : (state.LibraryFiles, state.LibraryBytes);
                if (store.IsNetwork && state.IndexedUtc is not null && await Task.Run(() => store.Stat(""), ct) is null)
                    problem = $"{store.Root} isn't there any more - it is made again when the first file is added.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                (count, bytes) = (state.LibraryFiles, state.LibraryBytes);
                problem = exception.Message;
            }

            return Results.Ok(new
            {
                enabled = true,
                isAdmin = admin,
                username = context.User.Identity?.Name,
                libraryFolder = admin ? store.Root : null,
                libraryOnNetwork = store.IsNetwork,
                libraryProblem = problem,
                maxUploadBytes = (long)options.MaxUploadMegabytes * 1024 * 1024,
                library = new { files = count, bytes, indexedUtc = state.IndexedUtc },
                drives = all.Select(d => d.Public(state.Drives.GetValueOrDefault(d.Id), admin)).ToList(),
                nightly = new
                {
                    hour = options.Hour,
                    minute = options.Minute,
                    removeDuplicates = options.RemoveDuplicates,
                    trashKeepDays = options.TrashKeepDays,
                    nextRunUtc = await jobs.NextRunAsync(ct),
                    lastRunUtc = state.LastNightlyUtc,
                    lastResult = state.LastNightlyResult,
                    lastMessage = state.LastNightlyMessage
                },
                duplicates = state.Duplicates is { } report ? new
                {
                    ranUtc = report.RanUtc,
                    removed = report.Removed,
                    groups = report.Groups,
                    copies = report.Copies,
                    bytes = report.Bytes,
                    filesChecked = report.FilesChecked,
                    trashBatchId = report.TrashBatchId
                } : null,
                trash = new { batches = trashed.Count, files = trashed.Sum(b => b.Files.Count), bytes = trashed.Sum(b => b.Files.Sum(f => f.Size)) },
                job = jobs.Current
            });
        });

        var enabled = files.MapGroup("").AddEndpointFilter(async (invocation, next) =>
        {
            var locations = invocation.HttpContext.RequestServices.GetRequiredService<FamilyFilesLocations>();
            var options = await locations.OptionsAsync(invocation.HttpContext.RequestAborted);
            if (!options.Enabled) return Results.NotFound(new { error = "Family Files is turned off in Settings." });
            try
            {
                return await next(invocation);
            }
            // A library on another computer that is off, or that turned down the sign-in.
            catch (MediaNetworkException exception)
            {
                return Results.Json(new { error = $"The library is on another computer that can't be reached: {exception.Message}", needsLogin = exception.NeedsLogin },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (UnauthorizedAccessException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // ---- the library ------------------------------------------------------------------

        enabled.MapGet("/list", async (string? path, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(path ?? "") is not { } folder) return BadPath();
            var listing = await library.ListAsync(folder, ct);
            return listing is null ? Results.NotFound(new { error = "That folder isn't in the library." }) : Results.Ok(ToWire(listing));
        });

        enabled.MapGet("/search", async (string? q, int? limit, FamilyFilesLibrary library, CancellationToken ct) =>
            Results.Ok(new { files = (await library.SearchAsync(q, Math.Clamp(limit ?? 100, 1, 1000), ct)).Select(ToWire).ToList() }));

        enabled.MapPost("/folders", async (FamilyFolderRequest request, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(request.Path) is not { Length: > 0 } folder) return BadPath();
            return await library.CreateFolderAsync(folder, ct)
                ? Results.Ok(new { path = folder })
                : Results.Conflict(new { error = "There is already a file with that name." });
        });

        // Raw body, the path in the URL: PUT /api/family-files/files/Photos/2026/beach.jpg. The
        // optional `modified` query keeps the file's original date (what the apps send for a
        // photo), so "oldest copy" in the duplicate check means what it says.
        enabled.MapPut("/files/{**path}", async (string path, DateTimeOffset? modified, HttpContext context,
            FamilyFilesLocations locations, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(path) is not { Length: > 0 } relative) return BadPath();
            var options = await locations.OptionsAsync(ct);
            var maxBytes = (long)options.MaxUploadMegabytes * 1024 * 1024;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = maxBytes;
            if (context.Request.ContentLength > maxBytes) return TooLarge(options);

            try
            {
                var saved = await library.SaveAsync(FamilyFilePaths.ParentOf(relative), FamilyFilePaths.NameOf(relative),
                    context.Request.Body, maxBytes, modified, ct);
                return Results.Ok(new { path = saved.Path, name = FamilyFilePaths.NameOf(saved.Path), size = saved.Size, sha256 = saved.Sha256, alreadyThere = saved.AlreadyThere });
            }
            catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)
            {
                return TooLarge(options);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = $"Could not save the file: {exception.Message}" }, statusCode: StatusCodes.Status507InsufficientStorage);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        enabled.MapGet("/files/{**path}", async (string path, bool? download, HttpContext context, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(path) is not { Length: > 0 } relative) return BadPath();
            var file = await library.FindAsync(relative, ct);
            if (file is null) return Results.NotFound(new { error = "That file isn't in the library." });
            context.Response.Headers.CacheControl = "private, no-cache";
            var contentType = FamilyFilePaths.ContentTypeFor(file.Name);
            var downloadName = UploadedContent.DownloadNameFor(context, contentType, file.Name, download == true);
            var modified = new DateTimeOffset(DateTime.SpecifyKind(file.ModifiedUtc, DateTimeKind.Utc));
            var store = await library.StoreAsync(ct);
            if (store.LocalPath(relative) is { } local)
                return Results.File(local, contentType, downloadName, modified, enableRangeProcessing: true);
            // Seekable, so a video on the other computer still scrubs.
            return Results.Stream(await Task.Run(() => store.OpenRead(relative), ct), contentType, downloadName, modified, enableRangeProcessing: true);
        });

        enabled.MapGet("/thumbnail/{**path}", async (string path, HttpContext context, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(path) is not { Length: > 0 } relative) return BadPath();
            var file = await library.FindAsync(relative, ct);
            if (file is null) return Results.NotFound();
            var thumbnail = await library.ThumbnailAsync(file, ct);
            if (thumbnail is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(thumbnail, "image/jpeg");
        });

        enabled.MapDelete("/files/{**path}", async (string path, HttpContext context, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(path) is not { Length: > 0 } relative) return BadPath();
            var batch = await library.DeleteAsync(relative, context.User.Identity?.Name, ct);
            return batch is null
                ? Results.NotFound(new { error = "That file or folder isn't in the library." })
                : Results.Ok(new { trashed = batch.Files.Count, batchId = batch.Id });
        });

        enabled.MapPost("/move", async (FamilyMoveRequest request, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(request.From) is not { } from || FamilyFilePaths.Normalize(request.To) is not { } to) return BadPath();
            var error = await library.MoveAsync(from, to, ct);
            return error is null ? Results.Ok(new { path = to }) : Results.BadRequest(new { error });
        });

        // ---- the trash --------------------------------------------------------------------

        enabled.MapGet("/trash", async (FamilyFilesTrash trash, CancellationToken ct) =>
            Results.Ok(new { batches = (await trash.ListAsync(ct)).Select(b => new
            {
                id = b.Id,
                deletedUtc = b.DeletedUtc,
                reason = b.Reason,
                by = b.By,
                files = b.Files.Select(f => new { path = f.Path, name = FamilyFilePaths.NameOf(f.Path), size = f.Size, keptCopy = f.KeptCopy }).ToList()
            }).ToList() }));

        enabled.MapPost("/trash/restore", async (FamilyTrashRestoreRequest request, FamilyFilesLibrary library, FamilyFilesTrash trash, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.BatchId) || request.BatchId.Contains('/') || request.BatchId.Contains('\\') || request.BatchId.Contains(".."))
                return Results.BadRequest(new { error = "Choose what to put back." });
            var only = request.Path is null ? null : FamilyFilePaths.Normalize(request.Path);
            var restored = await trash.RestoreAsync(await library.StoreAsync(ct), request.BatchId.Trim(), only, ct);
            return Results.Ok(new { restored, detail = restored == 0 ? "Nothing to put back - it may already be gone." : $"Put back {restored} file(s)." });
        });

        // ---- admin: drives and jobs -------------------------------------------------------

        var admin = enabled.MapGroup("").RequireAuthorization("AdminOnly");

        // ---- admin: where the library lives ----------------------------------------------

        // Signs in (saving the sign-in when one is given and works) and proves this server can
        // make, write and delete a file there - nothing about the library changes.
        admin.MapPost("/library/test", async (FamilyLibraryLocationRequest request, FamilyFilesLocations locations, CancellationToken ct) =>
        {
            var (error, _, detail) = await CheckLibraryAsync(request, locations, ct);
            return error ?? Results.Ok(new { detail });
        });

        // The same check, then the library moves there. Files already in the old place stay
        // where they are; "bring files back" from a drive fills the new place.
        admin.MapPut("/library", async (FamilyLibraryLocationRequest request, FamilyFilesLocations locations, AppSettingsFile settings,
            FamilyFilesIndex index, FamilyFilesStateFile stateFile, CancellationToken ct) =>
        {
            var (error, location, detail) = await CheckLibraryAsync(request, locations, ct);
            if (error is not null) return error;
            var options = await locations.OptionsAsync(ct);
            var changed = !string.Equals(options.LibraryFolder?.Trim() ?? "", location, StringComparison.Ordinal);
            options.LibraryFolder = location;
            await settings.MergeSectionAsync(FamilyFilesSettingsDto.SectionName, options, ct);
            if (changed)
            {
                await index.ClearAsync(ct);
                await stateFile.UpdateAsync(s => s with { IndexedUtc = null, LibraryFiles = 0, LibraryBytes = 0, Duplicates = null }, ct);
            }
            return Results.Ok(new { detail = changed ? $"The library is now {locations.StoreFor(location).Root}. {detail}" : detail, libraryFolder = locations.StoreFor(location).Root });
        });

        admin.MapDelete("/trash", async (FamilyFilesTrash trash, CancellationToken ct) =>
        {
            var removed = await trash.PurgeAsync(TimeSpan.Zero, ct);
            return Results.Ok(new { removed, detail = removed == 0 ? "The trash was already empty." : $"Deleted {removed} file(s) for good." });
        });

        admin.MapGet("/drives", async (FamilyDriveStore drives, FamilyFilesStateFile stateFile, CancellationToken ct) =>
        {
            var state = await stateFile.LoadAsync(ct);
            return Results.Ok(new { drives = (await drives.GetAllAsync(ct)).Select(d => d.Public(state.Drives.GetValueOrDefault(d.Id), admin: true)).ToList() });
        });

        admin.MapPost("/drives", async (FamilyDrive drive, FamilyDriveStore drives, FamilyFilesStateFile stateFile, CancellationToken ct) =>
        {
            drive.Id = "";
            return await SaveDriveAsync(drive, drives, stateFile, ct);
        });

        admin.MapPut("/drives/{id}", async (string id, FamilyDrive drive, FamilyDriveStore drives, FamilyFilesStateFile stateFile, CancellationToken ct) =>
        {
            if (await drives.FindAsync(id, ct) is null) return Results.NotFound(new { error = "That drive is no longer set up." });
            drive.Id = id;
            return await SaveDriveAsync(drive, drives, stateFile, ct);
        });

        admin.MapDelete("/drives/{id}", async (string id, FamilyDriveStore drives, FamilyFilesStateFile stateFile, CancellationToken ct) =>
        {
            if (!await drives.RemoveAsync(id, ct)) return Results.NotFound(new { error = "That drive is no longer set up." });
            await stateFile.UpdateAsync(s =>
            {
                var updated = new Dictionary<string, FamilyDriveState>(s.Drives, StringComparer.Ordinal);
                updated.Remove(id);
                return s with { Drives = updated };
            }, ct);
            return Results.Ok(new { removed = true, detail = "The drive was removed from the list. Nothing on it was touched." });
        });

        // Connects and reports what it finds. Takes the drive as sent, so the form can be tested
        // before it is saved; a blank password on an existing drive means "the saved one".
        admin.MapPost("/drives/test", async (FamilyDrive drive, FamilyDriveStore drives, CancellationToken ct) =>
        {
            drive.Normalized();
            if (drive.IsSftp && string.IsNullOrEmpty(drive.SftpPassword) && drive.Id.Length > 0 && await drives.FindAsync(drive.Id, ct) is { } saved)
                drive.SftpPassword = saved.SftpPassword;
            var problems = Validate(drive);
            if (problems.Count > 0) return Results.BadRequest(new { error = string.Join(" ", problems), errors = problems });
            try
            {
                var (_, message, hostKey) = await FamilyDriveSync.TestAsync(drive, ct);
                return Results.Ok(new { detail = message + (hostKey is null ? "" : $" Host key {hostKey}."), hostKeyFingerprint = hostKey });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.BadRequest(new { error = FamilyDriveSync.Describe(exception), hostKeyFingerprint = (exception as FamilyDriveHostKeyException)?.Seen });
            }
        });

        admin.MapPost("/drives/{id}/sync", async (string id, FamilyDriveStore drives, FamilyFilesJobs jobs, CancellationToken ct) =>
        {
            if (await drives.FindAsync(id, ct) is not { } drive) return Results.NotFound(new { error = "That drive is no longer set up." });
            return jobs.TryStart(FamilyFilesJobs.KindSync, drive.Id, drive.Name, token => jobs.SyncDriveAsync(drive, null, token))
                ? Results.Accepted(null, new { started = true, detail = $"Backing up to {drive.Name} - progress shows on the Files tab." })
                : Busy(jobs);
        });

        admin.MapPost("/drives/{id}/restore", async (string id, FamilyDriveStore drives, FamilyFilesJobs jobs, CancellationToken ct) =>
        {
            if (await drives.FindAsync(id, ct) is not { } drive) return Results.NotFound(new { error = "That drive is no longer set up." });
            return jobs.TryStart(FamilyFilesJobs.KindRestore, drive.Id, drive.Name, token => jobs.RestoreFromDriveAsync(drive, token))
                ? Results.Accepted(null, new { started = true, detail = $"Bringing back anything on {drive.Name} that the library doesn't have - progress shows on the Files tab." })
                : Busy(jobs);
        });

        // Every enabled drive, in the background. Also the settings section's "back up now".
        admin.MapPost("/sync", async (FamilyDriveStore drives, FamilyFilesJobs jobs, CancellationToken ct) =>
        {
            var enabledDrives = (await drives.GetAllAsync(ct)).Where(d => d.Enabled).ToList();
            if (enabledDrives.Count == 0)
                return Results.BadRequest(new { error = "No backup drives are set up yet - add one on the Files tab." });
            var started = jobs.TryStart(FamilyFilesJobs.KindSync, null, null, async token =>
            {
                var files = await jobs.IndexAsync(token);
                foreach (var drive in enabledDrives) await jobs.SyncDriveAsync(drive, files, token);
            });
            return started
                ? Results.Accepted(null, new { started = true, detail = $"Backing up to {enabledDrives.Count} drive(s) - progress shows on the Files tab." })
                : Busy(jobs);
        });

        admin.MapPost("/duplicates/check", (bool? remove, HttpContext context, FamilyFilesJobs jobs) =>
        {
            var by = context.User.Identity?.Name;
            return jobs.TryStart(FamilyFilesJobs.KindDuplicates, null, null, token => jobs.CheckDuplicatesAsync(remove ?? false, by, token))
                ? Results.Accepted(null, new { started = true, detail = remove == true ? "Checking for duplicates and moving copies to the trash." : "Checking for duplicates." })
                : Busy(jobs);
        });

        admin.MapGet("/duplicates", async (FamilyFilesStateFile stateFile, CancellationToken ct) =>
            Results.Ok(new { report = (await stateFile.LoadAsync(ct)).Duplicates }));

        admin.MapPost("/nightly/run", (FamilyFilesJobs jobs) =>
            jobs.TryStart(FamilyFilesJobs.KindNightly, null, null, token => jobs.RunNightlyAsync(null, token))
                ? Results.Accepted(null, new { started = true, detail = "Running the nightly pass now - progress shows on the Files tab." })
                : Busy(jobs));

        enabled.MapGet("/job", (FamilyFilesJobs jobs) => Results.Ok(new { job = jobs.Current }));
    }

    private static async Task<(IResult? Error, string Location, string Detail)> CheckLibraryAsync(
        FamilyLibraryLocationRequest request, FamilyFilesLocations locations, CancellationToken ct)
    {
        if (FamilyLibraryLocation.Normalize(request.Location, out var problem) is not { } location)
            return (Results.BadRequest(new { error = problem }), "", "");
        var network = locations.Network;
        try
        {
            if (FamilyLibraryLocation.IsNetwork(location))
            {
                if (network is null) return (Results.BadRequest(new { error = "This server can't reach shared folders." }), "", "");
                MediaNetworkPath.TryParse(location, out var path);
                var computer = path with { Share = "", Rest = "" };
                var existing = network.Computers.ForHost(computer.HostKey);
                var username = request.Username?.Trim() ?? "";
                var domain = request.Domain?.Trim() ?? "";
                if (domain.Length == 0 && username.Contains('\\'))
                {
                    var slash = username.IndexOf('\\');
                    (domain, username) = (username[..slash], username[(slash + 1)..]);
                }
                if (username.Length > 0)
                {
                    var password = string.IsNullOrEmpty(request.Password) && existing is not null && string.Equals(existing.Username, username, StringComparison.OrdinalIgnoreCase)
                        ? existing.Password
                        : request.Password ?? "";
                    var candidate = new MediaNetworkComputer(existing?.Id ?? Guid.NewGuid(), computer.HostWithPort, username, password, domain,
                        existing?.CreatedAt ?? DateTimeOffset.UtcNow, existing?.Name);
                    await Task.Run(() => network.ListShares(computer, candidate), ct);
                    // Shared with Movies: one sign-in per computer.
                    network.Computers.Save(candidate);
                }
            }

            var store = locations.StoreFor(location);
            var detail = await Task.Run(() =>
            {
                store.CreateFolder("");
                var probe = $".sentinelcore-write-check-{Guid.NewGuid():N}";
                using (var output = store.CreateNew(probe))
                {
                    output.Write("Sentinel Core checking it can write here."u8);
                    output.Flush();
                }
                var written = store.Stat(probe);
                store.DeleteFile(probe);
                if (written is not { Size: > 0 }) throw new IOException("A test file came out empty - the disk may be full.");
                var inside = store.List("")?.Count(e => !e.Name.StartsWith('.')) ?? 0;
                return $"Reached {store.Root} and wrote a test file there" +
                    (inside == 0 ? "; it is empty." : $"; it already holds {inside} file(s) and folder(s) at the top, which will show in the library.");
            }, ct);
            return (null, location, detail);
        }
        catch (MediaNetworkException exception)
        {
            return (Results.BadRequest(new { error = exception.Message, needsLogin = exception.NeedsLogin }), "", "");
        }
        catch (UnauthorizedAccessException exception)
        {
            return (Results.BadRequest(new { error = $"{exception.Message} The account needs permission to change files in that folder, not just read them.", needsLogin = true }), "", "");
        }
        catch (IOException exception)
        {
            return (Results.BadRequest(new { error = exception.Message }), "", "");
        }
    }

    private static async Task<IResult> SaveDriveAsync(FamilyDrive drive, FamilyDriveStore drives, FamilyFilesStateFile stateFile, CancellationToken ct)
    {
        drive.Normalized();
        if (drive.IsSftp && string.IsNullOrEmpty(drive.SftpPassword) && drive.Id.Length > 0 && await drives.FindAsync(drive.Id, ct) is { } saved)
            drive.SftpPassword = saved.SftpPassword;
        var problems = Validate(drive);
        if (problems.Count > 0) return Results.BadRequest(new { errors = problems });
        var stored = await drives.UpsertAsync(drive, ct);
        var state = await stateFile.LoadAsync(ct);
        return Results.Ok(stored.Public(state.Drives.GetValueOrDefault(stored.Id), admin: true));
    }

    private static List<string> Validate(FamilyDrive drive)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(drive, new ValidationContext(drive), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid.").ToList();
    }

    private static bool IsAdmin(HttpContext context) => context.User.IsInRole(nameof(AccountRole.Admin));

    private static IResult BadPath() =>
        Results.BadRequest(new { error = "That name can't be used: no leading dots, and none of < > : \" | ? * \\ in a name." });

    private static IResult TooLarge(FamilyFilesSettingsDto options) =>
        Results.Json(new { error = $"Files can be at most {options.MaxUploadMegabytes} MB - the limit is in Settings -> Family Files." },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult Busy(FamilyFilesJobs jobs) =>
        Results.Conflict(new { error = $"Something is already running ({jobs.Current?.Message ?? "a job"}) - wait for it to finish." });

    private static object ToWire(FamilyFolderListing listing) => new
    {
        path = listing.Path,
        name = listing.Path.Length == 0 ? "Family Files" : FamilyFilePaths.NameOf(listing.Path),
        parent = listing.Path.Length == 0 ? null : FamilyFilePaths.ParentOf(listing.Path),
        folders = listing.Folders.Select(f => new { path = f.Path, name = f.Name }).ToList(),
        files = listing.Files.Select(ToWire).ToList()
    };

    private static object ToWire(FamilyFileEntry file) => new
    {
        path = file.Path,
        name = file.Name,
        size = file.Size,
        modifiedUtc = file.ModifiedUtc,
        sha256 = file.Sha256,
        isImage = file.IsImage,
        contentType = FamilyFilePaths.ContentTypeFor(file.Name)
    };
}
