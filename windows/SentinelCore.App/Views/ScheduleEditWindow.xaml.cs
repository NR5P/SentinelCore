using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class ScheduleEditWindow : Window
{
    private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    private readonly AppModel _model;
    private readonly ScheduleMetadata _metadata;
    private readonly Schedule? _existing;
    private readonly List<CheckBox> _dayBoxes = new();

    public ScheduleEditWindow(AppModel model, ScheduleMetadata metadata, Schedule? existing)
    {
        _model = model;
        _metadata = metadata;
        _existing = existing;
        InitializeComponent();

        HeadingText.Text = existing is null ? "Add a schedule" : "Edit schedule";
        Title = HeadingText.Text;

        BuildChoices();
        Populate(existing);
    }

    private void BuildChoices()
    {
        // Every vocabulary here comes from the server, so a kind or an action added there shows up
        // without this app being changed.
        KindBox.ItemsSource = _metadata.Kinds.Count > 0
            ? _metadata.Kinds
            : new List<string> { "daily", "weekly", "interval", "cron", "once", "solar" };
        ActionTypeBox.ItemsSource = _metadata.ActionTypes.Count > 0
            ? _metadata.ActionTypes
            : new List<string> { "device", "mqtt", "webhook" };
        CatchUpBox.ItemsSource = _metadata.CatchUpPolicies.Count > 0
            ? _metadata.CatchUpPolicies
            : new List<string> { "skip", "run" };
        SolarEventBox.ItemsSource = _metadata.SolarEvents;
        DeviceActionBox.ItemsSource = _metadata.DeviceActions.Count > 0
            ? _metadata.DeviceActions
            : new List<string> { "on", "off" };

        // Named darkness levels, so this app doesn't have to know that "civil twilight" means -6°.
        DarknessBox.ItemsSource = _metadata.DarknessLevels;

        DeviceBox.ItemsSource = _metadata.Devices.Where(device => device.Enabled).ToList();

        foreach (var (name, index) in DayNames.Select((name, index) => (name, index)))
        {
            var box = new CheckBox { Content = name, Margin = new Thickness(0, 0, 12, 6), Tag = index };
            _dayBoxes.Add(box);
            DaysHost.Children.Add(box);
        }

        LocationWarning.Visibility = _metadata.LocationSet ? Visibility.Collapsed : Visibility.Visible;
        DarknessBox.SelectionChanged += (_, _) =>
        {
            DarknessHint.Text = DarknessBox.SelectedItem is DarknessLevel level ? level.Description : "";
        };
    }

    private void Populate(Schedule? schedule)
    {
        if (schedule is null)
        {
            KindBox.SelectedItem = "daily";
            ActionTypeBox.SelectedItem = "device";
            CatchUpBox.SelectedIndex = 0;
            TimeOfDayBox.Text = "18:00";
            IntervalBox.Text = Math.Max(_metadata.MinimumIntervalSeconds, 60).ToString(CultureInfo.InvariantCulture);
            WindowStartBox.Text = "00:00";
            WindowEndBox.Text = "23:59";
            WebhookMethodBox.SelectedIndex = 0;
            WebhookContentTypeBox.Text = "application/json";
            RunAtBox.Text = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            SolarOffsetBox.Text = "0";
            if (DarknessBox.Items.Count > 0) DarknessBox.SelectedIndex = 0;
            ApplyKind();
            ApplyActionType();
            return;
        }

        NameBox.Text = schedule.Name;
        EnabledToggle.IsChecked = schedule.Enabled;
        KindBox.SelectedItem = schedule.Kind;
        TimeOfDayBox.Text = schedule.TimeOfDay;
        IntervalBox.Text = schedule.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        WindowStartBox.Text = schedule.WindowStart;
        WindowEndBox.Text = schedule.WindowEnd;
        CronBox.Text = schedule.CronExpression;
        RunAtBox.Text = schedule.RunAtUtc is { } at
            ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
        SolarEventBox.SelectedItem = schedule.SolarEvent;
        SolarOffsetBox.Text = schedule.SolarOffsetMinutes.ToString(CultureInfo.InvariantCulture);
        DarknessBox.SelectedValue = schedule.SolarElevationDegrees;
        CatchUpBox.SelectedItem = schedule.CatchUpPolicy;

        foreach (var box in _dayBoxes)
        {
            box.IsChecked = box.Tag is int day && schedule.DaysOfWeek.Contains(day);
        }

        ActionTypeBox.SelectedItem = schedule.Action.Type;
        DeviceBox.SelectedValue = schedule.Action.DeviceId;
        DeviceActionBox.SelectedItem = schedule.Action.DeviceAction;
        BrightnessBox.Text = schedule.Action.DeviceBrightness?.ToString(CultureInfo.InvariantCulture) ?? "";
        MqttTopicBox.Text = schedule.Action.MqttTopic;
        MqttPayloadBox.Text = schedule.Action.MqttPayload;
        MqttRetainToggle.IsChecked = schedule.Action.MqttRetain;
        WebhookUrlBox.Text = schedule.Action.WebhookUrl;
        WebhookMethodBox.SelectedIndex = schedule.Action.WebhookMethod switch
        {
            "GET" => 1,
            "PUT" => 2,
            _ => 0,
        };
        WebhookContentTypeBox.Text = schedule.Action.WebhookContentType;
        WebhookHeadersBox.Text = schedule.Action.WebhookHeaders;
        WebhookBodyBox.Text = schedule.Action.WebhookBody;

        ApplyKind();
        ApplyActionType();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => ApplyKind();

    private void ApplyKind()
    {
        var kind = KindBox.SelectedItem as string ?? "daily";
        TimeOfDayPanel.Visibility = kind is "daily" or "weekly" ? Visibility.Visible : Visibility.Collapsed;
        DaysPanel.Visibility = kind == "weekly" ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = kind == "interval" ? Visibility.Visible : Visibility.Collapsed;
        CronPanel.Visibility = kind == "cron" ? Visibility.Visible : Visibility.Collapsed;
        OncePanel.Visibility = kind == "once" ? Visibility.Visible : Visibility.Collapsed;
        SolarPanel.Visibility = kind == "solar" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnActionTypeChanged(object sender, SelectionChangedEventArgs e) => ApplyActionType();

    private void ApplyActionType()
    {
        var type = ActionTypeBox.SelectedItem as string ?? "device";
        DeviceActionPanel.Visibility = type == "device" ? Visibility.Visible : Visibility.Collapsed;
        MqttActionPanel.Visibility = type == "mqtt" ? Visibility.Visible : Visibility.Collapsed;
        WebhookActionPanel.Visibility = type == "webhook" ? Visibility.Visible : Visibility.Collapsed;
    }

    private ScheduleInput? BuildInput()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Message("Give the schedule a name.");
            return null;
        }

        var kind = KindBox.SelectedItem as string ?? "daily";
        var actionType = ActionTypeBox.SelectedItem as string ?? "device";

        if (kind == "solar" && !_metadata.LocationSet)
        {
            Message("A sun-following schedule needs a latitude and longitude on the server first.");
            return null;
        }
        if (kind == "weekly" && _dayBoxes.All(box => box.IsChecked != true))
        {
            Message("Pick at least one day.");
            return null;
        }

        var input = new ScheduleInput
        {
            Name = name,
            Enabled = EnabledToggle.IsChecked == true,
            Kind = kind,
            TimeOfDay = TimeOfDayBox.Text.Trim(),
            DaysOfWeek = _dayBoxes.Where(box => box.IsChecked == true).Select(box => (int)box.Tag).ToList(),
            WindowStart = WindowStartBox.Text.Trim(),
            WindowEnd = WindowEndBox.Text.Trim(),
            CronExpression = CronBox.Text.Trim(),
            SolarEvent = SolarEventBox.SelectedItem as string ?? "",
            CatchUpPolicy = CatchUpBox.SelectedItem as string ?? "",
            ActionType = actionType,
            MqttTopic = MqttTopicBox.Text.Trim(),
            MqttPayload = MqttPayloadBox.Text,
            MqttRetain = MqttRetainToggle.IsChecked == true,
            WebhookUrl = WebhookUrlBox.Text.Trim(),
            WebhookMethod = (WebhookMethodBox.SelectedItem as ComboBoxItem)?.Content as string ?? "POST",
            WebhookContentType = WebhookContentTypeBox.Text.Trim(),
            WebhookHeaders = WebhookHeadersBox.Text,
            WebhookBody = WebhookBodyBox.Text,
        };

        if (kind == "interval")
        {
            if (!int.TryParse(IntervalBox.Text.Trim(), out var interval) ||
                interval < _metadata.MinimumIntervalSeconds)
            {
                Message($"The interval has to be at least {_metadata.MinimumIntervalSeconds} seconds.");
                return null;
            }
            input.IntervalSeconds = interval;
        }

        if (kind == "once")
        {
            if (!DateTime.TryParse(RunAtBox.Text.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var runAt))
            {
                Message("Enter the run time as yyyy-MM-dd HH:mm.");
                return null;
            }
            // Sent as ISO-8601 in UTC: the box is in local time because that is what someone means
            // when they type a time, and the server works in UTC.
            input.RunAtUtc = runAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        if (kind == "solar")
        {
            if (!int.TryParse(SolarOffsetBox.Text.Trim(), out var offset))
            {
                Message("The offset has to be a whole number of minutes.");
                return null;
            }
            input.SolarOffsetMinutes = offset;
            input.SolarElevationDegrees = DarknessBox.SelectedItem is DarknessLevel level
                ? level.ElevationDegrees
                : 0;
        }

        if (actionType == "device")
        {
            if (DeviceBox.SelectedValue is not Guid deviceId)
            {
                Message("Pick the device this schedule should act on.");
                return null;
            }
            input.DeviceId = deviceId;
            input.DeviceAction = DeviceActionBox.SelectedItem as string ?? "on";

            var brightnessText = BrightnessBox.Text.Trim();
            if (brightnessText.Length > 0)
            {
                if (!int.TryParse(brightnessText, out var brightness))
                {
                    Message("Brightness has to be a whole number, or blank.");
                    return null;
                }
                input.DeviceBrightness = brightness;
            }
        }

        if (actionType == "mqtt" && input.MqttTopic.Length == 0)
        {
            Message("An MQTT action needs a topic.");
            return null;
        }
        if (actionType == "webhook" && input.WebhookUrl.Length == 0)
        {
            Message("A webhook action needs a URL.");
            return null;
        }

        return input;
    }

    /// <summary>
    /// Upcoming firing times for a schedule that hasn't been saved yet - the only practical way to
    /// check that a darkness level or a cron expression says what was meant.
    /// </summary>
    private async void OnPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (BuildInput() is not { } input) return;
        try
        {
            var preview = await _model.Client.PreviewScheduleAsync(input);
            PreviewPanel.Visibility = Visibility.Visible;
            PreviewExplanation.Text = string.IsNullOrWhiteSpace(preview.Explanation)
                ? $"Times shown in {preview.TimeZone}."
                : $"{preview.Explanation} (times in {preview.TimeZone})";
            PreviewList.ItemsSource = preview.Occurrences.Select(occurrence => occurrence.Local).ToList();
            Message(preview.Occurrences.Count == 0 ? "This schedule would never fire as configured." : "");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (BuildInput() is not { } input) return;

        SaveButton.IsEnabled = false;
        try
        {
            if (_existing is null)
            {
                await _model.Client.CreateScheduleAsync(input);
            }
            else
            {
                await _model.Client.UpdateScheduleAsync(_existing.Id, input);
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

    private void Message(string message) => MessageText.Text = message;

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
