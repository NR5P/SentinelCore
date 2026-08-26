using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// One event, opened from the merged Events list.
/// </summary>
/// <remarks>
/// What it shows depends on what exists for that moment rather than on which screen you came from -
/// the clip if one was recorded, the AI's read of the frame if anything analysed it, and the motion
/// facts when a trigger is behind it. A port of the Apple client's ActivityDetailView, including
/// the parts that are easy to leave out: the package-theft reason, the cameras that also recorded
/// the moment, and how much of the frame actually moved.
/// </remarks>
public partial class ActivityDetailView : UserControl
{
    private readonly AppModel _model;
    private readonly ActivityItem _item;

    public ActivityDetailView(AppModel model, ActivityItem item)
    {
        _model = model;
        _item = item;
        InitializeComponent();

        Watermark.IsLicensed = model.IsLicensed;
        SnapshotImage.SourcePath = item.ImageUrl;

        ApplyAnalysis();
        ApplyFacts();
        ApplyMediaButtons();

        // A clip is the more informative thing when one exists, so it opens on it - matching the
        // Apple client, which starts playing as soon as the screen appears.
        if (CanPlayClip) Loaded += async (_, _) => await ShowClipAsync();

        Unloaded += (_, _) => ClipPlayer.Stop();
    }

    private bool CanPlayClip => _item.ClipUrl is { Length: > 0 } && _item.Clip is not ActivityClipState.Missing;

    private void ApplyAnalysis()
    {
        if (!_item.IsAnalyzed)
        {
            AnalysisCard.Visibility = Visibility.Collapsed;
            return;
        }

        TypeText.Text = (_item.EventType ?? "unknown").Replace('_', ' ').ToUpperInvariant();
        NotifiedChip.Visibility = _item.Notified ? Visibility.Visible : Visibility.Collapsed;
        ConfidenceText.Text = _item.Confidence is { } confidence
            ? $"{Math.Round(confidence * 100)}%"
            : "";

        SummaryText.Text = string.IsNullOrWhiteSpace(_item.Summary) ? "No description" : _item.Summary!;

        var reason = _item.NotificationReason;
        NotifyReasonRow.Visibility = _item.Notified ? Visibility.Visible : Visibility.Collapsed;
        NotifyReasonText.Text = string.IsNullOrWhiteSpace(reason) ? "Notification sent" : reason!;

        PackageRow.Visibility = string.IsNullOrWhiteSpace(_item.PackageTheftReason)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PackageText.Text = _item.PackageTheftReason ?? "";

        LinkedRow.Visibility = string.IsNullOrWhiteSpace(_item.LinkedCameras)
            ? Visibility.Collapsed
            : Visibility.Visible;
        LinkedText.Text = $"Also recorded: {_item.LinkedCameras}";
    }

    private void ApplyFacts()
    {
        Fact("Camera", _item.Camera);
        if (_item.ZoneName is { Length: > 0 } zone) Fact("Zone", zone);
        Fact("Time", _item.OccurredAt.LocalDateTime.ToString("dddd d MMMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture));
        if (_item.TriggerAreaFraction is { } area) Fact("Motion area", $"{area * 100:0.0}% of the frame");
        Fact("Media", _item.HasClip ? "Video clip" : "Snapshot");

        // Only worth stating when it isn't the ordinary case. "Confirmed" on every event would be
        // noise; the other two explain why a row is only visible with Show hidden on.
        if (_item.Confirmation is { } confirmation && confirmation != "confirmed")
        {
            Fact("AI verdict", confirmation == "rejected" ? "Nothing found in frame" : "Awaiting verdict");
        }

        if (!_item.IsAnalyzed)
        {
            FactsPanel.Children.Add(new TextBlock
            {
                Text = "Nothing analysed this frame - either this camera doesn't send to AI analysis, " +
                       "or the forward was refused.",
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void Fact(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("Caption"),
        };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var content = new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("BodyText"),
        };
        Grid.SetColumn(content, 1);
        row.Children.Add(content);

        FactsPanel.Children.Add(row);
    }

    private void ApplyMediaButtons()
    {
        // The clip button says what there is to say about the clip, rather than being absent and
        // leaving "was one recorded?" unanswered.
        var (enabled, label) = _item.Clip switch
        {
            ActivityClipState.Ready => (true, "Clip"),
            ActivityClipState.Recording => (false, "Clip (recording…)"),
            ActivityClipState.Missing => (false, "Clip (missing)"),
            ActivityClipState.Off => (false, "Clip (not recorded)"),
            _ => (_item.HasClip, _item.HasClip ? "Clip" : "No clip"),
        };
        ClipButton.IsEnabled = enabled && _item.ClipUrl is not null;
        ClipButton.Content = label;
        SnapshotButton.IsEnabled = false;
    }

    private void OnShowSnapshotClicked(object sender, RoutedEventArgs e)
    {
        ClipPlayer.Stop();
        ClipPlayer.Visibility = Visibility.Collapsed;
        SnapshotImage.Visibility = Visibility.Visible;
        MediaNotice.Visibility = Visibility.Collapsed;
        SnapshotButton.IsEnabled = false;
        ApplyClipButtonEnabled();
    }

    private void ApplyClipButtonEnabled() => ClipButton.IsEnabled = CanPlayClip;

    private async void OnShowClipClicked(object sender, RoutedEventArgs e) => await ShowClipAsync();

    private async Task ShowClipAsync()
    {
        if (_item.ClipUrl is not { } clip) return;

        SnapshotImage.Visibility = Visibility.Collapsed;
        ClipPlayer.Visibility = Visibility.Visible;
        MediaNotice.Visibility = Visibility.Collapsed;
        ClipButton.IsEnabled = false;
        SnapshotButton.IsEnabled = true;

        await ClipPlayer.PlayClipAsync(_model, clip, autoplay: true);
    }

    /// <summary>
    /// Saving goes through the app's own session rather than handing the URL to a browser: against
    /// a server that requires a login, a browser has no cookie and would save a 401 page named like
    /// a JPEG.
    /// </summary>
    private async void OnSaveSnapshotClicked(object sender, RoutedEventArgs e)
    {
        if (_item.ImageUrl is not { } path)
        {
            Notice("This event has no snapshot to save.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "JPEG image|*.jpg",
            FileName = $"{Sanitize(_item.Camera)}-{_item.OccurredAt.LocalDateTime:yyyy-MM-dd-HHmmss}.jpg",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            if (_model.Client.Url(path) is not { } url) throw new InvalidOperationException("No server address.");
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            {
                CookieContainer = _model.Client.Cookies,
                UseCookies = true,
            });
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            Notice($"Saved to {dialog.FileName}");
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '-');
        return name;
    }

    private void Notice(string message) => NoticeText.Text = message;
}
