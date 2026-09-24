using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public static class PushMessages
{
    public static PushMessage FromAlert(AlertResult result)
    {
        var analysis = result.Analysis;
        var title = string.IsNullOrWhiteSpace(result.Camera)
            ? "Sentinel Core alert"
            : result.Camera;
        var body = string.IsNullOrWhiteSpace(analysis?.Summary)
            ? "New security camera activity"
            : analysis.Summary;
        return new PushMessage(
            title,
            body,
            result.Id.ToString(),
            result.Camera,
            analysis?.EventType);
    }
}

public enum PushProvider
{
    None,
    Direct,
    Relay
}

// Which way a push to a given platform goes. A direct provider (the operator's own Apple or
// Firebase credentials) that is enabled and complete always wins; the relay is what an install
// with no credentials of its own uses. One place, so the dispatcher, the device-registration
// endpoint (which forwards registrations to the relay) and /api/push/status all agree.
public static class PushProviderSelection
{
    public static PushProvider For(PushNotificationOptions options, string platform)
    {
        if (!options.Enabled) return PushProvider.None;
        var direct = PushPlatforms.Normalize(platform) switch
        {
            PushPlatforms.Apple => options.Apple.Enabled && options.Apple.HasCredentials,
            PushPlatforms.Android => options.Firebase.Enabled && options.Firebase.HasCredentials,
            _ => false
        };
        if (direct) return PushProvider.Direct;
        return PushPlatforms.IsSupported(platform) && options.Relay.IsConfigured
            ? PushProvider.Relay
            : PushProvider.None;
    }

    public static bool UsesRelay(PushNotificationOptions options) =>
        For(options, PushPlatforms.Apple) == PushProvider.Relay ||
        For(options, PushPlatforms.Android) == PushProvider.Relay;

    public static bool AnyConfigured(PushNotificationOptions options) =>
        For(options, PushPlatforms.Apple) != PushProvider.None ||
        For(options, PushPlatforms.Android) != PushProvider.None;
}

public sealed record PushDeliveryReport(int Attempted, int Delivered, int Failed, int Removed);

public sealed class PushNotificationDispatcher(
    IPushDeviceStore devices,
    ApnsSender apns,
    FcmSender fcm,
    PushRelayClient relay,
    ILogger<PushNotificationDispatcher> logger)
{
    public async Task<PushDeliveryReport> SendAsync(
        PushNotificationOptions options,
        PushMessage message,
        CancellationToken cancellationToken,
        Func<PushDevice, bool>? recipients = null)
    {
        if (!options.Enabled) return new PushDeliveryReport(0, 0, 0, 0);

        // Everyone unless the caller picks - a board automation notifying only some people.
        var registered = (await devices.GetAllAsync(cancellationToken)).Where(recipients ?? (_ => true)).ToList();
        var attempted = 0;
        var delivered = 0;
        var failed = 0;
        var removed = 0;

        foreach (var device in registered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Silent pushes only go direct - an older relay would show them as a blank alert - and
            // never to the Apple TV, which has nothing to do in the background.
            var provider = PushProviderSelection.For(options, device.Platform);
            if (message.Silent && (provider == PushProvider.Relay ||
                                   string.Equals(device.AppVariant, "tvos", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            PushAttempt attempt;
            try
            {
                attempt = provider switch
                {
                    PushProvider.Direct when device.Platform == PushPlatforms.Apple =>
                        await apns.SendAsync(
                            options.Apple,
                            options.Apple.UseSandbox,
                            device.Token,
                            options.Apple.ResolveApnsTopic(device.AppVariant),
                            message,
                            cancellationToken),
                    PushProvider.Direct when device.Platform == PushPlatforms.Android =>
                        await fcm.SendAsync(options.Firebase, device.Token, message, cancellationToken),
                    PushProvider.Relay =>
                        await relay.SendAsync(
                            options.Relay, device.Platform, device.Token, device.AppVariant, message, cancellationToken),
                    _ => PushAttempt.Skipped
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Push delivery failed for {Platform} device {DeviceId}",
                    device.Platform,
                    device.Id);
                attempt = PushAttempt.Failed;
            }

            if (attempt == PushAttempt.Skipped) continue;
            attempted++;
            if (attempt == PushAttempt.Delivered)
            {
                delivered++;
                continue;
            }

            failed++;
            if (attempt == PushAttempt.Unregistered)
            {
                await devices.RemoveAsync(device.Platform, device.Token, cancellationToken);
                removed++;

                // Logged here, at the point the row is actually deleted, rather than left to the
                // callers - neither of them reads PushDeliveryReport.Removed, so this permanent
                // change to the device store had no record anywhere. It matters most in the case
                // that reads as harmless: with two devices registered and one token gone stale,
                // PushAlertAction reports "delivered to 1/2 registered devices", which is
                // indistinguishable from a transient partial failure even though that device has
                // been dropped for good and will never be attempted again. Re-registration only
                // happens when the app next launches and calls POST /api/push/devices, so
                // without this line a phone or Apple TV can stop receiving alerts indefinitely
                // and the journal's only hint is a rejection warning that says nothing about the
                // registration being removed.
                logger.LogWarning(
                    "Removed the push registration for {Platform} device {DeviceName} ({DeviceId}) because the provider " +
                    "reported its token is no longer valid - that device receives no further pushes until its app " +
                    "re-registers on next launch",
                    device.Platform,
                    string.IsNullOrWhiteSpace(device.DeviceName) ? "unnamed" : device.DeviceName,
                    device.Id);
            }
        }

        return new PushDeliveryReport(attempted, delivered, failed, removed);
    }
}

public sealed class PushAlertAction(
    PushNotificationDispatcher dispatcher,
    IOptionsMonitor<PushNotificationOptions> optionsMonitor,
    IOptionsMonitor<QuietHoursOptions> quietHoursMonitor,
    ILogger<PushAlertAction> logger) : IAlertAction
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled || result.Analysis is null) return;
        if (options.OnlyWhenNotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result)) return;

        var minimumConfidence = QuietHoursPolicy.EffectiveMinimumConfidence(
            quietHoursMonitor.CurrentValue,
            options.MinimumConfidence,
            result.OccurredAt);
        if (result.Analysis.Confidence < minimumConfidence) return;

        var category = AlertCategorizer.Categorize(result.Analysis) ?? result.Analysis.EventType;
        var cooldownKey = $"{result.Camera}|{category}";
        if (options.DuplicateCooldownMinutes > 0 &&
            _lastSent.TryGetValue(cooldownKey, out var lastSent) &&
            DateTimeOffset.UtcNow - lastSent < TimeSpan.FromMinutes(options.DuplicateCooldownMinutes))
        {
            logger.LogInformation(
                "Duplicate push category {Category} suppressed for camera {Camera}",
                category,
                result.Camera);
            return;
        }

        var report = await dispatcher.SendAsync(options, PushMessages.FromAlert(result), cancellationToken);
        if (report.Delivered > 0)
        {
            _lastSent[cooldownKey] = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Push alert {AlertId} delivered to {Delivered}/{Attempted} registered devices",
                result.Id,
                report.Delivered,
                report.Attempted);
        }
        else if (report.Attempted > 0)
        {
            logger.LogWarning(
                "Push alert {AlertId} failed for all {Attempted} attempted devices",
                result.Id,
                report.Attempted);
        }
    }
}
