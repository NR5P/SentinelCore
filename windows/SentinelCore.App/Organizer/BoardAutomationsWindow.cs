using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Board automations: the list of one board's rules, and the no-code builder for them. People drag
/// blocks from the palette into WHEN / ONLY IF / THEN (or click a block to add it), fill in the
/// blanks, and read the rule back as a plain sentence. The rules run on the server, so this window
/// is only the editor - and, like the dashboard's, it needs the server: rules never sync.
/// The wording and the rules of the builder live in the client library (BoardAutomations.cs), a
/// port of the dashboard's organizer-automations.js.
/// </summary>
internal sealed class BoardAutomationsWindow : Window
{
    private const string BlockFormat = "SentinelCore.AutomationBlock";
    private const string Offline = "Automations need the server - they can't be changed offline.";

    private readonly OrganizerEngine _engine;
    private readonly OrganizerItem _board;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };

    private List<BoardAutomation> _rules = new();
    private List<OrganizerPerson> _people = new();
    private bool _pushReady = true;
    private bool _emailReady = true;
    private AutomationContext _context;

    // The editor, while it's showing.
    private BoardAutomation? _draft;
    private TextBox _name = new();
    private readonly StackPanel _canvas = new();
    private readonly TextBlock _sentence = new() { TextWrapping = TextWrapping.Wrap, Foreground = Palette.PrimaryText };
    private readonly StackPanel _errors = new();
    private TextBlock? _next;
    private int _previewRound;
    // The text boxes a {token} can go into, and the one last typed in.
    private readonly List<TextBox> _tokenBoxes = new();
    private TextBox? _lastTokenBox;
    private readonly Dictionary<string, Border> _placed = new();
    private Point _dragStart;
    private string? _pressed;

    public BoardAutomationsWindow(OrganizerEngine engine, OrganizerItem board)
    {
        _engine = engine;
        _board = board;
        _context = BuildContext();

        Style = Ui.Style("Dialog");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 1060;
        Height = 780;
        MinWidth = 760;
        MinHeight = 480;
        Title = $"Automations · {BoardTitle}";

        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _ = PreviewAsync();
        };
        Closed += (_, _) => _previewTimer.Stop();
        PreviewKeyDown += (_, e) =>
        {
            // Escape leaves the list, as the dashboard's does, but never throws away a half-built rule.
            if (e.Key == Key.Escape && _draft is null)
            {
                Close();
                e.Handled = true;
            }
        };

        Content = Ui.Caption("Loading…");
        Loaded += async (_, _) =>
        {
            await LoadPeopleAsync();
            await LoadAsync();
        };
    }

    private string BoardTitle => _board.Title.Length == 0 ? "board" : _board.Title;

    // ---- the board's lists, labels and people ---------------------------------------------------

    private List<OrganizerItem> Columns(string boardId) =>
        _engine.Children(boardId, OrganizerKinds.Column).OrderBy(column => column.SortPosition).ToList();

    private List<OrganizerItem> OtherBoards() =>
        _engine.Items(OrganizerKinds.Board).Where(board => board.Id != _board.Id && !board.Bool("archived"))
            .OrderBy(board => board.SortPosition).ToList();

    private AutomationContext BuildContext() => new()
    {
        ColumnTitles = _engine.Items(OrganizerKinds.Column).GroupBy(column => column.Id).ToDictionary(group => group.Key, group => group.First().Title),
        BoardColumnIds = Columns(_board.Id).Select(column => column.Id).ToList(),
        People = _people,
        Culture = CultureInfo.CurrentCulture,
        TimeZone = BoardAutomationRules.DeviceTimeZone(),
    };

    private List<string> KnownLabels() => Columns(_board.Id)
        .SelectMany(column => _engine.Children(column.Id, OrganizerKinds.Card))
        .SelectMany(card => card.List("labels").OfType<IReadOnlyDictionary<string, object?>>())
        .Select(label => label.GetValueOrDefault("name") as string)
        .OfType<string>().Where(name => name.Length > 0)
        .Distinct().OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // Only with login on is there anyone to pick; with it off, every phone is the same person.
    private async Task LoadPeopleAsync()
    {
        if (_engine.Status is not { PersonalSpaces: true }) return;
        try
        {
            _people = await _engine.PeopleAsync();
        }
        catch (Exception)
        {
            _people = new List<OrganizerPerson>();
        }
        _context = BuildContext();
    }

    // ---- the list of rules ------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        try
        {
            var list = await _engine.Automations.ListAsync(_board.Id);
            _rules = list.Automations.ToList();
            _pushReady = list.PushReady;
            _emailReady = list.EmailReady;
            _context = BuildContext();
            if (_draft is null) ShowList();
        }
        catch (Exception)
        {
            if (_draft is not null) return;
            var page = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
            page.Children.Add(Ui.Text(Offline));
            Content = Framed(page, CloseFooter());
        }
    }

    private void Notice(string message, bool ok)
    {
        _notice.Text = message;
        _notice.Foreground = ok ? Palette.Green : Palette.Red;
        _notice.Visibility = Visibility.Visible;
    }

    private static UIElement Framed(UIElement body, UIElement footer)
    {
        var root = new DockPanel();
        var footerBorder = new Border { Style = Ui.Style("DialogFooter"), Child = footer };
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);
        root.Children.Add(body is ScrollViewer ? body : new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private UIElement CloseFooter()
    {
        var footer = new DockPanel { LastChildFill = false };
        var close = Ui.Button("Close", Close);
        close.MinWidth = 90;
        close.IsCancel = true;
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        return footer;
    }

    private void ShowList()
    {
        _draft = null;
        _previewTimer.Stop();
        Title = $"Automations · {BoardTitle}";

        var page = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        if (_notice.Parent is Panel old) old.Children.Remove(_notice);
        page.Children.Add(_notice);
        var intro = Ui.Caption("Rules that look after this board for you. They run on the server, so they happen even when every phone and computer is off.");
        intro.Margin = new Thickness(0, 0, 0, 12);
        page.Children.Add(intro);

        var start = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        var build = Ui.Button("+ Build a rule", () => ShowEditor(null, null), "PrimaryButton");
        build.Margin = new Thickness(0, 0, 12, 8);
        start.Children.Add(build);
        var or = Ui.Caption("or start from a recipe:");
        or.VerticalAlignment = VerticalAlignment.Center;
        or.Margin = new Thickness(0, 0, 12, 8);
        start.Children.Add(or);
        foreach (var recipe in BoardAutomationRules.Recipes(_board.Id, _context))
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = recipe.Name, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText });
            content.Children.Add(new TextBlock { Text = recipe.Why, FontSize = 11, Foreground = Palette.SecondaryText });
            var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 8), MinWidth = 180 };
            var rule = recipe.Rule;
            button.Click += (_, _) => ShowEditor(null, rule);
            start.Children.Add(button);
        }
        page.Children.Add(start);

        if (_rules.Count == 0) page.Children.Add(Ui.Caption("No rules yet. Build one, or pick a recipe above and change it to suit."));
        foreach (var rule in _rules) page.Children.Add(RuleRow(rule));

        Content = Framed(page, CloseFooter());
    }

    private Border RuleRow(BoardAutomation rule)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new CheckBox { IsChecked = rule.Enabled, ToolTip = "on / off", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        enabled.Click += async (_, _) => await SetEnabledAsync(rule, enabled.IsChecked == true);
        grid.Children.Add(enabled);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = rule.Name, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap });
        var sentence = Ui.Text(BoardAutomationRules.RuleSentence(rule, _context));
        sentence.Margin = new Thickness(0, 2, 0, 4);
        body.Children.Add(sentence);
        var status = new WrapPanel();
        if (rule.IsScheduled && rule.Enabled && rule.NextRunAt is { } next)
        {
            status.Children.Add(new TextBlock { Text = $"next: {When(next)}", FontSize = 11.5, Foreground = Palette.SecondaryText, Margin = new Thickness(0, 0, 14, 0) });
        }
        status.Children.Add(rule.LastRunAt is { } last
            ? new TextBlock
            {
                Text = $"{(rule.LastOk == true ? "✓" : "!")} {When(last)}: {rule.LastResult}",
                FontSize = 11.5,
                Foreground = rule.LastOk == true ? Palette.Green : Palette.Red,
                TextWrapping = TextWrapping.Wrap,
            }
            : new TextBlock { Text = "hasn't run yet", FontSize = 11.5, Foreground = Palette.SecondaryText });
        body.Children.Add(status);
        var history = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(history);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
        Button Small(string text, Action onClick, string? style = null, string? tooltip = null)
        {
            var button = Ui.Button(text, onClick, style, tooltip);
            button.Padding = new Thickness(9, 4, 9, 4);
            button.Margin = new Thickness(6, 0, 0, 0);
            return button;
        }
        if (rule.IsScheduled) actions.Children.Add(Small("Run now", () => _ = RunNowAsync(rule), tooltip: "Do it right now, to try it out"));
        actions.Children.Add(Small("Edit", () => ShowEditor(rule, null)));
        actions.Children.Add(Small("History", () => _ = ToggleHistoryAsync(rule, history)));
        actions.Children.Add(Small("Delete", () => _ = DeleteAsync(rule), "DangerButton"));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return new Border
        {
            Background = Ui.Brush("CardBackground"),
            BorderBrush = Ui.Brush("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = rule.Enabled ? 1 : 0.6,
            Child = grid,
        };
    }

    private string When(DateTimeOffset moment) => BoardAutomationRules.FormatWhen(moment, Ui.Zone, CultureInfo.CurrentCulture);

    private async Task SetEnabledAsync(BoardAutomation rule, bool enabled)
    {
        try
        {
            await _engine.Automations.SetEnabledAsync(rule, enabled);
        }
        catch (Exception error)
        {
            Notice(Message(error), false);
        }
        await LoadAsync();
    }

    private async Task RunNowAsync(BoardAutomation rule)
    {
        try
        {
            var run = await _engine.Automations.RunNowAsync(rule.Id ?? "");
            Notice(string.IsNullOrEmpty(run.Message) ? "Done." : run.Message, run.Ok);
            // The card it made is an ordinary card; fetch it now rather than at the next sync.
            _ = _engine.SyncAsync();
            await LoadAsync();
        }
        catch (Exception error)
        {
            Notice(Message(error), false);
        }
    }

    private async Task DeleteAsync(BoardAutomation rule)
    {
        if (!Ui.Confirm(this, $"Delete the rule \"{rule.Name}\"? Cards it already made stay where they are.")) return;
        try
        {
            await _engine.Automations.DeleteAsync(rule.Id ?? "");
            await LoadAsync();
        }
        catch (Exception error)
        {
            Notice(Message(error), false);
        }
    }

    private async Task ToggleHistoryAsync(BoardAutomation rule, StackPanel box)
    {
        if (box.Visibility == Visibility.Visible)
        {
            box.Visibility = Visibility.Collapsed;
            return;
        }
        box.Visibility = Visibility.Visible;
        box.Children.Clear();
        box.Children.Add(Ui.Caption("Loading…"));
        try
        {
            var runs = await _engine.Automations.RunsAsync(rule.Id ?? "");
            box.Children.Clear();
            if (runs.Count == 0) box.Children.Add(Ui.Caption("Nothing yet."));
            foreach (var run in runs)
            {
                box.Children.Add(new TextBlock
                {
                    Text = $"{(run.Ok ? "✓" : "!")} {When(run.At)} - {run.Message}",
                    FontSize = 11.5,
                    Foreground = run.Ok ? Palette.SecondaryText : Palette.Red,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        catch (Exception error)
        {
            box.Children.Clear();
            box.Children.Add(Ui.Caption(Message(error)));
        }
    }

    private static string Message(Exception error) => error is OrganizerOfflineException ? Offline : error.Message;

    // ---- the editor -------------------------------------------------------------------------------

    private void ShowEditor(BoardAutomation? rule, BoardAutomation? recipe)
    {
        _context = BuildContext();
        // A copy: going back leaves the rule as it was. Fields this app doesn't know ride along.
        _draft = (rule ?? recipe)?.Clone() ?? new BoardAutomation();
        _draft.BoardId = _board.Id;
        if (rule is null)
        {
            _draft.Id = null;
            _draft.Enabled = true;
        }
        _lastTokenBox = null;
        _errors.Children.Clear();
        _notice.Visibility = Visibility.Collapsed;
        Title = $"{(rule is null ? "New rule" : "Edit rule")} · {BoardTitle}";

        _name = new TextBox { FontSize = 15, Text = _draft.Name };
        _name.TextChanged += (_, _) =>
        {
            if (_draft is not null) _draft.Name = _name.Text;
        };
        var nameBox = Ui.WithPlaceholder(_name, "Name this rule, e.g. Eggs every morning");
        nameBox.Margin = new Thickness(18, 14, 18, 10);

        var palette = new ScrollViewer
        {
            Width = 270,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Palette_(),
            Margin = new Thickness(18, 0, 12, 0),
        };
        var canvas = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _canvas, Padding = new Thickness(0, 0, 14, 0) };
        var builder = new DockPanel();
        DockPanel.SetDock(palette, Dock.Left);
        builder.Children.Add(palette);
        builder.Children.Add(canvas);

        var summary = new StackPanel { Margin = new Thickness(18, 10, 18, 10) };
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new System.Windows.Documents.Run("In plain words: ") { Foreground = Palette.SecondaryText });
        summary.Children.Add(line);
        summary.Children.Add(_sentence);
        summary.Children.Add(_errors);

        var footer = new DockPanel { LastChildFill = false };
        var save = Ui.Button("Save rule", () => _ = SaveAsync(), "PrimaryButton");
        save.MinWidth = 100;
        var back = Ui.Button("Back", ShowList);
        back.MinWidth = 90;
        back.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(back, Dock.Right);
        footer.Children.Add(save);
        footer.Children.Add(back);

        var root = new DockPanel();
        var footerBorder = new Border { Style = Ui.Style("DialogFooter"), Child = footer };
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);
        var summaryBorder = new Border { BorderBrush = Ui.Brush("CardBorder"), BorderThickness = new Thickness(0, 1, 0, 0), Child = summary };
        DockPanel.SetDock(summaryBorder, Dock.Bottom);
        root.Children.Add(summaryBorder);
        DockPanel.SetDock(nameBox, Dock.Top);
        root.Children.Add(nameBox);
        root.Children.Add(builder);
        Content = root;

        RenderCanvas();
        Dispatcher.BeginInvoke(() => _name.Focus(), DispatcherPriority.Input);
    }

    private static Brush ZoneBrush(string zone) => zone switch
    {
        "trigger" => Palette.Cyan,
        "conditions" => Palette.Purple,
        _ => Palette.Green,
    };

    // ---- the palette ------------------------------------------------------------------------------

    private StackPanel Palette_()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Caption("Drag a block into the rule, or click it."));
        void Group(string title, string zone, IEnumerable<AutomationBlock> blocks)
        {
            var heading = Ui.Heading(title);
            heading.Foreground = ZoneBrush(zone);
            heading.Margin = new Thickness(0, 14, 0, 6);
            panel.Children.Add(heading);
            foreach (var block in blocks.Where(block => !block.NeedsPeople || _people.Count > 0)) panel.Children.Add(PaletteBlock(block));
        }
        Group("When", "trigger", BoardAutomationRules.TriggerBlocks);
        Group("Only if", "conditions", BoardAutomationRules.ConditionBlocks);
        Group("Then", "actions", BoardAutomationRules.ActionBlocks);

        var tokensHeading = Ui.Heading("Words that fill themselves in");
        tokensHeading.Margin = new Thickness(0, 14, 0, 6);
        panel.Children.Add(tokensHeading);
        var tokens = new WrapPanel();
        foreach (var (token, what) in BoardAutomationRules.Tokens)
        {
            // Not focusable, so clicking one leaves the cursor in the box it's meant for.
            var chip = new Button
            {
                Content = token,
                Focusable = false,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = $"{what} - click to put it into the title, comment or message you were typing in",
            };
            chip.Click += (_, _) => InsertToken(token);
            tokens.Children.Add(chip);
        }
        panel.Children.Add(tokens);
        return panel;
    }

    private Border PaletteBlock(AutomationBlock block)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = block.Label, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap });
        if (block.Hint is not null) text.Children.Add(new TextBlock { Text = block.Hint, FontSize = 11, Foreground = Palette.SecondaryText, TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        var icon = new TextBlock { Text = block.Icon, Width = 26, FontSize = 14, Foreground = ZoneBrush(block.Zone), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var key = $"{block.Zone}|{block.Type}";
        var border = new Border
        {
            Background = Ui.Brush("CardBackground"),
            BorderBrush = Ui.Brush("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 5),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = "Drag into the rule, or click",
            Child = row,
        };
        border.MouseEnter += (_, _) => border.Background = Ui.Brush("CardHoverBackground");
        border.MouseLeave += (_, _) => border.Background = Ui.Brush("CardBackground");
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _pressed = key;
        };
        border.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressed != key || !Dragged(e)) return;
            _pressed = null;
            DragDrop.DoDragDrop(border, new DataObject(BlockFormat, key), DragDropEffects.Copy);
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (_pressed != key) return;
            _pressed = null;
            AddBlock(block.Zone, block.Type, null);
        };
        border.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            AddBlock(block.Zone, block.Type, null);
            e.Handled = true;
        };
        return border;
    }

    private bool Dragged(MouseEventArgs e)
    {
        var moved = e.GetPosition(this) - _dragStart;
        return Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance
               || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    private void InsertToken(string token)
    {
        var box = _lastTokenBox is not null && _tokenBoxes.Contains(_lastTokenBox) ? _lastTokenBox : _tokenBoxes.FirstOrDefault();
        if (box is null)
        {
            ShowErrors(new[] { "Add a card title or comment first, then drop the word into it." });
            return;
        }
        var (text, caret) = BoardAutomationRules.InsertToken(box.Text, box.SelectionStart, box.SelectionLength, token);
        box.Text = text;
        box.CaretIndex = caret;
        box.Focus();
    }

    // ---- the rule ---------------------------------------------------------------------------------

    private void AddBlock(string zone, string type, int? at)
    {
        if (_draft is null) return;
        string key;
        if (zone == "trigger")
        {
            _draft.Trigger = BoardAutomationRules.MakeTrigger(type, _context);
            if (_draft.Name.Trim().Length == 0 && BoardAutomationRules.Block(zone, type) is { } block) _name.Text = block.Label;
            key = "trigger|0";
        }
        else if (zone == "conditions")
        {
            var index = Math.Clamp(at ?? _draft.Conditions.Count, 0, _draft.Conditions.Count);
            _draft.Conditions.Insert(index, BoardAutomationRules.MakeCondition(type, _context));
            key = $"conditions|{index}";
        }
        else
        {
            var index = Math.Clamp(at ?? _draft.Actions.Count, 0, _draft.Actions.Count);
            _draft.Actions.Insert(index, BoardAutomationRules.MakeAction(type, _context));
            key = $"actions|{index}";
        }
        RenderCanvas();
        // Straight into the new block's first box, as the dashboard does.
        if (_placed.TryGetValue(key, out var placed))
        {
            placed.BringIntoView();
            if (FindFirst<TextBox>(placed) is { } first) Dispatcher.BeginInvoke(() => first.Focus(), DispatcherPriority.Input);
        }
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T hit) return hit;
            if (FindFirst<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void RenderCanvas()
    {
        if (_draft is not { } draft) return;
        _canvas.Children.Clear();
        _tokenBoxes.Clear();
        _placed.Clear();
        _next = null;

        var trigger = new StackPanel();
        if (draft.Trigger is { } t) trigger.Children.Add(Placed("trigger", 0, 1, BoardAutomationRules.Block("trigger", t.Type), TriggerBody(t), null));
        else trigger.Children.Add(Placeholder("Drop a WHEN block here - what starts the rule?"));
        _canvas.Children.Add(Slot("When", null, "trigger", trigger));

        var conditions = new StackPanel();
        for (var i = 0; i < draft.Conditions.Count; i++)
        {
            var condition = draft.Conditions[i];
            conditions.Children.Add(Placed("conditions", i, draft.Conditions.Count, BoardAutomationRules.Block("conditions", condition.Type),
                ConditionBody(condition), BoardAutomationRules.ConditionWarning(draft, i)));
        }
        conditions.Children.Add(Placeholder(draft.Conditions.Count > 0 ? "Drop another to narrow it further" : "Drop ONLY IF blocks here to narrow it down"));
        _canvas.Children.Add(Slot("Only if", "(optional)", "conditions", conditions));

        var actions = new StackPanel();
        for (var i = 0; i < draft.Actions.Count; i++)
        {
            var action = draft.Actions[i];
            actions.Children.Add(Placed("actions", i, draft.Actions.Count, BoardAutomationRules.Block("actions", action.Type),
                ActionBody(action), BoardAutomationRules.ActionWarning(draft, i)));
        }
        actions.Children.Add(Placeholder(draft.Actions.Count > 0 ? "Drop another step here" : "Drop THEN blocks here - what should happen?"));
        _canvas.Children.Add(Slot("Then", "(in this order - drag to reorder)", "actions", actions));

        RefreshSentence();
        QueuePreview();
    }

    private static TextBlock Placeholder(string text)
    {
        var caption = Ui.Caption(text);
        caption.Margin = new Thickness(4, 6, 4, 6);
        return caption;
    }

    private void RefreshSentence()
    {
        if (_draft is not null) _sentence.Text = BoardAutomationRules.DraftSentence(_draft, _context);
    }

    private StackPanel Slot(string title, string? note, string zone, StackPanel items)
    {
        var header = new TextBlock { Margin = new Thickness(0, 12, 0, 6) };
        header.Inlines.Add(new System.Windows.Documents.Run(title.ToUpperInvariant()) { Foreground = ZoneBrush(zone), FontWeight = FontWeights.SemiBold });
        if (note is not null) header.Inlines.Add(new System.Windows.Documents.Run("  " + note) { Foreground = Palette.SecondaryText, FontSize = 11 });

        var drop = new Border
        {
            BorderBrush = Ui.Brush("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Ui.Brush("SidebarBackground"),
            Padding = new Thickness(8),
            MinHeight = 48,
            AllowDrop = true,
            Child = items,
        };
        // Preview (tunnelling) events, so a text box inside a block doesn't claim a block dropped on it.
        drop.PreviewDragOver += (_, e) =>
        {
            if (DragFor(e, zone) is null) return;
            e.Effects = DragFor(e, zone)!.Value.From is null ? DragDropEffects.Copy : DragDropEffects.Move;
            drop.BorderBrush = ZoneBrush(zone);
            e.Handled = true;
        };
        drop.PreviewDragEnter += (_, e) =>
        {
            if (DragFor(e, zone) is null) return;
            drop.BorderBrush = ZoneBrush(zone);
            e.Handled = true;
        };
        drop.DragLeave += (_, _) => drop.BorderBrush = Ui.Brush("CardBorder");
        drop.PreviewDrop += (_, e) =>
        {
            drop.BorderBrush = Ui.Brush("CardBorder");
            if (DragFor(e, zone) is not { } drag || _draft is null) return;
            e.Handled = true;
            var at = InsertIndex(items, e);
            if (drag.From is { } from)
            {
                BoardAutomationRules.MoveAction(_draft.Actions, from, at);
                RenderCanvas();
            }
            else AddBlock(zone, drag.Type, at);
        };

        var slot = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        slot.Children.Add(header);
        slot.Children.Add(drop);
        return slot;
    }

    /// <summary>What's being dragged, when it belongs in <paramref name="zone"/>: a palette block
    /// ("actions|createCard"), or a placed action being reordered ("actions|@2").</summary>
    private static (string Type, int? From)? DragFor(DragEventArgs e, string zone)
    {
        if (e.Data.GetData(BlockFormat) is not string data) return null;
        var parts = data.Split('|', 2);
        if (parts.Length != 2 || parts[0] != zone) return null;
        if (parts[1].StartsWith('@'))
        {
            return int.TryParse(parts[1][1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) ? (parts[1], from) : null;
        }
        return (parts[1], null);
    }

    // Before the first placed block whose middle is below the pointer.
    private static int InsertIndex(StackPanel items, DragEventArgs e)
    {
        var placed = items.Children.OfType<Border>().Where(child => child.Tag is int).ToList();
        for (var i = 0; i < placed.Count; i++)
        {
            if (e.GetPosition(placed[i]).Y < placed[i].ActualHeight / 2) return i;
        }
        return placed.Count;
    }

    private Border Placed(string zone, int index, int count, AutomationBlock? block, UIElement? body, string? warning)
    {
        var movable = zone == "actions";
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        Button Mini(string glyph, string tip, Action onClick)
        {
            var button = Ui.Button(glyph, onClick, tooltip: tip);
            button.Padding = new Thickness(7, 1, 7, 1);
            button.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            return button;
        }
        head.Children.Add(Mini("✕", "Remove", () => Remove(zone, index)));
        if (movable && index < count - 1) head.Children.Add(Mini("▼", "Move down", () => MoveAction(index, index + 2)));
        if (movable && index > 0) head.Children.Add(Mini("▲", "Move up", () => MoveAction(index, index - 1)));

        var border = new Border
        {
            Background = Ui.Brush("CardBackground"),
            BorderBrush = warning is null ? ZoneBrush(zone) : Palette.Amber,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Tag = index,
        };

        if (movable)
        {
            var grip = new TextBlock
            {
                Text = "⋮⋮",
                Foreground = Palette.SecondaryText,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to reorder",
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            var key = $"actions|@{index}";
            grip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _dragStart = e.GetPosition(this);
                _pressed = key;
                e.Handled = true;
                grip.CaptureMouse();
            };
            grip.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _pressed != key || !Dragged(e)) return;
                _pressed = null;
                grip.ReleaseMouseCapture();
                border.Opacity = 0.5;
                DragDrop.DoDragDrop(border, new DataObject(BlockFormat, key), DragDropEffects.Move);
                border.Opacity = 1;
            };
            grip.MouseLeftButtonUp += (_, _) =>
            {
                _pressed = null;
                grip.ReleaseMouseCapture();
            };
            DockPanel.SetDock(grip, Dock.Left);
            head.Children.Add(grip);
        }
        var icon = new TextBlock { Text = block?.Icon ?? "?", Width = 24, FontSize = 14, Foreground = ZoneBrush(zone), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock
        {
            Text = block?.Label ?? "Something this app doesn't know yet",
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var stack = new StackPanel();
        stack.Children.Add(head);
        if (block is null) stack.Children.Add(Ui.Caption("Made on a newer device or the dashboard. It's kept as it is when you save."));
        else if (body is not null) stack.Children.Add(body);
        if (warning is not null) stack.Children.Add(Warning(warning));
        border.Child = stack;
        _placed[$"{zone}|{index}"] = border;
        return border;
    }

    private static TextBlock Warning(string text) => new()
    {
        Text = text,
        Foreground = Palette.Amber,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11.5,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private void Remove(string zone, int index)
    {
        if (_draft is null) return;
        if (zone == "trigger") _draft.Trigger = null;
        else if (zone == "conditions") _draft.Conditions.RemoveAt(index);
        else _draft.Actions.RemoveAt(index);
        RenderCanvas();
    }

    private void MoveAction(int from, int to)
    {
        if (_draft is null) return;
        BoardAutomationRules.MoveAction(_draft.Actions, from, to);
        RenderCanvas();
    }

    // ---- blanks to fill in ------------------------------------------------------------------------

    /// <summary>One line of a block: words and controls flowing together, as a sentence.</summary>
    private static WrapPanel Line(params object?[] parts)
    {
        var line = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var part in parts)
        {
            FrameworkElement? element = part switch
            {
                string text when text.Length > 0 => new TextBlock { Text = text, Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center },
                FrameworkElement control => control,
                _ => null,
            };
            if (element is null) continue;
            element.Margin = new Thickness(0, 0, 6, 4);
            element.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(element);
        }
        return line;
    }

    private FrameworkElement TextField(string? value, Action<string> set, string placeholder, bool tokens = false, double width = 260, bool multiline = false)
    {
        var box = new TextBox { Text = value ?? "", MinWidth = width, MaxWidth = 520 };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 56;
        }
        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            RefreshSentence();
        };
        if (tokens)
        {
            _tokenBoxes.Add(box);
            box.GotKeyboardFocus += (_, _) => _lastTokenBox = box;
        }
        return Ui.WithPlaceholder(box, placeholder);
    }

    private FrameworkElement ListField(IEnumerable<string>? value, Action<List<string>> set, string placeholder, bool lines)
    {
        var box = new TextBox { Text = string.Join(lines ? "\n" : ", ", value ?? Enumerable.Empty<string>()), MinWidth = 260, MaxWidth = 520 };
        if (lines)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 56;
        }
        box.TextChanged += (_, _) =>
        {
            set(lines ? BoardAutomationRules.Lines(box.Text) : BoardAutomationRules.Csv(box.Text));
            RefreshSentence();
        };
        return Ui.WithPlaceholder(box, placeholder);
    }

    /// <summary>A label name: typed, or picked from the ones already on this board's cards.</summary>
    private FrameworkElement LabelField(string? value, Action<string> set, string placeholder)
    {
        var box = new TextBox { Text = value ?? "", MinWidth = 160 };
        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            RefreshSentence();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.WithPlaceholder(box, placeholder));
        var known = KnownLabels();
        if (known.Count > 0)
        {
            var menu = new ContextMenu();
            foreach (var name in known)
            {
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => box.Text = name;
                menu.Items.Add(item);
            }
            var pick = Ui.Button("▾", () => { }, tooltip: "Labels already on this board");
            pick.Padding = new Thickness(8, 4, 8, 4);
            pick.Margin = new Thickness(4, 0, 0, 0);
            pick.ContextMenu = menu;
            pick.Click += (_, _) =>
            {
                menu.PlacementTarget = pick;
                menu.IsOpen = true;
            };
            row.Children.Add(pick);
        }
        return row;
    }

    private ComboBox Choice<T>(IEnumerable<(string Label, T Value)> options, T current, Action<T> set, bool structural = false)
    {
        var picker = new ComboBox { MinWidth = 120 };
        foreach (var (label, value) in options) picker.Items.Add(Ui.Option(label, value));
        Ui.Select(picker, current);
        picker.SelectionChanged += (_, _) =>
        {
            if ((picker.SelectedItem as ComboBoxItem)?.Tag is not T value) return;
            set(value);
            if (structural) RenderCanvas();
            else RefreshSentence();
        };
        return picker;
    }

    /// <summary>A list picker: this board's lists, then - where a card can be sent further - other
    /// boards' lists underneath. "any list" where the block can mean anywhere.</summary>
    private ComboBox ColumnField(string? selected, bool allowAny, Action<string> set)
    {
        var picker = new ComboBox { MinWidth = 170 };
        if (allowAny) picker.Items.Add(Ui.Option("any list", ""));
        else if (string.IsNullOrEmpty(selected)) picker.Items.Add(Ui.Option("pick a list", ""));
        foreach (var column in Columns(_board.Id)) picker.Items.Add(Ui.Option(column.Title.Length == 0 ? "list" : column.Title, column.Id));
        if (!allowAny)
        {
            foreach (var other in OtherBoards())
            {
                var columns = Columns(other.Id);
                if (columns.Count == 0) continue;
                picker.Items.Add(new ComboBoxItem { Content = $"on {(other.Title.Length == 0 ? "board" : other.Title)}", IsEnabled = false, FontStyle = FontStyles.Italic });
                foreach (var column in columns) picker.Items.Add(Ui.Option("    " + (column.Title.Length == 0 ? "list" : column.Title), column.Id));
            }
        }
        // A list that's since been deleted stays chosen, so saving says so rather than quietly
        // moving the rule to another.
        if (!string.IsNullOrEmpty(selected) && picker.Items.OfType<ComboBoxItem>().All(item => !Equals(item.Tag, selected)))
        {
            picker.Items.Add(Ui.Option("(a list that's gone)", selected));
        }
        Ui.Select(picker, selected ?? "");
        picker.SelectionChanged += (_, _) =>
        {
            if (Ui.SelectedTag(picker) is not { } id) return;
            set(id);
            RefreshSentence();
        };
        return picker;
    }

    private CheckBox Check(string label, bool value, Action<bool> set, bool structural = false)
    {
        var box = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            if (structural) RenderCanvas();
            else RefreshSentence();
        };
        return box;
    }

    /// <summary>A time of day, typed the way the person likes - "7:00 AM", "19:00" - and kept as
    /// "HH:mm". Something that isn't a time is sent as it is, for the server to say so.</summary>
    private TextBox TimeField(string? value, Action<string> set, bool optional, Action? changed = null)
    {
        var box = new TextBox
        {
            Width = 96,
            Text = string.IsNullOrEmpty(value) ? "" : BoardAutomationRules.Time12(value, CultureInfo.CurrentCulture),
            ToolTip = optional ? "A time, for example 5:00 PM - leave it empty for no time" : "A time, for example 7:00 AM or 19:00",
        };
        var normal = box.BorderBrush;
        box.TextChanged += (_, _) =>
        {
            var parsed = BoardAutomationRules.ParseTime(box.Text, CultureInfo.CurrentCulture);
            var empty = box.Text.Trim().Length == 0;
            box.BorderBrush = parsed is null && !(empty && optional) ? Palette.Red : normal;
            set(parsed ?? (empty ? "" : box.Text.Trim()));
            RefreshSentence();
            changed?.Invoke();
        };
        return box;
    }

    private WrapPanel DaysPicker(List<int>? days, Action<List<int>> set)
    {
        var chips = new WrapPanel();
        for (var day = 0; day < 7; day++)
        {
            var on = days?.Contains(day) == true;
            var chip = DayChip(BoardAutomationRules.DayShort[day], on);
            var which = day;
            chip.Click += (_, _) =>
            {
                // Unticking the last day would silently mean "every day"; keep at least one.
                if (BoardAutomationRules.ToggleDay(days, which) is not { } next) return;
                set(next);
                RenderCanvas();
            };
            chips.Children.Add(chip);
        }
        return chips;
    }

    private static Button DayChip(string text, bool on, bool enabled = true, string? tooltip = null)
    {
        var chip = new Button
        {
            Content = text,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 4, 4),
            IsEnabled = enabled,
            ToolTip = tooltip,
        };
        if (on)
        {
            chip.Background = Ui.Brush("GreenDim");
            chip.BorderBrush = Palette.Green;
            chip.Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x12, 0x0A));
        }
        return chip;
    }

    private ComboBox PersonField(string? selected, bool allowNone, Action<string> set)
    {
        var options = new List<(string, string)>();
        if (allowNone) options.Add(("nobody", ""));
        options.AddRange(_people.Select(person => (person.Label, person.Username)));
        // Someone picked on another device who isn't listed here stays picked.
        if (!string.IsNullOrEmpty(selected) && options.All(option => option.Item2 != selected)) options.Add((selected, selected));
        return Choice(options, selected ?? "", value => set(value));
    }

    /// <summary>Chips for who a notification or email goes to. A notification has an "everyone"
    /// chip; for email, people with no address on their account are shown but can't be picked.</summary>
    private WrapPanel PeoplePicker(AutomationAction action, bool allowEveryone)
    {
        var chips = new WrapPanel();
        if (allowEveryone)
        {
            var everyone = DayChip("everyone", action.Everyone, _people.Count > 0, _people.Count > 0 ? null : "With login off, every phone gets it");
            everyone.Click += (_, _) =>
            {
                action.Everyone = !action.Everyone;
                if (action.Everyone) action.People = new List<string>();
                RenderCanvas();
            };
            chips.Children.Add(everyone);
        }
        foreach (var person in _people)
        {
            var noEmail = !allowEveryone && !person.HasEmail;
            var on = !action.Everyone && action.People?.Contains(person.Username) == true;
            var chip = DayChip(person.Label + (noEmail ? " (no email)" : ""), on, !noEmail,
                noEmail ? "No email address on this account - add one under Accounts" : null);
            var username = person.Username;
            chip.Click += (_, _) =>
            {
                var list = action.People ?? new List<string>();
                action.People = list.Contains(username) ? list.Where(p => p != username).ToList() : list.Append(username).ToList();
                action.Everyone = false;
                RenderCanvas();
            };
            chips.Children.Add(chip);
        }
        return chips;
    }

    private FrameworkElement DueFields(AutomationAction action) => Line(
        Choice(BoardAutomationRules.DueChoices.Select(choice => (choice.Label, choice.Days)), action.InDays ?? 1, days => action.InDays = days),
        "at",
        TimeField(action.Time, time => action.Time = time, optional: true));

    private UIElement? TriggerBody(AutomationTrigger trigger)
    {
        var body = new StackPanel();
        switch (trigger.Type)
        {
            case AutomationTypes.Schedule:
            {
                var schedule = trigger.Schedule ??= new AutomationSchedule { TimeZone = _context.TimeZone };
                var mode = BoardAutomationRules.ScheduleMode(schedule);
                var modes = BoardAutomationRules.ScheduleModes.Select(m => (m.Label, m.Mode)).ToList();
                // A repeat from a newer server stays as it is until someone picks another.
                if (modes.All(m => m.Mode != mode)) modes.Add(($"({mode})", mode));
                var picker = Choice(modes, mode, chosen =>
                {
                    BoardAutomationRules.ApplyScheduleMode(schedule, chosen, DateTime.Today);
                }, structural: true);
                object? extra = null;
                switch (mode)
                {
                    case "days":
                        extra = DaysPicker(schedule.Days, days => schedule.Days = days);
                        break;
                    case "monthly":
                        var dayOptions = Enumerable.Range(1, 31).Select(day => ($"day {day}", day)).Append(("the last day", -1));
                        extra = Line("on", Choice(dayOptions, schedule.DayOfMonth is null or 0 ? -1 : schedule.DayOfMonth.Value, day =>
                        {
                            schedule.DayOfMonth = day;
                            QueuePreview();
                        }));
                        break;
                    case "hourly":
                        extra = Line("every", Choice(BoardAutomationRules.HourSteps.Select(n => ($"{n} hour{(n == 1 ? "" : "s")}", n)),
                            schedule.EveryHours is null or 0 ? 1 : schedule.EveryHours.Value, n =>
                            {
                                schedule.EveryHours = n;
                                QueuePreview();
                            }), "starting");
                        break;
                    case "once":
                        var date = new DatePicker { Width = 150 };
                        if (DateTime.TryParseExact(schedule.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) date.SelectedDate = day;
                        date.SelectedDateChanged += (_, _) =>
                        {
                            schedule.Date = date.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            RefreshSentence();
                            QueuePreview();
                        };
                        extra = Line("on", date);
                        break;
                }
                body.Children.Add(Line(picker, extra, "at", TimeField(schedule.Time, time => schedule.Time = time, optional: false, QueuePreview)));
                _next = new TextBlock { FontSize = 11.5, Foreground = Palette.SecondaryText, TextWrapping = TextWrapping.Wrap };
                body.Children.Add(_next);
                return body;
            }
            case "cardAdded":
                return Line("to", ColumnField(trigger.ColumnId, true, id => trigger.ColumnId = id));
            case "cardMoved":
                return Line("into", ColumnField(trigger.ColumnId, true, id => trigger.ColumnId = id));
            case "cardCompleted":
            case "checklistCompleted":
                return null;
            case "cardDue":
            {
                var offsets = BoardAutomationRules.DueOffsets.Select(o => (o.Label, o.Minutes)).ToList();
                var minutes = trigger.MinutesBefore ?? 0;
                if (offsets.All(o => o.Minutes != minutes)) offsets.Add(($"{minutes} minutes before", minutes));
                body.Children.Add(Line(
                    Choice(offsets, minutes, value => trigger.MinutesBefore = value),
                    "for cards in",
                    ColumnField(trigger.ColumnId, true, id => trigger.ColumnId = id)));
                body.Children.Add(Ui.Caption("A card with a date but no time counts as due at 9:00 AM that day. Finished cards are left alone."));
                return body;
            }
            case "labelAdded":
                return Line("called", LabelField(trigger.Label, text => trigger.Label = text, "any label"));
            default:
                return null;
        }
    }

    private UIElement? ConditionBody(AutomationCondition condition) => condition.Type switch
    {
        "titleContains" => Line("the card's title contains", TextField(condition.Text, text => condition.Text = text, "e.g. asap", width: 180)),
        "hasLabel" => Line("the card has the label", LabelField(condition.Text, text => condition.Text = text, "label")),
        "lacksLabel" => Line("the card does not have the label", LabelField(condition.Text, text => condition.Text = text, "label")),
        "inColumn" => Line("the card is in", ColumnField(condition.ColumnId, false, id => condition.ColumnId = id)),
        "onDays" => Line("today is", DaysPicker(condition.Days, days => condition.Days = days)),
        _ => null,
    };

    private UIElement? ActionBody(AutomationAction action)
    {
        var body = new StackPanel();
        switch (action.Type)
        {
            case "createCard":
                body.Children.Add(Line("called", TextField(action.Title, text => action.Title = text, "e.g. pick up eggs", tokens: true)));
                body.Children.Add(Line(
                    "in", ColumnField(action.ColumnId, false, id => action.ColumnId = id),
                    "at the", Choice(new[] { ("bottom", "bottom"), ("top", "top") }, action.Position == "top" ? "top" : "bottom", value => action.Position = value)));
                body.Children.Add(Line(Check("skip if an unfinished card with this title is already there", action.SkipIfExists, on => action.SkipIfExists = on)));
                body.Children.Add(Line(
                    Check("due", action.InDays is not null, on =>
                    {
                        action.InDays = on ? 1 : null;
                        if (!on) action.Time = "";
                    }, structural: true),
                    action.InDays is not null ? DueFields(action) : null));
                body.Children.Add(Line("labels", ListField(action.Labels, list => action.Labels = list, "comma separated", lines: false)));
                body.Children.Add(Line("checklist", ListField(action.Items, list => action.Items = list, "one item per line", lines: true)));
                if (_people.Count > 0) body.Children.Add(Line("assign to", PersonField(action.Username, true, name => action.Username = name)));
                return body;
            case "moveCard":
                return Line(
                    "to", ColumnField(action.ColumnId, false, id => action.ColumnId = id),
                    "at the", Choice(new[] { ("top", "top"), ("bottom", "bottom") }, action.Position == "bottom" ? "bottom" : "top", value => action.Position = value));
            case "addLabel":
            {
                var colors = Ui.Colors.Select(c => (c.Name, c.Hex ?? "")).ToList();
                if (!string.IsNullOrEmpty(action.Color) && colors.All(c => !string.Equals(c.Item2, action.Color, StringComparison.OrdinalIgnoreCase)))
                {
                    colors.Add(("Custom", action.Color));
                }
                var current = colors.FirstOrDefault(c => string.Equals(c.Item2, action.Color ?? "", StringComparison.OrdinalIgnoreCase)).Item2 ?? "";
                return Line(LabelField(action.Name, text => action.Name = text, "label"), "colour", Choice(colors, current, hex => action.Color = hex));
            }
            case "removeLabel":
                return Line(LabelField(action.Name, text => action.Name = text, "label"));
            case "setDue":
                return Line(
                    Check("set one (untick to clear it)", action.InDays is not null, on =>
                    {
                        action.InDays = on ? 1 : null;
                        if (!on) action.Time = "";
                    }, structural: true),
                    action.InDays is not null ? DueFields(action) : null);
            case "complete":
                return Line("as", Choice(new[] { ("done", true), ("not done", false) }, action.Done, done => action.Done = done));
            case "addChecklist":
                return Line(ListField(action.Items, list => action.Items = list, "one item per line", lines: true));
            case "resetChecklist":
                return Line("so it can be done again");
            case "addComment":
                return Line(TextField(action.Text, text => action.Text = text, "e.g. finished on {date}", tokens: true, width: 320));
            case "assign":
                return Line(PersonField(action.Username, false, name => action.Username = name));
            case "deleteCard":
                return Line("for good");
            case "notify":
                if (!_pushReady) body.Children.Add(Warning("Push notifications aren't set up on this server yet (Settings → Push), so this won't reach anyone until they are."));
                body.Children.Add(Line("to", PeoplePicker(action, allowEveryone: true)));
                body.Children.Add(Line("title", TextField(action.Title, text => action.Title = text, "optional - the board's name if empty", tokens: true)));
                body.Children.Add(Line("message", TextField(action.Text, text => action.Text = text, "e.g. {card} is due {due}", tokens: true, width: 320)));
                return body;
            case "email":
                if (!_emailReady) body.Children.Add(Warning("Email isn't set up on this server yet (Settings → Email), so nothing will be sent until it is."));
                if (_people.Count > 0) body.Children.Add(Line("to", PeoplePicker(action, allowEveryone: false)));
                body.Children.Add(Line(_people.Count > 0 ? "and" : "to",
                    ListField(action.Addresses, list => action.Addresses = list, "other addresses, comma separated", lines: false)));
                body.Children.Add(Line("subject", TextField(action.Title, text => action.Title = text, "optional", tokens: true)));
                body.Children.Add(Line("message", TextField(action.Text, text => action.Text = text, "e.g. {card} is due {due}", tokens: true, width: 320, multiline: true)));
                return body;
            case "moveAllCards":
                body.Children.Add(Line(
                    "from", ColumnField(action.ColumnId, false, id => action.ColumnId = id),
                    "to", ColumnField(action.ToColumnId, false, id => action.ToColumnId = id)));
                body.Children.Add(Line(Check("only the ones marked done", action.OnlyCompleted, on => action.OnlyCompleted = on)));
                return body;
            case "deleteAllCards":
                body.Children.Add(Line(ColumnField(action.ColumnId, false, id => action.ColumnId = id)));
                body.Children.Add(Line(Check("only the cards marked done", action.OnlyCompleted, on => action.OnlyCompleted = on)));
                return body;
            default:
                return null;
        }
    }

    // ---- preview and save -------------------------------------------------------------------------

    // The next few times it will run, from the server, so they're what will really happen.
    private void QueuePreview()
    {
        _previewTimer.Stop();
        if (_draft?.Trigger is { Type: AutomationTypes.Schedule, Schedule: not null }) _previewTimer.Start();
    }

    private async Task PreviewAsync()
    {
        if (_draft?.Trigger?.Schedule is not { } schedule || _next is not { } target) return;
        var round = ++_previewRound;
        try
        {
            var runs = await _engine.Automations.PreviewAsync(schedule);
            if (round != _previewRound) return;
            target.Text = runs.Count > 0 ? "next: " + string.Join("  ·  ", runs.Select(When)) : "this won't run - that time has passed";
            target.Foreground = runs.Count > 0 ? Palette.SecondaryText : Palette.Red;
        }
        catch (Exception)
        {
            if (round == _previewRound) target.Text = "";
        }
    }

    private void ShowErrors(IEnumerable<string> errors)
    {
        _errors.Children.Clear();
        foreach (var error in errors)
        {
            _errors.Children.Add(new TextBlock { Text = "• " + error, Foreground = Palette.Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }
    }

    private async Task SaveAsync()
    {
        if (_draft is null) return;
        _draft.Name = _name.Text.Trim();
        var problems = BoardAutomationRules.CheckBeforeSave(_draft);
        if (problems.Count > 0)
        {
            ShowErrors(problems);
            return;
        }
        try
        {
            await _engine.Automations.SaveAsync(_draft, BoardAutomationRules.DeviceTimeZone());
            Notice("Rule saved.", true);
            _draft = null;
            ShowList();
            await LoadAsync();
        }
        catch (BoardAutomationException error)
        {
            ShowErrors(error.Errors);
        }
        catch (Exception error)
        {
            ShowErrors(new[] { Message(error) });
        }
    }
}
