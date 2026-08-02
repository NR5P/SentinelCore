using System.Security.Claims;

namespace SentinelCore.Web;

public sealed class LiveOrderInputDto
{
    public List<Guid> CameraIds { get; set; } = [];
}

// The Live grid's tile order, per signed-in person - see ILiveOrderStore for why it isn't global.
//
// Not Admin-gated: watching live video is the one thing a Viewer account exists for, so arranging
// one's own view of it has to be available to a Viewer too. Nothing here can affect what anyone
// else sees, so there's no privilege to withhold.
//
// GET always answers with a complete order covering every camera that currently exists, so a
// client can render straight from it without merging anything itself: saved placements first (any
// that point at deleted cameras dropped), then cameras the saved order has never heard of - newly
// added ones - appended in the camera store's own order. That "new cameras go last" rule is
// deliberate: appearing at the end is predictable, whereas inserting by creation date would shove
// somebody's carefully placed tiles sideways the moment an unrelated camera is added.
public static class LiveOrderEndpoints
{
    // Login is off by default in this app (see AuthGateMiddleware), and in that mode every request
    // is the same synthetic Admin with no account id behind it - so everyone shares one order.
    // That's the honest answer for a single-shared-login install: there is only one "user".
    private const string SharedOwnerKey = "shared";

    public static void MapLiveOrderEndpoints(this WebApplication app)
    {
        var liveOrder = app.MapGroup("/api/live-order").RequireAuthorization();

        liveOrder.MapGet("", async (HttpContext context, ILiveOrderStore store, ICameraStore cameras, CancellationToken ct) =>
        {
            var saved = await LoadSavedOrderAsync(context, store, ct);
            var all = await cameras.GetAllAsync(ct);
            return Results.Ok(new { cameraIds = Resolve(saved, all) });
        });

        liveOrder.MapPut("", async (LiveOrderInputDto dto, HttpContext context, ILiveOrderStore store, ICameraStore cameras, CancellationToken ct) =>
        {
            var all = await cameras.GetAllAsync(ct);
            var known = all.Select(camera => camera.Id).ToHashSet();

            var errors = new List<string>();
            if (dto.CameraIds.Distinct().Count() != dto.CameraIds.Count)
                errors.Add("The same camera can't appear twice in the live order.");
            foreach (var cameraId in dto.CameraIds.Where(id => !known.Contains(id)))
                errors.Add($"Camera {cameraId} does not exist.");
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            // Stored resolved rather than exactly as sent: a client that was showing a stale camera
            // list (another tab added a camera while this one was mid-drag) sends a partial order,
            // and saving the resolved form means the cameras it left out get a stable place at the
            // end now instead of drifting on every later read.
            var resolved = Resolve(dto.CameraIds, all);
            await store.SaveAsync(OwnerKey(context), resolved, ct);
            return Results.Ok(new { saved = true, cameraIds = resolved });
        });
    }

    private static async Task<IReadOnlyList<Guid>> LoadSavedOrderAsync(
        HttpContext context, ILiveOrderStore store, CancellationToken ct)
    {
        var ownerKey = OwnerKey(context);
        var saved = await store.GetAsync(ownerKey, ct);

        // An account that has never dragged a tile inherits the shared order as its starting
        // point, so turning login on doesn't throw away the arrangement the household was already
        // using. The moment that account saves its own order it stops consulting this.
        if (saved.Count == 0 && ownerKey != SharedOwnerKey)
            saved = await store.GetAsync(SharedOwnerKey, ct);

        return saved;
    }

    private static string OwnerKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } accountId
            ? $"account:{accountId}"
            : SharedOwnerKey;

    private static List<Guid> Resolve(IReadOnlyList<Guid> saved, IReadOnlyList<Camera> cameras)
    {
        var known = cameras.Select(camera => camera.Id).ToHashSet();
        var placed = saved.Where(known.Contains).Distinct().ToList();

        var placedSet = placed.ToHashSet();
        placed.AddRange(cameras.Select(camera => camera.Id).Where(id => !placedSet.Contains(id)));
        return placed;
    }
}
