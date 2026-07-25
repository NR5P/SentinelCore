using BlueIrisOllamaBridge;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddOptions<SelfMonitoringOptions>()
    .Bind(builder.Configuration.GetSection(SelfMonitoringOptions.SectionName))
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
builder.Services.AddOptions<BugReportOptions>()
    .Bind(builder.Configuration.GetSection(BugReportOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IBugReportSender, SmtpBugReportSender>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new BugReportLoggerProvider("BlueIrisOllamaBridge", sp));

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

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IAlertQueue, AlertQueue>();
builder.Services.AddSingleton<IImageOptimizer, ImageOptimizer>();
builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
builder.Services.AddSingleton<IResultStore, FileResultStore>();
builder.Services.AddSingleton<IAlertAction, EmailAlertAction>();

// Registered before MqttPublisher so the embedded broker (if enabled) is listening before the
// publisher's own client tries to connect to it.
builder.Services.AddHostedService<EmbeddedMqttBrokerService>();
builder.Services.AddSingleton<MqttPublisher>();
builder.Services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttPublisher>());
builder.Services.AddSingleton<IAlertAction, MqttAlertAction>();

// Before AlertProcessor so interrupted alerts are already waiting when it starts draining.
builder.Services.AddHostedService<AlertRecoveryService>();
builder.Services.AddHostedService<AlertProcessor>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHostedService<DailySummaryWorker>();
builder.Services.AddHostedService<TelegramChatWorker>();
builder.Services.AddHostedService<SelfMonitoringWorker>();

var ollamaSettings = builder.Configuration.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new();
builder.Services.AddHttpClient<IOllamaVisionClient, OllamaVisionClient>(client =>
{
    client.BaseAddress = new Uri(ollamaSettings.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(ollamaSettings.RequestTimeoutSeconds);
});
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

var app = builder.Build();
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

app.MapGet("/health/ready", async (IOllamaVisionClient ollama, CancellationToken ct) =>
    await ollama.IsReadyAsync(ct)
        ? Results.Ok(new { status = "ready", ollama = "reachable" })
        : Results.Json(new { status = "not-ready", ollama = "unreachable" }, statusCode: 503));

app.MapPost("/api/blueiris/alert", async (
    HttpRequest request,
    IAlertQueue queue,
    IResultStore store,
    IOptionsMonitor<BridgeOptions> bridgeOptions,
    IOptionsMonitor<OllamaOptions> ollamaOptions,
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

    var alert = new IncomingAlert(
        Guid.NewGuid(), camera, form["alertId"].ToString(), occurredAt,
        DateTimeOffset.UtcNow, profile, image.ContentType, Path.GetFileName(image.FileName), memory.ToArray());

    // Write the image and the queued row before handing the alert to the in-memory queue. The
    // queue doesn't survive a restart, so anything enqueued without a durable copy first is lost
    // outright - it used to leave a permanently "queued" row with no image behind it.
    alert = alert with { ImagePath = await store.SaveImageAsync(alert, ct) };

    var queuedResult = new AlertResult
    {
        Id = alert.Id, Camera = alert.Camera, BlueIrisAlertId = alert.BlueIrisAlertId,
        OccurredAt = alert.OccurredAt, ReceivedAt = alert.ReceivedAt,
        Profile = alert.Profile, Model = ollamaOptions.CurrentValue.Model, Status = "queued",
        ImagePath = alert.ImagePath
    };
    await store.SaveAsync(queuedResult, ct);

    if (!await queue.TryEnqueueAsync(alert, ct))
    {
        // Don't leave the row claiming to be queued when nothing is going to pick it up.
        await store.SaveAsync(new AlertResult
        {
            Id = alert.Id, Camera = alert.Camera, BlueIrisAlertId = alert.BlueIrisAlertId,
            OccurredAt = alert.OccurredAt, ReceivedAt = alert.ReceivedAt,
            Profile = alert.Profile, Model = ollamaOptions.CurrentValue.Model,
            Status = "failed", CompletedAt = DateTimeOffset.UtcNow, ImagePath = alert.ImagePath,
            Error = "The analysis queue was full when this alert arrived, so it was never analyzed."
        }, ct);
        return Results.Json(new { error = "The analysis queue is full. Retry later." }, statusCode: 503);
    }

    return Results.Accepted($"/api/results/{alert.Id}", new AcceptedAlertResponse(alert.Id, "queued", $"/api/results/{alert.Id}"));
}).DisableAntiforgery();

app.MapGet("/api/results/{id:guid}", async (Guid id, IResultStore store, CancellationToken ct) =>
{
    var result = await store.GetAsync(id, ct);
    return result is null ? Results.NotFound(new { error = "Alert result not found." }) : Results.Ok(result);
});

app.Run();

public partial class Program;