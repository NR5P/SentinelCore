using System.ComponentModel.DataAnnotations;
using BlueIrisOllamaBridge;

namespace BlueIrisOllamaBridge.Web;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", async (AppSettingsFile settings, CancellationToken ct) => Results.Ok(new
        {
            bridge = await settings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct),
            ollama = await settings.GetSectionAsync<OllamaSettingsDto>("Ollama", ct),
            imageProcessing = await settings.GetSectionAsync<ImageProcessingSettingsDto>("ImageProcessing", ct),
            promptProfiles = await settings.GetSectionAsync<Dictionary<string, PromptProfile>>("PromptProfiles", ct),
            email = await settings.GetSectionAsync<EmailSettingsDto>("Email", ct),
            dailySummary = await settings.GetSectionAsync<DailySummarySettingsDto>("DailySummary", ct),
            telegram = await settings.GetSectionAsync<TelegramSettingsDto>("Telegram", ct),
            telegramChat = await settings.GetSectionAsync<TelegramChatSettingsDto>("TelegramChat", ct),
            blueIris = await settings.GetSectionAsync<BlueIrisSettingsDto>("BlueIris", ct),
            selfMonitoring = await settings.GetSectionAsync<SelfMonitoringSettingsDto>("SelfMonitoring", ct),
            quietHours = await settings.GetSectionAsync<QuietHoursSettingsDto>("QuietHours", ct),
            knownVehicles = await settings.GetSectionAsync<List<KnownVehicle>>("KnownVehicles", ct) ?? [],
            mqtt = await settings.GetSectionAsync<MqttSettingsDto>("Mqtt", ct),
            webhook = await settings.GetSectionAsync<WebhookSettingsDto>("Webhook", ct)
        }));

        app.MapPut("/api/settings/bridge", async (BridgeSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Bridge", ct, extra =>
            {
                if (dto.ApiKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
                    extra.Add("ApiKey must be changed from the default.");
            }));

        app.MapPut("/api/settings/ollama", async (OllamaSettingsDto dto, AppSettingsFile settings, CancellationToken ct) =>
            await SaveSectionAsync(dto, settings, "Ollama", ct));

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
