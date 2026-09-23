using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// A month of events beside the agenda for the chosen day. Repeating events are expanded here from
/// their rule, the same way every other client does it, so an event lands on the same days
/// everywhere.
/// </summary>
public sealed class CalendarPane : OrganizerPane
{
    private readonly TextBlock _monthTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _month = new();
    private readonly StackPanel _agenda = new();
    private readonly TextBlock _agendaTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText };
    private DateTime _shownMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;
    private readonly Button _holidaysButton;

    public CalendarPane(OrganizerEngine engine) : base(engine)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 16, 0) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(Ui.Icon("", "Previous month", () => Move(-1)));
        var today = Ui.Button("Today", () =>
        {
            _shownMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            _selected = DateTime.Today;
            Refresh();
        });
        today.Margin = new Thickness(6, 0, 6, 0);
        nav.Children.Add(today);
        nav.Children.Add(Ui.Icon("", "Next month", () => Move(1)));
        _holidaysButton = Ui.Button("Holidays…", OpenHolidaySettings, tooltip: "Show holidays from other calendars - none are on until you choose");
        _holidaysButton.Margin = new Thickness(12, 0, 0, 0);
        nav.Children.Add(_holidaysButton);
        DockPanel.SetDock(nav, Dock.Right);
        header.Children.Add(nav);
        header.Children.Add(_monthTitle);
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);
        left.Children.Add(_month);
        Grid.SetColumn(left, 0);
        layout.Children.Add(left);

        var right = new DockPanel();
        var agendaHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var add = Ui.Button("+ Event", () => NewEvent(_selected), "PrimaryButton");
        DockPanel.SetDock(add, Dock.Right);
        agendaHeader.Children.Add(add);
        agendaHeader.Children.Add(_agendaTitle);
        DockPanel.SetDock(agendaHeader, Dock.Top);
        right.Children.Add(agendaHeader);
        right.Children.Add(new ScrollViewer { Content = _agenda, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);

        Content = layout;
        Focusable = true;
        PreviewMouseWheel += (_, e) =>
        {
            // Wheel over the month flips months, as most calendar apps do.
            if (!_month.IsMouseOver) return;
            Move(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        };
    }

    private void Move(int months)
    {
        _shownMonth = _shownMonth.AddMonths(months);
        if (_selected.Year != _shownMonth.Year || _selected.Month != _shownMonth.Month) _selected = _shownMonth;
        Refresh();
    }

    private List<Occurrence> Occurrences(DateTime from, DateTime to)
    {
        var zone = Ui.Zone;
        var start = OrganizerDates.StartOfDay(from, zone);
        var end = OrganizerDates.StartOfDay(to, zone);
        return Engine.Items(OrganizerKinds.Event)
            .Where(Shows)
            .SelectMany(item => Recurrence.Occurrences(item, start, end, zone))
            .OrderBy(occurrence => occurrence.Item.Bool("allDay") ? 0 : 1)
            .ThenBy(occurrence => occurrence.Start)
            .ThenBy(occurrence => occurrence.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public override void Refresh()
    {
        var culture = CultureInfo.CurrentCulture;
        _monthTitle.Text = _shownMonth.ToString("MMMM yyyy", culture);
        var firstDay = (int)culture.DateTimeFormat.FirstDayOfWeek;
        var gridStart = _shownMonth.AddDays(-(((int)_shownMonth.DayOfWeek - firstDay + 7) % 7));
        var gridEnd = gridStart.AddDays(42);
        var occurrences = Occurrences(gridStart, gridEnd);
        var holidays = Engine.HolidaysBetween(OrganizerDates.StartOfDay(gridStart, Ui.Zone), OrganizerDates.StartOfDay(gridEnd, Ui.Zone));
        _holidaysButton.Content = Engine.HolidaySets.Count == 0 ? "Holidays…" : $"Holidays ({Engine.HolidaySets.Count})…";

        _month.Children.Clear();
        _month.RowDefinitions.Clear();
        _month.ColumnDefinitions.Clear();
        for (var column = 0; column < 7; column++) _month.ColumnDefinitions.Add(new ColumnDefinition());
        _month.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < 6; row++) _month.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 76 });

        for (var column = 0; column < 7; column++)
        {
            var name = culture.DateTimeFormat.AbbreviatedDayNames[(firstDay + column) % 7];
            var label = Ui.Caption(name.ToUpperInvariant());
            label.Margin = new Thickness(6, 0, 0, 4);
            Grid.SetColumn(label, column);
            _month.Children.Add(label);
        }

        for (var index = 0; index < 42; index++)
        {
            var day = gridStart.AddDays(index);
            var cell = DayCell(day, occurrences.Where(o => Covers(o, day)).ToList(),
                holidays.Where(h => CoversHoliday(h.Start, h.End, day)).Select(h => h.Holiday).ToList());
            Grid.SetRow(cell, 1 + index / 7);
            Grid.SetColumn(cell, index % 7);
            _month.Children.Add(cell);
        }

        BuildAgenda(occurrences, holidays);
    }

    private static bool CoversHoliday(DateTimeOffset start, DateTimeOffset end, DateTime day)
    {
        var zone = Ui.Zone;
        return start < OrganizerDates.StartOfDay(day.AddDays(1), zone) && end > OrganizerDates.StartOfDay(day, zone);
    }

    private static Brush HolidayBrush(string setId, byte alpha = 255) => Ui.ColorBrush(HolidayColors.Hex(setId), alpha) ?? Palette.SecondaryText;

    private Border HolidayChip(Holiday holiday)
    {
        var chip = new Border
        {
            Background = HolidayBrush(holiday.SetId, 50),
            BorderBrush = HolidayBrush(holiday.SetId),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(4, 0, 2, 0),
            Margin = new Thickness(0, 0, 0, 2),
            Child = new TextBlock { Text = holiday.Name, FontSize = 11, FontStyle = FontStyles.Italic, Foreground = Palette.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis },
            ToolTip = $"{holiday.Name} - {Engine.HolidaySetName(holiday.SetId)}",
            Cursor = Cursors.Hand,
        };
        // On the press, and handled, so the day cell underneath doesn't rebuild the grid first.
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ShowHoliday(holiday);
        };
        return chip;
    }

    private Border HolidayRow(Holiday holiday, DateTimeOffset start, bool showDay)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = holiday.Name, Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, FontStyle = FontStyles.Italic, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = showDay ? Ui.When(start, true) : holiday.DayCount > 1 ? $"{holiday.DayCount} days" : "Holiday";
        if (holiday.Note is { Length: > 0 } note) details += $" · {note}";
        text.Children.Add(Ui.Caption(details));
        var set = Ui.Caption(Engine.HolidaySetName(holiday.SetId));
        set.Foreground = HolidayBrush(holiday.SetId);
        text.Children.Add(set);
        var row = new Border
        {
            Background = Ui.Brush("CardBackground"),
            BorderBrush = HolidayBrush(holiday.SetId),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = text,
            Cursor = Cursors.Hand,
        };
        row.MouseLeftButtonUp += (_, _) => ShowHoliday(holiday);
        return row;
    }

    private void ShowHoliday(Holiday holiday)
    {
        var dates = holiday.Span(Ui.Zone) is { } span
            ? holiday.DayCount > 1
                ? $"{Ui.When(span.Start, true)} – {Ui.When(OrganizerDates.At(span.End.DateTime.AddDays(-1), Ui.Zone), true)}"
                : Ui.When(span.Start, true)
            : holiday.Date;
        var message = $"{dates}\n\n" + (holiday.Note is { Length: > 0 } note ? $"{note}\n\n" : "") +
                      $"From {Engine.HolidaySetName(holiday.SetId)}. Holidays can't be edited - turn the whole calendar off in Holidays… if you don't want it.";
        MessageBox.Show(Window.GetWindow(this), message, holiday.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenHolidaySettings() => new HolidaySettingsWindow(Engine) { Owner = Window.GetWindow(this) }.ShowDialog();

    /// <summary>Whether an occurrence is on a day - an all-day or overnight one covers every day it spans.</summary>
    private static bool Covers(Occurrence occurrence, DateTime day)
    {
        var zone = Ui.Zone;
        var start = OrganizerDates.StartOfDay(day, zone);
        var end = OrganizerDates.StartOfDay(day.AddDays(1), zone);
        return occurrence.Start < end && occurrence.End > start;
    }

    private Border DayCell(DateTime day, List<Occurrence> events, List<Holiday> holidays)
    {
        var inMonth = day.Month == _shownMonth.Month;
        var isToday = day == DateTime.Today;
        var isSelected = day == _selected;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = day.Day.ToString(CultureInfo.CurrentCulture),
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isToday ? Palette.Green : inMonth ? Palette.PrimaryText : Palette.SecondaryText,
            Margin = new Thickness(2, 0, 0, 2),
        });
        // Holidays first, then events, three lines in all before "+n more".
        const int shown = 3;
        var total = holidays.Count + events.Count;
        var room = total > shown + 1 ? shown : shown + 1;
        foreach (var holiday in holidays.Take(room)) stack.Children.Add(HolidayChip(holiday));
        room = Math.Max(0, room - holidays.Count);
        foreach (var occurrence in events.Take(room))
        {
            var color = Ui.ColorBrush(occurrence.Item.String("color")) ?? Palette.Cyan;
            var label = occurrence.Item.Bool("allDay")
                ? occurrence.Item.Title
                : $"{OrganizerDates.InZone(occurrence.Start, Ui.Zone).ToString("t", CultureInfo.CurrentCulture)} {occurrence.Item.Title}";
            var chip = new Border
            {
                Background = occurrence.Item.Bool("allDay") ? Ui.ColorBrush(occurrence.Item.String("color") ?? "#4ae8ff", 70) : Brushes.Transparent,
                BorderBrush = color,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(4, 0, 2, 0),
                Margin = new Thickness(0, 0, 0, 2),
                Child = new TextBlock { Text = label, FontSize = 11, Foreground = Palette.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = label,
                Cursor = Cursors.Hand,
            };
            var target = occurrence;
            // On the press, and handled, so the day cell underneath never sees it: the cell rebuilds
            // the grid on its press, and a release would then land on whatever moved under it.
            chip.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                Open(target);
            };
            stack.Children.Add(chip);
        }
        if (total > shown + 1) stack.Children.Add(Ui.Caption($"+{total - shown} more"));

        var cell = new Border
        {
            BorderBrush = isSelected ? Palette.Green : Ui.Brush("CardBorder"),
            BorderThickness = new Thickness(isSelected ? 1.5 : 0.5),
            Background = inMonth ? Ui.Brush("CardBackground") : Brushes.Transparent,
            Padding = new Thickness(4),
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = stack,
            Cursor = Cursors.Hand,
            ToolTip = "Click to see the day · double-click to add an event",
        };
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                NewEvent(day);
                return;
            }
            _selected = day;
            if (day.Month != _shownMonth.Month) _shownMonth = new DateTime(day.Year, day.Month, 1);
            Refresh();
        };
        return cell;
    }

    private void BuildAgenda(List<Occurrence> occurrences, List<(Holiday Holiday, DateTimeOffset Start, DateTimeOffset End)> holidays)
    {
        var culture = CultureInfo.CurrentCulture;
        _agendaTitle.Text = _selected == DateTime.Today ? "Today" : _selected.ToString("dddd d MMMM", culture);
        _agenda.Children.Clear();
        var day = occurrences.Where(o => Covers(o, _selected)).ToList();
        var dayHolidays = holidays.Where(h => CoversHoliday(h.Start, h.End, _selected)).ToList();
        foreach (var holiday in dayHolidays) _agenda.Children.Add(HolidayRow(holiday.Holiday, holiday.Start, showDay: false));
        if (day.Count == 0 && dayHolidays.Count == 0)
        {
            _agenda.Children.Add(Ui.Caption("Nothing on this day."));
        }
        foreach (var occurrence in day) _agenda.Children.Add(AgendaRow(occurrence));

        // What's coming, so a quiet day doesn't hide next week.
        var from = OrganizerDates.StartOfDay(_selected.AddDays(1), Ui.Zone);
        var to = OrganizerDates.StartOfDay(_selected.AddDays(15), Ui.Zone);
        var upcoming = Occurrences(_selected.AddDays(1), _selected.AddDays(15))
            .Select(o => (Start: o.Start, Row: (UIElement)AgendaRow(o, showDay: true)))
            .Concat(Engine.HolidaysBetween(from, to).Where(h => h.Start >= from)
                .Select(h => (Start: h.Start, Row: (UIElement)HolidayRow(h.Holiday, h.Start, showDay: true))))
            .OrderBy(entry => entry.Start)
            .Take(12)
            .ToList();
        if (upcoming.Count == 0) return;
        var heading = Ui.Heading("Coming up");
        heading.Margin = new Thickness(0, 18, 0, 8);
        _agenda.Children.Add(heading);
        foreach (var entry in upcoming) _agenda.Children.Add(entry.Row);
    }

    private Border AgendaRow(Occurrence occurrence, bool showDay = false)
    {
        var item = occurrence.Item;
        var allDay = item.Bool("allDay");
        var zone = Ui.Zone;
        var start = OrganizerDates.InZone(occurrence.Start, zone);
        var end = OrganizerDates.InZone(occurrence.End, zone);
        var when = allDay
            ? (showDay ? Ui.When(occurrence.Start, true) : "All day")
            : (showDay ? Ui.When(occurrence.Start, false) : start.ToString("t", CultureInfo.CurrentCulture)) + $" – {end.ToString("t", CultureInfo.CurrentCulture)}";
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Title.Length == 0 ? "(No title)" : item.Title, Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = when;
        if (item.String("location") is { Length: > 0 } location) details += $" · {location}";
        if (item.String("recurrence") is { Length: > 0 }) details += " · repeats";
        text.Children.Add(Ui.Caption(details));
        if (SpaceFilter is null) text.Children.Add(Ui.Caption(Ui.SpaceLabel(Engine, item.SpaceId)));
        var attachments = Engine.Children(item.Id, OrganizerKinds.Attachment).Count;
        if (attachments > 0) text.Children.Add(Ui.Caption($"{attachments} attachment{(attachments == 1 ? "" : "s")}"));

        var row = new Border
        {
            Background = Ui.Brush("CardBackground"),
            BorderBrush = Ui.ColorBrush(item.String("color")) ?? Palette.Cyan,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = text,
            Cursor = Cursors.Hand,
        };
        row.MouseLeftButtonUp += (_, _) => Open(occurrence);
        return row;
    }

    private void NewEvent(DateTime day)
    {
        // The next whole hour on today, nine in the morning on any other day.
        var hour = day == DateTime.Today ? Math.Min(23, DateTime.Now.Hour + 1) : 9;
        var start = OrganizerDates.At(day.Date.AddHours(hour), Ui.Zone);
        var defaults = new Dictionary<string, object?>
        {
            ["start"] = OrganizerDates.FieldValue(start, false),
            ["end"] = OrganizerDates.FieldValue(start.AddHours(1), false),
        };
        new ItemEditorWindow(Engine, OrganizerKinds.Event, null, defaults, NewItemSpace).ShowDialog();
    }

    /// <summary>
    /// Opens an event. For one occurrence of a repeating event, asks whether the change is to that
    /// day or the whole series; a single day is split off as its own event linked to the series,
    /// and the series skips that day.
    /// </summary>
    private void Open(Occurrence occurrence)
    {
        var series = occurrence.Item;
        if (occurrence.OriginalStart is not { } originalStart)
        {
            new ItemEditorWindow(Engine, OrganizerKinds.Event, series).ShowDialog();
            return;
        }
        var choice = MessageBox.Show(
            Window.GetWindow(this),
            $"\"{series.Title}\" repeats. Change only the one on {Ui.When(occurrence.Start, series.Bool("allDay"))}?\n\n" +
            "Yes: just this one.   No: every one.",
            "Repeating event",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.No)
        {
            new ItemEditorWindow(Engine, OrganizerKinds.Event, series).ShowDialog();
            return;
        }
        if (choice != MessageBoxResult.Yes) return;

        var allDay = series.Bool("allDay");
        var fields = series.Fields.Where(pair => pair.Key is not ("recurrence" or "exdates"))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        fields["start"] = OrganizerDates.FieldValue(occurrence.Start, allDay);
        fields["end"] = OrganizerDates.FieldValue(occurrence.End, allDay);
        fields["seriesId"] = series.Id;
        fields["originalStart"] = originalStart;
        var editor = new ItemEditorWindow(Engine, OrganizerKinds.Event, null, fields, series.SpaceId);
        if (editor.ShowDialog() != true || editor.SavedId is null) return;
        // Read again: another device may have skipped a day while the editor was open.
        var exdates = (Engine.Item(series.Id) ?? series).List("exdates").ToList();
        exdates.Add(originalStart);
        Engine.Update(series.Id, "exdates", exdates);
    }

    public override void Reveal(OrganizerItem item)
    {
        if (Recurrence.Span(item, Ui.Zone) is { } span)
        {
            var day = OrganizerDates.InZone(span.Start, Ui.Zone).Date;
            _selected = day;
            _shownMonth = new DateTime(day.Year, day.Month, 1);
            Refresh();
        }
        new ItemEditorWindow(Engine, OrganizerKinds.Event, item).ShowDialog();
    }
}
