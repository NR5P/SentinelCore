namespace SentinelCore;

// Registers the HTTP-backed alert actions and their per-channel timeouts.
//
// This lives in its own file, rather than inline in Program.cs where it used to, for one reason:
// it is the only part of the startup wiring with a failure mode that is invisible at the call
// site and untestable from outside. Booting the ingest app in a test is not an option - it binds
// 5080 and the MQTT broker - so extracting the registration is what makes the rule below
// something a test can actually hold.
//
// The rule: every one of these MUST pass an explicit client name.
//
// AddHttpClient<TClient, TImplementation>() with no name derives the name from TClient. All four
// of these share TClient = IAlertAction, so unnamed they collapse into a single named client and
// the four configure delegates apply to it in registration order - last writer wins. That is not
// hypothetical: it shipped. The journal carried exactly one "HttpClient.IAlertAction" log
// category for all four channels, and on 2026-08-23 a Telegram photo send failed with "the
// configured HttpClient.Timeout of 10 seconds elapsing" while Telegram:TimeoutSeconds was 20 -
// it had been running on HomeKit's 10s, because HomeKit is registered last. Ten seconds is
// generous for a sidecar on localhost and tight for a ~450 KB JPEG to api.telegram.org, and
// raising Telegram's own setting would have changed nothing.
public static class AlertActionHttpClients
{
    // Named after the implementation rather than the interface, which is the whole point - these
    // are the strings that keep the four registrations apart. Public so the test asserts against
    // the same constants the registration uses instead of re-typing them.
    public const string Webhook = nameof(WebhookAlertAction);
    public const string Telegram = nameof(TelegramAlertAction);
    public const string Discord = nameof(DiscordAlertAction);
    public const string HomeKit = nameof(HomeKitAlertAction);

    public static IServiceCollection AddAlertActionHttpClients(
        this IServiceCollection services,
        int webhookTimeoutSeconds,
        int telegramTimeoutSeconds,
        int discordTimeoutSeconds,
        int homeKitTimeoutSeconds)
    {
        services.AddHttpClient<IAlertAction, WebhookAlertAction>(
            Webhook, client => client.Timeout = TimeSpan.FromSeconds(webhookTimeoutSeconds));

        services.AddHttpClient<IAlertAction, TelegramAlertAction>(
            Telegram, client => client.Timeout = TimeSpan.FromSeconds(telegramTimeoutSeconds));

        services.AddHttpClient<IAlertAction, DiscordAlertAction>(
            Discord, client => client.Timeout = TimeSpan.FromSeconds(discordTimeoutSeconds));

        services.AddHttpClient<IAlertAction, HomeKitAlertAction>(
            HomeKit, client => client.Timeout = TimeSpan.FromSeconds(homeKitTimeoutSeconds));

        return services;
    }
}
