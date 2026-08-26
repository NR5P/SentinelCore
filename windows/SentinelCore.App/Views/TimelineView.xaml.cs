using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// A day of one camera, laid out end to end: the clips that were recorded, the moments the AI had
/// something to say about, and a cursor that moves through both.
/// </summary>
/// <remarks>
/// The day is not continuous footage - the server keeps a clip around each trigger and nothing in
/// between - so this is not a scrubber over one long file. The cursor owns the position; whichever
/// clip covers it gets loaded and seeked to the right offset, and reaching the end of one jumps
/// straight to the next rather than sitting on a frozen frame. The arithmetic behind all of that
/// lives in <see cref="TimelinePlayback"/>, where it can be tested.
///
/// The reviewed flag is shared state, not a local one: marking an event reviewed here marks it
/// reviewed in the dashboard and on every other client, which is the point of having it.
/// </remarks>
public partial class TimelineView : UserControl
{
    private readonly AppModel _model;
    private readonly TimelineFilter _filter = new();

    private RecordingTimelineDay? _day;
    private DateTimeOffset _cursor;
    private TimelineRecordingSegment? _loadedSegment;
    private bool _isPlaying;
    private bool _draggingRail;

    // Set while the cursor is being moved by the player's own position reports, so syncing does not
    // turn around and re-seek the clip it just heard from.
    private bool _followingPlayer;

    public TimelineView(AppModel model)
    {
        _model = model;
        InitializeComponent();

        Watermark.IsLicensed = model.IsLicensed;
        DayPicker.SelectedDate = DateTime.Today;
        ReviewBox.SelectedIndex = 0;

        RailCanvas.SizeChanged += (_, _) => DrawRail();

        // Subscribed on Loaded rather than in the constructor, because Unloaded below drops the
        // subscriptions and this view is built once and reused. The shell swaps it out of the
        // visual tree when another section is chosen, which raises Unloaded; wiring these up here
        // is what makes them come back. Doing it in the constructor left the second visit to the
        // Timeline with a cursor that no longer tracked playback, no jump to the next clip at the
        // end of a segment, and no message when a clip failed - all of it silent.
        Loaded += async (_, _) =>
        {
            Player.ClipPositionChanged += OnClipPositionChanged;
            Player.ClipEnded += OnClipEnded;
            Player.ClipFailed += OnClipFailed;
            await LoadCamerasAsync();
        };
        Unloaded += (_, _) =>
        {
            Player.ClipPositionChanged -= OnClipPositionChanged;
            Player.ClipEnded -= OnClipEnded;
            Player.ClipFailed -= OnClipFailed;
            Player.Stop();
        };
    }

    // ------------------------------------------------------------------ Rows

    public sealed record EventRow(TimelineSecurityEvent Event)
    {
        public string Time => Event.OccurredAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        public string Category => Event.Category == "none" ? "NOTHING FOUND" : Event.Category.ToUpperInvariant();

        public string Severity => Event.Severity.ToUpperInvariant();

        public string Summary => string.IsNullOrWhiteSpace(Event.Summary) ? Event.EventType : Event.Summary;

        public string? ImageUrl => Event.HasImage ? $"/api/events/{Event.Id}/image" : null;

        // Worth saying, because an event with no clip is one the cursor can reach but not play.
        public string ClipLabel => Event.HasClip ? "" : "NO CLIP";

        public string ReviewedGlyph => Event.Reviewed ? "" : "";

        public string ReviewedTip => Event.Reviewed ? "Mark unreviewed" : "Mark reviewed";

        public Brush ReviewedBrush => Event.Reviewed ? Palette.Green : Palette.SecondaryText;

        public Brush CategoryBrush => Event.Category switch
        {
            "person" => Palette.Cyan,
            "vehicle" => Palette.Green,
            "plate" => Palette.Amber,
            "package" => Palette.Purple,
            "animal" => Palette.Orange,
            _ => Palette.SecondaryText,
        };

        public Brush SeverityBrush => Event.Severity switch
        {
            "critical" => Palette.Red,
            "high" => Palette.Amber,
            "medium" => Palette.Cyan,
            _ => Palette.SecondaryText,
        };
    }

    // ------------------------------------------------------------------ Loading

    private async Task LoadCamerasAsync()
    {
        try
        {
            var cameras = await _model.Client.TimelineCamerasAsync();
            // The camera list the server offers for review, plus any camera the loaded events
            // mention - a camera can have events worth reviewing before it has recordings.
            var names = cameras
                .Concat(_model.Activity.Select(item => item.Camera))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CameraBox.ItemsSource = names;
            if (names.Count > 0)
            {
                CameraBox.SelectedIndex = 0;
            }
            else
            {
                Message("No camera on this server has anything to review yet.");
            }
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async Task LoadDayAsync()
    {
        if (CameraBox.SelectedItem is not string camera) return;
        var date = (DayPicker.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        SetPlaying(false);
        _loadedSegment = null;
        _day = null;
        DrawRail();

        try
        {
            _day = await _model.Client.TimelineAsync(camera, date);
            _cursor = TimelinePlayback.InitialCursor(_day);
            ApplySummary();
            ApplyEvents();
            SyncPlayer(force: true);
            Message(null);
        }
        catch (Exception error)
        {
            _day = null;
            EventList.ItemsSource = null;
            ApplySummary();
            DrawRail();
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void ApplySummary()
    {
        if (_day is not { } day)
        {
            ClipsSummary.Text = "";
            EventsSummary.Text = "";
            return;
        }

        ClipsSummary.Text = day.Recording switch
        {
            { Configured: false } => "No trigger clips are recorded for this camera",
            { Available: false } => day.Recording.Error is { Length: > 0 }
                ? $"Clips unavailable: {day.Recording.Error}"
                : "Clips are configured but unavailable for this day",
            _ => $"{day.Recording.Segments.Count} trigger clip" +
                 $"{(day.Recording.Segments.Count == 1 ? "" : "s")} · " +
                 $"{day.Recording.PreSeconds}s before, {day.Recording.PostSeconds}s after",
        };

        var showing = _filter.Apply(day.Events).Count();
        EventsSummary.Text = $"{showing} of {day.Events.Count} AI events";
    }

    private void ApplyEvents()
    {
        if (_day is not { } day)
        {
            EventList.ItemsSource = null;
            return;
        }

        var rows = _filter.Apply(day.Events)
            .OrderBy(item => item.OccurredAt)
            .Select(item => new EventRow(item))
            .ToList();

        var selectedId = (EventList.SelectedItem as EventRow)?.Event.Id;
        EventList.ItemsSource = rows;
        if (selectedId is { } id)
        {
            EventList.SelectedItem = rows.FirstOrDefault(row => row.Event.Id == id);
        }

        ApplySummary();
        DrawRail();
    }

    private IReadOnlyList<TimelineSecurityEvent> VisibleEvents =>
        (EventList.ItemsSource as IEnumerable<EventRow>)?.Select(row => row.Event).ToList()
        ?? new List<TimelineSecurityEvent>();

    // ------------------------------------------------------------------ Playback

    private void SetCursor(DateTimeOffset value, bool force = false)
    {
        _cursor = value;
        UpdateCursorReadout();
        DrawRail();
        SyncPlayer(force);
    }

    /// <summary>
    /// Puts the player where the cursor is. Loading a different clip is a navigation; moving within
    /// the one already loaded is a seek, and only when it has drifted enough to be worth one - the
    /// cursor is usually just echoing the player's own position.
    /// </summary>
    private void SyncPlayer(bool force = false)
    {
        var segment = TimelinePlayback.SegmentAt(_day, _cursor);

        if (segment is null)
        {
            if (_loadedSegment is not null || force)
            {
                _loadedSegment = null;
                SetPlaying(false);
                Player.ShowMessage(_day?.Recording is { Configured: true, Available: false }
                    ? _day.Recording.Error ?? "Clips are unavailable for this day."
                    : "No clip covers this moment. Pick a highlighted stretch on the timeline, " +
                      "or an event from the list.");
            }
            SegmentText.Text = "";
            return;
        }

        var offset = TimelinePlayback.OffsetInto(segment, _cursor);
        SegmentText.Text =
            $"clip {segment.Start.LocalDateTime:HH:mm:ss}–{segment.End.LocalDateTime:HH:mm:ss}";

        if (segment.TriggerEventId != _loadedSegment?.TriggerEventId)
        {
            _loadedSegment = segment;
            // The clip lives with the trigger that recorded it, not with the AI event - different
            // store, different id.
            _ = Player.PlayClipAsync(
                _model,
                $"/api/triggers/{segment.TriggerEventId}/clip",
                offset,
                _isPlaying);
            return;
        }

        if (!_followingPlayer) Player.SeekClip(offset);
    }

    private void OnClipPositionChanged(double seconds) => Dispatcher.Invoke(() =>
    {
        if (!_isPlaying || _loadedSegment is not { } segment || _draggingRail) return;

        var updated = segment.Start.AddSeconds(seconds);
        if (updated >= segment.End)
        {
            AdvancePastSegment();
            return;
        }

        _followingPlayer = true;
        try
        {
            SetCursor(updated);
        }
        finally
        {
            _followingPlayer = false;
        }
    });

    private void OnClipEnded() => Dispatcher.Invoke(AdvancePastSegment);

    private void OnClipFailed() => Dispatcher.Invoke(() =>
    {
        SetPlaying(false);
        Message("That clip could not be played. It may have been cleaned up by the server's retention rules.");
    });

    /// <summary>
    /// Reached the end of the clip covering this moment. There is no footage in the gap before the
    /// next one, so jump it rather than trying to play through it.
    /// </summary>
    private void AdvancePastSegment()
    {
        if (_loadedSegment is not { } segment) return;

        if (TimelinePlayback.SegmentAfter(_day, segment) is not { } next)
        {
            SetPlaying(false);
            return;
        }

        SetCursor(next.Start);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        // Play and pause glyphs.
        PlayGlyph.Text = playing ? "" : "";
        PlayButton.ToolTip = playing ? "Pause" : "Play";
        Player.SetPlaying(playing);
    }

    private void UpdateCursorReadout() =>
        CursorText.Text = _cursor == default
            ? ""
            : _cursor.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ The rail

    private void DrawRail()
    {
        RailCanvas.Children.Clear();

        var width = RailCanvas.ActualWidth;
        var height = RailCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (_day is not { } day || day.End <= day.Start)
        {
            RailNotice.Text = "Pick a camera and a day.";
            return;
        }
        RailNotice.Text = "";

        // Hour ticks, so a mark two-thirds along reads as "about four in the afternoon" rather than
        // as an unlabelled position.
        for (var hour = 0; hour <= 24; hour += 3)
        {
            var moment = day.Start.AddHours(hour);
            if (moment > day.End) break;
            var x = TimelinePlayback.Position(day, moment) * width;

            RailCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = height - 14,
                Y2 = height,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x3A)),
                StrokeThickness = 1,
            });

            var label = new TextBlock
            {
                Text = moment.LocalDateTime.ToString("HH", CultureInfo.InvariantCulture),
                Foreground = Palette.SecondaryText,
                FontSize = 9.5,
            };
            Canvas.SetLeft(label, Math.Min(x + 3, width - 16));
            Canvas.SetTop(label, height - 14);
            RailCanvas.Children.Add(label);
        }

        // Recorded stretches.
        foreach (var segment in day.Recording.Segments)
        {
            var left = TimelinePlayback.Position(day, segment.Start) * width;
            var right = TimelinePlayback.Position(day, segment.End) * width;
            // A thirty-second clip is a fraction of a pixel on a day-long rail, so every one gets a
            // visible minimum - otherwise a day full of clips looks empty.
            var barWidth = Math.Max(right - left, 3);

            RailCanvas.Children.Add(Positioned(new Rectangle
            {
                Width = barWidth,
                Height = 18,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(0xB3, 0x1F, 0x9C, 0x61)),
                ToolTip = $"{segment.Start.LocalDateTime:HH:mm:ss} – {segment.End.LocalDateTime:HH:mm:ss}",
            }, left, 8));
        }

        // Event marks, only the ones the filters are showing.
        foreach (var item in VisibleEvents)
        {
            var x = TimelinePlayback.Position(day, item.OccurredAt) * width;
            var critical = item.Severity == "critical";
            RailCanvas.Children.Add(Positioned(new Rectangle
            {
                Width = critical ? 4 : 2,
                Height = 22,
                RadiusX = 1,
                RadiusY = 1,
                Fill = item.Reviewed ? Palette.SecondaryText : SeverityColour(item.Severity),
                ToolTip = $"{item.OccurredAt.LocalDateTime:HH:mm:ss} · {item.Summary}",
            }, x - (critical ? 2 : 1), 30));
        }

        // The cursor.
        RailCanvas.Children.Add(Positioned(new Rectangle
        {
            Width = 2,
            Height = height - 12,
            Fill = Palette.PrimaryText,
        }, TimelinePlayback.Position(day, _cursor) * width - 1, 4));
    }

    private static Brush SeverityColour(string severity) => severity switch
    {
        "critical" => Palette.Red,
        "high" => Palette.Amber,
        "medium" => Palette.Cyan,
        _ => Palette.SecondaryText,
    };

    private static UIElement Positioned(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }

    private void OnRailPressed(object sender, MouseButtonEventArgs e)
    {
        if (_day is null) return;
        _draggingRail = true;
        RailCanvas.CaptureMouse();
        ScrubTo(e.GetPosition(RailCanvas).X);
    }

    private void OnRailMoved(object sender, MouseEventArgs e)
    {
        if (!_draggingRail) return;
        ScrubTo(e.GetPosition(RailCanvas).X);
    }

    private void OnRailReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingRail) return;
        _draggingRail = false;
        RailCanvas.ReleaseMouseCapture();
        // The seek is applied on release: dragging across a day would otherwise load a different
        // clip for every pixel crossed.
        SyncPlayer(force: true);
    }

    private void ScrubTo(double x)
    {
        if (_day is not { } day) return;
        var width = RailCanvas.ActualWidth;
        if (width <= 0) return;

        _cursor = TimelinePlayback.MomentAt(day, x / width);
        UpdateCursorReadout();
        DrawRail();
    }

    // ------------------------------------------------------------------ Controls

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        // Pressing play while sitting in a gap should do something useful rather than nothing:
        // move to the next clip and start there.
        if (!_isPlaying && TimelinePlayback.SegmentAt(_day, _cursor) is null && _day is { } day)
        {
            var next = day.Recording.Segments
                .Where(segment => segment.Start > _cursor)
                .OrderBy(segment => segment.Start)
                .FirstOrDefault();
            if (next is not null)
            {
                SetPlaying(true);
                SetCursor(next.Start, force: true);
                return;
            }
        }

        SetPlaying(!_isPlaying);
    }

    private void OnPreviousEventClicked(object sender, RoutedEventArgs e) => Jump(-1);

    private void OnNextEventClicked(object sender, RoutedEventArgs e) => Jump(1);

    private void Jump(int direction)
    {
        if (TimelinePlayback.JumpTarget(_day, VisibleEvents, _cursor, direction) is not { } target) return;
        SetCursor(target, force: true);
    }

    private void OnNowClicked(object sender, RoutedEventArgs e)
    {
        DayPicker.SelectedDate = DateTime.Today;
        if (_day is { } day) SetCursor(TimelinePlayback.InitialCursor(day), force: true);
    }

    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is not EventRow row) return;
        // Land at the start of the clip recorded for it, so the approach is included rather than
        // starting mid-stride.
        SetPlaying(false);
        SetCursor(TimelinePlayback.PositionFor(_day, row.Event), force: true);
    }

    private async void OnToggleReviewedClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EventRow row) return;

        var target = !row.Event.Reviewed;
        try
        {
            await _model.Client.SetReviewedAsync(row.Event.Id, target);
            row.Event.Reviewed = target;
            ApplyEvents();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    // ------------------------------------------------------------------ Filters

    private void OnObjectsClicked(object sender, RoutedEventArgs e) =>
        ShowFilterMenu(ObjectsButton, TimelineFilter.Categories, _filter.SelectedCategories);

    private void OnSeverityClicked(object sender, RoutedEventArgs e) =>
        ShowFilterMenu(SeverityButton, TimelineFilter.Severities, _filter.SelectedSeverities);

    private void ShowFilterMenu(UIElement anchor, IReadOnlyList<string> values, HashSet<string> selection)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var value in values)
        {
            var item = new MenuItem
            {
                Header = TimelineFilter.Label(value),
                IsCheckable = true,
                IsChecked = selection.Contains(value),
                StaysOpenOnClick = true,
            };
            var captured = value;
            item.Click += (_, _) =>
            {
                if (item.IsChecked) selection.Add(captured);
                else selection.Remove(captured);
                ApplyEvents();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnReviewChanged(object sender, SelectionChangedEventArgs e)
    {
        _filter.Review = ReviewBox.SelectedIndex switch
        {
            1 => ReviewFilter.Unreviewed,
            2 => ReviewFilter.Reviewed,
            _ => ReviewFilter.All,
        };
        ApplyEvents();
    }

    private void OnResetFiltersClicked(object sender, RoutedEventArgs e)
    {
        _filter.Reset();
        ReviewBox.SelectedIndex = 0;
        ApplyEvents();
    }

    // ------------------------------------------------------------------ Day and camera

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e) => _ = LoadDayAsync();

    private void OnDayChanged(object sender, SelectionChangedEventArgs e)
    {
        NextDayButton.IsEnabled = (DayPicker.SelectedDate ?? DateTime.Today).Date < DateTime.Today;
        _ = LoadDayAsync();
    }

    private void OnPreviousDayClicked(object sender, RoutedEventArgs e) =>
        DayPicker.SelectedDate = (DayPicker.SelectedDate ?? DateTime.Today).AddDays(-1);

    private void OnNextDayClicked(object sender, RoutedEventArgs e)
    {
        // There is no footage in the future; the button turns itself off on today.
        var next = (DayPicker.SelectedDate ?? DateTime.Today).AddDays(1);
        if (next.Date > DateTime.Today) return;
        DayPicker.SelectedDate = next;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _ = LoadDayAsync();

    private void Message(string? message)
    {
        MessageText.Text = message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
