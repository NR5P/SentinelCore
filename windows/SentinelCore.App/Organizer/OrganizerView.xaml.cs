using System.Windows;
using System.Windows.Controls;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// One of the Organizer's four screens. Each rebuilds from the engine on <see cref="Refresh"/>,
/// showing only the chosen space when one is chosen.
/// </summary>
public abstract class OrganizerPane : UserControl
{
    protected OrganizerPane(OrganizerEngine engine)
    {
        Engine = engine;
    }

    protected OrganizerEngine Engine { get; }

    /// <summary>The space being shown, or null for everything.</summary>
    public string? SpaceFilter { get; set; }

    protected bool Shows(OrganizerItem item) => SpaceFilter is null || item.SpaceId == SpaceFilter;

    /// <summary>Where something new goes: the space being shown, or this person's own.</summary>
    protected string NewItemSpace => SpaceFilter ?? Engine.DefaultSpaceId;

    public abstract void Refresh();

    /// <summary>Brings one item into view and opens it, for a notification that was clicked.</summary>
    public virtual void Reveal(OrganizerItem item)
    {
    }
}

/// <summary>
/// Notes, calendar, reminders and boards - kept on this PC as well as the server, so all of it
/// works with the server unreachable. See OrganizerEngine for how the copy is kept in step.
/// </summary>
public partial class OrganizerView : UserControl
{
    private readonly OrganizerSession _session;
    private readonly Ui.Coalescer _refresh;
    private readonly CalendarPane _calendar;
    private readonly RemindersPane _reminders;
    private readonly NotesPane _notes;
    private readonly BoardsPane _boards;
    private bool _buildingFilter;

    public OrganizerView(OrganizerSession session)
    {
        _session = session;
        InitializeComponent();

        var engine = session.Engine;
        _calendar = new CalendarPane(engine);
        _reminders = new RemindersPane(engine);
        _notes = new NotesPane(engine);
        _boards = new BoardsPane(engine);
        CalendarTab.Content = _calendar;
        RemindersTab.Content = _reminders;
        NotesTab.Content = _notes;
        BoardsTab.Content = _boards;

        _refresh = new Ui.Coalescer(this, Refresh);
        Loaded += (_, _) =>
        {
            engine.Changed += _refresh.Request;
            Refresh();
        };
        Unloaded += (_, _) => engine.Changed -= _refresh.Request;
    }

    private IEnumerable<OrganizerPane> Panes => new OrganizerPane[] { _calendar, _reminders, _notes, _boards };

    private void Refresh()
    {
        var engine = _session.Engine;
        ApplyStatus(engine);
        BuildFilter(engine);
        foreach (var pane in Panes) pane.Refresh();
    }

    private void ApplyStatus(OrganizerEngine engine)
    {
        var state = engine.SyncState;
        var waiting = state.WaitingChanges > 0 ? $" · {state.WaitingChanges} change{(state.WaitingChanges == 1 ? "" : "s")} waiting to sync" : "";
        (SyncDot.Fill, SyncText.Text) = state switch
        {
            { NeedsSignIn: true } => (Palette.Amber, "Sign in to sync the Organizer." + waiting),
            { TurnedOff: true } => (Palette.Amber, "The Organizer is turned off on the server. Changes here are kept on this PC." + waiting),
            { Syncing: true } => (Palette.Cyan, "Syncing…" + waiting),
            { Online: true } => (Palette.Green,
                (state.LastSyncedAt is { } synced ? $"Up to date · synced {synced.LocalDateTime:t}" : "Up to date") + waiting),
            _ when !engine.IsConnected => (Palette.SecondaryText, "Offline - working from this PC's copy" + waiting),
            _ => (Palette.Amber, "Can't reach the server - working from this PC's copy" + waiting),
        };

        if (engine.ResetPrompt is { } prompt)
        {
            ResetBanner.Visibility = Visibility.Visible;
            var server = prompt.ServerCount is { } count ? $"it has {count} item{(count == 1 ? "" : "s")}" : "it may be new";
            ResetText.Text = $"This server doesn't have your Organizer data ({server}). It may have been rebuilt. " +
                             $"Upload this PC's copy ({prompt.LocalCount} item{(prompt.LocalCount == 1 ? "" : "s")}) to it, " +
                             "or replace this PC's copy with the server's?";
        }
        else
        {
            ResetBanner.Visibility = Visibility.Collapsed;
        }

        ErrorText.Text = state.LastError ?? "";
        ErrorBanner.Visibility = string.IsNullOrEmpty(state.LastError) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildFilter(OrganizerEngine engine)
    {
        var selected = (SpaceFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var spaces = engine.VisibleSpaces;
        var wanted = new List<(string Label, string? Id)> { ("All spaces", null) };
        wanted.AddRange(spaces.Select(space => (Ui.SpaceLabel(engine, space.Id), (string?)space.Id)));
        var current = SpaceFilter.Items.OfType<ComboBoxItem>().Select(item => ((string)item.Content, item.Tag as string)).ToList();
        if (current.SequenceEqual(wanted)) return;

        _buildingFilter = true;
        SpaceFilter.Items.Clear();
        foreach (var (label, id) in wanted) SpaceFilter.Items.Add(Ui.Option(label, id));
        Ui.Select(SpaceFilter, spaces.Any(space => space.Id == selected) ? selected : null);
        _buildingFilter = false;
        ApplyFilter();
    }

    private void OnSpaceFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingFilter) return;
        ApplyFilter();
        foreach (var pane in Panes) pane.Refresh();
    }

    private void ApplyFilter()
    {
        var filter = (SpaceFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        foreach (var pane in Panes) pane.SpaceFilter = filter;
    }

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        if (!_session.Engine.IsConnected)
        {
            await _session.ReconcileAsync();
            return;
        }
        await _session.Engine.SyncAsync();
    }

    private async void OnUploadCopyClicked(object sender, RoutedEventArgs e) =>
        await _session.Engine.ResolveResetAsync(upload: true);

    private async void OnUseServerCopyClicked(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm(this, "Replace everything in this PC's Organizer with the server's copy? Anything that exists only on this PC will be lost.")) return;
        await _session.Engine.ResolveResetAsync(upload: false);
    }

    private void OnDismissErrorClicked(object sender, RoutedEventArgs e) => _session.Engine.ClearError();

    /// <summary>Opens one item - for an alert notification that was clicked.</summary>
    public void Reveal(string itemId)
    {
        if (_session.Engine.Item(itemId) is not { } item) return;
        OrganizerPane pane = item.Kind switch
        {
            OrganizerKinds.Event => _calendar,
            OrganizerKinds.Reminder => _reminders,
            OrganizerKinds.Note => _notes,
            _ => _boards,
        };
        Tabs.SelectedItem = pane.Parent;
        pane.Reveal(item);
    }
}
