using System.ComponentModel.DataAnnotations;

namespace SentinelCore.Web;

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

    // Separate from RetentionDays and much shorter, because it answers a different question:
    // rejected motion is kept to tune a zone against, not as history. Zero keeps it forever, which
    // is how the system behaved before this existed - see MotionRetentionWorker.
    [Range(0, 365)]
    public int UnverifiedMotionRetentionDays { get; set; }

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

    public bool EscalationEnabled { get; set; }

    [Required]
    public string EscalationTarget { get; set; } = "auto";

    public bool EscalationVerdictWins { get; set; }

    [Range(0, 1)]
    public double MinimumConfidence { get; set; }
}

// ModelPath/DownloadUrl/InputSize are deliberately absent: the ONNX session loads once per
// process, so those only apply after a restart - appsettings.json remains the place for them,
// matching this file's rule of only exposing fields that take effect live.
public sealed class FastLocalSettingsDto
{
    [Range(0, 1)]
    public double ScoreThreshold { get; set; } = 0.5;

    public bool NotifyOnPerson { get; set; } = true;
    public bool NotifyOnVehicle { get; set; } = true;
    public bool NotifyOnAnimal { get; set; }
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

public sealed class DiscordSettingsDto
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public bool OnlyWhenNotificationRecommended { get; set; } = true;

    [Range(0, 1)]
    public double MinimumConfidence { get; set; } = 0.60;

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
    public string BaseUrl { get; set; } = string.Empty;
    public string Camera { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class MultiFrameAnalysisSettingsDto
{
    public bool Enabled { get; set; }

    [Range(1, 2)]
    public int ExtraFrameCount { get; set; } = 1;

    [Range(150, 1000)]
    public int FrameIntervalMs { get; set; } = 350;
}

public sealed class TriggerRewindSettingsDto
{
    public bool Enabled { get; set; }

    [Range(2, 15)]
    public int LookbackSeconds { get; set; } = 6;

    public bool BufferEveryCamera { get; set; } = true;

    public bool ApplyToCompanions { get; set; } = true;
}

public sealed class ScheduledRecordingSettingsDto
{
    [Range(1, 365)]
    public int RetentionDays { get; set; } = 14;

    [Range(1, 500)]
    public double MinimumFreeGigabytes { get; set; } = 5;
}

public sealed class PackageTheftWatchSettingsDto
{
    public bool Enabled { get; set; }

    [Range(1, 72)]
    public int WatchWindowHours { get; set; } = 12;
}

public sealed class SemanticSearchSettingsDto
{
    public bool Enabled { get; set; }

    [Range(0.0, 1.0)]
    public double MinimumSimilarity { get; set; } = 0.20;
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

public sealed class StartupNotificationSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 1440)]
    public int MinimumDowntimeMinutes { get; set; } = 2;

    [Range(10, 3600)]
    public int HeartbeatIntervalSeconds { get; set; } = 60;
}

public sealed class InternetOutageSettingsDto
{
    // Off unless the owner turns it on - LAN-only installs must not be told their network is
    // broken. See InternetOutageOptions for the rest of the reasoning behind these defaults.
    public bool Enabled { get; set; }

    [Range(10, 3600)]
    public int CheckIntervalSeconds { get; set; } = 60;

    [Range(1, 20)]
    public int ConsecutiveFailuresBeforeAlert { get; set; } = 3;

    [Range(1, 1440)]
    public int MinimumOutageMinutes { get; set; } = 2;

    [Range(1, 60)]
    public int ProbeTimeoutSeconds { get; set; } = 5;

    public List<string> ProbeUrls { get; set; } = [];
}

public sealed class LocationSettingsDto
{
    // Only ever used to work out sunrise and sunset here, which is what lets a schedule fire
    // "when it gets dark" instead of at a clock time that needs changing every few weeks. The
    // calculation is local arithmetic - nothing is sent anywhere. See LocationOptions.
    [Range(-90, 90)]
    public double Latitude { get; set; }

    [Range(-180, 180)]
    public double Longitude { get; set; }

    // Display only - what the coordinates were looked up from, so the form can show a place
    // name rather than two numbers nobody can sanity-check at a glance.
    public string Label { get; set; } = string.Empty;
}

public sealed class ScheduleSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 1440)]
    public int MaximumCatchUpMinutes { get; set; } = 60;

    public string TimeZone { get; set; } = string.Empty;
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

// Backing DTO for the Settings UI's "Internet Exposed Mode" toggle - persisted separately from
// the rest of SettingsSnapshot (see SettingsEndpoints.MapInternetExposedEndpoints) because it
// lives in this project's own appsettings.json (it configures this project's own Kestrel
// binding, same category as HttpsSettings), not the main app's shared settings file.
public sealed class InternetExposedSettingsDto
{
    public bool Enabled { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = InternetExposedSettings.DefaultPort;
}

public sealed class BugReportSettingsDto
{
    public bool Enabled { get; set; } = true;
}

public sealed class LoginAlertSettingsDto
{
    public bool Enabled { get; set; }

    [Range(1, 1440)]
    public int CooldownMinutes { get; set; } = 15;
}

// Mirrors NuisanceAdvisorOptions in the main app.
public sealed class NuisanceAdvisorSettingsDto
{
    public bool Enabled { get; set; } = true;

    [Range(10, 5000)]
    public int MinimumNightTriggers { get; set; } = 60;

    [Range(0, 100)]
    public double MaximumConfirmedPercent { get; set; } = 10;

    [Range(1, 50)]
    public double MinimumNightDayRatio { get; set; } = 3;

    [Range(1, 90)]
    public int CooldownDays { get; set; } = 3;
}

public sealed class TriggerApiSettingsDto
{
    public bool Enabled { get; set; }

    // Settable so it can be rotated (or pasted back from a backup), and auto-filled on first
    // enable by the /trigger-api endpoint rather than being invented by the owner.
    public string ApiKey { get; set; } = string.Empty;

    [Range(0, 3600)]
    public int CooldownSeconds { get; set; } = 15;
}

// See TriggerConfirmationService. Enabled defaults true both here and when the section is absent
// entirely, so an install upgrading into this feature gets the filtering without touching settings.
public sealed class TriggerConfirmationSettingsDto
{
    public bool Enabled { get; set; } = true;

    // How long to wait for the analysis before giving up and showing the trigger anyway. Ollama on
    // a CPU-only box can take minutes per frame, and the cost of waiting too long is a delayed
    // trigger card, while the cost of not waiting long enough is the filter quietly doing nothing.
    [Range(30, 3600)]
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class HomeKitSettingsDto
{
    public bool Enabled { get; set; }

    [Required]
    public string SidecarBaseUrl { get; set; } = "http://127.0.0.1:3321";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    [Range(0, 1)]
    public double MinimumConfidence { get; set; } = 0.6;
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

public sealed class DeviceHubSettingsDto
{
    // Strictly an add-on to MQTT - the settings endpoint refuses to turn this on while the Mqtt
    // section is off, since there'd be no broker connection to command anything through.
    public bool Enabled { get; set; }

    [Range(10, 86400)]
    public int StateTimeoutSeconds { get; set; } = 90;

    [Range(2, 60)]
    public int DiscoverySeconds { get; set; } = 10;

    [Range(1, 60)]
    public int CommandTimeoutSeconds { get; set; } = 5;
}

public sealed class WebhookSettingsDto
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    public string ApiKey { get; set; } = string.Empty;
    public bool OnlyWhenNotificationRecommended { get; set; } = true;
}

// Mirrors PushNotificationOptions in the main app, nested Apple/Firebase blocks included.
// MergeSectionAsync overlays whole top-level keys, so the sub-objects must carry every field
// the Options classes have - a missing one (TvBundleId, say) wouldn't just be uneditable, it
// would be silently wiped from the file on every save.
//
// The sub-types deliberately don't end in "SettingsDto": that suffix is what
// SettingsSchemaTests sweeps for when checking every DTO has its own schema section, and these
// two are halves of the PushNotifications section, not sections of their own.
public sealed class PushNotificationsSettingsDto
{
    public bool Enabled { get; set; }
    public bool OnlyWhenNotificationRecommended { get; set; } = true;

    [Range(0, 1)]
    public double MinimumConfidence { get; set; } = 0.60;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; set; } = 5;

    public ApplePushDto Apple { get; set; } = new();
    public FirebasePushDto Firebase { get; set; } = new();
}

public sealed class ApplePushDto
{
    public bool Enabled { get; set; }
    public string TeamId { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string BundleId { get; set; } = "com.sentinelcore.client";
    public string TvBundleId { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public bool UseSandbox { get; set; } = true;
}

public sealed class FirebasePushDto
{
    public bool Enabled { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ServiceAccountPath { get; set; } = string.Empty;
}

// Flat on purpose, even though the file shape is Logging.LogLevel.{Default,Microsoft.AspNetCore}:
// "Microsoft.AspNetCore" contains dots, so exposing the real nesting would make the flattened
// field paths the schema uses ambiguous. The endpoint maps between this and the real shape - see
// the /logging handler in SettingsEndpoints, which also preserves any extra LogLevel categories
// someone added to the file by hand.
public sealed class LoggingSettingsDto
{
    public string Default { get; set; } = "Information";
    public string MicrosoftAspNetCore { get; set; } = "Warning";
}

// "Urls" is a bare string at the root of appsettings.json, not a section - the one setting in
// the schema whose save handler writes a scalar. Restart-required: Kestrel binds it once at
// startup.
public sealed class ServerSettingsDto
{
    [Required]
    public string Urls { get; set; } = string.Empty;
}

// Two settings and no more, because the third thing this feature needs - permission from the
// operating system to reboot the machine - cannot be stored in a config file. Enabled here only
// says the buttons may appear; GET /api/system/host-reboot is what says whether they will work.
// See HostReboot.cs.
public sealed class HostRebootSettingsDto
{
    public bool Enabled { get; set; }

    [Range(0, 3600)]
    public int DelaySeconds { get; set; } = 60;
}
