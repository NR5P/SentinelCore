using System.ComponentModel.DataAnnotations;

namespace SentinelCore;

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

    // NOTE: UnverifiedMotionRetentionDays also lives in this section of appsettings.json, but is
    // deliberately NOT bound here. Nothing in this process reads it - it belongs to the web app's
    // MotionRetentionWorker, which reads the file directly - and binding it in two places with two
    // defaults is how a setting ends up meaning different things depending on who asks. It shares
    // the section only so the Settings UI can show it next to RetentionDays, and AppSettingsFile
    // merges sections key-by-key, so an unbound key survives a write untouched.

    public HashSet<string> AllowedImageContentTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };
}

public sealed class PushNotificationOptions
{
    public const string SectionName = "PushNotifications";

    public bool Enabled { get; init; }
    public bool OnlyWhenNotificationRecommended { get; init; } = true;

    [Range(0, 1)]
    public double MinimumConfidence { get; init; } = 0.60;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; init; } = 5;

    public ApplePushOptions Apple { get; init; } = new();
    public FirebasePushOptions Firebase { get; init; } = new();
}

public sealed class ApplePushOptions
{
    public bool Enabled { get; init; }
    public string TeamId { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public string BundleId { get; init; } = "com.sentinelcore.client";
    // Apple requires the apns-topic to exactly match the receiving app's own bundle id, so the
    // tvOS app (a separate App ID) needs its own topic here rather than reusing BundleId. Left
    // blank, SendAppleAsync falls back to BundleId - that push will simply be rejected by APNs
    // for tvOS devices rather than crash anything.
    public string TvBundleId { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public bool UseSandbox { get; init; } = true;

    // Pulled out of PushNotificationDispatcher as a pure function so the topic-selection rule
    // (tvOS gets its own topic, everything else keeps using BundleId) is directly testable
    // without standing up ECDSA signing or a fake HTTP pipeline.
    public string ResolveApnsTopic(string appVariant) =>
        string.Equals(appVariant, "tvos", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(TvBundleId)
            ? TvBundleId
            : BundleId;
}

public sealed class FirebasePushOptions
{
    public bool Enabled { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ServiceAccountPath { get; init; } = string.Empty;
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

    // "ollama", "cloud", or "fastlocal". Defaults to "ollama" so every existing install keeps
    // behaving exactly as it always has with zero config changes.
    [Required]
    public string Primary { get; init; } = "ollama";

    // If the primary backend's own retries are exhausted, automatically retry once against
    // another backend before giving up on the alert entirely - e.g. cloud unreachable -> fall
    // back to local Ollama, slower but still analyzed. When primary is "fastlocal", the
    // fallback is a VLM backend (cloud if configured, otherwise Ollama).
    public bool FallbackEnabled { get; init; }

    // Only meaningful when Primary is "fastlocal": after the fast detector finds a person,
    // vehicle, or animal, re-analyze the same alert with a VLM backend to get the full
    // description (gate state, delivery company, species, prose summary). Alerts where the
    // detector finds nothing - the large majority - never reach the expensive backend at all,
    // which is the whole point of the fast tier. If escalation fails, the detector's own
    // analysis still stands rather than failing the alert.
    public bool EscalationEnabled { get; init; }

    // Which backend escalation goes to: "auto" (cloud if configured, otherwise Ollama),
    // "cloud", or "ollama".
    [Required]
    public string EscalationTarget { get; init; } = "auto";

    // Only meaningful alongside EscalationEnabled: who wins when the detector found a subject and
    // the escalated VLM reports an empty frame. Off (the default) keeps the detector's result -
    // the detector is usually right about mere presence, and one VLM shrugging at a dark IR frame
    // would otherwise file real footage as Rejected and hide it. On, the VLM's verdict replaces
    // the detection, which is what you want when the two disagree constantly enough that the
    // detector is clearly the unreliable one.
    //
    // Either way, escalation *failure* - target unreachable, target threw, no target configured -
    // still keeps the detector's result. "The VLM said nothing is there" and "the VLM never
    // answered" are different facts and only the first one is a verdict.
    public bool EscalationVerdictWins { get; init; }

    // The VLM tier's counterpart to FastLocal:ScoreThreshold. A VLM analysis scoring below this is
    // treated as having found no subject: the visible flags and package status are cleared and the
    // notification is suppressed, while the summary, descriptions and the raw response are kept so
    // the event still reads correctly. 0 (the default) disables the check entirely, preserving the
    // behaviour every existing install has today.
    //
    // Pairs with the prompt convention in OllamaVisionClient.BuildPrompt, which tells the model to
    // report a confidence at or below 0.2 when the frame is too dark or obstructed to judge - so a
    // floor anywhere above that turns "I can't see" into "nothing here" instead of a false alert.
    //
    // Deliberately not applied to fast-detector results: FastLocalVisionClient scores an *empty*
    // frame 0.8 by convention and a real detection by its own max score, so the two aren't on the
    // same number line and a shared floor would suppress a 0.82 detection while passing the 0.8
    // empty frame. ScoreThreshold is that tier's gate and it runs per-detection, before mapping.
    // Also not applied to gate checks, which by construction have nobody in frame.
    [Range(0, 1)]
    public double MinimumConfidence { get; init; }
}

public sealed class FastLocalOptions
{
    public const string SectionName = "FastLocal";

    // Relative paths resolve under Bridge:DataDirectory, matching every other model this app
    // stores (clip-model, known-faces). Changing this after the session has loaded needs a
    // restart - the ONNX session is created once per process, like the CLIP encoders.
    [Required]
    public string ModelPath { get; init; } = "fastlocal-model/rf-detr-nano.onnx";

    // Fetched once on first use if ModelPath doesn't exist yet - RF-DETR Nano (Apache 2.0),
    // ~103MB. Clear this to require the model file to be provisioned manually instead.
    public string DownloadUrl { get; init; } =
        "https://huggingface.co/PierreMarieCurie/rf-detr-onnx/resolve/main/rf-detr-nano.onnx";

    // SHA-256 of the file DownloadUrl serves, verified before the download is moved into place.
    // Only applies to the download; a manually provisioned ModelPath is trusted as-is. Clear it
    // to skip verification when pointing DownloadUrl somewhere else.
    public string DownloadSha256 { get; init; } =
        "3fcbba0f68bad4939fdf1c38f432783b95691e2869af3be369780aa5be67abb2";

    // Fallback only: the model's own static input shape wins when it declares one (RF-DETR
    // Nano is fixed 384x384), so this only matters for a substituted model with dynamic axes.
    [Range(64, 1536)]
    public int InputSize { get; init; } = 384;

    // Minimum sigmoid score for a detection to count. 0.5 keeps essentially every real subject
    // in daylight and most at night while dropping the sub-0.4 noise DETR queries emit for
    // background texture; lower it if a camera is missing distant/night subjects, at the cost
    // of more false alerts.
    [Range(0, 1)]
    public double ScoreThreshold { get; init; } = 0.5;

    // Which detected subjects set notification_recommended. Animals default off, matching the
    // VLM prompt's own stance that ordinary wildlife isn't alert-worthy - a detector can't tell
    // a raccoon in the trash from a bird on the lawn, so it can't make that judgment call.
    public bool NotifyOnPerson { get; init; } = true;
    public bool NotifyOnVehicle { get; init; } = true;
    public bool NotifyOnAnimal { get; init; }
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

    // Off by default. When on, the AI is additionally asked whether the camera's own view is
    // blocked, covered, or obstructed. Only set on the ephemeral profile GateCheckAnalyzer builds
    // for a gate/door check that has this enabled - not exposed on the saved PromptProfiles list,
    // since every other alert path (Blue Iris, trigger zones) has no use for it.
    public bool CameraBlockedCheckEnabled { get; init; }

    // Off by default. When on, AlertProcessor runs local face detection/matching (see
    // FaceRecognitionService, KnownFaceMatcher) against the alert image and labels any faces
    // that match the configured KnownFaces list. Unlike LicensePlateRecognitionEnabled, this
    // never changes what's sent to the vision model - detection and matching happen entirely
    // outside the VLM, so there's no prompt/schema change for any backend.
    public bool FaceRecognitionEnabled { get; init; }
}

public sealed class KnownVehicle
{
    public string Plate { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

public sealed class KnownFace
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = string.Empty;
    public List<KnownFaceEnrollment> Enrollments { get; init; } = [];
}

public sealed class KnownFaceEnrollment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    // Relative to Bridge:DataDirectory, e.g. "known-faces/{knownFaceId}/{enrollmentId}.jpg" -
    // the reference photo this embedding was computed from, kept so the enrollment list can
    // show a thumbnail of what was enrolled.
    public string ImagePath { get; init; } = string.Empty;

    // The face embedding vector computed from ImagePath at enrollment time (ArcFace, 512-dim,
    // L2-normalized). Compared against detected faces via cosine similarity in KnownFaceMatcher.
    public float[] Embedding { get; init; } = [];
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

    // On by default - internal errors are worth hearing about, and an install that only reports
    // them once someone opts in never reports the ones that matter. Still sends nothing until
    // Email is configured. The recipient is fixed (see IBugReportSender.Recipient) and isn't
    // exposed as a setting; sending reuses whatever SMTP account is configured under Email above.
    public bool Enabled { get; init; } = true;
}

public sealed class LoginAlertOptions
{
    public const string SectionName = "LoginAlerts";

    // Off by default, like every other outbound-notification feature here - nothing is sent
    // anywhere until explicitly turned on. Sending itself still separately depends on Email
    // and/or Telegram being configured; this only gates whether an invalid login attempts to
    // use them at all.
    public bool Enabled { get; init; }

    // One alert per source IP per window, no matter how many usernames are tried against it -
    // otherwise a brute-force burst would send one email/Telegram message per guess instead of
    // one alert for the whole burst.
    [Range(1, 1440)]
    public int CooldownMinutes { get; init; } = 15;
}

public sealed class NuisanceAdvisorOptions
{
    public const string SectionName = "NuisanceAdvisor";

    // On by default, unlike the outbound-notification features above, because this one is advice
    // about the owner's own cameras sent to the owner's own address - and because the whole
    // problem it exists to name is one that otherwise goes unnoticed for months while the AI
    // quietly swallows a few hundred empty frames a night. It still sends nothing until Email or
    // Telegram is configured, and at most one message per camera per CooldownDays.
    public bool Enabled { get; init; } = true;

    // How many dusk-to-dawn triggers make a night worth mentioning. Below this it is just a
    // night, whatever caused it, and advice to go clean a lens would be noise of its own. The
    // gate camera's measured web night was 235.
    [Range(10, 5000)]
    public int MinimumNightTriggers { get; init; } = 60;

    // The share of those triggers the AI may confirm as a real person, vehicle, package, or
    // animal before this stops looking like a web. A night with something genuinely happening in
    // it confirms a good fraction; the measured web night confirmed none of 235.
    [Range(0, 100)]
    public double MaximumConfirmedPercent { get; init; } = 10;

    // How many times busier the dark hours must be than the daylight either side. This is what
    // keeps a camera pointed at a busy road - which triggers all day and needs its zone redrawn,
    // not a broom - from being told it has a spider web. Measured on the web night: 6.4.
    [Range(1, 50)]
    public double MinimumNightDayRatio { get; init; } = 3;

    // A web that isn't dealt with is still there tomorrow, so this repeats - but slowly. Long
    // enough that clearing the web on the morning you're told ends it, short enough that ignoring
    // the advice gets a reminder rather than silence.
    [Range(1, 90)]
    public int CooldownDays { get; init; } = 3;
}

public sealed class TriggerApiOptions
{
    public const string SectionName = "TriggerApi";

    // Off by default. While it's off the endpoint answers 404 rather than 403 - a scanner that
    // finds this dashboard shouldn't learn that a camera-triggering route exists here at all.
    public bool Enabled { get; init; }

    // Its own key rather than a share of Bridge:ApiKey, because this one gets pasted into
    // whatever doorbell, NVR, or relay is doing the triggering, and that config is far more
    // likely to leak than the one Blue Iris uses. Revoking it costs nothing but the devices
    // configured with it. Generated when the feature is first turned on - see SettingsEndpoints.
    public string ApiKey { get; init; } = string.Empty;

    // Per camera, not per caller: the point is to stop one over-eager device from queueing a
    // hundred analyses, and it doesn't matter which of them asked.
    [Range(0, 3600)]
    public int CooldownSeconds { get; init; } = 15;
}

public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public bool Enabled { get; init; }

    // If true, this process hosts its own MQTT broker instead of requiring an external one
    // (e.g. Mosquitto/Home Assistant's own broker add-on). Either way, this app's own client
    // still connects out to a broker to publish - when this is true it connects to its own
    // embedded one on 127.0.0.1; when false it connects to Host/Port below.
    // Unlike Urls and the other startup socket bindings, this and EmbeddedBrokerPort apply
    // live: EmbeddedMqttBrokerService reconciles on every options change.
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

// The Device Hub - controlling lights/switches/etc over the same broker MqttOptions above
// configures, rather than only publishing camera events to it. Strictly an add-on to MQTT: with
// Mqtt:Enabled off there's no broker connection to command anything through, so the settings
// endpoint refuses to turn this on until MQTT itself is on (see SettingsEndpoints).
public sealed class DeviceHubOptions
{
    public const string SectionName = "DeviceHub";

    public bool Enabled { get; init; }

    // How long a device with no availability topic can go silent before it's shown as
    // unavailable. Devices that do publish availability are judged by that instead - see
    // MqttDeviceBus.GetState - because most devices only publish when something changes, and a
    // lamp that has simply been on all day would otherwise read as offline.
    [Range(10, 86400)]
    public int StateTimeoutSeconds { get; init; } = 90;

    // How long a discovery scan listens to the broker before reporting what it found. Long
    // enough for retained Home Assistant discovery configs (which arrive immediately) plus a
    // window for chatty devices to announce themselves on their own schedule.
    [Range(2, 60)]
    public int DiscoverySeconds { get; init; } = 10;

    // How long to wait for a non-optimistic device to echo its new state back before the command
    // is reported as un-acknowledged. The publish itself has already happened either way.
    [Range(1, 60)]
    public int CommandTimeoutSeconds { get; init; } = 5;
}

public sealed class HomeKitOptions
{
    public const string SectionName = "HomeKit";

    public bool Enabled { get; init; }

    // The HomeKit accessory protocol (pairing crypto, mDNS advertisement) is handled by a small
    // Node sidecar - see core/homekit-bridge. There's no maintained .NET implementation of HAP,
    // so this app just pings the sidecar's loopback-only control API and lets it own everything
    // Apple-specific. If the sidecar isn't running, alerts carry on unaffected.
    [Required]
    public string SidecarBaseUrl { get; init; } = "http://127.0.0.1:3321";

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 10;

    // Every HomeKit trip is a push notification on a phone, so this matches the Email/Telegram
    // default rather than letting low-confidence readings through. Quiet hours can raise it.
    [Range(0, 1)]
    public double MinimumConfidence { get; init; } = 0.6;

    // Nothing in this app reads this - the sidecar polls appsettings.json for it directly, the
    // same way it polls Enabled. It lives here so the setting is bound, validated and documented
    // alongside the rest of the HomeKit section rather than existing only as a loose JSON key.
    public bool IncludeDevices { get; init; }
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

    // 587 (STARTTLS) is what most providers document first, but 465 (TLS from the first byte) and
    // 25/2525 for a relay on the LAN all work - see SmtpMailer.SecurityFor.
    [Range(1, 65535)]
    public int SmtpPort { get; init; } = 587;

    public bool EnableSsl { get; init; } = true;

    // Blank means the server wants no login, which is normal for a relay on your own network.
    // It does not have to be an email address: SES uses an access key, SendGrid the literal word
    // "apikey". Whenever it isn't one, FromAddress has to be filled in instead.
    public string Username { get; init; } = string.Empty;
    public string AppPassword { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = "Gate Camera";
    public string ToAddress { get; init; } = string.Empty;

    // The whole exchange, not just the connection. Long enough for a slow server to accept a
    // summary email with several photos on it, short enough that a wrong port fails while
    // someone is still sitting in front of the settings page waiting for the answer.
    [Range(5, 300)]
    public int TimeoutSeconds { get; init; } = 30;

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

    // Not [Required]: unlike CloudVisionOptions.BaseUrl/Model, this has no safe non-empty
    // default to fall back to (there's no "the" Blue Iris server), and BlueIris is off by
    // default. Program.cs's conditional .Validate(o => !o.Enabled || ...) below is what
    // actually enforces this - a blanket [Required] here would reject the shipped
    // Enabled: false, BaseUrl: "" default and make BlueIris impossible to leave unconfigured.
    public string BaseUrl { get; init; } = string.Empty;
    public string Camera { get; init; } = string.Empty;

    // Blue Iris's direct image endpoint accepts these as query parameters. Leave both blank
    // if the web server doesn't require login on your LAN.
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 15;
}

public sealed class MultiFrameOptions
{
    public const string SectionName = "MultiFrameAnalysis";

    // Off by default. Sends 2-3 frames captured a fraction of a second apart, instead of one, so
    // the AI can use temporal consistency to ignore a shadow/reflection/headlight sweep that
    // only appears in a single frame - see TriggerZoneMonitor for the capture side. Only ever
    // applies to your own added cameras' trigger zones, never Blue Iris alerts. This is a real
    // cost, not just a nice-to-have toggle: it multiplies AI processing time/cost per alert, and
    // a single image is already a meaningful load on modest local hardware - leave this off
    // unless you've confirmed single-image analysis is already comfortably fast.
    public bool Enabled { get; init; }

    // Total frames sent per alert = 1 (the frame captured at the trigger moment) + this.
    [Range(1, 2)]
    public int ExtraFrameCount { get; init; } = 1;

    [Range(150, 1000)]
    public int FrameIntervalMs { get; init; } = 350;
}

public sealed class TriggerRewindOptions
{
    public const string SectionName = "TriggerRewind";

    // Off by default, which is exactly today's behaviour: the trigger's image is a fresh photograph
    // taken at the instant the trigger finishes being handled.
    //
    // The problem that turns this on: motion is detected on a deliberately tiny 320x180 copy of the
    // video, because comparing full-resolution frames twice a second would cost more CPU than the
    // rest of the system together. So the moment is known but the picture is not, and the code has
    // to go and take one - which can only ever mean "now", some time after the event. With this on,
    // the full-resolution frame for that exact moment is fetched out of the rolling video buffer
    // instead, so what you see and what the AI is given are the frame the motion was actually in.
    //
    // This is the same approach every established system in this space takes: Frigate hands its
    // tracker a (camera, frame_time, frame_id) reference to look the frame up by, ZoneMinder keeps
    // a shared-memory ring of recent images, Blue Iris keeps a pre-trigger buffer measured in
    // frames. The lookup is by timestamp alone - no search, no scoring, nothing to tune - which is
    // what makes it behave identically on fast hardware and slow.
    public bool Enabled { get; init; }

    // How far back a fetch is allowed to reach. This is NOT a search window: the instant wanted is
    // known exactly. It only bounds the damage from a timestamp that cannot be right - a clock step,
    // a camera reconnecting - so nothing can present footage from minutes ago as the current event.
    [Range(2, 15)]
    public int LookbackSeconds { get; init; } = 6;

    // Fetching needs a few seconds of recent video on disk, which is the same rolling segment buffer
    // clip recording already keeps. With this on, that buffer runs for every camera with an enabled
    // trigger zone rather than only those recording clips, so rewind works everywhere instead of
    // being an accident of an unrelated setting. The buffer is written with -c copy - no decoding,
    // no encoding, near-zero CPU - but it is a continuous write at the camera's full bitrate, so on
    // a small or heavily-worn SSD this is the setting to weigh. Inert while Enabled is false.
    public bool BufferEveryCamera { get; init; } = true;

    // Fetch the same instant from the other cameras in a camera group. They have no detection of
    // their own to date the event by, so they borrow the triggering camera's timestamp - an
    // approximation, since each camera's own delay differs, but a far better one than a frame taken
    // after the event. Inert while Enabled is false.
    public bool ApplyToCompanions { get; init; } = true;
}

public sealed class PackageTheftOptions
{
    public const string SectionName = "PackageTheftWatch";

    // Off by default. When a package is marked delivered, watches for it to later show as gone
    // on a subsequent alert from the same camera, and flags it if whoever's in that later alert
    // isn't a recognized face/vehicle - see AlertProcessor and PackageWatchStore. Uses data
    // already computed for every alert, so unlike Multi-Frame Analysis or Face Recognition this
    // costs no extra AI calls - it's off by default because it's a heuristic judgment call that
    // can force a notification, not because of processing cost. It can only catch a removal that
    // itself trips a new motion alert on the camera; a package taken with no interceding alert
    // at all won't be caught.
    public bool Enabled { get; init; }

    [Range(1, 72)]
    public int WatchWindowHours { get; init; } = 12;
}

// What to do when one camera fires again within seconds of the last time - the ordinary shape of
// somebody arriving, not a fault. Both settings here exist because a burst is one event that
// happens to produce several frames, and the pipeline used to treat every frame as a fresh event
// with its own analysis and its own notification.
public sealed class RepeatAlertOptions
{
    public const string SectionName = "RepeatAlerts";

    // See AlertQueue: an alert still waiting behind an in-progress analysis is stood down when a
    // newer one from the same camera arrives. On by default - the frame being dropped is a few
    // seconds older than the one replacing it, and its footage is kept either way.
    public bool SupersedeQueued { get; init; } = true;

    // See NotificationDeduplicator. 0 disables the check, and every channel falls back to its own
    // per-category DuplicateCooldownMinutes exactly as before.
    [Range(0, 3600)]
    public int DuplicateNotificationWindowSeconds { get; init; } = 90;
}

public sealed class SemanticSearchOptions
{
    public const string SectionName = "SemanticSearch";

    // Off by default. When on, AlertProcessor encodes each saved alert image into a CLIP vector
    // (ImageEmbeddingService) so the Feed's search box can later match by visual content instead
    // of literal text. The CLIP model itself is a ~577MB one-time download on first use, and
    // encoding adds a per-alert cost similar to Face Recognition - both real costs worth
    // disclosing, not just an extra toggle.
    public bool Enabled { get; init; }

    // Empirically chosen from real CLIP score distributions (see the Step 0 smoke test): a
    // correct image/caption match scored ~0.25-0.27, a wrong one ~0.17-0.24 - much lower and
    // narrower than KnownFaceMatcher's face-embedding scores, so this needed its own threshold
    // rather than reusing that one.
    [Range(0.0, 1.0)]
    public double MinimumSimilarity { get; init; } = 0.20;
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

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public bool Enabled { get; init; }
    public string WebhookUrl { get; init; } = string.Empty;
    public bool OnlyWhenNotificationRecommended { get; init; } = true;

    [Range(0, 1)]
    public double MinimumConfidence { get; init; } = 0.60;

    public bool AttachImage { get; init; } = true;

    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 20;

    [Range(0, 1440)]
    public int DuplicateCooldownMinutes { get; init; } = 10;

    public static bool IsValidWebhookUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var trustedHost =
            string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "discordapp.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);

        return trustedHost &&
               System.Text.RegularExpressions.Regex.IsMatch(
                   uri.AbsolutePath,
                   @"^/api(?:/v\d+)?/webhooks/[^/]+/[^/]+",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                   System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
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

    // Long enough for a small local model to chew through a full event log (up to
    // MaxEventsInContext lines) or a documentation excerpt on modest CPU hardware - confirmed
    // against a real, actively-used camera with ~90 events in its 3-day lookback window that
    // 180s wasn't always enough. A "how do I..." or "what happened" question isn't time-critical
    // the way an alert is, so a longer wait here is the right tradeoff over cutting the request
    // off early - matches OllamaOptions.RequestTimeoutSeconds' own default for the same reason.
    [Range(5, 600)]
    public int TimeoutSeconds { get; init; } = 300;
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

    // Warn when the disk holding the data directory drops below this. 0 disables the check.
    //
    // The default sits deliberately above ScheduledRecordingOptions.MinimumFreeGigabytes (5), which
    // is the point at which a scheduled recording refuses to start. That floor is the first thing
    // that visibly breaks, and by then the databases and the clip recorder are already close to
    // having nowhere to write - so a warning that fires at the same number is a warning that
    // arrives too late to act on. 10 GB buys several days at the accumulation rate confirmed
    // trigger clips actually run at (see README "Data"), which is enough time to prune by hand.
    [Range(0, 10_000)]
    public int FreeDiskWarningGigabytes { get; init; } = 10;

    public bool NotifyOnRecovery { get; init; } = true;
}

public sealed class StartupNotificationOptions
{
    public const string SectionName = "StartupNotification";

    // Sends to Email:ToAddress - the same inbox as the alerts. Unlike SelfMonitoring, which can
    // only report problems it's running to observe, this one reports the outage after the fact.
    public bool Enabled { get; init; } = true;

    // Below this, a restart isn't worth an email. A redeploy takes a few seconds, so the default
    // keeps ordinary restarts silent while still catching anything that looks like a real outage.
    [Range(1, 1440)]
    public int MinimumDowntimeMinutes { get; init; } = 2;

    // Also the resolution of the reported downtime: the gap is measured from the last heartbeat,
    // so it can under-report by up to this much. The email says "at least" for that reason.
    [Range(10, 3600)]
    public int HeartbeatIntervalSeconds { get; init; } = 60;
}

public sealed class InternetOutageOptions
{
    public const string SectionName = "InternetOutage";

    // Any answer at all from one of these proves packets are leaving the building, so the exact
    // endpoints matter far less than their reliability. All three are the connectivity checks
    // their own operating systems use, which is about as reliable as public endpoints get.
    public static readonly IReadOnlyList<string> DefaultProbeUrls =
    [
        "https://www.gstatic.com/generate_204",
        "https://cp.cloudflare.com/generate_204",
        "https://captive.apple.com/hotspot-detect.html"
    ];

    // Off by default, deliberately: plenty of installs are LAN-only on purpose, and telling those
    // owners hourly that their deliberately isolated network is broken would be worse than
    // useless. Reports on recovery only - a machine with no route out can't tell anyone about it.
    public bool Enabled { get; init; }

    [Range(10, 3600)]
    public int CheckIntervalSeconds { get; init; } = 60;

    // How many checks in a row must fail before it counts. Domestic connections drop a packet or
    // renegotiate often enough that one failure means nothing.
    [Range(1, 20)]
    public int ConsecutiveFailuresBeforeAlert { get; init; } = 3;

    // Below this, the drop isn't worth telling anyone about after the fact.
    [Range(1, 1440)]
    public int MinimumOutageMinutes { get; init; } = 2;

    // Per probe. Every probe has to time out before a check counts as failed, so the worst case
    // for one cycle is this multiplied by the number of probes - keep the product well under
    // CheckIntervalSeconds.
    [Range(1, 60)]
    public int ProbeTimeoutSeconds { get; init; } = 5;

    // Empty falls back to DefaultProbeUrls rather than meaning "never check", so clearing the
    // field can't silently disable the feature while it still reads as on.
    public List<string> ProbeUrls { get; init; } = [];
}

public sealed class LocationOptions
{
    public const string SectionName = "Location";

    // Only ever used to work out when the sun rises and sets here, which is what lets a schedule
    // say "when it gets dark" instead of a clock time that would need changing every few weeks.
    // Never transmitted anywhere: the sunrise calculation is local arithmetic (SolarCalculator),
    // with no service to call and nothing to look up at run time.
    //
    // Unset is (0, 0) - a point in the Gulf of Guinea, and the reason GeoCoordinate.IsValid
    // treats it as "not configured" rather than as a real place near the equator.
    [Range(-90, 90)]
    public double Latitude { get; init; }

    [Range(-180, 180)]
    public double Longitude { get; init; }

    // What the coordinates were derived from ("Louisville, KY"), for display only. Kept so the
    // Settings screen can show somewhere recognisable rather than two numbers nobody can check
    // at a glance; nothing reads it back.
    public string Label { get; init; } = string.Empty;
}

public sealed class ScheduleOptions
{
    public const string SectionName = "Schedules";

    // On by default, unlike most integrations here, because it costs nothing until a schedule
    // exists - and once one does, the owner has already said what they want to happen.
    public bool Enabled { get; init; } = true;

    // How late a firing missed while the server was down may still be honoured, for schedules
    // set to catch up. Beyond this the run is recorded as missed instead: a coop door has no
    // business closing at breakfast because the machine was off overnight.
    [Range(1, 1440)]
    public int MaximumCatchUpMinutes { get; init; } = 60;

    // Empty means the machine's own time zone, which is what QuietHours and the daily digest
    // already use. Set it to an IANA id ("America/New_York") when the server's clock is on UTC
    // but the schedules should follow local wall-clock time - common on headless installs.
    public string TimeZone { get; init; } = string.Empty;
}
