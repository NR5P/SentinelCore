using System.ComponentModel.DataAnnotations;

namespace BlueIrisOllamaBridge;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    [Required, MinLength(16)]
    public string ApiKey { get; init; } = string.Empty;

    [Range(1024, 100 * 1024 * 1024)]
    public long MaximumImageBytes { get; init; } = 15 * 1024 * 1024;

    [Range(1, 10_000)]
    public int QueueCapacity { get; init; } = 100;

    [Required]
    public string DataDirectory { get; init; } = "data";

    public bool SaveImages { get; init; } = true;

    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 30;

    public HashSet<string> AllowedImageContentTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };
}

public sealed class ImageProcessingOptions
{
    public const string SectionName = "ImageProcessing";

    public bool Enabled { get; init; } = true;

    [Range(256, 4096)]
    public int MaxWidth { get; init; } = 768;

    [Range(256, 4096)]
    public int MaxHeight { get; init; } = 768;

    [Range(1, 100)]
    public int JpegQuality { get; init; } = 80;

    public bool AutoOrient { get; init; } = true;
}

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    [Required, Url]
    public string BaseUrl { get; init; } = "http://127.0.0.1:11434";

    [Required]
    public string Model { get; init; } = "gemma3:4b";

    [Range(5, 1800)]
    public int RequestTimeoutSeconds { get; init; } = 300;

    public string KeepAlive { get; init; } = "1h";

    [Range(0, 2)]
    public double Temperature { get; init; } = 0;

    [Range(32, 4096)]
    public int MaximumOutputTokens { get; init; } = 512;

    [Range(1, 100)]
    public int TopK { get; init; } = 20;

    [Range(0, 1)]
    public double TopP { get; init; } = 0.9;

    [Range(0, 10)]
    public int MaximumRetries { get; init; } = 2;

    [Range(1, 300)]
    public int RetryDelaySeconds { get; init; } = 3;
}

public sealed class CloudVisionOptions
{
    public const string SectionName = "CloudVision";

    public bool Enabled { get; init; }

    // Which cloud backend these settings apply to. "openai" is also the slot to use for any
    // other OpenAI-compatible endpoint (Azure OpenAI, OpenRouter, Together.ai, Groq, a local
    // vLLM/LM Studio/LocalAI server, etc.) - just point BaseUrl and Model at it. Anthropic and
    // Google use their own fixed API endpoints, since neither has a comparable "everyone speaks
    // this dialect" ecosystem the way OpenAI's Chat Completions format does.
    [Required, RegularExpression("^(openai|anthropic|google)$")]
    public string Provider { get; init; } = "openai";

    // Sent as this provider's API key, read fresh on every request (not baked into the
    // HttpClient at startup) so a key changed in settings takes effect without a restart.
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string Model { get; init; } = "gpt-4o-mini";

    // Only consulted when Provider is "openai" - Anthropic and Google always use their own
    // fixed endpoint. Overridable so an Azure OpenAI deployment or any other OpenAI-compatible
    // endpoint can stand in without a code change.
    [Required, Url]
    public string BaseUrl { get; init; } = "https://api.openai.com/v1/";

    [Range(5, 300)]
    public int TimeoutSeconds { get; init; } = 60;

    [Range(0, 10)]
    public int MaximumRetries { get; init; } = 2;

    [Range(1, 300)]
    public int RetryDelaySeconds { get; init; } = 3;
}

public sealed class VisionRoutingOptions
{
    public const string SectionName = "VisionRouting";

    // "ollama" or "cloud". Defaults to "ollama" so every existing install keeps behaving exactly
    // as it always has with zero config changes - this is the only backend that has ever existed
    // until now.
    [Required]
    public string Primary { get; init; } = "ollama";

    // If the primary backend's own retries are exhausted, automatically retry once against
    // whichever backend isn't primary before giving up on the alert entirely - e.g. cloud
    // unreachable -> fall back to local Ollama, slower but still analyzed.
    public bool FallbackEnabled { get; init; }
}

public sealed class PromptProfile
{
    // Friendly name shown in the UI. The dictionary key stays the stable identifier that
    // other config (AlertNotificationPolicy's gate logic, a camera's saved AlertProfile,
    // Blue Iris's own alert-action arguments) matches against, so renaming a profile here
    // never breaks anything that already points at its key.
    public string? DisplayName { get; init; }

    [Required]
    public string SystemPrompt { get; init; } = "Analyze this security-camera image accurately.";

    [MinLength(1)]
    public List<string> Questions { get; init; } = [];

    // Optional region of interest, applied before resizing, expressed as fractions (0-1)
    // of the auto-oriented source image so it stays correct regardless of camera resolution.
    public CropRegion? Crop { get; init; }

    // Off by default. When on, the AI is additionally asked to transcribe a vehicle's license
    // plate if clearly legible, and the result is matched against the configured KnownVehicles
    // list. Every profile this ships with defaults to off - camera resolution and angle matter
    // a lot for this to work at all, so it's opt-in per camera/profile rather than global.
    public bool LicensePlateRecognitionEnabled { get; init; }
}

public sealed class KnownVehicle
{
    public string Plate { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public sealed class CropRegion
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; } = 1;
    public double Bottom { get; init; } = 1;
}

public sealed class BugReportOptions
{
    public const string SectionName = "BugReports";

    // Off by default - nothing here is sent anywhere until explicitly turned on. The recipient
    // is fixed (see IBugReportSender.Recipient) and isn't exposed as a setting; sending reuses
    // whatever SMTP account is already configured under Email above.
    public bool Enabled { get; init; }
}

public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public bool Enabled { get; init; }

    // If true, this process hosts its own MQTT broker instead of requiring an external one
    // (e.g. Mosquitto/Home Assistant's own broker add-on). Either way, this app's own client
    // still connects out to a broker to publish - when this is true it connects to its own
    // embedded one on 127.0.0.1; when false it connects to Host/Port below.
    // Like Bridge:DataDirectory and other options that bind a socket at startup, this - and
    // EmbeddedBrokerPort - need a restart to take effect if changed.
    public bool RunEmbeddedBroker { get; init; }

    [Range(1, 65535)]
    public int EmbeddedBrokerPort { get; init; } = 1883;

    // Only used when RunEmbeddedBroker is false.
    public string BrokerHost { get; init; } = "127.0.0.1";

    [Range(1, 65535)]
    public int BrokerPort { get; init; } = 1883;

    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public bool UseTls { get; init; }

    // Root of every topic this app publishes, e.g. "sentinelcore/gateCamera/event/state".
    [Required]
    public string TopicPrefix { get; init; } = "sentinelcore";

    // Publishes Home Assistant MQTT discovery config so cameras show up automatically as
    // devices/entities, with no manual entity configuration needed on the Home Assistant side.
    public bool HomeAssistantDiscovery { get; init; } = true;

    [Required]
    public string HomeAssistantDiscoveryPrefix { get; init; } = "homeassistant";

    // Off by default (unlike the Webhook action) - most people wiring this up want every event
    // in Home Assistant to build their own history/automations from, not just alert-worthy ones.
    public bool OnlyWhenNotificationRecommended { get; init; }
}

public sealed class WebhookOptions
{
    public const string SectionName = "Webhook";
    public bool Enabled { get; init; }
    public string Url { get; init; } = string.Empty;
    public string ApiKeyHeaderName { get; init; } = "X-Api-Key";
    public string ApiKey { get; init; } = string.Empty;
    public bool OnlyWhenNotificationRecommended { get; init; } = true;
    [Range(1, 300)] public int TimeoutSeconds { get; init; } = 20;
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; init; }

    [Required]
    public string SmtpHost { get; init; } = "smtp.gmail.com";

    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    public bool EnableSsl { get; init; } = true;

    public string Username { get; init; } = string.Empty;
    public string AppPassword { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = "Gate Camera";
    public string ToAddress { get; init; } = string.Empty;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; init; } = 10;

    [Range(0, 1)]
    public double MinimumConfidence { get; init; } = 0.60;

    public bool AttachImage { get; init; } = true;
}

public sealed class QuietHoursOptions
{
    public const string SectionName = "QuietHours";

    public bool Enabled { get; init; }

    // StartHour == EndHour is treated as a zero-width window (never active) rather than a
    // full day, so an untouched default of 0/0 can't accidentally raise the bar around the clock.
    [Range(0, 23)]
    public int StartHour { get; init; } = 22;

    [Range(0, 23)]
    public int EndHour { get; init; } = 6;

    // Applied as a floor, not a replacement: a channel's own MinimumConfidence still wins if
    // it's already higher than this, so quiet hours can only ever raise the bar, never lower it.
    [Range(0, 1)]
    public double MinimumConfidence { get; init; } = 0.85;
}

public sealed class BlueIrisOptions
{
    public const string SectionName = "BlueIris";

    public bool Enabled { get; init; }

    [Required]
    public string BaseUrl { get; init; } = string.Empty;

    [Required]
    public string Camera { get; init; } = string.Empty;

    // Blue Iris's direct image endpoint accepts these as query parameters. Leave both blank
    // if the web server doesn't require login on your LAN.
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 15;
}

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; init; }

    public string BotToken { get; init; } = string.Empty;
    public string ChatId { get; init; } = string.Empty;

    public bool OnlyWhenNotificationRecommended { get; init; } = true;

    [Range(0, 1)]
    public double MinimumConfidence { get; init; } = 0.60;

    public bool AttachImage { get; init; } = true;

    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 20;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; init; } = 10;
}

public sealed class TelegramChatOptions
{
    public const string SectionName = "TelegramChat";

    // Uses Telegram:BotToken and Telegram:ChatId for identity/authorization -
    // only messages from that chat ID are ever answered.
    public bool Enabled { get; init; } = true;

    [Range(1, 30)]
    public int LookbackDays { get; init; } = 3;

    [Range(1, 500)]
    public int MaxEventsInContext { get; init; } = 100;

    [Required]
    public string Model { get; init; } = "gemma3:4b";

    [Range(1, 60)]
    public int PollTimeoutSeconds { get; init; } = 25;

    [Range(5, 600)]
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed class DailySummaryOptions
{
    public const string SectionName = "DailySummary";

    public bool Enabled { get; init; } = true;

    // Local-time hour (0-23) each digest is sent. The evening digest covers
    // [MorningHour, now) and the morning digest covers [previous day's EveningHour, now),
    // so the two digests partition a full day with no overlap.
    [Range(0, 23)]
    public int MorningHour { get; init; } = 7;

    [Range(0, 23)]
    public int EveningHour { get; init; } = 20;

    [Range(0, 50)]
    public int MaxImagesAttached { get; init; } = 8;
}

public sealed class SelfMonitoringOptions
{
    public const string SectionName = "SelfMonitoring";

    // Uses Telegram:BotToken and Telegram:ChatId to send its alerts.
    public bool Enabled { get; init; } = true;

    [Range(10, 3600)]
    public int CheckIntervalSeconds { get; init; } = 60;

    // How many consecutive unhealthy checks before alerting, to avoid paging on a momentary blip.
    [Range(1, 20)]
    public int ConsecutiveFailuresBeforeAlert { get; init; } = 3;

    [Range(1, 10_000)]
    public int QueueBacklogThreshold { get; init; } = 5;

    [Range(1, 20)]
    public int ConsecutiveBacklogChecksBeforeAlert { get; init; } = 3;

    public bool NotifyOnRecovery { get; init; } = true;
}