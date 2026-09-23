using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace SentinelCore.Web;

public sealed class OrganizerSyncRequest
{
    // The server id this device last synced with, or null on its first ever sync. A mismatch
    // means the server was rebuilt: nothing is applied and the reply carries reset=true so the
    // device can ask its owner before uploading its copy.
    public string? ServerId { get; set; }
    public long Cursor { get; set; }
    public List<string> KnownSpaces { get; set; } = [];
    public List<OrganizerChange> Changes { get; set; } = [];

    // Group definitions carried back to a rebuilt server along with their contents.
    public List<OrganizerSpace>? RestoreSpaces { get; set; }
    public int? Limit { get; set; }
}

public sealed class OrganizerGroupRequest
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = [];
    public string? Color { get; set; }
}

public sealed class OrganizerPreferences
{
    // Holiday calendar ids from OrganizerHolidays.Sets. Null in a reply means never saved.
    public List<string>? HolidaySets { get; set; }
}

public static class OrganizerEndpoints
{
    private const int DefaultPageSize = 2000;
    private const int MaxPageSize = 5000;

    public static void MapOrganizerEndpoints(this WebApplication app)
    {
        var organizer = app.MapGroup("/api/organizer").RequireAuthorization();

        // Always answers, even when the Organizer is off, so clients know whether to show it.
        organizer.MapGet("/status", async (HttpContext context, IOrganizerStore store, AppSettingsFile settings, IAccountStore accounts, CancellationToken ct) =>
        {
            var options = await ReadOptionsAsync(settings, ct);
            var user = UserOf(context);
            string? displayName = null;
            if (user.Username is not null && await accounts.GetByUsernameAsync(user.Username, ct) is { } account)
                displayName = account.DisplayName;

            return Results.Ok(new
            {
                enabled = options.Enabled,
                serverId = store.ServerId,
                cursor = store.CurrentCursor,
                username = user.Username,
                displayName,
                // Spaces other than Family need a real person behind the request.
                personalSpaces = user.Username is not null,
                itemCount = options.Enabled ? await store.CountVisibleAsync(user, ct) : 0,
                maxAttachmentBytes = (long)options.MaxAttachmentMegabytes * 1024 * 1024,
            });
        });

        var enabled = organizer.MapGroup("").AddEndpointFilter(async (invocation, next) =>
        {
            var settings = invocation.HttpContext.RequestServices.GetRequiredService<AppSettingsFile>();
            var options = await ReadOptionsAsync(settings, invocation.HttpContext.RequestAborted);
            return options.Enabled
                ? await next(invocation)
                : Results.NotFound(new { error = "The Organizer is turned off in Settings." });
        });

        enabled.MapPost("/sync", async (OrganizerSyncRequest request, HttpContext context, IOrganizerStore store, OrganizerBlobStore blobs,
            OrganizerPushNudger nudger, CancellationToken ct) =>
        {
            var user = UserOf(context);
            var reset = request.ServerId is not null && request.ServerId != store.ServerId;
            var limit = Math.Clamp(request.Limit ?? DefaultPageSize, 1, MaxPageSize);

            IReadOnlyList<OrganizerRejection> rejected = [];
            IReadOnlyList<OrganizerItem> pushed = [];
            if (!reset && (request.Changes.Count > 0 || request.RestoreSpaces is { Count: > 0 }))
            {
                var result = await store.ApplyChangesAsync(user, request.Changes, request.RestoreSpaces, ct);
                rejected = result.Rejected;
                pushed = result.Items;
                // Wake the other devices so their widgets catch up - see OrganizerPushNudger.
                if (pushed.Count > 0) nudger.Changed();
            }

            var pull = await store.PullAsync(
                user, reset ? 0 : request.Cursor, request.KnownSpaces.ToHashSet(StringComparer.Ordinal), limit, ct);

            // Whatever this device just sent comes back merged, even if it landed past this page,
            // so the device can settle its own pending edits in one round trip.
            var items = pull.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var item in pushed) items.TryAdd(item.Id, item);

            var missingBlobs = (await store.GetVisibleBlobHashesAsync(user, ct)).Where(hash => !blobs.Exists(hash)).ToList();

            return Results.Ok(new
            {
                serverId = store.ServerId,
                reset,
                cursor = pull.Cursor,
                hasMore = pull.HasMore,
                spaces = await store.GetVisibleSpacesAsync(user, ct),
                items = items.Values.OrderBy(item => item.Seq).Select(ToWire),
                removed = pull.Removed,
                rejected,
                missingBlobs,
            });
        });

        // Long poll: answers as soon as anything changes after `cursor`, or after `timeout`
        // seconds regardless. Says nothing about whose change it was; the client just syncs.
        enabled.MapGet("/wait", async (long cursor, int? timeout, IOrganizerStore store, CancellationToken ct) =>
        {
            var seconds = Math.Clamp(timeout ?? 25, 1, 55);
            try
            {
                return Results.Ok(new { cursor = await store.WaitForChangeAsync(cursor, TimeSpan.FromSeconds(seconds), ct) });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });

        // Online browse, for clients holding only a light copy: everything of one kind (and
        // optionally one space) this person can see.
        enabled.MapGet("/items", async (string? kind, string? spaceId, HttpContext context, IOrganizerStore store, CancellationToken ct) =>
        {
            if (kind is not null && !OrganizerKinds.IsKnown(kind))
                return Results.BadRequest(new { errors = new[] { $"\"{kind}\" isn't something the Organizer holds." } });
            var items = await store.GetItemsAsync(UserOf(context), kind, spaceId, ct);
            return Results.Ok(new { items = items.Select(ToWire) });
        });

        // Who can be put in a group. Names only - never roles or anything else about the account.
        enabled.MapGet("/people", async (HttpContext context, IAccountStore accounts, CancellationToken ct) =>
        {
            if (UserOf(context).Username is null) return Results.Ok(new { people = Array.Empty<object>() });
            var all = await accounts.GetAllAsync(ct);
            return Results.Ok(new
            {
                people = all.OrderBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
                    .Select(account => new { username = account.Username.ToLowerInvariant(), displayName = account.DisplayName })
            });
        });

        enabled.MapPost("/spaces", async (OrganizerGroupRequest request, HttpContext context, IOrganizerStore store, IAccountStore accounts, CancellationToken ct) =>
        {
            var user = UserOf(context);
            var errors = await ValidateGroupAsync(user, request, accounts, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var space = await store.CreateGroupAsync(user, request.Name, request.Members, request.Color, ct);
            return Results.Ok(space);
        });

        enabled.MapPut("/spaces/{id}", async (string id, OrganizerGroupRequest request, HttpContext context, IOrganizerStore store, IAccountStore accounts, CancellationToken ct) =>
        {
            var user = UserOf(context);
            var errors = await ValidateGroupAsync(user, request, accounts, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            var space = await store.UpdateGroupAsync(user, id, request.Name, request.Members, request.Color, ct);
            return space is null ? Results.NotFound() : Results.Ok(space);
        });

        enabled.MapDelete("/spaces/{id}", async (string id, HttpContext context, IOrganizerStore store, CancellationToken ct) =>
            await store.DeleteGroupAsync(UserOf(context), id, ct) ? Results.Ok(new { deleted = true }) : Results.NotFound());

        // Upload. Anyone signed in may upload bytes - they're inert until an attachment item the
        // uploader is allowed to write points at them, and the orphan sweep collects the rest.
        enabled.MapPut("/blobs/{hash}", async (string hash, HttpContext context, OrganizerBlobStore blobs, AppSettingsFile settings, CancellationToken ct) =>
        {
            if (!OrganizerBlobStore.IsValidHash(hash))
                return Results.BadRequest(new { errors = new[] { "The name has to be the file's SHA-256." } });

            var options = await ReadOptionsAsync(settings, ct);
            var maxBytes = (long)options.MaxAttachmentMegabytes * 1024 * 1024;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = maxBytes;
            if (context.Request.ContentLength > maxBytes)
                return Results.Json(new { errors = new[] { $"Attachments can be at most {options.MaxAttachmentMegabytes} MB." } }, statusCode: StatusCodes.Status413PayloadTooLarge);

            try
            {
                return await blobs.SaveAsync(hash, context.Request.Body, maxBytes, ct)
                    ? Results.Ok(new { stored = true })
                    : Results.BadRequest(new { errors = new[] { "The file didn't match its SHA-256 - the upload was probably cut short." } });
            }
            catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException)
            {
                return Results.Json(new { errors = new[] { $"Attachments can be at most {options.MaxAttachmentMegabytes} MB." } }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        });

        enabled.MapGet("/blobs/{hash}", async (string hash, bool? download, HttpContext context, IOrganizerStore store, OrganizerBlobStore blobs, CancellationToken ct) =>
        {
            if (!OrganizerBlobStore.IsValidHash(hash)) return Results.NotFound();
            var attachment = await store.FindVisibleAttachmentAsync(UserOf(context), hash.ToLowerInvariant(), ct);
            if (attachment is null || !blobs.Exists(hash)) return Results.NotFound();

            var contentType = StringField(attachment.Fields, "contentType") ?? "application/octet-stream";
            var name = StringField(attachment.Fields, "name");
            // Content never changes under a hash, so it can be cached forever.
            context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(blobs.PathFor(hash), contentType, download == true ? name ?? hash : null, enableRangeProcessing: true);
        });

        enabled.MapGet("/blobs/{hash}/thumbnail", async (string hash, HttpContext context, IOrganizerStore store, OrganizerBlobStore blobs, CancellationToken ct) =>
        {
            if (!OrganizerBlobStore.IsValidHash(hash)) return Results.NotFound();
            if (await store.FindVisibleAttachmentAsync(UserOf(context), hash.ToLowerInvariant(), ct) is null) return Results.NotFound();
            var thumbnail = await blobs.GetThumbnailAsync(hash, ct);
            if (thumbnail is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(thumbnail, "image/jpeg");
        });

        // Holiday calendars: what there is, and the days in the ones asked for. Computed, not
        // stored - see OrganizerHolidays.
        enabled.MapGet("/holidays/sets", () =>
            Results.Ok(new { sets = OrganizerHolidays.Sets.Select(set => new { id = set.Id, name = set.Name, description = set.Description }) }));

        enabled.MapGet("/holidays", (string? sets, int? from, int? to) =>
        {
            var thisYear = DateTime.UtcNow.Year;
            var fromYear = from ?? thisYear - 1;
            var toYear = to ?? thisYear + 2;
            if (toYear < fromYear || toYear - fromYear > 10)
                return Results.BadRequest(new { error = "Ask for at most ten years at a time." });
            var ids = (sets ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Ok(new
            {
                from = fromYear,
                to = toYear,
                holidays = OrganizerHolidays.Between(ids, fromYear, toYear).Select(HolidayToWire),
            });
        });

        // Per-person preferences, so a choice made on one device shows on all of them.
        enabled.MapGet("/preferences", async (HttpContext context, IOrganizerStore store, CancellationToken ct) =>
            Results.Ok(ReadPreferences(await store.GetPreferencesAsync(UserOf(context), ct)) ?? new OrganizerPreferences()));

        enabled.MapPut("/preferences", async (OrganizerPreferences request, HttpContext context, IOrganizerStore store, CancellationToken ct) =>
        {
            var unknown = (request.HolidaySets ?? []).Where(id => !OrganizerHolidays.IsKnown(id)).ToList();
            if (unknown.Count > 0)
                return Results.BadRequest(new { error = $"There's no holiday calendar called {string.Join(", ", unknown)}." });
            var saved = new OrganizerPreferences { HolidaySets = (request.HolidaySets ?? []).Distinct().ToList() };
            await store.SetPreferencesAsync(UserOf(context), System.Text.Json.JsonSerializer.Serialize(saved, PreferencesJson), ct);
            return Results.Ok(saved);
        });

        // The private subscription link for Apple/Google/Outlook calendars. Those apps can't sign
        // in, so the link itself is the credential - hence a reset that makes the old one stop
        // working.
        enabled.MapGet("/calendar-feed", async (HttpContext context, IOrganizerStore store, CancellationToken ct) =>
            Results.Ok(new { path = FeedPath(await store.GetOrCreateFeedTokenAsync(UserOf(context), ct)) }));

        enabled.MapPost("/calendar-feed/reset", async (HttpContext context, IOrganizerStore store, CancellationToken ct) =>
            Results.Ok(new { path = FeedPath(await store.ResetFeedTokenAsync(UserOf(context), ct)) }));

        enabled.MapGet("/feed/{token}.ics", async (string token, IOrganizerStore store, CancellationToken ct) =>
        {
            if (await store.ResolveFeedTokenAsync(token, ct) is not { } owner) return Results.NotFound();
            var spaces = await store.GetVisibleSpacesAsync(owner, ct);
            var events = await store.GetItemsAsync(owner, OrganizerKinds.Event, null, ct);
            // The holiday calendars this person turned on come along too, a year back and two ahead.
            var year = DateTime.UtcNow.Year;
            var holidays = ReadPreferences(await store.GetPreferencesAsync(owner, ct))?.HolidaySets is { Count: > 0 } chosen
                ? OrganizerHolidays.Between(chosen, year - 1, year + 2)
                : [];
            return Results.Text(OrganizerCalendarFeed.Build(events, spaces, holidays), "text/calendar; charset=utf-8");
        }).AllowAnonymous();
    }

    private static string FeedPath(string token) => $"/api/organizer/feed/{token}.ics";

    private static readonly System.Text.Json.JsonSerializerOptions PreferencesJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static OrganizerPreferences? ReadPreferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<OrganizerPreferences>(json, PreferencesJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static object HolidayToWire(Holiday holiday) => new
    {
        id = holiday.Id,
        setId = holiday.SetId,
        name = holiday.Name,
        date = holiday.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        days = holiday.Days,
        note = holiday.Note,
    };

    public static OrganizerUser UserOf(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } && context.User.Identity?.Name is { Length: > 0 } name
            ? new OrganizerUser(name.ToLowerInvariant(), null)
            : OrganizerUser.Shared;

    public static async Task<OrganizerSettingsDto> ReadOptionsAsync(AppSettingsFile settings, CancellationToken cancellationToken) =>
        await settings.GetSectionAsync<OrganizerSettingsDto>(OrganizerSettingsDto.SectionName, cancellationToken) ?? new OrganizerSettingsDto();

    private static async Task<List<string>> ValidateGroupAsync(
        OrganizerUser user, OrganizerGroupRequest request, IAccountStore accounts, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (user.Username is null)
        {
            errors.Add("Groups need login turned on, so the server knows who each person is.");
            return errors;
        }
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Give the group a name.");
        else if (request.Name.Trim().Length > 60) errors.Add("Group names can be at most 60 characters.");

        var known = (await accounts.GetAllAsync(cancellationToken))
            .Select(account => account.Username.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var member in request.Members.Where(member => !known.Contains(member.Trim().ToLowerInvariant())))
            errors.Add($"There's no account called \"{member}\".");
        return errors;
    }

    private static string? StringField(JsonObject fields, string name) =>
        fields[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // The shape every client reads. Deleted items go out as bare tombstones - the clocks are what
    // a device needs to keep the delete from being undone by a stale copy, the content isn't.
    public static object ToWire(OrganizerItem item) => new
    {
        id = item.Id,
        kind = item.Kind,
        spaceId = item.SpaceId,
        parentId = item.ParentId,
        deleted = item.Deleted,
        fields = item.Deleted ? new JsonObject() : item.Fields,
        clocks = item.Clocks,
        seq = item.Seq,
        createdAt = item.CreatedAt,
        createdBy = item.CreatedBy,
        updatedAt = item.UpdatedAt,
        updatedBy = item.UpdatedBy,
    };
}
