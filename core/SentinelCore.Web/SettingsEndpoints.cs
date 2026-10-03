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
    TriggerRewindSettingsDto? TriggerRewind,
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
    ServerSettingsDto? Server = null,
    TriggerConfirmationSettingsDto? TriggerConfirmation = null,
    HostRebootSettingsDto? HostReboot = null,
    LocationSettingsDto? Location = null,
    ScheduleSettingsDto? Schedules = null,
    NuisanceAdvisorSettingsDto? NuisanceAdvisor = null,
    RepeatAlertsSettingsDto? RepeatAlerts = null,
    // Appended last, with a default, so a backup file written before this property existed still
    // deserializes - the same rule the properties above it follow.
    ScheduledRecordingSettingsDto? ScheduledRecording = null,
    OrganizerSettingsDto? Organizer = null,
    AutoExportSettingsDto? AutoExport = null,
    WeatherAlertsSettingsDto? WeatherAlerts = null,
    AlarmSettingsDto? Alarm = null,
    FamilyFilesSettingsDto? FamilyFiles = null,
    MediaSettingsDto? Media = null,
    MediaDownloadsSettingsDto? MediaDownloads = null,
    // Music (MUSIC_AND_PODCASTS.md).
    MusicSettingsDto? Music = null);

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
            var snapshot = WithDefaultsForMissingSections(await BuildSnapshotAsync(appSettings, ct));
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
                // Deliberately the same call the server makes at startup, so a form that saves
                // cleanly here can never be a config the service then refuses to boot on.
                if (SmtpMailer.ValidateDeliverable(dto.ToOptions()) is { } problem) extra.Add(problem);
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

        settings.MapPut("/trigger-rewind", async (TriggerRewindSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "TriggerRewind", ct));

        settings.MapPut("/scheduled-recording", async (ScheduledRecordingSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, ScheduledRecordingOptions.SectionName, ct));

        settings.MapPut("/organizer", async (OrganizerSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, OrganizerSettingsDto.SectionName, ct));

        settings.MapPut("/alarm", async (AlarmSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, AlarmSettingsDto.SectionName, ct));

        settings.MapPut("/media", async (MediaSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            dto.TmdbApiKey = dto.TmdbApiKey?.Trim() ?? string.Empty;
            return await SaveSectionAsync(dto, appSettings, MediaSettingsDto.SectionName, ct);
        });

        // Music.
        settings.MapPut("/music", async (MusicSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, MusicSettingsDto.SectionName, ct));

        // MOVIE DOWNLOADS -> Mullvad Location's list (SettingsOptionsSourceSchema contract).
        settings.MapGet("/media-downloads/mullvad-locations", async (MediaVpnConfig vpn, CancellationToken ct) =>
        {
            try { return Results.Ok(new { options = await vpn.MullvadLocationsAsync(ct) }); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return Results.Json(new { error = "Could not fetch Mullvad's server list: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        settings.MapPut("/media-downloads", async (MediaDownloadsSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            dto.ProwlarrUrl = dto.ProwlarrUrl?.Trim() ?? string.Empty;
            dto.QbittorrentUrl = dto.QbittorrentUrl?.Trim() ?? string.Empty;
            dto.ProwlarrApiKey = dto.ProwlarrApiKey?.Trim() ?? string.Empty;
            dto.QbittorrentUsername = dto.QbittorrentUsername?.Trim() ?? string.Empty;
            dto.VpnProvider = dto.VpnProvider?.Trim().ToLowerInvariant() ?? MediaVpnProviders.Installed;
            dto.MullvadLocation = dto.MullvadLocation?.Trim().ToLowerInvariant() ?? "se";
            dto.WireGuardAddress = dto.WireGuardAddress?.Trim() ?? string.Empty;
            dto.WireGuardDns = dto.WireGuardDns?.Trim() ?? string.Empty;
            dto.WireGuardServerPublicKey = dto.WireGuardServerPublicKey?.Trim() ?? string.Empty;
            dto.WireGuardServerEndpoint = dto.WireGuardServerEndpoint?.Trim() ?? string.Empty;
            // Secrets arrive masked when unchanged; judge them after SaveSectionAsync unmasks them.
            return await SaveSectionAsync(dto, appSettings, MediaDownloadsSettingsDto.SectionName, ct, extra =>
            {
                extra.AddRange(MediaVpnConfig.Validate(dto));
                if (dto.AllowedUsers is not ("admins" or "everyone")) extra.Add("Who Can Add must be admins or everyone.");
                if (dto.PreferredQuality is not ("720p" or "1080p" or "2160p" or MediaDownloadsSettingsDto.QualityAny))
                    extra.Add("Preferred Quality must be 720p, 1080p, 2160p or any.");
                foreach (var (label, url) in new[] { ("Prowlarr Address", dto.ProwlarrUrl), ("qBittorrent Address", dto.QbittorrentUrl) })
                    if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                        extra.Add($"{label} must be a web address like http://10.250.0.2:8080.");
            });
        });

        settings.MapPut("/auto-export", async (AutoExportSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            dto.Destination = dto.Destination?.Trim().ToLowerInvariant() ?? AutoExportSettingsDto.DestinationFolder;
            dto.FolderPath = dto.FolderPath?.Trim() ?? string.Empty;
            dto.SftpHost = dto.SftpHost?.Trim() ?? string.Empty;
            dto.SftpFolder = dto.SftpFolder?.Trim() ?? string.Empty;
            dto.SftpHostKeyFingerprint = dto.SftpHostKeyFingerprint?.Trim() ?? string.Empty;
            return await SaveSectionAsync(dto, appSettings, AutoExportSettingsDto.SectionName, ct);
        });

        settings.MapPut("/family-files", async (FamilyFilesSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            dto.LibraryFolder = dto.LibraryFolder?.Trim() ?? string.Empty;
            return await SaveSectionAsync(dto, appSettings, FamilyFilesSettingsDto.SectionName, ct);
        });

        settings.MapPut("/package-theft-watch", async (PackageTheftWatchSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "PackageTheftWatch", ct));

        settings.MapPut("/semantic-search", async (SemanticSearchSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "SemanticSearch", ct));

        settings.MapPut("/repeat-alerts", async (RepeatAlertsSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "RepeatAlerts", ct));

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

        settings.MapPut("/weather-alerts", async (WeatherAlertsSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
        {
            dto.MinimumLevel = dto.MinimumLevel?.Trim().ToLowerInvariant() ?? WeatherAlertOptions.LevelWatch;
            return await SaveSectionAsync(dto, appSettings, WeatherAlertOptions.SectionName, ct, extra =>
            {
                if (dto.MinimumLevel is not (WeatherAlertOptions.LevelWarning or WeatherAlertOptions.LevelWatch or WeatherAlertOptions.LevelAdvisory))
                    extra.Add("Send Alerts At must be warnings only, watches and warnings, or everything.");
                // Switched on with every kind unticked would check the NWS every few minutes and
                // never say anything, which reads as broken rather than as a choice.
                if (dto.Enabled && !dto.HasAnyKind())
                    extra.Add("Tick at least one kind of weather to be told about, or turn Weather Alerts off.");
            });
        });

        settings.MapPut("/location", async (LocationSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Location", ct, extra =>
            {
                // (0, 0) is how "not set" is stored, so it has to stay allowed - but half a
                // coordinate pair is always a mistake, and one that would quietly put every
                // sunset an ocean away.
                if (dto.Latitude == 0 && dto.Longitude != 0)
                    extra.Add("Latitude is missing. Enter both coordinates, or clear both to unset your location.");
                if (dto.Longitude == 0 && dto.Latitude != 0)
                    extra.Add("Longitude is missing. Enter both coordinates, or clear both to unset your location.");
            }));

        settings.MapPut("/schedules", async (ScheduleSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "Schedules", ct, extra =>
            {
                if (string.IsNullOrWhiteSpace(dto.TimeZone)) return;

                try
                {
                    TimeZoneInfo.FindSystemTimeZoneById(dto.TimeZone.Trim());
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    // Caught on the way in rather than discovered as "every schedule ran an hour
                    // out" - the worker falls back to the machine's zone, which looks like a bug.
                    extra.Add($"'{dto.TimeZone}' is not a time zone this server knows. Use an IANA id such as America/New_York, or leave it empty to use the server's own time zone.");
                }
            }));

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

        settings.MapPut("/nuisance-advisor", async (NuisanceAdvisorSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "NuisanceAdvisor", ct));

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

        settings.MapPut("/trigger-confirmation", async (TriggerConfirmationSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "TriggerConfirmation", ct));

        // Saved into the main app's appsettings.json like every other section here, and read back
        // there through IOptionsMonitor - so switching this on takes effect within a second or two
        // and needs no restart of anything.
        settings.MapPut("/host-reboot", async (HostRebootSettingsDto dto, AppSettingsFile appSettings, CancellationToken ct) =>
            await SaveSectionAsync(dto, appSettings, "HostReboot", ct));

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
                if (dto.Enabled && !dto.Apple.Enabled && !dto.Firebase.Enabled && !dto.Relay.Enabled)
                    extra.Add("Turn on the Relay, Apple or Firebase below first - push notifications need at least one delivery service.");
                if (dto.Relay.Enabled &&
                    (!Uri.TryCreate(dto.Relay.Url, UriKind.Absolute, out var relayUri) ||
                     (relayUri.Scheme != Uri.UriSchemeHttps && relayUri.Scheme != Uri.UriSchemeHttp)))
                    extra.Add("Relay: Address must be a full address starting with https://.");
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
            var errors = await ValidateInternetExposedAsync(dto, configuration, accounts, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await webSettings.MergeSectionAsync("InternetExposed", dto, ct);
            return Results.Ok(new { saved = true, restartRequired = true });
        });
    }

    // Shared with restore (BackupService), which applies a backup's Internet Exposed Mode only when
    // this machine could actually run it - the rules depend on this machine's HTTPS certificate.
    internal static async Task<List<string>> ValidateInternetExposedAsync(
        InternetExposedSettingsDto dto, IConfiguration configuration, IAccountStore accounts, CancellationToken ct)
    {
        var errors = ValidateDataAnnotations(dto);
        if (!dto.Enabled) return errors;

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
        return errors;
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
        await settings.GetSectionAsync<TriggerRewindSettingsDto>("TriggerRewind", cancellationToken),
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
        await ReadServerAsync(settings, cancellationToken),
        // Defaulted rather than left null when the section is absent, so the settings form shows
        // this on (which is what an install with no section actually does) instead of unchecked.
        await settings.GetSectionAsync<TriggerConfirmationSettingsDto>("TriggerConfirmation", cancellationToken)
            ?? new TriggerConfirmationSettingsDto(),
        // Defaulted like TriggerConfirmation above so the form shows the real off-by-default
        // state and the 60-second undo window, rather than an empty box, on an install whose
        // appsettings.json predates this feature.
        await settings.GetSectionAsync<HostRebootSettingsDto>("HostReboot", cancellationToken)
            ?? new HostRebootSettingsDto(),
        // Defaulted for the same reason as the two above: an install predating schedules should
        // show the real defaults in the form, not an empty location and a blank interval.
        await settings.GetSectionAsync<LocationSettingsDto>("Location", cancellationToken)
            ?? new LocationSettingsDto(),
        await settings.GetSectionAsync<ScheduleSettingsDto>("Schedules", cancellationToken)
            ?? new ScheduleSettingsDto(),
        // Defaulted for the same reason as the sections above, and it matters more here: this one
        // ships on, so a null would show the form unchecked while the advisor was in fact running.
        await settings.GetSectionAsync<NuisanceAdvisorSettingsDto>("NuisanceAdvisor", cancellationToken)
            ?? new NuisanceAdvisorSettingsDto(),
        // Defaulted for the same reason: both of these ship on, so a null would show the form as
        // unchecked and zero while the pipeline was in fact merging repeat alerts.
        await settings.GetSectionAsync<RepeatAlertsSettingsDto>("RepeatAlerts", cancellationToken)
            ?? new RepeatAlertsSettingsDto(),
        await settings.GetSectionAsync<ScheduledRecordingSettingsDto>(
            ScheduledRecordingOptions.SectionName, cancellationToken),
        await settings.GetSectionAsync<OrganizerSettingsDto>(OrganizerSettingsDto.SectionName, cancellationToken),
        await settings.GetSectionAsync<AutoExportSettingsDto>(AutoExportSettingsDto.SectionName, cancellationToken),
        await settings.GetSectionAsync<WeatherAlertsSettingsDto>(WeatherAlertOptions.SectionName, cancellationToken),
        await settings.GetSectionAsync<AlarmSettingsDto>(AlarmSettingsDto.SectionName, cancellationToken),
        await settings.GetSectionAsync<FamilyFilesSettingsDto>(FamilyFilesSettingsDto.SectionName, cancellationToken),
        await settings.GetSectionAsync<MediaSettingsDto>(MediaSettingsDto.SectionName, cancellationToken),
        await settings.GetSectionAsync<MediaDownloadsSettingsDto>(MediaDownloadsSettingsDto.SectionName, cancellationToken),
        // Music.
        await settings.GetSectionAsync<MusicSettingsDto>(MusicSettingsDto.SectionName, cancellationToken));

    // Every schema section, guaranteed non-null - what a settings *form* needs, as opposed to
    // what a backup needs.
    //
    // A section absent from appsettings.json reads back as null, and a client rendering the
    // schema then draws its fields empty: a blank "Cooldown (min)" box on an install that has
    // never opened LOGIN ALERTS, even though the server is using 15. Blank is not just cosmetic
    // on a number field - every client refuses to save one ("Cooldown (min) must be a number"),
    // so the section cannot be turned on at all without the user inventing a value the form
    // never showed them. Substituting a default-constructed DTO shows the values the server is
    // actually running, which is what the form claims to be displaying.
    //
    // Six sections already default themselves inline in BuildSnapshotAsync for exactly this
    // reason. Those are deliberately left alone rather than folded in here: because they sit in
    // BuildSnapshotAsync they also reach the backup, and moving them would quietly change what a
    // restore writes. This covers the remaining sections, and any section added later, on the
    // read path only.
    //
    // Deliberately NOT applied to the backup export, which keeps reading BuildSnapshotAsync
    // directly. There, null carries meaning restore depends on - "the backup does not mention
    // this section, so leave the target's own value alone" (see ApplySnapshotAsync). Filling
    // defaults in would turn every unconfigured section into an explicit instruction to write
    // that default, freezing today's defaults into a config restored years from now, and for
    // Server it would restore an empty Urls over whatever the machine was bound to.
    //
    // INTERNET EXPOSED MODE is absent below because it is the one section that keeps its values
    // outside this snapshot (SettingsSectionSchema.DataEndpoint) and fetches them separately.
    private static SettingsSnapshot WithDefaultsForMissingSections(SettingsSnapshot snapshot) => snapshot with
    {
        Bridge = snapshot.Bridge ?? new(),
        Ollama = snapshot.Ollama ?? new(),
        CloudVision = snapshot.CloudVision ?? new(),
        VisionRouting = snapshot.VisionRouting ?? new(),
        FastLocal = snapshot.FastLocal ?? new(),
        ImageProcessing = snapshot.ImageProcessing ?? new(),
        Email = snapshot.Email ?? new(),
        DailySummary = snapshot.DailySummary ?? new(),
        Telegram = snapshot.Telegram ?? new(),
        Discord = snapshot.Discord ?? new(),
        TelegramChat = snapshot.TelegramChat ?? new(),
        BlueIris = snapshot.BlueIris ?? new(),
        MultiFrameAnalysis = snapshot.MultiFrameAnalysis ?? new(),
        TriggerRewind = snapshot.TriggerRewind ?? new(),
        ScheduledRecording = snapshot.ScheduledRecording ?? new(),
        Organizer = snapshot.Organizer ?? new(),
        AutoExport = snapshot.AutoExport ?? new(),
        WeatherAlerts = snapshot.WeatherAlerts ?? new(),
        Alarm = snapshot.Alarm ?? new(),
        FamilyFiles = snapshot.FamilyFiles ?? new(),
        Media = snapshot.Media ?? new(),
        MediaDownloads = snapshot.MediaDownloads ?? new(),
        Music = snapshot.Music ?? new(),
        PackageTheftWatch = snapshot.PackageTheftWatch ?? new(),
        RepeatAlerts = snapshot.RepeatAlerts ?? new(),
        SemanticSearch = snapshot.SemanticSearch ?? new(),
        SelfMonitoring = snapshot.SelfMonitoring ?? new(),
        StartupNotification = snapshot.StartupNotification ?? new(),
        InternetOutage = snapshot.InternetOutage ?? new(),
        Location = snapshot.Location ?? new(),
        Schedules = snapshot.Schedules ?? new(),
        QuietHours = snapshot.QuietHours ?? new(),
        Webhook = snapshot.Webhook ?? new(),
        Mqtt = snapshot.Mqtt ?? new(),
        DeviceHub = snapshot.DeviceHub ?? new(),
        PushNotifications = snapshot.PushNotifications ?? new(),
        Logging = snapshot.Logging ?? new(),
        Server = snapshot.Server ?? new(),
        BugReports = snapshot.BugReports ?? new(),
        LoginAlerts = snapshot.LoginAlerts ?? new(),
        NuisanceAdvisor = snapshot.NuisanceAdvisor ?? new(),
        TriggerApi = snapshot.TriggerApi ?? new(),
        TriggerConfirmation = snapshot.TriggerConfirmation ?? new(),
        HostReboot = snapshot.HostReboot ?? new()
    };

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
        if (snapshot.TriggerRewind is not null) await settings.MergeSectionAsync("TriggerRewind", snapshot.TriggerRewind, cancellationToken);
        if (snapshot.PackageTheftWatch is not null) await settings.MergeSectionAsync("PackageTheftWatch", snapshot.PackageTheftWatch, cancellationToken);
        if (snapshot.SemanticSearch is not null) await settings.MergeSectionAsync("SemanticSearch", snapshot.SemanticSearch, cancellationToken);
        if (snapshot.RepeatAlerts is not null) await settings.MergeSectionAsync("RepeatAlerts", snapshot.RepeatAlerts, cancellationToken);
        if (snapshot.SelfMonitoring is not null) await settings.MergeSectionAsync("SelfMonitoring", snapshot.SelfMonitoring, cancellationToken);
        if (snapshot.StartupNotification is not null) await settings.MergeSectionAsync("StartupNotification", snapshot.StartupNotification, cancellationToken);
        if (snapshot.InternetOutage is not null) await settings.MergeSectionAsync("InternetOutage", snapshot.InternetOutage, cancellationToken);
        if (snapshot.QuietHours is not null) await settings.MergeSectionAsync("QuietHours", snapshot.QuietHours, cancellationToken);
        if (snapshot.Location is not null) await settings.MergeSectionAsync("Location", snapshot.Location, cancellationToken);
        if (snapshot.Schedules is not null) await settings.MergeSectionAsync("Schedules", snapshot.Schedules, cancellationToken);
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
        if (snapshot.TriggerConfirmation is not null) await settings.MergeSectionAsync("TriggerConfirmation", snapshot.TriggerConfirmation, cancellationToken);
        if (snapshot.PushNotifications is not null) await settings.MergeSectionAsync("PushNotifications", snapshot.PushNotifications, cancellationToken);
        if (snapshot.Logging is not null) await WriteLoggingAsync(snapshot.Logging, settings, cancellationToken);
        if (snapshot.Server is not null) await settings.ReplaceSectionAsync("Urls", snapshot.Server.Urls, cancellationToken);
        if (snapshot.HostReboot is not null) await settings.MergeSectionAsync("HostReboot", snapshot.HostReboot, cancellationToken);
        if (snapshot.NuisanceAdvisor is not null) await settings.MergeSectionAsync("NuisanceAdvisor", snapshot.NuisanceAdvisor, cancellationToken);
        if (snapshot.ScheduledRecording is not null) await settings.MergeSectionAsync(ScheduledRecordingOptions.SectionName, snapshot.ScheduledRecording, cancellationToken);
        if (snapshot.Organizer is not null) await settings.MergeSectionAsync(OrganizerSettingsDto.SectionName, snapshot.Organizer, cancellationToken);
        if (snapshot.AutoExport is not null) await MergeUnmaskedAsync(snapshot.AutoExport, AutoExportSettingsDto.SectionName, settings, cancellationToken);
        if (snapshot.WeatherAlerts is not null) await settings.MergeSectionAsync(WeatherAlertOptions.SectionName, snapshot.WeatherAlerts, cancellationToken);
        if (snapshot.Alarm is not null) await settings.MergeSectionAsync(AlarmSettingsDto.SectionName, snapshot.Alarm, cancellationToken);
        if (snapshot.FamilyFiles is not null) await settings.MergeSectionAsync(FamilyFilesSettingsDto.SectionName, snapshot.FamilyFiles, cancellationToken);
        if (snapshot.Media is not null) await MergeUnmaskedAsync(snapshot.Media, MediaSettingsDto.SectionName, settings, cancellationToken);
        if (snapshot.MediaDownloads is not null) await MergeUnmaskedAsync(snapshot.MediaDownloads, MediaDownloadsSettingsDto.SectionName, settings, cancellationToken);
        // Music.
        if (snapshot.Music is not null) await settings.MergeSectionAsync(MusicSettingsDto.SectionName, snapshot.Music, cancellationToken);
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

    // Every section of a snapshot, judged by the same DataAnnotations its own PUT applies.
    //
    // Config restore writes each section straight through MergeSectionAsync, so until this existed
    // it was the one way to get a value into appsettings.json that the settings endpoints would
    // have refused. RetentionDays is the one that bites hardest: [Range(1, 3650)] is validated on
    // start as well as on save, so a zero arriving from a restore does not misbehave at runtime -
    // it stops the main app booting at all, which is discovered on the next restart, by which time
    // the restore looks long finished.
    //
    // Deliberately the same shallow check SaveSectionAsync performs rather than a deeper one, so
    // that what restore accepts and what a save accepts cannot come apart.
    public static List<string> ValidateSnapshot(SettingsSnapshot snapshot)
    {
        var errors = new List<string>();

        foreach (var property in typeof(SettingsSnapshot).GetProperties())
        {
            // The DTO sections only. PromptProfiles, KnownVehicles and KnownFaces are collections
            // with their own shapes and no DataAnnotations to apply.
            if (!property.PropertyType.Name.EndsWith("SettingsDto", StringComparison.Ordinal)) continue;
            if (property.GetValue(snapshot) is not { } section) continue;

            foreach (var problem in ValidateDataAnnotations(section))
                errors.Add($"{property.Name}: {problem}");
        }

        return errors;
    }

    private static List<string> ValidateDataAnnotations<T>(T instance) where T : notnull
    {
        var context = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, context, results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid value.").ToList();
    }
}
