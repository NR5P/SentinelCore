using System.ComponentModel.DataAnnotations;
using SentinelCore;

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

    // Only consulted when Provider is "openai" - that slot is also every other endpoint speaking
    // the same dialect, and which one it is, is precisely this value. Editable here rather than
    // appsettings.json-only because switching providers is the ordinary path through this section,
    // not an edge case.
    // Unlike the other *BaseUrl fields the note at the top of this file excludes, this one is not
    // baked into an HttpClient at startup: OpenAiVisionClient resolves it per request, precisely
    // so it can live here and apply live like everything else.
    [Required, Url]
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";

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

    // One of the few TimeoutSeconds the note at the top of this file doesn't exclude: nothing
    // holds an SMTP connection open between sends, so it's read fresh on the next email.
    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 30;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; set; }

    [Range(0, 1)]
    public double MinimumConfidence { get; set; }

    public bool AttachImage { get; set; } = true;

    // Lets the saved form be judged by SmtpMailer.ValidateDeliverable, the same code that decides
    // whether the server will boot with this Email section - see SettingsEndpoints' /email PUT.
    public EmailOptions ToOptions() => new()
    {
        Enabled = Enabled,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        EnableSsl = EnableSsl,
        Username = Username,
        AppPassword = AppPassword,
        FromAddress = FromAddress,
        FromName = FromName,
        ToAddress = ToAddress,
        TimeoutSeconds = TimeoutSeconds
    };
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

    [Range(1, 240)]
    public int ManualMaxMinutes { get; set; } = 10;
}

public sealed class PackageTheftWatchSettingsDto
{
    public bool Enabled { get; set; }

    [Range(1, 72)]
    public int WatchWindowHours { get; set; } = 12;
}

public sealed class RepeatAlertsSettingsDto
{
    public bool SupersedeQueued { get; set; } = true;

    [Range(0, 3600)]
    public int DuplicateNotificationWindowSeconds { get; set; } = 90;
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

    [Range(0, 10_000)]
    public int FreeDiskWarningGigabytes { get; set; } = 10;

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
    // Used to work out sunrise and sunset here (local arithmetic, which is what lets a schedule
    // fire "when it gets dark" instead of at a clock time that needs changing every few weeks),
    // and by the Weather tab, which sends the pair to the National Weather Service for the
    // forecast while that tab is open. See LocationOptions.
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

// Mirrors WeatherAlertOptions in the main app. Off by default, and the kinds default to what
// can hurt you - see the options class for why.
public sealed class WeatherAlertsSettingsDto
{
    public bool Enabled { get; set; }

    [Range(1, 60)]
    public int CheckIntervalMinutes { get; set; } = 5;

    public string MinimumLevel { get; set; } = WeatherAlertOptions.LevelWatch;

    public bool Tornado { get; set; } = true;
    public bool SevereThunderstorm { get; set; } = true;
    public bool Flood { get; set; } = true;
    public bool Winter { get; set; } = true;
    public bool Wind { get; set; } = true;
    public bool Temperature { get; set; } = true;
    public bool Tropical { get; set; } = true;
    public bool Fire { get; set; }
    public bool Other { get; set; }
    public bool NotifyOnUpdates { get; set; }

    // A method, not a property: SettingsSchemaTests treats every property as a field a client
    // must be able to edit.
    public bool HasAnyKind() =>
        Tornado || SevereThunderstorm || Flood || Winter || Wind || Temperature || Tropical || Fire || Other;
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

    // Delete a rejected trigger's clip as soon as the verdict lands, instead of leaving it for
    // UnverifiedMotionRetentionDays to prune days later. The snapshot is deliberately NOT deleted
    // with it: the still is what tells you whether a zone is firing on a spider web, and it costs
    // a few tens of kilobytes against the clip's ~19 MB. On by default - a clip the AI found
    // nothing in is the single biggest consumer of disk here, and it is worth almost nothing.
    //
    // Nothing is deleted while this setting can't be read, and nothing is deleted for a clip a
    // confirmed (or still-pending) trigger shares - see TriggerConfirmationService.
    public bool DiscardRejectedClips { get; set; } = true;
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

    // Read by the HomeKit sidecar, not by this app - see core/homekit-bridge. Off by default
    // because it changes the shape of an already-paired bridge: turning it on adds an accessory
    // per Device Hub device to Apple Home, which is a pleasant surprise only if it was asked for.
    public bool IncludeDevices { get; set; }
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
    public RelayPushDto Relay { get; set; } = new();
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

// Mirrors RelayPushOptions. Defaults match the shipped appsettings.json so a save from a client
// that predates this block (or the round-trip test's minimal payload) writes the same thing the
// seeder would have.
public sealed class RelayPushDto
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "https://push.nr5p.com";
    public bool ApnsSandbox { get; set; }
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

// Server settings for the Organizer. On by default: a household that never opens it pays nothing
// for it, and one that does shouldn't have to find a switch first.
public sealed class OrganizerSettingsDto
{
    public const string SectionName = "Organizer";

    public bool Enabled { get; set; } = true;

    [Range(1, 2048)]
    public int MaxAttachmentMegabytes { get; set; } = 50;
}

// The home alarm (see AlarmEngine). On by default like the Organizer: the tab shows an empty
// "add a sensor" state until there is something to arm, and everything else about the alarm is
// set on the tab itself, beside the sensors it applies to.
public sealed class AlarmSettingsDto
{
    public const string SectionName = "Alarm";

    public bool Enabled { get; set; } = true;
}

// Nightly automatic export of the full backup (see AutoExportWorker) to a folder - on this
// machine, a mounted network share, or a Windows \\server\share path - or over SFTP to another
// computer. Lives in the main app's appsettings.json with every other section, so it is itself
// part of the backup it writes: a restored install goes on exporting to the same place.
public sealed class AutoExportSettingsDto : IValidatableObject
{
    public const string SectionName = "AutoExport";
    public const string DestinationFolder = "folder";
    public const string DestinationSftp = "sftp";

    public bool Enabled { get; set; }

    // Local time in the Schedules time zone (the server's own if that is blank), the same clock
    // every other schedule on this install runs by.
    [Range(0, 23)]
    public int Hour { get; set; } = 3;

    [Range(0, 59)]
    public int Minute { get; set; }

    public string Destination { get; set; } = DestinationFolder;

    public string FolderPath { get; set; } = string.Empty;

    public string SftpHost { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int SftpPort { get; set; } = 22;

    public string SftpUsername { get; set; } = string.Empty;

    public string SftpPassword { get; set; } = string.Empty;

    public string SftpFolder { get; set; } = string.Empty;

    // Optional. When set, the upload refuses a server whose host key doesn't match - the
    // protection against something else answering at that address and collecting every secret
    // this install has. When blank the key is accepted and its fingerprint shown in the export
    // status, ready to be pasted in here.
    public string SftpHostKeyFingerprint { get; set; } = string.Empty;

    // How many nightly files to keep at the destination; older ones this feature wrote are
    // deleted after each successful export. Nothing else in the folder is ever touched.
    [Range(1, 365)]
    public int KeepCount { get; set; } = 14;

    // Optional. Encrypts each file (AES-256-GCM, key from PBKDF2) - see BackupBundle. Needed
    // again to restore, and there is no way to recover a file whose passphrase is lost.
    public string Passphrase { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled) yield break;

        if (Destination == DestinationFolder)
        {
            // Rooted rather than relative: a relative path would land somewhere under the
            // service's working directory, which is exactly the machine this is meant to survive.
            if (string.IsNullOrWhiteSpace(FolderPath))
                yield return new ValidationResult("Choose a folder to export to.", [nameof(FolderPath)]);
            else if (!Path.IsPathRooted(FolderPath.Trim()))
                yield return new ValidationResult(
                    "The export folder must be a full path, e.g. /mnt/backups/sentinelcore or \\\\nas\\backups.", [nameof(FolderPath)]);
        }
        else if (Destination == DestinationSftp)
        {
            if (string.IsNullOrWhiteSpace(SftpHost))
                yield return new ValidationResult("Enter the SFTP server's address.", [nameof(SftpHost)]);
            if (string.IsNullOrWhiteSpace(SftpUsername))
                yield return new ValidationResult("Enter the SFTP username.", [nameof(SftpUsername)]);
            if (string.IsNullOrEmpty(SftpPassword))
                yield return new ValidationResult("Enter the SFTP password.", [nameof(SftpPassword)]);
            if (!string.IsNullOrWhiteSpace(SftpHostKeyFingerprint) && !AutoExportSftp.IsFingerprint(SftpHostKeyFingerprint))
                yield return new ValidationResult(
                    "The host key fingerprint should look like SHA256:... as shown by ssh-keygen -lf or in the export status.",
                    [nameof(SftpHostKeyFingerprint)]);
        }
        else
        {
            yield return new ValidationResult("Destination must be a folder or SFTP.", [nameof(Destination)]);
        }

        if (!string.IsNullOrEmpty(Passphrase) && Passphrase.Length < 8)
            yield return new ValidationResult("Use a passphrase of at least 8 characters, or leave it blank.", [nameof(Passphrase)]);
    }
}

// The family file library and its backup drives (FamilyFiles.cs). The drives themselves are a
// list, kept in their own file under the data directory and edited on the Files tab; this is
// the flat part a settings form can show.
public sealed class FamilyFilesSettingsDto : IValidatableObject
{
    public const string SectionName = "FamilyFiles";

    public bool Enabled { get; set; } = true;

    // Blank means <data>/family-files/library. A full path moves the library to a bigger disk;
    // the files already there are not moved automatically.
    public string LibraryFolder { get; set; } = string.Empty;

    [Range(1, 1048576)]
    public int MaxUploadMegabytes { get; set; } = 4096;

    // Local time in the Schedules time zone, like every other nightly job here. An hour after
    // the auto export's default so the two don't compete for the disk.
    [Range(0, 23)]
    public int Hour { get; set; } = 4;

    [Range(0, 59)]
    public int Minute { get; set; }

    // When on, the nightly check moves every duplicate but one to the trash; when off, it only
    // lists them on the Files tab.
    public bool RemoveDuplicates { get; set; } = true;

    [Range(1, 365)]
    public int TrashKeepDays { get; set; } = 30;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(LibraryFolder) && !Path.IsPathRooted(LibraryFolder.Trim()))
            yield return new ValidationResult(
                "The library folder must be a full path, e.g. /mnt/bigdisk/family-files, or blank for the default.", [nameof(LibraryFolder)]);
    }
}
