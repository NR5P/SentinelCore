using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Live;

/// <summary>
/// The Weather section's radar: the dashboard's zoomable map (layers, loop playback, legend, home,
/// radar sites, storm tracks, warning outlines and the SPC outlook), running in WebView2.
/// </summary>
/// <remarks>
/// WPF has no map control, and WebView2 is already how this app plays live video, so the map runs
/// as a page - Live/radar.html, a port of the dashboard's RadarMap. Like the live player (see
/// PlayerHost), the page is answered locally for a made-up path on the *server's own origin*: that
/// makes every tile, legend and storm request same-origin, so they carry this session's cookie and
/// need no CORS the server doesn't send. Leaflet itself comes from the server's /vendor folder.
///
/// When the map can't run - no WebView2 runtime, or the page couldn't load its map library - this
/// shows the NWS's own animated radar loop instead, refreshed every two minutes like the map.
/// </remarks>
public partial class RadarMapHost : UserControl
{
    private const string RadarPath = "/__sentinel-radar.html";
    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private static readonly TimeSpan LoopRefreshInterval = TimeSpan.FromMinutes(2);

    private AppModel? _model;
    private bool _initialized;
    private bool _initializing;
    private string? _loadedUrl;
    private bool _pageReady;
    private bool _active = true;
    private bool _expanded;

    private bool _fallback;
    private string _scope = "local";
    private GifAnimation? _loop;
    private readonly DispatcherTimer _loopRefresh;
    private bool _loopLoading;

    public RadarMapHost()
    {
        InitializeComponent();
        _loopRefresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = LoopRefreshInterval };
        _loopRefresh.Tick += async (_, _) => await LoadLoopAsync();
        UpdateScopeButtons();
    }

    /// <summary>The map's expand button was pressed (or Esc while expanded).</summary>
    public event Action? ExpandRequested;

    /// <summary>
    /// Shows the map for this home. Navigates only when the server or home has changed, so the
    /// section's ten-minute refresh keeps the zoom, the layer and a playing loop exactly as they were
    /// - the page refreshes its own radar every two minutes.
    /// </summary>
    public async Task ShowAsync(AppModel model, WeatherLocation? home)
    {
        _model = model;
        if (_fallback)
        {
            if (_loop is null) await LoadLoopAsync();
            return;
        }

        var origin = OriginFor(model);
        if (origin is null)
        {
            ShowPlaceholder("The server address isn't set yet.");
            return;
        }
        if (!await EnsureInitializedAsync()) return;
        if (Web.CoreWebView2 is not { } core) return;

        SyncCookies(origin);
        var url = origin + RadarPath + QueryFor(home);
        if (url == _loadedUrl) return;
        _loadedUrl = url;
        _pageReady = false;
        try
        {
            core.Navigate(url);
        }
        catch (Exception error)
        {
            model.Log($"The radar map could not load: {error.Message}", DiagnosticLevel.Notice);
        }
    }

    /// <summary>Pauses (false) or resumes the map's timers and loop - off while the section isn't on screen.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (_fallback)
        {
            if (active)
            {
                _loop?.Resume();
                _loopRefresh.Start();
            }
            else
            {
                _loop?.Pause();
                _loopRefresh.Stop();
            }
            return;
        }
        if (_pageReady) Run($"window.sentinelSetActive && window.sentinelSetActive({Js(active)})");
    }

    /// <summary>Asks for fresh radar frames and storms now.</summary>
    public async Task RefreshAsync()
    {
        if (_fallback)
        {
            await LoadLoopAsync();
            return;
        }
        if (_model is not null && OriginFor(_model) is { } origin) SyncCookies(origin);
        if (_pageReady) Run("window.sentinelRefresh && window.sentinelRefresh()");
    }

    /// <summary>Flies the map to a storm and opens its details.</summary>
    public void FocusCell(WeatherStormCell cell)
    {
        if (_fallback || !_pageReady) return;
        var latitude = cell.Latitude.ToString("R", CultureInfo.InvariantCulture);
        var longitude = cell.Longitude.ToString("R", CultureInfo.InvariantCulture);
        Run($"window.sentinelFocusCell && window.sentinelFocusCell({JsonSerializer.Serialize(cell.Id)}, {latitude}, {longitude})");
    }

    /// <summary>Tells the page whether it is filling the section, so its button says which way it goes.</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        if (_pageReady) Run($"window.sentinelSetExpanded && window.sentinelSetExpanded({Js(expanded)})");
    }

    /// <summary>Whether the interactive map is running, as opposed to the fallback loop.</summary>
    public bool IsInteractive => !_fallback;

    private static string Js(bool value) => value ? "true" : "false";

    private static string QueryFor(WeatherLocation? home)
    {
        if (home is null) return "";
        string Degrees(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var label = (home.DisplayName.Length > 0 ? home.DisplayName : "Home");
        var query = $"?lat={Degrees(home.Latitude)}&lon={Degrees(home.Longitude)}&label={Uri.EscapeDataString(label)}";
        if (!string.IsNullOrWhiteSpace(home.TimeZone)) query += $"&tz={Uri.EscapeDataString(home.TimeZone)}";
        return query;
    }

    private void Run(string script)
    {
        if (!_initialized || Web.CoreWebView2 is not { } core) return;
        try
        {
            _ = core.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // The page is gone or going; the next navigation starts it afresh.
        }
    }

    // ---------------------------------------------------------------- WebView2

    private async Task<bool> EnsureInitializedAsync()
    {
        if (_initialized) return true;
        if (_initializing || _model is null) return false;

        _initializing = true;
        try
        {
            // The same user data folder as the live players: one browser profile for the whole app.
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
            _model.Log($"WebView2 is unavailable for the radar map: {error.Message}", DiagnosticLevel.Notice);
            _initializing = false;
            await EnterFallbackAsync(
                "The zoomable radar map needs the Microsoft Edge WebView2 runtime, which isn't installed on this PC. " +
                "This is the National Weather Service's own radar loop instead.",
                showRuntimeLink: true);
            return false;
        }

        var core = Web.CoreWebView2;
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x21, 0x26);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.AddWebResourceRequestedFilter($"*{RadarPath}*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;

        // Nothing on the map opens a window of its own; anything that tries is refused rather than
        // popping a bare browser window up over the app.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.NavigationCompleted += (_, args) =>
        {
            if (_fallback) return;
            if (args.IsSuccess) ShowMap();
        };

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
        if (!requested.AbsolutePath.Equals(RadarPath, StringComparison.OrdinalIgnoreCase)) return;

        var stream = new MemoryStream(Encoding.UTF8.GetBytes(RadarHtml.Value));
        args.Response = core.Environment.CreateWebResourceResponse(
            stream,
            200,
            "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    /// <summary>
    /// This session's cookies, copied into the engine so the page's own requests are signed in.
    /// Cleared first, as PlayerHost does: the store is shared by every WebView2 in the app.
    /// </summary>
    private void SyncCookies(string origin)
    {
        if (_model is null || Web.CoreWebView2 is null) return;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return;
        try
        {
            var manager = Web.CoreWebView2.CookieManager;
            manager.DeleteAllCookies();
            foreach (System.Net.Cookie cookie in _model.Client.Cookies.GetCookies(originUri))
            {
                var copy = manager.CreateCookie(cookie.Name, cookie.Value, originUri.Host, "/");
                copy.IsHttpOnly = cookie.HttpOnly;
                copy.IsSecure = cookie.Secure;
                manager.AddOrUpdateCookie(copy);
            }
        }
        catch (Exception error)
        {
            _model.Log($"Could not hand the session to the radar map: {error.Message}", DiagnosticLevel.Notice);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var type = root.TryGetProperty("type", out var value) ? value.GetString() : null;
            switch (type)
            {
                case "ready":
                    _pageReady = true;
                    ShowMap();
                    if (_expanded) SetExpanded(true);
                    if (!_active) SetActive(false);
                    break;
                case "failed":
                    var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
                    _model?.Log($"The radar map could not start: {message}", DiagnosticLevel.Notice);
                    _ = EnterFallbackAsync(
                        "The zoomable radar map couldn't start (this server may be too old to have one). " +
                        "This is the National Weather Service's own radar loop instead.",
                        showRuntimeLink: false);
                    break;
                case "expand":
                    ExpandRequested?.Invoke();
                    break;
            }
        }
        catch (JsonException)
        {
            // Not a message this host understands.
        }
    }

    private void ShowMap()
    {
        Web.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Collapsed;
    }

    private void ShowPlaceholder(string text)
    {
        Web.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        PlaceholderText.Text = text;
    }

    private static readonly Lazy<string> RadarHtml = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith(".radar.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<html><body style=\"background:#1b2126\"></body></html>";
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    // ---------------------------------------------------------------- The fallback loop

    private async Task EnterFallbackAsync(string note, bool showRuntimeLink)
    {
        _fallback = true;
        _pageReady = false;
        Web.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Visible;
        FallbackNote.Text = note;
        RuntimeButton.Visibility = showRuntimeLink ? Visibility.Visible : Visibility.Collapsed;
        if (Web.CoreWebView2 is { } core)
        {
            try
            {
                core.Navigate("about:blank");
            }
            catch (Exception)
            {
                // Nothing left to stop.
            }
        }
        if (_active) _loopRefresh.Start();
        await LoadLoopAsync();
    }

    private async Task LoadLoopAsync()
    {
        if (_model is null || _loopLoading) return;
        _loopLoading = true;
        var scope = _scope;
        try
        {
            if (_loop is null) LoopMessage.Text = "loading the radar loop…";
            var bytes = await _model.Client.WeatherRadarImageAsync(scope, "loop");
            if (scope != _scope) return;
            var loop = GifAnimation.Decode(bytes);
            _loop?.Dispose();
            _loop = loop;
            LoopMessage.Text = "";
            loop.Play(LoopImage);
            if (!_active) loop.Pause();
            FallbackTitle.Text = scope == "national" ? "RADAR · NATIONAL LOOP" : "RADAR · LOCAL LOOP";
        }
        catch (Exception error)
        {
            if (_loop is null) LoopMessage.Text = $"The radar loop couldn't be loaded: {AppModel.UserFacingMessage(error)}";
            _model.Log($"Radar loop failed: {error.Message}", DiagnosticLevel.Notice);
        }
        finally
        {
            _loopLoading = false;
        }
    }

    private async void OnScopeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string scope } || scope == _scope) return;
        _scope = scope;
        UpdateScopeButtons();
        _loop?.Dispose();
        _loop = null;
        LoopImage.Source = null;
        await LoadLoopAsync();
    }

    private void UpdateScopeButtons()
    {
        LocalButton.Style = _scope == "local" ? (Style)FindResource("PrimaryButton") : null;
        NationalButton.Style = _scope == "national" ? (Style)FindResource("PrimaryButton") : null;
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
}
