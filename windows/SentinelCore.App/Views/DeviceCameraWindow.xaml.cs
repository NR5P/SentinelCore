using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

/// <summary>
/// "Use this PC as a camera": this PC's webcam becomes one of the dashboard's cameras.
/// </summary>
/// <remarks>
/// The streaming itself is the dashboard's own publish page (wwwroot/publish.html and
/// publisher.js), hosted in WebView2 the same way the live tiles are - WPF has no WebRTC of its
/// own, and that page already does everything this needs: capture, the SDP exchange, reconnecting
/// after a blip, and the server's confirmation that frames are actually arriving. What this
/// window adds is what a browser tab cannot: the PC is held out of sleep for as long as the page
/// says it is streaming, the choice of camera can be made (or created) without leaving the app,
/// the session cookie is handed over so nothing has to be signed in twice, and streaming resumes
/// on the next launch if the app was closed while a camera.
///
/// Two things the browser's own rules would otherwise stop. A page only gets a webcam in a
/// secure context, and the dashboard's usual address is plain http on the LAN - so the engine is
/// told to treat that one origin as secure. And the dashboard's https certificate is signed by the
/// install's own authority, which this PC has no reason to trust - so a certificate error for the
/// server's host is accepted, but only for a certificate that authority signed, as remembered by
/// ServerIdentity. Both are scoped to the server this app is already talking to; nothing else this
/// engine loads is affected.
/// </remarks>
public partial class DeviceCameraWindow : Window
{
    private static DeviceCameraWindow? _open;

    private readonly AppModel _model;
    private string _origin = "";
    private bool _publishing;
    private Guid _cameraId;

    /// <summary>One window at a time: a second would be a second copy of the same webcam.</summary>
    public static void Open(AppModel model, Guid? cameraId = null)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        _open = new DeviceCameraWindow(model, cameraId);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    /// <summary>Whether this PC was streaming when the app last closed - what the app resumes on launch.</summary>
    public static Guid? StoredCameraId
    {
        get
        {
            try
            {
                if (!File.Exists(IntentPath)) return null;
                var stored = JsonSerializer.Deserialize<Intent>(File.ReadAllText(IntentPath));
                return stored?.CameraId is { } id && id != Guid.Empty ? id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private DeviceCameraWindow(AppModel model, Guid? cameraId)
    {
        _model = model;
        InitializeComponent();
        NewNameBox.Text = Environment.MachineName;
        Loaded += async (_, _) =>
        {
            await LoadTargetsAsync();
            if (cameraId is { } id) await StreamToAsync(id);
        };
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- Choosing the camera

    private async Task LoadTargetsAsync()
    {
        try
        {
            var cameras = await _model.Client.CamerasAsync();
            var published = cameras.Where(camera => camera.IsDevicePublished).ToList();
            TargetList.ItemsSource = published;
            // The camera this PC streamed to last time, if it still exists. Never "the only one
            // there is": on a first run that one is usually another device's, and it must not be
            // taken over by a click on the wrong button.
            var remembered = published.FindIndex(camera => camera.Id == LastCameraId);
            if (remembered >= 0) TargetList.SelectedIndex = remembered;
            UseExistingButton.IsEnabled = published.Count > 0;
            ChoiceMessage.Text = published.Count == 0
                ? "No device cameras exist yet. Create one for this PC below - it appears on the dashboard the moment you do."
                : "Each device streams to its own camera. Pick this PC's, or make a new one.";
        }
        catch (Exception error)
        {
            ChoiceMessage.Text = $"Couldn't load the camera list: {SentinelClient.Describe(error)}";
        }
    }

    private async void OnUseExistingClicked(object sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedItem is Camera camera) await StreamToAsync(camera.Id);
    }

    private async void OnCreateClicked(object sender, RoutedEventArgs e)
    {
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ChoiceMessage.Text = "Give the new camera a name.";
            return;
        }
        CreateButton.IsEnabled = false;
        try
        {
            var created = await _model.Client.CreateCameraAsync(new CameraInput
            {
                Name = name,
                StreamUrl = "",
                IsBrowserPublished = true,
            });
            await StreamToAsync(created.Id);
        }
        catch (Exception error)
        {
            ChoiceMessage.Text = $"Couldn't create the camera: {SentinelClient.Describe(error)}";
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- The page

    private async Task StreamToAsync(Guid cameraId)
    {
        _cameraId = cameraId;
        if (!await EnsureInitializedAsync()) return;

        SyncCookies();
        ChoicePanel.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        Web.CoreWebView2.Navigate($"{_origin}/publish.html?camera={cameraId}");
    }

    private async Task<bool> EnsureInitializedAsync()
    {
        var address = string.IsNullOrEmpty(_model.ActiveServerUrl) ? _model.ServerUrl : _model.ActiveServerUrl;
        if (ServerAddress.Normalize(address) is not { } normalized ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var serverUri))
        {
            ShowPlaceholder("NO SERVER", "The server address isn't set yet.");
            return false;
        }
        _origin = serverUri.GetLeftPart(UriPartial.Authority);
        if (Web.CoreWebView2 is not null) return true;

        try
        {
            // Its own browser profile, apart from the live tiles': the engine's command line is
            // fixed per profile, and this one names the origin that is to count as secure. The
            // folder is keyed by that origin so a different server gets a different profile rather
            // than an error about a mismatched one.
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(_origin)))[..12];
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SentinelCore",
                $"WebView2-publisher-{key}");
            Directory.CreateDirectory(userData);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = $"--unsafely-treat-insecure-origin-as-secure={_origin}",
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception error)
        {
            _model.Log($"WebView2 is unavailable for the device camera: {error.Message}", DiagnosticLevel.Error);
            ShowPlaceholder(
                "UNAVAILABLE",
                "Streaming this PC's webcam needs the Microsoft Edge WebView2 runtime, which isn't installed on this PC.");
            return false;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        var host = serverUri.Host;
        core.PermissionRequested += (_, args) =>
        {
            // The page's camera and microphone prompts, answered here so no browser-style dialog
            // appears inside the app - and only for the server's own origin.
            if (args.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone &&
                Uri.TryCreate(args.Uri, UriKind.Absolute, out var from) &&
                string.Equals(from.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                args.State = CoreWebView2PermissionState.Allow;
            }
        };
        core.ServerCertificateErrorDetected += (_, args) =>
        {
            // Only the server's own certificate - one its remembered authority signed (see
            // ServerIdentity). Anything else at the same host is refused rather than waved through:
            // this page carries the session cookie and streams this PC's camera.
            args.Action = Uri.TryCreate(args.RequestUri, UriKind.Absolute, out var requested) &&
                          string.Equals(requested.Host, host, StringComparison.OrdinalIgnoreCase) &&
                          PresentedCertificate(args.ServerCertificate) is { } presented &&
                          _model.ServerIdentity.Accepts(requested, presented)
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Cancel;
        };
        core.WebMessageReceived += OnWebMessageReceived;

        // The page reports its state to this window through the module every host of it shares:
        // whether it is streaming, and what it is saying about it. Wired before any script on the
        // page runs, so nothing is missed.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("""
            document.addEventListener('DOMContentLoaded', () => {
              const publisher = window.SentinelPublisher;
              if (!publisher || !window.chrome?.webview) return;
              publisher.onChange((state) => window.chrome.webview.postMessage({
                kind: 'publisher',
                publishing: !!state.publishing,
                status: state.status || '',
                camera: state.camera ? state.camera.name : null
              }));
            });
            """);
        return true;
    }

    // The certificate WebView2 was shown, in the form ServerIdentity can check.
    private static System.Security.Cryptography.X509Certificates.X509Certificate2? PresentedCertificate(
        CoreWebView2Certificate? certificate)
    {
        if (certificate is null) return null;
        try
        {
            return System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(certificate.ToPemEncoding());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies this session's cookies into the engine, so the page's own requests are authenticated
    /// exactly as the rest of the app is. Same pattern as the live player.
    /// </summary>
    private void SyncCookies()
    {
        if (Web.CoreWebView2 is null || !Uri.TryCreate(_origin, UriKind.Absolute, out var originUri)) return;
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

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("kind", out var kind) || kind.GetString() != "publisher") return;

            var publishing = root.TryGetProperty("publishing", out var p) && p.ValueKind == JsonValueKind.True;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var camera = root.TryGetProperty("camera", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            SetPublishing(publishing, camera);
            StateText.Text = status;
        }
        catch (JsonException)
        {
            // Not a message this window understands.
        }
    }

    // ---------------------------------------------------------------- Staying awake

    private void SetPublishing(bool publishing, string? cameraName)
    {
        if (publishing == _publishing) return;
        _publishing = publishing;
        Title = publishing ? $"Streaming as \"{cameraName ?? "camera"}\"" : "This PC as a camera";

        // The one thing a browser tab cannot do. ES_CONTINUOUS makes it stick until cleared; the
        // display is kept on as well because a webcam page in a window whose display has gone to
        // sleep is throttled to a crawl by the engine.
        if (publishing)
        {
            SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
            RememberIntent();
        }
        else
        {
            SetThreadExecutionState(ExecutionState.Continuous);
            ForgetIntent();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_publishing)
        {
            var answer = MessageBox.Show(
                this,
                "This PC is streaming as a camera. Closing this window stops the stream.\n\nClose it anyway?",
                "Stop streaming?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            // A deliberate stop: the app must not bring this back on its next launch. An app quit
            // with the window open goes through the same handler - Windows asks the same
            // question - and answering yes is just as deliberate.
            ForgetIntent();
        }
        SetThreadExecutionState(ExecutionState.Continuous);
        try
        {
            Web.CoreWebView2?.Navigate("about:blank");
        }
        catch (Exception)
        {
            // Tearing down; nothing left to affect.
        }
    }

    // ---------------------------------------------------------------- Surviving a relaunch

    private sealed record Intent(Guid CameraId);

    // Kept across a stop, unlike the intent, so the list offers the same camera next time rather
    // than a fresh one being created.
    private static string LastCameraPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCore", "device-camera-last.json");

    private static Guid? LastCameraId
    {
        get
        {
            try
            {
                if (!File.Exists(LastCameraPath)) return null;
                var stored = JsonSerializer.Deserialize<Intent>(File.ReadAllText(LastCameraPath));
                return stored?.CameraId is { } id && id != Guid.Empty ? id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static string IntentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCore", "device-camera.json");

    private void RememberIntent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IntentPath)!);
            File.WriteAllText(IntentPath, JsonSerializer.Serialize(new Intent(_cameraId)));
            File.WriteAllText(LastCameraPath, JsonSerializer.Serialize(new Intent(_cameraId)));
        }
        catch (Exception error)
        {
            _model.Log($"Couldn't remember the device camera: {error.Message}", DiagnosticLevel.Notice);
        }
    }

    private static void ForgetIntent()
    {
        try
        {
            if (File.Exists(IntentPath)) File.Delete(IntentPath);
        }
        catch (Exception)
        {
            // A stale intent only means one extra window on the next launch.
        }
    }

    private void ShowPlaceholder(string title, string message)
    {
        ChoicePanel.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        PlaceholderTitle.Text = title;
        PlaceholderMessage.Text = message;
    }

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);
}
