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
            var (count, bytes) = state.IndexedUtc is null ? await library.TotalsAsync(ct) : (state.LibraryFiles, state.LibraryBytes);

            return Results.Ok(new
            {
                enabled = true,
                isAdmin = admin,
                username = context.User.Identity?.Name,
                libraryFolder = admin ? await library.RootAsync(ct) : null,
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
            return options.Enabled
                ? await next(invocation)
                : Results.NotFound(new { error = "Family Files is turned off in Settings." });
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
            return Results.File(file.FullName, contentType,
                UploadedContent.DownloadNameFor(context, contentType, file.Name, download == true), file.LastWriteTimeUtc, enableRangeProcessing: true);
        });

        enabled.MapGet("/thumbnail/{**path}", async (string path, HttpContext context, FamilyFilesLibrary library, CancellationToken ct) =>
        {
            if (FamilyFilePaths.Normalize(path) is not { Length: > 0 } relative) return BadPath();
            var file = await library.FindAsync(relative, ct);
            if (file is null) return Results.NotFound();
            var thumbnail = await library.ThumbnailAsync(relative, file, ct);
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
            var restored = await trash.RestoreAsync(await library.RootAsync(ct), request.BatchId.Trim(), only, ct);
            return Results.Ok(new { restored, detail = restored == 0 ? "Nothing to put back - it may already be gone." : $"Put back {restored} file(s)." });
        });

        // ---- admin: drives and jobs -------------------------------------------------------

        var admin = enabled.MapGroup("").RequireAuthorization("AdminOnly");

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
