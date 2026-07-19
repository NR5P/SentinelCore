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

public sealed class PromptProfile
{
    [Required]
    public string SystemPrompt { get; init; } = "Analyze this security-camera image accurately.";

    [MinLength(1)]
    public List<string> Questions { get; init; } = [];
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