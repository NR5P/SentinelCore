using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// Movie downloads in progress and just finished: the VPN's state, each active download's
/// progress, speed and time left, and the recent ones with Open (into the library), Retry and
/// Remove. Admins also reach the torrent sites from here.
/// </summary>
/// <remarks>
/// The contract is core/docs/MOVIE_DOWNLOADS.md; the layout is the web dashboard's Downloads pane.
/// Pushed on top of Movies, so Open lands on the title's detail page and Back returns here. While
/// something is downloading and this screen is on show, it polls every 2.5 seconds. A poll that
/// changes only numbers patches them in place, so a button under the pointer is never swapped for
/// a new one between the press and the release.
/// </remarks>
public partial class MediaDownloadsView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

    private sealed record RowParts(ProgressBar? Bar, TextBlock Facts, TextBlock Note);

    private readonly AppModel _model;
    private readonly INavigator _navigator;
    private readonly DispatcherTimer _poll;
    private MediaDownloadStatus _status;
    private bool _statusChecked;

    private int _generation;
    private string? _shape;
    private readonly Dictionary<string, RowParts> _rows = new();

    // Each download's state at the last poll, to notice the moment one lands or fails.
    private readonly Dictionary<string, string> _states = new();

    public MediaDownloadsView(AppModel model, INavigator? navigator = null, MediaDownloadStatus? status = null)
    {
        _model = model;
        _navigator = navigator ?? NullNavigator.Instance;
        _status = status ?? MediaDownloadStatus.Off;
        InitializeComponent();
        SitesButton.Visibility = _status.ShowsSitesButton ? Visibility.Visible : Visibility.Collapsed;

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += async (_, _) => await ReloadAsync(quiet: false);

        Loaded += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _ = ReloadAsync(quiet: true);
        };

        // Back from a title's page: pick up where things are now. Away: stop asking.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _poll.Stop();
            else if (_loadedOnce) _ = ReloadAsync(quiet: true);
        };
        Unloaded += (_, _) => _poll.Stop();
    }

    private bool _loadedOnce;

    // ---------------------------------------------------------------- Loading

    private async Task ReloadAsync(bool quiet)
    {
        _poll.Stop();
        var generation = ++_generation;

        if (!_statusChecked)
        {
            _statusChecked = true;
            try
            {
                _status = await _model.Client.MediaDownloadStatusAsync();
            }
            catch (Exception)
            {
                // Only the Sites button depends on it; the list below says what is wrong.
            }
            SitesButton.Visibility = _status.ShowsSitesButton ? Visibility.Visible : Visibility.Collapsed;
        }

        try
        {
            var list = await _model.Client.MediaDownloadsAsync();
            if (generation != _generation) return;
            NoteChanges(list, quiet);
            Render(list);
            if (list.Active.Count > 0 && IsVisible) _poll.Start();
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            StatusText.Text = "The downloads could not be loaded.";
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    // "Bluey S02E05 is in the library" the moment it lands; the reason the moment one fails.
    private void NoteChanges(MediaDownloadList list, bool quiet)
    {
        foreach (var download in list.Active.Concat(list.Recent))
        {
            if (download.Id is not { } id || download.State is not { } state) continue;
            if (!quiet && _states.TryGetValue(id, out var before) && before != state)
            {
                var name = string.IsNullOrWhiteSpace(download.Subtitle) ? download.Title : $"{download.Title} {download.Subtitle}";
                if (state == "done") Notice($"{name} is in the library.");
                else if (state == "failed") Notice($"{download.Title}: {download.Error ?? "the download failed."}", isError: true);
            }
            _states[id] = state;
        }
    }

    // What decides the layout: which downloads, in which state, with which buttons, and the VPN
    // line. While it stays the same a poll only patches the numbers.
    private static string Shape(MediaDownloadList list)
    {
        var rows = list.Active.Concat(list.Recent).Select(d => $"{d.Id}:{d.State}:{d.ItemId}");
        return string.Join("|", rows);
    }

    private void Render(MediaDownloadList list)
    {
        StatusText.Text = list.Active.Count switch
        {
            0 => list.Recent.Count == 0 ? "Nothing downloading" : $"Nothing downloading · {list.Recent.Count} recent",
            1 => "1 downloading",
            var n => $"{n} downloading",
        };

        RenderVpn(list.Status);
        var shape = Shape(list);
        var now = DateTimeOffset.Now;
        if (shape == _shape)
        {
            foreach (var download in list.Active)
            {
                if (download.Id is { } id && _rows.TryGetValue(id, out var parts)) Patch(parts, download, now);
            }
            return;
        }

        _shape = shape;
        _rows.Clear();
        ContentHost.Children.Clear();

        ContentHost.Children.Add(Heading("Downloading"));
        if (list.Active.Count == 0)
        {
            ContentHost.Children.Add(new TextBlock
            {
                Text = "Nothing downloading. Press + Add to find a movie or TV episodes.",
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(2, 0, 0, 12),
            });
        }
        foreach (var download in list.Active) ContentHost.Children.Add(Row(download, now));

        if (list.Recent.Count > 0)
        {
            ContentHost.Children.Add(Heading("Recent"));
            foreach (var download in list.Recent) ContentHost.Children.Add(Row(download, now));
        }
    }

    // The VPN line lives in the XAML, not in the rebuilt list, so it is redrawn on every poll and
    // the Test button under the pointer is never replaced. The dot is only repainted when its
    // tone changes, so a pulse runs smoothly across polls.
    private MediaDownloadText.VpnTone? _vpnTone;

    private void RenderVpn(MediaDownloadServiceStatus? status)
    {
        if (MediaDownloadText.VpnLook(status) is not { } look)
        {
            VpnBar.Visibility = Visibility.Collapsed;
            return;
        }
        VpnBar.Visibility = Visibility.Visible;
        VpnShortText.Text = look.Short;
        VpnShortText.Foreground = look.Tone == MediaDownloadText.VpnTone.Idle ? Palette.PrimaryText : VpnLight.Brush(look.Tone);
        VpnText.Text = look.Text;
        VpnText.Foreground = look.Tone is MediaDownloadText.VpnTone.Disabled or MediaDownloadText.VpnTone.Problem
            ? VpnLight.Brush(look.Tone)
            : Palette.PrimaryText;
        if (_vpnTone != look.Tone)
        {
            _vpnTone = look.Tone;
            VpnLight.Paint(VpnDot, look.Tone);
        }
    }

    // Connects if needed and checks the sites see another address than home - up to a minute.
    // The answer's message is the result either way, and its status redraws the line.
    private async void OnTestVpnClicked(object sender, RoutedEventArgs e)
    {
        VpnTestButton.IsEnabled = false;
        VpnTestButton.Content = "Testing…";
        VpnTestResult.Foreground = Palette.SecondaryText;
        VpnTestResult.Text = "Connecting if needed and checking what address the sites see. This can take up to a minute.";
        VpnTestResult.Visibility = Visibility.Visible;
        try
        {
            var test = await _model.Client.TestMediaDownloadVpnAsync();
            VpnTestResult.Foreground = test.Succeeded ? Palette.Green : Palette.Red;
            VpnTestResult.Text = string.IsNullOrWhiteSpace(test.Message)
                ? test.Succeeded ? "The VPN works." : "The VPN test failed."
                : test.Message;
            if (test.Status is not null) RenderVpn(test.Status);
        }
        catch (Exception error)
        {
            VpnTestResult.Foreground = Palette.Red;
            VpnTestResult.Text = AppModel.UserFacingMessage(error);
        }
        finally
        {
            VpnTestButton.IsEnabled = true;
            VpnTestButton.Content = "Test";
        }
    }

    private TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Style = (Style)FindResource("GroupHeading"),
        Margin = new Thickness(2, 6, 0, 10),
    };

    // ---------------------------------------------------------------- Rows

    private FrameworkElement Row(MediaDownload download, DateTimeOffset now)
    {
        var title = download.Title ?? "";
        var poster = MovieCards.Artwork(this, download.PosterUrl, title, 60, 90, 120);

        var titleLine = new WrapPanel();
        titleLine.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Palette.PrimaryText,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (download.Year is { } year) titleLine.Children.Add(Chip(year.ToString(), Palette.SecondaryText));
        if (!string.IsNullOrWhiteSpace(download.Subtitle)) titleLine.Children.Add(Chip(download.Subtitle!, Palette.Cyan));

        var body = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(titleLine);

        var release = string.IsNullOrWhiteSpace(download.Site)
            ? download.ReleaseTitle ?? ""
            : $"{download.ReleaseTitle} · {download.Site}";
        body.Children.Add(new TextBlock
        {
            Text = release,
            Foreground = Palette.SecondaryText,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = download.ReleaseTitle,
            Margin = new Thickness(0, 3, 0, 0),
        });

        ProgressBar? bar = null;
        if (download.IsActive)
        {
            bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(bar);
        }

        var facts = new TextBlock { Foreground = Palette.SecondaryText, FontSize = 11.5, Margin = new Thickness(0, 5, 0, 0) };
        body.Children.Add(facts);
        var note = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        body.Children.Add(note);

        var parts = new RowParts(bar, facts, note);
        Patch(parts, download, now);
        if (download.Id is { } id && download.IsActive) _rows[id] = parts;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(poster);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var actions = Actions(download);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackground"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    private static void Patch(RowParts parts, MediaDownload download, DateTimeOffset now)
    {
        if (parts.Bar is { } bar) bar.Value = MediaDownloadText.BarFraction(download);
        parts.Facts.Text = string.Join("  ·  ", MediaDownloadText.DownloadFacts(download, now));
        parts.Note.Text = download.Error ?? "";
        parts.Note.Foreground = download.IsFailed ? Palette.Red : Palette.Amber;
        parts.Note.Visibility = string.IsNullOrWhiteSpace(download.Error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement Actions(MediaDownload download)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (download.CanOpen)
        {
            var open = new Button { Content = "▶  Open", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (_, _) => Open(download);
            actions.Children.Add(open);
        }

        if (download.IsFailed)
        {
            var retry = new Button { Content = "Retry", Margin = new Thickness(0, 0, 8, 0) };
            retry.Click += async (_, _) => await ActAsync(retry, () => _model.Client.RetryMediaDownloadAsync(download.Id!));
            actions.Children.Add(retry);
        }

        if (download.IsActive)
        {
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("DangerButton") };
            cancel.Click += async (_, _) =>
            {
                const string question = "Cancel this download? What has downloaded so far is deleted.";
                var owner = Window.GetWindow(this);
                var answer = owner is null
                    ? MessageBox.Show(question, "Cancel download", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    : MessageBox.Show(owner, question, "Cancel download", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                var confirmed = answer == MessageBoxResult.OK;
                if (confirmed) await ActAsync(cancel, () => _model.Client.CancelMediaDownloadAsync(download.Id!));
            };
            actions.Children.Add(cancel);
        }
        else
        {
            var remove = new Button { Content = "Remove", ToolTip = "Remove from the list" };
            remove.Click += async (_, _) => await ActAsync(remove, () => _model.Client.CancelMediaDownloadAsync(download.Id!));
            actions.Children.Add(remove);
        }

        foreach (var child in actions.Children.OfType<Button>()) child.IsEnabled = !string.IsNullOrEmpty(download.Id);
        return actions;
    }

    private async Task ActAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try
        {
            await action();
            NoticeText.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            button.IsEnabled = true;
            Notice(AppModel.UserFacingMessage(error), isError: true);
            return;
        }
        _shape = null;
        await ReloadAsync(quiet: true);
    }

    // The item may still be on its way into the library: the detail page says so if the rescan
    // has not reached it yet (404), and Back returns here.
    private void Open(MediaDownload download)
    {
        if (download.ItemId is not { Length: > 0 } itemId) return;
        var title = string.IsNullOrWhiteSpace(download.Subtitle) ? download.Title ?? "" : $"{download.Title} · {download.Subtitle}";
        _navigator.Push(new MovieDetailView(_model, _navigator, itemId), title);
    }

    private static Border Chip(string text, Brush foreground) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x26, 0x2C)),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(0, 2, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = foreground, FontSize = 11 },
    };

    // ---------------------------------------------------------------- Controls

    private void Notice(string message, bool isError = false)
    {
        NoticeText.Text = message;
        NoticeText.Foreground = isError ? Palette.Red : Palette.SecondaryText;
        NoticeText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var window = new MediaDownloadAddWindow(_model, _status) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        if (window.Grabbed is { } grabbed)
        {
            var name = string.IsNullOrWhiteSpace(grabbed.Subtitle) ? grabbed.Title : $"{grabbed.Title} {grabbed.Subtitle}";
            Notice($"Downloading {name}.");
        }
        _shape = null;
        await ReloadAsync(quiet: true);
    }

    private async void OnSitesClicked(object sender, RoutedEventArgs e)
    {
        new MediaDownloadSitesWindow(_model) { Owner = Window.GetWindow(this) }.ShowDialog();
        _shape = null;
        await ReloadAsync(quiet: true);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        _statusChecked = false;
        _shape = null;
        await ReloadAsync(quiet: true);
    }
}
