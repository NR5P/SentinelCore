using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using SentinelCore.Client;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Audio;

/// <summary>
/// The Music &amp; Podcasts sound engine: an &lt;audio&gt; element in a WebView2 that is never
/// shown.
/// </summary>
/// <remarks>
/// <para>
/// Why WebView2 rather than WPF's MediaPlayer or NAudio: the server streams mp3, aac (m4a), flac,
/// opus, vorbis and wav to "Web and Windows" (MUSIC_AND_PODCASTS.md §4), and Chromium decodes all
/// of those on every Windows 10/11 PC with no codec pack, seeks over HTTP ranges, and changes
/// speed with the pitch preserved (podcasts at 1.5×). MediaPlayer can't play opus or vorbis, can't
/// keep pitch at speed, and - like NAudio's MediaFoundationReader - opens its own HTTP connection
/// with none of this session's cookies, so against a server with login on every stream would be
/// a 401. The app already ships WebView2 for live video; this reuses the same trick of serving the
/// page off the server's own origin so the stream requests are same-origin and carry the cookie.
/// </para>
/// <para>
/// The control is a bare <see cref="CoreWebView2Controller"/> parented to the main window at zero
/// size rather than a WPF element, so it plays whichever section is on screen and while the window
/// is hidden in the tray (background playback). It gets its own user-data folder, because it is
/// started with different browser arguments from the live player's: autoplay without a click (a
/// speaker command or an alarm starts music with nobody at the PC), and Chromium's own media-key
/// handling off so Windows shows one media session - ours, from <see cref="MediaControls"/> - not
/// a second one for the page.
/// </para>
/// </remarks>
public sealed class AudioEngine : IDisposable
{
    private const string PagePath = "/__sentinel-audio.html";

    private readonly AppModel _model;
    private CoreWebView2Controller? _controller;
    private Task<bool>? _starting;
    private string _origin = "";
    private TaskCompletionSource? _pageReady;
    private bool _disposed;

    // The page reports position a few times a second; between reports the position is extrapolated
    // from the last one, so lyrics highlight and the scrubber move smoothly.
    private double _reportedPosition;
    private DateTime _reportedAt = DateTime.UtcNow;

    public AudioEngine(AppModel model) => _model = model;

    public double Duration { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsBuffering { get; private set; }
    public double Rate { get; private set; } = 1;

    /// <summary>Why nothing can play (WebView2 missing, no server), or null when it can.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>Where playback is now, extrapolated between the page's reports.</summary>
    public double Position =>
        IsPlaying && !IsBuffering
            ? _reportedPosition + (DateTime.UtcNow - _reportedAt).TotalSeconds * Rate
            : _reportedPosition;

    /// <summary>The current item played to its end.</summary>
    public event Action? Ended;

    /// <summary>The item could not be played, with a sentence saying why.</summary>
    public event Action<string>? Failed;

    /// <summary>Playing, paused, buffering or the duration changed.</summary>
    public event Action? StateChanged;

    /// <summary>The page reported a new position (a few times a second while playing).</summary>
    public event Action? TimeUpdated;

    private Task<bool> EnsureAsync()
    {
        var origin = OriginFor(_model);
        if (origin is null)
        {
            Unavailable = "The server address isn't set yet.";
            return Task.FromResult(false);
        }
        if (_controller is not null && origin == _origin) return Task.FromResult(true);
        if (_controller is not null)
        {
            // The app moved to the other address (remote, or plain HTTP): reload the page there.
            _origin = origin;
            return NavigateAsync();
        }
        _origin = origin;
        return _starting ??= StartAsync();
    }

    private async Task<bool> StartAsync()
    {
        try
        {
            var window = Application.Current.MainWindow ?? throw new InvalidOperationException("No main window yet.");
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCore", "WebView2-Audio");
            Directory.CreateDirectory(userData);
            var options = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling,MediaSessionService");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
            var controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 0, 0);
            // Visible (at zero size) on purpose: a hidden page is throttled by Chromium, which would
            // slow the position reports the lyrics and transcript follow.
            controller.IsVisible = true;

            var core = controller.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.AddWebResourceRequestedFilter($"*{PagePath}*", CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            _controller = controller;
            Unavailable = null;
            return await NavigateAsync();
        }
        catch (Exception error)
        {
            _model.Log($"The audio player could not start: {error.Message}", DiagnosticLevel.Error);
            Unavailable = "Music and podcasts play through the Microsoft Edge WebView2 runtime, which isn't installed on this PC.";
            _starting = null;
            return false;
        }
    }

    private async Task<bool> NavigateAsync()
    {
        if (_controller is null) return false;
        SyncCookies();
        _pageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controller.CoreWebView2.Navigate($"{_origin}{PagePath}");
        var finished = await Task.WhenAny(_pageReady.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (finished != _pageReady.Task)
        {
            Unavailable = "The audio player page didn't load.";
            return false;
        }
        return true;
    }

    /// <summary>Copies this session's cookies into the engine - same as the live player does.</summary>
    private void SyncCookies()
    {
        if (_controller is null || !Uri.TryCreate(_origin, UriKind.Absolute, out var originUri)) return;
        var manager = _controller.CoreWebView2.CookieManager;
        manager.DeleteAllCookies();
        foreach (System.Net.Cookie cookie in _model.Client.Cookies.GetCookies(originUri))
        {
            var copy = manager.CreateCookie(cookie.Name, cookie.Value, originUri.Host, "/");
            copy.IsHttpOnly = cookie.HttpOnly;
            copy.IsSecure = cookie.Secure;
            manager.AddOrUpdateCookie(copy);
        }
    }

    private static string? OriginFor(AppModel model)
    {
        var address = string.IsNullOrEmpty(model.ActiveServerUrl) ? model.ServerUrl : model.ActiveServerUrl;
        if (ServerAddress.Normalize(address) is not { } normalized) return null;
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_controller is null) return;
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requested)) return;
        if (!requested.AbsolutePath.Equals(PagePath, StringComparison.OrdinalIgnoreCase)) return;
        args.Response = _controller.CoreWebView2.Environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(Page.Value)), 200, "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "ready":
                    _pageReady?.TrySetResult();
                    break;
                case "time":
                {
                    _reportedPosition = Number(root, "position");
                    _reportedAt = DateTime.UtcNow;
                    var duration = Number(root, "duration");
                    var playing = root.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
                    var buffering = root.TryGetProperty("buffering", out var b) && b.ValueKind == JsonValueKind.True;
                    var changed = playing != IsPlaying || buffering != IsBuffering || Math.Abs(duration - Duration) > 0.5;
                    if (duration > 0) Duration = duration;
                    IsPlaying = playing;
                    IsBuffering = buffering;
                    if (changed) StateChanged?.Invoke();
                    TimeUpdated?.Invoke();
                    break;
                }
                case "state":
                {
                    var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;
                    IsBuffering = state == "buffering";
                    if (state == "playing") IsPlaying = true;
                    if (state == "paused") IsPlaying = false;
                    _reportedAt = DateTime.UtcNow;
                    StateChanged?.Invoke();
                    break;
                }
                case "loaded":
                    Duration = Number(root, "duration");
                    StateChanged?.Invoke();
                    break;
                case "ended":
                    IsPlaying = false;
                    StateChanged?.Invoke();
                    Ended?.Invoke();
                    break;
                case "error":
                    IsPlaying = false;
                    StateChanged?.Invoke();
                    Failed?.Invoke(root.TryGetProperty("message", out var m) ? m.GetString() ?? "The audio could not be played." : "The audio could not be played.");
                    break;
            }
        }
        catch (JsonException)
        {
            // Not a message this host understands.
        }
    }

    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && !double.IsNaN(number) ? number : 0;

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void Run(string script)
    {
        if (_controller is null || _disposed) return;
        try
        {
            _ = _controller.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception error)
        {
            _model.Log($"Audio player call failed: {error.Message}", DiagnosticLevel.Notice);
        }
    }

    /// <summary>
    /// Loads one item from a server-relative path and, when <paramref name="play"/>, starts it.
    /// Returns false when the engine can't run at all (see <see cref="Unavailable"/>).
    /// </summary>
    public async Task<bool> LoadAsync(string path, double startSeconds, bool play, double rate, double volume)
    {
        if (!await EnsureAsync()) return false;
        SyncCookies(); // a sign-in since the last item would otherwise 401 the stream
        Rate = rate;
        Duration = 0;
        _reportedPosition = startSeconds;
        _reportedAt = DateTime.UtcNow;
        IsPlaying = false;
        var url = JsonSerializer.Serialize(path);
        Run($"window.sc && window.sc.load({url}, {N(startSeconds)}, {(play ? "true" : "false")}, {N(rate)}, {N(volume)})");
        return true;
    }

    public void Play() => Run("window.sc && window.sc.play()");

    public void Pause() => Run("window.sc && window.sc.pause()");

    public void Seek(double seconds)
    {
        _reportedPosition = Math.Max(0, seconds);
        _reportedAt = DateTime.UtcNow;
        Run($"window.sc && window.sc.seek({N(Math.Max(0, seconds))})");
        TimeUpdated?.Invoke();
    }

    public void SetRate(double rate)
    {
        _reportedPosition = Position;
        _reportedAt = DateTime.UtcNow;
        Rate = rate;
        Run($"window.sc && window.sc.rate({N(rate)})");
    }

    private double _lastVolume = -1;

    /// <summary>The element volume (0-1). Repeats of the same value are not sent.</summary>
    public void SetVolume(double volume)
    {
        volume = Math.Round(Math.Clamp(volume, 0, 1), 3);
        if (Math.Abs(volume - _lastVolume) < 0.001) return;
        _lastVolume = volume;
        Run($"window.sc && window.sc.volume({N(volume)})");
    }

    public void Stop()
    {
        IsPlaying = false;
        _reportedPosition = 0;
        Duration = 0;
        Run("window.sc && window.sc.stop()");
        StateChanged?.Invoke();
    }

    private static readonly Lazy<string> Page = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith(".audio.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<html><body></body></html>";
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _controller?.Close();
        }
        catch (Exception)
        {
            // Shutting down.
        }
        _controller = null;
    }
}
