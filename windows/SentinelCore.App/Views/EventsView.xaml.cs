using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The one list of everything the cameras caught.
/// </summary>
/// <remarks>
/// This used to be two screens - "Feed" for AI-analysed alerts and "Triggers" for raw motion -
/// which meant a camera that forwards to AI analysis put the same moment on both, once with the
/// description and once with the clip. The server now joins them (see ActivityEndpoints), so a row
/// here is a thing that happened, carrying whatever exists for it: a snapshot always, a clip when
/// one was recorded, a description when something actually looked at the frame.
/// </remarks>
public partial class EventsView : UserControl
{
    private readonly AppModel _model;
    private readonly INavigator _navigator;
    private bool _suppressReload;

    public EventsView(AppModel model, INavigator? navigator = null)
    {
        _model = model;
        _navigator = navigator ?? NullNavigator.Instance;
        InitializeComponent();
        DataContext = model;

        ShowHiddenToggle.IsChecked = model.ShowHiddenActivity;
        _model.Activity.CollectionChanged += OnActivityChanged;
        _model.PropertyChanged += OnModelPropertyChanged;

        Apply();
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.Invoke(Apply);

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.LastUpdated) or nameof(AppModel.LastError)
            or nameof(AppModel.IsLoading))
        {
            Dispatcher.Invoke(Apply);
        }
    }

    private void Apply()
    {
        var search = SearchBox.Text.Trim();
        var notifiedOnly = NotifiedOnlyToggle.IsChecked == true;

        var rows = _model.Activity
            .Where(item =>
                // A motion row has no notification decision to satisfy, so filtering on one
                // excludes it - the same rule the server applies to its own analysis-only filters.
                (!notifiedOnly || item.Notified) &&
                (search.Length == 0 || Matches(item, search)))
            .Select(item => new ActivityRow(item))
            .ToList();

        // Selection is restored by id, not by index: a refresh replaces every row object, and
        // without this the detail pane would jump to whatever landed in the same position.
        var selectedId = (EventList.SelectedItem as ActivityRow)?.Id;
        EventList.ItemsSource = rows;
        if (selectedId is not null)
        {
            EventList.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId);
        }

        CountText.Text = $"{rows.Count} event{(rows.Count == 1 ? "" : "s")}";
        UpdatedText.Text = _model.LastUpdated is { } updated ? $"Updated {updated.LocalDateTime:t}" : "";

        ErrorText.Text = _model.LastError ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(ErrorText.Text) ? Visibility.Collapsed : Visibility.Visible;

        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = EmptyTitleText(search, notifiedOnly);
        EmptyDescription.Text = EmptyDescriptionText(search, notifiedOnly);
    }

    private static bool Matches(ActivityItem item, string search) =>
        Contains(item.Summary, search) ||
        Contains(item.Camera, search) ||
        Contains(item.ZoneName, search) ||
        Contains(item.EventType, search);

    private static bool Contains(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private string EmptyTitleText(string search, bool notifiedOnly)
    {
        if (_model.IsLoading) return "Loading events…";
        if (search.Length > 0 || notifiedOnly) return "No matching events";
        return "No events";
    }

    private string EmptyDescriptionText(string search, bool notifiedOnly)
    {
        if (search.Length > 0 || notifiedOnly) return "Try changing your search or notification filter.";
        if (!_model.ShowHiddenActivity)
        {
            return "Anything the cameras catch will appear here. Motion the AI rejected is hidden - " +
                   "turn on Show hidden to see it.";
        }
        return "Anything the cameras catch will appear here.";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Apply();

    private void OnNotifiedOnlyToggled(object sender, RoutedEventArgs e) => Apply();

    /// <summary>
    /// Changing this changes what the server is asked for, so it reloads rather than filtering what
    /// is already here - the hidden rows were never fetched.
    /// </summary>
    private void OnShowHiddenToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressReload) return;
        _model.ShowHiddenActivity = ShowHiddenToggle.IsChecked == true;
        _ = _model.ConnectAsync();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) =>
        _ = _model.ConnectAsync(resetNetworkSession: true);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is not ActivityRow row) return;

        // Pushed into the same window rather than opened as a dialog, so coming back leaves the
        // list exactly as it was - same scroll position, same search, same filters.
        _navigator.Push(new ActivityDetailView(_model, row.Item), row.Camera);

        // Deselected afterwards so returning and clicking the same row reopens it rather than
        // doing nothing.
        EventList.SelectedItem = null;
    }

    /// <summary>Opens one alert, used when a tray notification is clicked.</summary>
    public void Select(ActivityItem item)
    {
        _suppressReload = true;
        SearchBox.Text = "";
        NotifiedOnlyToggle.IsChecked = false;
        _suppressReload = false;
        Apply();

        if (EventList.ItemsSource is IEnumerable<ActivityRow> rows &&
            rows.FirstOrDefault(row => row.Id == item.Id) is { } match)
        {
            EventList.SelectedItem = match;
        }
    }
}
