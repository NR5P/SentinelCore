using SentinelCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

// Before the host reads any configuration: pull in settings this version introduced that the
// installed appsettings.json predates. Release bundles ship appsettings.defaults.json instead of
// appsettings.json precisely so that unpacking an upgrade cannot overwrite a live configuration,
// and this is the other half of that bargain - see ConfigDefaultsSeeder.
//
// Keyed off the binary's own directory rather than the content root because the defaults file is
// deployed next to the executable; every shipped service unit sets WorkingDirectory to that same
// folder, so the two agree in each configuration this actually ships in.
var configSeed = ConfigDefaultsSeeder.Seed(AppContext.BaseDirectory);
if (configSeed.Describe() is { } configSeedMessage) Console.WriteLine(configSeedMessage);

var builder = WebApplication.CreateBuilder(args);

// Before anything reads the listening address: let ASPNETCORE_URLS outrank appsettings.json,
// which the framework's own source ordering otherwise reverses - see HostUrls. This is what makes
// the install scripts' MAIN_PORT option do anything.
if (HostUrls.ResolveOverride(args, Environment.GetEnvironmentVariable(HostUrls.EnvironmentVariable)) is { } urlsOverride)
    builder.Configuration[HostUrls.ConfigurationKey] = urlsOverride;

// Drops the routine per-request logging of the two polling HttpClients, which would otherwise be
// 83% of this service's journal. See PollingHttpClientLogging for the measurements and for why
// this is scoped to two named clients rather than to HttpClient as a whole.
builder.Logging.AddPollingHttpClientFilters();

// Lets the same binaries run as a Windows Service or a systemd unit without a separate build.
// Both are no-ops when the process wasn't started by that service manager - including when it's
// run straight from a terminal - so this is safe on every platform.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// Self-provisions a real API key on first boot instead of crashing on BridgeOptions'
// [Required] validation below - see AutoApiKeyProvisioner. Persisted to disk (not just this
// process's config) because, unlike the HTTPS certificate, Blue Iris gets configured with this
// value once and a key that changed on every restart would silently break that integration.
var appSettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
try
{
    var apiKey = AutoApiKeyProvisioner.EnsureApiKey(appSettingsPath, builder.Configuration["Bridge:ApiKey"]);
    builder.Configuration["Bridge:ApiKey"] = apiKey;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // Falls through to the [Required] validation below, which reports it clearly and stops
    // the app rather than running with no authentication at all.
    Console.WriteLine($"Could not auto-generate Bridge:ApiKey ({ex.Message}). Set it by hand in {appSettingsPath}.");
}

builder.Services.AddOptions<BridgeOptions>()
    .Bind(builder.Configuration.GetSection(BridgeOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !o.ApiKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase), "Bridge:ApiKey must be changed.")
    .ValidateOnStart();
builder.Services.AddOptions<ImageProcessingOptions>()
    .Bind(builder.Configuration.GetSection(ImageProcessingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<CloudVisionOptions>()
    .Bind(builder.Configuration.GetSection(CloudVisionOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ApiKey), "CloudVision:ApiKey is required when cloud vision is enabled.")
    .ValidateOnStart();
builder.Services.AddOptions<VisionRoutingOptions>()
    .Bind(builder.Configuration.GetSection(VisionRoutingOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => o.Primary is "ollama" or "cloud" or "fastlocal", "VisionRouting:Primary must be 'ollama', 'cloud', or 'fastlocal'.")
    .Validate(o => o.EscalationTarget is "auto" or "cloud" or "ollama", "VisionRouting:EscalationTarget must be 'auto', 'cloud', or 'ollama'.")
    .ValidateOnStart();
builder.Services.AddOptions<FastLocalOptions>()
    .Bind(builder.Configuration.GetSection(FastLocalOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !o.Enabled || Uri.TryCreate(o.Url, UriKind.Absolute, out _), "Webhook:Url must be valid when enabled.")
    .ValidateOnStart();
// The rules themselves live in SmtpMailer.ValidateDeliverable, registered below as an
// IValidateOptions so a hand-edited appsettings.json and a settings form saved from a phone are
// judged by exactly the same code - they used to be two lists that had drifted apart.
builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection(EmailOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
builder.Services.AddOptions<DailySummaryOptions>()
    .Bind(builder.Configuration.GetSection(DailySummaryOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => o.MorningHour != o.EveningHour, "DailySummary:MorningHour and DailySummary:EveningHour must differ.")
    .ValidateOnStart();
builder.Services.AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.BotToken), "Telegram:BotToken is required when Telegram is enabled.")
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ChatId), "Telegram:ChatId is required when Telegram is enabled.")
    .ValidateOnStart();
builder.Services.AddOptions<DiscordOptions>()
    .Bind(builder.Configuration.GetSection(DiscordOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !o.Enabled || DiscordOptions.IsValidWebhookUrl(o.WebhookUrl), "Discord:WebhookUrl must be a valid Discord HTTPS webhook URL when Discord is enabled.")
    .ValidateOnStart();
builder.Services.AddOptions<TelegramChatOptions>()
    .Bind(builder.Configuration.GetSection(TelegramChatOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<BlueIrisOptions>()
    .Bind(builder.Configuration.GetSection(BlueIrisOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.BaseUrl), "BlueIris:BaseUrl is required when BlueIris is enabled.")
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Camera), "BlueIris:Camera is required when BlueIris is enabled.")
    .Validate(o => !o.Enabled || Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "BlueIris:BaseUrl must be a valid URL.")
    .ValidateOnStart();
builder.Services.AddOptions<MultiFrameOptions>()
    .Bind(builder.Configuration.GetSection(MultiFrameOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<PackageTheftOptions>()
    .Bind(builder.Configuration.GetSection(PackageTheftOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SemanticSearchOptions>()
    .Bind(builder.Configuration.GetSection(SemanticSearchOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RepeatAlertOptions>()
    .Bind(builder.Configuration.GetSection(RepeatAlertOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SelfMonitoringOptions>()
    .Bind(builder.Configuration.GetSection(SelfMonitoringOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<StartupNotificationOptions>()
    .Bind(builder.Configuration.GetSection(StartupNotificationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<InternetOutageOptions>()
    .Bind(builder.Configuration.GetSection(InternetOutageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<QuietHoursOptions>()
    .Bind(builder.Configuration.GetSection(QuietHoursOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ScheduleOptions>()
    .Bind(builder.Configuration.GetSection(ScheduleOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<LocationOptions>()
    .Bind(builder.Configuration.GetSection(LocationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<MqttOptions>()
    .Bind(builder.Configuration.GetSection(MqttOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DeviceHubOptions>()
    .Bind(builder.Configuration.GetSection(DeviceHubOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<HomeKitOptions>()
    .Bind(builder.Configuration.GetSection(HomeKitOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<BugReportOptions>()
    .Bind(builder.Configuration.GetSection(BugReportOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<HostRebootOptions>()
    .Bind(builder.Configuration.GetSection(HostRebootOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<PushNotificationOptions>()
    .Bind(builder.Configuration.GetSection(PushNotificationOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        o => !o.Enabled || !o.Apple.Enabled ||
             (!string.IsNullOrWhiteSpace(o.Apple.TeamId) &&
              !string.IsNullOrWhiteSpace(o.Apple.KeyId) &&
              !string.IsNullOrWhiteSpace(o.Apple.BundleId) &&
              !string.IsNullOrWhiteSpace(o.Apple.PrivateKeyPath)),
        "PushNotifications:Apple provider credentials are required when Apple push is enabled.")
    .Validate(
        o => !o.Enabled || !o.Firebase.Enabled ||
             (!string.IsNullOrWhiteSpace(o.Firebase.ProjectId) &&
              !string.IsNullOrWhiteSpace(o.Firebase.ServiceAccountPath)),
        "PushNotifications:Firebase provider credentials are required when Firebase push is enabled.")
    .ValidateOnStart();

builder.Services.AddSingleton<IBugReportSender, SmtpBugReportSender>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new BugReportLoggerProvider("SentinelCore", sp));

var bridgeSettings = builder.Configuration.GetSection(BridgeOptions.SectionName).Get<BridgeOptions>() ?? new();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = bridgeSettings.MaximumImageBytes + 1024 * 1024;
});

_ = builder.Configuration.GetSection("PromptProfiles").Get<Dictionary<string, PromptProfile>>()
    ?? throw new InvalidOperationException("No PromptProfiles are configured.");
builder.Services.Configure<Dictionary<string, PromptProfile>>(builder.Configuration.GetSection("PromptProfiles"));
builder.Services.AddSingleton<IPromptProfileProvider, PromptProfileProvider>();

// Empty by default - only consulted for profiles with LicensePlateRecognitionEnabled turned on.
builder.Services.Configure<List<KnownVehicle>>(builder.Configuration.GetSection("KnownVehicles"));

// Empty by default - only consulted for profiles with FaceRecognitionEnabled turned on.
builder.Services.Configure<List<KnownFace>>(builder.Configuration.GetSection("KnownFaces"));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IAlertQueue, AlertQueue>();
// Consulted only when a burst actually needs deciding, so an install that never sees two frames
// close together never loads the detector on this path. See BurstFramePicker.
builder.Services.AddSingleton<BurstFramePicker>();
// Singleton, not scoped: "what did this camera last notify about" is process-wide state with the
// same lifetime as the per-channel cooldowns it sits above.
builder.Services.AddSingleton<NotificationDeduplicator>();
builder.Services.AddSingleton<IImageOptimizer, ImageOptimizer>();
builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
builder.Services.AddSingleton<IResultStore, FileResultStore>();
builder.Services.AddSingleton<IPushDeviceStore, SqlitePushDeviceStore>();
builder.Services.AddSingleton<IPackageWatchStore, SqlitePackageWatchStore>();
builder.Services.AddSingleton<PushNotificationDispatcher>();
builder.Services.AddSingleton<IAlertAction, EmailAlertAction>();
builder.Services.AddSingleton<IAlertAction, PushAlertAction>();

// Registered before MqttPublisher so the embedded broker (if enabled) is listening before the
// publisher's own client tries to connect to it.
builder.Services.AddHostedService<EmbeddedMqttBrokerService>();
builder.Services.AddSingleton<MqttPublisher>();
builder.Services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttPublisher>());
builder.Services.AddSingleton<IAlertAction, MqttAlertAction>();

// The Device Hub's own MQTT client - separate from MqttPublisher's so device state traffic and
// the resubscribes that follow every device edit can't disturb alert publishing. Registered as
// a singleton the endpoints resolve directly as well as a hosted service, the same shape
// MqttPublisher above already uses.
builder.Services.AddSingleton<IDeviceStore, SqliteDeviceStore>();
builder.Services.AddSingleton<MqttDeviceBus>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttDeviceBus>());
builder.Services.AddSingleton<MqttDeviceDiscovery>();

builder.Services.AddSingleton<IAutomationRuleStore, SqliteAutomationRuleStore>();

// Schedules - the clock-driven counterpart to the alert-driven automation rules above. The
// worker is registered in the main app only, for the same reason as StartupNotificationWorker
// below: three processes each firing the same schedule would open the gate three times.
builder.Services.AddSingleton<IScheduleStore, SqliteScheduleStore>();
builder.Services.AddSingleton<ScheduleContext>();
builder.Services.AddSingleton<ScheduleRunner>();
builder.Services.AddHostedService<ScheduleWorker>();

// Before AlertProcessor so interrupted alerts are already waiting when it starts draining.
builder.Services.AddHostedService<AlertRecoveryService>();
builder.Services.AddHostedService<AlertProcessor>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHostedService<DailySummaryWorker>();
builder.Services.AddSingleton<ChatAnswerService>();
// Periodic per-camera gate/door open check (SentinelCore.Web's GateCheckMonitor forwards a
// snapshot here). Deliberately not queued through IAlertQueue/AlertProcessor - the analysis
// happens synchronously in the request, and its own notify decision (GateCheckDecision) bypasses
// AlertNotificationPolicy's person/vehicle/package/animal requirement, which doesn't apply to an
// ambient state check. See GateCheckAnalyzer.
builder.Services.AddSingleton<GateCheckAnalyzer>();
// Loads its ONNX face-detection/embedding models once at startup - registered as a singleton
// for the same reason GateCheckAnalyzer and the vision clients are: expensive to construct,
// safe to reuse across every alert for the process lifetime.
builder.Services.AddSingleton<IFaceRecognitionService, FaceRecognitionService>();
// Unlike FaceRecognitionService, model download/load is deferred to first actual use (see
// ImageEmbeddingService.EnsureInitializedAsync) rather than done in this constructor - the CLIP
// model is a ~577MB one-time download, and semantic search is opt-in, so a user who never enables
// it shouldn't pay that cost just by starting the app. Still registered as a singleton so the
// loaded ONNX sessions are reused once initialization does happen.
builder.Services.AddHttpClient<IImageEmbeddingService, ImageEmbeddingService>();
builder.Services.AddHostedService<TelegramChatWorker>();
builder.Services.AddHostedService<SelfMonitoringWorker>();
// Registered in the main app only - all three services would otherwise each report the same
// reboot separately.
builder.Services.AddHostedService<StartupNotificationWorker>();
// Same reasoning, and the same one-report-per-outage argument.
builder.Services.AddHostedService<InternetOutageWorker>();
// Registered as both so the /api/system/status endpoint below can read Latest off the same
// instance the background loop is updating, rather than standing up a second sampler.
builder.Services.AddSingleton<SystemStatsSampler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemStatsSampler>());

// Which of the three operating systems this is running on is settled once, at startup - the
// answer cannot change while the process lives, and resolving it per-request would mean every
// caller repeating the same three checks.
builder.Services.AddSingleton<IHostRebootController>(sp =>
    HostRebootControllerFactory.Create(sp.GetRequiredService<IOptionsMonitor<HostRebootOptions>>()));
builder.Services.AddSingleton<HostRebootScheduler>();

var ollamaSettings = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new();
builder.Services.AddHttpClient<OllamaVisionClient>(client =>
{
    client.BaseAddress = new Uri(ollamaSettings.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(ollamaSettings.RequestTimeoutSeconds);
});
var cloudVisionSettings = builder.Configuration.GetSection(CloudVisionOptions.SectionName).Get<CloudVisionOptions>() ?? new();
// Only actually used when CloudVision:Provider is "openai" - that's also the slot for any other
// OpenAI-compatible endpoint (Azure OpenAI, OpenRouter, a local vLLM/LM Studio server, etc.).
builder.Services.AddHttpClient<OpenAiVisionClient>(client =>
{
    client.BaseAddress = new Uri(cloudVisionSettings.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(cloudVisionSettings.TimeoutSeconds);
});
// Anthropic and Google each have one real endpoint - unlike OpenAI's format, nothing else in
// common use imitates their request shape, so there's no equivalent reason to make these
// overridable.
builder.Services.AddHttpClient<AnthropicVisionClient>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/v1/");
    client.Timeout = TimeSpan.FromSeconds(cloudVisionSettings.TimeoutSeconds);
});
builder.Services.AddHttpClient<GoogleVisionClient>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
    client.Timeout = TimeSpan.FromSeconds(cloudVisionSettings.TimeoutSeconds);
});
builder.Services.AddSingleton(sp => new CloudVisionClient(
    sp.GetRequiredService<OpenAiVisionClient>(),
    sp.GetRequiredService<AnthropicVisionClient>(),
    sp.GetRequiredService<GoogleVisionClient>(),
    sp.GetRequiredService<IOptionsMonitor<CloudVisionOptions>>(),
    sp.GetRequiredService<ILogger<CloudVisionClient>>()));
// Same deferred-download singleton shape as ImageEmbeddingService: the detector's ~100MB ONNX
// model is only fetched/loaded the first time an alert actually routes to the fastlocal backend,
// so installs that never select it pay nothing at startup.
builder.Services.AddHttpClient<IFastLocalDetector, FastLocalDetector>();
builder.Services.AddSingleton(sp => new FastLocalVisionClient(
    sp.GetRequiredService<IFastLocalDetector>(),
    sp.GetRequiredService<IOptionsMonitor<FastLocalOptions>>(),
    sp.GetRequiredService<ILogger<FastLocalVisionClient>>()));
builder.Services.AddSingleton<IVisionAnalysisClient>(sp => new FailoverVisionClient(
    sp.GetRequiredService<OllamaVisionClient>(),
    sp.GetRequiredService<CloudVisionClient>(),
    sp.GetRequiredService<FastLocalVisionClient>(),
    sp.GetRequiredService<IOptionsMonitor<VisionRoutingOptions>>(),
    sp.GetRequiredService<IOptionsMonitor<CloudVisionOptions>>(),
    sp.GetRequiredService<ILogger<FailoverVisionClient>>()));
// All four share TClient = IAlertAction, so they must be registered with explicit, distinct
// client names or they collapse into one - see AlertActionHttpClients for what that cost.
var webhookSettings = builder.Configuration.GetSection(WebhookOptions.SectionName).Get<WebhookOptions>() ?? new();
var telegramSettings = builder.Configuration.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new();
var discordSettings = builder.Configuration.GetSection(DiscordOptions.SectionName).Get<DiscordOptions>() ?? new();
var homeKitSettings = builder.Configuration.GetSection(HomeKitOptions.SectionName).Get<HomeKitOptions>() ?? new();
builder.Services.AddAlertActionHttpClients(
    webhookSettings.TimeoutSeconds,
    telegramSettings.TimeoutSeconds,
    discordSettings.TimeoutSeconds,
    homeKitSettings.TimeoutSeconds);
// The one place MQTT publishes, webhook calls and Device Hub commands are actually carried out,
// shared by the alert-driven rules below and the clock-driven ScheduleWorker.
builder.Services.AddHttpClient<AutomationActionDispatcher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
// Registered as its own concrete type (not just IAlertAction) so the /test endpoint can resolve
// it directly - same pattern as MqttPublisher/IMqttPublisher above.
builder.Services.AddSingleton<AutomationAlertAction>();
builder.Services.AddSingleton<IAlertAction>(sp => sp.GetRequiredService<AutomationAlertAction>());
var telegramChatSettings = builder.Configuration.GetSection(TelegramChatOptions.SectionName).Get<TelegramChatOptions>() ?? new();
builder.Services.AddHttpClient(nameof(TelegramChatWorker), client =>
{
    // Longer than the long-poll's own "timeout" query parameter so the poll itself never
    // gets cut off by the client before Telegram responds. This is read once at startup while
    // PollTimeoutSeconds hot-reloads, so TelegramChatWorker.ClampPollTimeout holds the other
    // side of the same invariant using the same headroom constant.
    client.Timeout = TimeSpan.FromSeconds(telegramChatSettings.PollTimeoutSeconds + TelegramChatWorker.PollTimeoutHeadroomSeconds);
});
builder.Services.AddHttpClient<IOllamaChatClient, OllamaChatClient>(client =>
{
    client.BaseAddress = new Uri(ollamaSettings.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(telegramChatSettings.TimeoutSeconds);
});
var blueIrisSettings = builder.Configuration.GetSection(BlueIrisOptions.SectionName).Get<BlueIrisOptions>() ?? new();
builder.Services.AddHttpClient<IBlueIrisSnapshotClient, BlueIrisSnapshotClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(blueIrisSettings.TimeoutSeconds);
});
builder.Services.AddHttpClient(nameof(SelfMonitoringWorker), client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
// Used for both the connectivity probes and the Telegram report. The per-probe timeout is
// enforced separately and is shorter; this is the backstop for the Telegram call.
builder.Services.AddHttpClient(nameof(InternetOutageWorker), client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});

var app = builder.Build();

// Visibility only - never a functional gate. See LicenseStatus.cs.
var configuredLicensePath = builder.Configuration["License:FilePath"] ?? "license.lic";
var licenseFilePath = Path.IsPathRooted(configuredLicensePath)
    ? configuredLicensePath
    : Path.Combine(app.Environment.ContentRootPath, configuredLicensePath);
var license = LicenseStatus.Check(licenseFilePath);
if (license is null)
    app.Logger.LogWarning("No valid license found at {LicenseFilePath} - running unlicensed. See LicenseGenerator/README.md.", licenseFilePath);
else
    app.Logger.LogInformation("Licensed to {CustomerName} ({LicenseId})", license.CustomerName, license.LicenseId);

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "Blue Iris Ollama Bridge",
    // Was AssemblyName.Version, which is only ever the four-part "1.0.0.0" and carries no
    // commit - useless for identifying what a tester is actually running. BuildInfo carries the
    // git revision too.
    build = BuildInfo.Current,
    endpoints = new[] { "/health/live", "/health/ready", "/api/blueiris/alert", "/api/results/{id}" }
}));

app.MapGet("/health/live", (IAlertQueue queue) => Results.Ok(new
{
    status = "healthy",
    queued = queue.ApproximateCount,
    build = BuildInfo.Current,
    timestamp = DateTimeOffset.UtcNow
}));

// "unavailable" rather than "unreachable": readiness now also fails when the backend answers
// fine but the configured model isn't installed, which is not a reachability problem. The
// specific cause (and the `ollama pull` to fix it) is logged by the client.
// The build is reported on the failure branch too, deliberately: "not ready" is precisely the
// response someone will paste into a support thread, and it's worth nothing without knowing
// which build produced it.
app.MapGet("/health/ready", async (IVisionAnalysisClient vision, CancellationToken ct) =>
    await vision.IsReadyAsync(ct)
        ? Results.Ok(new { status = "ready", vision = "reachable", build = BuildInfo.Current })
        : Results.Json(new { status = "not-ready", vision = "unavailable", build = BuildInfo.Current }, statusCode: 503));

app.MapPost("/api/blueiris/alert", async (
    HttpRequest request,
    IAlertQueue queue,
    IResultStore store,
    IOptionsMonitor<BridgeOptions> bridgeOptions,
    IOptionsMonitor<RepeatAlertOptions> repeatAlerts,
    BurstFramePicker burstFrames,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Use multipart/form-data with an image field." });

    if (await AlertFormReader.TryReadAsync(request, logger, ct) is not { } form)
        return Results.BadRequest(new { error = AlertFormReader.MalformedBodyMessage });

    var image = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
    if (image is null || image.Length == 0)
        return Results.BadRequest(new { error = "No image was supplied." });

    var settings = bridgeOptions.CurrentValue;
    if (image.Length > settings.MaximumImageBytes)
        return Results.Json(new { error = $"Image exceeds {settings.MaximumImageBytes} bytes." }, statusCode: 413);
    if (!settings.AllowedImageContentTypes.Contains(image.ContentType))
        return Results.BadRequest(new { error = $"Unsupported image content type: {image.ContentType}" });

    var camera = form["camera"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(camera)) camera = "unknown";
    if (camera.Length > 100) return Results.BadRequest(new { error = "Camera name is too long." });

    var profile = form["profile"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(profile)) profile = "default";
    if (profile.Length > 100) return Results.BadRequest(new { error = "Profile name is too long." });

    var occurredAt = DateTimeOffset.TryParse(form["occurredAt"], out var parsed) ? parsed : DateTimeOffset.UtcNow;
    await using var memory = new MemoryStream((int)image.Length);
    await image.CopyToAsync(memory, ct);

    // Optional extra frames from a burst-capturing trigger zone (see TriggerZoneMonitor,
    // AlertForwarder) - Blue Iris's own alert action never sends these, so this is always empty
    // for a Blue-Iris-sourced alert. A frame that fails the same size/content-type checks as the
    // primary image is just dropped rather than failing the whole alert - it's purely additive.
    var extraFrames = new List<byte[]>();
    foreach (var fieldName in new[] { "frame1", "frame2" })
    {
        var frame = form.Files.GetFile(fieldName);
        if (frame is null || frame.Length == 0) continue;
        if (frame.Length > settings.MaximumImageBytes || !settings.AllowedImageContentTypes.Contains(frame.ContentType))
            continue;

        await using var frameStream = new MemoryStream((int)frame.Length);
        await frame.CopyToAsync(frameStream, ct);
        extraFrames.Add(frameStream.ToArray());
    }

    // Only ever sent by a trigger-zone camera that belongs to a Camera Group (see
    // TriggerZoneMonitor/AlertForwarder) - Blue Iris's own alert action never sends these.
    var linkedCameras = form["linkedCameras"].ToString().Trim();
    var incidentId = form["incidentId"].ToString().Trim();

    var alert = new IncomingAlert(
        Guid.NewGuid(), camera, form["alertId"].ToString(), occurredAt,
        DateTimeOffset.UtcNow, profile, image.ContentType, Path.GetFileName(image.FileName), memory.ToArray())
    {
        ExtraFrames = extraFrames,
        LinkedCameras = string.IsNullOrWhiteSpace(linkedCameras) ? null : linkedCameras,
        IncidentId = string.IsNullOrWhiteSpace(incidentId) ? null : incidentId
    };

    // Write the image and the queued row before handing the alert to the in-memory queue. The
    // queue doesn't survive a restart, so anything enqueued without a durable copy first is lost
    // outright - it used to leave a permanently "queued" row with no image behind it.
    alert = alert with { ImagePath = await store.SaveImageAsync(alert, ct) };

    // Which backend actually handles this isn't decided until AlertProcessor picks it up (could
    // be the primary or a fallback) - "pending" here gets overwritten with the real model once
    // analysis completes.
    var queuedResult = new AlertResult
    {
        Id = alert.Id, Camera = alert.Camera, BlueIrisAlertId = alert.BlueIrisAlertId,
        OccurredAt = alert.OccurredAt, ReceivedAt = alert.ReceivedAt,
        Profile = alert.Profile, Model = "pending", Status = "queued",
        ImagePath = alert.ImagePath, LinkedCameras = alert.LinkedCameras, IncidentId = alert.IncidentId
    };
    await store.SaveAsync(queuedResult, ct);

    // Supersession keeps the newest frame, which is right when both frames show something and
    // wrong when the newer one is empty - see BurstFramePicker. The peek is advisory; the queue
    // re-decides under its own lock, so the worst case here is a detector pass whose answer is
    // then unused.
    var supersedeQueued = repeatAlerts.CurrentValue.SupersedeQueued
        && await burstFrames.ShouldSupersedeAsync(queue.PeekWaiting(camera), alert, ct);

    var enqueued = await queue.TryEnqueueAsync(alert, supersedeQueued, ct);

    // A frame from this camera was still waiting its turn when this newer one arrived, so it stood
    // down (see AlertQueue). Its row has to stop saying "queued" for the same reason a full queue's
    // does - AlertRecoveryService resumes anything left claiming that on the next restart - and it
    // records what replaced it, which is how the dashboard's trigger confirmation finds a verdict
    // for the trigger that produced it.
    if (enqueued.Superseded is { } displaced)
    {
        await store.SaveAsync(new AlertResult
        {
            Id = displaced.Id, Camera = displaced.Camera, BlueIrisAlertId = displaced.BlueIrisAlertId,
            OccurredAt = displaced.OccurredAt, ReceivedAt = displaced.ReceivedAt,
            Profile = displaced.Profile, Model = "superseded",
            Status = "superseded", CompletedAt = DateTimeOffset.UtcNow, ImagePath = displaced.ImagePath,
            LinkedCameras = displaced.LinkedCameras, IncidentId = displaced.IncidentId,
            SupersededByAlertId = alert.Id,
            Error = $"A newer frame from {displaced.Camera} arrived while this one was still queued, so that one was analyzed instead."
        }, ct);

        logger.LogInformation(
            "Alert {SupersededId} from {Camera} was still queued when {AlertId} arrived {Seconds:F0}s later, so the newer frame took its place",
            displaced.Id, displaced.Camera, alert.Id, (alert.ReceivedAt - displaced.ReceivedAt).TotalSeconds);
    }

    if (!enqueued.Accepted)
    {
        // Don't leave the row claiming to be queued when nothing is going to pick it up.
        await store.SaveAsync(new AlertResult
        {
            Id = alert.Id, Camera = alert.Camera, BlueIrisAlertId = alert.BlueIrisAlertId,
            OccurredAt = alert.OccurredAt, ReceivedAt = alert.ReceivedAt,
            Profile = alert.Profile, Model = "unavailable",
            Status = "failed", CompletedAt = DateTimeOffset.UtcNow, ImagePath = alert.ImagePath,
            LinkedCameras = alert.LinkedCameras, IncidentId = alert.IncidentId,
            Error = "The analysis queue was full when this alert arrived, so it was never analyzed."
        }, ct);
        return Results.Json(new { error = "The analysis queue is full. Retry later." }, statusCode: 503);
    }

    return Results.Accepted($"/api/results/{alert.Id}", new AcceptedAlertResponse(alert.Id, "queued", $"/api/results/{alert.Id}"));
}).DisableAntiforgery();

// Forwarded from SentinelCore.Web's GateCheckMonitor - a periodic per-camera snapshot, not a
// Blue Iris alert, so this is deliberately synchronous (no queue) and returns the caller's own
// last-known state back for it to persist (see GateCheckAnalyzer's own comment on the split).
app.MapPost("/api/gate-check", async (HttpRequest request, GateCheckAnalyzer analyzer, IOptionsMonitor<BridgeOptions> bridgeOptions, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Use multipart/form-data with an image field." });

    if (await AlertFormReader.TryReadAsync(request, logger, ct) is not { } form)
        return Results.BadRequest(new { error = AlertFormReader.MalformedBodyMessage });

    var image = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
    if (image is null || image.Length == 0)
        return Results.BadRequest(new { error = "No image was supplied." });

    var settings = bridgeOptions.CurrentValue;
    if (image.Length > settings.MaximumImageBytes)
        return Results.Json(new { error = $"Image exceeds {settings.MaximumImageBytes} bytes." }, statusCode: 413);

    var camera = form["camera"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(camera)) return Results.BadRequest(new { error = "camera is required." });

    var checkLabel = form["checkLabel"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(checkLabel)) checkLabel = "the gate";

    await using var memory = new MemoryStream((int)image.Length);
    await image.CopyToAsync(memory, ct);

    var gateRequest = new GateCheckRequest(
        Camera: camera,
        CheckLabel: checkLabel,
        CheckBlocked: form["checkBlocked"] == "true",
        ImageBytes: memory.ToArray(),
        ContentType: image.ContentType,
        FileName: Path.GetFileName(image.FileName),
        PreviousState: string.IsNullOrWhiteSpace(form["previousState"]) ? "unknown" : form["previousState"].ToString(),
        PreviousBlocked: form["previousBlocked"] == "true",
        LastStateChangedAt: DateTimeOffset.TryParse(form["lastStateChangedAt"], out var changedAt) ? changedAt : null,
        LastNotifiedAt: DateTimeOffset.TryParse(form["lastNotifiedAt"], out var notifiedAt) ? notifiedAt : null,
        RemindIfStillOpenAfterHours: int.TryParse(form["remindIfStillOpenAfterHours"], out var hours) ? hours : null);

    var outcome = await analyzer.AnalyzeAsync(gateRequest, ct);
    return Results.Ok(outcome);
}).DisableAntiforgery();

app.MapGet("/api/results/{id:guid}", async (Guid id, IResultStore store, CancellationToken ct) =>
{
    var result = await store.GetAsync(id, ct);
    return result is null ? Results.NotFound(new { error = "Alert result not found." }) : Results.Ok(result);
});

app.MapGet("/api/queue/status", (IAlertQueue queue) =>
    Results.Ok(new { queued = queue.ApproximateCount }));

app.MapGet("/api/system/status", (SystemStatsSampler sampler) =>
    sampler.Latest is { } snapshot
        ? Results.Ok(snapshot)
        : Results.Json(new { error = "System stats are not available yet." }, statusCode: 503));

// Exits this process (gracefully, via host shutdown) so its supervisor starts a fresh one -
// the last step that lets restart-required settings like Urls be applied from the dashboard or
// a phone instead of a shell. Covered by ApiKeyMiddleware like every /api route; the dashboard
// forwards to here from its own Admin-only /api/system/restart. Refused outright when nothing
// is supervising this process - see RestartSupport.
app.MapPost("/api/system/restart", (IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<Program> logger) =>
{
    if (!RestartSupport.IsSupervised(configuration))
    {
        return Results.Conflict(new
        {
            error = "This process isn't running under a supervisor that would restart it, so exiting would just take it down. " +
                    "Run it as a service (the shipped systemd units restart automatically), or set Restart:ForceAllowed to true " +
                    "if a supervisor this can't detect (Docker, launchd) is in charge."
        });
    }

    logger.LogWarning("Restart requested via /api/system/restart - shutting down for the supervisor to bring us back.");
    RestartSupport.StopSoon(lifetime);
    return Results.Accepted(value: new { restarting = true });
});

// Rebooting the machine itself, as opposed to bouncing this process. See HostReboot.cs for why
// this is off by default and why turning it on is only half of what it needs.
//
// The probe is a GET with no side effects on purpose: a client should be able to open the
// settings screen and show the true state - including "the sudoers rule you added in March is
// gone" - without anything happening to the machine.
app.MapGet("/api/system/host-reboot", async (
    IHostRebootController controller,
    HostRebootScheduler scheduler,
    CancellationToken ct) =>
{
    var probe = await controller.ProbeAsync(ct);
    return Results.Ok(probe with { ScheduledFor = scheduler.ScheduledFor });
});

app.MapPost("/api/system/host-reboot", async (HostRebootScheduler scheduler, CancellationToken ct) =>
{
    var outcome = await scheduler.ScheduleAsync(ct);

    // Conflict rather than BadRequest: nothing about the request was wrong, the machine simply
    // isn't in a state where this can be honoured - the feature is off, or the OS never granted
    // the privilege, or the grant has since disappeared.
    return outcome.Ok
        ? Results.Accepted(value: outcome)
        : Results.Json(outcome, statusCode: StatusCodes.Status409Conflict);
});

app.MapPost("/api/system/host-reboot/cancel", (HostRebootScheduler scheduler) =>
{
    var outcome = scheduler.Cancel();
    return outcome.Ok
        ? Results.Ok(outcome)
        : Results.Json(outcome, statusCode: StatusCodes.Status409Conflict);
});

// For when the queue has fallen so far behind that catching up isn't worth it - each alert here
// takes as long as a real Ollama analysis does, so a backlog of a few hundred can mean hours.
// Only discards what's still waiting; whatever's already being analyzed finishes normally.
//
// Driven by the database (GetPendingAlertsAsync), not just what's sitting in the in-memory
// channel - the same source AlertRecoveryService itself trusts for "what's queued" after a
// restart. queue.Clear() alone would miss anything that was ever accepted but never actually made
// it into the channel (the DropWrite-vs-Wait bug fixed alongside this endpoint is exactly that
// kind of gap), so this is the version that's actually guaranteed to match "what's left queued."
app.MapPost("/api/queue/clear", async (IAlertQueue queue, IEventStore eventStore, IResultStore store, ILogger<Program> logger, CancellationToken ct) =>
{
    // Drained first so nothing still sitting in memory gets picked up and analyzed between here
    // and the database update below.
    queue.Clear();

    var pending = await eventStore.GetPendingAlertsAsync(ct);
    foreach (var record in pending)
    {
        // Same terminal-AlertResult pattern AlertRecoveryService.AbandonAsync uses for "failed" -
        // this is what stops the row from being marked "queued" forever, which is what would
        // otherwise make AlertRecoveryService resume it right back into the queue on the next
        // restart.
        await store.SaveAsync(new AlertResult
        {
            Id = record.Id,
            Camera = record.Camera,
            BlueIrisAlertId = record.BlueIrisAlertId,
            OccurredAt = record.OccurredAt,
            ReceivedAt = record.ReceivedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Profile = record.Profile,
            Model = "cancelled",
            Status = "cancelled",
            ImagePath = record.ImagePath,
            Error = "Cleared from the analysis queue by an operator - never analyzed."
        }, ct);
    }

    if (pending.Count > 0)
        logger.LogWarning("Cleared {Count} alert(s) from the analysis queue", pending.Count);

    return Results.Ok(new { cleared = pending.Count });
});

// Same Q&A logic the Telegram bot already uses (ChatAnswerService) - reachable here so the
// dashboard's own ask box (see SentinelCore.Web/ChatEndpoints.cs) doesn't need a
// Telegram account. Already covered by ApiKeyMiddleware above like every other /api route.
app.MapAutomationRuleEndpoints();
app.MapScheduleEndpoints();
app.MapDeviceHubEndpoints();
app.MapNotificationTestEndpoints();
app.MapCloudVisionTestEndpoints();

app.MapPost("/api/chat/ask", async (ChatQuestionRequest request, ChatAnswerService chatAnswers, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "A question is required." });

    var answer = await chatAnswers.AnswerAsync(request.Question, ct);
    return Results.Ok(new { answer });
});

app.Run();

public sealed record ChatQuestionRequest(string Question);

public partial class Program;
