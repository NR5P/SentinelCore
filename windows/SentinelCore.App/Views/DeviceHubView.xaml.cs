using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The lights, gates, locks and sensors the server is tracking over MQTT, grouped by room.
/// </summary>
public partial class DeviceHubView : UserControl
{
    // The light poll while this section is on screen - no topics or payload strings, just what each
    // device is currently doing.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

    private readonly AppModel _model;
    private readonly DispatcherTimer _poll;
    private bool _isActive;

    public DeviceHubView(AppModel model)
    {
        _model = model;
        InitializeComponent();

        AddButton.Visibility = model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        DiscoverButton.Visibility = model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        _model.Devices.CollectionChanged += OnDevicesChanged;

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += async (_, _) => await PollStateAsync();

        IsVisibleChanged += (_, _) => SetActive(IsVisible);

        Rebuild();
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            _poll.Start();
            _ = PollStateAsync();
        }
        else
        {
            _poll.Stop();
        }
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.Invoke(Rebuild);

    /// <summary>
    /// Redraws the cards, unless someone is in the middle of using one.
    /// </summary>
    /// <remarks>
    /// Rebuilding replaces every control, and the brightness slider commits its value on mouse-up
    /// from a handler attached to that specific slider instance. The four-second state poll calls
    /// this, so any drag lasting longer than the poll interval had the slider pulled out from under
    /// it: capture was lost, the handler never ran, no command was sent, and the thumb sprang back
    /// to where it started. Deferring the redraw until the pointer is released costs at most one
    /// poll's worth of staleness on one card.
    /// </remarks>
    private void RebuildUnlessBusy()
    {
        if (Mouse.Captured is DependencyObject captured && IsInteractive(captured)) return;
        Rebuild();
    }

    private async Task PollStateAsync()
    {
        if (!_isActive) return;
        try
        {
            var snapshot = await _model.Client.DeviceStatesAsync();
            foreach (var state in snapshot.Devices)
            {
                if (_model.Devices.FirstOrDefault(device => device.Id == state.Id) is not { } device) continue;
                device.Power = state.Power;
                device.Brightness = state.Brightness;
                device.Available = state.Available;
                if (state.Measurements is not null) device.Measurements = state.Measurements;
            }

            BrokerDot.Fill = snapshot.Connected ? Palette.Green : Palette.Red;
            BrokerText.Text = snapshot.Connected
                ? $"{_model.Devices.Count} device{(_model.Devices.Count == 1 ? "" : "s")} · broker connected"
                : "Not connected to the MQTT broker - nothing here can be switched until it is.";
            RebuildUnlessBusy();
        }
        catch (Exception error)
        {
            BrokerDot.Fill = Palette.Amber;
            BrokerText.Text = AppModel.UserFacingMessage(error);
        }
    }

    /// <summary>
    /// Devices as the server ordered them, grouped by room. A hub of any size is unusable as one
    /// flat list - "which of these four switches is the kitchen one" is exactly the question the
    /// room is there to answer.
    /// </summary>
    private List<IGrouping<string, HubDevice>> Grouped() =>
        _model.Devices
            .OrderBy(device => device.SortOrder ?? int.MaxValue)
            .GroupBy(device => string.IsNullOrWhiteSpace(device.Room) ? "Unassigned" : device.Room)
            .OrderBy(group => group.Key == "Unassigned" ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Rebuild()
    {
        DeviceHost.Items.Clear();
        EmptyState.Visibility = _model.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var room in Grouped())
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            section.Children.Add(new TextBlock
            {
                Text = room.Key.ToUpperInvariant(),
                Style = (Style)FindResource("GroupHeading"),
                Margin = new Thickness(2, 6, 0, 8),
            });

            var cards = new WrapPanel();
            foreach (var device in room) cards.Children.Add(BuildCard(device));
            section.Children.Add(cards);

            DeviceHost.Items.Add(section);
        }
    }

    private Border BuildCard(HubDevice device)
    {
        var body = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = device.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        titles.Children.Add(new TextBlock
        {
            Text = TitleCase(device.Kind),
            Foreground = Palette.SecondaryText,
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(titles, 0);
        header.Children.Add(titles);

        var state = new TextBlock
        {
            Text = StateLabel(device),
            Foreground = StateBrush(device),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(state, 1);
        header.Children.Add(state);
        body.Children.Add(header);

        // Controls, which differ by what the device actually speaks.
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

        if (device.IsMomentary)
        {
            // A pulse device: one press, no on/off state afterwards.
            controls.Children.Add(CommandButton(device, "Press", "on"));
        }
        else if (device.IsCoverLike)
        {
            controls.Children.Add(CommandButton(device, "Open", "on"));
            if (device.HasStop) controls.Children.Add(CommandButton(device, "Stop", "stop"));
            controls.Children.Add(CommandButton(device, "Close", "off"));
        }
        else if (device.IsLock)
        {
            controls.Children.Add(CommandButton(device, "Unlock", "on"));
            controls.Children.Add(CommandButton(device, "Lock", "off"));
        }
        else if (device.Commandable)
        {
            controls.Children.Add(CommandButton(device, "On", "on"));
            controls.Children.Add(CommandButton(device, "Off", "off"));
        }

        if (controls.Children.Count > 0) body.Children.Add(controls);

        if (device.SupportsBrightness && device.Commandable)
        {
            // Percent, not the raw payload scale. BrightnessScale is what the server multiplies by
            // on its way out to MQTT; the API speaks 0-100 and so does every other client.
            var slider = new Slider
            {
                Minimum = 1,
                Maximum = 100,
                Value = Math.Clamp(device.Brightness ?? 100, 1, 100),
                Margin = new Thickness(0, 6, 0, 0),
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
            };

            var readout = new TextBlock
            {
                Text = $"Brightness · {(int)slider.Value}%",
                Foreground = Palette.SecondaryText,
                FontSize = 11.5,
                Margin = new Thickness(0, 10, 0, 0),
            };
            slider.ValueChanged += (_, _) => readout.Text = $"Brightness · {(int)slider.Value}%";

            // Committed on release rather than continuously: an MQTT publish per pixel of a drag
            // floods the broker and makes the lamp visibly stutter.
            slider.AddHandler(
                Mouse.PreviewMouseUpEvent,
                new MouseButtonEventHandler((_, _) => _ = SendAsync(device, null, (int)slider.Value)),
                true);

            body.Children.Add(readout);
            body.Children.Add(slider);
        }

        // Readings, in the order the server gave them - sorted by key server-side, so rendering in
        // that order keeps the rows from reshuffling between polls.
        if (device.Measurements is { Count: > 0 } measurements)
        {
            var readings = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var measurement in measurements)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var key = new TextBlock
                {
                    Text = measurement.Key,
                    Foreground = Palette.SecondaryText,
                    FontSize = 11.5,
                };
                Grid.SetColumn(key, 0);
                row.Children.Add(key);

                // No units and no friendly name: the server hands over the raw number under the
                // device's own key, because no two vendors agree on units well enough for guessing
                // to beat saying nothing.
                var value = new TextBlock
                {
                    Text = measurement.FormattedValue,
                    Foreground = Palette.Cyan,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                };
                Grid.SetColumn(value, 1);
                row.Children.Add(value);

                readings.Children.Add(row);
            }
            body.Children.Add(readings);
        }

        if (_model.IsAdmin)
        {
            var admin = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var edit = new Button { Content = "Edit", Padding = new Thickness(9, 4, 9, 4) };
            edit.Click += (_, _) => EditDevice(device);
            admin.Children.Add(edit);
            body.Children.Add(admin);
        }

        var card = new Border
        {
            Width = 300,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackground"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = body,
            Opacity = device.Available || device.IsReporting ? 1 : 0.55,
            Tag = device.Id,
            AllowDrop = _model.IsAdmin && _model.Devices.Count > 1,
        };

        if (card.AllowDrop) AttachReorder(card, device);
        return card;
    }

    /// <summary>
    /// Drag one card onto another to rearrange the hub, the same way the live grid rearranges
    /// cameras. The order is per-server, so it is the arrangement everyone sees.
    /// </summary>
    private void AttachReorder(Border card, HubDevice device)
    {
        card.MouseMove += (_, args) =>
        {
            if (args.LeftButton != MouseButtonState.Pressed) return;
            // A drag started on a slider or a button is that control's, not a reorder.
            if (args.OriginalSource is DependencyObject source && IsInteractive(source)) return;
            DragDrop.DoDragDrop(card, device.Id.ToString(), DragDropEffects.Move);
        };
        card.DragOver += (_, args) =>
        {
            args.Effects = DragDropEffects.Move;
            card.BorderBrush = Palette.Green;
            args.Handled = true;
        };
        card.DragLeave += (_, _) => card.BorderBrush = (Brush)FindResource("CardBorder");
        card.Drop += (_, args) =>
        {
            card.BorderBrush = (Brush)FindResource("CardBorder");
            if (args.Data.GetData(typeof(string)) is not string dropped) return;
            if (!Guid.TryParse(dropped, out var movingId)) return;
            MoveDevice(movingId, device.Id);
        };
    }

    private static bool IsInteractive(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Slider or ButtonBase) return true;
        }
        return false;
    }

    /// <summary>
    /// The same optimistic pattern as the live grid: the list moves now and the save happens in the
    /// background, because a round trip is no reason to watch a card snap back before it moves.
    /// </summary>
    private void MoveDevice(Guid movingId, Guid targetId)
    {
        var ordered = _model.Devices.OrderBy(device => device.SortOrder ?? int.MaxValue).ToList();
        var from = ordered.FindIndex(device => device.Id == movingId);
        var to = ordered.FindIndex(device => device.Id == targetId);
        if (from < 0 || to < 0 || from == to) return;

        var moving = ordered[from];
        ordered.RemoveAt(from);
        ordered.Insert(to, moving);

        for (var index = 0; index < ordered.Count; index++) ordered[index].SortOrder = index;

        _model.Devices.Clear();
        foreach (var device in ordered) _model.Devices.Add(device);

        var ids = ordered.Select(device => device.Id).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await _model.Client.SaveDeviceOrderAsync(ids);
            }
            catch (Exception error)
            {
                _model.Log($"Could not save the device order: {error.Message}", DiagnosticLevel.Error);
            }
        });
    }

    private Button CommandButton(HubDevice device, string label, string action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), MinWidth = 68 };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            await SendAsync(device, action, null);
            button.IsEnabled = true;
        };
        return button;
    }

    private async Task SendAsync(HubDevice device, string? action, int? brightness)
    {
        try
        {
            var result = await _model.Client.SendDeviceCommandAsync(device.Id, action, brightness);
            device.Power = result.Power;
            if (result.Brightness is not null) device.Brightness = result.Brightness;

            // Sent and acknowledged are different facts, and the difference is worth saying: an
            // optimistic device reports "sent" and nothing more, and a device that has gone away
            // looks exactly the same until its state never comes back. A momentary device has no
            // echoed state to wait for, so unconfirmed is its normal successful outcome.
            Notice(result.Acknowledged || device.IsMomentary
                ? $"{device.Name}: {StateLabel(device).ToLowerInvariant()}"
                : $"{device.Name} did not confirm the change.");
            Rebuild();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    private static string StateLabel(HubDevice device)
    {
        if (device.IsReporting) return "Reporting";
        if (!device.Available) return "Unavailable";
        if (device.IsMomentary) return "Ready";
        if (device.Power == "unknown") return "Unknown";
        if (device.IsCoverLike) return device.IsOn ? "Open" : "Closed";
        if (device.IsLock) return device.IsOn ? "Unlocked" : "Locked";
        if (device.IsOn && device.SupportsBrightness && device.Brightness is { } level) return $"On · {level}%";
        return device.IsOn ? "On" : "Off";
    }

    private static Brush StateBrush(HubDevice device)
    {
        if (!device.Available && !device.IsReporting) return Palette.Red;
        if (device.Power == "unknown" && !device.IsReporting && !device.IsMomentary) return Palette.SecondaryText;
        return device.IsOn || device.IsReporting ? Palette.Green : Palette.SecondaryText;
    }

    private static string TitleCase(string text) =>
        text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];

    private void Notice(string message)
    {
        NoticeText.Text = message;
        NoticeText.Visibility = Visibility.Visible;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        try
        {
            var devices = await _model.Client.DevicesAsync();
            _model.Devices.Clear();
            foreach (var device in devices) _model.Devices.Add(device);
            await PollStateAsync();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var window = new DeviceEditWindow(_model, null) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }

    private void EditDevice(HubDevice device)
    {
        var window = new DeviceEditWindow(_model, device) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }

    private void OnDiscoverClicked(object sender, RoutedEventArgs e)
    {
        var window = new DeviceDiscoveryWindow(_model) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }
}
