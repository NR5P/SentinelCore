using System.ComponentModel.DataAnnotations;

namespace BlueIrisOllamaBridge.Web;

// Mirrors the editable subset of each Options class in the main app. Deliberately omits
// anything that requires a process restart to take effect (QueueCapacity, DataDirectory,
// Urls/ports, and the handful of *BaseUrl/*TimeoutSeconds fields baked into an HttpClient
// at startup) so every field this UI can touch applies live, no restart banner needed.

public sealed class BridgeSettingsDto
{
    [Required, MinLength(16)]
    public string ApiKey { get; set; } = string.Empty;

    [Range(1024, 100 * 1024 * 1024)]
    public long MaximumImageBytes { get; set; }

    public bool SaveImages { get; set; }

    [Range(1, 3650)]
    public int RetentionDays { get; set; }

    [MinLength(1)]
    public List<string> AllowedImageContentTypes { get; set; } = [];
}

public sealed class OllamaSettingsDto
{
    [Required]
    public string Model { get; set; } = string.Empty;

    public string KeepAlive { get; set; } = "1h";

    [Range(0, 2)]
    public double Temperature { get; set; }

    [Range(32, 4096)]
    public int MaximumOutputTokens { get; set; }

    [Range(1, 100)]
    public int TopK { get; set; }

    [Range(0, 1)]
    public double TopP { get; set; }

    [Range(0, 10)]
    public int MaximumRetries { get; set; }

    [Range(1, 300)]
    public int RetryDelaySeconds { get; set; }
}

public sealed class CloudVisionSettingsDto
{
    public bool Enabled { get; set; }

    [Required]
    public string Provider { get; set; } = "openai";

    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string Model { get; set; } = string.Empty;

    [Range(0, 10)]
    public int MaximumRetries { get; set; }

    [Range(1, 300)]
    public int RetryDelaySeconds { get; set; }
}

public sealed class VisionRoutingSettingsDto
{
    [Required]
    public string Primary { get; set; } = "ollama";

    public bool FallbackEnabled { get; set; }
}

public sealed class ImageProcessingSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(256, 4096)]
    public int MaxWidth { get; set; }

    [Range(256, 4096)]
    public int MaxHeight { get; set; }

    [Range(1, 100)]
    public int JpegQuality { get; set; }

    public bool AutoOrient { get; set; } = true;
}

public sealed class EmailSettingsDto
{
    public bool Enabled { get; set; }

    [Required]
    public string SmtpHost { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int SmtpPort { get; set; }

    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string AppPassword { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; set; }

    [Range(0, 1)]
    public double MinimumConfidence { get; set; }

    public bool AttachImage { get; set; } = true;
}

public sealed class DailySummarySettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(0, 23)]
    public int MorningHour { get; set; }

    [Range(0, 23)]
    public int EveningHour { get; set; }

    [Range(0, 50)]
    public int MaxImagesAttached { get; set; }
}

public sealed class TelegramSettingsDto
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool OnlyWhenNotificationRecommended { get; set; } = true;

    [Range(0, 1)]
    public double MinimumConfidence { get; set; }

    public bool AttachImage { get; set; } = true;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; set; } = 10;
}

public sealed class TelegramChatSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 30)]
    public int LookbackDays { get; set; }

    [Range(1, 500)]
    public int MaxEventsInContext { get; set; }

    [Required]
    public string Model { get; set; } = string.Empty;
}

public sealed class BlueIrisSettingsDto
{
    public bool Enabled { get; set; }
    public string Camera { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class SelfMonitoringSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(10, 3600)]
    public int CheckIntervalSeconds { get; set; }

    [Range(1, 20)]
    public int ConsecutiveFailuresBeforeAlert { get; set; }

    [Range(1, 10_000)]
    public int QueueBacklogThreshold { get; set; }

    [Range(1, 20)]
    public int ConsecutiveBacklogChecksBeforeAlert { get; set; }

    public bool NotifyOnRecovery { get; set; } = true;
}

public sealed class QuietHoursSettingsDto
{
    public bool Enabled { get; set; }

    [Range(0, 23)]
    public int StartHour { get; set; } = 22;

    [Range(0, 23)]
    public int EndHour { get; set; } = 6;

    [Range(0, 1)]
    public double MinimumConfidence { get; set; } = 0.85;
}

public sealed class BugReportSettingsDto
{
    public bool Enabled { get; set; }
}

public sealed class HomeKitSettingsDto
{
    public bool Enabled { get; set; }

    [Required]
    public string SidecarBaseUrl { get; set; } = "http://127.0.0.1:3321";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class MqttSettingsDto
{
    public bool Enabled { get; set; }
    public bool RunEmbeddedBroker { get; set; }

    [Range(1, 65535)]
    public int EmbeddedBrokerPort { get; set; } = 1883;

    public string BrokerHost { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int BrokerPort { get; set; } = 1883;

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseTls { get; set; }

    [Required]
    public string TopicPrefix { get; set; } = "sentinelcore";

    public bool HomeAssistantDiscovery { get; set; } = true;

    [Required]
    public string HomeAssistantDiscoveryPrefix { get; set; } = "homeassistant";

    public bool OnlyWhenNotificationRecommended { get; set; }
}

public sealed class WebhookSettingsDto
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    public string ApiKey { get; set; } = string.Empty;
    public bool OnlyWhenNotificationRecommended { get; set; } = true;
}
