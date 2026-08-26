using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Networking;

/// <summary>
/// Everything this app asks of the SentinelCore.Web JSON API. A direct port of
/// ios/SentinelCore/Networking/SentinelClient.swift - same endpoints, same order, same rules about
/// which bodies must round-trip untouched.
/// </summary>
/// <remarks>
/// The app talks to the dashboard's JSON API only. It never connects directly to cameras or to the
/// main bridge on port 5080.
/// </remarks>
public sealed class SentinelClient : IDisposable
{
    // This client only targets a server on the current LAN (or a tunnel to it). A long timeout
    // leaves the UI in CONNECTING when the host is powered off or this PC is on another network,
    // so fail promptly and let the user retry as soon as the server is available.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ISentinelLog _log;
    private readonly Func<string> _activeServerUrl;

    private HttpClient _http;
    private CookieContainer _cookies;
    private HttpClientHandler _handler;

    public SentinelClient(Func<string> activeServerUrl, ISentinelLog log)
    {
        _activeServerUrl = activeServerUrl;
        _log = log;
        (_handler, _cookies, _http) = CreateSession();
    }

    /// <summary>The session cookies, so the WebView2 live player can be handed the same session.</summary>
    public CookieContainer Cookies => _cookies;

    /// <summary>
    /// Throws away the HTTP session and starts a fresh one, cookies included. Called on an
    /// explicit reconnect: a handler that has cached a dead connection or a stale DNS answer will
    /// otherwise keep failing after the network itself has recovered.
    /// </summary>
    public void ResetSession()
    {
        var old = _http;
        (_handler, _cookies, _http) = CreateSession();
        old.Dispose();
        _log.Log("Created a fresh network session");
    }

    private static (HttpClientHandler, CookieContainer, HttpClient) CreateSession()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SentinelCore-Windows/1.0");
        return (handler, cookies, http);
    }

    // MARK: - Connection and identity

    public Task<AuthStatus> AuthStatusAsync(CancellationToken ct = default) =>
        GetAsync<AuthStatus>("/api/auth/status", ct: ct);

    // Unauthenticated on the server side, so this is readable at the login screen too - which is
    // where knowing the server is too old is arguably most useful.
    public Task<ServerHealthResponse> ServerHealthAsync(CancellationToken ct = default) =>
        GetAsync<ServerHealthResponse>("/api/health", ct: ct);

    public Task<SecurityEvent> EventAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<SecurityEvent>($"/api/events/{id}", ct: ct);

    /// <summary>
    /// The merged Events list, and the only list this app fetches - it replaced separate calls to
    /// /api/events and /api/triggers, which returned two rows for one moment whenever a camera
    /// forwarded to AI analysis.
    /// </summary>
    /// <remarks>
    /// includeUnconfirmed is the "show hidden" toggle - motion the AI rejected as nothing, plus
    /// anything still waiting on a verdict. Off by default, matching the web dashboard.
    /// </remarks>
    public Task<List<ActivityItem>> ActivityAsync(
        string? search = null,
        bool notifiedOnly = false,
        bool includeUnconfirmed = false,
        CancellationToken ct = default)
    {
        // This list is the day-level audit trail now that it carries motion as well as analyses,
        // so a busy camera's whole day has to fit.
        var query = new List<KeyValuePair<string, string>> { new("limit", "500") };
        if (!string.IsNullOrEmpty(search)) query.Add(new KeyValuePair<string, string>("search", search));
        if (notifiedOnly) query.Add(new KeyValuePair<string, string>("notifiedOnly", "true"));
        if (includeUnconfirmed) query.Add(new KeyValuePair<string, string>("includeUnconfirmed", "true"));
        return GetAsync<List<ActivityItem>>("/api/activity", query, ct);
    }

    // MARK: - Cameras

    public Task<List<Models.Camera>> CamerasAsync(CancellationToken ct = default) =>
        GetAsync<List<Models.Camera>>("/api/cameras", ct: ct);

    public Task<Models.Camera> CreateCameraAsync(CameraInput input, CancellationToken ct = default) =>
        SendAsync<Models.Camera, CameraInput>("/api/cameras", HttpMethod.Post, input, ct);

    public Task UpdateCameraAsync(Guid id, CameraInput input, CancellationToken ct = default) =>
        SendNoResultAsync($"/api/cameras/{id}", HttpMethod.Put, input, ct);

    /// <summary>
    /// The one way to see a camera's real password. Deliberately per-camera and on demand: the
    /// camera list carries only the mask, so nothing holds a live device credential in memory until
    /// someone actually asks to look at it. Admin-only on the server.
    /// </summary>
    public async Task<string> CameraPasswordAsync(Guid id, CancellationToken ct = default) =>
        (await GetAsync<CameraPasswordResponse>($"/api/cameras/{id}/password", ct: ct).ConfigureAwait(false)).Password;

    public Task DeleteCameraAsync(Guid id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/cameras/{id}", HttpMethod.Delete, null, ct);

    /// <summary>
    /// How this account has arranged the Live grid. The server answers with a complete order
    /// covering every camera that currently exists - deleted ones dropped, ones it has never seen
    /// appended last - so there is nothing to merge here.
    /// </summary>
    public async Task<List<Guid>> LiveOrderAsync(CancellationToken ct = default) =>
        (await GetAsync<LiveOrder>("/api/live-order", ct: ct).ConfigureAwait(false)).CameraIds;

    public Task SaveLiveOrderAsync(List<Guid> cameraIds, CancellationToken ct = default) =>
        SendNoResultAsync("/api/live-order", HttpMethod.Put, new LiveOrder { CameraIds = cameraIds }, ct);

    public Task<CameraProbeResult> ProbeCameraAsync(CameraProbeRequest request, CancellationToken ct = default) =>
        SendAsync<CameraProbeResult, CameraProbeRequest>("/api/cameras/discovery/probe", HttpMethod.Post, request, ct);

    /// <summary>
    /// "Record this now" from the live view. The server turns it into the same kind of trigger a
    /// motion zone produces - same event, same Feed card - and attaches a clip covering the seconds
    /// either side of the press.
    /// </summary>
    public Task RecordNowAsync(Guid cameraId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/cameras/{cameraId}/record", HttpMethod.Post, null, ct);

    /// <summary>
    /// Which live transport a tile actually landed on, and why. Logged by the server, not stored -
    /// the question it answers ("was this PC on MSE or HLS at 9pm, and what pushed it down") is
    /// asked of the log alongside the go2rtc and ffmpeg lines that explain it.
    /// </summary>
    public Task ReportLivePathAsync(
        Guid cameraId, string tier, string outcome, string? reason, int elapsedMs, CancellationToken ct = default) =>
        SendNoResultAsync(
            $"/api/cameras/{cameraId}/live-path",
            HttpMethod.Post,
            new LivePathReportBody { Tier = tier, Outcome = outcome, Reason = reason, ElapsedMs = elapsedMs },
            ct);

    // Continuous-move semantics: the camera keeps moving at this velocity until PtzStopAsync is
    // called - there is no "move by this much and stop automatically" in this mode, so the joystick
    // UI is expected to call PtzStopAsync on release, not just PtzMoveAsync once.
    public Task PtzMoveAsync(Guid cameraId, double pan, double tilt, double zoom, CancellationToken ct = default) =>
        SendNoResultAsync(
            $"/api/cameras/{cameraId}/ptz/move",
            HttpMethod.Post,
            new PtzMoveBody { Pan = pan, Tilt = tilt, Zoom = zoom },
            ct);

    public Task PtzStopAsync(Guid cameraId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/cameras/{cameraId}/ptz/stop", HttpMethod.Post, null, ct);

    public Task PtzHomeAsync(Guid cameraId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/cameras/{cameraId}/ptz/home", HttpMethod.Post, null, ct);

    /// <summary>
    /// Saves wherever the camera is pointing right now as its home position. Separate from
    /// PtzHomeAsync for the same reason the server splits POST from PUT: one reads the stored
    /// position, the other replaces it.
    /// </summary>
    public Task PtzSaveHomeAsync(Guid cameraId, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/cameras/{cameraId}/ptz/home", HttpMethod.Put, null, ct);

    public Task<List<ScanInterface>> ScanInterfacesAsync(CancellationToken ct = default) =>
        GetAsync<List<ScanInterface>>("/api/cameras/discovery/interfaces", ct: ct);

    public Task<ScanStartResult> StartScanAsync(ScanRequest request, CancellationToken ct = default) =>
        SendAsync<ScanStartResult, ScanRequest>("/api/cameras/discovery/scan", HttpMethod.Post, request, ct);

    public Task<ScanJobSnapshot> ScanStatusAsync(Guid scanId, CancellationToken ct = default) =>
        GetAsync<ScanJobSnapshot>($"/api/cameras/discovery/scan/{scanId}", ct: ct);

    public Task<TriggerConfig> TriggerConfigAsync(Guid cameraId, CancellationToken ct = default) =>
        GetAsync<TriggerConfig>($"/api/cameras/{cameraId}/trigger-config", ct: ct);

    public Task<TriggerConfig> SaveTriggerConfigAsync(Guid cameraId, TriggerConfig config, CancellationToken ct = default) =>
        SendAsync<TriggerConfig, TriggerConfig>($"/api/cameras/{cameraId}/trigger-config", HttpMethod.Put, config, ct);

    /// <summary>Live ffmpeg snapshot, never cached - fetched fresh every time the zone editor opens.</summary>
    public Task<byte[]> ReferenceFrameAsync(Guid cameraId, CancellationToken ct = default) =>
        GetBytesAsync($"/api/cameras/{cameraId}/reference-frame", ct);

    /// <summary>
    /// The bytes at a server-relative path, over this session. For the snapshots the feed, the
    /// timeline and the live tiles show, and for saving one to a file.
    /// </summary>
    /// <remarks>
    /// Public so that nothing else has to build its own HTTP stack to fetch an authenticated image.
    /// Two callers used to construct - and dispose - an HttpClient and handler per image, which
    /// leaves each one's sockets in TIME_WAIT for minutes afterwards; against a feed that fetches a
    /// snapshot per row and reloads every 30 seconds, that is a steady climb towards running out of
    /// ephemeral ports. Sharing the one handler this class already owns also means these requests
    /// carry the same cookies and time out on the same rules as every other call.
    /// </remarks>
    public Task<byte[]> DownloadAsync(string path, CancellationToken ct = default) =>
        GetBytesAsync(path, ct);

    public Task<List<PromptProfileName>> PromptProfileNamesAsync(CancellationToken ct = default) =>
        GetAsync<List<PromptProfileName>>("/api/prompt-profile-names", ct: ct);

    // MARK: - Known faces

    public Task<List<KnownFace>> KnownFacesAsync(CancellationToken ct = default) =>
        GetAsync<List<KnownFace>>("/api/known-faces", ct: ct);

    public Task<KnownFace> CreateKnownFaceAsync(
        string label, byte[] imageData, string fileName = "photo.jpg", string mimeType = "image/jpeg",
        CancellationToken ct = default) =>
        SendMultipartAsync<KnownFace>(
            "/api/known-faces",
            new Dictionary<string, string> { ["label"] = label },
            "image", imageData, fileName, mimeType, ct);

    public Task<KnownFace> AddKnownFaceEnrollmentAsync(
        string faceId, byte[] imageData, string fileName = "photo.jpg", string mimeType = "image/jpeg",
        CancellationToken ct = default) =>
        SendMultipartAsync<KnownFace>(
            $"/api/known-faces/{Uri.EscapeDataString(faceId)}/enrollments",
            new Dictionary<string, string>(),
            "image", imageData, fileName, mimeType, ct);

    public Task DeleteKnownFaceEnrollmentAsync(string faceId, string enrollmentId, CancellationToken ct = default) =>
        SendNoResultAsync<object>(
            $"/api/known-faces/{Uri.EscapeDataString(faceId)}/enrollments/{Uri.EscapeDataString(enrollmentId)}",
            HttpMethod.Delete, null, ct);

    public Task DeleteKnownFaceAsync(string id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/known-faces/{Uri.EscapeDataString(id)}", HttpMethod.Delete, null, ct);

    // MARK: - Timeline

    public Task<RecordingTimelineDay> TimelineAsync(string camera, string date, CancellationToken ct = default) =>
        GetAsync<RecordingTimelineDay>(
            "/api/timeline/day",
            new List<KeyValuePair<string, string>>
            {
                new("camera", camera),
                new("date", date),
            },
            ct);

    public Task<List<string>> TimelineCamerasAsync(CancellationToken ct = default) =>
        GetAsync<List<string>>("/api/timeline/cameras", ct: ct);

    public Task SetReviewedAsync(Guid eventId, bool reviewed, CancellationToken ct = default) =>
        SendNoResultAsync($"/api/events/{eventId}/reviewed", HttpMethod.Put, new ReviewedBody { Reviewed = reviewed }, ct);

    // MARK: - Accounts and authentication

    public Task<List<Account>> AccountsAsync(CancellationToken ct = default) =>
        GetAsync<List<Account>>("/api/accounts", ct: ct);

    public Task<Account> CreateAccountAsync(
        string username, string password, string role, string? displayName, CancellationToken ct = default) =>
        SendAsync<Account, CreateAccountBody>(
            "/api/accounts",
            HttpMethod.Post,
            new CreateAccountBody { Username = username, Password = password, Role = role, DisplayName = displayName },
            ct);

    public Task UpdateAccountRoleAsync(Guid id, string role, CancellationToken ct = default) =>
        SendNoResultAsync($"/api/accounts/{id}/role", HttpMethod.Put, new UpdateRoleBody { Role = role }, ct);

    public Task ResetAccountPasswordAsync(Guid id, string newPassword, CancellationToken ct = default) =>
        SendNoResultAsync(
            $"/api/accounts/{id}/reset-password",
            HttpMethod.Post,
            new ResetPasswordBody { NewPassword = newPassword },
            ct);

    public Task DeleteAccountAsync(Guid id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/accounts/{id}", HttpMethod.Delete, null, ct);

    /// <summary>
    /// The server deliberately leaves the current session alone (doesn't invalidate it) - no
    /// separate re-login needed here, the existing cookie just keeps working.
    /// </summary>
    public Task ChangeMyPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default) =>
        SendNoResultAsync(
            "/api/accounts/change-password",
            HttpMethod.Post,
            new ChangePasswordBody { CurrentPassword = currentPassword, NewPassword = newPassword },
            ct);

    public Task LoginAsync(string username, string password, CancellationToken ct = default) =>
        SendNoResultAsync(
            "/api/auth/login",
            HttpMethod.Post,
            new LoginBody { Username = username, Password = password },
            ct);

    public Task LogoutAsync(CancellationToken ct = default) =>
        SendNoResultAsync<object>("/api/auth/logout", HttpMethod.Post, null, ct);

    /// <summary>
    /// Turning the dashboard's login requirement on creates the first admin account in the same
    /// step - there is deliberately no way to require a login with nobody able to satisfy it.
    /// </summary>
    public Task EnableDashboardLoginAsync(string username, string password, CancellationToken ct = default) =>
        SendNoResultAsync(
            "/api/auth/enable",
            HttpMethod.Post,
            new Dictionary<string, string> { ["username"] = username, ["password"] = password },
            ct);

    public Task DisableDashboardLoginAsync(CancellationToken ct = default) =>
        SendNoResultAsync<object>("/api/auth/disable", HttpMethod.Post, null, ct);

    // MARK: - Push, status and licence

    public Task<PushStatus> PushStatusAsync(CancellationToken ct = default) =>
        GetAsync<PushStatus>("/api/push/status", ct: ct);

    public Task<SystemStatus> SystemStatusAsync(CancellationToken ct = default) =>
        GetAsync<SystemStatus>("/api/system/status", ct: ct);

    public Task<PushDeliveryReport> TestPushNotificationAsync(CancellationToken ct = default) =>
        SendAsync<PushDeliveryReport, object>("/api/push/test", HttpMethod.Post, null, ct);

    /// <summary>
    /// Readable by any signed-in account, not just an Admin, precisely so this app can ask - see
    /// the note on the endpoint in LicenseEndpoints.cs.
    /// </summary>
    public Task<LicenseState> LicenseStateAsync(CancellationToken ct = default) =>
        GetAsync<LicenseState>("/api/license/status", ct: ct);

    /// <summary>
    /// The raw contents of a .lic file, sent as text/plain the way the install endpoint expects -
    /// it takes the file's bytes, not a path, because the server has no way to reach whatever
    /// machine the file was emailed to. Verified server-side against the embedded public key before
    /// anything is written, so a truncated pick can't clobber a working license.
    /// </summary>
    public async Task<LicenseState> InstallLicenseAsync(string contents, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireUri("/api/license/install"))
        {
            Content = new StringContent(contents, Encoding.UTF8, "text/plain"),
        };
        var data = await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
        return Deserialize<LicenseState>(data, "/api/license/install");
    }

    // MARK: - Settings-backed sections

    /// <summary>
    /// Prompt profiles arrive and leave as raw JSON rather than a typed object, and that is
    /// load bearing rather than laziness. The save endpoint replaces the whole PromptProfiles
    /// dictionary, so anything this app failed to model would be erased by the act of saving - a
    /// profile's crop region is a rectangle no form here should be editing, but it must survive
    /// being edited here. Keeping the original object and writing back only the handful of fields
    /// this screen actually shows means the rest round-trips untouched.
    /// </summary>
    public async Task<Dictionary<string, SettingsValue>> PromptProfilesAsync(CancellationToken ct = default)
    {
        var snapshot = await SettingsSnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.TryGetValue("promptProfiles", out var value) &&
               value.Kind == SettingsValue.ValueKind.Object &&
               value.ObjectValue is { } profiles
            ? profiles
            : new Dictionary<string, SettingsValue>();
    }

    public Task SavePromptProfilesAsync(Dictionary<string, SettingsValue> profiles, CancellationToken ct = default) =>
        SendNoResultAsync("/api/settings/prompt-profiles", HttpMethod.Put, profiles, ct);

    /// <summary>
    /// The backup is deliberately untyped: it is whatever the server considers its whole
    /// configuration, and a client that decoded it into a fixed shape would quietly drop any
    /// section added after this app was built - which is the one thing a backup must never do. It
    /// travels through this app as raw bytes and goes back exactly as it came.
    /// </summary>
    public Task<byte[]> ExportBackupAsync(CancellationToken ct = default) =>
        GetBytesAsync("/api/backup/export", ct);

    public async Task<BackupRestoreResult> ImportBackupAsync(byte[] contents, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RequireUri("/api/backup/import"))
        {
            Content = new ByteArrayContent(contents),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var data = await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
        return Deserialize<BackupRestoreResult>(data, "/api/backup/import");
    }

    public Task<HomeKitStatus> HomeKitStatusAsync(CancellationToken ct = default) =>
        GetAsync<HomeKitStatus>("/api/homekit/status", ct: ct);

    public async Task<HomeKitSettings> HomeKitSettingsAsync(CancellationToken ct = default)
    {
        var snapshot = await SettingsSnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.TryGetValue("homeKit", out var section) ||
            section.Kind != SettingsValue.ValueKind.Object ||
            section.ObjectValue is not { } fields)
        {
            return new HomeKitSettings();
        }

        return new HomeKitSettings
        {
            Enabled = fields.TryGetValue("enabled", out var enabled) && enabled.AsBool,
            SidecarBaseUrl = fields.TryGetValue("sidecarBaseUrl", out var url) && url.DisplayString.Length > 0
                ? url.DisplayString
                : "http://127.0.0.1:3321",
            TimeoutSeconds = fields.TryGetValue("timeoutSeconds", out var timeout) &&
                             double.TryParse(timeout.DisplayString, out var seconds)
                ? (int)seconds
                : 10,
            MinimumConfidence = fields.TryGetValue("minimumConfidence", out var confidence) &&
                                double.TryParse(confidence.DisplayString, out var value)
                ? value
                : 0.6,
        };
    }

    public Task SaveHomeKitSettingsAsync(HomeKitSettings settings, CancellationToken ct = default) =>
        SendNoResultAsync("/api/settings/homekit", HttpMethod.Put, settings, ct);

    /// <summary>
    /// The literal "RESET" is what the endpoint requires - an explicit confirmation string rather
    /// than a bare POST, since this drops the pairing and every Home app on the network has to pair
    /// again.
    /// </summary>
    public Task ResetHomeKitPairingAsync(CancellationToken ct = default) =>
        SendNoResultAsync("/api/homekit/reset", HttpMethod.Post, new Dictionary<string, string> { ["confirm"] = "RESET" }, ct);

    public async Task<List<KnownVehicle>> KnownVehiclesAsync(CancellationToken ct = default)
    {
        var snapshot = await SettingsSnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.TryGetValue("knownVehicles", out var section) ||
            section.Kind != SettingsValue.ValueKind.Array ||
            section.ArrayValue is not { } values)
        {
            return new List<KnownVehicle>();
        }

        return values
            .Where(value => value.Kind == SettingsValue.ValueKind.Object && value.ObjectValue is not null)
            .Select(value => new KnownVehicle
            {
                Plate = value.ObjectValue!.TryGetValue("plate", out var plate) ? plate.DisplayString : "",
                Label = value.ObjectValue!.TryGetValue("label", out var label) ? label.DisplayString : "",
            })
            .ToList();
    }

    /// <summary>
    /// The whole list every time, which is what the endpoint takes - it replaces the section rather
    /// than patching it, so a partial send would delete the rest.
    /// </summary>
    public Task SaveKnownVehiclesAsync(List<KnownVehicle> vehicles, CancellationToken ct = default) =>
        SendNoResultAsync("/api/settings/known-vehicles", HttpMethod.Put, vehicles, ct);

    // MARK: - Device Hub

    /// <summary>
    /// Whether the DEVICE HUB section should exist at all. Deliberately its own endpoint rather
    /// than read from /api/settings, which is Admin-only - a Viewer gets the tab too.
    /// </summary>
    public Task<DeviceHubEnabled> DeviceHubEnabledAsync(CancellationToken ct = default) =>
        GetAsync<DeviceHubEnabled>("/api/device-hub/enabled", ct: ct);

    public Task<List<HubDevice>> DevicesAsync(CancellationToken ct = default) =>
        GetAsync<List<HubDevice>>("/api/devices", ct: ct);

    public Task<DeviceHubMetadata> DeviceMetadataAsync(CancellationToken ct = default) =>
        GetAsync<DeviceHubMetadata>("/api/devices/metadata", ct: ct);

    /// <summary>
    /// The light poll while the Device Hub is on screen - no topics or payload strings, just what
    /// each device is currently doing.
    /// </summary>
    public Task<HubDeviceStateSnapshot> DeviceStatesAsync(CancellationToken ct = default) =>
        GetAsync<HubDeviceStateSnapshot>("/api/devices/state", ct: ct);

    public Task<DeviceCommandResult> SendDeviceCommandAsync(
        Guid id, string? action = null, int? brightness = null, CancellationToken ct = default) =>
        SendAsync<DeviceCommandResult, DeviceCommandInput>(
            $"/api/devices/{id}/command",
            HttpMethod.Post,
            new DeviceCommandInput { Action = action, Brightness = brightness },
            ct);

    public Task<HubDevice> CreateDeviceAsync(HubDeviceInput input, CancellationToken ct = default) =>
        SendAsync<HubDevice, HubDeviceInput>("/api/devices", HttpMethod.Post, input, ct);

    public Task<HubDevice> UpdateDeviceAsync(Guid id, HubDeviceInput input, CancellationToken ct = default) =>
        SendAsync<HubDevice, HubDeviceInput>($"/api/devices/{id}", HttpMethod.Put, input, ct);

    public Task DeleteDeviceAsync(Guid id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/devices/{id}", HttpMethod.Delete, null, ct);

    /// <summary>
    /// The whole arrangement in one PUT, like SaveLiveOrderAsync for camera tiles. The response is
    /// the reordered device list, which callers can ignore - the local move already happened.
    /// </summary>
    public Task<List<HubDevice>> SaveDeviceOrderAsync(List<Guid> deviceIds, CancellationToken ct = default) =>
        SendAsync<List<HubDevice>, DeviceOrderInput>(
            "/api/devices/order",
            HttpMethod.Put,
            new DeviceOrderInput { DeviceIds = deviceIds },
            ct);

    public Task<DeviceScanStartResult> StartDeviceScanAsync(CancellationToken ct = default) =>
        SendAsync<DeviceScanStartResult, object>("/api/devices/discover", HttpMethod.Post, null, ct);

    public Task<DeviceScanSnapshot> DeviceScanStatusAsync(Guid scanId, CancellationToken ct = default) =>
        GetAsync<DeviceScanSnapshot>($"/api/devices/discover/{scanId}", ct: ct);

    // MARK: - Schedules

    /// <summary>
    /// Schedules live in the main app and are proxied by the web host, exactly like automation
    /// rules - see ScheduleProxyEndpoints.cs. Reads need only a signed-in account; everything that
    /// changes or fires one is AdminOnly server-side.
    /// </summary>
    public Task<List<Schedule>> SchedulesAsync(CancellationToken ct = default) =>
        GetAsync<List<Schedule>>("/api/schedules", ct: ct);

    public Task<ScheduleMetadata> ScheduleMetadataAsync(CancellationToken ct = default) =>
        GetAsync<ScheduleMetadata>("/api/schedules/metadata", ct: ct);

    public Task<List<ScheduleRun>> ScheduleRunsAsync(Guid id, int limit = 25, CancellationToken ct = default) =>
        GetAsync<List<ScheduleRun>>(
            $"/api/schedules/{id}/runs",
            new List<KeyValuePair<string, string>> { new("limit", limit.ToString()) },
            ct);

    /// <summary>
    /// Upcoming firing times for a schedule that hasn't been saved yet - the only practical way to
    /// check that a darkness level or a cron expression says what was meant.
    /// </summary>
    public Task<SchedulePreview> PreviewScheduleAsync(ScheduleInput input, CancellationToken ct = default) =>
        SendAsync<SchedulePreview, ScheduleInput>("/api/schedules/preview", HttpMethod.Post, input, ct);

    public Task<Schedule> CreateScheduleAsync(ScheduleInput input, CancellationToken ct = default) =>
        SendAsync<Schedule, ScheduleInput>("/api/schedules", HttpMethod.Post, input, ct);

    public Task<Schedule> UpdateScheduleAsync(Guid id, ScheduleInput input, CancellationToken ct = default) =>
        SendAsync<Schedule, ScheduleInput>($"/api/schedules/{id}", HttpMethod.Put, input, ct);

    public Task DeleteScheduleAsync(Guid id, CancellationToken ct = default) =>
        SendNoResultAsync<object>($"/api/schedules/{id}", HttpMethod.Delete, null, ct);

    public Task<Schedule> SetScheduleEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) =>
        SendAsync<Schedule, ScheduleEnabledInput>(
            $"/api/schedules/{id}/enabled",
            HttpMethod.Post,
            new ScheduleEnabledInput { Enabled = enabled },
            ct);

    /// <summary>
    /// Fires the action now, ignoring the timing. This really opens the gate or switches the light,
    /// which is why the server puts it behind AdminOnly.
    /// </summary>
    public Task<ScheduleRunResult> RunScheduleAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<ScheduleRunResult, object>($"/api/schedules/{id}/run", HttpMethod.Post, null, ct);

    // MARK: - Server settings (schema-driven, Admin-only server-side)

    public Task<List<SettingsSectionSchema>> SettingsSchemaAsync(CancellationToken ct = default) =>
        GetAsync<List<SettingsSectionSchema>>("/api/settings/schema", ct: ct);

    /// <summary>
    /// The full settings snapshot, untyped on purpose: its shape is described by the schema, and
    /// hard-coding it here is exactly what the schema exists to avoid. Secrets arrive masked and
    /// are sent back unchanged to mean "keep the stored value".
    /// </summary>
    public Task<Dictionary<string, SettingsValue>> SettingsSnapshotAsync(CancellationToken ct = default) =>
        GetAsync<Dictionary<string, SettingsValue>>("/api/settings", ct: ct);

    /// <summary>
    /// The values for a section that isn't in the bulk snapshot, fetched from the dataEndpoint the
    /// schema gives it. Only INTERNET EXPOSED MODE needs this - it configures the dashboard
    /// project's own Kestrel binding and so lives in a different settings file - but nothing here
    /// knows that, which is the point: the section says where its values are and this fetches them.
    /// </summary>
    public Task<Dictionary<string, SettingsValue>> SettingsSectionDataAsync(string endpoint, CancellationToken ct = default) =>
        GetAsync<Dictionary<string, SettingsValue>>(endpoint, ct: ct);

    public Task<SettingsSaveResult> SaveSettingsSectionAsync(
        string endpoint, Dictionary<string, SettingsValue> values, CancellationToken ct = default) =>
        SendAsync<SettingsSaveResult, Dictionary<string, SettingsValue>>(endpoint, HttpMethod.Put, values, ct);

    /// <summary>
    /// Runs a settings field's lookup (SettingsLookupSchema) - the endpoint and the meaning of the
    /// results both come from the schema, so this stays one method however many fields grow one.
    /// </summary>
    public async Task<List<SettingsLookupCandidate>> SettingsLookupAsync(
        SettingsLookupSchema lookup, string query, CancellationToken ct = default)
    {
        var response = await GetAsync<SettingsLookupResponse>(
            lookup.Endpoint,
            new List<KeyValuePair<string, string>> { new("query", query) },
            ct).ConfigureAwait(false);
        return response.Results;
    }

    /// <summary>
    /// Fetches the values a settings field can hold (SettingsOptionsSource). Same shape of method
    /// as SettingsLookupAsync and for the same reason: the endpoint comes from the schema, so one
    /// method serves however many fields grow a list.
    /// </summary>
    public async Task<List<SettingsFieldOption>> SettingsOptionsAsync(
        SettingsOptionsSource source, CancellationToken ct = default) =>
        (await GetAsync<SettingsOptionsResponse>(source.Endpoint, ct: ct).ConfigureAwait(false)).Options;

    /// <summary>
    /// Returns whatever the action had to say for itself, when it says anything. The test sends
    /// report which address the mail actually went to; the setup reset has nothing to report and
    /// answers with a body of a different shape entirely. So the detail is decoded leniently - an
    /// action that succeeded but couldn't be read is still an action that succeeded, and a failure
    /// has already been thrown by then with the server's own message.
    /// </summary>
    public async Task<string?> RunSettingsActionAsync(SettingsActionSchema action, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(action.Method), RequireUri(action.Endpoint));
        request.Content = new StringContent("", Encoding.UTF8, "application/json");
        var data = await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<SettingsActionResult>(data, JsonOptions)?.Detail;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// service is "bridge" or "dashboard". The server answers 202 and restarts only when running
    /// under a supervisor (a Windows service, systemd, etc.); otherwise it refuses with an
    /// explanation, which surfaces through SentinelServerException like any other error message.
    /// </summary>
    public Task RestartServerAsync(string service, CancellationToken ct = default) =>
        SendNoResultAsync("/api/system/restart", HttpMethod.Post, new Dictionary<string, string> { ["service"] = service }, ct);

    /// <summary>
    /// Rebooting the machine itself, as opposed to the two service restarts above. The probe is a
    /// plain GET with no side effects - it is safe to call every time this screen appears, and is
    /// the only trustworthy answer to "will the reboot button work", since the permission it
    /// depends on lives in the operating system and can disappear without this app knowing.
    /// </summary>
    public Task<HostRebootProbe> HostRebootProbeAsync(CancellationToken ct = default) =>
        GetAsync<HostRebootProbe>("/api/system/host-reboot", ct: ct);

    public Task<HostRebootOutcome> RebootHostAsync(CancellationToken ct = default) =>
        SendAsync<HostRebootOutcome, Dictionary<string, string>>(
            "/api/system/host-reboot", HttpMethod.Post, new Dictionary<string, string>(), ct);

    public Task<HostRebootOutcome> CancelHostRebootAsync(CancellationToken ct = default) =>
        SendAsync<HostRebootOutcome, Dictionary<string, string>>(
            "/api/system/host-reboot/cancel", HttpMethod.Post, new Dictionary<string, string>(), ct);

    // MARK: - URL construction

    /// <summary>
    /// An absolute URL against the active server for a server-relative path, or null when the
    /// configured address isn't usable. Used for image and video URLs handed straight to the UI.
    /// </summary>
    public Uri? Url(string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        if (ServerAddress.Normalize(_activeServerUrl()) is not { } baseAddress) return null;
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var baseUri)) return null;

        // A path that already carries its own query (the activity feed's imageUrl does) has to keep
        // it - splitting it off and re-adding it is how those URLs lost their cache-busting token.
        var split = path.Split('?', 2);
        var builder = new UriBuilder(baseUri) { Path = split[0], Query = split.Length > 1 ? split[1] : "" };
        if (query is not null)
        {
            var encoded = string.Join(
                "&",
                query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            if (encoded.Length > 0)
            {
                builder.Query = builder.Query.TrimStart('?') is { Length: > 0 } existing
                    ? $"{existing}&{encoded}"
                    : encoded;
            }
        }
        return builder.Uri;
    }

    /// <summary>
    /// The ws:// or wss:// form of <see cref="Url"/>, for the MSE live socket. Same host, port and
    /// path rules - and the same cookie, since the live player is handed this session's cookies.
    /// </summary>
    public Uri? WebSocketUrl(string path) => ServerAddress.WebSocketUri(_activeServerUrl(), path);

    /// <summary>
    /// Used to decide between the local and remote server addresses before a real connect - any
    /// HTTP response counts as "reachable" (even a 401 or 500), since the point is just to detect
    /// whether the host is reachable at all, not to validate the response itself.
    /// </summary>
    public async Task<bool> ProbeReachableAsync(string address, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (ServerAddress.Normalize(address) is not { } normalized) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(2.5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{normalized}/api/auth/status");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            // A rejected certificate still proves a server answered - it takes a completed-enough
            // TLS handshake to have a certificate to reject. Treating it as unreachable here would
            // wrongly send Connect to the remote address when the local server is fine.
            return IsCertificateTrustFailure(error);
        }
    }

    /// <summary>
    /// Everything the HTTP stack reports when a TLS handshake completed enough to judge the
    /// server's certificate and the judgement was "no". Distinct from the host being unreachable:
    /// these mean the server is up and answering, just not trusted by this machine.
    /// </summary>
    public static bool IsCertificateTrustFailure(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException) return true;
        }
        return false;
    }

    // MARK: - Plumbing

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private Uri RequireUri(string path, IEnumerable<KeyValuePair<string, string>>? query = null) =>
        Url(path, query) ?? throw new SentinelInvalidAddressException();

    private async Task<T> GetAsync<T>(
        string path, IEnumerable<KeyValuePair<string, string>>? query = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireUri(path, query));
        var data = await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
        return Deserialize<T>(data, path);
    }

    private async Task<byte[]> GetBytesAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireUri(path));
        return await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<TResult> SendAsync<TResult, TBody>(
        string path, HttpMethod method, TBody? body, CancellationToken ct)
    {
        var data = await SendForDataAsync(path, method, body, ct).ConfigureAwait(false);
        return Deserialize<TResult>(data, path);
    }

    // Some endpoints answer 204, and some answer a body this app has no use for. Both are the same
    // call as far as the caller is concerned: it either threw or it worked.
    private async Task SendNoResultAsync<TBody>(string path, HttpMethod method, TBody? body, CancellationToken ct) =>
        await SendForDataAsync(path, method, body, ct).ConfigureAwait(false);

    private async Task<byte[]> SendForDataAsync<TBody>(
        string path, HttpMethod method, TBody? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, RequireUri(path));
        var json = body is null ? "" : JsonSerializer.Serialize(body, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Only used for Known Faces photo enrollment - every other endpoint in this client sends plain
    /// JSON. Builds a standard multipart/form-data body since there is no other binary-upload need
    /// in this app to justify a dependency for it.
    /// </summary>
    private async Task<T> SendMultipartAsync<T>(
        string path,
        Dictionary<string, string> fields,
        string fileFieldName,
        byte[] fileData,
        string fileName,
        string mimeType,
        CancellationToken ct)
    {
        using var content = new MultipartFormDataContent($"Boundary-{Guid.NewGuid()}");
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value), name);
        }

        var file = new ByteArrayContent(fileData);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(file, fileFieldName, fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, RequireUri(path)) { Content = content };
        var data = await ExecuteAndValidateAsync(request, ct).ConfigureAwait(false);
        return Deserialize<T>(data, path);
    }

    /// <summary>
    /// Runs the request, logs it, and turns a non-2xx response into a typed exception with the
    /// server's message extracted, without assuming the success body is JSON.
    /// </summary>
    private async Task<byte[]> ExecuteAndValidateAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var method = request.Method.Method;
        var displayUrl = request.RequestUri?.ToString() ?? "(invalid URL)";
        _log.Log($"{method} {displayUrl}");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        // A request the app itself abandoned is not a failure and is deliberately not logged. A
        // request that ran out of time is - it arrives as the same exception type, so the caller's
        // token is what tells them apart, and without this the single most confusing failure on a
        // LAN (a firewall dropping packets, so the server never answers and never refuses) left no
        // trace in the connection log at all.
        catch (Exception error) when (error is not OperationCanceledException ||
                                      !ct.IsCancellationRequested)
        {
            _log.Log($"{method} failed after {Elapsed(started)}: {Describe(error)}", DiagnosticLevel.Error);
            throw;
        }

        using (response)
        {
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            _log.Log($"{method} {(int)response.StatusCode} in {Elapsed(started)}");
            if (response.IsSuccessStatusCode) return data;

            var message = ExtractErrorMessage(data);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SentinelAuthenticationRequiredException();
            throw new SentinelServerException((int)response.StatusCode, message);
        }
    }

    private static string? ExtractErrorMessage(byte[] data)
    {
        if (data.Length == 0) return null;
        try
        {
            var error = JsonSerializer.Deserialize<ApiError>(data, JsonOptions);
            if (error is null) return null;
            if (!string.IsNullOrWhiteSpace(error.Error)) return error.Error;
            if (error.Errors is { Count: > 0 }) return string.Join("\n", error.Errors);
            return string.IsNullOrWhiteSpace(error.Detail) ? null : error.Detail;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private T Deserialize<T>(byte[] data, string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(data, JsonOptions);
            if (value is null) throw new SentinelInvalidResponseException();
            return value;
        }
        catch (JsonException error)
        {
            _log.Log($"Could not decode {path}: {error.Message}", DiagnosticLevel.Error);
            throw new SentinelInvalidResponseException(error);
        }
    }

    private static string Elapsed(Stopwatch stopwatch) =>
        stopwatch.Elapsed.TotalSeconds >= 1
            ? $"{stopwatch.Elapsed.TotalSeconds:0.00}s"
            : $"{stopwatch.ElapsedMilliseconds}ms";

    /// <summary>
    /// The bare message alone produced errors that covered a genuinely dead network, a server that
    /// isn't listening, and a rejected certificate - three completely different problems with three
    /// completely different fixes. The exception chain tells those apart, so it gets logged too.
    /// </summary>
    public static string Describe(Exception error)
    {
        var parts = new List<string> { $"{error.GetType().Name}: {error.Message}" };
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
        {
            parts.Add($"caused by {inner.GetType().Name}: {inner.Message}");
        }
        if (error is HttpRequestException { HttpRequestError: var kind } && kind != HttpRequestError.Unknown)
        {
            parts.Add($"[{kind}]");
        }
        return string.Join(" -> ", parts);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class SentinelInvalidAddressException()
    : Exception("Enter a valid server address, such as http://192.168.1.50:3320.");

public sealed class SentinelInvalidResponseException(Exception? inner = null)
    : Exception("The server returned an invalid response.", inner);

public sealed class SentinelAuthenticationRequiredException()
    : Exception("This server requires you to sign in.");

public sealed class SentinelServerException(int status, string? serverMessage)
    : Exception(serverMessage ?? $"Server request failed (HTTP {status}).")
{
    public int Status { get; } = status;
}
