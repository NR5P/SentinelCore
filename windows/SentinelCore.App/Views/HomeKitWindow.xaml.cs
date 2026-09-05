using System.Globalization;
using System.Windows;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class HomeKitWindow : Window
{
    private readonly AppModel _model;

    public HomeKitWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await LoadSettingsAsync();
            await LoadStatusAsync();
        };
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _model.Client.HomeKitSettingsAsync();
            EnabledToggle.IsChecked = settings.Enabled;
            SidecarBox.Text = settings.SidecarBaseUrl;
            TimeoutBox.Text = settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ConfidenceBox.Text = settings.MinimumConfidence.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            var status = await _model.Client.HomeKitStatusAsync();
            StatusText.Text = Describe(status);

            // Running and published are deliberately separate facts: the sidecar runs for everyone,
            // but only advertises on the network once HomeKit is switched on.
            var code = status.PairingCode;
            var showCode = status.Published == true && status.Paired != true && code is { Length: > 0 };
            PairingPanel.Visibility = showCode ? Visibility.Visible : Visibility.Collapsed;
            PairingCodeText.Text = code ?? "";

            AccessoryList.ItemsSource = status.Accessories is { Count: > 0 }
                ? status.Accessories.Select(name => $"· {name}").ToList()
                : null;
        }
        catch (Exception error)
        {
            StatusText.Text = "Could not reach the HomeKit sidecar.";
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private static string Describe(HomeKitStatus status)
    {
        if (!status.Running)
        {
            return status.Error is { Length: > 0 }
                ? $"The HomeKit sidecar is not running: {status.Error}"
                : "The HomeKit sidecar is not running.";
        }
        if (status.Enabled != true) return "The sidecar is running, but HomeKit is switched off.";
        if (status.Published != true) return "The sidecar is running and enabled, but not advertising yet.";
        return status.Paired == true
            ? "Paired with the Home app and publishing."
            : "Advertising on the network and waiting to be paired.";
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimeoutBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ||
            timeout <= 0)
        {
            Message("The timeout has to be a whole number of seconds.");
            return;
        }
        if (!double.TryParse(ConfidenceBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) ||
            confidence is < 0 or > 1)
        {
            Message("The minimum confidence has to be between 0 and 1.");
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            await _model.Client.SaveHomeKitSettingsAsync(new HomeKitSettings
            {
                Enabled = EnabledToggle.IsChecked == true,
                SidecarBaseUrl = SidecarBox.Text.Trim(),
                TimeoutSeconds = timeout,
                MinimumConfidence = confidence,
            });
            Design.SaveFeedback.Saved(MessageText, SaveButton);
            await LoadStatusAsync();
        }
        catch (Exception error)
        {
            Design.SaveFeedback.Failed(MessageText, AppModel.UserFacingMessage(error));
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnResetClicked(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            this,
            "Reset the HomeKit pairing?\n\nThe bridge gets a new identity and a new code. Every Home app on the " +
            "network loses it and has to pair again, and any automations built on these accessories stop working " +
            "until they do.",
            "Reset pairing",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        ResetButton.IsEnabled = false;
        try
        {
            await _model.Client.ResetHomeKitPairingAsync();
            Message("Pairing reset. A new code should appear shortly.");
            await LoadStatusAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            ResetButton.IsEnabled = true;
        }
    }

    private void Message(string message) => Design.SaveFeedback.Note(MessageText, message);

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
