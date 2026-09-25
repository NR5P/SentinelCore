using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Networking;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The home alarm: arm and disarm the house, see every sensor, and (for admins) set it up. The
/// state machine and the sensor subscription live on the server (see AlarmEngine.cs); this view
/// polls /api/alarm/status while it is on screen and counts the exit and entry delays down from
/// the deadlines the server hands it.
/// </summary>
public partial class AlarmView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly AppModel _model;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tick;
    private AlarmStatus? _status;
    private TimeSpan _clockOffset;
    private bool _isActive;

    public AlarmView(AppModel model)
    {
        _model = model;
        InitializeComponent();

        var admin = model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        TestButton.Visibility = admin;
        SettingsButton.Visibility = admin;
        AddButton.Visibility = admin;

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += async (_, _) => await RefreshAsync();
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _tick.Tick += (_, _) => RenderHeadline();

        IsVisibleChanged += (_, _) => SetActive(IsVisible);
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            _poll.Start();
            _tick.Start();
            _ = RefreshAsync();
        }
        else
        {
            _poll.Stop();
            _tick.Stop();
        }
    }

    private DateTimeOffset ServerNow => DateTimeOffset.UtcNow + _clockOffset;

    private async Task RefreshAsync()
    {
        if (!_isActive) return;
        try
        {
            var status = await _model.Client.AlarmStatusAsync();
            if (status.Now is { } now) _clockOffset = now - DateTimeOffset.UtcNow;
            _status = status;
            Render();
        }
        catch (Exception error)
        {
            BrokerDot.Fill = Palette.Amber;
            BrokerText.Text = AppModel.UserFacingMessage(error);
        }
    }

    private void Render()
    {
        if (_status is null) return;
        if (!_status.Enabled)
        {
            HeadlineText.Text = "Alarm is off";
            DetailText.Text = "The alarm is turned off in the server's Settings.";
            return;
        }

        var sensors = _status.Sensors ?? new List<AlarmSensor>();
        if (_status.HubEnabled != true)
        {
            BrokerDot.Fill = Palette.Red;
            BrokerText.Text = "The Device Hub is off - the alarm has nothing to listen to.";
        }
        else if (_status.BrokerConnected == true)
        {
            BrokerDot.Fill = Palette.Green;
            BrokerText.Text = $"{sensors.Count} sensor{(sensors.Count == 1 ? "" : "s")} · broker connected";
        }
        else
        {
            BrokerDot.Fill = sensors.Count == 0 ? Palette.SecondaryText : Palette.Red;
            BrokerText.Text = sensors.Count == 0 ? "No sensors yet" : "Not connected to the MQTT broker - sensors can't be heard.";
        }

        RenderHeadline();

        var by = _status.ChangedBy is { } who ? $" by {who}" : "";
        DetailText.Text = _status.Status switch
        {
            "triggered" => $"{_status.TriggeredBy ?? "Alarm"}{(_status.TriggeredAt is { } at ? $" at {at.LocalDateTime:t}" : "")}{(_status.SirenOn == true ? " · siren on" : "")}. Disarm to silence it.",
            "disarmed" => _status.ChangedAt is { } changed ? $"Disarmed{by} {changed.LocalDateTime:g}" : "Never armed",
            _ => $"{AlarmModes.Label(_status.Mode)}{by}{(_status.ChangedAt is { } since ? $" since {since.LocalDateTime:t}" : "")}"
        };

        if (_status.Status == "disarmed")
        {
            ReadyText.Visibility = Visibility.Visible;
            ReadyText.Text = _status.Ready == true ? "Ready to arm - every sensor is closed and reporting" : "Not ready";
            ReadyText.Foreground = _status.Ready == true ? Palette.Green : Palette.Amber;
        }
        else
        {
            ReadyText.Visibility = Visibility.Collapsed;
        }

        IssuesHost.Items.Clear();
        foreach (var issue in _status.Issues ?? new List<string>())
        {
            IssuesHost.Items.Add(new TextBlock
            {
                Text = "• " + issue,
                Foreground = Palette.Amber,
                FontSize = (double)FindResource("CaptionFontSize"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var mode = _status.Mode ?? "disarmed";
        foreach (var button in new[] { DisarmButton, HomeButton, NightButton, AwayButton })
        {
            var selected = (string)button.Tag == mode;
            button.IsEnabled = !selected || _status.IsTriggered;
            button.Style = selected ? (Style)FindResource("PrimaryButton") : null;
        }
        PanicButton.IsEnabled = !_status.IsTriggered;

        RebuildSensors(sensors);
    }

    private void RenderHeadline()
    {
        if (_status is not { Enabled: true }) return;
        HeadlineText.Text = _status.Status switch
        {
            "arming" => $"Arming away · {SecondsLeft(_status.ExitUntil)}s to leave",
            "pending" => $"Entry · {SecondsLeft(_status.PendingUntil)}s to disarm",
            "triggered" => "ALARM",
            "armed" => $"Armed {AlarmModes.Label(_status.Mode).ToLowerInvariant()}",
            _ => "Disarmed"
        };
        HeadlineText.Foreground = _status.Status switch
        {
            "armed" => Palette.Green,
            "arming" or "pending" => Palette.Amber,
            "triggered" => Palette.Red,
            _ => Palette.SecondaryText
        };
    }

    private int SecondsLeft(DateTimeOffset? until) =>
        until is { } deadline ? Math.Max(0, (int)Math.Ceiling((deadline - ServerNow).TotalSeconds)) : 0;

    private void RebuildSensors(List<AlarmSensor> sensors)
    {
        SensorHost.Items.Clear();
        if (sensors.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyText.Text = _status?.HubEnabled == true
                ? (_model.IsAdmin
                    ? "Add a Device Hub sensor - a door contact, a motion sensor, a leak sensor - and say what it guards."
                    : "An admin account can add sensors.")
                : "The alarm listens to Device Hub sensors, and the Device Hub is off. Turn on MQTT and the Device Hub in the server's Settings, add your sensors there, then come back here.";
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;
        foreach (var sensor in sensors) SensorHost.Items.Add(BuildCard(sensor));
    }

    private Border BuildCard(AlarmSensor sensor)
    {
        var body = new StackPanel();

        var header = new DockPanel();
        var state = new TextBlock
        {
            Text = sensor.StateLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = sensor.State switch { "open" => Palette.Amber, "closed" => Palette.Green, _ => Palette.SecondaryText },
        };
        DockPanel.SetDock(state, Dock.Right);
        header.Children.Add(state);
        header.Children.Add(new TextBlock { Text = sensor.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(header);

        var meta = new List<string> { sensor.ZoneLabel };
        if (sensor.Battery is { } battery) meta.Add($"battery {battery:0}%");
        if (sensor.LinkQuality is { } link) meta.Add($"signal {link:0}");
        body.Children.Add(Caption(string.Join(" · ", meta), sensor.LowBattery ? Palette.Amber : null));

        var when = new List<string> { sensor.LastSeen is { } seen ? $"heard {Ago(seen)}" : "never heard" };
        if (sensor.LastChanged is { } changed) when.Add($"changed {Ago(changed)}");
        if (sensor.DeviceName is { } device) when.Add(sensor.Room is { Length: > 0 } room ? $"{device} · {room}" : device);
        body.Children.Add(Caption(string.Join(" · ", when), null));

        var flags = new List<(string Text, Brush Color)>();
        if (sensor.DeviceMissing) flags.Add(("DEVICE MISSING", Palette.Red));
        else if (sensor.Offline) flags.Add(("OFFLINE", Palette.Red));
        if (sensor.Bypassed) flags.Add(("SKIPPED UNTIL DISARM", Palette.Amber));
        if (sensor.LowBattery) flags.Add(("LOW BATTERY", Palette.Amber));
        if (!sensor.Enabled) flags.Add(("DISABLED", Palette.SecondaryText));
        else if (!sensor.Watched && _status?.Status != "disarmed") flags.Add(($"NOT WATCHED IN {AlarmModes.Label(_status?.Mode).ToUpperInvariant()}", Palette.SecondaryText));
        if (flags.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (text, color) in flags)
                row.Children.Add(new TextBlock { Text = text, Foreground = color, FontSize = 10, Margin = new Thickness(0, 0, 10, 0) });
            body.Children.Add(row);
        }

        if (_model.IsAdmin)
        {
            var admin = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var edit = new Button { Content = "Edit", Padding = new Thickness(9, 4, 9, 4) };
            edit.Click += (_, _) => EditSensor(sensor);
            var remove = new Button { Content = "Remove", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += async (_, _) => await RemoveSensorAsync(sensor);
            admin.Children.Add(edit);
            admin.Children.Add(remove);
            body.Children.Add(admin);
        }

        return new Border
        {
            Width = 300,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackground"),
            BorderBrush = sensor.IsOpen ? Palette.Amber : (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = body,
            Opacity = sensor.Enabled && !sensor.Offline && !sensor.DeviceMissing ? 1 : 0.55,
        };
    }

    private TextBlock Caption(string text, Brush? color) => new()
    {
        Text = text,
        Style = (Style)FindResource("Caption"),
        Foreground = color ?? (Brush)FindResource("SecondaryText"),
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private string Ago(DateTimeOffset at)
    {
        var seconds = Math.Max(0, (int)(ServerNow - at).TotalSeconds);
        return seconds switch
        {
            < 60 => $"{seconds}s ago",
            < 3600 => $"{seconds / 60}m ago",
            < 86400 => $"{seconds / 3600}h ago",
            _ => $"{seconds / 86400}d ago"
        };
    }

    // ---- commands

    private async void OnModeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        await ChangeAsync(mode, force: false, pin: null);
    }

    // Arm or disarm; the server says no with a 4xx and an explanation, and two of those answers
    // get their own dialog: a wrong or missing PIN, and something being open.
    private async Task ChangeAsync(string mode, bool force, string? pin)
    {
        try
        {
            var reply = mode == "disarmed"
                ? await _model.Client.DisarmAlarmAsync(pin)
                : await _model.Client.ArmAlarmAsync(mode, force, pin);
            Message(reply.Note);
            await RefreshAsync();
        }
        catch (SentinelServerException error) when (error.Status == 403 && _status?.PinSet == true)
        {
            var prompt = new AlarmPinWindow(mode == "disarmed" ? "PIN to disarm" : $"PIN to arm {AlarmModes.Label(mode).ToLowerInvariant()}", pin is null ? null : error.Message)
            {
                Owner = Window.GetWindow(this),
            };
            if (prompt.ShowDialog() == true) await ChangeAsync(mode, force, prompt.Pin);
        }
        catch (SentinelServerException error) when (error.Status == 409 && mode != "disarmed" && error.Message.Contains("open", StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(Window.GetWindow(this), error.Message + "\n\nArm anyway?", "Something is open",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) await ChangeAsync(mode, force: true, pin);
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnPanicClicked(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this), "The siren, everyone's phones and the cameras - right now.",
            "Set the alarm off?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _model.Client.AlarmPanicAsync();
            await RefreshAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnTestClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var reply = await _model.Client.AlarmTestAsync();
            Message(reply.Note ?? "Test sent: the siren for five seconds, a notification, and the cameras.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => EditSensor(null);

    private async void EditSensor(AlarmSensor? sensor)
    {
        var window = new AlarmSensorWindow(_model, sensor, _status?.Sensors ?? new List<AlarmSensor>()) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private async Task RemoveSensorAsync(AlarmSensor sensor)
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"{sensor.Name} will no longer be part of the alarm. Its Device Hub device is not touched.",
            $"Remove {sensor.Name}?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _model.Client.DeleteAlarmSensorAsync(sensor.Id);
            await RefreshAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_status?.Config is not { } config) return;
        var window = new AlarmSettingsWindow(_model, config, _status.PinSet == true) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await RefreshAsync();
    }

    private void OnActivityClicked(object sender, RoutedEventArgs e)
    {
        new AlarmActivityWindow(_model) { Owner = Window.GetWindow(this) }.Show();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Message(string? text)
    {
        MessageText.Text = text ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
