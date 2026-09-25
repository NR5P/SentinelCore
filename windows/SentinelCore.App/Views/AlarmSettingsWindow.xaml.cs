using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>Delays, sirens, cameras, notifications and the PIN.</summary>
public partial class AlarmSettingsWindow : Window
{
    private readonly AppModel _model;
    private readonly AlarmConfig _config;
    private readonly bool _pinSet;
    private readonly List<(Guid Id, CheckBox Box)> _sirens = new();
    private readonly List<(Guid Id, CheckBox Box)> _cameras = new();

    public AlarmSettingsWindow(AppModel model, AlarmConfig config, bool pinSet)
    {
        _model = model;
        _config = config;
        _pinSet = pinSet;
        InitializeComponent();

        ExitBox.Text = config.ExitDelaySeconds.ToString(CultureInfo.InvariantCulture);
        EntryBox.Text = config.EntryDelaySeconds.ToString(CultureInfo.InvariantCulture);
        SirenBox.Text = config.SirenSeconds.ToString(CultureInfo.InvariantCulture);
        PushToggle.IsChecked = config.NotifyPush;
        ArmDisarmToggle.IsChecked = config.NotifyArmDisarm;
        BatteryBox.Text = config.LowBatteryPercent.ToString(CultureInfo.InvariantCulture);
        OfflineBox.Text = config.OfflineAfterHours.ToString(CultureInfo.InvariantCulture);
        PinToArmToggle.IsChecked = config.RequirePinToArm;
        PinLabel.Text = pinSet ? "New PIN (leave blank to keep the current one)" : "PIN (4-8 digits, blank for none)";
        PinHint.Text = pinSet ? "Disarming needs the PIN." : "No PIN: anyone signed in can disarm.";
        RemovePinToggle.Visibility = pinSet ? Visibility.Visible : Visibility.Collapsed;

        foreach (var camera in model.Cameras)
        {
            var box = new CheckBox { Content = camera.Name, IsChecked = config.CameraIds.Contains(camera.Id), Margin = new Thickness(0, 4, 0, 0) };
            _cameras.Add((camera.Id, box));
            CameraPanel.Children.Add(box);
        }
        if (model.Cameras.Count == 0)
            CameraPanel.Children.Add(new TextBlock { Text = "No cameras yet.", Style = (Style)FindResource("Caption") });

        Loaded += async (_, _) => await LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _model.Client.DevicesAsync();
            var switchable = devices.Where(d => d.Kind is not ("sensor" or "button" or "cover" or "gate" or "garage" or "lock")).ToList();
            foreach (var device in switchable)
            {
                var box = new CheckBox
                {
                    Content = string.IsNullOrEmpty(device.Room) ? device.Name : $"{device.Name} ({device.Room})",
                    IsChecked = _config.SirenDeviceIds.Contains(device.Id),
                    Margin = new Thickness(0, 4, 0, 0),
                };
                _sirens.Add((device.Id, box));
                SirenPanel.Children.Add(box);
            }
            if (switchable.Count == 0)
            {
                SirenPanel.Children.Add(new TextBlock
                {
                    Text = "No switchable Device Hub devices yet. A siren, a strobe or a plug with a lamp on it can be switched on when the alarm goes off.",
                    Style = (Style)FindResource("Caption"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        catch (Exception error)
        {
            MessageText.Text = AppModel.UserFacingMessage(error);
        }
    }

    private static int Number(TextBox box, int fallback) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var config = new AlarmConfig
        {
            ExitDelaySeconds = Number(ExitBox, _config.ExitDelaySeconds),
            EntryDelaySeconds = Number(EntryBox, _config.EntryDelaySeconds),
            SirenSeconds = Number(SirenBox, _config.SirenSeconds),
            SirenDeviceIds = _sirens.Where(s => s.Box.IsChecked == true).Select(s => s.Id).ToList(),
            CameraIds = _cameras.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList(),
            NotifyPush = PushToggle.IsChecked == true,
            NotifyArmDisarm = ArmDisarmToggle.IsChecked == true,
            LowBatteryPercent = Number(BatteryBox, _config.LowBatteryPercent),
            OfflineAfterHours = Number(OfflineBox, _config.OfflineAfterHours),
            RequirePinToArm = PinToArmToggle.IsChecked == true,
        };
        SaveButton.IsEnabled = false;
        try
        {
            await _model.Client.SaveAlarmConfigAsync(config);
            var newPin = PinBox.Password.Trim();
            if (_pinSet && RemovePinToggle.IsChecked == true) await _model.Client.SetAlarmPinAsync("", null);
            else if (newPin.Length > 0) await _model.Client.SetAlarmPinAsync(newPin, null);
            DialogResult = true;
        }
        catch (Exception error)
        {
            MessageText.Text = AppModel.UserFacingMessage(error);
            SaveButton.IsEnabled = true;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
}
