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
        var add = Ui.Button("+ Note", NewNote, "PrimaryButton");
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
        var signature = string.Join("|", notes.Select(note => $"{note.Id}:{note.Title}:{note.Bool("pinned")}:{note.String("color")}:{note.SpaceId}:{note.UpdatedAt?.ToUnixTimeSeconds()}:{(note.String("body") ?? "").Length}"))
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

    private void Select(string id)
    {
        _editor?.Save();
        if (Engine.Item(id) is not { } note) return;
        _selectedId = id;
        _editor = new NoteEditor(Engine, note, () =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        });
        _editorHost.Content = _editor;
    }

    private void NewNote()
    {
        _editor?.Save();
        var note = Engine.Create(OrganizerKinds.Note, new Dictionary<string, object?> { ["title"] = "", ["body"] = "" }, spaceId: NewItemSpace);
        _search.Text = "";
        _selectedId = note.Id;
        Refresh();
        Select(note.Id);
        _editor?.FocusTitle();
    }

    public override void Reveal(OrganizerItem item)
    {
        _search.Text = "";
        _selectedId = item.Id;
        Refresh();
        Select(item.Id);
    }

    /// <summary>One note, edited in place.</summary>
    private sealed class NoteEditor : DockPanel
    {
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
        private OrganizerItem _note;
        private string _savedTitle;
        private string _savedBody;

        public NoteEditor(OrganizerEngine engine, OrganizerItem note, Action changed)
        {
            _engine = engine;
            _note = note;
            _changed = changed;
            _savedTitle = note.Title;
            _savedBody = note.String("body") ?? "";
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

            var header = new StackPanel();
            header.Children.Add(_title);
            header.Children.Add(toolbar);
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
            Children.Add(_body);
            BuildLinks();

            _title.TextChanged += (_, _) => _changed();
            _body.TextChanged += (_, _) => _changed();
            _title.LostKeyboardFocus += (_, _) => Save();
            _body.LostKeyboardFocus += (_, _) => Save();
            _pinned.Click += (_, _) => _engine.Update(_note.Id, "pinned", _pinned.IsChecked == true);
            _archived.Click += (_, _) => _engine.Update(_note.Id, "archived", _archived.IsChecked == true);
            _offline.Click += (_, _) => _engine.SetKeptOffline(_note.Id, _offline.IsChecked == true);
            _space.SelectionChanged += (_, _) =>
            {
                if (Ui.SelectedTag(_space) is { } space && space != _note.SpaceId) _engine.Update(_note.Id, "spaceId", space);
            };
            _body.ToolTip = "Markdown works here - \"- [ ] \" starts a checklist line";
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

        /// <summary>Writes what changed since the last save - just the fields that did.</summary>
        public void Save()
        {
            var changes = new Dictionary<string, object?>();
            if (_title.Text != _savedTitle) changes["title"] = _title.Text;
            if (_body.Text != _savedBody) changes["body"] = _body.Text;
            if (changes.Count == 0) return;
            _savedTitle = _title.Text;
            _savedBody = _body.Text;
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
            var body = note.String("body") ?? "";
            if (!_body.IsKeyboardFocusWithin && _body.Text == _savedBody && body != _savedBody)
            {
                _savedBody = body;
                _body.Text = body;
            }
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
            _savedBody = _body.Text;
            _engine.DeleteTree(_note.Id);
        }
    }
}
