using BlueIrisOllamaBridge;
using BlueIrisOllamaBridge.Web;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Only DataDirectory is used here (to find the shared events.db/images), so this
// deliberately skips the main app's ApiKey validation - this project has no API key.
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));

builder.Services.AddSingleton<IEventStore, SqliteEventStore>();

var mainAppSettingsPath = builder.Configuration["MainAppSettingsPath"]
    ?? throw new InvalidOperationException("MainAppSettingsPath is not configured.");
builder.Services.AddSingleton(new AppSettingsFile(mainAppSettingsPath));

// Cameras added through the UI are stored separately from the main app's data entirely -
// they're for live viewing only and the alert pipeline never sees them.
var camerasDataDirectory = builder.Configuration["CamerasDataDirectory"] ?? "data";
var camerasRoot = Path.IsPathRooted(camerasDataDirectory)
    ? camerasDataDirectory
    : Path.Combine(builder.Environment.ContentRootPath, camerasDataDirectory);
builder.Services.AddSingleton<ICameraStore>(new SqliteCameraStore(camerasRoot));
builder.Services.AddSingleton<HlsStreamManager>();

// Trigger zones have their own detection database and image folder, separate from the main
// app's. What happens after a trigger fires is opt-in per camera - see AlertForwarder for the
// (off by default) bridge into the main app's own Ollama analysis + email/Telegram/webhook pipeline.
var triggerStore = new SqliteTriggerStore(camerasRoot);
builder.Services.AddSingleton<ITriggerConfigStore>(triggerStore);
builder.Services.AddSingleton<ITriggerEventStore>(triggerStore);
builder.Services.AddSingleton<ClipRecorder>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AlertForwarder>();
builder.Services.AddHostedService<TriggerZoneMonitor>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapSettingsEndpoints();
app.MapCameraEndpoints();
app.MapTriggerEndpoints();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/api/events", async (IEventStore store, CancellationToken ct, int limit = 50) =>
{
    var capped = Math.Clamp(limit, 1, 500);
    var events = await store.GetRecentEventsAsync(capped, ct);
    return Results.Ok(events.Select(e => new
    {
        e.Id,
        e.Camera,
        occurredAt = e.OccurredAt,
        e.Status,
        eventType = e.EventType ?? "unknown",
        summary = e.Summary ?? string.Empty,
        notified = e.NotificationRecommended == true,
        notificationReason = e.NotificationReason ?? string.Empty,
        confidence = e.Confidence ?? 0,
        hasImage = !string.IsNullOrWhiteSpace(e.ImagePath) && File.Exists(e.ImagePath)
    }));
});

app.MapGet("/api/events/{id:guid}/image", async (Guid id, IEventStore store, CancellationToken ct) =>
{
    var record = await store.GetEventAsync(id, ct);
    if (record?.ImagePath is null || !File.Exists(record.ImagePath))
        return Results.NotFound();

    var bytes = await File.ReadAllBytesAsync(record.ImagePath, ct);
    return Results.File(bytes, "image/jpeg");
});

app.Run();
