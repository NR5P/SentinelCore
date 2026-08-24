using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Web;

// The single description of what every settings section contains: which fields, what to call
// them, what to tell the user about them, and what values are legal. Served to every client from
// /api/settings/schema so the web dashboard, the iOS/macOS app, and the Android app all render
// the same forms from the same source instead of each hard-coding its own copy of 120 fields.
//
// This was previously the SECTIONS array inside wwwroot/settings.js, where only the browser
// could reach it. Moving it here is what makes "every setting manageable from every client"
// maintainable: adding a setting is one edit in this file plus its DTO, not four edits across
// four codebases that silently drift apart.
//
// Kept as plain data on purpose - no behaviour, no formatting, nothing platform-specific. A
// client decides for itself how a "number" field or a "select" should look; this only says what
// the field *is*. The one deliberate exception is ExtraAction, which is still pure data (an
// endpoint to POST to), not a callback.
//
// SettingsSchemaTests asserts this stays in exact correspondence with the DTOs in
// SettingsDtos.cs, so a field added to one and forgotten in the other fails the build rather
// than quietly becoming uneditable.

public sealed record SettingsFieldOption(string Value, string Label);

// An action a section offers beyond editing its fields - a button, in UI terms. Expressed as an
// endpoint and method rather than a callback so a client that can't or won't implement one can
// simply skip it.
public sealed record SettingsActionSchema(
    string Key,
    string Label,
    string Tip,
    string Endpoint,
    string Method)
{
    // Whether the client should reload the page after the action succeeds. Only "re-run setup
    // wizard" needs it - the wizard can't reappear without one - and the web dashboard used to
    // reload after every action because it was the only action there was. A test send has
    // something to say when it finishes, and reloading throws it away, so this is opt-in rather
    // than the default. Clients that don't have a page to reload (the phone and Mac apps) ignore
    // it entirely.
    public bool ReloadAfter { get; init; }
}

// A lookup a text field offers: type something into the field, press the button, and pick from
// what comes back to fill in *other* fields of the same section. Pure data like ExtraAction - an
// endpoint, not a callback - so this stays platform-neutral and a client that hasn't implemented
// it renders an ordinary text box, exactly as every client did before this existed.
//
// The endpoint takes ?query= and answers either
//   { "results": [ { "label": "Holland, Bell County, Texas", "values": { "latitude": 31.0932, "longitude": -97.4025 } } ] }
// or, for anything the user should be told about, an { "error": "..." } with a non-2xx status.
// Keys in "values" are field names in this same section; a client applies the ones it recognises
// and ignores the rest.
public sealed record SettingsLookupSchema(
    string Label,
    string Tip,
    string Endpoint);

public sealed record SettingsFieldSchema
{
    public required string Name { get; init; }
    public required string Label { get; init; }

    // One of: text, number, checkbox, password, select, csv. A client that meets a type it
    // doesn't recognise should render the field read-only rather than guessing - better to show
    // a value that can't be edited than to write back something malformed.
    public required string Type { get; init; }

    public required string Tip { get; init; }

    // Numeric bounds, mirroring the [Range] attributes on the DTO properties. Advisory only -
    // the server validates regardless, since a client is free to ignore these. Kept honest by
    // SettingsSchemaTests, which compares every one of these against its DTO's [Range]: bounds
    // that drifted would have clients rejecting values the server accepts, or vice versa.
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }

    // Whether a "number" field holds a whole number. Set from the DTO property type when Sections
    // is built rather than declared per field, for the same reason as Secret below: a hand-written
    // flag could disagree with the type the value actually binds to. Without it a client has no way
    // to tell a port (int) from a free-space threshold in GB (double) - both are plain numbers with
    // no step - and 3.7 typed into the port box would reach the server as a bind error rather than
    // as "Broker Port must be a whole number".
    public bool Integer { get; init; }

    // The value someone with no opinion should use, for the numeric settings where "what do I
    // even put here?" is the real question - a confidence threshold means nothing to somebody who
    // hasn't watched this particular camera misfire for a week. Clients show it next to the
    // control and can offer to jump back to it.
    //
    // This is a recommendation, not the shipped default, and the two differ on purpose: a setting
    // can ship off (0) because off is the safe way to arrive, while the number worth trying first
    // once you have a reason to touch it is something else entirely. Where they agree, saying so
    // is still useful - it tells the reader they have nothing to decide.
    public double? Recommended { get; init; }

    // Only for Type == "select".
    public IReadOnlyList<SettingsFieldOption>? Options { get; init; }

    // Only for Type == "text": an optional "look this up for me" button that fills in sibling
    // fields. Optional on every client - the field is still an editable text box without it.
    public SettingsLookupSchema? Lookup { get; init; }

    // True for the handful of fields that bind a socket or otherwise only read their value at
    // startup. A client should tell the user the change won't apply until the server restarts.
    public bool RequiresRestart { get; init; }

    // Whether the stored value is a secret. Derived from Type rather than declared separately so
    // the two can't disagree: everything rendered as a password is masked on read and left alone
    // on write unless the client actually changed it.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool Secret => Type == "password";
}

// The seven headings the settings page is divided into. This exists because the section list grew
// to 36 entries in build order, which meant the four trigger sections sat at positions 14, 17, 34
// and 35 and everything that depends on Email being configured was spread across four corners of
// the page. Grouping is deliberately a property of the schema rather than of any one client: the
// web dashboard draws these as headings between accordions, the phone and Mac apps as section
// headers in their list, and neither had to be told which section means what.
//
// A group is a heading, not a container - nothing collapses, and every section is still exactly
// one click from the top of its page. Nesting the accordions two deep was the other option and is
// worse on every surface: two clicks to reach any field, collapse state that's easy to lose track
// of, and a shape the drill-down apps can't mirror anyway.
public static class SettingsGroups
{
    public const string Alerts = "Alerts & Delivery";
    public const string Ai = "AI & Detection";
    public const string Cameras = "Cameras, Triggers & Recording";
    public const string Automation = "Automation & Smart Home";
    public const string Health = "Health & Monitoring";
    public const string Access = "Access & Network";
    public const string Maintenance = "Maintenance";

    // The order the headings appear in, which is not alphabetical and shouldn't be: this runs from
    // what a new install touches first (where do my alerts go?) to what it touches last, if ever.
    // Clients render groups in this order and put anything they don't recognise at the end, so a
    // group added here doesn't need a matching client release to avoid disappearing.
    public static IReadOnlyList<string> Order { get; } =
        [Alerts, Ai, Cameras, Automation, Health, Access, Maintenance];

    private static readonly Dictionary<string, int> Positions =
        Order.Select((group, index) => (group, index))
             .ToDictionary(entry => entry.group, entry => entry.index, StringComparer.Ordinal);

    // An unknown group sorts last rather than throwing: a client running against a newer server
    // should show a group it doesn't recognise at the bottom, not refuse to draw the page.
    public static int PositionOf(string group) =>
        Positions.TryGetValue(group, out var position) ? position : Order.Count;
}

public sealed record SettingsSectionSchema
{
    public required string Key { get; init; }
    public required string Title { get; init; }

    // Which heading this section sits under - one of SettingsGroups. Required rather than
    // optional: a section with no group would silently fall to the bottom of every client's page,
    // which is exactly the drift this schema exists to prevent.
    public required string Group { get; init; }

    // The group's position in SettingsGroups.Order, serialized so a client can sort the headings
    // into the intended order without a second request or its own hand-copied list of groups.
    public int GroupOrder => SettingsGroups.PositionOf(Group);

    // Where a PUT of this section's values goes.
    public required string Endpoint { get; init; }

    // Which service has to restart for this section's RequiresRestart fields to take effect.
    // "bridge" for everything that configures the main app - which is all but one section, hence
    // the default. INTERNET EXPOSED MODE is the exception: it binds this dashboard's own Kestrel
    // port, so restarting the bridge would do nothing at all.
    public string RestartService { get; init; } = "bridge";

    // Where to GET this section's current values, for the sections that aren't part of the bulk
    // /api/settings snapshot. Null for all but one, which is the point: a client fetches the
    // snapshot once and reads every section out of it by key, and only falls back to a per-section
    // request where a section says it needs one.
    //
    // INTERNET EXPOSED MODE is that section - it configures this dashboard project's own Kestrel
    // binding and so lives in the Web project's appsettings.json rather than the main app's. The
    // web dashboard used to handle this by hard-coding the whole section client-side, which is
    // exactly why it was the one section the phone and Mac apps never had. Saying "fetch my values
    // from here" in the schema instead costs one optional property and hands the section to every
    // client at once.
    public string? DataEndpoint { get; init; }

    // Whether the section shows in the dashboard's basic mode. Advanced sections are the ones a
    // typical install never needs to touch.
    public required bool Basic { get; init; }

    public required string Description { get; init; }
    public required IReadOnlyList<SettingsFieldSchema> Fields { get; init; }

    public SettingsActionSchema? ExtraAction { get; init; }

    // The DTO this section's values bind to. Never serialized - it exists so the drift test can
    // walk the schema and check each section against its DTO without a second hand-maintained
    // map that could itself be forgotten. Not `required` like the rest: System.Text.Json refuses
    // to serialize a type whose required property is [JsonIgnore]d, so enforcement falls to
    // SettingsSchemaTests, which dereference this for every section and would throw on a null.
    [JsonIgnore]
    public Type DtoType { get; init; } = null!;
}

public static class SettingsSchema
{
    private static SettingsFieldSchema Text(
        string name, string label, string tip, SettingsLookupSchema? lookup = null) =>
        new() { Name = name, Label = label, Type = "text", Tip = tip, Lookup = lookup };

    private static SettingsFieldSchema Password(string name, string label, string tip) =>
        new() { Name = name, Label = label, Type = "password", Tip = tip };

    private static SettingsFieldSchema Check(string name, string label, string tip) =>
        new() { Name = name, Label = label, Type = "checkbox", Tip = tip };

    private static SettingsFieldSchema Csv(string name, string label, string tip) =>
        new() { Name = name, Label = label, Type = "csv", Tip = tip };

    private static SettingsFieldSchema Number(
        string name, string label, string tip, double min, double max, double? step = null,
        double? recommended = null) =>
        new()
        {
            Name = name, Label = label, Type = "number", Tip = tip,
            Min = min, Max = max, Step = step, Recommended = recommended
        };

    private static SettingsFieldSchema Select(
        string name, string label, string tip, params (string Value, string Label)[] options) =>
        new()
        {
            Name = name,
            Label = label,
            Type = "select",
            Tip = tip,
            Options = options.Select(o => new SettingsFieldOption(o.Value, o.Label)).ToArray()
        };

    // The sections as written below. Sections - the one clients see - is this run through
    // WithDerivedFlags, which fills in the per-field flags that are read off the DTOs rather than
    // typed out by hand. Declared first, because static initializers run in textual order.
    private static readonly IReadOnlyList<SettingsSectionSchema> Declared =
    [
        new()
        {
            Key = "bridge",
            Title = "ALERT INTAKE & HISTORY",
            Group = SettingsGroups.Cameras,
            Endpoint = "/api/settings/bridge",
            Basic = true,
            DtoType = typeof(BridgeSettingsDto),
            Description = "How alerts get into this app, and how long history is kept. Everything that raises an alert goes through here - Blue Iris, and the cameras this app watches itself - so if Blue Iris alerts specifically stop arriving, check the API key below still matches what you entered in Blue Iris. Re-run setup wizard walks you through the essentials again without losing anything you've already configured.",
            Fields =
            [
                Password("apiKey", "API Key", "The secret every alert must carry to be accepted. Blue Iris has to be configured with this same key or its alerts are rejected; this app's own cameras use it automatically."),
                Number("maximumImageBytes", "Max Image Bytes", "Largest snapshot accepted with an alert. Anything bigger is refused instead of eating memory.", 1024, 104857600),
                Number("retentionDays", "Retention Days", "How long events and their images are kept before being cleaned up automatically.", 1, 3650),
                Number("unverifiedMotionRetentionDays", "Unverified Motion Days", "How long to keep motion the AI rejected as nothing, along with its clips. These are only useful for checking a camera's trigger zone is tuned right, and they use far more disk than confirmed events do. 0 keeps them forever.", 0, 365),
                Check("saveImages", "Save Images", "Keep a copy of each snapshot on disk. Turn off to record only the AI's text analysis."),
                Csv("allowedImageContentTypes", "Allowed Content Types (comma separated)", "Image formats accepted with an alert, e.g. image/jpeg. Anything else is rejected.")
            ],
            ExtraAction = new SettingsActionSchema(
                "setupReset",
                "re-run setup wizard",
                "Bring back the first-run setup wizard (vision backend, a camera, notifications) without losing anything already configured.",
                "/api/setup/reset",
                "POST") { ReloadAfter = true }
        },
        new()
        {
            Key = "ollama",
            Title = "OLLAMA",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/ollama",
            Basic = false,
            DtoType = typeof(OllamaSettingsDto),
            Description = "Tuning for the local AI that looks at your snapshots. The defaults are picked to keep descriptions literal and consistent, which is what you want for security alerts. You can ignore this section entirely unless answers are coming back wrong or too slowly.",
            Fields =
            [
                Text("model", "Model", "Which local vision model analyses the snapshots, e.g. gemma3:4b. It must already be pulled in Ollama."),
                Text("keepAlive", "Keep Alive", "How long Ollama keeps the model loaded in memory between alerts, e.g. 5m. Longer means faster replies but more RAM held."),
                Number("temperature", "Temperature", "How much the model is allowed to improvise. Low values keep descriptions consistent and literal, which is what you want here.", 0, 2, 0.1),
                Number("maximumOutputTokens", "Max Output Tokens", "Ceiling on the length of the model's reply. Too low and the analysis gets cut off mid-sentence.", 32, 4096),
                Number("topK", "Top K", "Limits the model to the K most likely next words. Lower is more predictable.", 1, 100),
                Number("topP", "Top P", "Another way to limit word choice, by probability mass rather than count. Lower is more predictable.", 0, 1, 0.05),
                Number("maximumRetries", "Max Retries", "How many times to retry when Ollama fails or returns something unparseable.", 0, 10),
                Number("retryDelaySeconds", "Retry Delay (s)", "How long to wait between those retries.", 1, 300)
            ]
        },
        new()
        {
            Key = "cloudVision",
            Title = "CLOUD VISION",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/cloud-vision",
            Basic = false,
            DtoType = typeof(CloudVisionSettingsDto),
            Description = "Use an outside AI service instead of, or as a backup to, the local model. It's faster and more accurate than a small local model, but it costs money per image and sends your snapshots off your network. Only needed if the local model isn't keeping up.",
            Fields =
            [
                Check("enabled", "Enabled", "Allow this cloud account to be used for alert analysis, either as the primary backend or as a fallback - see VISION ROUTING below to choose which."),
                Select("provider", "Provider", "Which cloud AI account the API key and model below belong to. 'OpenAI (or compatible)' also covers any other endpoint that speaks the same request format - Azure OpenAI, OpenRouter, Together.ai, Groq, or a self-hosted vLLM/LM Studio/LocalAI server - by changing CloudVision:BaseUrl in appsettings.json (needs a restart, same as Ollama's own address).",
                    ("openai", "OpenAI (or compatible)"),
                    ("anthropic", "Anthropic"),
                    ("google", "Google")),
                Password("apiKey", "API Key", "An API key for the provider selected above, with access to a vision-capable model."),
                Text("model", "Model", "Which model analyses the snapshots - must match the provider above, e.g. gpt-4o-mini (OpenAI), claude-3-5-sonnet-latest (Anthropic), or gemini-1.5-flash (Google)."),
                Number("maximumRetries", "Max Retries", "How many times to retry when the provider fails or returns something unparseable.", 0, 10),
                Number("retryDelaySeconds", "Retry Delay (s)", "How long to wait between those retries.", 1, 300)
            ]
        },
        new()
        {
            Key = "fastLocal",
            Title = "FAST LOCAL DETECTION",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/fast-local",
            Basic = false,
            DtoType = typeof(FastLocalSettingsDto),
            Description = "A small object detector that answers 'is a person, vehicle, or animal there?' in well under a second, even on a CPU-only machine that would take minutes to run Ollama's full model. It can't describe the scene, read gate state, or spot packages - pair it with escalation in VISION ROUTING below to get the full analysis only for alerts that actually contain something. The detector model (~100MB) downloads automatically the first time it's used.",
            Fields =
            [
                Number("scoreThreshold", "Score Threshold", "Minimum detection confidence for a subject to count, 0-1. Lower it if the detector misses distant or nighttime subjects; raise it if shadows and headlights are causing false detections.", 0, 1, 0.05, recommended: 0.5),
                Check("notifyOnPerson", "Notify On Person", "Recommend a notification when a person is detected."),
                Check("notifyOnVehicle", "Notify On Vehicle", "Recommend a notification when a vehicle is detected. Unlike the full AI, the detector can't tell passing road traffic from a vehicle at your gate - draw your trigger zones so the road isn't included, or turn this off for road-facing cameras."),
                Check("notifyOnAnimal", "Notify On Animal", "Recommend a notification when an animal is detected. Off by default - the detector can't judge which animals matter to you.")
            ]
        },
        new()
        {
            Key = "visionRouting",
            Title = "VISION ROUTING",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/vision-routing",
            Basic = false,
            DtoType = typeof(VisionRoutingSettingsDto),
            Description = "Which AI analyses a snapshot first, whether to fall back to another one if it fails, and - when Fast Local Detection is primary - whether alerts with something in them get escalated to a full AI for the detailed description.",
            Fields =
            [
                Select("primary", "Primary Backend", "Which backend analyses alerts first. Fast Local Detection is the right choice for hardware too slow to run Ollama's full model - pair it with escalation below so alerts with something in them still get a full description. If Cloud Vision is picked but not enabled/configured above, Ollama is used instead automatically. Gate/door open checks always use a full AI (cloud if configured, otherwise Ollama) - a plain detector can't read whether a gate is open.",
                    ("ollama", "Ollama (local)"),
                    ("cloud", "Cloud Vision (OpenAI)"),
                    ("fastlocal", "Fast Local Detection")),
                Check("fallbackEnabled", "Fall Back To Another Backend", "If the primary backend fails after its own retries, automatically retry once against another one before giving up - slower, but the alert still gets analyzed instead of being lost."),
                Check("escalationEnabled", "Escalate Fast Detections For Full Analysis", "Only applies when the primary is Fast Local Detection: when the detector finds a person, vehicle, or animal, re-analyze that alert with a full AI to get the detailed description (delivery company, gate state, animal species). Empty alerts - the vast majority - never reach the expensive model at all. If escalation fails, the fast result still stands."),
                Select("escalationTarget", "Escalation Target", "Which full AI escalated alerts go to. 'Cloud Vision only' never falls back to Ollama - the right choice on hardware where a surprise multi-minute local analysis is worse than a missing description.",
                    ("auto", "Auto (cloud if configured, else Ollama)"),
                    ("cloud", "Cloud Vision only"),
                    ("ollama", "Ollama (local)")),
                Check("escalationVerdictWins", "Full AI Has The Final Say", "What happens when the fast detector finds someone but the escalated full AI says the frame is empty. Off (the default), the detector is believed and the event is kept - one full AI shrugging at a dark night-vision frame can't hide real footage. On, the full AI is believed instead: no notification, and the event is filed as rejected so it drops off EVENTS unless you show hidden ones. Turn it on if you're getting a steady stream of events the full AI never confirmed. Either way, if the full AI is unreachable or errors out, the fast result still stands - that's a failure, not a verdict."),
                Number("minimumConfidence", "Full AI Minimum Confidence", "How sure the full AI must be before what it reports counts as a real subject, 0-1. Below this the alert is treated as empty: no notification, and the event is filed as rejected - though the description it wrote is still kept on the event. This is the full AI's counterpart to Fast Local Detection's Score Threshold; it doesn't affect the detector's own results or gate/door open checks. 0 turns it off. Note that with Fast Local Detection as the primary, this only changes the outcome when the setting above is also on - otherwise the detector's result wins anyway.", 0, 1, 0.05, recommended: 0)
            ]
        },
        new()
        {
            Key = "imageProcessing",
            Title = "IMAGE PROCESSING",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/image-processing",
            Basic = false,
            DtoType = typeof(ImageProcessingSettingsDto),
            Description = "Snapshots are shrunk before going to the AI, which is faster and usually just as accurate. Lower quality means quicker answers but a blurrier image for the model to read. Best left alone unless analysis is too slow or the AI is missing obvious things.",
            Fields =
            [
                Check("enabled", "Enabled", "Shrink snapshots before sending them to the AI. Usually faster with no real loss in accuracy."),
                Number("maxWidth", "Max Width", "Images wider than this are scaled down before analysis.", 256, 4096),
                Number("maxHeight", "Max Height", "Images taller than this are scaled down before analysis.", 256, 4096),
                Number("jpegQuality", "JPEG Quality", "Quality of the resized image. Lower means smaller and faster but blurrier for the model to read.", 1, 100),
                Check("autoOrient", "Auto Orient", "Rotate images upright based on their orientation tag, so the model isn't looking at a sideways scene.")
            ]
        },
        new()
        {
            Key = "email",
            Title = "EMAIL",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/email",
            Basic = true,
            DtoType = typeof(EmailSettingsDto),
            Description = "Emails you when something worth knowing about happens. Works with any SMTP provider - Gmail, Outlook, iCloud, Fastmail, your own mail server, or a sending service like SES or SendGrid. Most consumer providers need an app password here rather than your normal one. Only events the AI judges alert-worthy are sent, so you won't get a message every time a shadow moves.",
            Fields =
            [
                Check("enabled", "Enabled", "Send email when an alert is worth telling you about."),
                Text("smtpHost", "SMTP Host", "Outgoing mail server your provider gives you, e.g. smtp.gmail.com, smtp.office365.com, smtp.fastmail.com."),
                Number("smtpPort", "SMTP Port", "Port for the mail server. 587 is the usual one; 465 also works for providers that ask for it. Use whichever number your provider's own instructions give.", 1, 65535),
                Check("enableSsl", "Enable SSL", "Encrypt the connection to the mail server. Leave on. Turn it off only for a mail relay on your own network that has no certificate - and never on port 465, which is encrypted from the start."),
                Text("username", "Username", "Account used to log in to the mail server - usually your full email address, but some services use a key or a name instead. Leave blank for a relay on your own network that needs no login."),
                Password("appPassword", "Password", "The password for that account. Gmail, Outlook, iCloud and Yahoo all refuse your normal one here and want an app password generated in their security settings instead."),
                Text("fromAddress", "From Address", "Address the alert appears to come from. Required if the username above isn't itself an email address; otherwise it falls back to the username."),
                Text("fromName", "From Name", "Display name shown as the sender, e.g. Sentinel Core."),
                Text("toAddress", "To Address", "Where alerts are sent."),
                Number("timeoutSeconds", "Timeout (sec)", "How long to wait for the mail server before giving up on one message.", 5, 300),
                Number("duplicateCooldownMinutes", "Duplicate Cooldown (min)", "Suppress repeat emails about the same kind of event for this long, so one visitor doesn't fill your inbox.", 0, 1440),
                Number("minimumConfidence", "Minimum Confidence", "How sure the AI must be before emailing. Raise it if you get false alarms, lower it if real events slip through.", 0, 1, 0.05, recommended: 0.7),
                Check("attachImage", "Attach Image", "Include the snapshot in the email rather than text only.")
            ],
            ExtraAction = new SettingsActionSchema(
                "sendTestEmail",
                "send test email",
                "Send a plain test message to the To Address using the settings above, and say what the mail server answered. Save first - this tests what is saved, not what is currently typed in the boxes. It ignores Enabled and Minimum Confidence, so it sends even while email alerts are switched off.",
                "/api/notifications/test/email",
                "POST")
        },
        new()
        {
            Key = "dailySummary",
            Title = "DAILY SUMMARY",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/daily-summary",
            Basic = true,
            DtoType = typeof(DailySummarySettingsDto),
            Description = "A twice-daily email digest of everything the cameras saw. This is the “what did I miss” roundup rather than an urgent alert, and it arrives even on quiet days. Needs Email above to be working first.",
            Fields =
            [
                Check("enabled", "Enabled", "Email a digest of everything that happened, instead of only alerting on notable events."),
                Number("morningHour", "Morning Hour (0-23)", "Hour of day the morning digest is sent, in 24-hour time. 7 means 7am.", 0, 23),
                Number("eveningHour", "Evening Hour (0-23)", "Hour of day the evening digest is sent, in 24-hour time. 19 means 7pm.", 0, 23),
                Number("maxImagesAttached", "Max Images Attached", "Cap on how many snapshots ride along in the digest, to keep the email a sane size.", 0, 50)
            ]
        },
        new()
        {
            Key = "telegram",
            Title = "TELEGRAM ALERTS",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/telegram",
            Basic = true,
            DtoType = typeof(TelegramSettingsDto),
            Description = "Sends alerts to your phone through a Telegram bot - usually faster and harder to miss than email. You'll need to create a bot by messaging @BotFather in Telegram, then paste the token it gives you here.",
            Fields =
            [
                Check("enabled", "Enabled", "Push alerts to your phone through a Telegram bot."),
                Password("botToken", "Bot Token", "The token BotFather gave you when you created the bot."),
                Text("chatId", "Chat ID", "Which Telegram conversation to send to. This is the only chat the bot will talk to."),
                Check("onlyWhenNotificationRecommended", "Only When Notification Recommended", "Only message when the AI judges the event worth your attention, rather than for every single one."),
                Number("minimumConfidence", "Minimum Confidence", "How sure the AI must be before messaging you.", 0, 1, 0.05, recommended: 0.7),
                Check("attachImage", "Attach Image", "Send the snapshot with the message rather than text only."),
                Number("duplicateCooldownMinutes", "Duplicate Cooldown (min)", "Suppress repeat Telegram messages about the same kind of event for this long, so one vehicle sitting at the gate doesn't message you over and over. 0 disables this.", 0, 1440)
            ],
            ExtraAction = new SettingsActionSchema(
                "sendTestTelegram",
                "send test message",
                "Send a test message to the Chat ID using the token above, and show what Telegram said if it refuses - which is how a wrong chat ID or a bot that was never started tells you so. Save first - this tests what is saved, not what is currently typed in the boxes. It ignores Enabled and Minimum Confidence, so it sends even while Telegram alerts are switched off.",
                "/api/notifications/test/telegram",
                "POST")
        },
        new()
        {
            Key = "telegramChat",
            Title = "TELEGRAM CHAT",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/telegram-chat",
            Basic = false,
            DtoType = typeof(TelegramChatSettingsDto),
            Description = "Lets you text the bot questions like “what happened last night?” and get an answer from your camera history. This also powers the ask box on the Feed tab, so turning it off here turns off both.",
            Fields =
            [
                Check("enabled", "Enabled", "Let you message the bot and ask questions about recent gate activity."),
                Number("lookbackDays", "Lookback Days", "How far back the bot looks when answering a question.", 1, 30),
                Number("maxEventsInContext", "Max Events In Context", "How many events the bot can consider at once. Higher gives fuller answers but is slower.", 1, 500),
                Text("model", "Model", "Which Ollama model answers your questions. Can differ from the vision model used for images.")
            ]
        },
        new()
        {
            Key = "discord",
            Title = "DISCORD ALERTS",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/discord",
            Basic = true,
            DtoType = typeof(DiscordSettingsDto),
            Description = "Posts alert cards and snapshots to a Discord channel using an incoming webhook. In Discord, open the channel settings, choose Integrations, create a Webhook, and paste its URL here. No bot application or public server is needed.",
            Fields =
            [
                Check("enabled", "Enabled", "Post camera alerts to a Discord channel."),
                Password("webhookUrl", "Webhook URL", "The Discord channel webhook URL. Treat it like a password: anyone with it can post to that channel."),
                Check("onlyWhenNotificationRecommended", "Only When Notification Recommended", "Only post when the AI judges the event worth your attention."),
                Number("minimumConfidence", "Minimum Confidence", "How sure the AI must be before posting the alert.", 0, 1, 0.05, recommended: 0.7),
                Check("attachImage", "Attach Image", "Include the trigger snapshot in the Discord alert card."),
                Number("duplicateCooldownMinutes", "Duplicate Cooldown (min)", "Suppress repeat posts for the same camera and event category during this window. 0 disables this.", 0, 1440)
            ]
        },
        new()
        {
            Key = "blueIris",
            Title = "BLUE IRIS MEDIA",
            Group = SettingsGroups.Cameras,
            Endpoint = "/api/settings/blue-iris",
            Basic = false,
            DtoType = typeof(BlueIrisSettingsDto),
            Description = "Connects Sentinel to Blue Iris for on-demand live snapshots (used by the Telegram bot's /photo command). Sentinel's own Timeline uses its own recorded trigger clips instead and does not need Blue Iris to be running.",
            Fields =
            [
                Check("enabled", "Enabled", "Allow on-demand snapshots through Blue Iris."),
                Text("baseUrl", "Blue Iris URL", "The Blue Iris web server URL reachable from Sentinel Core, including its port."),
                Text("camera", "Camera Short Name", "The camera's short name in Blue Iris, e.g. gateCamera. Must match exactly."),
                Text("username", "Username", "Blue Iris login used to request the snapshot."),
                Password("password", "Password", "Password for the Blue Iris account used by Core; it is never sent to a mobile client.")
            ]
        },
        new()
        {
            Key = "multiFrameAnalysis",
            Title = "MULTI-FRAME ANALYSIS",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/multi-frame-analysis",
            Basic = false,
            DtoType = typeof(MultiFrameAnalysisSettingsDto),
            Description = "Off by default. When on, a trigger zone that sends to the alert pipeline captures 2-3 frames a fraction of a second apart instead of one, and the AI is told to use consistency across them to ignore a shadow, reflection, headlight sweep, or insect near the lens that only shows up in a single frame. Only applies to your own added cameras' trigger zones - never Blue Iris alerts, which always send one image. This is a real cost, not just an extra option: it multiplies AI processing time per alert, and a single image can already be a meaningful load on modest local hardware - leave this off unless a single image already analyzes comfortably fast for you.",
            Fields =
            [
                Check("enabled", "Enabled", "Capture and analyze multiple frames per trigger instead of one. Increases AI processing time/cost per alert."),
                Number("extraFrameCount", "Extra Frames (1-2)", "How many additional frames to capture beyond the one at the trigger moment. Total frames sent to the AI = 1 + this.", 1, 2),
                Number("frameIntervalMs", "Frame Interval (ms)", "How far apart, in milliseconds, each captured frame is.", 150, 1000)
            ]
        },
        new()
        {
            Key = "triggerRewind",
            Title = "TRIGGER REWIND",
            Group = SettingsGroups.Cameras,
            Endpoint = "/api/settings/trigger-rewind",
            Basic = false,
            DtoType = typeof(TriggerRewindSettingsDto),
            Description = "Off by default. Motion is spotted on a deliberately tiny copy of the video - full-resolution frames twice a second would cost more CPU than everything else put together - so the moment is known but the picture is not, and a fresh photo has to be taken afterwards. On a busy machine that photo can be seconds too late to still show anything: a car drives past and the saved image is of an empty road. With this on, the full-resolution frame for that exact moment is fetched out of the recent video instead, and it is both what you see and what the AI is given. The frame is looked up by its timestamp, so there is nothing to tune and it behaves the same on fast hardware and slow.",
            Fields =
            [
                Check("enabled", "Enabled", "Use the frame the motion was really in, instead of photographing the scene again once the trigger has been handled. If the frame cannot be fetched for any reason the old live snapshot is used, so a trigger is never lost."),
                Number("lookbackSeconds", "Lookback (s)", "How far back a fetch may reach. This is not a search window - the moment wanted is known exactly - it is only a sanity limit, so a camera reconnecting or a clock jumping can never present footage from minutes ago as the current event. The default suits everything; raise it only if the log says a frame was refused for being older than this.", 2, 15),
                Check("bufferEveryCamera", "Keep Footage For Every Camera", "Fetching needs a few seconds of recent video on disk - the same rolling buffer clip recording already keeps. On, every camera with a trigger zone keeps one, so this works everywhere instead of only where clips happen to be enabled. The buffer is copied, never re-encoded, so it costs almost no CPU - but it is a continuous write at the camera's full bitrate, which is worth weighing on a small or heavily-worn SSD."),
                Check("applyToCompanions", "Apply To Companion Cameras", "Fetch the same moment from the other cameras in a camera group. They have no motion of their own to date the event by, so they borrow the triggering camera's timing - an approximation, but a much better one than a picture taken after the event.")
            ]
        },
        new()
        {
            Key = "scheduledRecording",
            Title = "SCHEDULED RECORDING",
            Group = SettingsGroups.Cameras,
            Endpoint = "/api/settings/scheduled-recording",
            Basic = false,
            DtoType = typeof(ScheduledRecordingSettingsDto),
            Description = "Controls the recordings made by the timed windows set up on each camera's trigger screen ('record the driveway from 8pm to 8:30pm every Tuesday'). These are the largest files this app produces by a wide margin - full-bitrate main-stream video, where an hour is roughly what a month of trigger clips costs - so both settings here exist to stop them quietly filling the disk. Neither affects trigger clips or alert history, which age out under their own separate settings.",
            Fields =
            [
                Number("retentionDays", "Keep Recordings For (days)", "How long a finished recording is kept before it is deleted. Much shorter than alert history on purpose, because of the size involved - raise it only if you have the space to spare.", 1, 365),
                Number("minimumFreeGigabytes", "Stop Below Free Space (GB)", "A recording will not start, and a running one stops itself, when free disk space falls below this. The point is that the rest of the system keeps working: the databases, trigger clips and alert pipeline all need somewhere to write, and they fail far less visibly than a recording that says it stopped early.", 1, 500)
            ]
        },
        new()
        {
            Key = "packageTheftWatch",
            Title = "PACKAGE THEFT WATCH",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/package-theft-watch",
            Basic = false,
            DtoType = typeof(PackageTheftWatchSettingsDto),
            Description = "Off by default. When on, an alert showing a package delivered or already present starts a watch on that camera. If a later alert on the same camera shows the package gone and whoever's there isn't a recognized household face or vehicle, it's flagged as suspected theft and forced to notify even if it otherwise wouldn't have. This adds no AI processing cost - it only reuses package/face/vehicle detection your other settings already produce - but it's a heuristic guess, not proof: a stranger picking it up isn't necessarily a thief, and this can only catch a removal that itself trips a new motion alert on that camera. A package taken with no interceding motion event won't be caught.",
            Fields =
            [
                Check("enabled", "Enabled", "Watch for packages disappearing without a recognized pickup, and force a notification when suspected."),
                Number("watchWindowHours", "Watch Window (hours)", "How long after a package is seen delivered the watch stays open. Most legitimate pickups happen same-day.", 1, 72)
            ]
        },
        new()
        {
            Key = "repeatAlerts",
            Title = "REPEAT ALERTS FROM ONE CAMERA",
            Group = SettingsGroups.Cameras,
            Endpoint = "/api/settings/repeat-alerts",
            Basic = false,
            DtoType = typeof(RepeatAlertsSettingsDto),
            Description = "What happens when one camera fires again within seconds of the last time - which is what somebody arriving actually looks like, not a fault. The vehicle stops outside, pulls in, a door opens, someone walks to the gate: four triggers, one arrival. Each of those frames is analyzed one at a time and each takes as long as an analysis takes, so without these two the last frame - usually the interesting one, because it's the one with a person in it - waits behind every earlier near-duplicate before anyone hears about it. Neither setting deletes anything: every trigger, clip and Feed entry is kept exactly as before, and these only decide what is worth spending an analysis on and what is worth interrupting you for.",
            Fields =
            [
                Check("supersedeQueued", "Skip Stale Frames In The Queue", "When a camera sends a new frame while an earlier one from that same camera is still waiting to be analyzed, analyze the new one and stand the old one down. The frame being dropped is seconds older than the one replacing it and its video is kept either way, so this costs nothing and gets the verdict on what's happening now minutes sooner. An analysis already underway is never interrupted."),
                Number("duplicateNotificationWindowSeconds", "Only Re-Notify On A Change Within (s)", "Inside this window, the same camera notifies again only if what's actually in frame has changed - a person appearing beside the truck is worth telling you about, the truck having moved twenty feet is not. Set to 0 to notify on every analysis, the way it worked before, and leave the per-channel duplicate cooldowns to it.", 0, 3600)
            ]
        },
        new()
        {
            Key = "semanticSearch",
            Title = "SEMANTIC SEARCH",
            Group = SettingsGroups.Ai,
            Endpoint = "/api/settings/semantic-search",
            Basic = false,
            DtoType = typeof(SemanticSearchSettingsDto),
            Description = """
                Off by default. When on, each alert's image is run through a local CLIP model that turns it into a vector capturing what's actually in the photo, so the Feed's search box can find events by visual content ("red truck", "person in a blue jacket") instead of only matching words already in the AI's saved description. This downloads a ~577MB model to this machine the first time it's used, and adds a per-alert cost similar to Face Recognition - a real cost on modest hardware, not just an extra toggle. Searching itself stays fast regardless of history size, since only the one-time-per-alert encoding is expensive.
                """,
            Fields =
            [
                Check("enabled", "Enabled", "Encode each alert's image for visual search. Downloads a one-time ~577MB model on first use and adds per-alert processing time."),
                Number("minimumSimilarity", "Minimum Similarity", "How close a visual match must be to appear in results. Lower finds more (including weaker matches); higher shows only close matches.", 0, 1, 0.01)
            ]
        },
        new()
        {
            Key = "selfMonitoring",
            Title = "SELF-MONITORING",
            Group = SettingsGroups.Health,
            Endpoint = "/api/settings/self-monitoring",
            Basic = false,
            DtoType = typeof(SelfMonitoringSettingsDto),
            Description = "Watches the app's own health and warns you if it stops keeping up - the AI going unresponsive, or alerts backing up faster than they're processed. The defaults are sensible; these numbers decide how quickly it calls something genuinely broken rather than briefly slow.",
            Fields =
            [
                Check("enabled", "Enabled", "Watch the bridge's own health and tell you if it stops working properly."),
                Number("checkIntervalSeconds", "Check Interval (s)", "How often to run the health check.", 10, 3600),
                Number("consecutiveFailuresBeforeAlert", "Consecutive Failures Before Alert", "How many checks must fail in a row before you're told, so one hiccup doesn't wake you.", 1, 20),
                Number("queueBacklogThreshold", "Queue Backlog Threshold", "How many unprocessed alerts count as falling behind. Usually means the AI can't keep up.", 1, 10000),
                Number("consecutiveBacklogChecksBeforeAlert", "Consecutive Backlog Checks Before Alert", "How many checks in a row must show a backlog before alerting, ignoring brief bursts.", 1, 20),
                Check("notifyOnRecovery", "Notify On Recovery", "Also tell you when things go back to normal, not just when they break.")
            ]
        },
        new()
        {
            Key = "startupNotification",
            // "SERVER" is doing real work in this title: INTERNET OUTAGE ALERTS sits directly
            // below it, both are basic, and "OUTAGE ALERTS" on its own gave no way to tell which
            // outage each one meant.
            Title = "SERVER OUTAGE ALERTS",
            Group = SettingsGroups.Health,
            Endpoint = "/api/settings/startup-notification",
            Basic = true,
            DtoType = typeof(StartupNotificationSettingsDto),
            Description = "Emails you when the server comes back after being off - so a power cut doesn't go unnoticed until you next check the feed. The email goes to the same address as your alerts, and says how long it was down and whether it was shut down on purpose or lost power. Note this can only tell you it came back: if the machine stays off, nothing is running to send the email.",
            Fields =
            [
                Check("enabled", "Enabled", "Email you on startup when the server has been down longer than the threshold below."),
                Number("minimumDowntimeMinutes", "Minimum Downtime (min)", "Ignore outages shorter than this. Stops routine restarts and updates, which take seconds, from emailing you every time.", 1, 1440),
                Number("heartbeatIntervalSeconds", "Heartbeat Interval (s)", "How often the server records that it's alive. Also how precise the reported downtime is - the real outage can be up to this much longer than the email says.", 10, 3600)
            ]
        },
        new()
        {
            Key = "internetOutage",
            Title = "INTERNET OUTAGE ALERTS",
            Group = SettingsGroups.Health,
            Endpoint = "/api/settings/internet-outage",
            Basic = true,
            DtoType = typeof(InternetOutageSettingsDto),
            Description = "Off by default. When on, the server checks every so often that it can still reach the internet, and tells you once the connection comes back — by email and Telegram, with how long you were offline. Like the outage alerts above, it can only report this afterwards: while the line is down nothing can be sent to anyone. Leave this off if this machine is deliberately kept off the internet.",
            Fields =
            [
                Check("enabled", "Enabled", "Watch the internet connection and tell you, once it's back, how long it was down."),
                Number("checkIntervalSeconds", "Check Interval (s)", "How often to check that the internet is reachable.", 10, 3600),
                Number("consecutiveFailuresBeforeAlert", "Consecutive Failures Before Outage", "How many checks must fail in a row before it counts as a real outage, so a momentary blip is ignored.", 1, 20),
                Number("minimumOutageMinutes", "Minimum Outage (min)", "Ignore outages shorter than this, so a brief drop while you were asleep doesn't notify you.", 1, 1440),
                Number("probeTimeoutSeconds", "Probe Timeout (s)", "How long to wait for each address to answer before treating it as unreachable. Every address has to time out before a check counts as failed, so keep this times the number of addresses well under the check interval.", 1, 60),
                Csv("probeUrls", "Probe Addresses (comma separated)", "Addresses used to prove the connection is alive. Any reply at all counts, and only failing every one of them counts as an outage. Leave empty to use the built-in list.")
            ]
        },
        new()
        {
            Key = "location",
            Title = "LOCATION",
            Group = SettingsGroups.Automation,
            Endpoint = "/api/settings/location",
            Basic = true,
            DtoType = typeof(LocationSettingsDto),
            Description = "Where this server is, used for one thing only: working out when the sun rises and sets here, so a schedule can fire at sunset or \"when it gets dark\" instead of at a fixed clock time that drifts out of step through the year. Nothing is sent anywhere - the calculation happens on this machine. You can type a town to fill the coordinates in, or enter them yourself; only the numbers are saved.",
            Fields =
            [
                Text("label", "Town or City", "A place name to look up, e.g. \"Louisville, KY\". Filling this in and pressing Find sets the two coordinates below. Only ever a convenience - entering the coordinates directly works just as well, and is the only option on a server with no internet access.",
                    new SettingsLookupSchema(
                        "find",
                        "Look this place up and fill in the coordinates below. This is the one part of the app that asks anything of the internet, and only when pressed - if this server has no route out, type the coordinates yourself instead.",
                        "/api/settings/location/lookup")),
                Number("latitude", "Latitude", "Degrees north of the equator, negative for south. Leave this and longitude at 0 if you would rather not set a location - schedules that follow the sun simply won't run.", -90, 90, 0.0001),
                Number("longitude", "Longitude", "Degrees east of Greenwich, negative for west.", -180, 180, 0.0001)
            ]
        },
        new()
        {
            Key = "schedules",
            Title = "SCHEDULES",
            Group = SettingsGroups.Automation,
            Endpoint = "/api/settings/schedules",
            Basic = true,
            DtoType = typeof(ScheduleSettingsDto),
            Description = "How the scheduler behaves as a whole. The schedules themselves are on the Automations tab - this is only the handful of settings that apply to all of them.",
            Fields =
            [
                Check("enabled", "Enabled", "Run schedules at their appointed times. Turning this off stops every schedule at once without having to disable them one by one."),
                Number("maximumCatchUpMinutes", "Catch-Up Limit (min)", "For schedules set to catch up on a firing they missed while the server was off: how late is still worth running. Beyond this the run is recorded as missed instead, so a coop door doesn't close at breakfast because the machine was down overnight.", 1, 1440),
                Text("timeZone", "Time Zone", "Leave empty to use this machine's own time zone, which is almost always right. Set an IANA id such as America/New_York only if the server's clock runs on UTC but schedules should follow local time.")
            ]
        },
        new()
        {
            Key = "quietHours",
            Title = "QUIET HOURS",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/quiet-hours",
            Basic = true,
            DtoType = typeof(QuietHoursSettingsDto),
            Description = "During hours you choose, the AI has to be more confident before it wakes you. It never fully silences anything - something clearly worth knowing about still gets through at 3am - and it has no effect on the Feed or the daily summary.",
            Fields =
            [
                Check("enabled", "Enabled", "During the window below, raise the confidence bar for Email and Telegram alerts so only confident detections still ping you. Nothing is ever fully muted, and the Feed and daily digest are unaffected."),
                Number("startHour", "Start Hour (0-23)", "Hour the quiet window begins, in 24-hour time. 22 means 10pm.", 0, 23),
                Number("endHour", "End Hour (0-23)", "Hour the quiet window ends, in 24-hour time. 6 means 6am. Can be earlier than Start Hour to span midnight, e.g. 22 to 6.", 0, 23),
                Number("minimumConfidence", "Minimum Confidence During Window", "How sure the AI must be to still alert during quiet hours. Only used if it's higher than the channel's own Minimum Confidence in Email/Telegram settings - it can raise the bar, never lower it.", 0, 1, 0.05)
            ]
        },
        new()
        {
            Key = "webhook",
            Title = "WEBHOOK",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/webhook",
            Basic = false,
            DtoType = typeof(WebhookSettingsDto),
            Description = "Sends each analysed event to another system as JSON, for home automation, logging, or anything you're building yourself. Only useful if you have somewhere specific to send it.",
            Fields =
            [
                Check("enabled", "Enabled", "POST each analysed event to another system, for home automation or logging."),
                Text("url", "URL", "Address the event data is posted to."),
                Text("apiKeyHeaderName", "API Key Header Name", "Name of the header the key is sent in, e.g. X-API-Key. Whatever the receiving end expects."),
                Password("apiKey", "API Key", "Secret sent in that header so the receiving end knows it's you."),
                Check("onlyWhenNotificationRecommended", "Only When Notification Recommended", "Only post events the AI flagged as notable, rather than every one.")
            ],
            ExtraAction = new SettingsActionSchema(
                "sendTestWebhook",
                "send test webhook",
                "POST one test event to the URL above, in exactly the shape real alerts arrive in, and show what came back - which is how a wrong address, a rejected key or an endpoint that never answers tells you so. Save first - this tests what is saved, not what is currently typed in the boxes. It ignores Enabled, so it posts even while the webhook is switched off, and whatever is listening receives a real event it may act on.",
                "/api/notifications/test/webhook",
                "POST")
        },
        new()
        {
            Key = "mqtt",
            Title = "MQTT / HOME ASSISTANT",
            Group = SettingsGroups.Automation,
            Endpoint = "/api/settings/mqtt",
            Basic = false,
            DtoType = typeof(MqttSettingsDto),
            Description = "Publishes events over MQTT so Home Assistant, or anything else that speaks MQTT, can see your cameras. Home Assistant picks them up automatically with no manual YAML. If you don't already run a broker this app can be one; if you don't use Home Assistant, skip this.",
            Fields =
            [
                Check("enabled", "Enabled", "Publish every analysed event over MQTT, with Home Assistant auto-discovery so cameras show up as devices with no manual setup on the Home Assistant side."),
                Check("runEmbeddedBroker", "Run Built-In Broker", "Have this app act as its own MQTT broker on the port below, so you don't need a separate one (like Mosquitto) running anywhere. Leave off to connect to a broker you already have - e.g. Home Assistant's own Mosquitto add-on. Applies immediately when saved - no restart needed."),
                Number("embeddedBrokerPort", "Built-In Broker Port", "Port the built-in broker listens on, if enabled above. Point other MQTT clients (including this app's own publisher) at this host on this port. Changing it moves the broker immediately - other clients pointed at the old port will need updating.", 1, 65535),
                Text("brokerHost", "Broker Host", "Address of the MQTT broker to connect to. Ignored if Run Built-In Broker is on - this app connects to its own broker instead."),
                Number("brokerPort", "Broker Port", "Port of the external broker. 1883 is the standard unencrypted MQTT port.", 1, 65535),
                Text("username", "Username", "Leave blank if the broker allows anonymous connections (Mosquitto's default is often anonymous on a LAN)."),
                Password("password", "Password", "Only needed if the broker requires a username above."),
                Check("useTls", "Use TLS", "Encrypt the connection to an external broker. Leave off for a local/LAN broker without TLS configured."),
                Text("topicPrefix", "Topic Prefix", "Root of every topic this app publishes, e.g. 'sentinelcore' produces sentinelcore/gateCamera/event/state."),
                Check("homeAssistantDiscovery", "Home Assistant Discovery", "Automatically publish Home Assistant's MQTT discovery config so each camera appears as a device with its own entities - no manual YAML or UI entity setup needed."),
                Text("homeAssistantDiscoveryPrefix", "Discovery Prefix", "Must match the discovery prefix configured in Home Assistant's own MQTT integration. 'homeassistant' is Home Assistant's default - only change this if you changed it there too."),
                Check("onlyWhenNotificationRecommended", "Only When Notification Recommended", "Only publish events the AI flagged as notable. Leave off if you want every event in Home Assistant to build your own history or automations from.")
            ]
        },
        new()
        {
            Key = "deviceHub",
            Title = "DEVICE HUB",
            Group = SettingsGroups.Automation,
            Endpoint = "/api/settings/device-hub",
            Basic = false,
            DtoType = typeof(DeviceHubSettingsDto),
            Description = "Turns the MQTT connection above into two-way control: a DEVICE HUB tab appears where you can switch lights, plugs, fans, and anything else on your broker on and off, from here or from the phone, Mac, and Apple TV apps. Requires the MQTT section above to be turned on first - that's where the broker is configured. Use the tab's discover button to find what's already on your broker rather than typing topics by hand.",
            Fields =
            [
                Check("enabled", "Enabled", "Show the DEVICE HUB tab and start watching your devices' MQTT topics. Requires MQTT above to be enabled - saving this on with MQTT off is refused, since there'd be no broker to talk to."),
                Number("stateTimeoutSeconds", "State Timeout (s)", "For devices with no Availability Topic only: how long one can go silent before it's shown as unavailable. A device that does have an Availability Topic ignores this entirely - what that topic says is trusted instead, so a lamp that's simply been on all day is never wrongly marked offline.", 10, 86400),
                Number("discoverySeconds", "Discovery Scan Seconds", "How long the discover button listens to the broker. Devices that announce themselves for Home Assistant appear instantly; chattier ones need long enough to say something, so raise this if a device you know is there doesn't turn up.", 2, 60),
                Number("commandTimeoutSeconds", "Command Confirm Timeout (s)", "How long to wait for a device to confirm a command before the tile says 'not confirmed'. The command is still sent either way - this only affects how long the UI waits to hear back.", 1, 60)
            ]
        },
        new()
        {
            Key = "pushNotifications",
            Title = "PUSH NOTIFICATIONS",
            Group = SettingsGroups.Alerts,
            Endpoint = "/api/settings/push-notifications",
            Basic = true,
            DtoType = typeof(PushNotificationsSettingsDto),
            Description = "Native push alerts to the Sentinel Core apps on iPhone, iPad, Mac, Apple TV, and Android - the banner-on-your-lock-screen kind, not email or Telegram. Requires developer credentials for the platform you're pushing to: an Apple Developer key for the Apple apps, a Firebase service account for Android. Each device also has to opt in from its own app's Settings screen (Enable Notifications) before anything reaches it.",
            Fields =
            [
                Check("enabled", "Enabled", "Send native push notifications to registered phones, tablets, and TVs. The delivery services below still need their own credentials and their own Enabled switch."),
                Check("onlyWhenNotificationRecommended", "Only When Notification Recommended", "Only push when the AI judges the event worth your attention, rather than for every single one."),
                Number("minimumConfidence", "Minimum Confidence", "How sure the AI must be before pushing. Raise it if you get false alarms, lower it if real events slip through.", 0, 1, 0.05, recommended: 0.7),
                Number("duplicateCooldownMinutes", "Duplicate Cooldown (min)", "Suppress repeat pushes about the same kind of event for this long, so one visitor doesn't buzz your phone over and over. 0 disables this.", 0, 1440),
                Check("apple.enabled", "Apple: Enabled", "Deliver to iPhone, iPad, Mac, and Apple TV through Apple's push service. Needs the key details below from your Apple Developer account."),
                Text("apple.teamId", "Apple: Team ID", "Your Apple Developer Team ID - the 10-character code shown under Membership Details at developer.apple.com."),
                Text("apple.keyId", "Apple: Key ID", "The ID of the APNs auth key (.p8) you created under Certificates, Identifiers & Profiles → Keys."),
                Text("apple.bundleId", "Apple: Bundle ID", "The iOS/macOS app's bundle identifier the pushes are addressed to. Only change this if you built the app yourself under a different identifier."),
                Text("apple.tvBundleId", "Apple: TV Bundle ID", "The Apple TV app's own bundle identifier, if you use it - APNs requires the exact receiving app's ID, so the TV app can't reuse the one above. Leave blank if you don't push to Apple TVs."),
                Text("apple.privateKeyPath", "Apple: Private Key Path", "Path on the server to the APNs auth key file (.p8) downloaded from Apple. The file itself is the secret - keep it readable by this service only."),
                Check("apple.useSandbox", "Apple: Use Sandbox", "Send through Apple's sandbox push environment. On for apps installed via Xcode/development builds; off for TestFlight or App Store builds."),
                Check("firebase.enabled", "Firebase: Enabled", "Deliver to Android devices through Firebase Cloud Messaging. Needs the project details below from your Firebase console."),
                Text("firebase.projectId", "Firebase: Project ID", "The Firebase project ID from your project's settings page in the Firebase console."),
                Text("firebase.serviceAccountPath", "Firebase: Service Account Path", "Path on the server to the Firebase service-account JSON file. Like the Apple key, the file itself is the secret - keep it readable by this service only.")
            ]
        },
        new()
        {
            Key = "logging",
            Title = "LOGGING",
            Group = SettingsGroups.Health,
            Endpoint = "/api/settings/logging",
            Basic = false,
            DtoType = typeof(LoggingSettingsDto),
            Description = "How much detail the server writes to its own log. Information is the right day-to-day level; drop to Debug only while chasing a specific problem, since it's noisy and makes the log large. Changes apply immediately, no restart needed.",
            Fields =
            [
                Select("default", "Default Level", "Log detail for the app's own components. Debug shows everything including per-alert processing steps; Warning and above keeps the log nearly silent unless something is wrong.",
                    ("Trace", "Trace (most detail)"),
                    ("Debug", "Debug"),
                    ("Information", "Information (default)"),
                    ("Warning", "Warning"),
                    ("Error", "Error"),
                    ("Critical", "Critical"),
                    ("None", "None (silent)")),
                Select("microsoftAspNetCore", "ASP.NET Core Level", "Log detail for the web framework itself - request handling, routing, connections. Kept at Warning by default because Information logs every single HTTP request.",
                    ("Trace", "Trace (most detail)"),
                    ("Debug", "Debug"),
                    ("Information", "Information"),
                    ("Warning", "Warning (default)"),
                    ("Error", "Error"),
                    ("Critical", "Critical"),
                    ("None", "None (silent)"))
            ]
        },
        new()
        {
            Key = "server",
            Title = "SERVER ADDRESS",
            Group = SettingsGroups.Access,
            Endpoint = "/api/settings/server",
            Basic = false,
            DtoType = typeof(ServerSettingsDto),
            Description = "The address and port the main bridge service listens on. Get this wrong and the service comes back unreachable after its next restart - fixable only by editing appsettings.json on the server itself, so double-check before saving. Takes effect on restart.",
            Fields =
            [
                new()
                {
                    Name = "urls",
                    Label = "Listen Address(es)",
                    Type = "text",
                    Tip = "Where the bridge listens for connections, e.g. http://0.0.0.0:5080. 0.0.0.0 means every network interface on the machine; 127.0.0.1 means this machine only. Multiple addresses can be separated with semicolons.",
                    RequiresRestart = true
                }
            ]
        },
        new()
        {
            Key = "internetExposed",
            Title = "INTERNET EXPOSED MODE",
            Group = SettingsGroups.Access,
            Endpoint = "/api/settings/internet-exposed",
            // Not in the bulk snapshot, and not the bridge's port to rebind - see the two
            // properties' own notes above. Both exceptions are declared rather than special-cased
            // in any client.
            DataEndpoint = "/api/settings/internet-exposed",
            RestartService = "dashboard",
            Basic = false,
            DtoType = typeof(InternetExposedSettingsDto),
            Description = "For reaching this dashboard from outside your home network - e.g. through a Cloudflare Tunnel. When enabled, this server listens on a SECOND port that always requires login and only accepts HTTPS connections, no matter how Login and HTTPS are configured. The normal port for local network use is completely unaffected by this - point your tunnel or reverse proxy at the new port instead, not the regular one. Requires local HTTPS to already be configured (see docs/DEPLOYMENT.md §14a) and at least one account to already exist. Takes effect after a restart.",
            Fields =
            [
                new()
                {
                    Name = "enabled",
                    Label = "Enabled",
                    Type = "checkbox",
                    Tip = "Turn on the dedicated internet-exposed port. Requires HTTPS to already be configured and at least one account to exist. Needs a restart to take effect - use the restart button below.",
                    RequiresRestart = true
                },
                new()
                {
                    Name = "port",
                    Label = "Port",
                    Type = "number",
                    Tip = "Port for internet-facing traffic. Point your Cloudflare Tunnel (or other reverse proxy) here instead of the normal port. Must be different from the HTTPS port. Needs a restart to take effect - use the restart button below.",
                    Min = 1,
                    Max = 65535,
                    RequiresRestart = true
                }
            ]
        },
        new()
        {
            Key = "bugReports",
            Title = "BUG REPORTS",
            Group = SettingsGroups.Health,
            Endpoint = "/api/settings/bug-reports",
            Basic = true,
            DtoType = typeof(BugReportSettingsDto),
            Description = "Emails the developer when something breaks inside the app, using the email account configured above. On by default, so bugs get reported instead of going unnoticed - turn it off if you'd rather nothing left the machine. Only the error text and stack trace are sent, never images or clips.",
            Fields =
            [
                Check("enabled", "Enabled", "Email the developer when something in the bridge or dashboard hits an internal error, using the same account configured under Email above. Goes to a fixed developer address, not one you set - there's nothing else to configure here.")
            ]
        },
        new()
        {
            Key = "loginAlerts",
            Title = "LOGIN ALERTS",
            Group = SettingsGroups.Access,
            Endpoint = "/api/settings/login-alerts",
            Basic = true,
            DtoType = typeof(LoginAlertSettingsDto),
            Description = "Emails and/or Telegrams you when someone enters the wrong password for this dashboard, using the accounts configured under Email and Telegram above. Off by default. One alert is sent per source IP per cooldown window, no matter how many usernames or passwords are tried against it, so a brute-force burst doesn't flood you.",
            Fields =
            [
                Check("enabled", "Enabled", "Alert you by Email and/or Telegram when someone fails to log in to this dashboard, using the accounts configured under Email and Telegram above."),
                Number("cooldownMinutes", "Cooldown (min)", "After alerting on a failed login from a given address, suppress further alerts from that same address for this many minutes - so one attacker hammering the login page sends one alert, not one per attempt.", 1, 1440)
            ]
        },
        new()
        {
            Key = "nuisanceAdvisor",
            Title = "SPIDER WEB & INSECT ADVISOR",
            Group = SettingsGroups.Health,
            Endpoint = "/api/settings/nuisance-advisor",
            Basic = true,
            DtoType = typeof(NuisanceAdvisorSettingsDto),
            Description = "Tells you when a camera has spent the night triggering on a spider web or on insects in front of the lens, using the accounts configured under Email and Telegram above. A camera's own infrared light draws moths, moths draw spiders, and both sit close enough to the lens to be huge, white, out of focus, and never quite still - so motion fires all night on an empty driveway. No setting fixes it, because at that distance a strand of silk covers about as many pixels as a person at the gate does; the fix is physical, and this tells you when it's worth going out with a broom. Checks once each morning for the night just gone, and only speaks up when a camera was busy after dark, quiet in daylight, and the AI found nothing real in almost all of it.",
            Fields =
            [
                Check("enabled", "Enabled", "Email and/or Telegram you when a camera's overnight triggers look like a spider web or insects on the lens rather than anything real."),
                Number("minimumNightTriggers", "Triggers to speak up (per night)", "How many times a camera has to trigger between dusk and dawn before the night is worth mentioning at all. Below this it's just a night, whatever caused it.", 10, 5000, recommended: 60),
                Number("maximumConfirmedPercent", "Most that may be real (%)", "If the AI confirmed more than this share of the night's triggers as a person, vehicle, package, or animal, something was genuinely happening and nothing is sent. A camera behind a web typically confirms none at all.", 0, 100, recommended: 10),
                Number("minimumNightDayRatio", "Night-to-day busyness", "How many times busier the dark hours must be than the daylight either side. This is what stops a camera pointed at a busy road - which triggers all day and wants its zone redrawn, not a broom - from being blamed on spiders.", 1, 50, recommended: 3),
                Number("cooldownDays", "Remind again after (days)", "A web that isn't dealt with is still there tomorrow, so this repeats - but no more often than this, per camera. Clear the web on the morning you're told and you'll hear nothing more.", 1, 90, recommended: 3)
            ]
        },
        new()
        {
            Key = "triggerApi",
            Title = "TRIGGER API",
            Group = SettingsGroups.Cameras,
            Endpoint = "/api/settings/trigger-api",
            Basic = true,
            DtoType = typeof(TriggerApiSettingsDto),
            Description = "Lets something else on your network tell a camera to take a look right now, by fetching a web address. Point a doorbell, an NVR's alert action, a home-automation script, or anything else that can open a URL at it, and that camera is captured and analysed exactly as if it had triggered itself. Off by default. The address for each camera is on that camera's trigger zone screen; the key below goes on the end of it.",
            Fields =
            [
                Check("enabled", "Enabled", "Allow cameras to be triggered by a web request. Off by default - while off the address answers 'not found' rather than admitting it exists."),
                // Deliberately "text", not "password", even though the tip says to treat it like
                // one: the whole point of this key is copying it onto doorbells and scripts, and
                // the save endpoint's clear-to-rotate behavior needs an empty value to actually
                // arrive - both of which the secret-masking machinery would break.
                Text("apiKey", "Key", "Goes on the end of the trigger address as ?key=... - anything that knows it can trigger your cameras, so treat it like a password. Generated for you when you first turn this on; clear it and save to issue a new one, which stops every device using the old one."),
                Number("cooldownSeconds", "Cooldown (s)", "Ignore repeat triggers for the same camera for this many seconds, so a device that fires ten times a second doesn't queue ten analyses. Applies per camera, whoever asked.", 0, 3600)
            ]
        },
        new()
        {
            Key = "triggerConfirmation",
            Title = "AI-CONFIRMED TRIGGERS",
            Group = SettingsGroups.Cameras,
            Endpoint = "/api/settings/trigger-confirmation",
            Basic = true,
            DtoType = typeof(TriggerConfirmationSettingsDto),
            Description = "Keeps the Triggers tab to things that turned out to be real. Motion detection can't tell a person from a branch in the wind or a moth on the lens, so with this on a trigger is held back until the AI has looked at the frame, and only appears if it found a person, vehicle, package, or animal. On by default. Only applies to cameras with 'Send to alerts' switched on - the AI never sees the others, so their triggers show up immediately as before. Nothing is ever deleted: rejected triggers and their clips stay on disk, and 'show hidden' on the Triggers tab lists them.",
            Fields =
            [
                Check("enabled", "Only show confirmed triggers", "Hold each trigger back until the AI confirms something was actually there. Turn this off to see every motion trigger the moment it fires, the way it worked before."),
                Number("timeoutSeconds", "Give up after (s)", "How long to wait for the AI before showing the trigger anyway. A trigger is never hidden because the analysis was slow, crashed, or couldn't be reached - only because the AI looked and found nothing. Raise this if your analyses regularly take longer than this and unconfirmed triggers are slipping through.", 30, 3600)
            ]
        },
        new()
        {
            Key = "hostReboot",
            Title = "REBOOT SERVER",
            Group = SettingsGroups.Maintenance,
            Endpoint = "/api/settings/host-reboot",
            Basic = false,
            DtoType = typeof(HostRebootSettingsDto),
            Description = "Lets you restart the computer this software runs on - the whole machine, not just this app - from here, your phone, or the TV app. Off by default, and switching it on is only half of it: the operating system also has to be told to allow it, which is a command you run once on the server itself. Turn this on and the reboot screen will work out what your machine needs and show you exactly what to paste. Cameras keep recording through a reboot if your recorder is a separate machine; what stops for a minute is analysis, this dashboard, and notifications. There is deliberately no way to shut the machine down from here - software can't turn it back on again.",
            Fields =
            [
                Check("enabled", "Enabled", "Allow this machine to be rebooted from the apps. You'll still be asked to confirm every time, and the reboot screen will tell you if the operating system hasn't been set up to permit it yet."),
                Number("delaySeconds", "Undo window (s)", "How long to wait before actually rebooting, so a mis-tap can be called back. The reboot screen counts down and offers Cancel for this long. Set to 0 to reboot immediately.", 0, 3600)
            ]
        }
    ];

    public static IReadOnlyList<SettingsSectionSchema> Sections { get; } = WithDerivedFlags(Declared);

    // Fills in the field flags that are read off the DTO instead of being written out per field.
    // Currently that is Integer alone; anything else derivable belongs here rather than in 67
    // hand-typed call sites that would each be one edit away from disagreeing with the DTO.
    private static IReadOnlyList<SettingsSectionSchema> WithDerivedFlags(
        IReadOnlyList<SettingsSectionSchema> sections) =>
        sections
            .Select(section => section with
            {
                Fields = section.Fields
                    .Select(field => field.Type == "number"
                        ? field with { Integer = IsIntegral(section.DtoType, field.Name) }
                        : field)
                    .ToArray()
            })
            .ToArray();

    // Walks a dotted camelCase field name ("apple.teamId", "minimumConfidence") down to the
    // property it binds to. A name that resolves to nothing returns false rather than throwing:
    // SettingsSchemaTests is what fails the build on a field with no matching property, and it
    // gives a far better message than a NullReferenceException during static initialization.
    private static bool IsIntegral(Type dtoType, string fieldName)
    {
        var current = dtoType;
        var segments = fieldName.Split('.');

        for (var i = 0; i < segments.Length; i++)
        {
            var property = current?.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => string.Equals(
                    JsonNamingPolicy.CamelCase.ConvertName(p.Name), segments[i], StringComparison.Ordinal));

            if (property is null) return false;

            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (i == segments.Length - 1)
                return underlying == typeof(int) || underlying == typeof(long);

            current = underlying;
        }

        return false;
    }
}
