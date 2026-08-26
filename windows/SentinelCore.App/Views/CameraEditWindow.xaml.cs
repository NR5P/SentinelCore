using System.Windows;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// Adding a camera, or editing one that exists.
/// </summary>
/// <remarks>
/// The PUT endpoint overwrites the whole record rather than merging, so every field this form does
/// not show still has to be sent back exactly as it arrived - the ONVIF PTZ service URL and profile
/// token in particular. Omitting them turns "rename a camera" into "silently break its PTZ", which
/// is precisely the bug the Apple client's CameraInput comments warn about.
/// </remarks>
public partial class CameraEditWindow : Window
{
    private readonly AppModel _model;
    private readonly Camera? _existing;

    // Carried through the edit untouched unless a probe replaces them.
    private string? _onvifPtzServiceUrl;
    private string? _onvifProfileToken;
    private bool _passwordRevealed;

    public CameraEditWindow(AppModel model, Camera? existing)
    {
        _model = model;
        _existing = existing;
        InitializeComponent();

        HeadingText.Text = existing is null ? "Add a camera" : "Edit camera";
        Title = HeadingText.Text;

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            StreamBox.Text = existing.StreamUrl ?? "";
            UsernameBox.Text = existing.Username ?? "";
            // The list carries only the mask; the real value is fetched on demand by Show.
            PasswordBox.Text = existing.Password ?? "";
            AudioToggle.IsChecked = existing.IncludeAudio == true;
            PtzToggle.IsChecked = existing.PtzEnabled == true;
            _onvifPtzServiceUrl = existing.OnvifPtzServiceUrl;
            _onvifProfileToken = existing.OnvifProfileToken;

            MaskNotice.Visibility = existing.Password == SentinelSecrets.Mask
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
            RevealButton.IsEnabled = false;
        }

        UpdatePtzNotice();
        PtzToggle.Click += (_, _) => UpdatePtzNotice();
    }

    /// <summary>
    /// Seeds the form from a discovery result. The address is the camera's IP rather than a stream
    /// URL, because that is all a scan knows - Test connection turns it into one.
    /// </summary>
    public void PrefillFromDiscovery(ScanResultCamera result)
    {
        NameBox.Text = result.SuggestedName;
        StreamBox.Text = result.IpAddress;
        Message(result.NeedsCredentials
            ? "This camera wants a username and password. Fill them in, then press Test connection."
            : "Press Test connection to find this camera's stream address.");
    }

    private void UpdatePtzNotice()
    {
        if (PtzToggle.IsChecked != true)
        {
            PtzNotice.Text = "";
            return;
        }

        PtzNotice.Text = string.IsNullOrEmpty(_onvifPtzServiceUrl)
            ? "No ONVIF PTZ service is stored for this camera yet. Run Test connection below to find one - " +
              "without it the PTZ controls have nowhere to send a command."
            : "ONVIF PTZ service found and stored for this camera.";
    }

    private async void OnRevealClicked(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        if (_passwordRevealed) return;

        RevealButton.IsEnabled = false;
        try
        {
            // Deliberately per-camera and on demand: the camera list carries only the mask, so
            // nothing holds a live device credential in memory until someone actually asks.
            PasswordBox.Text = await _model.Client.CameraPasswordAsync(_existing.Id);
            _passwordRevealed = true;
            MaskNotice.Visibility = Visibility.Collapsed;
            RevealButton.Content = "Shown";
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            RevealButton.IsEnabled = true;
        }
    }

    private async void OnTestClicked(object sender, RoutedEventArgs e)
    {
        var address = StreamBox.Text.Trim();
        if (address.Length == 0)
        {
            Message("Enter a stream address, or an IP address, to test.");
            return;
        }

        TestButton.IsEnabled = false;
        TestResult("Testing…");
        try
        {
            var result = await _model.Client.ProbeCameraAsync(new CameraProbeRequest
            {
                Address = address,
                Username = Nullable(UsernameBox.Text),
                Password = PasswordForSend(),
            });

            switch (result.Status)
            {
                case "Found":
                    if (result.StreamUrl is { Length: > 0 }) StreamBox.Text = result.StreamUrl;
                    if (NameBox.Text.Trim().Length == 0 && result.SuggestedName is { Length: > 0 })
                    {
                        NameBox.Text = result.SuggestedName;
                    }
                    // Only ever populated when an ONVIF PTZ service was actually found - absent
                    // just means this camera doesn't have one, which is the normal case.
                    if (result.SupportsPtz == true)
                    {
                        _onvifPtzServiceUrl = result.OnvifPtzServiceUrl;
                        _onvifProfileToken = result.OnvifProfileToken;
                        PtzToggle.IsChecked = true;
                    }
                    UpdatePtzNotice();
                    TestResult(Describe(result));
                    break;
                case "NeedsCredentials":
                    TestResult(result.Message ?? "That camera answered, but needs a username and password.");
                    break;
                default:
                    TestResult(result.Message ?? "Nothing answered at that address.");
                    break;
            }
        }
        catch (Exception error)
        {
            TestResult(AppModel.UserFacingMessage(error));
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static string Describe(CameraProbeResult result)
    {
        var parts = new List<string> { "Connected." };
        if (result.Manufacturer is { Length: > 0 } || result.Model is { Length: > 0 })
        {
            parts.Add($"{result.Manufacturer} {result.Model}".Trim());
        }
        if (result.Resolution is { Length: > 0 }) parts.Add(result.Resolution);
        if (result.SupportsPtz == true) parts.Add("PTZ available.");
        return string.Join(" · ", parts);
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var stream = StreamBox.Text.Trim();
        if (name.Length == 0 || stream.Length == 0)
        {
            Message("A camera needs a name and a stream address.");
            return;
        }

        var input = new CameraInput
        {
            Name = name,
            StreamUrl = stream,
            Username = Nullable(UsernameBox.Text),
            Password = PasswordForSend(),
            IncludeAudio = AudioToggle.IsChecked == true,
            PtzEnabled = PtzToggle.IsChecked == true,
            // Carried through every save so editing a camera's name doesn't unbind its PTZ.
            OnvifPtzServiceUrl = _onvifPtzServiceUrl,
            OnvifProfileToken = _onvifProfileToken,
        };

        SaveButton.IsEnabled = false;
        try
        {
            if (_existing is null)
            {
                await _model.Client.CreateCameraAsync(input);
            }
            else
            {
                await _model.Client.UpdateCameraAsync(_existing.Id, input);
            }
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The mask goes back unchanged to mean "keep the password you have"; anything else is a real
    /// new value. An empty box on a new camera means there is no login.
    /// </summary>
    private string? PasswordForSend()
    {
        var value = PasswordBox.Text;
        return value.Length == 0 ? null : value;
    }

    private static string? Nullable(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private void TestResult(string message)
    {
        TestResultText.Text = message;
        TestResultText.Visibility = Visibility.Visible;
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
