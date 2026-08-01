using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SentinelCore;
using Microsoft.AspNetCore.Mvc;

namespace SentinelCore.Web;

// Every user-editable settings section, in one place - shared by GET /api/settings and the
// config backup/restore feature (BackupEndpoints), so both read the exact same shape and
// there's no risk of the two drifting apart as sections get added later.
public sealed record SettingsSnapshot(
    BridgeSettingsDto? Bridge,
    OllamaSettingsDto? Ollama,
    CloudVisionSettingsDto? CloudVision,
    VisionRoutingSettingsDto? VisionRouting,
    FastLocalSettingsDto? FastLocal,
    ImageProcessingSettingsDto? ImageProcessing,
    Dictionary<string, PromptProfile>? PromptProfiles,
    EmailSettingsDto? Email,
    DailySummarySettingsDto? DailySummary,
    TelegramSettingsDto? Telegram,
    DiscordSettingsDto? Discord,
    TelegramChatSettingsDto? TelegramChat,
    BlueIrisSettingsDto? BlueIris,
    MultiFrameAnalysisSettingsDto? MultiFrameAnalysis,
    PackageTheftWatchSettingsDto? PackageTheftWatch,
    SemanticSearchSettingsDto? SemanticSearch,
    SelfMonitoringSettingsDto? SelfMonitoring,
    StartupNotificationSettingsDto? StartupNotification,
    InternetOutageSettingsDto? InternetOutage,
    QuietHoursSettingsDto? QuietHours,
    List<KnownVehicle>? KnownVehicles,
    List<KnownFace>? KnownFaces,
    MqttSettingsDto? Mqtt,
    DeviceHubSettingsDto? DeviceHub,
    HomeKitSettingsDto? HomeKit,
    BugReportSettingsDto? BugReports,
    WebhookSettingsDto? Webhook,
    LoginAlertSettingsDto? LoginAlerts,
    TriggerApiSettingsDto? TriggerApi,
    PushNotificationsSettingsDto? PushNotifications = null,
    LoggingSettingsDto? Logging = null,
    ServerSettingsDto? Server = null);

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        // Every route here - including the GET - is Admin-only: the settings snapshot contains
        // SMTP passwords, bot tokens, and API keys, so even read access isn't safe for a Viewer.
        var settings = app.MapGroup("/api/settings").RequireAuthorization("AdminOnly");

        settings.MapGet("", async (AppSettingsFile appSettings, CancellationToken ct) =>
        {
            // Secrets go out masked - a settings form only ever needs to know a secret is set,
            // never what it is, and this response now reaches phones and TVs, not just a browser
            // on the LAN. The config backup endpoint deliberately keeps using BuildSnapshotAsync
            // directly, unmasked: a backup that couldn't restore its own secrets wouldn't be one.
            var snapshot = await BuildSnapshotAsync(appSettings, ct);
            SettingsSecrets.MaskInPlace(snapshot);
            return Results.Ok(snapshot);
        });

        // What the sections and fields *are*, as opposed to what they're currently set to. Every
        // client renders its settings forms from this rather than hard-coding its own copy - see
        // SettingsSchema. Static data with no stored values in it at all, but kept inside the
        // AdminOnly group with the rest: only an admin has any use for it, and leaving it open
        // would hand an unauthenticated caller a map of every configurable field on the box.
        settings.MapGet("/schema", () => Results.Ok(SettingsSchema.Sections));

        settings.MapPut("/bridge", async (BridgeSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Bridge", ct, extra =>
            {
                if (dto.ApiKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
                    extra.Add("ApiKey must be changed from the default.");
            }));

        settings.MapPut("/ollama", async (OllamaSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Ollama", ct));

        settings.MapPut("/cloud-vision", async (CloudVisionSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "CloudVision", ct, extra =>
            {
                if (dto.Enabled && string.IsNullOrWhiteSpace(dto.ApiKey))
                    extra.Add("ApiKey is required when cloud vision is enabled.");
                if (dto.Provider is not ("openai" or "anthropic" or "google"))
                    extra.Add("Provider must be 'openai', 'anthropic', or 'google'.");
            }));

        settings.MapPut("/vision-routing", async (VisionRoutingSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "VisionRouting", ct, extra =>
            {
                if (dto.Primary is not ("ollama" or "cloud" or "fastlocal"))
                    extra.Add("Primary must be 'ollama', 'cloud', or 'fastlocal'.");
                if (dto.EscalationTarget is not ("auto" or "cloud" or "ollama"))
                    extra.Add("EscalationTarget must be 'auto', 'cloud', or 'ollama'.");
            }));

        settings.MapPut("/fast-local", async (FastLocalSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "FastLocal", ct));

        settings.MapPut("/image-processing", async (ImageProcessingSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "ImageProcessing", ct));

        settings.MapPut("/email", async (EmailSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Email", ct, extra =>
            {
                if (!dto.Enabled) return;
                if (string.IsNullOrWhiteSpace(dto.Username)) extra.Add("Username is required when email is enabled.");
                if (string.IsNullOrWhiteSpace(dto.AppPassword)) extra.Add("AppPassword is required when email is enabled.");
                if (string.IsNullOrWhiteSpace(dto.ToAddress)) extra.Add("ToAddress is required when email is enabled.");
                if (!string.IsNullOrWhiteSpace(dto.Username) && !new EmailAddressAttribute().IsValid(dto.Username))
                    extra.Add("Username must be a valid email address.");
                if (!string.IsNullOrWhiteSpace(dto.ToAddress) && !new EmailAddressAttribute().IsValid(dto.ToAddress))
                    extra.Add("ToAddress must be a valid email address.");
            }));

        settings.MapPut("/daily-summary", async (DailySummarySettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "DailySummary", ct, extra =>
            {
                if (dto.MorningHour == dto.EveningHour)
                    extra.Add("MorningHour and EveningHour must differ.");
            }));

        settings.MapPut("/telegram", async (TelegramSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Telegram", ct, extra =>
            {
                if (!dto.Enabled) return;
                if (string.IsNullOrWhiteSpace(dto.BotToken)) extra.Add("BotToken is required when Telegram is enabled.");
                if (string.IsNullOrWhiteSpace(dto.ChatId)) extra.Add("ChatId is required when Telegram is enabled.");
            }));

        settings.MapPut("/discord", async (DiscordSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Discord", ct, extra =>
            {
                if (dto.Enabled && !DiscordOptions.IsValidWebhookUrl(dto.WebhookUrl))
                    extra.Add("WebhookUrl must be a valid Discord HTTPS webhook URL when Discord is enabled.");
            }));

        settings.MapPut("/telegram-chat", async (TelegramChatSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "TelegramChat", ct));

        settings.MapPut("/blue-iris", async (BlueIrisSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "BlueIris", ct, extra =>
            {
                if (dto.Enabled && string.IsNullOrWhiteSpace(dto.BaseUrl))
                    extra.Add("BaseUrl is required when Blue Iris is enabled.");
                if (dto.Enabled && string.IsNullOrWhiteSpace(dto.Camera))
                    extra.Add("Camera is required when Blue Iris is enabled.");
            }));

        settings.MapPut("/self-monitoring", async (SelfMonitoringSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "SelfMonitoring", ct));

        settings.MapPut("/multi-frame-analysis", async (MultiFrameAnalysisSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "MultiFrameAnalysis", ct));

        settings.MapPut("/package-theft-watch", async (PackageTheftWatchSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "PackageTheftWatch", ct));

        settings.MapPut("/semantic-search", async (SemanticSearchSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "SemanticSearch", ct));

        settings.MapPut("/startup-notification", async (StartupNotificationSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "StartupNotification", ct));

        settings.MapPut("/internet-outage", async (InternetOutageSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "InternetOutage", ct, extra =>
            {
                // A typo'd probe URL would fail every check forever and read as a permanent
                // outage, so it's caught here rather than at 3am.
                foreach (var url in dto.ProbeUrls)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                        (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                        extra.Add($"Probe URL '{url}' must be a full http:// or https:// address.");
                }

                // Every probe has to time out before a check counts as failed, so a check cycle
                // can otherwise outlast the interval it's meant to run on. Empty means the
                // built-in list, so that's what gets counted.
                var probeCount = dto.ProbeUrls.Count > 0 ? dto.ProbeUrls.Count : InternetOutageOptions.DefaultProbeUrls.Count;
                var worstCaseSeconds = dto.ProbeTimeoutSeconds * probeCount;
                if (worstCaseSeconds > dto.CheckIntervalSeconds)
                    extra.Add(
                        $"Probe Timeout x {probeCount} probe(s) is {worstCaseSeconds}s, which is longer than the {dto.CheckIntervalSeconds}s check interval. Lower the timeout, use fewer probes, or raise the interval.");
            }));

        settings.MapPut("/quiet-hours", async (QuietHoursSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "QuietHours", ct));

        settings.MapPut("/mqtt", async (MqttSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Mqtt", ct, extra =>
            {
                if (!dto.Enabled) return;
                if (!dto.RunEmbeddedBroker && string.IsNullOrWhiteSpace(dto.BrokerHost))
                    extra.Add("Broker Host is required when not running the embedded broker.");
            }));

        // The one section whose validity depends on another one: the Device Hub controls devices
        // over the MQTT connection the section above configures, so enabling it without MQTT
        // would produce a tab whose every button fails. Checked here rather than in
        // SaveSectionAsync's synchronous callback because it has to read the current Mqtt block.
        settings.MapPut("/device-hub", async (DeviceHubSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            var errors = ValidateDataAnnotations(dto);

            if (dto.Enabled)
            {
                var mqtt = await appSettings.GetSectionAsync<MqttSettingsDto>("Mqtt", ct);
                if (mqtt is null || !mqtt.Enabled)
                    errors.Add("Turn on the MQTT / Home Assistant section above first - the Device Hub controls devices over that same broker connection.");
            }

            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await appSettings.MergeSectionAsync("DeviceHub", dto, ct);
            return Results.Ok(new { saved = true });
        });

        settings.MapPut("/homekit", async (HomeKitSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "HomeKit", ct, extra =>
            {
                if (dto.Enabled && !Uri.TryCreate(dto.SidecarBaseUrl, UriKind.Absolute, out _))
                    extra.Add("Sidecar Base Url must be a valid absolute URL when HomeKit is enabled.");
            }));

        settings.MapPut("/bug-reports", async (BugReportSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "BugReports", ct));

        settings.MapPut("/login-alerts", async (LoginAlertSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "LoginAlerts", ct));

        // Generates the key the first time this is switched on, rather than making the owner
        // invent one - the same reasoning as AutoApiKeyProvisioner for Bridge:ApiKey, and it means
        // a blank key field in the UI never silently saves a feature that can't authenticate
        // anything. An existing key is left alone; clearing the field on purpose rotates it.
        settings.MapPut("/trigger-api", async (TriggerApiSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            if (dto.Enabled && string.IsNullOrWhiteSpace(dto.ApiKey))
                dto.ApiKey = RandomNumberGenerator.GetHexString(48);

            return await SaveSectionAsync(dto, appSettings, "TriggerApi", ct);
        });

        settings.MapPut("/webhook", async (WebhookSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Webhook", ct, extra =>
            {
                if (dto.Enabled && !Uri.TryCreate(dto.Url, UriKind.Absolute, out _))
                    extra.Add("Url must be a valid absolute URL when the webhook is enabled.");
            }));

        settings.MapPut("/push-notifications", async (PushNotificationsSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "PushNotifications", ct, extra =>
            {
                // Same shape of guard as Device Hub's "MQTT must be on first": the master toggle
                // with no delivery service underneath it would silently send nothing.
                if (dto.Enabled && !dto.Apple.Enabled && !dto.Firebase.Enabled)
                    extra.Add("Turn on Apple or Firebase below first - push notifications need at least one delivery service.");
            }));

        // Logging lives at Logging.LogLevel with a dotted category name ("Microsoft.AspNetCore")
        // that a flat schema field path can't carry - see LoggingSettingsDto. Mapped by hand
        // here; WriteLoggingAsync preserves any extra categories added to the file directly.
        settings.MapPut("/logging", async (LoggingSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            var errors = new List<string>();
            foreach (var (label, value) in new[] { ("Default", dto.Default), ("Microsoft.AspNetCore", dto.MicrosoftAspNetCore) })
            {
                if (!LogLevelNames.Contains(value))
                    errors.Add($"{label} must be one of: {string.Join(", ", LogLevelNames)}.");
            }
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await WriteLoggingAsync(dto, appSettings, ct);
            return Results.Ok(new { saved = true });
        });

        // "Urls" is a bare string at the root of appsettings.json, not a section - the only save
        // here that doesn't go through MergeSectionAsync. Kestrel binds it once at startup, so
        // this is saved-but-pending until the server restarts, same contract as Internet
        // Exposed Mode below.
        settings.MapPut("/server", async (ServerSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            var errors = ValidateDataAnnotations(dto);
            var parts = dto.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                errors.Add("At least one listen address is required, e.g. http://0.0.0.0:5080.");
            foreach (var part in parts)
            {
                // Kestrel accepts wildcard hosts (http://*:5080, http://+:5080) that Uri can't
                // parse - swap them for a placeholder so the rest of the address still gets
                // checked.
                var parseable = part.Replace("//*:", "//wildcard-host:").Replace("//+:", "//wildcard-host:");
                if (!Uri.TryCreate(parseable, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    errors.Add($"'{part}' is not a valid http(s) listen address.");
            }
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await appSettings.ReplaceSectionAsync("Urls", dto.Urls, ct);
            return Results.Ok(new { saved = true, restartRequired = true });
        });

        settings.MapPut("/known-vehicles", async (List<KnownVehicle> vehicles, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            var errors = new List<string>();
            foreach (var vehicle in vehicles)
            {
                if (string.IsNullOrWhiteSpace(vehicle.Plate))
                    errors.Add("Every known vehicle needs a plate.");
            }

            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await appSettings.ReplaceSectionAsync("KnownVehicles", vehicles, ct);
            return Results.Ok(new { saved = true });
        });

        settings.MapPut("/prompt-profiles", async (Dictionary<string, PromptProfile> profiles, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            var errors = new List<string>();
            if (profiles.Count == 0)
                errors.Add("At least one prompt profile is required.");
            if (!profiles.ContainsKey("default"))
                errors.Add("A 'default' profile is required as a fallback for unrecognized profile names.");

            foreach (var (name, profile) in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.SystemPrompt))
                    errors.Add($"Profile '{name}': SystemPrompt is required.");
                if (profile.Questions.Count == 0)
                    errors.Add($"Profile '{name}': at least one question is required.");
                if (profile.Crop is { } crop &&
                    (crop.Left < 0 || crop.Top < 0 || crop.Right > 1 || crop.Bottom > 1 ||
                     crop.Left >= crop.Right || crop.Top >= crop.Bottom))
                {
                    errors.Add($"Profile '{name}': Crop must satisfy 0 <= Left < Right <= 1 and 0 <= Top < Bottom <= 1.");
                }
            }

            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await appSettings.ReplaceSectionAsync("PromptProfiles", profiles, ct);
            return Results.Ok(new { saved = true });
        });

        // Lives in this project's own appsettings.json (a keyed AppSettingsFile instance
        // registered separately in Program.cs), not the main app's shared settings file - it
        // configures this project's own Kestrel binding, the same category as the Https block
        // it depends on. Takes effect after a restart, like Https already does.
        settings.MapGet("/internet-exposed", async (
            [FromKeyedServices("web")] AppSettingsFile webSettings,
            InternetExposedSettings active,
            CancellationToken ct) =>
        {
            var dto = await webSettings.GetSectionAsync<InternetExposedSettingsDto>("InternetExposed", ct)
                ?? new InternetExposedSettingsDto();
            return Results.Ok(new { dto.Enabled, dto.Port, active = active.Enabled, activePort = active.Port });
        });

        settings.MapPut("/internet-exposed", async (
            InternetExposedSettingsDto dto,
            [FromKeyedServices("web")] AppSettingsFile webSettings,
            IConfiguration configuration,
            IAccountStore accounts,
            CancellationToken ct) =>
        {
            var errors = ValidateDataAnnotations(dto);
            if (dto.Enabled)
            {
                var httpsSettings = HttpsSettings.FromConfiguration(configuration);
                if (!httpsSettings.CanEnable(out var httpsReason))
                {
                    errors.Add(httpsReason
                        ?? "Internet Exposed Mode requires a local HTTPS certificate to be configured first - see docs/DEPLOYMENT.md.");
                }
                else if (dto.Port == httpsSettings.Port)
                {
                    errors.Add($"Internet Exposed port {dto.Port} must be different from the HTTPS port.");
                }

                if (!await accounts.AnyAccountsExistAsync(ct))
                    errors.Add("Create an admin account before enabling Internet Exposed Mode - otherwise the port would require login with no way to sign in.");
            }
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await webSettings.MergeSectionAsync("InternetExposed", dto, ct);
            return Results.Ok(new { saved = true, restartRequired = true });
        });
    }

    public static async Task<SettingsSnapshot> BuildSnapshotAsync(AppSettingsFile settings, CancellationToken cancellationToken) => new(
        await settings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken),
        await settings.GetSectionAsync<OllamaSettingsDto>("Ollama", cancellationToken),
        await settings.GetSectionAsync<CloudVisionSettingsDto>("CloudVision", cancellationToken),
        await settings.GetSectionAsync<VisionRoutingSettingsDto>("VisionRouting", cancellationToken),
        await settings.GetSectionAsync<FastLocalSettingsDto>("FastLocal", cancellationToken),
        await settings.GetSectionAsync<ImageProcessingSettingsDto>("ImageProcessing", cancellationToken),
        await settings.GetSectionAsync<Dictionary<string, PromptProfile>>("PromptProfiles", cancellationToken),
        await settings.GetSectionAsync<EmailSettingsDto>("Email", cancellationToken),
        await settings.GetSectionAsync<DailySummarySettingsDto>("DailySummary", cancellationToken),
        await settings.GetSectionAsync<TelegramSettingsDto>("Telegram", cancellationToken),
        await settings.GetSectionAsync<DiscordSettingsDto>("Discord", cancellationToken),
        await settings.GetSectionAsync<TelegramChatSettingsDto>("TelegramChat", cancellationToken),
        await settings.GetSectionAsync<BlueIrisSettingsDto>("BlueIris", cancellationToken),
        await settings.GetSectionAsync<MultiFrameAnalysisSettingsDto>("MultiFrameAnalysis", cancellationToken),
        await settings.GetSectionAsync<PackageTheftWatchSettingsDto>("PackageTheftWatch", cancellationToken),
        await settings.GetSectionAsync<SemanticSearchSettingsDto>("SemanticSearch", cancellationToken),
        await settings.GetSectionAsync<SelfMonitoringSettingsDto>("SelfMonitoring", cancellationToken),
        await settings.GetSectionAsync<StartupNotificationSettingsDto>("StartupNotification", cancellationToken),
        await settings.GetSectionAsync<InternetOutageSettingsDto>("InternetOutage", cancellationToken),
        await settings.GetSectionAsync<QuietHoursSettingsDto>("QuietHours", cancellationToken),
        await settings.GetSectionAsync<List<KnownVehicle>>("KnownVehicles", cancellationToken) ?? [],
        await settings.GetSectionAsync<List<KnownFace>>("KnownFaces", cancellationToken) ?? [],
        await settings.GetSectionAsync<MqttSettingsDto>("Mqtt", cancellationToken),
        await settings.GetSectionAsync<DeviceHubSettingsDto>("DeviceHub", cancellationToken),
        await settings.GetSectionAsync<HomeKitSettingsDto>("HomeKit", cancellationToken),
        await settings.GetSectionAsync<BugReportSettingsDto>("BugReports", cancellationToken),
        await settings.GetSectionAsync<WebhookSettingsDto>("Webhook", cancellationToken),
        await settings.GetSectionAsync<LoginAlertSettingsDto>("LoginAlerts", cancellationToken),
        await settings.GetSectionAsync<TriggerApiSettingsDto>("TriggerApi", cancellationToken),
        await settings.GetSectionAsync<PushNotificationsSettingsDto>("PushNotifications", cancellationToken),
        await ReadLoggingAsync(settings, cancellationToken),
        await ReadServerAsync(settings, cancellationToken));

    // Applied by config restore - merges each present section exactly the way its own PUT
    // endpoint above already does (Merge for most sections, Replace for the two wholly user-
    // owned lists), so restoring behaves identically to re-saving each section by hand. A
    // section absent from the snapshot (null) is left completely untouched - restore overlays
    // onto the current config, it never wipes something the backup doesn't mention.
    public static async Task ApplySnapshotAsync(SettingsSnapshot snapshot, AppSettingsFile settings, CancellationToken cancellationToken)
    {
        // Sections holding secrets go through MergeUnmaskedAsync: a snapshot that came from the
        // masked GET /api/settings (rather than the unmasked backup endpoint) must keep the
        // current secrets, not overwrite them with mask characters.
        if (snapshot.Bridge is not null) await MergeUnmaskedAsync(snapshot.Bridge, "Bridge", settings, cancellationToken);
        if (snapshot.Ollama is not null) await settings.MergeSectionAsync("Ollama", snapshot.Ollama, cancellationToken);
        if (snapshot.CloudVision is not null) await MergeUnmaskedAsync(snapshot.CloudVision, "CloudVision", settings, cancellationToken);
        if (snapshot.VisionRouting is not null) await settings.MergeSectionAsync("VisionRouting", snapshot.VisionRouting, cancellationToken);
        if (snapshot.FastLocal is not null) await settings.MergeSectionAsync("FastLocal", snapshot.FastLocal, cancellationToken);
        if (snapshot.ImageProcessing is not null) await settings.MergeSectionAsync("ImageProcessing", snapshot.ImageProcessing, cancellationToken);
        if (snapshot.PromptProfiles is not null) await settings.ReplaceSectionAsync("PromptProfiles", snapshot.PromptProfiles, cancellationToken);
        if (snapshot.Email is not null) await MergeUnmaskedAsync(snapshot.Email, "Email", settings, cancellationToken);
        if (snapshot.DailySummary is not null) await settings.MergeSectionAsync("DailySummary", snapshot.DailySummary, cancellationToken);
        if (snapshot.Telegram is not null) await MergeUnmaskedAsync(snapshot.Telegram, "Telegram", settings, cancellationToken);
        if (snapshot.Discord is not null) await MergeUnmaskedAsync(snapshot.Discord, "Discord", settings, cancellationToken);
        if (snapshot.TelegramChat is not null) await settings.MergeSectionAsync("TelegramChat", snapshot.TelegramChat, cancellationToken);
        if (snapshot.BlueIris is not null) await MergeUnmaskedAsync(snapshot.BlueIris, "BlueIris", settings, cancellationToken);
        if (snapshot.MultiFrameAnalysis is not null) await settings.MergeSectionAsync("MultiFrameAnalysis", snapshot.MultiFrameAnalysis, cancellationToken);
        if (snapshot.PackageTheftWatch is not null) await settings.MergeSectionAsync("PackageTheftWatch", snapshot.PackageTheftWatch, cancellationToken);
        if (snapshot.SemanticSearch is not null) await settings.MergeSectionAsync("SemanticSearch", snapshot.SemanticSearch, cancellationToken);
        if (snapshot.SelfMonitoring is not null) await settings.MergeSectionAsync("SelfMonitoring", snapshot.SelfMonitoring, cancellationToken);
        if (snapshot.StartupNotification is not null) await settings.MergeSectionAsync("StartupNotification", snapshot.StartupNotification, cancellationToken);
        if (snapshot.InternetOutage is not null) await settings.MergeSectionAsync("InternetOutage", snapshot.InternetOutage, cancellationToken);
        if (snapshot.QuietHours is not null) await settings.MergeSectionAsync("QuietHours", snapshot.QuietHours, cancellationToken);
        if (snapshot.KnownVehicles is not null) await settings.ReplaceSectionAsync("KnownVehicles", snapshot.KnownVehicles, cancellationToken);
        // Enrollment photos on disk aren't part of the backup file (same as alert images
        // aren't) - restoring KnownFaces brings back the labels/embeddings, but re-enrolling
        // is needed if the photos themselves were lost.
        if (snapshot.KnownFaces is not null) await settings.ReplaceSectionAsync("KnownFaces", snapshot.KnownFaces, cancellationToken);
        if (snapshot.Mqtt is not null) await MergeUnmaskedAsync(snapshot.Mqtt, "Mqtt", settings, cancellationToken);
        if (snapshot.DeviceHub is not null) await settings.MergeSectionAsync("DeviceHub", snapshot.DeviceHub, cancellationToken);
        if (snapshot.HomeKit is not null) await settings.MergeSectionAsync("HomeKit", snapshot.HomeKit, cancellationToken);
        if (snapshot.BugReports is not null) await settings.MergeSectionAsync("BugReports", snapshot.BugReports, cancellationToken);
        if (snapshot.Webhook is not null) await MergeUnmaskedAsync(snapshot.Webhook, "Webhook", settings, cancellationToken);
        if (snapshot.LoginAlerts is not null) await settings.MergeSectionAsync("LoginAlerts", snapshot.LoginAlerts, cancellationToken);
        // Carries its own key, so restoring a backup onto a rebuilt machine keeps whatever the
        // doorbells and relays out there are already configured to send.
        if (snapshot.TriggerApi is not null) await settings.MergeSectionAsync("TriggerApi", snapshot.TriggerApi, cancellationToken);
        if (snapshot.PushNotifications is not null) await settings.MergeSectionAsync("PushNotifications", snapshot.PushNotifications, cancellationToken);
        if (snapshot.Logging is not null) await WriteLoggingAsync(snapshot.Logging, settings, cancellationToken);
        if (snapshot.Server is not null) await settings.ReplaceSectionAsync("Urls", snapshot.Server.Urls, cancellationToken);
    }

    // Restore-side twin of the unmask in SaveSectionAsync. New sections whose schema gains a
    // password field need their ApplySnapshotAsync line switched to this - the
    // Secrets_SurviveARestoreOfAMaskedExport test covers the pattern.
    private static async Task MergeUnmaskedAsync<T>(
        T dto, string sectionName, AppSettingsFile settings, CancellationToken cancellationToken)
        where T : notnull
    {
        await SettingsSecrets.UnmaskAsync(dto, sectionName, settings, cancellationToken);
        await settings.MergeSectionAsync(sectionName, dto, cancellationToken);
    }

    private static readonly string[] LogLevelNames =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    // The two named categories are the ones the shipped appsettings.json carries; anything else
    // in LogLevel (a category someone added by hand for targeted debugging) is read back and
    // written out untouched rather than being clobbered by the save.
    private static async Task<LoggingSettingsDto?> ReadLoggingAsync(AppSettingsFile settings, CancellationToken cancellationToken)
    {
        var logging = await settings.GetSectionAsync<JsonObject>("Logging", cancellationToken);
        if (logging?["LogLevel"] is not JsonObject levels) return null;
        return new LoggingSettingsDto
        {
            Default = levels["Default"]?.GetValue<string>() ?? "Information",
            MicrosoftAspNetCore = levels["Microsoft.AspNetCore"]?.GetValue<string>() ?? "Warning"
        };
    }

    private static async Task WriteLoggingAsync(LoggingSettingsDto dto, AppSettingsFile settings, CancellationToken cancellationToken)
    {
        var logging = await settings.GetSectionAsync<JsonObject>("Logging", cancellationToken) ?? new JsonObject();
        if (logging["LogLevel"] is not JsonObject levels)
        {
            levels = new JsonObject();
            logging["LogLevel"] = levels;
        }
        levels["Default"] = dto.Default;
        levels["Microsoft.AspNetCore"] = dto.MicrosoftAspNetCore;
        await settings.ReplaceSectionAsync("Logging", logging, cancellationToken);
    }

    private static async Task<ServerSettingsDto?> ReadServerAsync(AppSettingsFile settings, CancellationToken cancellationToken)
    {
        var urls = await settings.GetSectionAsync<string>("Urls", cancellationToken);
        return string.IsNullOrWhiteSpace(urls) ? null : new ServerSettingsDto { Urls = urls };
    }

    private static async Task<IResult> SaveSectionAsync<T>(
        T dto,
        AppSettingsFile settings,
        string sectionName,
        CancellationToken cancellationToken,
        Action<List<string>>? extraValidation = null)
        where T : notnull
    {
        // Secret fields arriving as the literal mask mean "unchanged" - swapped back to the
        // stored value first, so validation (and the extraValidation closures reading dto
        // fields) judge the real value rather than eight mask characters.
        await SettingsSecrets.UnmaskAsync(dto, sectionName, settings, cancellationToken);

        var errors = ValidateDataAnnotations(dto);
        extraValidation?.Invoke(errors);
        if (errors.Count > 0) return Results.BadRequest(new { errors });

        await settings.MergeSectionAsync(sectionName, dto, cancellationToken);
        return Results.Ok(new { saved = true });
    }

    private static List<string> ValidateDataAnnotations<T>(T instance) where T : notnull
    {
        var context = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, context, results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid value.").ToList();
    }
}
