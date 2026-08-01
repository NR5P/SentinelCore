using SentinelCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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
    builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection(EmailOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Username), "Email:Username is required when email is enabled.")
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.AppPassword), "Email:AppPassword is required when email is enabled.")
    .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ToAddress), "Email:ToAddress is required when email is enabled.")
    .Validate(o => !o.Enabled || new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(o.Username), "Email:Username must be a valid email address.")
    .Validate(o => !o.Enabled || new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(o.ToAddress), "Email:ToAddress must be a valid email address.")
    .ValidateOnStart();
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
var webhookSettings = builder.Configuration.GetSection(WebhookOptions.SectionName).Get<WebhookOptions>() ?? new();
builder.Services.AddHttpClient<IAlertAction, WebhookAlertAction>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(webhookSettings.TimeoutSeconds);
});
var telegramSettings = builder.Configuration.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new();
builder.Services.AddHttpClient<IAlertAction, TelegramAlertAction>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(telegramSettings.TimeoutSeconds);
});
var discordSettings = builder.Configuration.GetSection(DiscordOptions.SectionName).Get<DiscordOptions>() ?? new();
builder.Services.AddHttpClient<IAlertAction, DiscordAlertAction>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(discordSettings.TimeoutSeconds);
});
var homeKitSettings = builder.Configuration.GetSection(HomeKitOptions.SectionName).Get<HomeKitOptions>() ?? new();
builder.Services.AddHttpClient<IAlertAction, HomeKitAlertAction>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(homeKitSettings.TimeoutSeconds);
});
// Registered as its own concrete type (not just IAlertAction) so the /test endpoint can resolve
// it directly - same pattern as MqttPublisher/IMqttPublisher above.
builder.Services.AddHttpClient<AutomationAlertAction>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<IAlertAction>(sp => sp.GetRequiredService<AutomationAlertAction>());
var telegramChatSettings = builder.Configuration.GetSection(TelegramChatOptions.SectionName).Get<TelegramChatOptions>() ?? new();
builder.Services.AddHttpClient(nameof(TelegramChatWorker), client =>
{
    // Longer than the long-poll's own "timeout" query parameter so the poll itself never
    // gets cut off by the client before Telegram responds.
    client.Timeout = TimeSpan.FromSeconds(telegramChatSettings.PollTimeoutSeconds + 15);
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
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    endpoints = new[] { "/health/live", "/health/ready", "/api/blueiris/alert", "/api/results/{id}" }
}));

app.MapGet("/health/live", (IAlertQueue queue) => Results.Ok(new
{
    status = "healthy",
    queued = queue.ApproximateCount,
    timestamp = DateTimeOffset.UtcNow
}));

// "unavailable" rather than "unreachable": readiness now also fails when the backend answers
// fine but the configured model isn't installed, which is not a reachability problem. The
// specific cause (and the `ollama pull` to fix it) is logged by the client.
app.MapGet("/health/ready", async (IVisionAnalysisClient vision, CancellationToken ct) =>
    await vision.IsReadyAsync(ct)
        ? Results.Ok(new { status = "ready", vision = "reachable" })
        : Results.Json(new { status = "not-ready", vision = "unavailable" }, statusCode: 503));

app.MapPost("/api/blueiris/alert", async (
    HttpRequest request,
    IAlertQueue queue,
    IResultStore store,
    IOptionsMonitor<BridgeOptions> bridgeOptions,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Use multipart/form-data with an image field." });

    var form = await request.ReadFormAsync(ct);
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

    if (!await queue.TryEnqueueAsync(alert, ct))
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
app.MapPost("/api/gate-check", async (HttpRequest request, GateCheckAnalyzer analyzer, IOptionsMonitor<BridgeOptions> bridgeOptions, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Use multipart/form-data with an image field." });

    var form = await request.ReadFormAsync(ct);
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
app.MapDeviceHubEndpoints();

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
