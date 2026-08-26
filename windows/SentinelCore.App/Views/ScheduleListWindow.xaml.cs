using System.Windows;
using System.Windows.Media;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class ScheduleListWindow : Window
{
    private readonly AppModel _model;
    private ScheduleMetadata _metadata = ScheduleMetadata.Empty;

    public ScheduleListWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await LoadMetadataAsync();
            await ReloadAsync();
        };
    }

    public sealed record ScheduleRow(Schedule Schedule)
    {
        public string Name => Schedule.Name;

        public Brush NameBrush => Schedule.Enabled ? Palette.PrimaryText : Palette.SecondaryText;

        public string StateLabel => Schedule.Enabled ? "On" : "Off";

        public Brush StateBrush => Schedule.Enabled ? Palette.Green : Palette.SecondaryText;

        public string ToggleLabel => Schedule.Enabled ? "Turn off" : "Turn on";

        public string Timing => Describe(Schedule);

        public string Action => DescribeAction(Schedule.Action);

        public string LastRun => Schedule.LastRunUtc is { } last
            ? $"Last run {last.LocalDateTime:d MMM HH:mm} · {Schedule.LastStatus}" +
              (string.IsNullOrWhiteSpace(Schedule.LastDetail) ? "" : $" · {Schedule.LastDetail}")
            : "Has not run yet";

        public Brush LastRunBrush =>
            Schedule.LastStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
                ? Palette.Red
                : Palette.SecondaryText;

        private static string Describe(Schedule schedule)
        {
            var next = schedule.NextDueUtc is { } due
                ? $" · next {due.LocalDateTime:d MMM HH:mm}"
                : "";

            return schedule.Kind switch
            {
                "daily" => $"Every day at {schedule.TimeOfDay}{next}",
                "weekly" => $"{DescribeDays(schedule.DaysOfWeek)} at {schedule.TimeOfDay}{next}",
                "interval" => $"Every {schedule.IntervalSeconds}s between {schedule.WindowStart} and {schedule.WindowEnd}{next}",
                "cron" => $"Cron: {schedule.CronExpression}{next}",
                "once" => schedule.RunAtUtc is { } at ? $"Once, at {at.LocalDateTime:d MMM HH:mm}{next}" : $"Once{next}",
                // A darkness level rather than a time, which is what makes it track the seasons.
                "solar" => $"At {schedule.SolarEvent}" +
                           (schedule.SolarOffsetMinutes == 0 ? "" : $" {schedule.SolarOffsetMinutes:+#;-#} min") +
                           $" (sun at {schedule.SolarElevationDegrees}°){next}",
                _ => schedule.Kind + next,
            };
        }

        private static string DescribeDays(IReadOnlyList<int> days)
        {
            if (days.Count == 0) return "No days chosen";
            string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            return string.Join(", ", days.Where(day => day is >= 0 and <= 6).Select(day => names[day]));
        }

        private static string DescribeAction(ScheduleAction action) => action.Type switch
        {
            "device" => $"Device: {action.DeviceAction}" +
                        (action.DeviceBrightness is { } brightness ? $" at brightness {brightness}" : ""),
            "mqtt" => $"MQTT: publish \"{action.MqttPayload}\" to {action.MqttTopic}",
            "webhook" => $"Webhook: {action.WebhookMethod} {action.WebhookUrl}",
            _ => action.Type,
        };
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            _metadata = await _model.Client.ScheduleMetadataAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            var schedules = await _model.Client.SchedulesAsync();
            ScheduleList.ItemsSource = schedules.Select(schedule => new ScheduleRow(schedule)).ToList();
            Message(schedules.Count == 0 ? "No schedules yet." : "");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var window = new ScheduleEditWindow(_model, _metadata, null) { Owner = this };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScheduleRow row) return;
        var window = new ScheduleEditWindow(_model, _metadata, row.Schedule) { Owner = this };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }

    private async void OnToggleClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScheduleRow row) return;
        try
        {
            await _model.Client.SetScheduleEnabledAsync(row.Schedule.Id, !row.Schedule.Enabled);
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScheduleRow row) return;

        // This really opens the gate or switches the light, which is why the server puts it behind
        // AdminOnly - and why it is worth asking first.
        var confirmed = MessageBox.Show(
            this,
            $"Run \"{row.Name}\" now?\n\n{row.Action}\n\nThis fires the action for real, immediately.",
            "Run schedule",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            var result = await _model.Client.RunScheduleAsync(row.Schedule.Id);
            Message(result.Fired
                ? result.Detail ?? $"\"{row.Name}\" fired."
                : result.Error ?? $"\"{row.Name}\" did not fire.");
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ScheduleRow row) return;

        var confirmed = MessageBox.Show(
            this,
            $"Delete the schedule \"{row.Name}\"?",
            "Delete schedule",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.DeleteScheduleAsync(row.Schedule.Id);
            await ReloadAsync();
            Message($"Deleted {row.Name}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
