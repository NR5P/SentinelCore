using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class DeviceEditWindow : Window
{
    private readonly AppModel _model;
    private readonly HubDevice? _existing;

    public DeviceEditWindow(AppModel model, HubDevice? existing, HubDeviceInput? prefill = null)
    {
        _model = model;
        _existing = existing;
        InitializeComponent();

        HeadingText.Text = existing is null ? "Add a device" : "Edit device";
        Title = HeadingText.Text;
        DeleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;

        Loaded += async (_, _) =>
        {
            await LoadMetadataAsync();
            Populate(prefill ?? (existing is null ? new HubDeviceInput() : HubDeviceInput.FromDevice(existing)));
        };
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            var metadata = await _model.Client.DeviceMetadataAsync();
            KindBox.ItemsSource = metadata.Kinds.Count > 0
                ? metadata.Kinds
                : new List<string> { "light", "switch", "outlet", "fan", "cover", "gate", "garage", "lock", "button", "sensor" };
            SchemaBox.ItemsSource = metadata.Schemas.Count > 0
                ? metadata.Schemas
                : new List<string> { "raw", "json" };
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void Populate(HubDeviceInput input)
    {
        NameBox.Text = input.Name;
        KindBox.SelectedItem = input.Kind;
        RoomBox.Text = input.Room;
        EnabledToggle.IsChecked = input.Enabled;
        CommandTopicBox.Text = input.CommandTopic;
        StateTopicBox.Text = input.StateTopic;
        AvailabilityTopicBox.Text = input.AvailabilityTopic;
        PayloadOnBox.Text = input.PayloadOn;
        PayloadOffBox.Text = input.PayloadOff;
        PayloadStopBox.Text = input.PayloadStop;
        MomentaryToggle.IsChecked = input.Momentary;
        PayloadAvailableBox.Text = input.PayloadAvailable;
        PayloadNotAvailableBox.Text = input.PayloadNotAvailable;
        SchemaBox.SelectedItem = input.Schema;
        TemplateBox.Text = input.StateValueTemplate;
        OptimisticToggle.IsChecked = input.Optimistic;
        BrightnessToggle.IsChecked = input.SupportsBrightness;
        BrightnessCommandBox.Text = input.BrightnessCommandTopic;
        BrightnessStateBox.Text = input.BrightnessStateTopic;
        BrightnessScaleBox.Text = input.BrightnessScale.ToString(CultureInfo.InvariantCulture);

        ApplyKind();
        ApplyBrightness();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => ApplyKind();

    private void ApplyKind()
    {
        // Kinds that speak open/close rather than on/off get the wording, and the stop payload,
        // that actually matches what they do.
        var kind = KindBox.SelectedItem as string ?? "light";
        var coverLike = kind is "cover" or "gate" or "garage";
        var lockLike = kind == "lock";

        PayloadOnLabel.Text = coverLike ? "Payload for open" : lockLike ? "Payload for unlock" : "Payload for on";
        PayloadOffLabel.Text = coverLike ? "Payload for close" : lockLike ? "Payload for lock" : "Payload for off";
        StopPanel.Visibility = coverLike ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrightnessToggled(object sender, RoutedEventArgs e) => ApplyBrightness();

    private void ApplyBrightness() =>
        BrightnessPanel.Visibility = BrightnessToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Message("Give the device a name.");
            return;
        }

        var kind = KindBox.SelectedItem as string ?? "light";
        var commandTopic = CommandTopicBox.Text.Trim();
        var stateTopic = StateTopicBox.Text.Trim();
        if (commandTopic.Length == 0 && stateTopic.Length == 0)
        {
            // A device with neither topic can be neither controlled nor listened to, so it would sit
            // in the hub doing nothing at all.
            Message("A device needs at least a command topic or a state topic.");
            return;
        }

        if (!int.TryParse(BrightnessScaleBox.Text.Trim(), out var scale) || scale <= 0) scale = 255;

        var input = new HubDeviceInput
        {
            Name = name,
            Kind = kind,
            Room = RoomBox.Text.Trim(),
            Enabled = EnabledToggle.IsChecked == true,
            CommandTopic = commandTopic,
            StateTopic = stateTopic,
            AvailabilityTopic = AvailabilityTopicBox.Text.Trim(),
            PayloadOn = PayloadOnBox.Text,
            PayloadOff = PayloadOffBox.Text,
            PayloadStop = PayloadStopBox.Text.Trim(),
            Momentary = MomentaryToggle.IsChecked == true,
            PayloadAvailable = PayloadAvailableBox.Text,
            PayloadNotAvailable = PayloadNotAvailableBox.Text,
            Schema = SchemaBox.SelectedItem as string ?? "raw",
            StateValueTemplate = TemplateBox.Text.Trim(),
            Optimistic = OptimisticToggle.IsChecked == true,
            SupportsBrightness = BrightnessToggle.IsChecked == true,
            BrightnessCommandTopic = BrightnessCommandBox.Text.Trim(),
            BrightnessStateTopic = BrightnessStateBox.Text.Trim(),
            BrightnessScale = scale,
            DiscoverySource = _existing?.DiscoverySource ?? "manual",
        };

        SaveButton.IsEnabled = false;
        try
        {
            if (_existing is null)
            {
                await _model.Client.CreateDeviceAsync(input);
            }
            else
            {
                await _model.Client.UpdateDeviceAsync(_existing.Id, input);
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

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;

        var confirmed = MessageBox.Show(
            this,
            $"Delete \"{_existing.Name}\" from the Device Hub?\n\nThe device itself is untouched - it just stops " +
            "being listed and controllable here. Any schedule pointing at it stops working.",
            "Delete device",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.DeleteDeviceAsync(_existing.Id);
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
