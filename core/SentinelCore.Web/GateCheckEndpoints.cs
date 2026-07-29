using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

public sealed class GateCheckInputDto
{
    public bool Enabled { get; set; }

    [Required]
    public string CheckLabel { get; set; } = "the gate";

    public bool CheckBlocked { get; set; } = true;
    public int IntervalMinutes { get; set; } = 10;
    public int? RemindIfStillOpenAfterHours { get; set; }
}

public static class GateCheckEndpoints
{
    public static void MapGateCheckEndpoints(this WebApplication app)
    {
        app.MapGet("/api/gate-checks", async (IGateCheckStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct))).RequireAuthorization();

        app.MapPut("/api/gate-checks/{cameraId:guid}", async (Guid cameraId, GateCheckInputDto dto, ICameraStore cameras, IGateCheckStore store, CancellationToken ct) =>
        {
            if (await cameras.GetAsync(cameraId, ct) is null) return Results.NotFound();

            var errors = Validate(dto);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            // Preserve whatever transition memory already exists - saving just a label or
            // interval change shouldn't reset the open/closed state GateCheckMonitor is tracking.
            var existing = await store.GetAsync(cameraId, ct);
            var config = new GateCheckConfig(
                cameraId,
                dto.Enabled,
                dto.CheckLabel.Trim(),
                dto.CheckBlocked,
                dto.IntervalMinutes,
                dto.RemindIfStillOpenAfterHours,
                existing?.LastKnownState ?? "unknown",
                existing?.LastStateChangedAt,
                existing?.LastNotifiedAt,
                existing?.LastKnownBlocked ?? false);
            await store.SaveAsync(config, ct);
            return Results.Ok(config);
        }).RequireAuthorization("AdminOnly");

        app.MapDelete("/api/gate-checks/{cameraId:guid}", async (Guid cameraId, IGateCheckStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(cameraId, ct);
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("AdminOnly");
    }

    private static List<string> Validate(GateCheckInputDto dto)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(dto.CheckLabel)) errors.Add("A label is required (e.g. \"the front gate\").");
        if (dto.IntervalMinutes < 1) errors.Add("Interval must be at least 1 minute.");
        if (dto.RemindIfStillOpenAfterHours is < 1) errors.Add("Reminder interval must be at least 1 hour.");
        return errors;
    }
}
