using Microsoft.Extensions.Options;

namespace SentinelCore.PushRelay;

// The wire shapes PushRelayClient (in the main app) sends. Kept in step by hand - the relay
// deliberately doesn't reference that project.
public sealed record RegisterDeviceRequest(string Platform, string Token, string? AppVariant, bool ApnsSandbox);
public sealed record UnregisterDeviceRequest(string Platform, string Token);
public sealed record PushRequest(string Platform, string Token, string? AppVariant, PushMessage Message);

public static class RelayEndpoints
{
    public const string PushRateLimitPolicy = "per-license";

    public static void MapRelayEndpoints(this WebApplication app)
    {
        // Unauthenticated, and says nothing a caller couldn't learn by trying: which platforms
        // this relay is able to serve.
        app.MapGet("/health", (IOptions<RelayOptions> options) => Results.Ok(new
        {
            status = "ok",
            apple = ServesApple(options.Value),
            firebase = ServesFirebase(options.Value)
        }));

        app.MapPost("/v1/devices", async (
            RegisterDeviceRequest request,
            HttpContext context,
            RelayDeviceStore store,
            IOptions<RelayOptions> options,
            ILogger<RelayDeviceStore> logger,
            CancellationToken ct) =>
        {
            var errors = Validate(request.Platform, request.Token, request.AppVariant);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            if (!ServesPlatform(options.Value, request.Platform))
                return Results.Json(new { error = $"This relay does not deliver to {request.Platform} devices." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var license = context.License();
            var outcome = await store.RegisterAsync(
                license.LicenseId,
                license.CustomerName,
                request.Platform,
                request.Token,
                NormalizeVariant(request.AppVariant),
                request.ApnsSandbox,
                ct);

            switch (outcome)
            {
                case RegisterOutcome.OwnedByAnotherLicense:
                    logger.LogWarning(
                        "{Customer} ({LicenseId}) tried to register a {Platform} token that another license holds",
                        license.CustomerName, license.LicenseId, request.Platform);
                    return Results.Conflict(new { error = "That device is registered to a different license." });
                case RegisterOutcome.LimitReached:
                    logger.LogWarning(
                        "{Customer} ({LicenseId}) is at the device limit of {Limit}",
                        license.CustomerName, license.LicenseId, options.Value.MaxDevicesPerLicense);
                    return Results.Json(
                        new { error = $"This license already has {options.Value.MaxDevicesPerLicense} devices registered." },
                        statusCode: StatusCodes.Status429TooManyRequests);
                default:
                    logger.LogInformation(
                        "{Customer} ({LicenseId}) registered a {Platform} device ({Variant}{Sandbox})",
                        license.CustomerName, license.LicenseId, PushPlatforms.Normalize(request.Platform),
                        NormalizeVariant(request.AppVariant), request.ApnsSandbox ? ", sandbox" : "");
                    return Results.Ok(new { registered = true });
            }
        });

        app.MapPost("/v1/devices/unregister", async (
            UnregisterDeviceRequest request,
            HttpContext context,
            RelayDeviceStore store,
            CancellationToken ct) =>
        {
            var errors = Validate(request.Platform, request.Token, null);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            // Only the owner may remove a token; anyone else gets the same answer as for a token
            // that was never there.
            var existing = await store.FindAsync(request.Platform, request.Token, ct);
            if (existing is not null && existing.LicenseId == context.License().LicenseId)
                await store.RemoveAsync(request.Platform, request.Token, ct);
            return Results.Ok(new { success = true });
        });

        app.MapPost("/v1/push", async (
            PushRequest request,
            HttpContext context,
            RelayDeviceStore store,
            ApnsSender apns,
            FcmSender fcm,
            IOptions<RelayOptions> options,
            ILogger<PushRequest> logger,
            CancellationToken ct) =>
        {
            var errors = Validate(request.Platform, request.Token, request.AppVariant);
            if (request.Message is null) errors.Add("A message is required.");
            else if (request.Message.Title.Length > 200 || request.Message.Body.Length > 1000) errors.Add("The message is too long.");
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var license = context.License();
            var device = await store.FindAsync(request.Platform, request.Token, ct);
            // One answer for "never registered" and "registered by someone else": the caller
            // learns nothing about other licenses' tokens, and PushRelayClient treats 404 as
            // "register it and try again", which is right in the first case and harmlessly
            // becomes a 409 in the second.
            if (device is null || device.LicenseId != license.LicenseId)
                return Results.NotFound(new { status = "unknown" });
            if (!ServesPlatform(options.Value, device.Platform))
                return Results.Json(new { status = "failed", error = $"This relay does not deliver to {device.Platform} devices." }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var attempt = device.Platform switch
            {
                PushPlatforms.Apple => await apns.SendAsync(
                    options.Value.Apple,
                    device.ApnsSandbox,
                    device.Token,
                    options.Value.Apple.ResolveApnsTopic(device.AppVariant),
                    request.Message!,
                    ct),
                PushPlatforms.Android => await fcm.SendAsync(options.Value.Firebase, device.Token, request.Message!, ct),
                _ => PushAttempt.Failed
            };

            if (attempt == PushAttempt.Unregistered)
                await store.RemoveAsync(device.Platform, device.Token, ct);

            // Who, which platform, what happened - and not what the message said. The text passes
            // through this process but is nobody's business to keep.
            logger.LogInformation(
                "{Customer} ({LicenseId}) push to {Platform}: {Outcome}",
                license.CustomerName, license.LicenseId, device.Platform, attempt);

            return Results.Ok(new { status = attempt.ToString().ToLowerInvariant() });
        }).RequireRateLimiting(PushRateLimitPolicy);
    }

    private static bool ServesApple(RelayOptions options) => options.Apple.Enabled && options.Apple.HasCredentials;
    private static bool ServesFirebase(RelayOptions options) => options.Firebase.Enabled && options.Firebase.HasCredentials;

    private static bool ServesPlatform(RelayOptions options, string platform) => PushPlatforms.Normalize(platform) switch
    {
        PushPlatforms.Apple => ServesApple(options),
        PushPlatforms.Android => ServesFirebase(options),
        _ => false
    };

    private static string NormalizeVariant(string? appVariant) =>
        string.IsNullOrWhiteSpace(appVariant) ? "ios" : appVariant.Trim().ToLowerInvariant();

    private static List<string> Validate(string? platform, string? token, string? appVariant)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(platform) || !PushPlatforms.IsSupported(platform))
            errors.Add("Platform must be 'apple' or 'android'.");
        if (string.IsNullOrWhiteSpace(token))
            errors.Add("A push token is required.");
        else if (token.Length > 4096)
            errors.Add("The push token is too long.");
        if (!string.IsNullOrWhiteSpace(appVariant) && NormalizeVariant(appVariant) is not ("ios" or "tvos"))
            errors.Add("AppVariant must be 'ios' or 'tvos'.");
        return errors;
    }
}
