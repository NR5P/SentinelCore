using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using SentinelCore.Client;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Live;

/// <summary>
/// One live tile or one clip, played inside WebView2.
/// </summary>
/// <remarks>
/// WPF can play neither of the transports this server offers for live video - WebRTC needs a peer
/// connection and MSE needs MediaSource, and both live in a browser engine. Rather than vendor a
/// media stack, this hosts the Edge WebView2 control and runs the same ladder the web dashboard
/// runs. See Live/player.html for the ladder itself.
///
/// The page is served from the *server's own origin* rather than from a file or a virtual host, by
/// intercepting one made-up path on that origin and answering it locally. That is what makes the
/// session cookie, the MSE WebSocket and the WebRTC POST all same-origin - from a file:// or a
/// custom virtual host they would be cross-origin, the server sends no CORS headers, and every one
/// of them would be refused.
/// </remarks>
public partial class PlayerHost : UserControl
{
    // Any path that cannot collide with a real endpoint. It is answered locally and never reaches
    // the server.
    private const string PlayerPath = "/__sentinel-player.html";

    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private AppModel? _model;
    private bool _initialized;
    private bool _initializing;
    private string _origin = "";
    private Guid _cameraId;
    private bool _stopped = true;

    public PlayerHost() => InitializeComponent();

    /// <summary>Muted tiles never ask the server for an audio track at all - see player.html.</summary>
    public bool WantsAudio { get; set; }

    /// <summary>"sub" for a grid tile, "main" for the full-size view.</summary>
    public string Quality { get; set; } = "sub";

    /// <summary>
    /// Which set of controls the page draws over the video: a tile gets record and open, the
    /// full-size view gets audio, digital zoom and - when the camera has one - the PTZ pad.
    /// </summary>
    public string Mode { get; set; } = "tile";

    /// <summary>Whether to draw the PTZ pad. Only meaningful in the full-size view.</summary>
    public bool PtzEnabled { get; set; }

    /// <summary>The record button on the video was pressed.</summary>
    public event Action? RecordRequested;

    /// <summary>A tile was double-clicked, or its open button pressed.</summary>
    public event Action? OpenRequested;

    /// <summary>The audio was muted or unmuted from the video's own control.</summary>
    public event Action<bool>? MuteChanged;

    /// <summary>A PTZ button was pressed (true) or released (false), by direction name.</summary>
    public event Action<string, bool>? PtzGesture;

    /// <summary>A still shown behind the player until the first frame lands.</summary>
    public string? PosterPath
    {
        get => _posterPath;
        set
        {
            _posterPath = value;
            _ = LoadPosterAsync(value);
        }
    }

    private string? _posterPath;

    private async Task LoadPosterAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var bitmap = await Design.ImageLoader.LoadAsync(path, 640);
        if (_posterPath == path) Poster.Source = bitmap;
    }

    /// <summary>Starts the live ladder for one camera. Safe to call again for the same camera.</summary>
    public async Task PlayAsync(AppModel model, Guid cameraId)
    {
        _model = model;
        _cameraId = cameraId;
        _stopped = false;

        if (!await EnsureInitializedAsync()) return;
        if (_stopped) return;

        await SyncCookiesAsync();
        if (_stopped) return;

        var query =
            $"?base={Uri.EscapeDataString(_origin)}" +
            $"&camera={cameraId}" +
            $"&quality={Quality}" +
            $"&audio={(WantsAudio ? "1" : "0")}" +
            $"&mode={Mode}" +
            $"&ptz={(PtzEnabled ? "1" : "0")}";
        Web.CoreWebView2.Navigate($"{_origin}{PlayerPath}{query}");
    }

    /// <summary>How far into the current clip playback has reached.</summary>
    public event Action<double>? ClipPositionChanged;

    /// <summary>The clip reached its end - for the timeline, the cue to jump the gap.</summary>
    public event Action? ClipEnded;

    /// <summary>The clip could not be played at all.</summary>
    public event Action? ClipFailed;

    /// <summary>The current digital-zoom scale, 1 when the picture is untouched.</summary>
    public event Action<double>? ZoomChanged;

    /// <summary>
    /// Plays a recorded clip rather than a live stream, starting <paramref name="offsetSeconds"/>
    /// into it. The caller drives position from there - see the timeline, whose cursor spans a whole
    /// day while a clip is only one window inside it.
    /// </summary>
    public async Task PlayClipAsync(AppModel model, string clipPath, double offsetSeconds = 0, bool autoplay = false)
    {
        _model = model;
        _cameraId = Guid.Empty;
        _stopped = false;

        if (!await EnsureInitializedAsync()) return;
        if (_stopped) return;

        await SyncCookiesAsync();
        if (_stopped) return;

        // Navigating for every clip would reload the page and drop the element the video is in;
        // once the page is up, later clips are handed to the script it already has.
        if (!_clipPageReady)
        {
            // No camera in the query, so the page starts no live ladder - it waits to be told.
            Web.CoreWebView2.Navigate($"{_origin}{PlayerPath}?base={Uri.EscapeDataString(_origin)}");
            await WhenNavigatedAsync();
            if (_stopped) return;
            _clipPageReady = true;
        }

        ShowPlayer();
        var path = JsonSerializer.Serialize(clipPath);
        var offset = offsetSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        await Web.CoreWebView2.ExecuteScriptAsync(
            $"window.sentinelPlayClip({path}, {offset}, {(autoplay ? "true" : "false")})");
    }

    /// <summary>Moves the current clip to a position, without reloading it.</summary>
    public void SeekClip(double seconds)
    {
        if (!_initialized || Web.CoreWebView2 is null) return;
        var value = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _ = Web.CoreWebView2.ExecuteScriptAsync($"window.sentinelSeekClip && window.sentinelSeekClip({value})");
    }

    public void SetPlaying(bool playing)
    {
        if (!_initialized || Web.CoreWebView2 is null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"window.sentinelSetPlaying && window.sentinelSetPlaying({(playing ? "true" : "false")})");
    }

    /// <summary>A short-lived line of text over the video - what a command did, or why it didn't.</summary>
    public void Toast(string text)
    {
        if (!_initialized || Web.CoreWebView2 is null) return;
        var escaped = JsonSerializer.Serialize(text);
        _ = Web.CoreWebView2.ExecuteScriptAsync($"window.sentinelToast && window.sentinelToast({escaped})");
    }

    /// <summary>Marks a control as working, so a slow command doesn't look like a dead button.</summary>
    public void SetBusy(string action, bool busy)
    {
        if (!_initialized || Web.CoreWebView2 is null) return;
        var escaped = JsonSerializer.Serialize(action);
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"window.sentinelSetBusy && window.sentinelSetBusy({escaped}, {(busy ? "true" : "false")})");
    }

    /// <summary>Flashes the Home button to show a hold actually saved the position.</summary>
    public void SetHomeSaved(bool saved)
    {
        if (!_initialized || Web.CoreWebView2 is null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"window.sentinelSetHomeSaved && window.sentinelSetHomeSaved({(saved ? "true" : "false")})");
    }

    /// <summary>Shows a line of text over the black surface, for "no clip here" and the like.</summary>
    public void ShowMessage(string text)
    {
        if (!_initialized || Web.CoreWebView2 is null)
        {
            ShowPlaceholder("", text);
            return;
        }
        var escaped = JsonSerializer.Serialize(text);
        _ = Web.CoreWebView2.ExecuteScriptAsync($"window.sentinelShowMessage && window.sentinelShowMessage({escaped})");
    }

    // Digital zoom, scaling the frames already on screen. Nothing to do with a PTZ camera's own
    // lens: that is a command sent to the camera and it moves the picture for everyone watching,
    // while this is local and is nobody else's business.
    public void ZoomIn() => RunZoom("window.sentinelZoomIn");

    public void ZoomOut() => RunZoom("window.sentinelZoomOut");

    public void ZoomReset() => RunZoom("window.sentinelZoomReset");

    private void RunZoom(string function)
    {
        if (!_initialized || Web.CoreWebView2 is null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync($"{function} && {function}()");
    }

    private bool _clipPageReady;

    public void Stop()
    {
        _stopped = true;
        // The page is navigated away below, so the script the clip API lives in is gone with it.
        _clipPageReady = false;
        if (!_initialized || Web.CoreWebView2 is null) return;
        try
        {
            _ = Web.CoreWebView2.ExecuteScriptAsync("window.sentinelStop && window.sentinelStop()");
            // Navigated away as well as told to stop: a page left loaded keeps its sockets and its
            // decoder alive, which on a grid of tiles is exactly the CPU this is trying to release.
            Web.CoreWebView2.Navigate("about:blank");
        }
        catch (Exception)
        {
            // Tearing down; a failure here has nothing left to affect.
        }
        ShowPlaceholder("CONNECTING", null);
    }

    public void SetMuted(bool muted)
    {
        if (!_initialized || Web.CoreWebView2 is null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync($"window.sentinelSetMuted && window.sentinelSetMuted({(muted ? "true" : "false")})");
    }

    private async Task<bool> EnsureInitializedAsync()
    {
        if (_initializing) return false;
        if (_model is null) return false;

        var origin = OriginFor(_model);
        if (origin is null)
        {
            ShowPlaceholder("NO SERVER", "The server address isn't set yet.");
            return false;
        }

        // Re-read on every play, not captured once at first use. The address this app talks to can
        // change while the app is running - moving to the remote address on leaving the house, or
        // dropping to the plain-HTTP port when this PC doesn't trust the server's certificate - and
        // a player that kept the origin it first saw would go on requesting video from an address
        // nothing is answering on, while the rest of the app had already moved.
        _origin = origin;
        if (_initialized) return true;

        _initializing = true;
        try
        {
            // One shared user data folder for every player in the app: WebView2 refuses a second
            // environment on the same folder from the same process otherwise, and a folder per tile
            // would be a browser profile per camera.
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SentinelCore",
                "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception error)
        {
            // The runtime ships with Windows 11 and with current Windows 10, but it can be absent
            // on an older or stripped install - and this is the one failure a user can fix
            // themselves, so it says so rather than showing a black rectangle.
            _model.Log($"WebView2 is unavailable: {error.Message}", DiagnosticLevel.Error);
            ShowPlaceholder(
                "LIVE VIEW UNAVAILABLE",
                "Live video needs the Microsoft Edge WebView2 runtime, which isn't installed on this PC. " +
                "Everything else in the app works without it.",
                showRuntimeLink: true);
            _initializing = false;
            return false;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // The made-up path is answered from the embedded resource; everything else goes to the real
        // server. Filtered on the path across every origin rather than on one baked-in address,
        // since the origin can change under a player that is already initialised.
        core.AddWebResourceRequestedFilter($"*{PlayerPath}*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, _) => { if (!_stopped) ShowPlayer(); };

        _initialized = true;
        _initializing = false;
        return true;
    }

    private static string? OriginFor(AppModel model)
    {
        var address = string.IsNullOrEmpty(model.ActiveServerUrl) ? model.ServerUrl : model.ActiveServerUrl;
        if (ServerAddress.Normalize(address) is not { } normalized) return null;
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (Web.CoreWebView2 is not { } core) return;
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requested)) return;
        if (!requested.AbsolutePath.Equals(PlayerPath, StringComparison.OrdinalIgnoreCase)) return;

        var html = PlayerHtml.Value;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        args.Response = core.Environment.CreateWebResourceResponse(
            stream,
            200,
            "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    /// <summary>
    /// Copies this session's cookies into the engine, so the player's own requests - the MSE
    /// socket, the WebRTC POST, the snapshot - are authenticated exactly as the rest of the app is.
    /// </summary>
    private async Task SyncCookiesAsync()
    {
        if (_model is null || Web.CoreWebView2 is null) return;
        if (!Uri.TryCreate(_origin, UriKind.Absolute, out var originUri)) return;

        var manager = Web.CoreWebView2.CookieManager;

        // Cleared first, not merged into. The engine keeps one cookie store shared by every player
        // in the app and persisted on disk, while this method only ever added to it - so signing
        // out, or pointing the app at a different server, left the previous session's cookie in
        // place for the next tile to send.
        manager.DeleteAllCookies();

        foreach (System.Net.Cookie cookie in _model.Client.Cookies.GetCookies(originUri))
        {
            var copy = manager.CreateCookie(cookie.Name, cookie.Value, originUri.Host, "/");
            copy.IsHttpOnly = cookie.HttpOnly;
            copy.IsSecure = cookie.Secure;
            manager.AddOrUpdateCookie(copy);
        }
        await Task.CompletedTask;
    }

    private Task WhenNavigatedAsync()
    {
        if (Web.CoreWebView2 is not { } core) return Task.CompletedTask;
        var completion = new TaskCompletionSource();
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            core.NavigationCompleted -= Handler;
            completion.TrySetResult();
        }
        core.NavigationCompleted += Handler;
        return completion.Task;
    }

    /// <summary>
    /// The page reports which tier it landed on and why. Forwarded to the server, which logs it
    /// rather than storing it - the question it answers ("was this PC on WebRTC or MSE at 9pm, and
    /// what pushed it down") is asked of the log alongside the go2rtc lines that explain it.
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var kindName = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;

            switch (kindName)
            {
                case "clipTime":
                    if (root.TryGetProperty("seconds", out var seconds) && seconds.TryGetDouble(out var position))
                    {
                        ClipPositionChanged?.Invoke(position);
                    }
                    return;
                case "clipEnded":
                    ClipEnded?.Invoke();
                    return;
                case "clipError":
                    ClipFailed?.Invoke();
                    return;
                case "zoom":
                    if (root.TryGetProperty("scale", out var scaleValue) && scaleValue.TryGetDouble(out var scale))
                    {
                        ZoomChanged?.Invoke(scale);
                    }
                    return;
                case "action":
                    switch (root.TryGetProperty("name", out var name) ? name.GetString() : null)
                    {
                        case "record":
                            RecordRequested?.Invoke();
                            break;
                        case "open":
                            OpenRequested?.Invoke();
                            break;
                        case "mute":
                            MuteChanged?.Invoke(
                                root.TryGetProperty("muted", out var muted) && muted.ValueKind == JsonValueKind.True);
                            break;
                    }
                    return;
                case "ptz":
                    if (root.TryGetProperty("direction", out var direction) &&
                        direction.GetString() is { Length: > 0 } which)
                    {
                        var pressed = root.TryGetProperty("action", out var gesture) &&
                                      gesture.GetString() == "press";
                        PtzGesture?.Invoke(which, pressed);
                    }
                    return;
                case "livePath":
                    break;
                default:
                    return;
            }

            // Live-path telemetry belongs to a camera; a clip has none.
            if (_model is null || _cameraId == Guid.Empty) return;

            var tier = root.TryGetProperty("tier", out var t) ? t.GetString() ?? "" : "";
            var outcome = root.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "";
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            var elapsed = root.TryGetProperty("elapsedMs", out var e) && e.TryGetInt32(out var ms) ? ms : 0;
            if (tier.Length == 0 || outcome.Length == 0) return;

            _model.Log($"Live {tier} {outcome}{(reason is null ? "" : $": {reason}")} after {elapsed}ms");

            var model = _model;
            var cameraId = _cameraId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await model.Client.ReportLivePathAsync(cameraId, tier, outcome, reason, elapsed);
                }
                catch (Exception)
                {
                    // Telemetry that couldn't be delivered must never disturb playback.
                }
            });
        }
        catch (JsonException)
        {
            // Not a message this host understands.
        }
    }

    private void ShowPlayer()
    {
        Web.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Collapsed;
    }

    private void ShowPlaceholder(string title, string? message, bool showRuntimeLink = false)
    {
        Web.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        PlaceholderTitle.Text = title;
        PlaceholderMessage.Text = message ?? "";
        PlaceholderMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        RuntimeButton.Visibility = showRuntimeLink ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRuntimeLinkClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _model?.Log($"Could not open the WebView2 download page: {error.Message}", DiagnosticLevel.Notice);
        }
    }

    private static readonly Lazy<string> PlayerHtml = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith("player.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<html><body style=\"background:#000\"></body></html>";
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
