using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Creating or editing an event, a reminder or a board card. Only the fields that actually changed
/// are written, so saving without touching something never stamps it newer than another device's
/// edit to it.
/// </summary>
internal sealed class ItemEditorWindow : Window
{
    private readonly OrganizerEngine _engine;
    private readonly string _kind;
    private readonly OrganizerItem? _existing;
    private readonly string? _parentId;
    private readonly IReadOnlyDictionary<string, object?> _original;

    private readonly TextBox _title = new() { FontSize = 15 };
    private readonly ComboBox? _space;
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _location = new();
    private readonly CheckBox _allDay = new() { Content = "All day" };
    private readonly CheckBox _hasDue = new() { Content = "Due" };
    private readonly CheckBox _completed = new() { Content = "Done" };
    private readonly CheckBox _flagged = new() { Content = "Flagged" };
    private readonly DateTimeInput _start;
    private DateTimeOffset? _lastStart;
    private readonly DateTimeInput _end;
    private readonly DateTimeInput _due;
    private readonly ComboBox _repeat = new() { MinWidth = 200 };
    private readonly ComboBox _alert = new() { MinWidth = 200 };
    private readonly ComboBox _priority = new() { MinWidth = 140 };
    private readonly ComboBox _color = new() { MinWidth = 140 };
    private readonly LinksEditor _links;
    private readonly AttachmentsEditor _attachments;
    private readonly ChecklistEditor? _checklist;
    private readonly LabelsEditor? _labels;
    private readonly CommentsEditor? _comments;
    private readonly WrapPanel _assignees = new();
    private readonly TextBlock _problem = new() { Foreground = Palette.Red, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public ItemEditorWindow(
        OrganizerEngine engine,
        string kind,
        OrganizerItem? existing,
        IReadOnlyDictionary<string, object?>? defaults = null,
        string? spaceId = null,
        string? parentId = null)
    {
        _engine = engine;
        _kind = kind;
        _existing = existing;
        _parentId = existing?.ParentId ?? parentId;
        _original = existing?.Fields ?? defaults ?? new Dictionary<string, object?>();

        Style = Ui.Style("Dialog");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 640;
        Height = 720;
        MinWidth = 520;
        MinHeight = 420;
        Title = (existing is null ? "New " : "") + kind switch
        {
            OrganizerKinds.Event => "Event",
            OrganizerKinds.Reminder => "Reminder",
            _ => "Card",
        };

        var zone = Ui.Zone;
        var allDay = Bool("allDay");
        _title.Text = Str("title");
        _notes.Text = Str(kind == OrganizerKinds.Card ? "description" : "notes");
        _location.Text = Str("location");
        _allDay.IsChecked = allDay;
        _completed.IsChecked = Bool("completed");
        _flagged.IsChecked = Bool("flagged");

        var span = kind == OrganizerKinds.Event && existing is not null ? Recurrence.Span(existing, zone) : null;
        var start = span?.Start ?? OrganizerDates.Parse(Str("start"), allDay, zone);
        var end = span?.End ?? OrganizerDates.Parse(Str("end"), allDay, zone);
        // All-day ends are exclusive; people think of the last day, so that's what's shown.
        if (allDay && end is { } exclusive) end = OrganizerDates.At(exclusive.DateTime.AddDays(-1), zone);
        _start = new DateTimeInput(start, allDay);
        _end = new DateTimeInput(end, allDay);
        var due = OrganizerDates.Parse(Str("due"), allDay, zone);
        // A card's due date is a day; there's no time for it to throw away.
        _due = new DateTimeInput(due, allDay || kind == OrganizerKinds.Card);
        _hasDue.IsChecked = due is not null;
        _lastStart = start;
        _start.Changed += EndFollowsStart;

        _allDay.Click += (_, _) =>
        {
            var on = _allDay.IsChecked == true;
            _start.AllDay = _end.AllDay = on;
            if (_kind != OrganizerKinds.Card) _due.AllDay = on;
        };

        BuildRepeat(Str("recurrence"));
        BuildAlert(existing is null && kind == OrganizerKinds.Event && !_original.ContainsKey("alerts")
            ? new List<object?> { 15.0 }
            : _original.GetValueOrDefault("alerts") as IReadOnlyList<object?> ?? Array.Empty<object?>());
        foreach (var (label, value) in new[] { ("None", 0), ("Low", 1), ("Medium", 2), ("High", 3) }) _priority.Items.Add(Ui.Option(label, value));
        Ui.Select(_priority, (int)(Num("priority") ?? 0));
        foreach (var (name, hex) in Ui.Colors) _color.Items.Add(Ui.Option(name, hex));
        Ui.Select(_color, Str("color") is { Length: > 0 } color ? Ui.Colors.FirstOrDefault(c => string.Equals(c.Hex, color, StringComparison.OrdinalIgnoreCase)).Hex : null);

        _links = new LinksEditor(_original.GetValueOrDefault("links") as IReadOnlyList<object?> ?? Array.Empty<object?>());
        _attachments = new AttachmentsEditor(engine, existing?.Id);
        if (kind == OrganizerKinds.Card)
        {
            _checklist = new ChecklistEditor(_original.GetValueOrDefault("checklist") as IReadOnlyList<object?> ?? Array.Empty<object?>());
            _labels = new LabelsEditor(_original.GetValueOrDefault("labels") as IReadOnlyList<object?> ?? Array.Empty<object?>());
            // Comments hang off the card, so there's nothing to hang them on until it's saved.
            if (existing is not null)
            {
                _comments = new CommentsEditor(engine, existing.Id);
                Closed += (_, _) => _comments.Detach();
            }
        }

        // Only a top-level thing chooses its space; a card is in its board's.
        if (_parentId is null)
        {
            // A reminder says who it's for, as the Reminders screen does: everyone and admins
            // reminders also show on the wall kiosk when they're due.
            _space = Ui.SpacePicker(engine, existing?.SpaceId ?? spaceId ?? engine.DefaultSpaceId, audience: kind == OrganizerKinds.Reminder);
        }

        Content = Build();
        Loaded += (_, _) =>
        {
            _title.Focus();
            _title.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // A date typed into a DatePicker only counts once focus leaves it; move focus on so
                // what's on screen is what gets saved.
                (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Save();
                e.Handled = true;
            }
        };
        if (kind == OrganizerKinds.Card) _ = LoadPeopleAsync();
    }

    /// <summary>The id of what was saved, when something was.</summary>
    public string? SavedId { get; private set; }

    private string Str(string name) => _original.GetValueOrDefault(name) as string ?? "";

    private bool Bool(string name) => _original.GetValueOrDefault(name) is true;

    private double? Num(string name) => _original.GetValueOrDefault(name) as double?;

    private UIElement Build()
    {
        var form = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        void Row(string label, UIElement control, string? hint = null)
        {
            var caption = Ui.Caption(label);
            caption.Margin = new Thickness(0, 10, 0, 4);
            form.Children.Add(caption);
            form.Children.Add(control);
            if (hint is not null)
            {
                var text = Ui.Caption(hint);
                text.Margin = new Thickness(0, 4, 0, 0);
                form.Children.Add(text);
            }
        }
        StackPanel Line(params UIElement[] children)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var child in children)
            {
                if (child is FrameworkElement element && line.Children.Count > 0) element.Margin = new Thickness(12, 0, 0, 0);
                line.Children.Add(child);
            }
            return line;
        }

        Row("Title", _title);
        if (_space is not null) Row(_kind == OrganizerKinds.Reminder ? "For" : "Who can see it", _space);

        switch (_kind)
        {
            case OrganizerKinds.Event:
                Row("Starts", Line(_start, _allDay));
                Row("Ends", _end);
                Row("Repeat", _repeat);
                Row("Alert", _alert, "Raised by this PC while the app is running, and by each phone on its own.");
                Row("Location", _location);
                Row("Color", _color);
                Row("Notes", _notes);
                break;
            case OrganizerKinds.Reminder:
                Row("When", Line(_hasDue, _due, _allDay));
                Row("Repeat", _repeat, "Ticking off a repeating reminder moves it to the next time rather than finishing it.");
                Row("Priority", Line(_priority, _flagged, _completed));
                Row("Notes", _notes);
                break;
            default:
                Row("Description", _notes);
                Row("Due", Line(_hasDue, _due, _completed));
                Row("Labels", _labels!);
                Row("Checklist", _checklist!);
                Row("Assigned to", _assignees);
                break;
        }
        Row("Links", _links);
        Row("Pictures and files", _attachments);
        if (_kind == OrganizerKinds.Card)
        {
            // Not a Row: the comments carry their own heading and read as a feed under the card.
            if (_comments is not null)
            {
                _comments.Margin = new Thickness(0, 22, 0, 0);
                form.Children.Add(_comments);
            }
            else Row("Comments", Ui.Caption("Comments can be added once the card is saved."));
        }
        form.Children.Add(_problem);

        var footer = new DockPanel { LastChildFill = false };
        if (_existing is not null)
        {
            var delete = Ui.Button("Delete", Delete, "DangerButton");
            DockPanel.SetDock(delete, Dock.Left);
            footer.Children.Add(delete);
        }
        var save = Ui.Button(_existing is null ? "Add" : "Save", Save, "PrimaryButton", "Ctrl+Enter");
        save.MinWidth = 90;
        save.IsDefault = false;
        var cancel = Ui.Button("Cancel", Close);
        cancel.MinWidth = 90;
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(save);
        footer.Children.Add(cancel);

        var root = new DockPanel();
        var footerBorder = new Border { Style = Ui.Style("DialogFooter"), Child = footer };
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    // Moving the start carries the end along, keeping the event's length (an hour when there's
    // nothing sensible to keep), as calendar apps do.
    private void EndFollowsStart()
    {
        if (_start.Value is not { } start || start == _lastStart) return;
        var length = _lastStart is { } last && _end.Value is { } end && end >= last
            ? end - last
            : (_allDay.IsChecked == true ? TimeSpan.Zero : TimeSpan.FromHours(1));
        _end.Value = start + length;
        _lastStart = start;
    }

    private static readonly (string Label, string Rule)[] Repeats =
    {
        ("Never", ""), ("Every day", "FREQ=DAILY"), ("Every weekday", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"),
        ("Every week", "FREQ=WEEKLY"), ("Every 2 weeks", "FREQ=WEEKLY;INTERVAL=2"), ("Every month", "FREQ=MONTHLY"),
        ("Every year", "FREQ=YEARLY"),
    };

    private void BuildRepeat(string current)
    {
        foreach (var (label, rule) in Repeats) _repeat.Items.Add(Ui.Option(label, rule));
        // A rule made elsewhere that isn't one of these - "every other Tuesday until June" - is kept
        // exactly as it is unless someone picks something else.
        if (current.Length > 0 && Repeats.All(option => option.Rule != current))
        {
            _repeat.Items.Add(Ui.Option("Custom (set on another device)", current));
        }
        Ui.Select(_repeat, current);
    }

    private static readonly (string Label, double? Minutes)[] Alerts =
    {
        ("None", null), ("At the time", 0), ("5 minutes before", 5), ("15 minutes before", 15), ("30 minutes before", 30),
        ("1 hour before", 60), ("2 hours before", 120), ("1 day before", 1440), ("2 days before", 2880), ("1 week before", 10080),
    };

    private void BuildAlert(IReadOnlyList<object?> current)
    {
        foreach (var (label, minutes) in Alerts) _alert.Items.Add(Ui.Option(label, minutes));
        var values = current.OfType<double>().ToList();
        if (values.Count > 1 || (values.Count == 1 && Alerts.All(option => option.Minutes != values[0])))
        {
            _alert.Items.Add(Ui.Option("Custom (set on another device)", current));
            _alert.SelectedIndex = _alert.Items.Count - 1;
            return;
        }
        Ui.Select(_alert, values.Count == 0 ? null : values[0]);
    }

    private async Task LoadPeopleAsync()
    {
        var assigned = (_original.GetValueOrDefault("assignees") as IReadOnlyList<object?> ?? Array.Empty<object?>()).OfType<string>().ToList();
        List<OrganizerPerson> people;
        try
        {
            people = await _engine.PeopleAsync();
        }
        catch (Exception)
        {
            people = new List<OrganizerPerson>();
        }
        _comments?.SetPeople(people);
        // Someone assigned on another device stays assigned even when the list can't be fetched.
        foreach (var username in assigned.Where(name => people.All(person => person.Username != name)))
        {
            people.Add(new OrganizerPerson(username, null));
        }
        _assignees.Children.Clear();
        if (people.Count == 0)
        {
            _assignees.Children.Add(Ui.Caption("Assigning cards needs dashboard login turned on, so each person has an account."));
            return;
        }
        foreach (var person in people)
        {
            _assignees.Children.Add(new CheckBox
            {
                Content = person.Label,
                Tag = person.Username,
                IsChecked = assigned.Contains(person.Username),
                Margin = new Thickness(0, 0, 14, 6),
            });
        }
    }

    /// <summary>Everything the form says, as field values. Null clears a field.</summary>
    private Dictionary<string, object?>? Collect()
    {
        var zone = Ui.Zone;
        var allDay = _allDay.IsChecked == true;
        var fields = new Dictionary<string, object?> { ["title"] = _title.Text.Trim() };
        void Problem(string message)
        {
            _problem.Text = message;
            _problem.Visibility = Visibility.Visible;
        }
        if (_title.Text.Trim().Length == 0 && _kind != OrganizerKinds.Event)
        {
            Problem("Give it a title.");
            return null;
        }
        foreach (var input in new[] { _start, _end, _due })
        {
            if (!input.TimeIsValid)
            {
                Problem("Give each date a time this PC understands - like 14:30 or 2:30 PM - or tick All day.");
                return null;
            }
        }

        switch (_kind)
        {
            case OrganizerKinds.Event:
                if (_start.Value is not { } start)
                {
                    Problem("Pick when it starts.");
                    return null;
                }
                var end = _end.Value ?? (allDay ? start : start.AddHours(1));
                if (allDay) end = OrganizerDates.At(end.DateTime.Date.AddDays(1), zone);
                if (end <= start) end = allDay ? OrganizerDates.At(start.DateTime.Date.AddDays(1), zone) : start.AddHours(1);
                fields["allDay"] = allDay;
                fields["start"] = OrganizerDates.FieldValue(start, allDay);
                fields["end"] = OrganizerDates.FieldValue(end, allDay);
                if (!allDay) fields["timeZone"] = IanaZone();
                fields["recurrence"] = NullIfEmpty(Ui.SelectedTag(_repeat));
                fields["alerts"] = (_alert.SelectedItem as ComboBoxItem)?.Tag switch
                {
                    double minutes => new List<object?> { minutes },
                    IReadOnlyList<object?> custom => custom.ToList(),
                    _ => null,
                };
                fields["location"] = NullIfEmpty(_location.Text.Trim());
                fields["color"] = (_color.SelectedItem as ComboBoxItem)?.Tag as string;
                fields["notes"] = NullIfEmpty(_notes.Text);
                break;
            case OrganizerKinds.Reminder:
                fields["due"] = _hasDue.IsChecked == true && _due.Value is { } due ? OrganizerDates.FieldValue(due, allDay) : null;
                fields["allDay"] = allDay;
                fields["recurrence"] = NullIfEmpty(Ui.SelectedTag(_repeat));
                fields["priority"] = (double)((_priority.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0);
                fields["flagged"] = _flagged.IsChecked == true;
                SetCompleted(fields);
                // Done on a repeating reminder means "this time": it moves to the next one and stays
                // open, as ticking it off in the list does.
                if (fields["completed"] is true && !Bool("completed") && fields["recurrence"] is string rule
                    && fields["due"] is string dueValue
                    && Recurrence.NextDue(new OrganizerItem("", OrganizerKinds.Reminder, "", Fields: new Dictionary<string, object?>
                        { ["due"] = dueValue, ["allDay"] = allDay, ["recurrence"] = rule }), zone) is { } next)
                {
                    fields["due"] = next;
                    fields["completed"] = false;
                    fields["completedAt"] = null;
                }
                fields["notes"] = NullIfEmpty(_notes.Text);
                break;
            default:
                fields["description"] = NullIfEmpty(_notes.Text);
                fields["due"] = _hasDue.IsChecked == true && _due.Value is { } cardDue ? OrganizerDates.FieldValue(cardDue, true) : null;
                SetCompleted(fields);
                fields["labels"] = _labels!.Value;
                fields["checklist"] = _checklist!.Value;
                var people = _assignees.Children.OfType<CheckBox>().ToList();
                if (people.Count > 0) fields["assignees"] = people.Where(box => box.IsChecked == true).Select(box => (object?)box.Tag).ToList();
                break;
        }
        fields["links"] = _links.Value is { Count: > 0 } links ? links : null;
        return fields;
    }

    private void SetCompleted(Dictionary<string, object?> fields)
    {
        var done = _completed.IsChecked == true;
        fields["completed"] = done;
        if (done != Bool("completed")) fields["completedAt"] = done ? OrganizerDates.Timestamp() : null;
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string IanaZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }

    // A field the form shows as off or empty is the same as one that was never set. Without this,
    // saving an untouched reminder wrote completed: false with a fresh clock - enough to undo a
    // tick another device made at the same time.
    private static string Canonical(object? value) => value switch
    {
        null or false or "" => "null",
        IReadOnlyList<object?> { Count: 0 } => "null",
        _ => OrganizerJson.ToNode(value)?.ToJsonString() ?? "null",
    };

    private void Save()
    {
        if (Collect() is not { } fields) return;
        try
        {
            if (_existing is null)
            {
                var create = fields.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
                // Anything the caller set up front that the form doesn't show - a position, a series
                // link, fields from a newer app. Never one the form does show: clearing an inherited
                // location on a split-off occurrence has to stay cleared.
                foreach (var (key, value) in _original)
                {
                    if (!fields.ContainsKey(key)) create.TryAdd(key, value);
                }
                var item = _engine.Create(_kind, create, spaceId: _space is null ? null : Ui.SelectedTag(_space), parentId: _parentId);
                _attachments.Attach(item.Id);
                SavedId = item.Id;
            }
            else
            {
                var changes = fields
                    .Where(pair => Canonical(pair.Value) != Canonical(_existing.Fields.GetValueOrDefault(pair.Key)))
                    .Where(pair => !SameMoment(pair.Key, pair.Value, _existing))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (_space is not null && Ui.SelectedTag(_space) is { } space && space != _existing.SpaceId) changes["spaceId"] = space;
                if (changes.Count > 0) _engine.Update(_existing.Id, changes);
                SavedId = _existing.Id;
            }
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            Ui.ShowError(this, error);
        }
    }

    // Another app may write the same moment differently - "Z", milliseconds, another offset. The
    // same instant isn't an edit, and rewriting it would stamp a clock that beats a real edit made
    // elsewhere at the same time.
    private static bool SameMoment(string key, object? value, OrganizerItem existing)
    {
        if (key is not ("start" or "end" or "due") || value is not string text || existing.String(key) is not { } old) return false;
        var allDay = existing.Bool("allDay");
        if ((text.Length == 10) != (old.Length == 10)) return false;
        return OrganizerDates.Parse(text, allDay, Ui.Zone) is { } a && OrganizerDates.Parse(old, allDay, Ui.Zone) is { } b && a == b;
    }

    private void Delete()
    {
        if (_existing is null) return;
        var what = _kind switch { OrganizerKinds.Event => "event", OrganizerKinds.Reminder => "reminder", _ => "card" };
        if (!Ui.Confirm(this, $"Delete this {what}? It goes from every device.")) return;
        _engine.DeleteTree(_existing.Id);
        DialogResult = true;
        Close();
    }
}

/// <summary>
/// A card's comments, laid out like Trello's activity feed: a "Write a comment…" box with the
/// writer's initials beside it, then the comments newest first - initials, name and time over a
/// bubble of text, with small Edit / Delete links under it. Posting, editing and deleting happen at
/// once, not on the card's Save - like attachments. Follows sync while the card is open, but never
/// redraws under a half-edited comment.
/// </summary>
internal sealed class CommentsEditor : StackPanel
{
    // Same order as the Apple and Android apps, so a person keeps one colour everywhere.
    private static readonly Color[] AvatarColors =
    {
        Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0xFF, 0x37, 0x5F), Color.FromRgb(0x40, 0xC8, 0xE0), Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0xFF, 0x45, 0x3A),
    };

    private readonly OrganizerEngine _engine;
    private readonly string _cardId;
    private readonly TextBlock _count = new() { Foreground = Palette.SecondaryText, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _composerAvatar = new();
    private readonly StackPanel _rows = new();
    private readonly TextBox _new = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, MaxHeight = 160,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6),
        ToolTip = "Enter posts; Shift+Enter starts a new line",
    };
    private readonly StackPanel _composeButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _post;
    private readonly Button _discard;
    private readonly Ui.Coalescer _refresh;
    private Dictionary<string, string> _names = new();
    private string? _editing;
    private TextBox? _editBox;
    private string? _shown;
    // What's typed into the comment being edited, kept across a redraw.
    private string? _draft;

    public CommentsEditor(OrganizerEngine engine, string cardId)
    {
        _engine = engine;
        _cardId = cardId;
        _refresh = new Ui.Coalescer(this, Refresh);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(Ui.Glyph("", Ui.Brush("Cyan"), 14));
        var title = Ui.Heading("Comments");
        title.Margin = new Thickness(8, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        header.Children.Add(_count);
        Children.Add(header);

        // Like Trello, the Post button only shows once you're writing.
        _post = Ui.Button("Post", Post, "PrimaryButton");
        _post.MinWidth = 70;
        _discard = Ui.Button("Discard", () => { _new.Text = ""; Keyboard.ClearFocus(); UpdateComposer(); }, "LinkButton");
        _discard.Margin = new Thickness(12, 0, 0, 0);
        _composeButtons.Children.Add(_post);
        _composeButtons.Children.Add(_discard);
        var box = new StackPanel();
        box.Children.Add(Ui.WithPlaceholder(_new, "Write a comment…"));
        box.Children.Add(_composeButtons);
        Children.Add(Beside(_composerAvatar, box));
        _new.TextChanged += (_, _) => UpdateComposer();
        _new.GotKeyboardFocus += (_, _) => UpdateComposer();
        _new.LostKeyboardFocus += (_, _) => UpdateComposer();
        _new.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            Post();
            e.Handled = true;
        };

        _rows.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(_rows);
        engine.Changed += _refresh.Request;
        UpdateComposer();
        Rebuild();
    }

    /// <summary>Stops following sync. Called when the window closes.</summary>
    public void Detach() => _engine.Changed -= _refresh.Request;

    /// <summary>Display names for usernames, once the people list arrives.</summary>
    public void SetPeople(IEnumerable<OrganizerPerson> people)
    {
        _names = new Dictionary<string, string>();
        foreach (var person in people) _names[person.Username.ToLowerInvariant()] = person.Label;
        _shown = null;
        Refresh();
    }

    private string? Me => _engine.Status?.Username;

    private string? NameOf(string? username) =>
        string.IsNullOrEmpty(username) ? null : _names.GetValueOrDefault(username.ToLowerInvariant()) is { Length: > 0 } name ? name : username;

    private void UpdateComposer()
    {
        var hasText = _new.Text.Trim().Length > 0;
        _composeButtons.Visibility = hasText || _new.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
        _post.IsEnabled = hasText;
        _discard.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh()
    {
        if (_editing is not null) return;
        if (Signature() == _shown) return;
        Rebuild();
    }

    private string Signature() =>
        Me + "|" + string.Join("|", OrganizerComments.Of(_engine, _cardId)
            .Select(c => $"{c.Id}:{c.CreatedBy}:{c.String("editedAt")}:{c.String("body")}"));

    private void Post()
    {
        if (_new.Text.Trim() is not { Length: > 0 } body) return;
        try
        {
            _engine.Create(OrganizerKinds.Comment, new Dictionary<string, object?> { ["body"] = body }, parentId: _cardId);
            _new.Text = "";
            Rebuild();
        }
        catch (Exception error)
        {
            Ui.ShowError(this, error);
        }
    }

    private void Rebuild()
    {
        _shown = Signature();
        _draft = _editing is not null ? _editBox?.Text ?? _draft : null;
        _rows.Children.Clear();
        _editBox = null;
        _composerAvatar.Child = Avatar(NameOf(Me));
        var comments = OrganizerComments.Of(_engine, _cardId);
        if (_editing is not null && comments.All(c => c.Id != _editing)) _editing = null;
        _count.Text = comments.Count > 0 ? comments.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        for (var i = comments.Count - 1; i >= 0; i--) _rows.Children.Add(Row(comments[i]));
    }

    private UIElement Row(OrganizerItem comment)
    {
        var author = OrganizerComments.Author(comment, _names);
        var body = new StackPanel();

        var byline = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) };
        // With login off there's no author to show - just when.
        if (author is not null) byline.Inlines.Add(new System.Windows.Documents.Run(author + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText });
        var when = comment.CreatedAt is { } created ? Ui.When(created, false) : "";
        if (comment.String("editedAt") is { Length: > 0 }) when = (when + " (edited)").Trim();
        if (when.Length > 0) byline.Inlines.Add(new System.Windows.Documents.Run(when) { Foreground = Palette.SecondaryText, FontSize = 11 });
        body.Children.Add(byline);

        if (_editing == comment.Id)
        {
            var box = new TextBox
            {
                Text = _draft ?? comment.String("body") ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 52, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8, 6, 8, 6),
            };
            void SaveEdit()
            {
                var current = _engine.Item(comment.Id);
                _editing = null;
                if (current is not null && OrganizerComments.EditChanges(current, box.Text) is { } changes)
                {
                    try
                    {
                        _engine.Update(current.Id, changes);
                    }
                    catch (Exception error)
                    {
                        Ui.ShowError(this, error);
                    }
                }
                Rebuild();
            }
            void CancelEdit()
            {
                _editing = null;
                Rebuild();
            }
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    SaveEdit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    // Escape leaves the edit, not the whole card.
                    CancelEdit();
                    e.Handled = true;
                }
            };
            _editBox = box;
            body.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var save = Ui.Button("Save", SaveEdit, "PrimaryButton");
            save.MinWidth = 70;
            buttons.Children.Add(save);
            var cancel = Ui.Button("Cancel", CancelEdit, "LinkButton");
            cancel.Margin = new Thickness(12, 0, 0, 0);
            buttons.Children.Add(cancel);
            body.Children.Add(buttons);
        }
        else
        {
            // A read-only text box in a bubble, so the words can be selected and copied.
            body.Children.Add(new Border
            {
                Background = Ui.Brush("CardBackground"),
                BorderBrush = Ui.Brush("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Child = new TextBox
                {
                    Text = comment.String("body") ?? "",
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = Palette.PrimaryText,
                    Padding = new Thickness(0),
                },
            });
            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            if (OrganizerComments.CanEdit(comment, Me))
            {
                links.Children.Add(SmallLink("Edit", () =>
                {
                    _editing = comment.Id;
                    _editBox = null;
                    _draft = null;
                    Rebuild();
                    _editBox?.Focus();
                    _editBox?.SelectAll();
                }));
                links.Children.Add(new TextBlock { Text = "•", Foreground = Palette.SecondaryText, FontSize = 11, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            }
            links.Children.Add(SmallLink("Delete", () => DeleteComment(comment)));
            body.Children.Add(links);
        }

        var row = Beside(new Border { Child = Avatar(author) }, body);
        row.Margin = new Thickness(0, 8, 0, 6);
        return row;
    }

    private static Button SmallLink(string text, Action onClick)
    {
        var link = Ui.Button(text, onClick, "LinkButton");
        link.Foreground = Palette.SecondaryText;
        link.FontSize = 11;
        link.Cursor = Cursors.Hand;
        return link;
    }

    /// <summary>An avatar on the left with <paramref name="content"/> filling the rest, both top-aligned.</summary>
    private static Grid Beside(FrameworkElement avatar, UIElement content)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        avatar.Margin = new Thickness(0, 0, 10, 0);
        avatar.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(avatar);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>The round initials badge beside a comment; a person glyph when there's no name (login off).</summary>
    private static UIElement Avatar(string? name)
    {
        var initials = OrganizerComments.Initials(name);
        var color = name is { Length: > 0 } ? AvatarColors[OrganizerComments.ColorIndex(name, AvatarColors.Length)] : Color.FromRgb(0x6B, 0x77, 0x80);
        return new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(color),
            Child = initials is not null
                ? new TextBlock { Text = initials, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private void DeleteComment(OrganizerItem comment)
    {
        if (!Ui.Confirm(this, "Delete this comment? It goes from every device.")) return;
        if (_editing == comment.Id) _editing = null;
        try
        {
            _engine.DeleteTree(comment.Id);
        }
        catch (Exception error)
        {
            Ui.ShowError(this, error);
        }
        Rebuild();
    }
}

/// <summary>A card's checklist: <c>[{id, text, done}]</c>.</summary>
internal sealed class ChecklistEditor : StackPanel
{
    private readonly List<Dictionary<string, object?>> _items;
    private readonly StackPanel _rows = new();
    private readonly TextBox _new = new();

    public ChecklistEditor(IReadOnlyList<object?> items)
    {
        _items = items.OfType<IReadOnlyDictionary<string, object?>>().Select(item => new Dictionary<string, object?>(item)).ToList();
        Children.Add(_rows);
        _new.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AddTyped();
            e.Handled = true;
        };
        var add = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var button = Ui.Button("Add", AddTyped);
        button.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(button, Dock.Right);
        add.Children.Add(button);
        add.Children.Add(_new);
        Children.Add(add);
        Rebuild();
    }

    public List<object?> Value
    {
        get
        {
            AddTyped();
            return _items.Cast<object?>().ToList();
        }
    }

    private void AddTyped()
    {
        if (_new.Text.Trim() is not { Length: > 0 } text) return;
        _items.Add(new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString("N")[..10], ["text"] = text, ["done"] = false });
        _new.Text = "";
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        var done = _items.Count(item => item.GetValueOrDefault("done") is true);
        if (_items.Count > 0) _rows.Children.Add(Ui.Caption($"{done} of {_items.Count} done"));
        foreach (var item in _items.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = Ui.Icon("", "Remove", () =>
            {
                _items.Remove(item);
                Rebuild();
            });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var box = new CheckBox { IsChecked = item.GetValueOrDefault("done") is true, VerticalAlignment = VerticalAlignment.Center };
            box.Click += (_, _) =>
            {
                item["done"] = box.IsChecked == true;
                Rebuild();
            };
            row.Children.Add(box);
            var text = new TextBox { Text = item.GetValueOrDefault("text") as string ?? "", Margin = new Thickness(6, 0, 6, 0) };
            text.TextChanged += (_, _) => item["text"] = text.Text;
            row.Children.Add(text);
            _rows.Children.Add(row);
        }
    }
}

/// <summary>A card's labels: <c>[{name, color}]</c>.</summary>
internal sealed class LabelsEditor : StackPanel
{
    private readonly List<Dictionary<string, object?>> _labels;
    private readonly WrapPanel _chips = new();
    private readonly TextBox _name = new() { Width = 160 };
    private readonly ComboBox _color = new() { Width = 110, Margin = new Thickness(8, 0, 0, 0) };

    public LabelsEditor(IReadOnlyList<object?> labels)
    {
        _labels = labels.OfType<IReadOnlyDictionary<string, object?>>().Select(label => new Dictionary<string, object?>(label)).ToList();
        foreach (var (name, hex) in Ui.Colors.Skip(1)) _color.Items.Add(Ui.Option(name, hex));
        _color.SelectedIndex = 0;
        Children.Add(_chips);
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        add.Children.Add(_name);
        add.Children.Add(_color);
        var button = Ui.Button("Add label", AddTyped);
        button.Margin = new Thickness(8, 0, 0, 0);
        add.Children.Add(button);
        Children.Add(add);
        _name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AddTyped();
            e.Handled = true;
        };
        Rebuild();
    }

    public List<object?> Value
    {
        get
        {
            AddTyped();
            return _labels.Cast<object?>().ToList();
        }
    }

    private void AddTyped()
    {
        if (_name.Text.Trim() is not { Length: > 0 } name) return;
        _labels.Add(new Dictionary<string, object?> { ["name"] = name, ["color"] = Ui.SelectedTag(_color) });
        _name.Text = "";
        Rebuild();
    }

    private void Rebuild()
    {
        _chips.Children.Clear();
        foreach (var label in _labels.ToList())
        {
            var chip = Ui.Chip(label.GetValueOrDefault("name") as string ?? "", Ui.ColorBrush(label.GetValueOrDefault("color") as string, 90));
            chip.Cursor = Cursors.Hand;
            chip.ToolTip = "Click to remove";
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _labels.Remove(label);
                Rebuild();
            };
            _chips.Children.Add(chip);
        }
    }
}
