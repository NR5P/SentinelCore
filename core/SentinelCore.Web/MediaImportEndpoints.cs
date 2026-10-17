namespace SentinelCore.Web;

// "Import a list" on Music (/api/music/downloads/...) and Movies (/api/media/downloads/...).
// Mapped inside each tab's own "downloads on and allowed" group. Contract:
// core/docs/MOVIE_DOWNLOADS.md "Import a list".
public static class MediaImportEndpoints
{
    private const string GoneMessage = "That list is gone.";
    private const string NotYoursMessage = "Only the person who imported this list, or an admin, can change it.";
    // A Spotify export of a big library is a few MB; nothing real comes near this.
    private const int MaxTextLength = 25_000_000;

    public static void MapImports(this RouteGroupBuilder enabled, string kind)
    {
        enabled.MapPost("/import/preview", async (MediaImportPreviewInput input, MediaImportService imports, CancellationToken ct) =>
        {
            if (input.Files.Count == 0 || input.Files.All(f => string.IsNullOrWhiteSpace(f.Text)))
                return Results.BadRequest(new { error = "Choose a file to import." });
            if (input.Files.Sum(f => (long)(f.Text?.Length ?? 0)) > MaxTextLength)
                return Results.BadRequest(new { error = "That file is too big to be a list." });
            return Results.Ok(await imports.PreviewAsync(input, kind, ct));
        });

        enabled.MapPost("/imports", async (MediaImportStartInput input, HttpContext context, MediaImportService imports, CancellationToken ct) =>
        {
            try
            {
                var job = await imports.StartAsync(kind, input, context.User.Identity?.Name ?? "", IsAdmin(context), ct);
                return Results.Created($"{context.Request.Path}/{job.Id}", JobDto(job, new Dictionary<Guid, MediaDownload>()));
            }
            catch (MediaDownloadException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        enabled.MapGet("/imports", async (HttpContext context, MediaImportService imports, CancellationToken ct) =>
        {
            var downloads = await imports.DownloadsAsync(ct);
            var jobs = (await imports.ListAsync(kind, ct)).Where(j => MayManage(context, j));
            return Results.Ok(new { imports = jobs.Select(j => JobDto(j, downloads, withItems: false)) });
        });

        enabled.MapGet("/imports/{id:guid}", async (Guid id, HttpContext context, MediaImportService imports, CancellationToken ct) =>
            await imports.GetAsync(id, ct) is { } job && job.Kind == kind && MayManage(context, job)
                ? Results.Ok(JobDto(job, await imports.DownloadsAsync(ct)))
                : Results.NotFound(new { error = GoneMessage }));

        enabled.MapPost("/imports/{id:guid}/pause", (Guid id, MediaImportPauseInput input, HttpContext context, MediaImportService imports, CancellationToken ct) =>
            Change(id, context, imports, kind, job => { job.Paused = input.Paused; return true; }, ct));

        // Everything not found or failed goes back on the list to be searched again.
        enabled.MapPost("/imports/{id:guid}/retry", (Guid id, HttpContext context, MediaImportService imports, CancellationToken ct) =>
            Change(id, context, imports, kind, job =>
            {
                foreach (var item in job.Items.Where(i => i.State is MediaImportStates.NotFound or MediaImportStates.Failed))
                {
                    item.State = MediaImportStates.Waiting;
                    item.Message = null;
                }
                job.Paused = false;
                return true;
            }, ct));

        // Leaves one album or film out. A download already queued for it carries on.
        enabled.MapDelete("/imports/{id:guid}/items/{itemId}", (Guid id, string itemId, HttpContext context, MediaImportService imports, CancellationToken ct) =>
            Change(id, context, imports, kind, job =>
            {
                if (job.Items.FirstOrDefault(i => i.Id == itemId) is not { State: MediaImportStates.Waiting or MediaImportStates.Searching or MediaImportStates.NotFound or MediaImportStates.Failed } item) return false;
                item.State = MediaImportStates.Canceled;
                item.Message = null;
                return true;
            }, ct));

        // Stops the list and forgets it. Downloads it already queued carry on in Downloads, where
        // they can be canceled one by one.
        enabled.MapDelete("/imports/{id:guid}", async (Guid id, HttpContext context, MediaImportService imports, CancellationToken ct) =>
        {
            if (await imports.GetAsync(id, ct) is not { } job || job.Kind != kind) return Results.NotFound(new { error = GoneMessage });
            if (!MayManage(context, job)) return Results.Json(new { error = NotYoursMessage }, statusCode: StatusCodes.Status403Forbidden);
            await imports.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> Change(Guid id, HttpContext context, MediaImportService imports, string kind, Func<MediaImportJob, bool> change, CancellationToken ct)
    {
        if (await imports.GetAsync(id, ct) is not { } existing || existing.Kind != kind) return Results.NotFound(new { error = GoneMessage });
        if (!MayManage(context, existing)) return Results.Json(new { error = NotYoursMessage }, statusCode: StatusCodes.Status403Forbidden);
        var job = await imports.UpdateAsync(id, change, ct);
        if (job is null) return Results.NotFound(new { error = GoneMessage });
        imports.Wake();
        return Results.Ok(JobDto(job, await imports.DownloadsAsync(ct)));
    }

    private static bool IsAdmin(HttpContext context) => context.User.IsInRole(nameof(AccountRole.Admin));

    private static bool MayManage(HttpContext context, MediaImportJob job) =>
        IsAdmin(context) || string.Equals(job.RequestedBy, context.User.Identity?.Name ?? "", StringComparison.OrdinalIgnoreCase);

    // An item's state as people see it: once queued, its download's own state.
    public static string ItemState(MediaImportItem item, IReadOnlyDictionary<Guid, MediaDownload> downloads) =>
        item.State == MediaImportStates.Queued
            ? item.DownloadId is { } d && downloads.TryGetValue(d, out var download) ? download.State : MediaDownloadStates.Done
            : item.State;

    public static object JobDto(MediaImportJob job, IReadOnlyDictionary<Guid, MediaDownload> downloads, bool withItems = true)
    {
        var states = job.Items.Select(i => ItemState(i, downloads)).ToList();
        int Count(params string[] wanted) => states.Count(s => wanted.Contains(s));
        var waiting = Count(MediaImportStates.Waiting, MediaImportStates.Searching);
        return new
        {
            id = job.Id,
            kind = job.Kind,
            name = job.Name,
            requestedBy = job.RequestedBy,
            createdAt = job.CreatedAt,
            paused = job.Paused,
            message = job.Message,
            finished = waiting == 0 && Count(MediaDownloadStates.Queued, MediaDownloadStates.Downloading, MediaDownloadStates.Seeding, MediaDownloadStates.Importing) == 0,
            counts = new
            {
                total = job.Items.Count,
                waiting,
                downloading = Count(MediaDownloadStates.Queued, MediaDownloadStates.Downloading, MediaDownloadStates.Seeding, MediaDownloadStates.Importing),
                done = Count(MediaDownloadStates.Done),
                inLibrary = Count(MediaImportStates.InLibrary),
                notFound = Count(MediaImportStates.NotFound),
                failed = Count(MediaImportStates.Failed, MediaDownloadStates.Failed),
                canceled = Count(MediaImportStates.Canceled, MediaDownloadStates.Canceled)
            },
            items = withItems
                ? job.Items.Select((i, n) =>
                {
                    MediaDownload? download = i.DownloadId is { } d && downloads.TryGetValue(d, out var found) ? found : null;
                    return (object)new
                    {
                        id = i.Id,
                        artist = i.Artist,
                        title = i.Title,
                        year = i.Year,
                        trackCount = i.TrackCount,
                        state = states[n],
                        message = download?.Error ?? i.Message,
                        progress = download is null ? (double?)null : Math.Round(download.Progress, 4),
                        downloadId = i.DownloadId,
                        releaseTitle = i.ReleaseTitle
                    };
                })
                : null
        };
    }
}

public sealed class MediaImportPauseInput
{
    public bool Paused { get; set; }
}
