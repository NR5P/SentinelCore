using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Notes: a searchable list beside the note being edited. Typing saves as it goes, after a short
/// pause. Each save is a body edit that carries the clock it started from, so two people rewriting
/// one note both keep their text - the server files the other version as a conflicted copy.
/// A note with checklist lines ("- [ ] milk") opens as tick boxes, so a list can be checked off and
/// added to without typing brackets; the body stays plain Markdown underneath.
/// </summary>
public sealed class NotesPane : OrganizerPane
{
    private readonly TextBox _search = new() { ToolTip = "Search notes" };
    private readonly CheckBox _showArchived = new() { Content = "Show archived", Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _list = new();
    private readonly ContentControl _editorHost = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private NoteEditor? _editor;
    private string? _selectedId;
    private bool _building;

    public NotesPane(OrganizerEngine engine) : base(engine)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 16, 0) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        // A checklist is just a note that opens as tick boxes, ready for the first item.
        var addChecklist = Ui.Button("+ Checklist", () => NewNote(checklist: true), tooltip: "New checklist - add items and tick them off");
        addChecklist.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(addChecklist, Dock.Right);
        top.Children.Add(addChecklist);
        var add = Ui.Button("+ Note", () => NewNote(checklist: false), "PrimaryButton");
        add.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        DockPanel.SetDock(_showArchived, Dock.Top);
        left.Children.Add(_showArchived);
        left.Children.Add(_list);
        Grid.SetColumn(left, 0);
        layout.Children.Add(left);
        Grid.SetColumn(_editorHost, 1);
        layout.Children.Add(_editorHost);
        Content = layout;

        _search.TextChanged += (_, _) => Refresh();
        _showArchived.Click += (_, _) => Refresh();
        _list.SelectionChanged += (_, _) =>
        {
            if (_building || _list.SelectedItem is not ListBoxItem { Tag: string id }) return;
            Select(id);
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _editor?.Save();
        };
        // Leaving the screen, or the app, never drops the last few keystrokes.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _editor?.Save();
        };
    }

    private List<OrganizerItem> Notes()
    {
        var query = _search.Text.Trim();
        return Engine.Items(OrganizerKinds.Note)
            .Where(Shows)
            .Where(note => _showArchived.IsChecked == true || !note.Bool("archived"))
            .Where(note => query.Length == 0
                           || note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                           || (note.String("body") ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(note => note.Bool("pinned"))
            .ThenByDescending(note => note.UpdatedAt ?? note.CreatedAt)
            .ToList();
    }

    private string? _listed;

    public override void Refresh()
    {
        var notes = Notes();
        // Rebuilt only when something shown in it changed, so a sync doesn't take focus out of it.
        var signature = string.Join("|", notes.Select(note => $"{note.Id}:{note.Title}:{note.Bool("pinned")}:{note.String("color")}:{note.SpaceId}:{note.UpdatedAt?.ToUnixTimeSeconds()}:{(note.String("body") ?? "").Length}:{NoteChecklist.Progress(note.String("body"))}"))
                        + "#" + SpaceFilter;
        if (signature == _listed)
        {
            if (_selectedId is not null && Engine.Item(_selectedId) is { } unchanged) _editor?.Refresh(unchanged);
            else if (_selectedId is not null) ClearEditor(notes);
            return;
        }
        _listed = signature;
        _building = true;
        _list.Items.Clear();
        foreach (var note in notes)
        {
            var text = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            var title = new DockPanel();
            if (note.Bool("pinned"))
            {
                var pin = Ui.Glyph("", Palette.Amber, 11);
                pin.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(pin, Dock.Right);
                title.Children.Add(pin);
            }
            title.Children.Add(new TextBlock
            {
                Text = note.Title.Length == 0 ? "Untitled" : note.Title,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.ColorBrush(note.String("color")) ?? Palette.PrimaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(title);
            var preview = (note.String("body") ?? "").Replace('\n', ' ').Trim();
            var meta = note.UpdatedAt is { } updated ? Ui.When(updated, allDay: false) : "";
            // How far through its checklist a note is, so a shopping list shows at a glance.
            if (NoteChecklist.Progress(note.String("body")) is { Total: > 0 } progress)
            {
                meta = $"☑ {progress.Done}/{progress.Total}" + (meta.Length > 0 ? " · " + meta : "");
            }
            if (SpaceFilter is null) meta += (meta.Length > 0 ? " · " : "") + Ui.SpaceLabel(Engine, note.SpaceId);
            text.Children.Add(new TextBlock
            {
                Text = preview.Length == 0 ? meta : $"{meta} · {preview}",
                Style = Ui.Style("Caption"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var entry = new ListBoxItem { Content = text, Tag = note.Id };
            _list.Items.Add(entry);
            if (note.Id == _selectedId) _list.SelectedItem = entry;
        }
        _building = false;

        if (_selectedId is not null && Engine.Item(_selectedId) is { } selected)
        {
            _editor?.Refresh(selected);
        }
        else
        {
            ClearEditor(notes);
        }
    }

    private void ClearEditor(List<OrganizerItem> notes)
    {
        _editor = null;
        _selectedId = null;
        _editorHost.Content = Ui.Caption(notes.Count == 0 ? "No notes yet - start one with + Note." : "Pick a note to read or edit it.");
        if (notes.Count > 0 && _list.Items.Count > 0 && _search.Text.Length == 0) _list.SelectedIndex = 0;
    }

    /// <param name="checklist">True to open as tick boxes, false to open for typing, null to decide by whether it has checklist lines.</param>
    private void Select(string id, bool? checklist = null)
    {
        _editor?.Save();
        if (Engine.Item(id) is not { } note) return;
        _selectedId = id;
        _editor = new NoteEditor(Engine, note, () =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }, checklist);
        _editorHost.Content = _editor;
    }

    private void NewNote(bool checklist)
    {
        _editor?.Save();
        var note = Engine.Create(OrganizerKinds.Note, new Dictionary<string, object?> { ["title"] = "", ["body"] = "" }, spaceId: NewItemSpace);
        _search.Text = "";
        _selectedId = note.Id;
        Refresh();
        Select(note.Id, checklist);
        // Either way the title comes first; on a checklist, Enter there moves on to the first item.
        _editor?.FocusTitle();
    }

    public override void Reveal(OrganizerItem item)
    {
        _search.Text = "";
        _selectedId = item.Id;
        Refresh();
        Select(item.Id);
    }

    /// <summary>
    /// One note, edited in place. The body shows either as text to type into or, for a checklist,
    /// as tick boxes with an Add item box underneath - the "get it done" view.
    /// </summary>
    private sealed class NoteEditor : DockPanel
    {
        private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*)$");
        private static readonly Regex BulletLine = new(@"^(\s*)[-*]\s+(.*)$");
        private static readonly Regex QuoteLine = new(@"^\s*>\s?(.*)$");

        private readonly OrganizerEngine _engine;
        private readonly Action _changed;
        private readonly TextBox _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0) };
        private readonly TextBox _body = new()
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
            MinHeight = 240,
        };
        private readonly ComboBox _space;
        private readonly CheckBox _pinned = new() { Content = "Pinned", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _archived = new() { Content = "Archived", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _offline = new() { Content = "Keep on this PC", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Kept even when a light copy would let it go" };
        private readonly TextBlock _status = new() { Style = Ui.Style("Caption"), Margin = new Thickness(0, 4, 0, 0) };
        private readonly AttachmentsEditor _attachments;
        private readonly StackPanel _linksHost = new();
        private readonly ContentControl _bodyHost = new();
        private readonly Button _mode;
        private readonly Button _makeChecklist;
        private readonly ScrollViewer _checkView;
        private readonly StackPanel _rows = new();
        private readonly TextBox _addItem = new() { FontSize = 14, ToolTip = "Type an item and press Enter - the box stays ready for the next one" };
        private readonly Button _clearChecked;
        private OrganizerItem _note;
        private string _savedTitle;
        private string _savedBody;
        private bool _rendered;
        private string? _renderedBody;

        /// <param name="checklist">True to open as tick boxes, false to open for typing, null to decide by whether it has checklist lines.</param>
        public NoteEditor(OrganizerEngine engine, OrganizerItem note, Action changed, bool? checklist = null)
        {
            _engine = engine;
            _note = note;
            _changed = changed;
            _savedTitle = note.Title;
            // Stored with "\n" line ends, whatever this text box types - see NoteChecklist.Normalize.
            _savedBody = NoteChecklist.Normalize(note.String("body"));
            _title.Text = _savedTitle;
            _body.Text = _savedBody;
            _pinned.IsChecked = note.Bool("pinned");
            _archived.IsChecked = note.Bool("archived");
            _offline.IsChecked = engine.KeptOffline.Contains(note.Id);
            _space = Ui.SpacePicker(engine, note.SpaceId);
            _attachments = new AttachmentsEditor(engine, note.Id);

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            toolbar.Children.Add(_space);
            toolbar.Children.Add(_pinned);
            toolbar.Children.Add(_archived);
            toolbar.Children.Add(_offline);
            var delete = Ui.Button("Delete", Delete, "DangerButton");
            delete.Margin = new Thickness(16, 0, 0, 0);
            toolbar.Children.Add(delete);

            // Preview/Edit flips between the tick boxes and the text; Checklist turns what's typed into items.
            _mode = Ui.Button("Preview", () => SetRendered(!_rendered));
            _makeChecklist = Ui.Button("Checklist", MakeChecklist, tooltip: "Turn each line into a tick box - headings and blank lines stay as they are");
            _makeChecklist.Margin = new Thickness(8, 0, 0, 0);
            var modeBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            modeBar.Children.Add(_mode);
            modeBar.Children.Add(_makeChecklist);

            var header = new StackPanel();
            header.Children.Add(_title);
            header.Children.Add(toolbar);
            header.Children.Add(modeBar);
            SetDock(header, Dock.Top);
            Children.Add(header);

            var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            bottom.Children.Add(Ui.Heading("Links"));
            bottom.Children.Add(_linksHost);
            var files = Ui.Heading("Pictures and files");
            files.Margin = new Thickness(0, 10, 0, 6);
            bottom.Children.Add(files);
            bottom.Children.Add(_attachments);
            bottom.Children.Add(_status);
            SetDock(bottom, Dock.Bottom);
            Children.Add(bottom);

            // The check-off view: the note's lines, then the Add item box right under the last of them.
            _clearChecked = Ui.Button("", ClearChecked, "LinkButton", "Remove every ticked item from the note");
            _clearChecked.HorizontalAlignment = HorizontalAlignment.Left;
            _clearChecked.Margin = new Thickness(0, 4, 0, 0);
            var add = Ui.WithPlaceholder(_addItem, "Add item");
            add.Margin = new Thickness(0, 8, 0, 0);
            var checkStack = new StackPanel();
            checkStack.Children.Add(_rows);
            checkStack.Children.Add(add);
            checkStack.Children.Add(_clearChecked);
            _checkView = new ScrollViewer
            {
                Content = checkStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MinHeight = 240,
            };
            Children.Add(_bodyHost);
            BuildLinks();

            _title.TextChanged += (_, _) => _changed();
            _body.TextChanged += (_, _) => _changed();
            _title.LostKeyboardFocus += (_, _) => Save();
            _body.LostKeyboardFocus += (_, _) => Save();
            _body.PreviewKeyDown += ContinueList;
            // On a checklist, Enter after the title goes straight to the first item.
            _title.PreviewKeyDown += (_, e) =>
            {
                if (!_rendered || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                Save();
                FocusAddItem();
                e.Handled = true;
            };
            _addItem.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                var text = _addItem.Text;
                _addItem.Text = "";
                Apply(body => NoteChecklist.AddItem(body, text));
                // Focus never leaves the box, so the next item can be typed straight away.
                _addItem.BringIntoView();
                e.Handled = true;
            };
            _pinned.Click += (_, _) => _engine.Update(_note.Id, "pinned", _pinned.IsChecked == true);
            _archived.Click += (_, _) => _engine.Update(_note.Id, "archived", _archived.IsChecked == true);
            _offline.Click += (_, _) => _engine.SetKeptOffline(_note.Id, _offline.IsChecked == true);
            _space.SelectionChanged += (_, _) =>
            {
                if (Ui.SelectedTag(_space) is { } space && space != _note.SpaceId) _engine.Update(_note.Id, "spaceId", space);
            };
            _body.ToolTip = "Markdown works here. Press Checklist to turn these lines into tick boxes, or start a line with \"- [ ] \" - Enter carries it on to the next line.";

            // A note that already has checklist lines opens ready to tick; anything else opens for typing.
            SetRendered(checklist ?? NoteChecklist.Progress(_savedBody).Total > 0);
        }

        private void BuildLinks()
        {
            _linksHost.Children.Clear();
            var editor = new LinksEditor(_note.List("links"));
            _linksHost.Children.Add(editor);
            var save = Ui.Button("Save links", () =>
            {
                var links = editor.Value;
                _engine.Update(_note.Id, "links", links.Count == 0 ? null : links);
            });
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 4, 0, 0);
            _linksHost.Children.Add(save);
        }

        public void FocusTitle() => Dispatcher.BeginInvoke(() => _title.Focus(), DispatcherPriority.Input);

        private void FocusAddItem() => Dispatcher.BeginInvoke(() =>
        {
            _addItem.Focus();
            _addItem.BringIntoView();
        }, DispatcherPriority.Input);

        private void SetRendered(bool rendered)
        {
            // Whatever was typed is kept before the view changes under it.
            Save();
            _rendered = rendered;
            _mode.Content = rendered ? "Edit" : "Preview";
            _mode.ToolTip = rendered ? "Edit the note as text" : "Show the note with tick boxes you can check off";
            _makeChecklist.Visibility = rendered ? Visibility.Collapsed : Visibility.Visible;
            if (rendered) BuildRows();
            _bodyHost.Content = rendered ? _checkView : _body;
        }

        /// <summary>Runs a checklist change on the body and saves it straight away - a tick shouldn't wait for a pause in typing.</summary>
        private void Apply(Func<string, string> change)
        {
            var body = NoteChecklist.Normalize(_body.Text);
            var next = change(body);
            if (next != body)
            {
                _body.Text = next;
                Save();
            }
            if (_rendered) BuildRows();
        }

        /// <summary>
        /// A change to one line, made only if that line still reads as it did when the row was drawn -
        /// a sync may have moved things since, and then the rows are just redrawn.
        /// </summary>
        private void ApplyAt(NoteLine line, Func<string, int, string> change) => Apply(body =>
        {
            var lines = NoteChecklist.Scan(body);
            return line.Index < lines.Count && lines[line.Index].Text == line.Text ? change(body, line.Index) : body;
        });

        private void MakeChecklist()
        {
            Apply(NoteChecklist.MakeChecklist);
            SetRendered(true);
            FocusAddItem();
        }

        private void ClearChecked() => Apply(NoteChecklist.ClearChecked);

        /// <summary>
        /// Draws the check-off view. Not full Markdown - just enough to read a list: items as tick
        /// boxes, headings bigger, code in a fixed font, everything else as wrapped text.
        /// </summary>
        private void BuildRows()
        {
            var body = NoteChecklist.Normalize(_body.Text);
            _renderedBody = body;
            _rows.Children.Clear();
            var (done, total) = NoteChecklist.Progress(body);
            if (total > 0)
            {
                var progress = Ui.Caption($"{done} of {total} done");
                progress.Margin = new Thickness(0, 0, 0, 6);
                _rows.Children.Add(progress);
            }
            _clearChecked.Content = $"Clear checked ({done})";
            _clearChecked.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (body.Trim().Length == 0)
            {
                _rows.Children.Add(Ui.Caption("Nothing here yet - add the first item below."));
                return;
            }
            foreach (var line in NoteChecklist.Scan(body))
            {
                if (Row(line) is { } row) _rows.Children.Add(row);
            }
        }

        private UIElement? Row(NoteLine line)
        {
            var text = line.Text;
            if (line.InCode)
            {
                // The ``` markers themselves aren't worth a row; what's between them is shown as written.
                return text.Trim().StartsWith("```", StringComparison.Ordinal) ? null : Ui.Text(text, "Monospace");
            }
            var indent = new Thickness((text.Length - text.TrimStart().Length) * 6, 0, 0, 0);
            if (line.IsItem)
            {
                var row = new DockPanel { Margin = new Thickness(indent.Left, 2, 0, 2) };
                var remove = Ui.Icon("", "Remove this item", () => ApplyAt(line, NoteChecklist.RemoveLine));
                remove.Padding = new Thickness(6, 3, 6, 3);
                remove.VerticalAlignment = VerticalAlignment.Top;
                SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var done = line.Done == true;
                var label = new TextBlock
                {
                    Text = line.ItemText,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Foreground = done ? Palette.SecondaryText : Palette.PrimaryText,
                    TextDecorations = done ? TextDecorations.Strikethrough : null,
                };
                var box = new CheckBox { IsChecked = done, Content = label, VerticalContentAlignment = VerticalAlignment.Top };
                box.Click += (_, _) => ApplyAt(line, NoteChecklist.Toggle);
                row.Children.Add(box);
                return row;
            }
            if (text.Trim().Length == 0) return new Border { Height = 8 };
            if (HeadingLine.Match(text) is { Success: true } heading)
            {
                var size = heading.Groups[1].Value.Length switch { 1 => 20, 2 => 18, 3 => 16, _ => 14 };
                var block = Ui.Text(heading.Groups[2].Value, size: size);
                block.FontWeight = FontWeights.SemiBold;
                block.Margin = new Thickness(0, 6, 0, 2);
                return block;
            }
            if (QuoteLine.Match(text) is { Success: true } quote)
            {
                var block = Ui.Text(quote.Groups[1].Value);
                block.FontStyle = FontStyles.Italic;
                block.Foreground = Palette.SecondaryText;
                return new Border
                {
                    BorderBrush = Palette.SecondaryText,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0),
                    Margin = new Thickness(0, 2, 0, 2),
                    Child = block,
                };
            }
            if (BulletLine.Match(text) is { Success: true } bullet)
            {
                var block = Ui.Text("• " + bullet.Groups[2].Value);
                block.Margin = new Thickness(indent.Left, 1, 0, 1);
                return block;
            }
            var plain = Ui.Text(text.Trim());
            plain.Margin = new Thickness(0, 1, 0, 1);
            return plain;
        }

        /// <summary>
        /// Enter on a checklist or bullet line starts the next one with the same marker; Enter on
        /// an empty one ends the list instead. Anything else is an ordinary new line.
        /// </summary>
        private void ContinueList(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || _body.SelectionLength > 0) return;
            var text = _body.Text;
            var caret = _body.CaretIndex;
            var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
            var lineEnd = text.IndexOf('\n', caret);
            if (lineEnd < 0) lineEnd = text.Length;
            if (lineEnd > lineStart && text[lineEnd - 1] == '\r') lineEnd--;
            if (caret > lineEnd) return;
            var before = text[lineStart..caret];
            var after = text[caret..lineEnd];
            switch (NoteChecklist.ContinueOnEnter(before))
            {
                case { Insert: { } prefix }:
                    _body.SelectedText = "\r\n" + prefix;
                    _body.CaretIndex = caret + 2 + prefix.Length;
                    e.Handled = true;
                    break;
                case { EndList: true } when after.Trim().Length == 0:
                    // An empty item: take its marker away rather than adding another.
                    _body.Select(lineStart, lineEnd - lineStart);
                    _body.SelectedText = "";
                    _body.CaretIndex = lineStart;
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Writes what changed since the last save - just the fields that did.</summary>
        public void Save()
        {
            var body = NoteChecklist.Normalize(_body.Text);
            var changes = new Dictionary<string, object?>();
            if (_title.Text != _savedTitle) changes["title"] = _title.Text;
            if (body != _savedBody) changes["body"] = body;
            if (changes.Count == 0) return;
            _savedTitle = _title.Text;
            _savedBody = body;
            _engine.Update(_note.Id, changes);
            _status.Text = $"Saved {DateTime.Now:t}";
        }

        /// <summary>
        /// Takes in a newer version from a sync. Text someone is typing into is left alone - their
        /// save will carry the clock it started from, and the server keeps both versions if the two
        /// really did collide.
        /// </summary>
        public void Refresh(OrganizerItem note)
        {
            var linksChanged = OrganizerJson.ToNode(note.List("links"))?.ToJsonString() != OrganizerJson.ToNode(_note.List("links"))?.ToJsonString();
            _note = note;
            if (!_title.IsKeyboardFocusWithin && _title.Text == _savedTitle && note.Title != _savedTitle)
            {
                _savedTitle = note.Title;
                _title.Text = note.Title;
            }
            var body = NoteChecklist.Normalize(note.String("body"));
            if (!_body.IsKeyboardFocusWithin && NoteChecklist.Normalize(_body.Text) == _savedBody && body != _savedBody)
            {
                _savedBody = body;
                _body.Text = body;
            }
            // Redrawn only when the text under it changed, so a sync doesn't pull a tick box out from under the mouse.
            if (_rendered && NoteChecklist.Normalize(_body.Text) != _renderedBody) BuildRows();
            _pinned.IsChecked = note.Bool("pinned");
            _archived.IsChecked = note.Bool("archived");
            if (Ui.SelectedTag(_space) != note.SpaceId) Ui.Select(_space, note.SpaceId);
            _attachments.Refresh();
            if (linksChanged) BuildLinks();
        }

        private void Delete()
        {
            if (!Ui.Confirm(this, $"Delete \"{(_note.Title.Length == 0 ? "Untitled" : _note.Title)}\"? It goes from every device.")) return;
            _savedTitle = _title.Text;
            _savedBody = NoteChecklist.Normalize(_body.Text);
            _engine.DeleteTree(_note.Id);
        }
    }
}
