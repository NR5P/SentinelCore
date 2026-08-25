using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

public sealed class CameraGroupInputDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public List<Guid> CameraIds { get; set; } = [];

    // Default is a rough walking-time estimate between typically-spaced cameras (gate to
    // driveway to front door) - generous enough to catch a real incident, short enough that an
    // unrelated event an hour later doesn't get folded in.
    public int CorrelationWindowSeconds { get; set; } = 45;

    // On by default once a group exists - the whole point of grouping cameras is usually to get
    // footage from all of them. AnalyzeCompanionCaptures (AI analysis of companion cameras, not
    // just recording) stays off by default - see MultiFrameOptions for the same cost reasoning.
    public bool RecordCompanionClips { get; set; } = true;
    public bool AnalyzeCompanionCaptures { get; set; }
}

public static class CameraGroupEndpoints
{
    public static void MapCameraGroupEndpoints(this WebApplication app)
    {
        var groups = app.MapGroup("/api/camera-groups").RequireAuthorization("AdminOnly");

        groups.MapGet("", async (ICameraGroupStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        groups.MapPost("", async (CameraGroupInputDto dto, ICameraGroupStore groupStore, ICameraStore cameraStore, CancellationToken ct) =>
        {
            var errors = await ValidateAsync(dto, null, groupStore, cameraStore, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var group = await groupStore.AddAsync(
                dto.Name.Trim(), dto.CameraIds, dto.CorrelationWindowSeconds,
                dto.RecordCompanionClips, dto.AnalyzeCompanionCaptures, ct);
            return Results.Ok(group);
        });

        groups.MapPut("/{id:guid}", async (Guid id, CameraGroupInputDto dto, ICameraGroupStore groupStore, ICameraStore cameraStore, CancellationToken ct) =>
        {
            var errors = await ValidateAsync(dto, id, groupStore, cameraStore, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var updated = await groupStore.UpdateAsync(
                id, dto.Name.Trim(), dto.CameraIds, dto.CorrelationWindowSeconds,
                dto.RecordCompanionClips, dto.AnalyzeCompanionCaptures, ct);
            return updated ? Results.Ok(new { saved = true }) : Results.NotFound();
        });

        groups.MapDelete("/{id:guid}", async (Guid id, ICameraGroupStore store, CancellationToken ct) =>
        {
            var deleted = await store.DeleteAsync(id, ct);
            return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
        });
    }

    private static async Task<List<string>> ValidateAsync(
        CameraGroupInputDto dto, Guid? editingId, ICameraGroupStore groupStore, ICameraStore cameraStore, CancellationToken ct)
    {
        var cameras = await cameraStore.GetAllAsync(ct);
        var groups = await groupStore.GetAllAsync(ct);
        return Validate(dto, editingId, [.. cameras.Select(c => c.Id)], groups);
    }

    // The rules themselves, over plain data. Split out from the store lookups above because config
    // restore has to apply exactly these rules to a group arriving in a backup file - and there
    // the cameras and the other groups come from the file rather than from the database, since a
    // restore onto a fresh machine writes all three in one go. Passing what to judge against,
    // instead of where to read it from, is what lets both callers share one copy of the rules.
    internal static List<string> Validate(
        CameraGroupInputDto dto,
        Guid? editingId,
        IReadOnlyCollection<Guid> knownCameraIds,
        IReadOnlyCollection<CameraGroup> existingGroups)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("Name is required.");
        if (dto.CameraIds.Count < 2) errors.Add("A group needs at least two cameras - that's the whole point of linking them.");
        if (dto.CameraIds.Distinct().Count() != dto.CameraIds.Count) errors.Add("The same camera can't be listed twice in one group.");
        if (dto.CorrelationWindowSeconds is < 5 or > 600) errors.Add("Correlation window must be between 5 and 600 seconds.");

        foreach (var cameraId in dto.CameraIds)
        {
            if (!knownCameraIds.Contains(cameraId))
                errors.Add($"Camera {cameraId} does not exist.");
        }

        // A camera can only belong to one group at a time (see CameraGroup's doc comment) - keeps
        // "which group fired" unambiguous when a trigger zone fires. Checked against every other
        // group's membership, excluding the group being edited.
        foreach (var existing in existingGroups)
        {
            if (existing.Id == editingId) continue;
            var overlap = existing.CameraIds.Intersect(dto.CameraIds).Any();
            if (overlap) errors.Add($"One or more of these cameras is already in the group \"{existing.Name}\".");
        }

        return errors;
    }
}
