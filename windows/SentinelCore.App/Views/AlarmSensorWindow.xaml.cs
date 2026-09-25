using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>Add or edit one sensor: which Device Hub device it is, what it guards, when it is watched.</summary>
public partial class AlarmSensorWindow : Window
{
    private sealed record DeviceChoice(Guid Id, string Label);
    private sealed record ZoneChoice(string Zone, string Label);

    private readonly AppModel _model;
    private readonly AlarmSensor? _existing;
    private readonly HashSet<Guid> _taken;

    public AlarmSensorWindow(AppModel model, AlarmSensor? existing, IReadOnlyList<AlarmSensor> sensors)
    {
        _model = model;
        _existing = existing;
        _taken = sensors.Where(s => s.Id != existing?.Id).Select(s => s.DeviceId).ToHashSet();
        InitializeComponent();

        HeadingText.Text = existing is null ? "Add a sensor" : "Edit sensor";
        Title = HeadingText.Text;
        ZoneBox.ItemsSource = AlarmZones.All.Select(zone => new ZoneChoice(zone, AlarmZones.Label(zone))).ToList();

        Loaded += async (_, _) => await LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        List<HubDevice> devices;
        try
        {
            devices = await _model.Client.DevicesAsync();
        }
        catch (Exception error)
        {
            MessageText.Text = AppModel.UserFacingMessage(error);
            devices = new List<HubDevice>();
        }

        var choices = devices
            .Where(d => d.Kind == "sensor" && !_taken.Contains(d.Id))
            .Select(d => new DeviceChoice(d.Id, string.IsNullOrEmpty(d.Room) ? d.Name : $"{d.Name} ({d.Room})"))
            .ToList();
        if (_existing is not null && choices.All(c => c.Id != _existing.DeviceId))
            choices.Insert(0, new DeviceChoice(_existing.DeviceId, _existing.DeviceName ?? "(missing device)"));
        DeviceBox.ItemsSource = choices;

        var input = _existing is null ? new AlarmSensorInput() : AlarmSensorInput.FromSensor(_existing);
        DeviceBox.SelectedItem = choices.FirstOrDefault(c => c.Id == input.DeviceId) ?? choices.FirstOrDefault();
        NameBox.Text = _existing is null ? (DeviceBox.SelectedItem as DeviceChoice)?.Label.Split(" (")[0] ?? "" : input.Name;
        ZoneBox.SelectedItem = ((List<ZoneChoice>)ZoneBox.ItemsSource).First(z => z.Zone == input.Zone);
        HomeToggle.IsChecked = input.ArmedHome;
        NightToggle.IsChecked = input.ArmedNight;
        AwayToggle.IsChecked = input.ArmedAway;
        InvertedToggle.IsChecked = input.Inverted;
        EnabledToggle.IsChecked = input.Enabled;
        SaveButton.IsEnabled = choices.Count > 0;
        if (choices.Count == 0) MessageText.Text = "No unused sensor devices in the Device Hub.";
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_existing is null && string.IsNullOrWhiteSpace(NameBox.Text) && DeviceBox.SelectedItem is DeviceChoice choice)
            NameBox.Text = choice.Label.Split(" (")[0];
    }

    private void OnZoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoneBox.SelectedItem is not ZoneChoice choice) return;
        ZoneHint.Text = AlarmZones.Explanation(choice.Zone);
        var always = choice.Zone == "always";
        WatchedHeading.Visibility = always ? Visibility.Collapsed : Visibility.Visible;
        WatchedPanel.Visibility = always ? Visibility.Collapsed : Visibility.Visible;
        if (_existing is not null) return;
        HomeToggle.IsChecked = choice.Zone != "interior";
        NightToggle.IsChecked = choice.Zone != "interior";
        AwayToggle.IsChecked = true;
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not DeviceChoice device || ZoneBox.SelectedItem is not ZoneChoice zone) return;
        var input = new AlarmSensorInput
        {
            DeviceId = device.Id,
            Name = NameBox.Text.Trim(),
            Zone = zone.Zone,
            ArmedHome = HomeToggle.IsChecked == true,
            ArmedNight = NightToggle.IsChecked == true,
            ArmedAway = AwayToggle.IsChecked == true,
            Inverted = InvertedToggle.IsChecked == true,
            Enabled = EnabledToggle.IsChecked == true,
        };
        SaveButton.IsEnabled = false;
        try
        {
            if (_existing is null) await _model.Client.CreateAlarmSensorAsync(input);
            else await _model.Client.UpdateAlarmSensorAsync(_existing.Id, input);
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
