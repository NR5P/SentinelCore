using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

public partial class SettingsView : UserControl
{
    private readonly AppModel _model;

    public SettingsView(AppModel model)
    {
        _model = model;
        InitializeComponent();

        AddressBox.Text = model.ServerUrl;
        RemoteAddressBox.Text = model.RemoteServerUrl;
        DesktopAlertsToggle.IsChecked = model.DesktopAlertsEnabled;
        LogList.ItemsSource = model.Diagnostics;

        _model.PropertyChanged += OnModelPropertyChanged;

        ApplyAccount();
        ApplySystem();
        _ = RefreshHealthAsync();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppModel.AuthStatus):
                    ApplyAccount();
                    break;
                case nameof(AppModel.SystemStatus):
                    ApplyHealth();
                    break;
                case nameof(AppModel.UsingHttpFallback):
                    HttpFallbackNotice.Visibility = _model.UsingHttpFallback ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(AppModel.LastUpdated):
                case nameof(AppModel.LastError):
                    ApplySystem();
                    break;
                case nameof(AppModel.PushStatus):
                    ApplyPush();
                    break;
                case nameof(AppModel.ServerUrl):
                    AddressBox.Text = _model.ServerUrl;
                    break;
                case nameof(AppModel.RemoteServerUrl):
                    RemoteAddressBox.Text = _model.RemoteServerUrl;
                    break;
            }
        });

    private void ApplyAccount()
    {
        // Camera CRUD, discovery and every server setting are AdminOnly on the server - hidden
        // entirely for a Viewer rather than shown-disabled, so nothing on screen is a dead end.
        AdminSections.Visibility = _model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        TestNotificationButton.Visibility = _model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        RestartBridgeButton.Visibility = _model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        RestartDashboardButton.Visibility = _model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        HostRebootButton.Visibility = _model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        if (_model.AuthStatus is { Enabled: true } status)
        {
            AccountText.Text = status.Username is { Length: > 0 } username
                ? $"Signed in as {username} ({TitleCase(status.Role ?? "Admin")})"
                : "Signed in.";
            AccountButtons.Visibility = Visibility.Visible;
        }
        else
        {
            AccountText.Text = "Dashboard login is not enabled on this server, so everyone on the local " +
                               "network has full access.";
            AccountButtons.Visibility = Visibility.Collapsed;
        }

        ApplyPush();
    }

    private void ApplyPush()
    {
        PushStatusText.Text = _model.PushStatus is { } push
            ? $"Server push: {(push.Enabled ? "enabled" : "disabled")} · " +
              $"Apple {(push.AppleConfigured ? "configured" : "not configured")} · " +
              $"Firebase {(push.FirebaseConfigured ? "configured" : "not configured")} · " +
              $"{push.RegisteredDevices} registered device{(push.RegisteredDevices == 1 ? "" : "s")}"
            : "Server push status is unavailable.";

        TestNotificationButton.IsEnabled = _model.PushStatus is { Enabled: true };
    }

    private void ApplySystem()
    {
        var motionOnly = _model.Activity.Count(item => !item.IsAnalyzed);
        SystemText.Text =
            $"{_model.Activity.Count} events loaded · {_model.Cameras.Count} cameras · {motionOnly} motion only" +
            (_model.LastUpdated is { } updated ? $"\nLast updated {updated.LocalDateTime:HH:mm:ss}" : "");

        SystemErrorText.Text = _model.LastError ?? "";
        SystemErrorText.Visibility = string.IsNullOrEmpty(_model.LastError) ? Visibility.Collapsed : Visibility.Visible;
        HttpFallbackNotice.Visibility = _model.UsingHttpFallback ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshHealthAsync()
    {
        await _model.RefreshSystemStatusAsync();
        ApplyHealth();
    }

    private void ApplyHealth()
    {
        if (_model.SystemStatus is not { } status)
        {
            CpuText.Text = MemoryText.Text = DiskText.Text = "Unavailable";
            QueueText.Text = VisionText.Text = UptimeText.Text = "-";
            return;
        }

        CpuText.Text = $"{Math.Round(status.CpuUsagePercent)}% of {status.ProcessorCount} core" +
                       (status.ProcessorCount == 1 ? "" : "s");
        CpuBar.Value = Math.Min(status.CpuUsagePercent / 100, 1);

        MemoryText.Text = $"{ByteSizeConverter.FormatBytes(status.MemoryUsedBytes)} / " +
                          $"{ByteSizeConverter.FormatBytes(status.MemoryTotalBytes)}";
        MemoryBar.Value = Math.Min(status.MemoryUsedFraction, 1);

        DiskText.Text = $"{ByteSizeConverter.FormatBytes(status.DiskUsedBytes)} / " +
                        $"{ByteSizeConverter.FormatBytes(status.DiskTotalBytes)}";
        DiskBar.Value = Math.Min(status.DiskUsedFraction, 1);

        QueueText.Text = $"{status.QueuedAlerts} pending";
        VisionText.Text = status.VisionBackendHealthy ? "Reachable" : "Unreachable";
        VisionText.Foreground = status.VisionBackendHealthy ? Palette.PrimaryText : Palette.Red;
        UptimeText.Text = FormatUptime(status.UptimeSeconds);
    }

    private static string FormatUptime(double seconds)
    {
        var totalMinutes = (int)(seconds / 60);
        var days = totalMinutes / 1440;
        var hours = totalMinutes % 1440 / 60;
        var minutes = totalMinutes % 60;
        if (days > 0) return $"{days}d {hours}h";
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    private static string TitleCase(string text) =>
        text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];

    // ---------------------------------------------------------------- Server

    private void OnSaveServerClicked(object sender, RoutedEventArgs e)
    {
        var address = ServerAddress.AddSchemeIfMissing(AddressBox.Text);
        if (ServerAddress.Normalize(address) is null)
        {
            MessageBox.Show(this.OwnerWindow(), "Enter a valid server address, such as http://192.168.1.50:3320.",
                "Sentinel Core", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var remote = RemoteAddressBox.Text.Trim();
        // The remote address defaults to https:// rather than http://: it is meant to reach a
        // public internet-facing hostname, and silently falling back to plain HTTP there would send
        // credentials and cookies over the internet in the clear.
        var normalizedRemote = remote.Length == 0 ? "" : ServerAddress.AddSchemeIfMissing(remote, "https");
        if (normalizedRemote.Length > 0 && ServerAddress.Normalize(normalizedRemote) is null)
        {
            MessageBox.Show(this.OwnerWindow(), "The remote address isn't valid. Leave it blank if you don't use one.",
                "Sentinel Core", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _model.ServerUrl = address;
        _model.RemoteServerUrl = normalizedRemote;
        ImageLoader.Clear();
        _ = _model.ConnectAsync(resetNetworkSession: true);
    }

    private void OnForgetServerClicked(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            this.OwnerWindow(),
            "Forget this server and go back to the setup screen? The saved login for it is deleted too.",
            "Forget server",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;
        if (!confirmed) return;

        _ = _model.SignOutAsync();
        _model.ServerUrl = "";
        _model.RemoteServerUrl = "";
        ImageLoader.Clear();
    }

    // ---------------------------------------------------------------- Account

    private void OnChangePasswordClicked(object sender, RoutedEventArgs e) =>
        new ChangePasswordWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnSignOutClicked(object sender, RoutedEventArgs e) => _ = _model.SignOutAsync();

    // ---------------------------------------------------------------- Admin screens

    private void OnManageCamerasClicked(object sender, RoutedEventArgs e) =>
        new CameraAdminWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnKnownFacesClicked(object sender, RoutedEventArgs e) =>
        new KnownFacesWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnKnownVehiclesClicked(object sender, RoutedEventArgs e) =>
        new KnownVehiclesWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnManageAccountsClicked(object sender, RoutedEventArgs e) =>
        new AccountAdminWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnDashboardLoginClicked(object sender, RoutedEventArgs e) =>
        new DashboardLoginWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnServerSettingsClicked(object sender, RoutedEventArgs e) =>
        new ServerSettingsWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnSchedulesClicked(object sender, RoutedEventArgs e) =>
        new ScheduleListWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnPromptProfilesClicked(object sender, RoutedEventArgs e) =>
        new PromptProfilesWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnHomeKitClicked(object sender, RoutedEventArgs e) =>
        new HomeKitWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnBackupClicked(object sender, RoutedEventArgs e) =>
        new BackupWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void OnLicenseClicked(object sender, RoutedEventArgs e) =>
        new LicenseWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    // ---------------------------------------------------------------- Notifications

    private void OnDesktopAlertsToggled(object sender, RoutedEventArgs e) =>
        _model.DesktopAlertsEnabled = DesktopAlertsToggle.IsChecked == true;

    private async void OnTestNotificationClicked(object sender, RoutedEventArgs e)
    {
        TestNotificationButton.IsEnabled = false;
        try
        {
            var report = await _model.Client.TestPushNotificationAsync();
            PushMessage(report.Delivered > 0
                ? $"Test delivered to {report.Delivered} device(s)."
                : $"The server attempted {report.Attempted} delivery(s), but none succeeded.");
        }
        catch (Exception error)
        {
            PushMessage(AppModel.UserFacingMessage(error));
        }
        finally
        {
            TestNotificationButton.IsEnabled = true;
        }
    }

    private void PushMessage(string message)
    {
        PushMessageText.Text = message;
        PushMessageText.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- Health

    private void OnRefreshHealthClicked(object sender, RoutedEventArgs e) => _ = RefreshHealthAsync();

    private void OnRestartBridgeClicked(object sender, RoutedEventArgs e) => _ = RestartAsync("bridge");

    private void OnRestartDashboardClicked(object sender, RoutedEventArgs e) => _ = RestartAsync("dashboard");

    private async Task RestartAsync(string service)
    {
        var confirmed = MessageBox.Show(
            this.OwnerWindow(),
            $"Restart the {service}? Anything it is doing right now stops, and live views reconnect afterwards.",
            "Restart service",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.RestartServerAsync(service);
            // The server answers 202 and restarts only when running under a supervisor; otherwise
            // it refuses with an explanation, which arrives as the exception message below.
            ShowHealthNotice($"The {service} was asked to restart. It should be back in a few seconds.");
        }
        catch (Exception error)
        {
            ShowHealthNotice(AppModel.UserFacingMessage(error));
        }
    }

    private void OnHostRebootClicked(object sender, RoutedEventArgs e) =>
        new HostRebootWindow(_model) { Owner = this.OwnerWindow() }.ShowDialog();

    private void ShowHealthNotice(string message)
    {
        HealthNotice.Text = message;
        HealthNotice.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- Diagnostics

    private void OnToggleLogClicked(object sender, RoutedEventArgs e)
    {
        var showing = LogPanel.Visibility == Visibility.Visible;
        LogPanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        ToggleLogButton.Content = showing ? "Show connection log" : "Hide connection log";
    }

    private void OnCopyLogClicked(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var entry in _model.Diagnostics)
        {
            text.AppendLine($"{entry.Timestamp.LocalDateTime:HH:mm:ss} {entry.LevelLabel,-6} {entry.Message}");
        }

        try
        {
            Clipboard.SetText(text.ToString());
            ShowHealthNotice("Connection log copied to the clipboard.");
        }
        catch (Exception error)
        {
            // The clipboard is genuinely allowed to refuse - another process can hold it open.
            ShowHealthNotice($"Could not copy the log: {error.Message}");
        }
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e) => _model.ClearDiagnostics();
}

internal static class VisualHelpers
{
    /// <summary>The window a control sits in, for parenting dialogs and message boxes.</summary>
    public static Window OwnerWindow(this DependencyObject element) =>
        Window.GetWindow(element) ?? Application.Current.MainWindow;
}
