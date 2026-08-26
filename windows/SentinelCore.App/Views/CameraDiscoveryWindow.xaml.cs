using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class CameraDiscoveryWindow : Window
{
    private readonly AppModel _model;
    private readonly DispatcherTimer _poll;
    private Guid? _scanId;
    private bool _addedAny;

    public CameraDiscoveryWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await PollAsync();

        Loaded += async (_, _) => await LoadInterfacesAsync();
    }

    public sealed class InterfaceRow : INotifyPropertyChanged
    {
        public InterfaceRow(ScanInterface source)
        {
            Source = source;
            // A virtual adapter (Hyper-V, WSL, a VPN) is almost never where the cameras are, and
            // scanning one is a slow way to find nothing - so it starts unticked.
            _selected = !source.LooksVirtual;
        }

        public ScanInterface Source { get; }

        private bool _selected;

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public string Title => $"{Source.Name} — {Source.Address}/{Source.PrefixLength}";

        public string Subtitle => Source.LooksVirtual
            ? $"{Source.Description} · looks like a virtual adapter"
            : Source.Description;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed record ResultRow(ScanResultCamera Result)
    {
        public string SuggestedName => Result.SuggestedName;

        public string Detail =>
            $"{Result.IpAddress}" +
            (Result.Manufacturer is { Length: > 0 } || Result.Model is { Length: > 0 }
                ? $" · {Result.Manufacturer} {Result.Model}".TrimEnd()
                : "") +
            (Result.NeedsCredentials ? " · needs a username and password" : "");

        public bool CanAdd => !Result.AlreadySaved;

        public string ActionLabel => Result.AlreadySaved ? "Already added" : "Add";
    }

    private async Task LoadInterfacesAsync()
    {
        try
        {
            var interfaces = await _model.Client.ScanInterfacesAsync();
            InterfaceList.ItemsSource = interfaces.Select(item => new InterfaceRow(item)).ToList();
            Message(interfaces.Count == 0 ? "The server reported no networks it can scan." : "");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        var selected = (InterfaceList.ItemsSource as IEnumerable<InterfaceRow>)?
            .Where(row => row.Selected)
            .Select(row => row.Source.Address)
            .ToList() ?? new List<string>();

        if (selected.Count == 0)
        {
            Message("Pick at least one network to scan.");
            return;
        }

        ScanButton.IsEnabled = false;
        Message("Scanning… this takes a minute or so.");
        try
        {
            var started = await _model.Client.StartScanAsync(new ScanRequest { InterfaceAddresses = selected });
            _scanId = started.ScanId;
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
            var snapshot = await _model.Client.ScanStatusAsync(scanId);
            // Results are shown as they arrive rather than only at the end - a scan takes long
            // enough that a still list reads as a hung one.
            ResultList.ItemsSource = snapshot.Results.Select(result => new ResultRow(result)).ToList();

            if (!snapshot.Status.Equals("Running", StringComparison.OrdinalIgnoreCase))
            {
                _poll.Stop();
                _scanId = null;
                ScanButton.IsEnabled = true;
                Message(snapshot.Results.Count == 0
                    ? "The scan finished without finding any ONVIF cameras. A camera with ONVIF switched off " +
                      "won't be found this way - add it by hand with its stream address instead."
                    : $"Found {snapshot.Results.Count} camera{(snapshot.Results.Count == 1 ? "" : "s")}.");
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
        if ((sender as FrameworkElement)?.Tag is not ResultRow row) return;

        // Straight into the normal camera form, pre-filled: a discovered camera still needs a
        // stream address confirmed and, more often than not, a username and password.
        var window = new CameraEditWindow(_model, null) { Owner = this };
        window.PrefillFromDiscovery(row.Result);
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
