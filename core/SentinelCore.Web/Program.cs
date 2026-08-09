using System.Security.Cryptography;
using System.Text.Json.Serialization;
using SentinelCore;
using SentinelCore.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

// Settings this version introduced that the installed appsettings.json predates - see
// ConfigDefaultsSeeder, and the matching call at the top of the bridge's Program.cs. Only this
// project's own directory: the bridge seeds its own, and this process already writes that file
// through AppSettingsFile without needing to seed it too.
var configSeed = ConfigDefaultsSeeder.Seed(AppContext.BaseDirectory);
if (configSeed.Describe() is { } configSeedMessage) Console.WriteLine(configSeedMessage);

var builder = WebApplication.CreateBuilder(args);

// Before anything reads the listening address - and this project reads it early, to work out the
// HTTP port Kestrel and mDNS should use. Lets ASPNETCORE_URLS outrank appsettings.json, which the
// framework's own source ordering otherwise reverses; see HostUrls. This is what makes the install
// scripts' WEB_PORT option do anything.
if (HostUrls.ResolveOverride(args, Environment.GetEnvironmentVariable(HostUrls.EnvironmentVariable)) is { } urlsOverride)
    builder.Configuration[HostUrls.ConfigurationKey] = urlsOverride;

// Community license: this is personal/self-hosted software, well under QuestPDF's revenue
// threshold for free use - see https://www.questpdf.com/license/. Registers the DejaVu fonts
// shipped in Fonts/ so DocsPdfRenderer (Docs tab) renders consistently regardless of what fonts,
// if any, happen to be installed on the host - not something to assume on a Raspberry Pi image.
QuestPDF.Settings.License = LicenseType.Community;
var fontsDirectory = Path.Combine(builder.Environment.ContentRootPath, "Fonts");
if (Directory.Exists(fontsDirectory))
{
    foreach (var fontFile in Directory.GetFiles(fontsDirectory, "*.ttf"))
    {
        using var fontStream = File.OpenRead(fontFile);
        FontManager.RegisterFont(fontStream);
    }
}

// So enums like CameraProbeStatus serialize as readable strings ("Found", "NeedsCredentials")
// rather than raw integers - the first enum this project has put on the wire.
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Lets the same binaries run as a Windows Service or a systemd unit without a separate build.
// Both are no-ops when the process wasn't started by that service manager - including when it's
// run straight from a terminal - so this is safe on every platform.
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// HTTPS, added alongside the existing HTTP listener rather than replacing it. On by default -
// see HttpsSettings. When no certificate has been explicitly configured (the shipped default),
// one is generated automatically right here so nobody has to run a script or hand-edit this
// file to get it; see AutoCertificateProvisioner for how and where. An explicit
// CertificatePath/KeyPath (e.g. a real domain certificate - docs/DEPLOYMENT.md Route B) is
// always left untouched.
var httpsSettings = HttpsSettings.FromConfiguration(builder.Configuration);
if (httpsSettings.Enabled && string.IsNullOrWhiteSpace(httpsSettings.CertificatePath) && string.IsNullOrWhiteSpace(httpsSettings.KeyPath))
{
    var certDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "certs"));
    try
    {
        var provisioned = AutoCertificateProvisioner.EnsureCertificate(certDirectory);
        httpsSettings = httpsSettings with { CertificatePath = provisioned.CertificatePath, KeyPath = provisioned.KeyPath };

        // Written back into configuration (not just the local variable above) so every other
        // place that re-reads Https via IConfiguration - e.g. the Internet Exposed Mode
        // validation in SettingsEndpoints.cs - sees the same certificate this process is
        // actually serving, without needing the change persisted to appsettings.json on disk.
        builder.Configuration["Https:CertificatePath"] = provisioned.CertificatePath;
        builder.Configuration["Https:KeyPath"] = provisioned.KeyPath;

        if (provisioned.Reissued)
        {
            Console.WriteLine($"Generated a local HTTPS certificate authority and server certificate in {certDirectory}.");
            Console.WriteLine($"To remove the browser warning, trust {provisioned.AuthorityCertificatePath} on each device you browse from - see docs/DEPLOYMENT.md section 14a.");
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
    {
        // Falls through to plain HTTP below rather than crashing the whole dashboard over a
        // certificate problem - e.g. a read-only filesystem or a permissions issue on certDirectory.
        Console.WriteLine($"Could not auto-generate an HTTPS certificate ({ex.Message}). Continuing on plain HTTP.");
    }
}
var httpsEnabled = httpsSettings.CanEnable(out var httpsSkipReason);

// A third, dedicated port for traffic reaching this server from outside the LAN (e.g. via a
// Cloudflare Tunnel) - requires HTTPS to already be enabled, and reuses its certificate. See
// InternetExposedSettings for why: requests on this port always require login and are always
// genuine HTTPS, by construction, with no forwarded-header trust needed.
var internetExposedSettings = InternetExposedSettings.FromConfiguration(builder.Configuration);
// CanEnable already re-validates httpsSettings.CanEnable() itself, so this doesn't need (and
// must not short-circuit on) the outer httpsEnabled flag - that would leave internetExposedSkipReason
// unassigned whenever httpsEnabled was false.
var internetExposedEnabled = internetExposedSettings.CanEnable(httpsSettings, out var internetExposedSkipReason);

// Read regardless of httpsEnabled - MdnsAdvertiserService needs a real listening port to
// advertise even when HTTPS itself is off.
var httpPort = HttpsSettings.ParseHttpPort(builder.Configuration["Urls"]);

if (httpsEnabled)
{
    // Calling Listen() at all makes Kestrel ignore the Urls setting entirely, so the existing
    // HTTP port has to be re-declared here explicitly or it would silently stop listening -
    // which would break Blue Iris, both mobile apps, and any existing bookmark at once.

    // Loaded once here rather than per-connection - see HttpsSettings.LoadCertificate for why
    // this can't just be UseHttps(certPath, keyPath).
    var certificate = httpsSettings.LoadCertificate();

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(httpPort);
        kestrel.ListenAnyIP(httpsSettings.Port, listen => listen.UseHttps(certificate));
        if (internetExposedEnabled)
            kestrel.ListenAnyIP(internetExposedSettings.Port, listen => listen.UseHttps(certificate));
    });
}

// Steers discovered clients toward the HTTPS cert-trust flow (AutoCertificateProvisioner) when
// it's available, rather than training them to expect plaintext by default.
var discoveryEndpoint = new DiscoveryEndpoint(httpsEnabled, httpsEnabled ? httpsSettings.Port : httpPort);

// Reflects whether the mode is *actually* active (certificate loadable, port valid) rather than
// just requested - AuthGateMiddleware and /api/auth/status trust this, not the raw config.
builder.Services.AddSingleton(internetExposedSettings with { Enabled = internetExposedEnabled });

// Only DataDirectory is used here (to find the shared events.db/images), so this
// deliberately skips the main app's ApiKey validation - this project has no API key.
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));

builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
builder.Services.AddSingleton<IPushDeviceStore, SqlitePushDeviceStore>();
// Loads its own copy of the ONNX face-detection/embedding models - this is a separate process
// from the main app, so enrollment (computing an embedding for an uploaded reference photo)
// needs its own IFaceRecognitionService rather than sharing the main app's in-memory instance.
builder.Services.AddSingleton<IFaceRecognitionService, FaceRecognitionService>();
// Same reasoning as IFaceRecognitionService above: a separate process from the main app, so the
// Feed's visual search box needs its own instance to encode search-query text. Model download/
// load is deferred to first actual search (see ImageEmbeddingService.EnsureInitializedAsync), so
// this line alone costs nothing until semantic search is both enabled and used.
builder.Services.AddHttpClient<IImageEmbeddingService, ImageEmbeddingService>();
builder.Services.AddSingleton<PushNotificationDispatcher>();
builder.Services.AddSingleton<ITimelineReviewStore, SqliteTimelineReviewStore>();

var mainAppSettingsPath = builder.Configuration["MainAppSettingsPath"]
    ?? throw new InvalidOperationException("MainAppSettingsPath is not configured.");
var appSettingsFile = new AppSettingsFile(mainAppSettingsPath);
builder.Services.AddSingleton(appSettingsFile);

// A second AppSettingsFile instance, pointed at this project's own appsettings.json rather than
// the main app's - reuses the same safe read/merge/write logic for settings that configure this
// project's own Kestrel binding (Internet Exposed Mode), which can't live in the shared file.
var webAppSettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
builder.Services.AddKeyedSingleton("web", new AppSettingsFile(webAppSettingsPath));

// Local-network discovery (mDNS) - see DiscoverySettings/MdnsAdvertiserService. Unlike
// Bridge:ApiKey (AutoApiKeyProvisioner), InstanceId is never written back to appsettings.json:
// it's not a secret (only ever handed out over unauthenticated multicast, same as any other TXT
// record here) and doesn't need to survive a restart to do its job of telling two same-hostname
// boxes on one LAN apart, so MdnsAdvertiserService just generates one in memory when it's blank.
builder.Services.AddSingleton(DiscoverySettings.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(discoveryEndpoint);
builder.Services.AddHostedService<MdnsAdvertiserService>();

// Cameras added through the UI are stored separately from the main app's data entirely -
// they're for live viewing only and the alert pipeline never sees them.
var camerasDataDirectory = builder.Configuration["CamerasDataDirectory"] ?? "data";
var camerasRoot = Path.IsPathRooted(camerasDataDirectory)
    ? camerasDataDirectory
    : Path.Combine(builder.Environment.ContentRootPath, camerasDataDirectory);
// SqliteCameraStore backs two interfaces at once, same reason SqliteTriggerStore below does.
var cameraStore = new SqliteCameraStore(camerasRoot);
builder.Services.AddSingleton<ICameraStore>(cameraStore);
builder.Services.AddSingleton<ICameraGroupStore>(cameraStore);
// Resolves where a camera's video is actually read from, which differs for browser-published
// cameras - see CameraSource. Registered before HlsStreamManager, which depends on it.
builder.Services.AddSingleton<CameraSource>();
builder.Services.AddSingleton<HlsStreamManager>();
// How each person has dragged their Live tiles around - a per-account view preference, kept out
// of cameras.db so the camera list itself stays the one canonical, order-agnostic answer.
builder.Services.AddSingleton<ILiveOrderStore>(new SqliteLiveOrderStore(camerasRoot));
// WebRTC live view via a go2rtc sidecar - see Go2RtcManager. Generous timeout: the SDP exchange
// includes go2rtc gathering its ICE candidates, and the first request may also be paying the
// sidecar's startup wait.
builder.Services.AddHttpClient(nameof(Go2RtcManager), client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<Go2RtcManager>();
// Have the sidecar up before the first viewer arrives rather than on their time - see
// Go2RtcWarmUpService. Optional and non-blocking: an install without go2rtc boots unchanged.
builder.Services.AddHostedService<Go2RtcWarmUpService>();

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

// The session store above only covers the ticket data - the cookie ASP.NET Core actually hands
// the client is still encrypted/signed via Data Protection, which without an explicit key
// location falls back to an ephemeral (in-memory) key ring on Linux/systemd. That silently
// undoes the "redeploy shouldn't log people out" intent below: every restart would generate a
// fresh key ring and invalidate every outstanding cookie even though its ticket was still sitting
// in SqliteAccountStore. Persisting keys next to the other durable state fixes that.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(camerasRoot, "keys")))
    .SetApplicationName("SentinelCore");

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
builder.Services.AddSingleton<TriggerRewindService>();
builder.Services.AddHttpClient();
// Longer than the default 100s - the main app's own chat timeout is 300s (TelegramChatOptions.
// TimeoutSeconds, confirmed against a real, busy camera's event history that less wasn't
// always enough), so this needs headroom above that or the proxy would cut the request off
// before the main app itself gives up and returns its own graceful "took too long" message.
builder.Services.AddHttpClient(nameof(ChatEndpoints), client => client.Timeout = TimeSpan.FromSeconds(330));
// Short, because the overwhelmingly likely failure is a server with no route out at all, and
// the Settings form should say so promptly rather than appearing to hang.
builder.Services.AddHttpClient(nameof(LocationLookupEndpoints), client => client.Timeout = TimeSpan.FromSeconds(8));
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

// Holds a trigger out of the Triggers tab until the main app's analysis confirms something was
// really in frame (on by default - see TriggerConfirmationService). The client polls
// /api/results/{id} on loopback while an analysis runs, which on a CPU-only box is minutes, so the
// default 100s HttpClient timeout would cut individual polls off mid-wait.
builder.Services.AddHttpClient(nameof(TriggerConfirmationService), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<TriggerConfirmationService>();
builder.Services.AddHostedService<TriggerConfirmationSweeper>();

builder.Services.AddHostedService<TriggerZoneMonitor>();

// Ages out rejected motion and its clips. Lives here rather than beside the main app's
// RetentionWorker because trigger-images and trigger-clips are this project's data, under this
// project's data root, and that worker is deliberately scoped to its own.
builder.Services.AddHostedService<MotionRetentionWorker>();

// Triggering a camera from outside the motion detector - the key-authenticated HTTP endpoint and
// a Device Hub sensor firing both land in CameraTriggerService. The watcher holds no broker
// connection at all until some camera actually has a sensor bound to it.
builder.Services.AddSingleton<CameraTriggerService>();
builder.Services.AddHttpClient(nameof(SensorTriggerWatcher));
builder.Services.AddHostedService<SensorTriggerWatcher>();

// Gate/door open check - its own small table in the same cameras database, own periodic
// snapshot loop, own forwarder to a dedicated main-app endpoint (not AlertForwarder's - see
// GateCheckForwarder for why). Opt-in per camera, off unless a GateChecks row exists for it.
//
// Unlike AlertForwarder (which posts to the queue-based /api/blueiris/alert and gets an
// immediate 202 back), this endpoint is synchronous - it waits out the full Ollama analysis
// before responding, so it needs the same generous timeout as ChatEndpoints above, not the
// default 100s. Missing this crashed the whole host the first time it ran live: the timeout's
// TaskCanceledException is an OperationCanceledException, which GateCheckMonitor's own
// "not OperationCanceledException" catch filters were (wrongly) written to let through as if it
// meant the service was shutting down - see the fixed filters there for the other half of this.
builder.Services.AddSingleton<IGateCheckStore>(new SqliteGateCheckStore(camerasRoot));
builder.Services.AddHttpClient(nameof(GateCheckForwarder), client => client.Timeout = TimeSpan.FromSeconds(330));
builder.Services.AddSingleton<GateCheckForwarder>();
builder.Services.AddHostedService<GateCheckMonitor>();

// Reads BugReports/Email straight from the main app's appsettings.json (this project has no
// SMTP config of its own) - see WebBugReportSender.
builder.Services.AddSingleton<IBugReportSender, WebBugReportSender>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new BugReportLoggerProvider("SentinelCore.Web", sp));

// Alerts the homeowner (not the developer) on a failed dashboard login - see LoginAlertSender.
// Same reasoning as WebBugReportSender: no SMTP/Telegram config of its own, reads Email/Telegram
// straight from the main app's appsettings.json.
builder.Services.AddHttpClient(nameof(LoginAlertSender));
builder.Services.AddSingleton<ILoginAlertSender, LoginAlertSender>();

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

if (internetExposedEnabled)
{
    app.Logger.LogInformation(
        "Internet Exposed Mode enabled on port {Port} - login and HTTPS are always required there. Point a Cloudflare Tunnel (or other reverse proxy) at this port, not the LAN one.",
        internetExposedSettings.Port);
}
else if (internetExposedSkipReason is not null)
{
    app.Logger.LogWarning("{Reason} Internet Exposed Mode is not active.", internetExposedSkipReason);
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
// For the MSE live-view path: the dashboard opens a WebSocket to /api/cameras/{id}/mse, which
// this server bridges to go2rtc's loopback API. Same-origin, so the session cookie rides the
// handshake and RequireAuthorization applies as usual.
app.UseWebSockets();
app.MapSettingsEndpoints();
app.MapCameraEndpoints();
app.MapCameraPublishEndpoints();
app.MapCameraGroupEndpoints();
app.MapLiveOrderEndpoints();
app.MapKnownFaceEndpoints();
app.MapGateCheckEndpoints();
app.MapCameraDiscoveryEndpoints();
app.MapCameraPtzEndpoints();
app.MapTriggerEndpoints();
app.MapActivityEndpoints();
app.MapCameraTriggerEndpoints();
app.MapSetupEndpoints();
app.MapLicenseEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapBackupEndpoints();
app.MapChatEndpoints();
app.MapQueueEndpoints();
app.MapSystemStatusEndpoints();
app.MapAutomationRuleProxyEndpoints();
app.MapScheduleProxyEndpoints();
app.MapLocationLookupEndpoints();
app.MapDeviceHubProxyEndpoints();
app.MapHomeKitEndpoints();
app.MapPushEndpoints();
app.MapTimelineEndpoints();
app.MapDocsEndpoints();

// Reports this process's own build, which is not necessarily the bridge's - they're separate
// executables and nothing stops one being updated without the other. The dashboard footer reads
// this, and it stays unauthenticated like the rest of the endpoint: the version of software
// already answering on the port isn't a secret, and requiring a login would make it useless for
// the "is it even up, and what is it?" check this exists for.
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    build = BuildInfo.Current,
    timestamp = DateTimeOffset.UtcNow
}));

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
    IEventStore store, IImageEmbeddingService imageEmbeddings, AppSettingsFile appSettings, CancellationToken ct, int limit = 50,
    string? camera = null, string? eventType = null, bool? notifiedOnly = null,
    string? search = null, DateTimeOffset? from = null, DateTimeOffset? to = null, bool visualSearch = false) =>
{
    var filter = new EventFilter(camera, eventType, notifiedOnly, search, from, to, Math.Clamp(limit, 1, 500));

    IReadOnlyList<AlertEventRecord> events;
    // Reads live from the shared settings file rather than a bound IOptionsMonitor - this
    // project's own configuration doesn't include the main app's appsettings.json (see
    // mainAppSettingsPath above), so an IOptionsMonitor here would never see Settings UI edits.
    var semanticSearch = visualSearch && !string.IsNullOrWhiteSpace(search)
        ? await appSettings.GetSectionAsync<SemanticSearchSettingsDto>("SemanticSearch", ct)
        : null;
    if (semanticSearch is { Enabled: true })
    {
        var queryEmbedding = await imageEmbeddings.EmbedTextAsync(search!, ct);
        events = queryEmbedding is not null
            ? await store.SearchBySimilarityAsync(filter, queryEmbedding, semanticSearch.MinimumSimilarity, ct)
            : [];
    }
    else
    {
        events = await store.SearchEventsAsync(filter, ct);
    }

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
        hasImage = !string.IsNullOrWhiteSpace(e.ImagePath) && File.Exists(e.ImagePath),
        linkedCameras = e.LinkedCameras,
        incidentId = e.IncidentId,
        packageTheftReason = e.PackageTheftReason
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
            hasImage = !string.IsNullOrWhiteSpace(e.ImagePath) && File.Exists(e.ImagePath),
            linkedCameras = e.LinkedCameras,
            incidentId = e.IncidentId,
            packageTheftReason = e.PackageTheftReason
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
