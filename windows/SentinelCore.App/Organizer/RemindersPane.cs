using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Reminders, filtered the way Apple's Reminders app does it - today, scheduled, flagged, all,
/// done - with steps shown under the reminder they belong to.
/// </summary>
public sealed class RemindersPane : OrganizerPane
{
    private enum Filter { Today, Scheduled, Flagged, All, Completed }

    private readonly ListBox _filters = new() { Width = 190, Margin = new Thickness(0, 0, 16, 0) };
    private readonly StackPanel _list = new();
    private readonly TextBox _quickAdd = new() { FontSize = 14 };
    // Who the quick-add reminder is for, when there's more than one space; the choice is kept
    // while this screen is up.
    private readonly ComboBox _for = new()
    {
        MinWidth = 110,
        Margin = new Thickness(8, 0, 0, 0),
        Visibility = Visibility.Collapsed,
        ToolTip = "Who it's for - everyone and admins reminders also show on the wall kiosk when due",
    };
    private string? _forSpace;
    private readonly TextBlock _heading = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText, Margin = new Thickness(0, 0, 0, 10) };
    private Filter _filter = Filter.Today;
    private bool _building;
    // Built once and only their counts updated: clearing the list on every sync took keyboard
    // focus out of it.
    private readonly Dictionary<Filter, (ListBoxItem Entry, TextBlock Count)> _filterRows = new();

    public RemindersPane(OrganizerEngine engine) : base(engine)
    {
        var layout = new DockPanel();
        DockPanel.SetDock(_filters, Dock.Left);
        layout.Children.Add(_filters);
        _filters.SelectionChanged += (_, _) =>
        {
            if (_building || _filters.SelectedItem is not ListBoxItem { Tag: Filter filter }) return;
            _filter = filter;
            Refresh();
        };

        var right = new DockPanel();
        DockPanel.SetDock(_heading, Dock.Top);
        right.Children.Add(_heading);
        var add = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var automations = Ui.Button("⚡ Automations", () => new BoardAutomationsWindow(Engine, null) { Owner = Window.GetWindow(this) }.ShowDialog(),
            tooltip: "Rules that add reminders for you - every Saturday, or when the weather turns");
        automations.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(automations, Dock.Right);
        add.Children.Add(automations);
        var more = Ui.Button("Details…", () => NewReminder(_quickAdd.Text.Trim()));
        more.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(more, Dock.Right);
        add.Children.Add(more);
        DockPanel.SetDock(_for, Dock.Right);
        add.Children.Add(_for);
        _for.SelectionChanged += (_, _) =>
        {
            if (!_building && Ui.SelectedTag(_for) is { } space) _forSpace = space;
        };
        add.Children.Add(_quickAdd);
        DockPanel.SetDock(add, Dock.Top);
        right.Children.Add(add);
        _quickAdd.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _quickAdd.Text.Trim().Length == 0) return;
            QuickAdd(_quickAdd.Text.Trim());
            _quickAdd.Text = "";
            e.Handled = true;
        };
        _quickAdd.ToolTip = "Type a reminder and press Enter";
        right.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        layout.Children.Add(right);
        Content = layout;
    }

    private List<OrganizerItem> Reminders() =>
        Engine.Items(OrganizerKinds.Reminder).Where(Shows).ToList();

    private static DateTimeOffset? Due(OrganizerItem item) =>
        OrganizerDates.Parse(item.String("due"), item.Bool("allDay"), Ui.Zone);

    private bool Matches(OrganizerItem item, Filter filter)
    {
        var done = item.Bool("completed");
        var endOfToday = OrganizerDates.StartOfDay(DateTime.Today.AddDays(1), Ui.Zone);
        return filter switch
        {
            Filter.Today => !done && Due(item) is { } due && due < endOfToday,
            Filter.Scheduled => !done && Due(item) is not null,
            Filter.Flagged => !done && item.Bool("flagged"),
            Filter.All => !done,
            _ => done,
        };
    }

    public override void Refresh()
    {
        var all = Reminders();
        // Steps are listed under their reminder, not on their own.
        var topLevel = all.Where(item => item.ParentId is null || Engine.Item(item.ParentId) is not { Kind: OrganizerKinds.Reminder }).ToList();

        _building = true;
        if (_filterRows.Count == 0) BuildFilters();
        foreach (var (filter, row) in _filterRows)
        {
            row.Count.Text = topLevel.Count(item => Matches(item, filter)).ToString();
            if (filter == _filter && _filters.SelectedItem != row.Entry) _filters.SelectedItem = row.Entry;
        }
        BuildFor();
        _building = false;
        BuildList(topLevel);
    }

    // The "for" picker: just me / everyone / admins, rebuilt only when the spaces change.
    private void BuildFor()
    {
        var spaces = Engine.VisibleSpaces;
        _for.Visibility = spaces.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var wanted = spaces.Select(space => (BoardAutomationRules.AudienceName(space), space.Id)).ToList();
        var listed = _for.Items.OfType<ComboBoxItem>().Select(item => ((string)item.Content, (string)item.Tag)).ToList();
        if (!listed.SequenceEqual(wanted))
        {
            _for.Items.Clear();
            foreach (var (label, id) in wanted) _for.Items.Add(Ui.Option(label, id));
        }
        var selected = _forSpace is not null && spaces.Any(space => space.Id == _forSpace) ? _forSpace : NewItemSpace;
        if (Ui.SelectedTag(_for) != selected) Ui.Select(_for, selected);
    }

    // Where a new reminder goes: who the picker says, else the space being shown or this person's own.
    private string QuickAddSpace => _for.Visibility == Visibility.Visible && Ui.SelectedTag(_for) is { } space ? space : NewItemSpace;

    private void BuildFilters()
    {
        foreach (var (filter, label, glyph) in new[]
                 {
                     (Filter.Today, "Today", ""), (Filter.Scheduled, "Scheduled", ""), (Filter.Flagged, "Flagged", ""),
                     (Filter.All, "All", ""), (Filter.Completed, "Completed", ""),
                 })
        {
            var row = new DockPanel();
            var count = new TextBlock { Foreground = Palette.SecondaryText };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(Ui.Glyph(glyph, filter == Filter.Flagged ? Palette.Orange : Palette.Green, 13));
            name.Children.Add(new TextBlock { Text = label, Margin = new Thickness(10, 0, 0, 0), Foreground = Palette.PrimaryText });
            row.Children.Add(name);
            var entry = new ListBoxItem { Content = row, Tag = filter, Padding = new Thickness(8, 7, 8, 7) };
            _filters.Items.Add(entry);
            _filterRows[filter] = (entry, count);
        }
    }

    private void BuildList(List<OrganizerItem> topLevel)
    {
        _heading.Text = _filter switch
        {
            Filter.Today => "Today",
            Filter.Scheduled => "Scheduled",
            Filter.Flagged => "Flagged",
            Filter.All => "All reminders",
            _ => "Completed",
        };

        _list.Children.Clear();
        var shown = topLevel.Where(item => Matches(item, _filter));
        shown = _filter == Filter.Completed
            ? shown.OrderByDescending(item => OrganizerJson.Instant(item.String("completedAt")) ?? item.UpdatedAt)
            : shown.OrderBy(item => Due(item) ?? DateTimeOffset.MaxValue)
                .ThenByDescending(item => item.Number("priority") ?? 0)
                .ThenBy(item => item.SortPosition);
        var rows = shown.Take(500).ToList();
        if (rows.Count == 0)
        {
            _list.Children.Add(Ui.Caption(_filter switch
            {
                Filter.Today => "Nothing due today.",
                Filter.Completed => "Nothing finished yet.",
                _ => "No reminders here.",
            }));
        }
        foreach (var item in rows)
        {
            _list.Children.Add(Row(item, indent: 0));
            foreach (var step in Engine.Children(item.Id, OrganizerKinds.Reminder)
                         .Where(step => _filter == Filter.Completed || !step.Bool("completed"))
                         .OrderBy(step => step.SortPosition))
            {
                _list.Children.Add(Row(step, indent: 1));
            }
        }
    }

    private Border Row(OrganizerItem item, int indent)
    {
        var done = item.Bool("completed");
        var check = new CheckBox { IsChecked = done, VerticalAlignment = VerticalAlignment.Center, ToolTip = done ? "Mark not done" : "Mark done" };
        check.Click += (_, _) => Toggle(item);

        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        var title = new TextBlock
        {
            Text = (item.Number("priority") is { } priority and > 0 ? new string('!', (int)priority) + " " : "") + item.Title,
            Foreground = done ? Palette.SecondaryText : Palette.PrimaryText,
            TextDecorations = done ? TextDecorations.Strikethrough : null,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 14,
        };
        text.Children.Add(title);

        var details = new List<string>();
        if (Due(item) is { } due)
        {
            var overdue = !done && due < DateTimeOffset.Now;
            var when = Ui.When(due, item.Bool("allDay"));
            if (overdue && item.Bool("allDay") && OrganizerDates.InZone(due, Ui.Zone).Date == DateTime.Today) overdue = false;
            details.Add(overdue ? $"Overdue · {when}" : when);
            if (overdue) title.Foreground = Palette.Red;
        }
        if (item.String("recurrence") is { Length: > 0 }) details.Add("repeats");
        if (SpaceFilter is null) details.Add(Ui.SpaceLabel(Engine, item.SpaceId));
        var steps = Engine.Children(item.Id, OrganizerKinds.Reminder);
        if (steps.Count > 0) details.Add($"{steps.Count(step => step.Bool("completed"))}/{steps.Count} steps");
        var files = Engine.Children(item.Id, OrganizerKinds.Attachment).Count;
        if (files > 0) details.Add($"{files} attachment{(files == 1 ? "" : "s")}");
        if (item.String("notes") is { Length: > 0 } notes) details.Add(notes.Split('\n')[0]);
        if (details.Count > 0) text.Children.Add(Ui.Caption(string.Join(" · ", details)));

        var row = new DockPanel();
        if (item.Bool("flagged"))
        {
            var flag = Ui.Glyph("", Palette.Orange);
            DockPanel.SetDock(flag, Dock.Right);
            row.Children.Add(flag);
        }
        if (indent == 0)
        {
            var addStep = Ui.Icon("", "Add a step", () => AddStep(item));
            addStep.Margin = new Thickness(8, 0, 0, 0);
            addStep.Opacity = 0.7;
            DockPanel.SetDock(addStep, Dock.Right);
            row.Children.Add(addStep);
        }
        row.Children.Add(check);
        row.Children.Add(text);

        var border = new Border
        {
            Background = Ui.Brush("CardBackground"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 10, 8),
            Margin = new Thickness(indent * 32, 0, 0, 6),
            Child = row,
            Cursor = Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside<ButtonBase>(source)) return;
            new ItemEditorWindow(Engine, OrganizerKinds.Reminder, item).ShowDialog();
        };
        return border;
    }

    private static bool IsInside<T>(DependencyObject source) where T : DependencyObject
    {
        // VisualTreeHelper throws for anything that isn't a Visual - a Run inside a TextBlock can be
        // what was clicked - so those go up the logical tree instead.
        for (var current = source; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T) return true;
        }
        return false;
    }

    /// <summary>
    /// Ticks a reminder off, or back on. A repeating one moves to its next time and stays open -
    /// the same rule as the dashboard and the phones.
    /// </summary>
    private void Toggle(OrganizerItem item)
    {
        if (!item.Bool("completed") && Recurrence.NextDue(item, Ui.Zone) is { } next)
        {
            Engine.Update(item.Id, "due", next);
            return;
        }
        var done = !item.Bool("completed");
        Engine.Update(item.Id, new Dictionary<string, object?>
        {
            ["completed"] = done,
            ["completedAt"] = done ? OrganizerDates.Timestamp() : null,
        });
    }

    private void QuickAdd(string title)
    {
        var fields = new Dictionary<string, object?> { ["title"] = title };
        // Added while looking at Today, it's for today.
        if (_filter == Filter.Today)
        {
            fields["due"] = OrganizerDates.Ymd(OrganizerDates.StartOfDay(DateTime.Today, Ui.Zone));
            fields["allDay"] = true;
        }
        if (_filter == Filter.Flagged) fields["flagged"] = true;
        Engine.Create(OrganizerKinds.Reminder, fields, spaceId: QuickAddSpace);
    }

    private void NewReminder(string title)
    {
        var defaults = new Dictionary<string, object?> { ["title"] = title };
        if (_filter == Filter.Today)
        {
            defaults["due"] = OrganizerDates.Ymd(OrganizerDates.StartOfDay(DateTime.Today, Ui.Zone));
            defaults["allDay"] = true;
        }
        if (new ItemEditorWindow(Engine, OrganizerKinds.Reminder, null, defaults, QuickAddSpace).ShowDialog() == true) _quickAdd.Text = "";
    }

    private void AddStep(OrganizerItem parent)
    {
        var prompt = new TextPromptWindow("New step", $"A step of \"{parent.Title}\":") { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;
        Engine.Create(OrganizerKinds.Reminder, new Dictionary<string, object?>
        {
            ["title"] = prompt.Value,
            ["position"] = (double)Engine.Children(parent.Id, OrganizerKinds.Reminder).Count,
        }, parentId: parent.Id);
    }

    public override void Reveal(OrganizerItem item)
    {
        _filter = item.Bool("completed") ? Filter.Completed : Filter.All;
        Refresh();
        new ItemEditorWindow(Engine, OrganizerKinds.Reminder, item).ShowDialog();
    }
}
