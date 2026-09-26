using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

/// <summary>
/// The server's Movies &amp; TV libraries: add, edit, rescan and remove them. Admin only - the
/// Settings button that opens it is in the admin sections.
/// </summary>
/// <remarks>
/// The contract is core/docs/MOVIES.md. While a scan runs the window polls /api/media/status every
/// few seconds, so the banner and each library's count move on their own; it stops as soon as the
/// server says it is idle.
/// </remarks>
public partial class MediaLibrariesWindow : Window
{
    private static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(3);

    private readonly AppModel _model;
    private readonly DispatcherTimer _poll;
    private MediaStatus? _status;
    private bool _available;

    // A scan asked for a moment ago may not have started when the next status is read. A couple
    // of ticks of grace keep the poll going until it shows up, rather than stopping on "idle".
    private int _graceTicks;

    public MediaLibrariesWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = ScanPollInterval };
        _poll.Tick += async (_, _) => await ReloadAsync();
        Closed += (_, _) => _poll.Stop();

        Loaded += async (_, _) => await ReloadAsync();
    }

    public sealed record LibraryRow(MediaLibrary Library, bool Scanning)
    {
        public string Name => Library.Name;
        public string Path => Library.Path;
        public string KindLabel => Library.IsShows ? "TV Shows" : "Movies";
        public Visibility NetworkVisibility => Library.Network ? Visibility.Visible : Visibility.Collapsed;
        public bool CanScan => !Scanning;

        public string CountText => Library.ItemCount switch
        {
            0 => Library.IsShows ? "No shows yet" : "No movies yet",
            1 => Library.IsShows ? "1 show" : "1 movie",
            var count => Library.IsShows ? $"{count:N0} shows" : $"{count:N0} movies",
        };

        public string ScanText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Library.LastScanError)) return $"Last scan failed: {Library.LastScanError}";
                if (Library.LastScanFinishedAt is { } finished) return $"Scanned {RelativeTime.Describe(finished)}";
                return Library.LastScanStartedAt is not null ? "Scanning…" : "Not scanned yet";
            }
        }

        public Brush ScanBrush => string.IsNullOrWhiteSpace(Library.LastScanError) ? Palette.SecondaryText : Palette.Red;
    }

    private async Task ReloadAsync()
    {
        try
        {
            var status = await _model.Client.MediaStatusAsync();
            _status = status;
            if (!status.Enabled)
            {
                // /status answers even when the section is off; everything else would 404 with
                // this same wording, so say it once here instead of failing each button.
                ShowUnavailable("Movies & TV is turned off in Settings.");
                return;
            }

            var libraries = await _model.Client.MediaLibrariesAsync();
            _available = true;
            LibraryList.ItemsSource = libraries
                .OrderBy(library => library.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(library => new LibraryRow(library, status.Scanning))
                .ToList();
            EmptyState.Visibility = libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = "No libraries yet";
            EmptyMessage.Text =
                "Add a library for your movies, and another for TV shows. Choose the folder on the server, or a shared folder on another computer.";

            ApplyScan(status);
        }
        catch (Exception error)
        {
            if (error is SentinelServerException { Status: 404 } notFound)
            {
                ShowUnavailable(notFound.Message);
                return;
            }
            Message(AppModel.UserFacingMessage(error));
            _poll.Stop();
        }
        finally
        {
            AddButton.IsEnabled = _available;
            ScanAllButton.IsEnabled = _available && _status is { Scanning: false } && LibraryList.Items.Count > 0;
        }
    }

    private void ShowUnavailable(string message)
    {
        _available = false;
        _poll.Stop();
        LibraryList.ItemsSource = null;
        ScanBanner.Visibility = Visibility.Collapsed;
        EmptyTitle.Text = "Movies & TV is off";
        EmptyMessage.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        Message("");
    }

    private void ApplyScan(MediaStatus status)
    {
        if (!status.Scanning)
        {
            ScanBanner.Visibility = Visibility.Collapsed;
            if (_graceTicks > 0)
            {
                _graceTicks--;
                if (!_poll.IsEnabled) _poll.Start();
            }
            else
            {
                _poll.Stop();
            }
            return;
        }

        _graceTicks = 0;
        ScanText.Text = ScanWording(status.Scan);
        if (status.Scan is { Total: > 0 } scan)
        {
            ScanProgress.IsIndeterminate = false;
            ScanProgress.Value = Math.Clamp((double)scan.Processed / scan.Total, 0, 1);
        }
        else
        {
            ScanProgress.IsIndeterminate = true;
        }
        ScanBanner.Visibility = Visibility.Visible;
        if (!_poll.IsEnabled) _poll.Start();
    }

    /// <summary>One line on what the scan is doing, in words rather than phase names.</summary>
    internal static string ScanWording(MediaScanState? scan)
    {
        if (scan is null) return "Scanning…";
        var what = scan.Phase switch
        {
            "files" => "looking for video files",
            "metadata" => "fetching artwork and descriptions",
            "probing" => "reading video details",
            _ => "working",
        };
        var library = string.IsNullOrWhiteSpace(scan.LibraryName) ? "" : $" {scan.LibraryName}";
        var progress = scan.Total > 0 ? $" · {scan.Processed:N0} of {scan.Total:N0}" : "";
        return $"Scanning{library}: {what}{progress}";
    }

    // ---------------------------------------------------------------- Actions

    private async Task StartScanAsync(Func<Task> start, string started)
    {
        try
        {
            await start();
            Message(started);
            _graceTicks = 2;
        }
        catch (SentinelServerException error) when (error.Status == 409)
        {
            Message(error.Message);
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        await ReloadAsync();
    }

    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var window = new MediaLibraryEditWindow(_model, null) { Owner = this };
        if (window.ShowDialog() != true) return;
        Message($"Added {window.Saved?.Name}. The server is scanning it now.");
        _graceTicks = 2;
        await ReloadAsync();
    }

    private async void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LibraryRow row) return;
        var window = new MediaLibraryEditWindow(_model, row.Library) { Owner = this };
        if (window.ShowDialog() != true) return;
        Message($"Saved {window.Saved?.Name ?? row.Name}.");
        if (window.Saved is { } saved && saved.Path != row.Library.Path) _graceTicks = 2; // a new path rescans
        await ReloadAsync();
    }

    private async void OnScanClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LibraryRow row) return;
        await StartScanAsync(() => _model.Client.ScanMediaLibraryAsync(row.Library.Id), $"Scanning {row.Name}…");
    }

    private async void OnScanAllClicked(object sender, RoutedEventArgs e) =>
        await StartScanAsync(() => _model.Client.ScanAllMediaLibrariesAsync(), "Scanning every library…");

    private async void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LibraryRow row) return;

        var confirmed = MessageBox.Show(
            this,
            $"Remove the library \"{row.Name}\"?\n\nIts titles and everyone's watched progress are removed from " +
            "Sentinel Core. The video files are not touched.",
            "Remove library",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.DeleteMediaLibraryAsync(row.Library.Id);
            Message($"Removed {row.Name}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        await ReloadAsync();
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
