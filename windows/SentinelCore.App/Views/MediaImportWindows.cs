using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SentinelCore.App.Views.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

// "Import a list" for Music and Movies: pick a CSV/JSON exported from Spotify, Letterboxd, IMDb and
// the like, check what the server found, and the server works through it in the background
// (search -> best release -> download queue). The same three pieces serve both tabs:
//   MediaImportWindow       choose files, then the preview's checklist and "download N albums"
//   MediaImportDetailWindow one list's items and their states, with leave-one-out
//   ImportedListsPanel      the "Imported lists" cards under Downloads, kept current while shown
// Contract: core/docs/MOVIE_DOWNLOADS.md "Import a list"; the wording and behaviour mirror the web
// dashboard's media-import.js. Built in code like the Music screens, on AudioDialog's frame.

/// <summary>Choose the exported files, look over what was found, name the list and start it.</summary>
public sealed class MediaImportWindow : AudioDialog
{
    // Like the web: hundreds of check boxes are fine, thousands make the window crawl. The rest
    // stay chosen; the filter finds any one of them.
    private const int ShownAtOnce = 400;

    private readonly MediaImportKind _kind;
    private readonly Button _choose;
    private readonly Button _back;
    private readonly Button _start;
    private readonly TextBox _filter = new() { Width = 220, ToolTip = "Filter" };
    private readonly TextBox _name = new() { MaxLength = 120 };
    private readonly StackPanel _list = new();
    private readonly HashSet<string> _checked = new();
    private List<string> _fileNames = new();
    private MediaImportPreview? _preview;
    private List<MediaImportChoice> _items = new();
    private bool _busy;

    public MediaImportWindow(AppModel model, MediaImportKind kind)
        : base(model, $"Import a list of {MediaImportText.Things(kind)}", null, 780, 700)
    {
        _kind = kind;
        _back = AddFooterButton("choose other files", ShowPick);
        _choose = AddFooterButton("Choose files…", () => _ = ChooseAsync(), primary: true);
        _start = AddFooterButton("", () => _ = StartAsync(), primary: true);
        AddFooterButton("Close", Close, isCancel: true);
        // Enter in the filter or name box should not start a download (or open the picker).
        _choose.IsDefault = false;
        _start.IsDefault = false;
        _filter.TextChanged += (_, _) => DrawList();
        ShowPick();
    }

    /// <summary>The line to show once started ("Working through 12 albums…"); null until then.</summary>
    public string? StartedMessage { get; private set; }

    // ---------------------------------------------------------------- Choosing the files

    private void ShowPick()
    {
        _preview = null;
        _items = new();
        Body.Children.Clear();
        foreach (var line in MediaImportText.Help(_kind))
        {
            Body.Children.Add(new System.Windows.Controls.Primitives.BulletDecorator
            {
                Margin = new Thickness(0, 0, 0, 6),
                Bullet = new TextBlock { Text = "•", Foreground = Palette.Green, Margin = new Thickness(0, 0, 8, 0) },
                Child = AudioUi.Body(line),
            });
        }
        if (MediaImportText.HelpNote(_kind) is { } note) Body.Children.Add(AudioUi.Caption(note, 6));
        var exportify = new Button { Content = "Open exportify.net", Style = AudioUi.StyleOf("LinkButton"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        exportify.Click += (_, _) => OpenLink("https://exportify.net");
        if (_kind == MediaImportKind.Music) Body.Children.Add(exportify);
        Body.Children.Add(AudioUi.Caption(".csv or .json · several files at once is fine", 14));
        _choose.Visibility = Visibility.Visible;
        _choose.IsEnabled = true;
        _back.Visibility = Visibility.Collapsed;
        _start.Visibility = Visibility.Collapsed;
        Message(null);
    }

    private static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser to hand: the address is in the help text above.
        }
    }

    private async Task ChooseAsync()
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose the exported list",
            Multiselect = true,
            Filter = "Lists (*.csv;*.json;*.txt;*.tsv)|*.csv;*.json;*.txt;*.tsv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;

        _busy = true;
        _choose.IsEnabled = false;
        Message("Reading…");
        try
        {
            var files = new List<MediaImportFile>();
            foreach (var path in dialog.FileNames)
            {
                string text;
                try
                {
                    // UTF-8 unless the file carries a byte-order mark saying otherwise.
                    text = await File.ReadAllTextAsync(path, Encoding.UTF8);
                }
                catch (Exception)
                {
                    Message("That file could not be read.", error: true);
                    return;
                }
                files.Add(new MediaImportFile { Name = Path.GetFileName(path), Text = text });
            }
            var preview = await Model.Client.PreviewMediaImportAsync(_kind, files);
            _fileNames = files.Select(f => f.Name).ToList();
            ShowPreview(preview);
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            _busy = false;
            _choose.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- The preview

    private void ShowPreview(MediaImportPreview preview)
    {
        _preview = preview;
        _items = preview.Choices(_kind);
        _checked.Clear();
        foreach (var item in _items.Where(i => !i.InLibrary)) _checked.Add(item.Key);
        _name.Text = MediaImportText.DefaultName(_kind, _fileNames);
        _filter.Text = "";

        Body.Children.Clear();
        Body.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", MediaImportText.Facts(_kind, preview)),
            Foreground = Palette.PrimaryText,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var note in MediaImportText.Notes(_kind, preview))
        {
            Body.Children.Add(new TextBlock { Text = note, Foreground = Palette.Amber, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        }

        if (_items.Count == 0)
        {
            Body.Children.Add(AudioUi.Caption($"No {MediaImportText.Things(_kind)} found in {(_fileNames.Count == 1 ? "that file" : "those files")}.", 10));
        }
        else
        {
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
            tools.Children.Add(_filter);
            tools.Children.Add(Link("all", () => Choose(_ => true)));
            tools.Children.Add(Link("none", () => Choose(_ => false)));
            tools.Children.Add(Link("only missing", () => Choose(i => !i.InLibrary)));
            Body.Children.Add(tools);
            Body.Children.Add(new Border { Style = AudioUi.StyleOf("Card"), Padding = new Thickness(10, 6, 10, 6), Child = _list });
            Body.Children.Add(new TextBlock { Text = "Name this list", Style = AudioUi.StyleOf("FieldLabel"), Margin = new Thickness(0, 12, 0, 4) });
            Body.Children.Add(_name);
            Body.Children.Add(AudioUi.Caption(MediaImportText.Explainer, 10));
            DrawList();
        }

        _choose.Visibility = Visibility.Collapsed;
        _back.Visibility = Visibility.Visible;
        _start.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Message(null);
        UpdateStart();
    }

    private Button Link(string text, Action click)
    {
        var button = new Button { Content = text, Style = AudioUi.StyleOf("LinkButton"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }

    private void Choose(Func<MediaImportChoice, bool> pick)
    {
        _checked.Clear();
        foreach (var item in _items.Where(pick)) _checked.Add(item.Key);
        DrawList();
        UpdateStart();
    }

    private void DrawList()
    {
        _list.Children.Clear();
        var matching = _items.Where(i => i.Matches(_filter.Text)).ToList();
        foreach (var item in matching.Take(ShownAtOnce)) _list.Children.Add(Row(item));
        if (matching.Count > ShownAtOnce)
            _list.Children.Add(AudioUi.Caption($"…and {matching.Count - ShownAtOnce} more (all still included). Filter to find one.", 6));
        if (matching.Count == 0) _list.Children.Add(AudioUi.Caption("Nothing matches the filter."));
    }

    private CheckBox Row(MediaImportChoice item)
    {
        var title = new TextBlock { Foreground = item.InLibrary ? Palette.SecondaryText : Palette.PrimaryText, TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(item.Title);
        if (item.Year is { } year) title.Inlines.Add(new System.Windows.Documents.Run($"  {year}") { Foreground = Palette.SecondaryText });
        if (item.InLibrary) title.Inlines.Add(new System.Windows.Documents.Run("  in library") { Foreground = Palette.Green, FontSize = 11 });
        if (item.Single) title.Inlines.Add(new System.Windows.Documents.Run("  single") { Foreground = Palette.Amber, FontSize = 11 });
        var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        text.Children.Add(title);
        if (_kind == MediaImportKind.Music)
        {
            var sub = item.Artist ?? "";
            if (item.TrackCount > 0) sub += $" · {MediaImportText.Plural(item.TrackCount, "song")} on your list";
            text.Children.Add(new TextBlock { Text = sub, Foreground = Palette.SecondaryText, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        }
        var box = new CheckBox { Content = text, IsChecked = _checked.Contains(item.Key), Margin = new Thickness(0, 3, 0, 3), VerticalContentAlignment = VerticalAlignment.Top };
        box.Checked += (_, _) =>
        {
            _checked.Add(item.Key);
            UpdateStart();
        };
        box.Unchecked += (_, _) =>
        {
            _checked.Remove(item.Key);
            UpdateStart();
        };
        return box;
    }

    private void UpdateStart()
    {
        var n = _checked.Count;
        _start.Content = _busy ? "Starting…" : MediaImportText.StartLabel(_kind, n);
        _start.IsEnabled = n > 0 && !_busy;
    }

    private async Task StartAsync()
    {
        var chosen = _items.Where(i => _checked.Contains(i.Key)).ToList();
        if (chosen.Count == 0 || _busy) return;
        _busy = true;
        UpdateStart();
        Message(null);
        try
        {
            await Model.Client.StartMediaImportAsync(_kind, new MediaImportStartInput
            {
                Name = _name.Text.Trim(),
                Items = chosen.Select(c => c.ToStartItem()).ToList(),
            });
            StartedMessage = MediaImportText.Started(_kind, chosen.Count);
            Done();
        }
        catch (Exception error)
        {
            _busy = false;
            UpdateStart();
            Fail(error);
        }
    }
}

/// <summary>One imported list: how far along it is, and every album or film with its state.</summary>
public sealed class MediaImportDetailWindow : AudioDialog
{
    private const int ShownAtOnce = 800;

    private readonly MediaImportKind _kind;
    private readonly string _id;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Button _pause;
    private readonly Button _retry;
    private readonly Button _remove;
    private MediaImportJob? _job;
    private string _filter = "all";
    private string? _shape;

    public MediaImportDetailWindow(AppModel model, MediaImportKind kind, string id)
        : base(model, "Imported list", null, 760, 680)
    {
        _kind = kind;
        _id = id;
        _pause = AddFooterButton("pause", () => _ = PauseAsync());
        _retry = AddFooterButton("search missing again", () => _ = ChangeAsync(() => Model.Client.RetryMediaImportAsync(_kind, _id)));
        _remove = AddFooterButton("remove list", () => _ = RemoveAsync());
        AddFooterButton("Close", Close, isCancel: true);
        _pause.Visibility = _retry.Visibility = _remove.Visibility = Visibility.Collapsed;
        Body.Children.Add(AudioUi.Caption("Loading…"));
        _poll.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _poll.Stop();
    }

    /// <summary>Something was changed here, so the cards behind should be asked again.</summary>
    public bool Changed { get; private set; }

    private async Task LoadAsync()
    {
        _poll.Stop();
        try
        {
            Show(await Model.Client.MediaImportAsync(_kind, _id));
        }
        catch (Exception error)
        {
            Fail(error);
        }
        if (_job is { IsFinished: false } && IsLoaded) _poll.Start();
    }

    private void Show(MediaImportJob job)
    {
        _job = job;
        _pause.Content = job.IsPaused ? "resume" : "pause";
        _pause.Visibility = job.CanPause ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = job.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        _remove.Visibility = Visibility.Visible;
        Message(null);

        // Rebuild only when something shows differently, so a button under the pointer stays put.
        var shape = string.Join("|", job.Items.Select(i => $"{i.Id}:{i.State}:{i.Progress}:{i.Message}"))
            + $"#{_filter}#{job.Paused}#{job.Finished}#{job.Message}";
        if (shape == _shape) return;
        _shape = shape;

        Body.Children.Clear();
        Body.Children.Add(AudioUi.Title(job.Name ?? "Imported list", 18));
        Body.Children.Add(ImportedListsPanel.Summary(job));
        Body.Children.Add(new Border
        {
            Margin = new Thickness(0, 6, 0, 8),
            Child = AudioUi.Tabs(MediaImportText.Filters.ToList(), _filter, key =>
            {
                _filter = key;
                _shape = null;
                Show(_job ?? job);
            }),
        });
        var items = job.Items.Where(i => MediaImportText.InFilter(_filter, i.State)).ToList();
        if (items.Count == 0) Body.Children.Add(AudioUi.Caption("Nothing here.", 6));
        foreach (var item in items.Take(ShownAtOnce)) Body.Children.Add(Row(item));
        if (items.Count > ShownAtOnce) Body.Children.Add(AudioUi.Caption($"…and {items.Count - ShownAtOnce} more.", 6));
    }

    private FrameworkElement Row(MediaImportItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var text = new StackPanel();
        var title = new TextBlock { Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(item.Title ?? "");
        if (item.Year is { } year) title.Inlines.Add(new System.Windows.Documents.Run($"  {year}") { Foreground = Palette.SecondaryText });
        text.Children.Add(title);
        if (!string.IsNullOrEmpty(item.Artist)) text.Children.Add(Sub(item.Artist!, Palette.SecondaryText));
        if (!string.IsNullOrEmpty(item.ReleaseTitle))
        {
            var release = Sub(item.ReleaseTitle!, Palette.SecondaryText);
            release.TextWrapping = TextWrapping.NoWrap;
            release.TextTrimming = TextTrimming.CharacterEllipsis;
            release.ToolTip = item.ReleaseTitle;
            text.Children.Add(release);
        }
        if (!string.IsNullOrEmpty(item.Message)) text.Children.Add(Sub(item.Message!, item.State == "failed" ? Palette.Red : Palette.Amber));
        grid.Children.Add(text);

        var state = new TextBlock
        {
            Text = MediaImportText.StateWord(item),
            Foreground = item.State switch
            {
                "done" or "inLibrary" => Palette.Green,
                "notFound" or "failed" => Palette.Amber,
                _ => Palette.SecondaryText,
            },
            FontSize = 12,
            Margin = new Thickness(12, 2, 4, 0),
        };
        Grid.SetColumn(state, 1);
        grid.Children.Add(state);

        if (item.CanLeaveOut && item.Id is { } itemId)
        {
            var skip = AudioUi.GlyphButton("", "Leave this one out", () => _ = ChangeAsync(() => Model.Client.LeaveOutMediaImportItemAsync(_kind, _id, itemId)), 11);
            skip.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(skip, 2);
            grid.Children.Add(skip);
        }
        return grid;
    }

    private static TextBlock Sub(string text, System.Windows.Media.Brush brush) =>
        new() { Text = text, Foreground = brush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };

    private Task PauseAsync() =>
        _job is { } job ? ChangeAsync(() => Model.Client.PauseMediaImportAsync(_kind, _id, !job.IsPaused)) : Task.CompletedTask;

    private async Task ChangeAsync(Func<Task<MediaImportJob>> change)
    {
        try
        {
            var job = await change();
            Changed = true;
            Show(job);
            _poll.Stop();
            if (!job.IsFinished) _poll.Start();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task RemoveAsync()
    {
        if (MessageBox.Show(this, MediaImportText.RemoveQuestion, "Remove list", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            await Model.Client.DeleteMediaImportAsync(_kind, _id);
            Changed = true;
            Close();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}

/// <summary>
/// "Imported lists" under a tab's Downloads: a card per list with its progress and buttons, and
/// "Import a list". Asks again every 5 seconds while a list is working and every 30 otherwise,
/// only while on screen.
/// </summary>
public sealed class ImportedListsPanel : StackPanel
{
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    private readonly AppModel _model;
    private readonly MediaImportKind _kind;
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background);
    private readonly StackPanel _cards = new();
    private readonly TextBlock _note;
    private List<MediaImportJob>? _jobs;
    private string? _shape;

    public ImportedListsPanel(AppModel model, MediaImportKind kind)
    {
        _model = model;
        _kind = kind;
        Margin = new Thickness(0, 8, 0, 12);

        var head = new Grid { Margin = new Thickness(0, 14, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = AudioUi.Heading("Imported lists", 0);
        heading.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(heading);
        var import = AudioUi.TextButton("Import a list", OpenImport, "Download everything on a list exported from another service", margin: new Thickness(0));
        Grid.SetColumn(import, 1);
        head.Children.Add(import);
        Children.Add(head);

        _note = new TextBlock { Foreground = Palette.Green, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8), Visibility = Visibility.Collapsed };
        Children.Add(_note);
        Children.Add(_cards);

        _poll.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => _ = RefreshAsync();
        Unloaded += (_, _) => _poll.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _poll.Stop();
            else if (IsLoaded) _ = RefreshAsync();
        };
    }

    /// <summary>The green line above the cards, e.g. what the import window said once it started.</summary>
    public void Say(string? message)
    {
        _note.Text = message ?? "";
        _note.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    public async Task RefreshAsync()
    {
        _poll.Stop();
        try
        {
            _jobs = await _model.Client.MediaImportsAsync(_kind);
            Draw(null);
        }
        catch (Exception error)
        {
            Draw(AppModel.UserFacingMessage(error));
        }
        if (IsVisible && IsLoaded)
        {
            _poll.Interval = (_jobs ?? new()).Any(j => j.IsBusy) ? BusyInterval : IdleInterval;
            _poll.Start();
        }
    }

    private void Draw(string? error)
    {
        var jobs = _jobs ?? new();
        var shape = error + "#" + string.Join("|", jobs.Select(j =>
        {
            var c = j.CountsOrEmpty;
            return $"{j.Id}:{j.Name}:{j.Paused}:{j.Finished}:{j.Message}:{c.Total}:{c.Waiting}:{c.Downloading}:{c.Done}:{c.InLibrary}:{c.NotFound}:{c.Failed}:{c.Canceled}";
        }));
        if (shape == _shape) return;
        _shape = shape;

        _cards.Children.Clear();
        if (error is not null) _cards.Children.Add(new TextBlock { Text = error, Foreground = Palette.Amber, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8) });
        if (jobs.Count == 0)
        {
            if (error is null) _cards.Children.Add(AudioUi.Caption(MediaImportText.EmptyLine(_kind)));
            return;
        }
        foreach (var job in jobs) _cards.Children.Add(Card(job));
    }

    private FrameworkElement Card(MediaImportJob job)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var body = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var title = new TextBlock { Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(job.Name ?? "Imported list");
        title.Inlines.Add(new System.Windows.Documents.Run($"  {MediaImportText.CountOf(_kind, job.CountsOrEmpty.Total)}") { Foreground = Palette.SecondaryText, FontWeight = FontWeights.Normal, FontSize = 12 });
        body.Children.Add(title);
        if (!string.IsNullOrEmpty(job.RequestedBy) && _model.IsAdmin) body.Children.Add(new TextBlock { Text = $"Imported by {job.RequestedBy}", Foreground = Palette.SecondaryText, FontSize = 11.5 });
        body.Children.Add(Summary(job));
        grid.Children.Add(body);

        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ActionButton("details", () => OpenDetail(job)));
        if (job.CanPause && job.Id is { } pauseId)
            buttons.Children.Add(ActionButton(job.IsPaused ? "resume" : "pause", () => _ = ChangeAsync(() => _model.Client.PauseMediaImportAsync(_kind, pauseId, !job.IsPaused))));
        if (job.CanRetry && job.Id is { } retryId)
            buttons.Children.Add(ActionButton("search missing again", () => _ = ChangeAsync(() => _model.Client.RetryMediaImportAsync(_kind, retryId))));
        if (job.Id is { } removeId)
        {
            buttons.Children.Add(ActionButton("remove list", () =>
            {
                if (!AudioUi.Confirm("Remove list", MediaImportText.RemoveQuestion)) return;
                _ = ChangeAsync(() => _model.Client.DeleteMediaImportAsync(_kind, removeId));
            }));
        }
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        return new Border { Style = AudioUi.StyleOf("Card"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid };
    }

    private static Button ActionButton(string text, Action click) =>
        AudioUi.TextButton(text, click, margin: new Thickness(6, 2, 0, 2));

    /// <summary>The status word, the counts, the bar and the waiting message: shared with the detail window.</summary>
    public static FrameworkElement Summary(MediaImportJob job)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var status = MediaImportText.Status(job);
        var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.SecondaryText };
        line.Inlines.Add(new System.Windows.Documents.Run(status) { Foreground = status == "working" ? Palette.Green : Palette.SecondaryText, FontWeight = FontWeights.SemiBold });
        var bits = MediaImportText.Progress(job.CountsOrEmpty);
        if (bits.Count > 0) line.Inlines.Add($"  ·  {string.Join("  ·  ", bits)}");
        panel.Children.Add(line);
        panel.Children.Add(new ProgressBar { Maximum = 1, Value = job.CountsOrEmpty.Fraction, Height = 4, Margin = new Thickness(0, 6, 0, 0) });
        if (!string.IsNullOrEmpty(job.Message) && !job.IsPaused)
            panel.Children.Add(new TextBlock { Text = job.Message, Foreground = Palette.Amber, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    private async Task ChangeAsync(Func<Task> change)
    {
        try
        {
            await change();
        }
        catch (Exception error)
        {
            MessageBox.Show(Window.GetWindow(this), AppModel.UserFacingMessage(error), "Imported lists", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _shape = null;
        await RefreshAsync();
    }

    private void OpenImport()
    {
        var window = new MediaImportWindow(_model, _kind) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        Say(window.StartedMessage);
        _ = RefreshAsync();
    }

    private void OpenDetail(MediaImportJob job)
    {
        if (job.Id is not { } id) return;
        var window = new MediaImportDetailWindow(_model, _kind, id) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (window.Changed) _shape = null;
        _ = RefreshAsync();
    }
}
