using System.Windows;
using System.Windows.Controls;
using SentinelCore.App.Views;
using SentinelCore.Client;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// The Organizer's part of Settings: how much of it this PC keeps, what that takes up, groups, and
/// the calendar subscription link. A computer keeps everything by default - it's the copy that
/// refills a server that had to be rebuilt.
/// </summary>
internal sealed class OrganizerSettingsPanel : StackPanel
{
    private readonly AppModel _model;
    private readonly OrganizerSession _session;
    private readonly ComboBox _mode = new() { MinWidth = 260 };
    private readonly ComboBox _window = new() { MinWidth = 160 };
    private readonly ComboBox _attachments = new() { MinWidth = 260 };
    private readonly ComboBox _cap = new() { MinWidth = 160 };
    private readonly StackPanel _windowRow = new();
    private readonly TextBlock _summary = new() { Style = Ui.Style("BodyText"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _feedText = new() { Style = Ui.Style("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Ui.Coalescer _refresh;
    private bool _building;

    public OrganizerSettingsPanel(AppModel model, OrganizerSession session)
    {
        _model = model;
        _session = session;
        _refresh = new Ui.Coalescer(this, ApplySummary);

        Children.Add(Ui.Heading("Organizer"));
        Children.Add(Ui.Caption("Notes, calendar, reminders and boards are kept on this PC as well as the server, so they work with no connection and can refill a server that had to be rebuilt."));

        _mode.Items.Add(Ui.Option("Everything (recommended for a computer)", OfflineMode.Full));
        _mode.Items.Add(Ui.Option("Recent and upcoming only", OfflineMode.Light));
        _mode.Items.Add(Ui.Option("Nothing - only while connected", OfflineMode.Off));
        foreach (var days in new[] { 30, 90, 180, 365 }) _window.Items.Add(Ui.Option(days == 365 ? "The last year" : $"The last {days} days", days));
        _attachments.Items.Add(Ui.Option("Full files", OfflineAttachments.Full));
        _attachments.Items.Add(Ui.Option("Picture previews only - open files from the server", OfflineAttachments.Thumbnails));
        _attachments.Items.Add(Ui.Option("None - everything from the server", OfflineAttachments.None));
        _cap.Items.Add(Ui.Option("No limit", (long?)null));
        foreach (var gigabytes in new[] { 1, 5, 20, 50 }) _cap.Items.Add(Ui.Option($"{gigabytes} GB", (long?)(gigabytes * 1024L * 1024 * 1024)));

        Add("Keep on this PC", _mode);
        _windowRow.Children.Add(Label("How far back"));
        _windowRow.Children.Add(_window);
        _windowRow.Children.Add(Ui.Caption("Upcoming events, open reminders, pinned notes and every board are always kept."));
        Children.Add(_windowRow);
        Add("Pictures and files", _attachments);
        Add("Room for files", _cap, "The least recently opened files go first, once they're safely on the server. Nothing waiting to upload is ever removed.");

        var summaryHeading = Label("On this PC");
        Children.Add(summaryHeading);
        Children.Add(_summary);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Spaced(Ui.Button("Groups…", OpenGroups)));
        actions.Children.Add(Spaced(Ui.Button("Copy calendar subscription link", () => _ = CopyFeedAsync(reset: false))));
        actions.Children.Add(Spaced(Ui.Button("Reset link…", () =>
        {
            if (Ui.Confirm(this, "Make a new calendar link? Anything subscribed to the old one stops updating.")) _ = CopyFeedAsync(reset: true);
        })));
        Children.Add(actions);
        Children.Add(_feedText);
        _feedText.Text = "The subscription link lets Outlook, Google Calendar or any other calendar app show your Organizer events. Anyone with the link can read them, so keep it to yourself.";

        ApplyPolicy(session.Engine.Policy);
        foreach (var picker in new[] { _mode, _window, _attachments, _cap }) picker.SelectionChanged += (_, _) => SavePolicy();
        Loaded += (_, _) =>
        {
            _session.Engine.Changed += _refresh.Request;
            ApplySummary();
        };
        Unloaded += (_, _) => _session.Engine.Changed -= _refresh.Request;
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 0, 10, 8);
        return element;
    }

    private static TextBlock Label(string text)
    {
        var label = Ui.Caption(text);
        label.Margin = new Thickness(0, 12, 0, 4);
        return label;
    }

    private void Add(string label, Control control, string? hint = null)
    {
        Children.Add(Label(label));
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(control);
        if (hint is not null)
        {
            var text = Ui.Caption(hint);
            text.Margin = new Thickness(0, 4, 0, 0);
            Children.Add(text);
        }
    }

    private void ApplyPolicy(OfflinePolicy policy)
    {
        _building = true;
        Ui.Select(_mode, policy.Mode);
        Ui.Select(_window, policy.WindowDays ?? 90);
        Ui.Select(_attachments, policy.Attachments);
        Ui.Select(_cap, policy.StorageCapBytes);
        _windowRow.Visibility = policy.Mode == OfflineMode.Light ? Visibility.Visible : Visibility.Collapsed;
        _attachments.IsEnabled = _cap.IsEnabled = policy.Mode != OfflineMode.Off;
        _building = false;
    }

    private void SavePolicy()
    {
        if (_building) return;
        var mode = (_mode.SelectedItem as ComboBoxItem)?.Tag as OfflineMode? ?? OfflineMode.Full;
        var current = _session.Engine.Policy;
        var policy = new OfflinePolicy(
            mode,
            mode == OfflineMode.Light ? (_window.SelectedItem as ComboBoxItem)?.Tag as int? ?? 90 : null,
            mode == OfflineMode.Off ? OfflineAttachments.None : (_attachments.SelectedItem as ComboBoxItem)?.Tag as OfflineAttachments? ?? OfflineAttachments.Full,
            (_cap.SelectedItem as ComboBoxItem)?.Tag as long?);
        if (policy == current) return;
        if (policy.Mode == OfflineMode.Off && current.Mode != OfflineMode.Off
            && !Ui.Confirm(this, "Keep nothing on this PC? The Organizer will only work while connected, and this PC can't help refill a rebuilt server. Anything not yet synced is kept until it is."))
        {
            ApplyPolicy(current);
            return;
        }
        _session.SetPolicy(policy);
        ApplyPolicy(policy);
    }

    private void ApplySummary()
    {
        var summary = _session.Engine.StorageSummary();
        var state = _session.Engine.SyncState;
        var lines = new List<string>
        {
            $"{summary.ItemCount} item{(summary.ItemCount == 1 ? "" : "s")}" +
            (summary.ServerItemCount is { } server ? $" (the server has {server})" : ""),
            $"{summary.AttachmentsWithOriginal} of {summary.AttachmentCount} attachment{(summary.AttachmentCount == 1 ? "" : "s")} kept in full · {Ui.Size(summary.BytesOnDevice)} of files",
        };
        if (summary.WaitingUploads > 0) lines.Add($"{summary.WaitingUploads} file{(summary.WaitingUploads == 1 ? "" : "s")} waiting to upload");
        if (state.WaitingChanges > 0) lines.Add($"{state.WaitingChanges} change{(state.WaitingChanges == 1 ? "" : "s")} waiting to sync");
        if (state.LastSyncedAt is { } synced) lines.Add($"Last synced {synced.LocalDateTime:g}");
        _summary.Text = string.Join("\n", lines);
    }

    private void OpenGroups() => new GroupsWindow(_session.Engine) { Owner = Window.GetWindow(this) }.ShowDialog();

    private async Task CopyFeedAsync(bool reset)
    {
        try
        {
            var path = await _session.Engine.CalendarFeedPathAsync(reset);
            var url = _model.Client.Url(path)?.ToString() ?? path;
            Clipboard.SetText(url);
            _feedText.Text = $"Copied: {url}\nPaste it into your calendar app's \"subscribe\" or \"add from URL\" option.";
        }
        catch (Exception error)
        {
            _feedText.Text = $"Couldn't get the link: {error.Message}";
        }
    }
}

/// <summary>
/// Groups: spaces only the people in them can see. Any member can edit one, and taking yourself
/// out is how you leave.
/// </summary>
internal sealed class GroupsWindow : Window
{
    private readonly OrganizerEngine _engine;
    private readonly StackPanel _list = new();
    private readonly TextBlock _notice = new() { Style = Ui.Style("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public GroupsWindow(OrganizerEngine engine)
    {
        _engine = engine;
        Style = Ui.Style("Dialog");
        Title = "Groups";
        Width = 520;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var add = Ui.Button("New group", () => _ = EditAsync(null), "PrimaryButton");
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        top.Children.Add(Ui.Text("Only the people in a group see what's in it."));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(_notice, Dock.Bottom);
        root.Children.Add(_notice);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var groups = _engine.Spaces.Where(space => space.Kind == OrganizerSpaceKind.Group).OrderBy(space => space.Name).ToList();
        if (groups.Count == 0) _list.Children.Add(Ui.Caption("No groups yet."));
        foreach (var group in groups)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Ui.Button("Edit", () => _ = EditAsync(group)));
            var delete = Ui.Button("Delete", () => _ = DeleteAsync(group), "DangerButton");
            delete.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(delete);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = group.Name, Foreground = Ui.ColorBrush(group.Color) ?? Palette.PrimaryText, FontWeight = FontWeights.SemiBold });
            text.Children.Add(Ui.Caption(string.Join(", ", group.Members)));
            row.Children.Add(text);
            _list.Children.Add(new Border { Style = Ui.Style("Card"), Child = row, Margin = new Thickness(0, 0, 0, 8) });
        }
    }

    private async Task EditAsync(OrganizerSpace? group)
    {
        List<OrganizerPerson> people;
        try
        {
            people = await _engine.PeopleAsync();
        }
        catch (Exception error)
        {
            _notice.Text = $"Groups can only be changed while connected: {error.Message}";
            return;
        }
        if (people.Count == 0)
        {
            _notice.Text = "Groups need dashboard login turned on, so each person has their own account. Without it everyone shares one Family space.";
            return;
        }
        var me = _engine.Status?.Username;
        var dialog = new Window
        {
            Style = Ui.Style("Dialog"),
            Owner = this,
            Title = group is null ? "New group" : "Edit group",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var form = new StackPanel { Margin = new Thickness(18) };
        form.Children.Add(Ui.Caption("Name"));
        var name = new TextBox { Text = group?.Name ?? "", Margin = new Thickness(0, 4, 0, 10) };
        form.Children.Add(name);
        form.Children.Add(Ui.Caption("Color"));
        var color = new ComboBox { Margin = new Thickness(0, 4, 0, 10), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
        foreach (var (label, hex) in Ui.Colors) color.Items.Add(Ui.Option(label, hex));
        Ui.Select(color, group?.Color);
        form.Children.Add(color);
        form.Children.Add(Ui.Caption("Who's in it"));
        var boxes = new List<CheckBox>();
        foreach (var person in people)
        {
            var box = new CheckBox
            {
                Content = person.Username == me ? $"{person.Label} (you)" : person.Label,
                Tag = person.Username,
                IsChecked = group is null ? person.Username == me : group.Members.Contains(person.Username),
                Margin = new Thickness(0, 4, 0, 0),
            };
            boxes.Add(box);
            form.Children.Add(box);
        }
        var problem = new TextBlock { Foreground = Palette.Red, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        form.Children.Add(problem);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = Ui.Button("Cancel", dialog.Close);
        cancel.IsCancel = true;
        footer.Children.Add(cancel);
        var save = Ui.Button("Save", () => { }, "PrimaryButton");
        save.Margin = new Thickness(10, 0, 0, 0);
        save.Click += async (_, _) =>
        {
            if (name.Text.Trim().Length == 0)
            {
                problem.Text = "Give the group a name.";
                return;
            }
            var members = boxes.Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToList();
            if (group is not null && me is not null && !members.Contains(me)
                && !Ui.Confirm(dialog, "You've taken yourself out of this group - you'll stop seeing what's in it. Continue?"))
            {
                return;
            }
            save.IsEnabled = false;
            cancel.IsEnabled = false;
            try
            {
                await _engine.SaveGroupAsync(group?.Id, name.Text.Trim(), members, Ui.SelectedTag(color));
                if (dialog.IsLoaded) dialog.DialogResult = true;
            }
            catch (Exception error)
            {
                problem.Text = error.Message;
                save.IsEnabled = true;
                cancel.IsEnabled = true;
            }
        };
        footer.Children.Add(save);
        form.Children.Add(footer);
        dialog.Content = form;
        if (dialog.ShowDialog() == true) Rebuild();
    }

    private async Task DeleteAsync(OrganizerSpace group)
    {
        if (!Ui.Confirm(this, $"Delete \"{group.Name}\" and everything in it, for everyone in it?")) return;
        try
        {
            await _engine.DeleteGroupAsync(group.Id);
            Rebuild();
        }
        catch (Exception error)
        {
            _notice.Text = $"Couldn't delete it: {error.Message}";
        }
    }
}
