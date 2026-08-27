using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.Client;

public enum ConnectionState
{
    NotConfigured,
    Checking,
    Online,
    AuthenticationRequired,
    Offline,
}

public static class ConnectionStateExtensions
{
    public static string Label(this ConnectionState state) => state switch
    {
        ConnectionState.NotConfigured => "Set server",
        ConnectionState.Checking => "Connecting…",
        ConnectionState.Online => "Connected",
        ConnectionState.AuthenticationRequired => "Sign in required",
        _ => "Offline",
    };
}

/// <summary>
/// The app's single source of truth: which server, whether it is reachable, who is signed in, and
/// the lists every screen renders. A port of ios/SentinelCore/App/AppModel.swift.
/// </summary>
public sealed class AppModel : ObservableObject, ISentinelLog, IDisposable
{
    private readonly PreferenceStore _preferences;
    private readonly CredentialStore _credentials;
    private readonly SynchronizationContext? _uiContext;
    private readonly ServerDiscovery _discovery;

    private bool _isConnecting;

    // The https:// address this session has already found untrusted, and the plain-HTTP address it
    // settled on instead. Remembered so the failed handshake is not repeated - see ConnectAsync.
    private string? _untrustedHttpsAddress;
    private string? _httpFallbackAddress;

    /// <param name="uiContext">
    /// Where notifications and list mutations are delivered - the window's dispatcher. Passed in
    /// rather than only picked up from <see cref="SynchronizationContext.Current"/> because relying
    /// on ambient state for something this load-bearing is how the whole class of "the UI silently
    /// stopped updating" bugs starts: whether the ambient context is the right one depends on which
    /// thread happened to construct the model. Null means "run everything inline", which is what a
    /// console tool or a test wants.
    /// </param>
    public AppModel(
        PreferenceStore? preferences = null,
        CredentialStore? credentials = null,
        SynchronizationContext? uiContext = null)
    {
        _preferences = preferences ?? new PreferenceStore();
        _credentials = credentials ?? new CredentialStore();
        _uiContext = uiContext ?? SynchronizationContext.Current;

        _serverUrl = _preferences.GetString("serverURL");
        _remoteServerUrl = _preferences.GetString("remoteServerURL");
        _desktopAlertsEnabled = _preferences.GetBool("desktopAlertsEnabled", true);

        // The resolved address once a connect has run, and the configured one until then. Without
        // that fallback every request made before the first successful connect - signing in from
        // the login screen after a connect that failed early, most obviously - would be aimed at an
        // empty address and fail as "enter a valid server address" on a perfectly good one.
        Client = new SentinelClient(
            () => string.IsNullOrEmpty(ActiveServerUrl) ? ServerUrl : ActiveServerUrl,
            this);
        _discovery = new ServerDiscovery(this);
        _discovery.ServersChanged += servers => Post(() => DiscoveredServers.ReplaceAll(servers));
    }

    public SentinelClient Client { get; }

    // MARK: - Configuration

    private string _serverUrl;

    public string ServerUrl
    {
        get => _serverUrl;
        set
        {
            if (!SetProperty(ref _serverUrl, value)) return;
            ForgetHttpFallback();
            _preferences.Set("serverURL", value);
            OnPropertyChanged(nameof(IsConfigured));
        }
    }

    // Optional Cloudflare Tunnel (or any other externally-reachable) address, used only when the
    // local address can't be reached - see ConnectAsync for the resolution order.
    private string _remoteServerUrl;

    public string RemoteServerUrl
    {
        get => _remoteServerUrl;
        set
        {
            if (!SetProperty(ref _remoteServerUrl, value)) return;
            _preferences.Set("remoteServerURL", value);
            OnPropertyChanged(nameof(HasRemoteServerUrl));
        }
    }

    private string _activeServerUrl = "";

    public string ActiveServerUrl
    {
        get => _activeServerUrl;
        private set => SetProperty(ref _activeServerUrl, value);
    }

    private bool _isUsingRemote;

    public bool IsUsingRemote
    {
        get => _isUsingRemote;
        private set => SetProperty(ref _isUsingRemote, value);
    }

    // True while this session is talking plain HTTP to an HTTPS-configured server because this PC
    // doesn't trust the server's self-generated certificate - see ConnectAsync. Surfaced in
    // Settings so the downgrade is never silent.
    private bool _usingHttpFallback;

    public bool UsingHttpFallback
    {
        get => _usingHttpFallback;
        private set => SetProperty(ref _usingHttpFallback, value);
    }

    public bool IsConfigured => ServerAddress.Normalize(ServerUrl) is not null;

    public bool HasRemoteServerUrl => ServerAddress.Normalize(RemoteServerUrl) is not null;

    // MARK: - Connection state

    private ConnectionState _connectionState = ConnectionState.Checking;

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (!SetProperty(ref _connectionState, value)) return;
            OnPropertyChanged(nameof(ConnectionStateLabel));
        }
    }

    public string ConnectionStateLabel => ConnectionState.Label();

    // Whether the server this app is talking to is older than it expects. Advisory: nothing in the
    // app behaves differently, it just says so.
    private ServerCompatibility _serverCompatibility = ServerCompatibility.Unknown;

    public ServerCompatibility ServerCompatibility
    {
        get => _serverCompatibility;
        private set
        {
            if (!SetProperty(ref _serverCompatibility, value)) return;
            OnPropertyChanged(nameof(CompatibilityAdvisory));
        }
    }

    public string? CompatibilityAdvisory => ServerCompatibility.Advisory;

    private AuthStatus? _authStatus;

    public AuthStatus? AuthStatus
    {
        get => _authStatus;
        private set
        {
            if (!SetProperty(ref _authStatus, value)) return;
            OnPropertyChanged(nameof(RequiresLogin));
            OnPropertyChanged(nameof(IsAdmin));
        }
    }

    public bool RequiresLogin => AuthStatus is { Enabled: true, Authenticated: false };

    // Absent role (older server, or login not required) reads as Admin - matches the server's own
    // AuthGateMiddleware treatment of "no login required" as full access for everyone.
    public bool IsAdmin => (AuthStatus?.Role ?? "Admin") == "Admin";

    // MARK: - Loaded data

    // The single Events list: motion and the AI's read of it, already joined server-side into one
    // row per moment.
    public BulkObservableCollection<ActivityItem> Activity { get; } = new();

    // "Show hidden" - motion the AI rejected as nothing, plus anything still awaiting a verdict.
    // Not persisted: the confirmed-only list is the one to be looking at day to day.
    private bool _showHiddenActivity;

    public bool ShowHiddenActivity
    {
        get => _showHiddenActivity;
        set => SetProperty(ref _showHiddenActivity, value);
    }

    public BulkObservableCollection<Models.Camera> Cameras { get; } = new();

    // How this account has arranged the Live grid, newest server answer first. Kept apart from
    // Cameras on purpose: that list stays the canonical, order-agnostic one every other screen
    // reads, and only the Live grid cares where the tiles sit.
    private List<Guid> _liveCameraOrder = new();

    public BulkObservableCollection<HubDevice> Devices { get; } = new();

    public ObservableCollection<DiagnosticEntry> Diagnostics { get; } = new();

    public BulkObservableCollection<DiscoveredServer> DiscoveredServers { get; } = new();

    // Whether the server has the Device Hub switched on. Drives whether the section exists at all.
    private bool _deviceHubEnabled;

    public bool DeviceHubEnabled
    {
        get => _deviceHubEnabled;
        private set => SetProperty(ref _deviceHubEnabled, value);
    }

    // Drives the UNLICENSED watermark over camera images and live video. Starts out - and stays -
    // true whenever the check can't be made: an older server has no such endpoint, and a request
    // that simply failed says nothing about the licence. Accusing a paying customer over a dropped
    // request would be a worse bug than missing the nag.
    private bool _isLicensed = true;

    public bool IsLicensed
    {
        get => _isLicensed;
        private set => SetProperty(ref _isLicensed, value);
    }

    private bool _isLoading;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private string? _lastError;

    public string? LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    private DateTimeOffset? _lastUpdated;

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        private set => SetProperty(ref _lastUpdated, value);
    }

    private PushStatus? _pushStatus;

    public PushStatus? PushStatus
    {
        get => _pushStatus;
        private set => SetProperty(ref _pushStatus, value);
    }

    private SystemStatus? _systemStatus;

    public SystemStatus? SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    // Windows has no APNs, and the server's push providers are Apple and Firebase - so this app
    // can't register for a push token the way the phone does. What it can do, and what a desktop
    // client is actually better placed to do, is raise a native notification the moment a refresh
    // brings back an alert the server decided to notify about. See NewNotifiedEvents.
    private bool _desktopAlertsEnabled;

    public bool DesktopAlertsEnabled
    {
        get => _desktopAlertsEnabled;
        set
        {
            if (!SetProperty(ref _desktopAlertsEnabled, value)) return;
            _preferences.Set("desktopAlertsEnabled", value);
        }
    }

    /// <summary>
    /// Raised when a refresh brings back notified alerts this session has not seen before. The
    /// window turns these into tray notifications; nothing in the client library knows how.
    /// </summary>
    public event Action<IReadOnlyList<ActivityItem>>? NewNotifiedEvents;

    // Seeded on the first successful refresh rather than left empty, so connecting to a server with
    // a day of history behind it doesn't fire a hundred notifications at once.
    private HashSet<string>? _seenNotifiedIds;

    // MARK: - Connecting

    public async Task ConnectAsync(bool resetNetworkSession = false, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            ConnectionState = ConnectionState.NotConfigured;
            return;
        }

        if (_isConnecting)
        {
            Log("Connection request skipped because one is already active", DiagnosticLevel.Notice);
            return;
        }

        _isConnecting = true;
        try
        {
            // The configured address is logged rather than assumed. A field typed into but never
            // saved leaves this pointing somewhere else entirely, and "which address did it
            // actually use" is the single hardest thing to establish from outside the app.
            var wasOffline = ConnectionState == ConnectionState.Offline;
            if (ConnectionState != ConnectionState.Online) ConnectionState = ConnectionState.Checking;

            // A handler that cached a dead connection keeps failing after the network itself has
            // recovered, so an explicit reconnect starts a fresh one.
            if (resetNetworkSession || wasOffline) Client.ResetSession();

            // Deliberately on the explicit-reconnect path only: the Refresh button is how someone
            // who has just installed ca.crt gets HTTPS back without restarting.
            if (resetNetworkSession) ForgetHttpFallback();

            UsingHttpFallback = false;
            await ResolveActiveServerUrlAsync(ct).ConfigureAwait(false);

            // An address already known to be untrusted goes straight to the port that worked.
            // Without this the whole certificate dance ran again on every single connect - and
            // since the app reconnects every 30 seconds, that meant a doomed TLS handshake and an
            // ERROR line in the user's own diagnostics log twice a minute, for ever, while the app
            // was working perfectly normally. A log that cries wolf that often is a log nobody can
            // spot a real fault in. Re-tried from scratch on an explicit reconnect, which is what
            // makes installing the CA certificate take effect without restarting the app.
            if (!IsUsingRemote &&
                _untrustedHttpsAddress is { } known &&
                known == ActiveServerUrl &&
                _httpFallbackAddress is { } remembered)
            {
                ActiveServerUrl = remembered;
                UsingHttpFallback = true;
            }

            // Logged here rather than at the top of the method, and naming the address actually
            // about to be used. Announcing the configured one before resolving anything meant that
            // once the certificate shortcut above started skipping straight to the plain-HTTP port,
            // every refresh still opened with "Connecting to https://..." for a connection that was
            // never attempted - the log stopped describing what the app did. The configured address
            // is still named whenever it differs, because "which address did it actually use" is the
            // hardest thing to establish from outside the app, and a value typed into the settings
            // field but never saved is exactly how that question arises.
            var configured = IsUsingRemote ? RemoteServerUrl : ServerUrl;
            Log(ActiveServerUrl == configured
                ? $"Connecting to {ActiveServerUrl}{(HasRemoteServerUrl && !IsUsingRemote ? $" (remote fallback {RemoteServerUrl})" : "")}"
                : $"Connecting to {ActiveServerUrl} (configured as {configured})");

            try
            {
                await EstablishSessionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (IsLifecycleCancellation(error, ct))
                {
                    Log("Connection task cancelled by the app lifecycle", DiagnosticLevel.Notice);
                    return;
                }

                // The server generates its own certificate authority, which Windows does not trust
                // out of the box - so a fresh install pointed at the https:// address (the default
                // that server discovery advertises) would simply never connect. The server keeps
                // its plain-HTTP listener open precisely for app clients in this position, so fall
                // back to it rather than stranding the user. Local address only: a remote address
                // crosses the open Internet, where quietly dropping encryption is not an
                // acceptable trade.
                if (!IsUsingRemote &&
                    SentinelClient.IsCertificateTrustFailure(error) &&
                    ServerAddress.HttpFallback(ActiveServerUrl) is { } fallback)
                {
                    Log($"This PC does not trust the server's HTTPS certificate; trying the plain-HTTP port at {fallback}",
                        DiagnosticLevel.Notice);
                    if (await Client.ProbeReachableAsync(fallback, ct: ct).ConfigureAwait(false))
                    {
                        // Captured before the overwrite below, which is the whole point of the
                        // local: this has to be the https:// address that was rejected, since that
                        // is what the next connect will resolve to and compare against.
                        var untrusted = ActiveServerUrl;
                        ActiveServerUrl = fallback;
                        UsingHttpFallback = true;
                        try
                        {
                            await EstablishSessionAsync(ct).ConfigureAwait(false);
                            _untrustedHttpsAddress = untrusted;
                            _httpFallbackAddress = fallback;
                            Log("Connected over plain HTTP. For an encrypted connection instead, trust the server's " +
                                "certificate authority (ca.crt) on this PC - see DEPLOYMENT.md section 14a.",
                                DiagnosticLevel.Notice);
                        }
                        catch (OperationCanceledException)
                        {
                            UsingHttpFallback = false;
                            Log("Connection task cancelled by the app lifecycle", DiagnosticLevel.Notice);
                        }
                        catch (Exception retryError)
                        {
                            UsingHttpFallback = false;
                            FailConnection(retryError);
                        }
                        return;
                    }
                    Log($"The plain-HTTP port at {fallback} did not answer either", DiagnosticLevel.Notice);
                }

                FailConnection(error);
            }
        }
        finally
        {
            _isConnecting = false;
        }
    }

    /// <summary>
    /// The post-address-resolution half of <see cref="ConnectAsync"/>: everything from the first
    /// real request to fully online. Split out so the certificate-trust fallback can run it a
    /// second time against the plain-HTTP address.
    /// </summary>
    private async Task EstablishSessionAsync(CancellationToken ct)
    {
        AuthStatus = await Client.AuthStatusAsync(ct).ConfigureAwait(false);

        if (RequiresLogin && _credentials.Load(ServerUrl) is { } credential)
        {
            Log("The saved server session expired; renewing it with the stored login", DiagnosticLevel.Notice);
            try
            {
                await Client.LoginAsync(credential.Username, credential.Password, ct).ConfigureAwait(false);
                AuthStatus = await Client.AuthStatusAsync(ct).ConfigureAwait(false);
            }
            catch (SentinelAuthenticationRequiredException)
            {
                // A password reset or deleted account must not cause a failed automatic login on
                // every launch. Forget the stale secret and show the normal sign-in screen.
                _credentials.Delete(ServerUrl);
                AuthStatus = await Client.AuthStatusAsync(ct).ConfigureAwait(false);
            }
        }

        // Before the RequiresLogin return below, so the advisory is already known at the login
        // screen. Never throws - see CheckServerCompatibilityAsync.
        await CheckServerCompatibilityAsync(ct).ConfigureAwait(false);

        if (RequiresLogin)
        {
            Log("Server requires dashboard authentication");
            ConnectionState = ConnectionState.AuthenticationRequired;
            return;
        }

        await RefreshAllAsync(ct).ConfigureAwait(false);
        ConnectionState = ConnectionState.Online;
        Log("Connection established");
        await RefreshPushStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deliberately swallows every failure. This is a diagnostic nicety layered on top of a
    /// connection that has already succeeded - failing the connect that called it, over a server
    /// that merely declined to describe itself, would turn a cosmetic feature into an outage.
    /// </summary>
    private async Task CheckServerCompatibilityAsync(CancellationToken ct)
    {
        try
        {
            var health = await Client.ServerHealthAsync(ct).ConfigureAwait(false);
            ServerCompatibility = ServerCompatibility.Evaluate(health.Build);
        }
        catch (Exception error)
        {
            ServerCompatibility = ServerCompatibility.Unknown;
            Log($"Could not read the server's version: {SentinelClient.Describe(error)}");
            return;
        }

        if (ServerCompatibility.Advisory is { } advisory) Log(advisory, DiagnosticLevel.Notice);
    }

    /// <summary>
    /// Drops what this session learned about the server's certificate, so the next connect judges
    /// it afresh.
    /// </summary>
    private void ForgetHttpFallback()
    {
        _untrustedHttpsAddress = null;
        _httpFallbackAddress = null;
    }

    /// <summary>
    /// Whether an <see cref="OperationCanceledException"/> means "the app asked us to stop" rather
    /// than "the server never answered".
    /// </summary>
    /// <remarks>
    /// These are the same exception type and they mean opposite things. HttpClient reports its own
    /// request timeout as a TaskCanceledException, so treating every cancellation as a lifecycle
    /// stop left the app on CONNECTING forever - no error, no Offline badge - whenever a server was
    /// reachable but silent. That is not a rare case: a Windows Firewall rule drops rather than
    /// refuses, which is exactly the situation the offline message tells people to go and check. A
    /// closed port answers with a reset and always failed correctly, which is why this hid.
    ///
    /// The caller's token is the only thing that distinguishes them: only a real lifecycle stop
    /// cancels it. The inner TimeoutException is checked too, since HttpClient wraps its timeout
    /// that way and the distinction is worth not depending on a single signal.
    /// </remarks>
    internal static bool IsLifecycleCancellation(Exception error, CancellationToken ct)
    {
        if (error is not OperationCanceledException) return false;
        if (error.InnerException is TimeoutException) return false;
        return ct.IsCancellationRequested;
    }

    private void FailConnection(Exception error)
    {
        ConnectionState = ConnectionState.Offline;
        LastError = UserFacingMessage(error);
        Log($"Connection failed against {ActiveServerUrl}: {SentinelClient.Describe(error)}", DiagnosticLevel.Error);
    }

    /// <summary>
    /// The HTTP stack's own wording names the wrong problem for the two failures this app actually
    /// hits on a LAN. A distrusted self-signed certificate surfaces as a bare authentication
    /// error that names no way out; a server that isn't listening surfaces the same way as a dead
    /// network. Both get replaced with the real cause and a way out.
    /// </summary>
    public static string UserFacingMessage(Exception error)
    {
        if (SentinelClient.IsCertificateTrustFailure(error))
        {
            return "This PC doesn't trust the certificate the server generated for itself. Use the http:// address, " +
                   "or install the server's ca.crt in Local Machine → Trusted Root Certification Authorities to keep HTTPS.";
        }

        if (error is HttpRequestException http)
        {
            return http.HttpRequestError switch
            {
                HttpRequestError.ConnectionError =>
                    "Nothing answered at that address. Check the server is running and that this PC is on the same " +
                    "network - and that Windows Firewall isn't blocking the connection.",
                HttpRequestError.NameResolutionError =>
                    "That host name didn't resolve. Check the spelling, or use the server's IP address instead.",
                _ => http.Message,
            };
        }

        if (error is TaskCanceledException or OperationCanceledException)
        {
            return "The server didn't respond in time. It may be starting up, or a firewall may be dropping the connection.";
        }

        return error.Message;
    }

    /// <summary>
    /// Prefers the local address whenever it's reachable at all (any HTTP response counts, not just
    /// 2xx) and only falls back to the remote address on an actual network failure - this runs at
    /// the top of every connect, so moving between home and away self-heals on the next reconnect
    /// without any Wi-Fi/SSID detection.
    /// </summary>
    private async Task ResolveActiveServerUrlAsync(CancellationToken ct)
    {
        if (!HasRemoteServerUrl)
        {
            ActiveServerUrl = ServerUrl;
            IsUsingRemote = false;
            return;
        }

        if (await Client.ProbeReachableAsync(ServerUrl, ct: ct).ConfigureAwait(false))
        {
            Log($"Local address {ServerUrl} answered; using it");
            ActiveServerUrl = ServerUrl;
            IsUsingRemote = false;
        }
        else
        {
            Log($"Local address {ServerUrl} unreachable; using remote address {RemoteServerUrl}", DiagnosticLevel.Notice);
            ActiveServerUrl = RemoteServerUrl;
            IsUsingRemote = true;
        }
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            // The alert feed is the primary screen. Load each resource independently so an
            // unavailable camera stream or an older server without an endpoint cannot hide events
            // that were returned successfully.
            Exception? firstError = null;

            // Counted here rather than read back off Activity below. Replacing the collection is
            // posted to the UI thread, so on the thread this method is running on Activity.Count is
            // still whatever it was before - and deciding "did we get any events?" from a stale
            // zero would throw away a good list because some *other* resource failed.
            var activityCount = Activity.Count;

            try
            {
                var activity = await Client.ActivityAsync(includeUnconfirmed: ShowHiddenActivity, ct: ct)
                    .ConfigureAwait(false);
                activityCount = activity.Count;
                Post(() => Replace(Activity, activity));
                RaiseNewNotifiedEvents(activity);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                firstError = error;
                activityCount = 0;
            }

            try
            {
                var cameras = await Client.CamerasAsync(ct).ConfigureAwait(false);
                Post(() =>
                {
                    Replace(Cameras, cameras);
                    OnPropertyChanged(nameof(LiveCameras));
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { firstError ??= error; }

            // Not folded into firstError, same reasoning as the Device Hub below: a server older
            // than this feature has no such endpoint, and the Live grid falling back to the camera
            // list's own order is a fine outcome - not a failed connection.
            try
            {
                _liveCameraOrder = await Client.LiveOrderAsync(ct).ConfigureAwait(false);
                Post(() => OnPropertyChanged(nameof(LiveCameras)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* older server: no saved order */ }

            // Not folded into firstError: an older server has no such endpoint, and a 404 there
            // must not read as a failed connection - it just means no Device Hub.
            try
            {
                DeviceHubEnabled = (await Client.DeviceHubEnabledAsync(ct).ConfigureAwait(false)).Enabled;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { DeviceHubEnabled = false; }

            // Prime the Device Hub at startup rather than waiting until its tab is first opened.
            // The server's state endpoint first makes sure its MQTT subscriptions are ready, so a
            // retained device report can tell us whether each light is currently on or off.
            if (DeviceHubEnabled)
            {
                try
                {
                    var devices = await Client.DevicesAsync(ct).ConfigureAwait(false);
                    var snapshot = await Client.DeviceStatesAsync(ct).ConfigureAwait(false);
                    foreach (var state in snapshot.Devices)
                    {
                        if (devices.FirstOrDefault(device => device.Id == state.Id) is not { } device) continue;
                        device.Power = state.Power;
                        device.Brightness = state.Brightness;
                        device.Available = state.Available;
                        device.Measurements = state.Measurements ?? device.Measurements;
                    }
                    Post(() => Replace(Devices, devices));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    Log($"Could not prime Device Hub state during startup: {SentinelClient.Describe(error)}",
                        DiagnosticLevel.Notice);
                }
            }
            else
            {
                Post(Devices.Clear);
            }

            // Same treatment, opposite default - see IsLicensed.
            try
            {
                IsLicensed = (await Client.LicenseStateAsync(ct).ConfigureAwait(false)).Valid;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { IsLicensed = true; }

            if (firstError is not null)
            {
                LastError = UserFacingMessage(firstError);
                // Logged even when it isn't rethrown: a partial refresh still leaves the app
                // looking connected while quietly missing a resource, and without this that left
                // no trace.
                Log($"Refresh finished with an error: {SentinelClient.Describe(firstError)}", DiagnosticLevel.Error);
                if (activityCount == 0) throw firstError;
            }
            else
            {
                LastError = null;
            }

            LastUpdated = DateTimeOffset.Now;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Notified alerts that have appeared since the last refresh. The first refresh of a session
    /// only seeds the set - connecting to a server with a day of history behind it must not fire a
    /// hundred notifications at once.
    /// </summary>
    internal IReadOnlyList<ActivityItem> NewlyNotified(IEnumerable<ActivityItem> activity)
    {
        var notified = activity.Where(item => item.Notified).ToList();
        if (_seenNotifiedIds is null)
        {
            _seenNotifiedIds = notified.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            return Array.Empty<ActivityItem>();
        }

        var fresh = notified.Where(item => !_seenNotifiedIds.Contains(item.Id)).ToList();
        foreach (var item in fresh) _seenNotifiedIds.Add(item.Id);
        return fresh;
    }

    private void RaiseNewNotifiedEvents(IEnumerable<ActivityItem> activity)
    {
        var fresh = NewlyNotified(activity);
        if (fresh.Count == 0 || !DesktopAlertsEnabled) return;
        Post(() => NewNotifiedEvents?.Invoke(fresh));
    }

    // MARK: - Live grid order

    /// <summary>
    /// The cameras as this account has arranged them: saved placements first (any pointing at a
    /// camera that no longer exists dropped), then anything the saved order has never seen - a
    /// camera added since - at the end. Same rule the server applies, repeated here because the
    /// local camera list can be ahead of the last order fetched from it.
    /// </summary>
    public IReadOnlyList<Models.Camera> LiveCameras
    {
        get
        {
            var remaining = Cameras.ToList();
            var ordered = new List<Models.Camera>();
            foreach (var id in _liveCameraOrder)
            {
                var index = remaining.FindIndex(camera => camera.Id == id);
                if (index < 0) continue;
                ordered.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
            ordered.AddRange(remaining);
            return ordered;
        }
    }

    /// <summary>
    /// Drop a dragged tile onto another one: the dragged camera takes that tile's place and the
    /// rest close up behind it.
    /// </summary>
    public void MoveLiveCamera(Guid movingId, Guid targetId)
    {
        var ids = LiveCameras.Select(camera => camera.Id).ToList();
        var from = ids.IndexOf(movingId);
        var to = ids.IndexOf(targetId);
        if (from < 0 || to < 0 || from == to) return;
        ids.RemoveAt(from);
        ids.Insert(to, movingId);
        ApplyLiveCameraOrder(ids);
    }

    private void ApplyLiveCameraOrder(List<Guid> ids)
    {
        // Shown immediately and saved in the background: the arrangement is what the person just
        // asked for, and a round trip to the server is no reason to make them watch a tile snap
        // back before it moves.
        _liveCameraOrder = ids;
        OnPropertyChanged(nameof(LiveCameras));
        _ = Task.Run(async () =>
        {
            try
            {
                await Client.SaveLiveOrderAsync(ids).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Log($"Could not save the live camera order: {error.Message}", DiagnosticLevel.Error);
            }
        });
    }

    // MARK: - Sessions

    public async Task SignInAsync(string username, string password, CancellationToken ct = default)
    {
        await Client.LoginAsync(username, password, ct).ConfigureAwait(false);

        // Saved only after the server accepted the credentials. A failure to write the file should
        // not undo a valid login; it only means this session cannot renew itself automatically.
        try
        {
            _credentials.Save(new StoredCredential(username, password), ServerUrl);
        }
        catch (Exception error)
        {
            Log($"Signed in, but could not save the login on this PC: {error.Message}", DiagnosticLevel.Error);
        }

        AuthStatus = await Client.AuthStatusAsync(ct).ConfigureAwait(false);
        await RefreshAllAsync(ct).ConfigureAwait(false);
        ConnectionState = ConnectionState.Online;
        await RefreshPushStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads whether the server requires a login. Called after the Dashboard Login screen turns
    /// that requirement on or off, so the rest of the app stops describing the state it was in a
    /// moment ago.
    /// </summary>
    public async Task RefreshAuthStatusAsync(CancellationToken ct = default)
    {
        try
        {
            AuthStatus = await Client.AuthStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AuthStatus = null;
        }
        ConnectionState = RequiresLogin ? ConnectionState.AuthenticationRequired : ConnectionState.Online;
    }

    /// <summary>
    /// Set by the License screen after a successful install, so the unlicensed watermark clears
    /// straight away rather than at the next full refresh. IsLicensed is otherwise read-only from
    /// outside precisely so nothing else can decide this on its own.
    /// </summary>
    public void ApplyLicenseState(LicenseState state) => IsLicensed = state.Valid;

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        _credentials.Delete(ServerUrl);
        try
        {
            await Client.LogoutAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Signing out locally has to work even when the server can't be reached to be told.
        }
        await RefreshAuthStatusAsync(ct).ConfigureAwait(false);
    }

    public async Task RefreshPushStatusAsync(CancellationToken ct = default)
    {
        try
        {
            PushStatus = await Client.PushStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            PushStatus = null;
        }
    }

    public async Task RefreshSystemStatusAsync(CancellationToken ct = default)
    {
        try
        {
            SystemStatus = await Client.SystemStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            SystemStatus = null;
        }
    }

    /// <summary>
    /// Begins browsing the LAN for servers. Driven by the setup screen appearing rather than by
    /// connecting: the results only ever populate that screen's picker, and browsing multicasts a
    /// query on every interface every five seconds - which this used to do for the whole life of
    /// the process, having been started from the top of every connect and stopped only on Dispose.
    /// The Apple and Android clients browse only while their picker is on screen, for the same
    /// reason.
    /// </summary>
    public void StartServerDiscovery() => _discovery.Start();

    public void StopServerDiscovery() => _discovery.Stop();

    /// <summary>
    /// A push notification names an alert, and alerts are the analyzed half of the Events list -
    /// derived rather than fetched separately, so there is still exactly one request behind the
    /// list. Motion rows have no alert id and correctly never match a notification.
    /// </summary>
    public IEnumerable<SecurityEvent> Events =>
        Activity.Select(item => item.AsSecurityEvent()).OfType<SecurityEvent>();

    // MARK: - Diagnostics

    public void Log(string message, DiagnosticLevel level = DiagnosticLevel.Info)
    {
        Post(() =>
        {
            Diagnostics.Insert(0, new DiagnosticEntry(DateTimeOffset.Now, level, message));
            while (Diagnostics.Count > 100) Diagnostics.RemoveAt(Diagnostics.Count - 1);
        });
        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
    }

    public void ClearDiagnostics() => Post(Diagnostics.Clear);

    // MARK: - Threading

    /// <summary>
    /// Property notifications go to the UI thread too, not just collection changes.
    /// </summary>
    /// <remarks>
    /// Nearly every property here is written from the tail of an awaited HTTP call, which resumes
    /// on a thread-pool thread - so without this the notification reaches its subscribers there,
    /// and a window handler that touches a control throws a cross-thread exception. That exception
    /// escapes through the property setter into the middle of ConnectAsync, which leaves the app
    /// showing whatever it happened to be showing, badge stuck on "Connecting…", with no error
    /// anywhere to explain it. Marshalling here fixes every subscriber at once rather than asking
    /// each window to remember.
    /// </remarks>
    protected override void RaisePropertyChanged(PropertyChangedEventArgs args) =>
        Post(() => base.RaisePropertyChanged(args));

    /// <summary>
    /// Runs work on the UI thread. Every collection this model exposes is bound to by the window,
    /// and WPF will not tolerate one being mutated from a background thread.
    /// </summary>
    private void Post(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }
        _uiContext.Post(_ => action(), null);
    }

    // One notification for the whole list rather than one per row - see BulkObservableCollection
    // for what the per-row version cost the Live grid.
    private static void Replace<T>(BulkObservableCollection<T> target, IEnumerable<T> items) =>
        target.ReplaceAll(items);

    public void Dispose()
    {
        _discovery.Dispose();
        Client.Dispose();
    }
}
