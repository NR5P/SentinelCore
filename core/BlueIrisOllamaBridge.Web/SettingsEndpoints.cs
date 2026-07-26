using System.ComponentModel.DataAnnotations;
using BlueIrisOllamaBridge;

namespace BlueIrisOllamaBridge.Web;

// Every user-editable settings section, in one place - shared by GET /api/settings and the
// config backup/restore feature (BackupEndpoints), so both read the exact same shape and
// there's no risk of the two drifting apart as sections get added later.
public sealed record SettingsSnapshot(
    BridgeSettingsDto? Bridge,
    OllamaSettingsDto? Ollama,
    CloudVisionSettingsDto? CloudVision,
    VisionRoutingSettingsDto? VisionRouting,
    ImageProcessingSettingsDto? ImageProcessing,
    Dictionary<string, PromptProfile>? PromptProfiles,
    EmailSettingsDto? Email,
    DailySummarySettingsDto? DailySummary,
    TelegramSettingsDto? Telegram,
    TelegramChatSettingsDto? TelegramChat,
    BlueIrisSettingsDto? BlueIris,
    SelfMonitoringSettingsDto? SelfMonitoring,
    QuietHoursSettingsDto? QuietHours,
    List<KnownVehicle>? KnownVehicles,
    MqttSettingsDto? Mqtt,
    HomeKitSettingsDto? HomeKit,
    BugReportSettingsDto? BugReports,
    WebhookSettingsDto? Webhook);

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", async (AppSettingsFile settings, CancellationToken ct) =>
            Results.Ok(await BuildSnapshotAsync(settings, ct)));

        app.MapPut("/api/settings/bridge", async (BridgeSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Bridge", ct, extra =>
            {
                if (dto.ApiKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
                    extra.Add("ApiKey must be changed from the default.");
            }));

        app.MapPut("/api/settings/ollama", async (OllamaSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Ollama", ct));

        app.MapPut("/api/settings/cloud-vision", async (CloudVisionSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "CloudVision", ct, extra =>
            {
                if (dto.Enabled && string.IsNullOrWhiteSpace(dto.ApiKey))
                    extra.Add("ApiKey is required when cloud vision is enabled.");
                if (dto.Provider is not ("openai" or "anthropic" or "google"))
                    extra.Add("Provider must be 'openai', 'anthropic', or 'google'.");
            }));

        app.MapPut("/api/settings/vision-routing", async (VisionRoutingSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "VisionRouting", ct, extra =>
            {
                if (dto.Primary is not ("ollama" or "cloud"))
                    extra.Add("Primary must be 'ollama' or 'cloud'.");
            }));

        app.MapPut("/api/settings/image-processing", async (ImageProcessingSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "ImageProcessing", ct));

        app.MapPut("/api/settings/email", async (EmailSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Email", ct, extra =>
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

        app.MapPut("/api/settings/daily-summary", async (DailySummarySettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "DailySummary", ct, extra =>
            {
                if (dto.MorningHour == dto.EveningHour)
                    extra.Add("MorningHour and EveningHour must differ.");
            }));

        app.MapPut("/api/settings/telegram", async (TelegramSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Telegram", ct, extra =>
            {
                if (!dto.Enabled) return;
                if (string.IsNullOrWhiteSpace(dto.BotToken)) extra.Add("BotToken is required when Telegram is enabled.");
                if (string.IsNullOrWhiteSpace(dto.ChatId)) extra.Add("ChatId is required when Telegram is enabled.");
            }));

        app.MapPut("/api/settings/telegram-chat", async (TelegramChatSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "TelegramChat", ct));

        app.MapPut("/api/settings/blue-iris", async (BlueIrisSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "BlueIris", ct, extra =>
            {
                if (dto.Enabled && string.IsNullOrWhiteSpace(dto.Camera))
                    extra.Add("Camera is required when Blue Iris is enabled.");
            }));

        app.MapPut("/api/settings/self-monitoring", async (SelfMonitoringSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "SelfMonitoring", ct));

        app.MapPut("/api/settings/quiet-hours", async (QuietHoursSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "QuietHours", ct));

        app.MapPut("/api/settings/mqtt", async (MqttSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Mqtt", ct, extra =>
            {
                if (!dto.Enabled) return;
                if (!dto.RunEmbeddedBroker && string.IsNullOrWhiteSpace(dto.BrokerHost))
                    extra.Add("Broker Host is required when not running the embedded broker.");
            }));

        app.MapPut("/api/settings/homekit", async (HomeKitSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "HomeKit", ct, extra =>
            {
                if (dto.Enabled && !Uri.TryCreate(dto.SidecarBaseUrl, UriKind.Absolute, out _))
                    extra.Add("Sidecar Base Url must be a valid absolute URL when HomeKit is enabled.");
            }));

        app.MapPut("/api/settings/bug-reports", async (BugReportSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "BugReports", ct));

        app.MapPut("/api/settings/webhook", async (WebhookSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Webhook", ct, extra =>
            {
                if (dto.Enabled && !Uri.TryCreate(dto.Url, UriKind.Absolute, out _))
                    extra.Add("Url must be a valid absolute URL when the webhook is enabled.");
            }));

        app.MapPut("/api/settings/known-vehicles", async (List<KnownVehicle> vehicles, AppSettingsFile settings, CancellationToken ct) =>
        {
            var errors = new List<string>();
            foreach (var vehicle in vehicles)
            {
                if (string.IsNullOrWhiteSpace(vehicle.Plate))
                    errors.Add("Every known vehicle needs a plate.");
            }

            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await settings.ReplaceSectionAsync("KnownVehicles", vehicles, ct);
            return Results.Ok(new { saved = true });
        });

        app.MapPut("/api/settings/prompt-profiles", async (Dictionary<string, PromptProfile> profiles, AppSettingsFile settings, CancellationToken ct) =>
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

            await settings.ReplaceSectionAsync("PromptProfiles", profiles, ct);
            return Results.Ok(new { saved = true });
        });
    }

    public static async Task<SettingsSnapshot> BuildSnapshotAsync(AppSettingsFile settings, CancellationToken cancellationToken) => new(
        await settings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken),
        await settings.GetSectionAsync<OllamaSettingsDto>("Ollama", cancellationToken),
        await settings.GetSectionAsync<CloudVisionSettingsDto>("CloudVision", cancellationToken),
        await settings.GetSectionAsync<VisionRoutingSettingsDto>("VisionRouting", cancellationToken),
        await settings.GetSectionAsync<ImageProcessingSettingsDto>("ImageProcessing", cancellationToken),
        await settings.GetSectionAsync<Dictionary<string, PromptProfile>>("PromptProfiles", cancellationToken),
        await settings.GetSectionAsync<EmailSettingsDto>("Email", cancellationToken),
        await settings.GetSectionAsync<DailySummarySettingsDto>("DailySummary", cancellationToken),
        await settings.GetSectionAsync<TelegramSettingsDto>("Telegram", cancellationToken),
        await settings.GetSectionAsync<TelegramChatSettingsDto>("TelegramChat", cancellationToken),
        await settings.GetSectionAsync<BlueIrisSettingsDto>("BlueIris", cancellationToken),
        await settings.GetSectionAsync<SelfMonitoringSettingsDto>("SelfMonitoring", cancellationToken),
        await settings.GetSectionAsync<QuietHoursSettingsDto>("QuietHours", cancellationToken),
        await settings.GetSectionAsync<List<KnownVehicle>>("KnownVehicles", cancellationToken) ?? [],
        await settings.GetSectionAsync<MqttSettingsDto>("Mqtt", cancellationToken),
        await settings.GetSectionAsync<HomeKitSettingsDto>("HomeKit", cancellationToken),
        await settings.GetSectionAsync<BugReportSettingsDto>("BugReports", cancellationToken),
        await settings.GetSectionAsync<WebhookSettingsDto>("Webhook", cancellationToken));

    // Applied by config restore - merges each present section exactly the way its own PUT
    // endpoint above already does (Merge for most sections, Replace for the two wholly user-
    // owned lists), so restoring behaves identically to re-saving each section by hand. A
    // section absent from the snapshot (null) is left completely untouched - restore overlays
    // onto the current config, it never wipes something the backup doesn't mention.
    public static async Task ApplySnapshotAsync(SettingsSnapshot snapshot, AppSettingsFile settings, CancellationToken cancellationToken)
    {
        if (snapshot.Bridge is not null) await settings.MergeSectionAsync("Bridge", snapshot.Bridge, cancellationToken);
        if (snapshot.Ollama is not null) await settings.MergeSectionAsync("Ollama", snapshot.Ollama, cancellationToken);
        if (snapshot.CloudVision is not null) await settings.MergeSectionAsync("CloudVision", snapshot.CloudVision, cancellationToken);
        if (snapshot.VisionRouting is not null) await settings.MergeSectionAsync("VisionRouting", snapshot.VisionRouting, cancellationToken);
        if (snapshot.ImageProcessing is not null) await settings.MergeSectionAsync("ImageProcessing", snapshot.ImageProcessing, cancellationToken);
        if (snapshot.PromptProfiles is not null) await settings.ReplaceSectionAsync("PromptProfiles", snapshot.PromptProfiles, cancellationToken);
        if (snapshot.Email is not null) await settings.MergeSectionAsync("Email", snapshot.Email, cancellationToken);
        if (snapshot.DailySummary is not null) await settings.MergeSectionAsync("DailySummary", snapshot.DailySummary, cancellationToken);
        if (snapshot.Telegram is not null) await settings.MergeSectionAsync("Telegram", snapshot.Telegram, cancellationToken);
        if (snapshot.TelegramChat is not null) await settings.MergeSectionAsync("TelegramChat", snapshot.TelegramChat, cancellationToken);
        if (snapshot.BlueIris is not null) await settings.MergeSectionAsync("BlueIris", snapshot.BlueIris, cancellationToken);
        if (snapshot.SelfMonitoring is not null) await settings.MergeSectionAsync("SelfMonitoring", snapshot.SelfMonitoring, cancellationToken);
        if (snapshot.QuietHours is not null) await settings.MergeSectionAsync("QuietHours", snapshot.QuietHours, cancellationToken);
        if (snapshot.KnownVehicles is not null) await settings.ReplaceSectionAsync("KnownVehicles", snapshot.KnownVehicles, cancellationToken);
        if (snapshot.Mqtt is not null) await settings.MergeSectionAsync("Mqtt", snapshot.Mqtt, cancellationToken);
        if (snapshot.HomeKit is not null) await settings.MergeSectionAsync("HomeKit", snapshot.HomeKit, cancellationToken);
        if (snapshot.BugReports is not null) await settings.MergeSectionAsync("BugReports", snapshot.BugReports, cancellationToken);
        if (snapshot.Webhook is not null) await settings.MergeSectionAsync("Webhook", snapshot.Webhook, cancellationToken);
    }

    private static async Task<IResult> SaveSectionAsync<T>(
        T dto,
        AppSettingsFile settings,
        string sectionName,
        CancellationToken cancellationToken,
        Action<List<string>>? extraValidation = null)
        where T : notnull
    {
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
