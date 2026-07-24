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

var bridgeSettings = builder.Configuration.GetSection(BridgeOptions.SectionName).Get<BridgeOptions>() ?? new();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = bridgeSettings.MaximumImageBytes + 1024 * 1024;
});

var promptProfiles = builder.Configuration.GetSection("PromptProfiles").Get<Dictionary<string, PromptProfile>>()
    ?? throw new InvalidOperationException("No PromptProfiles are configured.");
builder.Services.AddSingleton<IReadOnlyDictionary<string, PromptProfile>>(promptProfiles);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IAlertQueue, AlertQueue>();
builder.Services.AddSingleton<IImageOptimizer, ImageOptimizer>();
builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
builder.Services.AddSingleton<IResultStore, FileResultStore>();
builder.Services.AddSingleton<IAlertAction, EmailAlertAction>();
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
    IOptions<BridgeOptions> bridgeOptions,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Use multipart/form-data with an image field." });

    var form = await request.ReadFormAsync(ct);
    var image = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
    if (image is null || image.Length == 0)
        return Results.BadRequest(new { error = "No image was supplied." });

    var settings = bridgeOptions.Value;
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

    var queued = await queue.TryEnqueueAsync(alert, ct);
    if (!queued)
        return Results.Json(new { error = "The analysis queue is full. Retry later." }, statusCode: 503);

    await store.SaveAsync(new AlertResult
    {
        Id = alert.Id, Camera = alert.Camera, BlueIrisAlertId = alert.BlueIrisAlertId,
        OccurredAt = alert.OccurredAt, ReceivedAt = alert.ReceivedAt,
        Profile = alert.Profile, Model = ollamaSettings.Model, Status = "queued"
    }, ct);

    return Results.Accepted($"/api/results/{alert.Id}", new AcceptedAlertResponse(alert.Id, "queued", $"/api/results/{alert.Id}"));
}).DisableAntiforgery();

app.MapGet("/api/results/{id:guid}", async (Guid id, IResultStore store, CancellationToken ct) =>
{
    var result = await store.GetAsync(id, ct);
    return result is null ? Results.NotFound(new { error = "Alert result not found." }) : Results.Ok(result);
});

app.Run();

public partial class Program;