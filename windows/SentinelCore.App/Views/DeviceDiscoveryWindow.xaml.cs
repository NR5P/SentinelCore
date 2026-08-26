using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class DeviceDiscoveryWindow : Window
{
    private readonly AppModel _model;
    private readonly DispatcherTimer _poll;
    private Guid? _scanId;
    private bool _addedAny;

    public DeviceDiscoveryWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await PollAsync();
    }

    public sealed record CandidateRow(DeviceCandidate Candidate)
    {
        public string SuggestedName => Candidate.SuggestedName;

        public string Detail =>
            $"{Candidate.Kind} · {(Candidate.CommandTopic.Length > 0 ? Candidate.CommandTopic : Candidate.StateTopic)}" +
            (Candidate.Source is { Length: > 0 } ? $" · via {Candidate.Source}" : "");

        public bool CanAdd => !Candidate.AlreadyAdded;

        public string ActionLabel => Candidate.AlreadyAdded ? "Already added" : "Add";
    }

    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        try
        {
            var started = await _model.Client.StartDeviceScanAsync();
            _scanId = started.ScanId;
            Message($"Listening for {started.ScanSeconds} seconds…");
            _poll.Start();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            ScanButton.IsEnabled = true;
        }
    }

    private async Task PollAsync()
    {
        if (_scanId is not { } scanId) return;
        try
        {
            var snapshot = await _model.Client.DeviceScanStatusAsync(scanId);
            ResultList.ItemsSource = snapshot.Results.Select(result => new CandidateRow(result)).ToList();

            if (!snapshot.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                _poll.Stop();
                _scanId = null;
                ScanButton.IsEnabled = true;
                Message(snapshot.Results.Count == 0
                    ? "Nothing was heard. A device that only publishes on a change stays silent until it changes - " +
                      "press its button or flip its switch and listen again."
                    : $"Heard {snapshot.Results.Count} device{(snapshot.Results.Count == 1 ? "" : "s")}.");
            }
        }
        catch (Exception error)
        {
            _poll.Stop();
            _scanId = null;
            ScanButton.IsEnabled = true;
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CandidateRow row) return;

        // Into the normal device form, pre-filled: the fields of a candidate line up with the ones
        // of a device, so accepting one is a straight copy - but it still deserves a name and a room
        // before it lands in the hub.
        var window = new DeviceEditWindow(_model, null, HubDeviceInput.FromCandidate(row.Candidate))
        {
            Owner = this,
        };
        if (window.ShowDialog() == true)
        {
            _addedAny = true;
            Message($"Added {row.SuggestedName}.");
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = _addedAny;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _poll.Stop();
        base.OnClosing(e);
    }
}
