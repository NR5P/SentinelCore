using System.ComponentModel.DataAnnotations;
using System.Net;

namespace SentinelCore.Web;

public sealed class CameraProbeRequestDto
{
    [Required]
    public string Address { get; set; } = string.Empty;

    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class CameraScanRequestDto
{
    // Which local interface addresses to send the WS-Discovery multicast Probe out of - the
    // frontend shows GetCandidateInterfaces as checkboxes and the admin confirms which ones
    // to use, rather than the server silently guessing. Deliberately not defaulted to "all
    // interfaces" for the same reason.
    public List<string> InterfaceAddresses { get; set; } = [];

    // Opt-in TCP sweep of the selected subnets for cameras that never answer the multicast Probe -
    // ONVIF disabled in firmware, or multicast not forwarded by an AP. Defaults to false so the
    // ordinary scan stays a single broadcast that touches no individual host.
    public bool DeepScan { get; set; }
}

public static class CameraDiscoveryEndpoints
{
    public static void MapCameraDiscoveryEndpoints(this WebApplication app)
    {
        // Same gate as every other mutating/probing camera endpoint - a network probe is a
        // meaningfully more privileged action than viewing a camera, even though it doesn't
        // change any stored data by itself.
        app.MapPost("/api/cameras/discovery/probe", async (
            CameraProbeRequestDto dto, CameraDiscoveryService discovery, CancellationToken ct) =>
        {
            if (!IPAddress.TryParse(dto.Address.Trim(), out var address))
                return Results.BadRequest(new { errors = new[] { "Enter a valid IP address, e.g. 192.168.1.50." } });

            var result = await discovery.ProbeCameraAsync(address, dto.Username, dto.Password, ct);
            return Results.Ok(result);
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/cameras/discovery/interfaces", () =>
            Results.Ok(CameraDiscoveryService.GetCandidateInterfaces())
        ).RequireAuthorization("AdminOnly");

        app.MapPost("/api/cameras/discovery/scan", (CameraScanRequestDto dto, CameraDiscoveryService discovery) =>
        {
            var addresses = new List<IPAddress>();
            foreach (var text in dto.InterfaceAddresses)
            {
                if (IPAddress.TryParse(text, out var address)) addresses.Add(address);
            }

            if (addresses.Count == 0)
                return Results.BadRequest(new { errors = new[] { "Select at least one network interface to scan." } });

            var scanId = discovery.StartScan(addresses, dto.DeepScan);
            return Results.Ok(new { scanId });
        }).RequireAuthorization("AdminOnly");

        app.MapGet("/api/cameras/discovery/scan/{scanId:guid}", (Guid scanId, CameraDiscoveryService discovery) =>
        {
            var snapshot = discovery.GetScanStatus(scanId);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).RequireAuthorization("AdminOnly");
    }
}
