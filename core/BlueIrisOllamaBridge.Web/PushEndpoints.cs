using BlueIrisOllamaBridge;

namespace BlueIrisOllamaBridge.Web;

public sealed record RegisterPushDeviceRequest(string Platform, string Token, string? DeviceName);
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
            var options = await settings.GetSectionAsync<PushNotificationOptions>(
                PushNotificationOptions.SectionName,
                ct) ?? new PushNotificationOptions();
            return Results.Ok(new
            {
                enabled = options.Enabled,
                appleConfigured = IsAppleConfigured(options),
                firebaseConfigured = IsFirebaseConfigured(options),
                registeredDevices = await devices.CountAsync(ct)
            });
        });

        app.MapPost("/api/push/devices", async (
            RegisterPushDeviceRequest request,
            IPushDeviceStore devices,
            CancellationToken ct) =>
        {
            var errors = Validate(request.Platform, request.Token, request.DeviceName);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var device = await devices.UpsertAsync(
                request.Platform,
                request.Token,
                request.DeviceName?.Trim(),
                ct);
            return Results.Ok(new
            {
                device.Id,
                device.Platform,
                device.DeviceName,
                device.UpdatedAt
            });
        });

        app.MapPost("/api/push/devices/unregister", async (
            UnregisterPushDeviceRequest request,
            IPushDeviceStore devices,
            CancellationToken ct) =>
        {
            var errors = Validate(request.Platform, request.Token, null);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await devices.RemoveAsync(request.Platform, request.Token, ct);
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/push/test", async (
            AppSettingsFile settings,
            PushNotificationDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var options = await settings.GetSectionAsync<PushNotificationOptions>(
                PushNotificationOptions.SectionName,
                ct) ?? new PushNotificationOptions();
            if (!options.Enabled)
                return Results.BadRequest(new { error = "Push notifications are not enabled in Core." });
            if (!IsAppleConfigured(options) && !IsFirebaseConfigured(options))
                return Results.BadRequest(new { error = "No push provider is configured in Core." });

            var report = await dispatcher.SendAsync(options, PushMessage.Test(), ct);
            return Results.Ok(report);
        });
    }

    private static List<string> Validate(string platform, string token, string? deviceName)
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
        return errors;
    }

    private static bool IsAppleConfigured(PushNotificationOptions options) =>
        options.Enabled &&
        options.Apple.Enabled &&
        !string.IsNullOrWhiteSpace(options.Apple.TeamId) &&
        !string.IsNullOrWhiteSpace(options.Apple.KeyId) &&
        !string.IsNullOrWhiteSpace(options.Apple.BundleId) &&
        !string.IsNullOrWhiteSpace(options.Apple.PrivateKeyPath);

    private static bool IsFirebaseConfigured(PushNotificationOptions options) =>
        options.Enabled &&
        options.Firebase.Enabled &&
        !string.IsNullOrWhiteSpace(options.Firebase.ProjectId) &&
        !string.IsNullOrWhiteSpace(options.Firebase.ServiceAccountPath);
}
