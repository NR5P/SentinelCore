using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

public partial class HostRebootWindow : Window
{
    private readonly AppModel _model;
    private readonly DispatcherTimer _countdown;
    private int _secondsRemaining;

    public HostRebootWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();

        // Counted down locally rather than by subtracting the server's scheduled time from this
        // PC's clock - on a machine whose clock has drifted that produces a confidently wrong
        // countdown.
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => Tick();

        Loaded += async (_, _) => await ProbeAsync();
    }

    /// <summary>
    /// A plain GET with no side effects, so it is safe every time this screen appears - and it is
    /// the only trustworthy answer to "will the reboot button work", since the permission it
    /// depends on lives in the operating system and can disappear without this app knowing.
    /// </summary>
    private async Task ProbeAsync()
    {
        try
        {
            var probe = await _model.Client.HostRebootProbeAsync();

            PlatformText.Text = probe.PlatformLabel;
            SummaryText.Text = probe.Summary;
            RunningAsText.Text = string.IsNullOrWhiteSpace(probe.RunningAs) ? "" : $"Running as {probe.RunningAs}";

            SetupPanel.Visibility = probe.NeedsSetup ? Visibility.Visible : Visibility.Collapsed;
            SetupNoteText.Text = probe.SetupNote ?? "";
            SetupScriptBox.Text = probe.SetupScript ?? "";

            // Ready and switched on are separate facts, and the difference is the whole reason this
            // screen exists: one is fixed in this app, the other only at a shell on the server.
            RebootButton.IsEnabled = probe.CanReboot;

            if (probe.SecondsRemaining is { } remaining and > 0)
            {
                StartCountdown(remaining);
            }
            else
            {
                StopCountdown();
            }

            Message(probe.IsUnsupported
                ? "This server can't reboot its own machine."
                : probe.CanReboot ? "" : "The reboot button is unavailable - see above.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            RebootButton.IsEnabled = false;
        }
    }

    private void StartCountdown(int seconds)
    {
        _secondsRemaining = seconds;
        CountdownPanel.Visibility = Visibility.Visible;
        Tick();
        _countdown.Start();
    }

    private void StopCountdown()
    {
        _countdown.Stop();
        CountdownPanel.Visibility = Visibility.Collapsed;
    }

    private void Tick()
    {
        if (_secondsRemaining <= 0)
        {
            CountdownText.Text = "Rebooting now";
            _countdown.Stop();
            return;
        }

        var span = TimeSpan.FromSeconds(_secondsRemaining);
        CountdownText.Text = span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s"
            : $"{span.Seconds}s";
        _secondsRemaining--;
    }

    private async void OnRebootClicked(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            this,
            "Reboot the machine Sentinel Core runs on?\n\nEvery camera stops recording until it is back, and " +
            "nothing will be watching in the meantime.",
            "Reboot the server",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        RebootButton.IsEnabled = false;
        try
        {
            var outcome = await _model.Client.RebootHostAsync();
            Message(outcome.Message);
            if (outcome.SecondsRemaining is { } remaining and > 0) StartCountdown(remaining);
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            RebootButton.IsEnabled = true;
        }
    }

    private async void OnCancelRebootClicked(object sender, RoutedEventArgs e)
    {
        CancelRebootButton.IsEnabled = false;
        try
        {
            var outcome = await _model.Client.CancelHostRebootAsync();
            Message(outcome.Message);
            StopCountdown();
            await ProbeAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            CancelRebootButton.IsEnabled = true;
        }
    }

    private void OnCopySetupClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SetupScriptBox.Text);
            Message("Command copied. Run it in a shell on the server.");
        }
        catch (Exception error)
        {
            Message($"Could not copy: {error.Message}");
        }
    }

    private void OnProbeClicked(object sender, RoutedEventArgs e) => _ = ProbeAsync();

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _countdown.Stop();
        base.OnClosing(e);
    }
}
