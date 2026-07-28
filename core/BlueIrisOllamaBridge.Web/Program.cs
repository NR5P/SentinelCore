using System.Text.Json.Serialization;
using BlueIrisOllamaBridge;
using BlueIrisOllamaBridge.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// So enums like CameraProbeStatus serialize as readable strings ("Found", "NeedsCredentials")
// rather than raw integers - the first enum this project has put on the wire.
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Lets the same binaries run as a Windows Service or a systemd unit without a separate build.
// Both are no-ops when the process wasn't started by that service manager - including when it's
// run straight from a terminal - so this is safe on every platform.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// Optional HTTPS, added alongside the existing HTTP listener rather than replacing it. Whether
// it's on is decided by whether a certificate is actually present on disk - see HttpsSettings.
var httpsSettings = HttpsSettings.FromConfiguration(builder.Configuration);
var httpsEnabled = httpsSettings.CanEnable(out var httpsSkipReason);
if (httpsEnabled)
{
    // Calling Listen() at all makes Kestrel ignore the Urls setting entirely, so the existing
    // HTTP port has to be re-declared here explicitly or it would silently stop listening -
    // which would break Blue Iris, both mobile apps, and any existing bookmark at once.
    var httpPort = HttpsSettings.ParseHttpPort(builder.Configuration["Urls"]);

    // Loaded once here rather than per-connection - see HttpsSettings.LoadCertificate for why
    // this can't just be UseHttps(certPath, keyPath).
    var certificate = httpsSettings.LoadCertificate();

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(httpPort);
        kestrel.ListenAnyIP(httpsSettings.Port, listen => listen.UseHttps(certificate));
    });
}

// Only DataDirectory is used here (to find the shared events.db/images), so this
// deliberately skips the main app's ApiKey validation - this project has no API key.
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));

builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
builder.Services.AddSingleton<IPushDeviceStore, SqlitePushDeviceStore>();
builder.Services.AddSingleton<PushNotificationDispatcher>();
builder.Services.AddSingleton<ITimelineReviewStore, SqliteTimelineReviewStore>();

var mainAppSettingsPath = builder.Configuration["MainAppSettingsPath"]
    ?? throw new InvalidOperationException("MainAppSettingsPath is not configured.");
var appSettingsFile = new AppSettingsFile(mainAppSettingsPath);
builder.Services.AddSingleton(appSettingsFile);

// Cameras added through the UI are stored separately from the main app's data entirely -
// they're for live viewing only and the alert pipeline never sees them.
var camerasDataDirectory = builder.Configuration["CamerasDataDirectory"] ?? "data";
var camerasRoot = Path.IsPathRooted(camerasDataDirectory)
    ? camerasDataDirectory
    : Path.Combine(builder.Environment.ContentRootPath, camerasDataDirectory);
builder.Services.AddSingleton<ICameraStore>(new SqliteCameraStore(camerasRoot));
builder.Services.AddSingleton<HlsStreamManager>();

// Short timeout on purpose - a camera being probed is either on the LAN and answers in
// milliseconds, or it isn't there, and the whole point of the probe is to fail fast on the ones
// that aren't rather than let a single bad IP stall out a user-facing request.
builder.Services.AddHttpClient<OnvifClient>(client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddSingleton<CameraDiscoveryService>();

// Dashboard accounts - separate from the main app's data for the same reason cameras are. Also
// backs the cookie authentication handler's session storage below (same instance, same reason
// SqliteTriggerStore backs two interfaces at once).
var accountStore = new SqliteAccountStore(camerasRoot);
builder.Services.AddSingleton<IAccountStore>(accountStore);

// Real per-account cookie login, replacing the old single-shared-credential hand-rolled check.
// Sessions persist in accountStore's Sessions table (SqliteAccountStore.SessionStore below), so
// a redeploy/restart no longer logs every signed-in person out.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "session"; // keep the existing cookie name - no forced re-login on upgrade
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.SessionStore = accountStore;
        // This is an API, not an MVC app - never redirect to a login page, just report the
        // status code and let the SPA/mobile clients react to it.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(AccountRole.Admin))));

// Trigger zones have their own detection database and image folder, separate from the main
// app's. What happens after a trigger fires is opt-in per camera - see AlertForwarder for the
// (off by default) bridge into the main app's own Ollama analysis + email/Telegram/webhook pipeline.
var triggerStore = new SqliteTriggerStore(camerasRoot);
builder.Services.AddSingleton<ITriggerConfigStore>(triggerStore);
builder.Services.AddSingleton<ITriggerEventStore>(triggerStore);
builder.Services.AddSingleton<ClipRecorder>();
builder.Services.AddHttpClient();
// Longer than the default 100s - the main app's own chat timeout is 300s (TelegramChatOptions.
// TimeoutSeconds, confirmed against a real, busy camera's event history that less wasn't
// always enough), so this needs headroom above that or the proxy would cut the request off
// before the main app itself gives up and returns its own graceful "took too long" message.
builder.Services.AddHttpClient(nameof(ChatEndpoints), client => client.Timeout = TimeSpan.FromSeconds(330));
// The HomeKit sidecar is on loopback and only ever asked for a status blob or a small PNG, so a
// short timeout is right here - if it's not answering promptly it's not running, and the settings
// UI should say so immediately rather than hanging.
//
// RemoveAllLoggers because "the sidecar isn't running" is the normal state for anyone not using
// HomeKit, and it's a condition HomeKitEndpoints already handles and reports in the UI. Without
// this, the factory's own logging writes a connection-refused stack trace on every settings page
// load - four log lines apiece - for something that isn't an error.
builder.Services.AddHttpClient(nameof(HomeKitEndpoints), client => client.Timeout = TimeSpan.FromSeconds(5))
    .RemoveAllLoggers();
builder.Services.AddSingleton<AlertForwarder>();
builder.Services.AddHostedService<TriggerZoneMonitor>();

// Reads BugReports/Email straight from the main app's appsettings.json (this project has no
// SMTP config of its own) - see WebBugReportSender.
builder.Services.AddSingleton<IBugReportSender, WebBugReportSender>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new BugReportLoggerProvider("BlueIrisOllamaBridge.Web", sp));

// Records whether anything has reached this dashboard from outside the LAN - see ExposureTracker.
builder.Services.AddSingleton<ExposureTracker>();

var app = builder.Build();

// One-time: promotes a pre-upgrade install's single shared Auth:Username/PasswordHash into the
// first Admin account, so nobody who could already log in loses access. A no-op on every
// startup after the first (and for any install that never had login enabled) - see
// AccountMigration for details.
await AccountMigration.MigrateLegacyAuthIfNeededAsync(accountStore, appSettingsFile, CancellationToken.None);

if (httpsEnabled)
{
    app.Logger.LogInformation("HTTPS enabled on port {Port} using {Certificate}. Plain HTTP is still listening.",
        httpsSettings.Port, httpsSettings.CertificatePath);
}
else if (httpsSkipReason is not null)
{
    // Only when it was configured but unusable - staying quiet otherwise, since running on plain
    // HTTP is the shipped default and not something to nag about.
    app.Logger.LogWarning("{Reason} Continuing on plain HTTP. See docs/DEPLOYMENT.md to set up HTTPS.", httpsSkipReason);
}

// First in the pipeline so it sees every request, including static files: someone loading the
// dashboard itself from off-LAN is exactly the case worth knowing about.
app.UseMiddleware<ExposureWarningMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles();
// UseAuthentication populates context.User from the session cookie (if any). AuthGateMiddleware
// then overrides context.User with a synthetic Admin when Auth:Enabled is false, so
// UseAuthorization's per-endpoint RequireAuthorization checks always pass in that state.
app.UseAuthentication();
app.UseMiddleware<AuthGateMiddleware>();
app.UseAuthorization();
app.MapSettingsEndpoints();
app.MapCameraEndpoints();
app.MapCameraDiscoveryEndpoints();
app.MapTriggerEndpoints();
app.MapSetupEndpoints();
app.MapLicenseEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapBackupEndpoints();
app.MapChatEndpoints();
app.MapHomeKitEndpoints();
app.MapPushEndpoints();
app.MapTimelineEndpoints();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));

// Drives the dashboard's exposure banner. Reports only what's actually been observed - it stays
// quiet until something genuinely reaches this host from off-LAN.
app.MapGet("/api/exposure/status", async (ExposureTracker tracker, AppSettingsFile settings, CancellationToken ct) =>
{
    var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", ct);
    return Results.Ok(new
    {
        externalRequestSeen = tracker.HasSeenExternalRequest,
        lastExternalAddress = tracker.LastExternalAddress,
        lastExternalAt = tracker.LastExternalAt,
        loginEnabled = auth is { Enabled: true }
    });
    // The old blanket AuthGateMiddleware gated everything under /api bar three named paths, so
    // this was covered without saying so. Under the per-endpoint model it has to opt in, or it
    // would hand an unauthenticated caller whether a login exists and the last off-LAN address
    // that reached this host. Harmless when the login is off - the synthetic Admin principal
    // satisfies it - so the exposure banner keeps working in the shipped default.
}).RequireAuthorization();

app.MapGet("/api/events", async (
    IEventStore store, CancellationToken ct, int limit = 50,
    string? camera = null, string? eventType = null, bool? notifiedOnly = null,
    string? search = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
{
    var filter = new EventFilter(camera, eventType, notifiedOnly, search, from, to, Math.Clamp(limit, 1, 500));
    var events = await store.SearchEventsAsync(filter, ct);
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
}).RequireAuthorization();

// Independent of any currently-applied filter, so the dropdowns always show every camera/
// event type that has ever appeared, not just what's in the current filtered result set.
app.MapGet("/api/events/filter-options", async (IEventStore store, CancellationToken ct) =>
{
    var options = await store.GetFilterOptionsAsync(ct);
    return Results.Ok(new { cameras = options.Cameras, eventTypes = options.EventTypes });
}).RequireAuthorization();

app.MapGet("/api/events/{id:guid}", async (Guid id, IEventStore store, CancellationToken ct) =>
{
    var e = await store.GetEventAsync(id, ct);
    return e is null
        ? Results.NotFound(new { error = "Event not found." })
        : Results.Ok(new
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
        });
}).RequireAuthorization();

app.MapGet("/api/events/{id:guid}/image", async (Guid id, IEventStore store, CancellationToken ct) =>
{
    var record = await store.GetEventAsync(id, ct);
    if (record?.ImagePath is null || !File.Exists(record.ImagePath))
        return Results.NotFound();

    var bytes = await File.ReadAllBytesAsync(record.ImagePath, ct);
    return Results.File(bytes, "image/jpeg");
}).RequireAuthorization();

app.Run();

public partial class Program;
