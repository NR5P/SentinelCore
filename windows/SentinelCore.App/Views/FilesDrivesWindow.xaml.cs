using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The library's backup drives, the nightly pass and the duplicate check. An admin runs and edits
/// everything from here; a Viewer sees the same list, read-only, so "when was this last backed up"
/// is a question anyone in the house can answer.
/// </summary>
public partial class FilesDrivesWindow : Window
{
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromSeconds(2);

    private readonly AppModel _model;
    private readonly DispatcherTimer _poll;
    private FamilyFilesStatus? _status;
    private bool _admin;

    public FilesDrivesWindow(AppModel model, FamilyFilesStatus? status = null)
    {
        _model = model;
        InitializeComponent();

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = JobPollInterval };
        _poll.Tick += async (_, _) => await ReloadAsync();
        Closed += (_, _) => _poll.Stop();

        if (status is not null) Apply(status);
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            Apply(await _model.Client.FamilyFilesStatusAsync());
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            _poll.Stop();
        }
    }

    private void Apply(FamilyFilesStatus status)
    {
        _status = status;
        _admin = status.IsAdmin;

        var adminOnly = _admin ? Visibility.Visible : Visibility.Collapsed;
        DriveActions.Visibility = adminOnly;
        RunNightlyButton.Visibility = adminOnly;
        DuplicateActions.Visibility = adminOnly;

        var busy = status.Job is not null;
        SyncAllButton.IsEnabled = !busy && status.Drives.Any(drive => drive.Enabled);
        RunNightlyButton.IsEnabled = !busy;
        CheckButton.IsEnabled = !busy;
        CheckRemoveButton.IsEnabled = !busy;

        DriveHost.Children.Clear();
        foreach (var drive in status.Drives) DriveHost.Children.Add(DriveRow(drive, busy));
        NoDrivesText.Visibility = status.Drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        NightlyText.Text = NightlyWording(status.Nightly);
        DuplicatesText.Text = DuplicatesWording(status.Duplicates);

        ApplyJob(status.Job);
    }

    private void ApplyJob(FamilyFilesJob? job)
    {
        if (job is null)
        {
            JobBanner.Visibility = Visibility.Collapsed;
            _poll.Stop();
            return;
        }

        JobText.Text = string.IsNullOrWhiteSpace(job.Message) ? $"{job.Title}…" : $"{job.Title} · {job.Message}";
        if (job.Total > 0)
        {
            JobProgress.IsIndeterminate = false;
            JobProgress.Value = Math.Clamp((double)job.Done / job.Total, 0, 1);
        }
        else
        {
            JobProgress.IsIndeterminate = true;
        }
        JobBanner.Visibility = Visibility.Visible;
        if (!_poll.IsEnabled) _poll.Start();
    }

    private Border DriveRow(FamilyDrive drive, bool busy)
    {
        var body = new StackPanel();

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = drive.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!drive.Enabled)
        {
            title.Children.Add(new Border
            {
                Style = (Style)FindResource("Chip"),
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(7, 2, 7, 2),
                Child = new TextBlock { Text = "Off", FontSize = 11, Foreground = Palette.SecondaryText },
            });
        }
        body.Children.Add(title);

        body.Children.Add(new TextBlock
        {
            Text = $"{(drive.IsSftp ? "SFTP" : "Folder")} · {drive.Location}",
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        status.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = drive.LastResult switch
            {
                "ok" => Palette.Green,
                "failed" => Palette.Red,
                _ => Palette.SecondaryText,
            },
        });
        status.Children.Add(new TextBlock
        {
            Text = StatusWording(drive),
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(8, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(status);

        if (drive.LastResult == "failed" && !string.IsNullOrWhiteSpace(drive.LastMessage))
        {
            body.Children.Add(new TextBlock
            {
                Text = drive.LastMessage,
                Foreground = Palette.Red,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 4, 0, 0),
            });
        }

        if (_admin)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var sync = new Button { Content = "Back up now", IsEnabled = !busy, Padding = new Thickness(9, 4, 9, 4) };
            sync.Click += async (_, _) => await StartAsync(() => _model.Client.FamilyFilesSyncDriveAsync(drive.Id));
            actions.Children.Add(sync);

            var restore = new Button
            {
                Content = "Bring files back",
                IsEnabled = !busy,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(9, 4, 9, 4),
                ToolTip = "Copies anything on the drive that the library no longer has back into the library.",
            };
            restore.Click += async (_, _) =>
            {
                var confirmed = MessageBox.Show(
                    this,
                    $"Bring files back from {drive.Name}?\n\nAnything on the drive that the library doesn't have is copied " +
                    "back into the library. Nothing in the library is changed or removed.",
                    "Bring files back",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) == MessageBoxResult.OK;
                if (confirmed) await StartAsync(() => _model.Client.FamilyFilesRestoreDriveAsync(drive.Id));
            };
            actions.Children.Add(restore);

            var edit = new Button { Content = "Edit", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(9, 4, 9, 4) };
            edit.Click += async (_, _) =>
            {
                var window = new FilesDriveEditWindow(_model, drive) { Owner = this };
                if (window.ShowDialog() == true) await ReloadAsync();
            };
            actions.Children.Add(edit);
            body.Children.Add(actions);
        }

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("CardHoverBackground"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = body,
            Opacity = drive.Enabled ? 1 : 0.65,
        };
    }

    /// <summary>One line on how the last backup went, in words rather than fields.</summary>
    internal static string StatusWording(FamilyDrive drive)
    {
        if (drive.LastAttemptUtc is null && drive.LastSuccessUtc is null) return "Never backed up yet";

        var parts = new List<string>();
        if (drive.LastResult == "failed")
        {
            parts.Add(drive.LastAttemptUtc is { } attempt ? $"Failed {RelativeTime.Describe(attempt)}" : "Failed");
            if (drive.LastSuccessUtc is { } lastGood) parts.Add($"last good backup {RelativeTime.Describe(lastGood)}");
        }
        else if (drive.LastSuccessUtc is { } success)
        {
            parts.Add($"Backed up {RelativeTime.Describe(success)}");
        }
        else
        {
            parts.Add("Not backed up yet");
        }

        if (drive.LastFileCount is { } files) parts.Add($"{files:N0} file{(files == 1 ? "" : "s")}");
        if (drive.LastBytes is { } bytes) parts.Add(ByteSizeConverter.FormatBytes(bytes));
        if (drive.LastCopied is { } copied && copied > 0) parts.Add($"{copied:N0} copied");
        if (drive.LastRemoved is { } removed && removed > 0) parts.Add($"{removed:N0} removed");
        if (drive.LastFreeBytes is { } free) parts.Add($"{ByteSizeConverter.FormatBytes(free)} free");
        return string.Join(" · ", parts);
    }

    private static string NightlyWording(FamilyNightlyStatus? nightly)
    {
        if (nightly is null) return "Not available.";

        var lines = new List<string>
        {
            $"Runs every night at {nightly.Hour:00}:{nightly.Minute:00}" +
            (nightly.NextRunUtc is { } next ? $" - next {next.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture)}." : "."),
        };
        lines.Add(nightly.LastRunUtc is { } last
            ? $"Last run {RelativeTime.Describe(last)}: " + nightly.LastResult switch
            {
                "ok" => "everything went fine.",
                "incomplete" => "did not finish - " + nightly.LastMessage,
                "failed" => "failed - " + nightly.LastMessage,
                _ => nightly.LastMessage,
            }
            : "It has not run yet.");
        lines.Add(nightly.RemoveDuplicates
            ? "Duplicate copies are moved to the trash on each pass."
            : "Duplicates are only reported, not removed.");
        lines.Add($"The trash keeps files for {nightly.TrashKeepDays} day{(nightly.TrashKeepDays == 1 ? "" : "s")}.");
        return string.Join("\n", lines);
    }

    private static string DuplicatesWording(FamilyDuplicateReport? report)
    {
        if (report is null) return "Not checked yet.";
        if (report.Groups == 0)
        {
            return $"Checked {RelativeTime.Describe(report.RanUtc)}: {report.FilesChecked:N0} files, no duplicates.";
        }
        return $"Checked {RelativeTime.Describe(report.RanUtc)}: {report.Groups:N0} file{(report.Groups == 1 ? "" : "s")} with " +
               $"{report.Copies:N0} extra cop{(report.Copies == 1 ? "y" : "ies")} taking {ByteSizeConverter.FormatBytes(report.Bytes)}. " +
               (report.Removed ? "The copies were moved to the trash." : "Nothing was removed.");
    }

    // ---------------------------------------------------------------- Actions

    private async Task StartAsync(Func<Task<FamilyJobStart>> start)
    {
        try
        {
            var result = await start();
            Message(result.Detail);
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnAddDriveClicked(object sender, RoutedEventArgs e)
    {
        var window = new FilesDriveEditWindow(_model, null) { Owner = this };
        if (window.ShowDialog() == true) await ReloadAsync();
    }

    private async void OnSyncAllClicked(object sender, RoutedEventArgs e) =>
        await StartAsync(() => _model.Client.FamilyFilesSyncAllAsync());

    private async void OnRunNightlyClicked(object sender, RoutedEventArgs e) =>
        await StartAsync(() => _model.Client.FamilyFilesRunNightlyAsync());

    private async void OnCheckDuplicatesClicked(object sender, RoutedEventArgs e) =>
        await StartAsync(() => _model.Client.FamilyFilesCheckDuplicatesAsync(remove: false));

    private async void OnCheckAndRemoveClicked(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            this,
            "Check for duplicates and move every extra copy to the trash?\n\nThe oldest copy of each file stays " +
            "in the library. The copies can be put back from the Trash until it is emptied.",
            "Remove duplicates",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (confirmed) await StartAsync(() => _model.Client.FamilyFilesCheckDuplicatesAsync(remove: true));
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
