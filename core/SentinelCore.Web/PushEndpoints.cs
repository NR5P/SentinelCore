using SentinelCore;

namespace SentinelCore.Web;

// AppVariant distinguishes which Apple app registered the token ("ios" or "tvos") so the
// dispatcher can send with the matching apns-topic; ignored for Android. Defaults to "ios" so
// existing (pre-tvOS) clients don't need to change anything.
public sealed record RegisterPushDeviceRequest(string Platform, string Token, string? DeviceName, string? AppVariant = null);
public sealed record UnregisterPushDeviceRequest(string Platform, string Token);

public static class PushEndpoints
{
    public static void MapPushEndpoints(this WebApplication app)
    {
        app.MapGet("/api/push/status", async (
            AppSettingsFile settings,
            IPushDeviceStore devices,
            CancellationToken ct) =>
        {
            var options = await ReadOptionsAsync(settings, ct);
            return Results.Ok(new
            {
                enabled = options.Enabled,
                appleConfigured = options.Enabled && options.Apple.Enabled && options.Apple.HasCredentials,
                firebaseConfigured = options.Enabled && options.Firebase.Enabled && options.Firebase.HasCredentials,
                relayConfigured = PushProviderSelection.UsesRelay(options),
                registeredDevices = await devices.CountAsync(ct)
            });
        }).RequireAuthorization();

        app.MapPost("/api/push/devices", async (
            RegisterPushDeviceRequest request,
            IPushDeviceStore devices,
            AppSettingsFile settings,
            PushRelayClient relay,
            ILogger<PushRelayClient> logger,
            CancellationToken ct) =>
        {
            var errors = Validate(request.Platform, request.Token, request.DeviceName, request.AppVariant);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var device = await devices.UpsertAsync(
                request.Platform,
                request.Token,
                request.DeviceName?.Trim(),
                ct,
                appVariant: string.IsNullOrWhiteSpace(request.AppVariant) ? "ios" : request.AppVariant);

            // The local row is the registration; the relay copy is a convenience so the first
            // push after enrolling doesn't have to take PushRelayClient's register-and-retry
            // path. Off the request's own token and never awaited: a phone enrolling must not
            // wait on, or fail because of, a relay that is slow or down.
            var options = await ReadOptionsAsync(settings, ct);
            if (PushProviderSelection.For(options, device.Platform) == PushProvider.Relay)
            {
                _ = ForwardAsync(
                    () => relay.RegisterAsync(options.Relay, device.Platform, device.Token, device.AppVariant, CancellationToken.None),
                    logger, "register");
            }

            return Results.Ok(new
            {
                device.Id,
                device.Platform,
                device.DeviceName,
                device.AppVariant,
                device.UpdatedAt
            });
        }).RequireAuthorization();

        app.MapPost("/api/push/devices/unregister", async (
            UnregisterPushDeviceRequest request,
            IPushDeviceStore devices,
            AppSettingsFile settings,
            PushRelayClient relay,
            ILogger<PushRelayClient> logger,
            CancellationToken ct) =>
        {
            var errors = Validate(request.Platform, request.Token, null);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await devices.RemoveAsync(request.Platform, request.Token, ct);

            var options = await ReadOptionsAsync(settings, ct);
            if (PushProviderSelection.For(options, request.Platform) == PushProvider.Relay)
            {
                var platform = PushPlatforms.Normalize(request.Platform);
                var token = request.Token.Trim();
                _ = ForwardAsync(
                    () => relay.UnregisterAsync(options.Relay, platform, token, CancellationToken.None),
                    logger, "unregister");
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        app.MapPost("/api/push/test", async (
            AppSettingsFile settings,
            PushNotificationDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var options = await ReadOptionsAsync(settings, ct);
            if (!options.Enabled)
                return Results.BadRequest(new { error = "Push notifications are not enabled in Core." });
            if (!PushProviderSelection.AnyConfigured(options))
                return Results.BadRequest(new { error = "No push provider is configured in Core." });

            var report = await dispatcher.SendAsync(options, PushMessage.Test(), ct);
            return Results.Ok(report);
        }).RequireAuthorization("AdminOnly");
    }

    private static async Task<PushNotificationOptions> ReadOptionsAsync(AppSettingsFile settings, CancellationToken ct) =>
        await settings.GetSectionAsync<PushNotificationOptions>(PushNotificationOptions.SectionName, ct)
        ?? new PushNotificationOptions();

    private static async Task ForwardAsync(Func<Task<bool>> call, ILogger logger, string what)
    {
        try
        {
            await call();
        }
        catch (Exception ex)
        {
            // PushRelayClient already logs the specific refusal or connection failure; this
            // catches only what it didn't expect, so a background forward can never surface as
            // an unobserved task exception.
            logger.LogWarning(ex, "Forwarding a push device {What} to the relay failed", what);
        }
    }

    private static List<string> Validate(string platform, string token, string? deviceName, string? appVariant = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(platform) || !PushPlatforms.IsSupported(platform))
            errors.Add("Platform must be 'apple' or 'android'.");
        if (string.IsNullOrWhiteSpace(token))
            errors.Add("A push token is required.");
        else if (token.Length > 4096)
            errors.Add("The push token is too long.");
        if (deviceName?.Length > 120)
            errors.Add("The device name is too long.");
        if (!string.IsNullOrWhiteSpace(appVariant) &&
            appVariant is not ("ios" or "tvos"))
            errors.Add("AppVariant must be 'ios' or 'tvos'.");
        return errors;
    }
}
