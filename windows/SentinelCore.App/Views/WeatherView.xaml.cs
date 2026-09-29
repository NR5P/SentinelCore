using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The Weather section: current conditions, active alerts, storms heading for home, the severe
/// outlook, rain and snow totals, the hourly chart and strip, the seven-day forecast, the
/// forecasters' discussion - and beside it the dashboard's zoomable radar map (Live/RadarMapHost).
/// </summary>
/// <remarks>
/// Everything comes from this server (core/SentinelCore.Web/WeatherEndpoints.cs), which talks to the
/// US National Weather Service for the location set in the server's Settings. A port of the
/// dashboard's Weather tab (wwwroot/weather.js); the arithmetic lives in SentinelCore.Client
/// (WeatherText, WeatherDay, WeatherClock) where it is tested. Fetches only while on screen: the
/// report every ten minutes and the storms near home every two, like the dashboard.
/// </remarks>
public partial class WeatherView : UserControl
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StormInterval = TimeSpan.FromMinutes(2);

    // Below this width the map moves above the report rather than beside it.
    private const double WideLayoutWidth = 980;
    private const double HourStep = 54;

    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");

    private readonly AppModel _model;
    private readonly DispatcherTimer _reportTimer;
    private readonly DispatcherTimer _stormTimer;

    private WeatherReport? _report;
    private WeatherDetails? _details;
    private WeatherStorms? _storms;
    private bool _stormsFailed;
    private WeatherClock _clock = WeatherClock.For(null);
    private bool _active;
    private bool _loading;
    private bool _expanded;

    // What is expanded, by id, so a ten-minute refresh doesn't fold up the warning somebody is in
    // the middle of reading.
    private readonly HashSet<string> _openAlerts = new();
    private readonly HashSet<string> _openDays = new();
    private bool _discussionOpen;

    // The storm parts, redrawn every two minutes without redrawing the rest.
    private StackPanel? _bannerSlot;
    private StackPanel? _stormsSlot;

    // Card grids whose column count follows the report's width.
    private readonly List<(UniformGrid Grid, double MinWidth, int MaxColumns)> _flowGrids = new();

    public WeatherView(AppModel model)
    {
        _model = model;
        InitializeComponent();

        _reportTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReportInterval };
        _reportTimer.Tick += async (_, _) => await LoadAsync();
        _stormTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = StormInterval };
        _stormTimer.Tick += async (_, _) => await LoadStormsAsync();

        IsVisibleChanged += (_, _) => SetActive(IsVisible);
        Body.SizeChanged += (_, _) => ApplyLayout();
        ReportHost.SizeChanged += (_, _) => FlowGrids();
        RadarMap.ExpandRequested += ToggleExpanded;
    }

    /// <summary>Starts (true) or stops (false) fetching. Off whenever the section isn't on screen.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        RadarMap.SetActive(active);
        if (active)
        {
            _reportTimer.Start();
            _stormTimer.Start();
            _ = LoadAsync();
        }
        else
        {
            _reportTimer.Stop();
            _stormTimer.Stop();
        }
    }

    // ---------------------------------------------------------------- Loading

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var report = await _model.Client.WeatherAsync();
            _report = report;
            _clock = WeatherClock.For(report.Location?.TimeZone);
            Render();

            if (report.IsOk)
            {
                await RadarMap.ShowAsync(_model, report.Location);
                try
                {
                    _details = await _model.Client.WeatherDetailsAsync();
                }
                catch (Exception error)
                {
                    _model.Log($"Weather details failed: {error.Message}", DiagnosticLevel.Notice);
                }
                await LoadStormsAsync(render: false);
                Render();
            }
        }
        catch (Exception error)
        {
            _model.Log($"Weather failed: {error.Message}", DiagnosticLevel.Notice);
            if (_report is null)
            {
                ReportHost.Children.Clear();
                ReportHost.Children.Add(Caption($"Couldn't load the weather - {AppModel.UserFacingMessage(error)}"));
            }
            else
            {
                UpdatedText.Text = "refresh failed · showing the last answer";
            }
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task LoadStormsAsync(bool render = true)
    {
        if (_report is not { IsOk: true, Location: { } home }) return;
        try
        {
            _storms = await _model.Client.WeatherStormsNearAsync(home.Latitude, home.Longitude);
            _stormsFailed = false;
        }
        catch (Exception error)
        {
            _stormsFailed = _storms is null;
            _model.Log($"Storm tracking failed: {error.Message}", DiagnosticLevel.Notice);
        }
        if (render) RenderStormParts();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        _ = RadarMap.RefreshAsync();
        await LoadAsync();
    }

    // ---------------------------------------------------------------- Layout

    private void ToggleExpanded()
    {
        _expanded = !_expanded;
        RadarMap.SetExpanded(_expanded);
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var showMap = _report?.IsOk == true;
        MapCard.Visibility = showMap ? Visibility.Visible : Visibility.Collapsed;
        ReportScroller.Visibility = showMap && _expanded ? Visibility.Collapsed : Visibility.Visible;

        if (!showMap)
        {
            Place(ReportScroller, 0, 2, 0, 2);
            return;
        }

        if (_expanded)
        {
            Place(MapCard, 0, 2, 0, 2);
            MapCard.Margin = new Thickness(0);
            return;
        }

        if (Body.ActualWidth >= WideLayoutWidth)
        {
            // Side by side: the report on the left, the map filling the height on the right.
            ReportColumn.Width = new GridLength(1, GridUnitType.Star);
            MapColumn.Width = new GridLength(1.15, GridUnitType.Star);
            MapRow.Height = new GridLength(0);
            Place(ReportScroller, 1, 1, 0, 1);
            Place(MapCard, 1, 1, 1, 1);
            MapCard.Margin = new Thickness(16, 0, 0, 0);
        }
        else
        {
            // Stacked: the map above, the report scrolling beneath it.
            MapColumn.Width = new GridLength(0);
            MapRow.Height = new GridLength(Math.Max(320, Body.ActualHeight * 0.5));
            Place(MapCard, 0, 1, 0, 2);
            Place(ReportScroller, 1, 1, 0, 2);
            MapCard.Margin = new Thickness(0, 0, 0, 14);
        }
    }

    private static void Place(UIElement element, int row, int rowSpan, int column, int columnSpan)
    {
        Grid.SetRow(element, row);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private void FlowGrids()
    {
        var width = ReportHost.ActualWidth;
        if (width <= 0) return;
        foreach (var (grid, minWidth, maxColumns) in _flowGrids)
        {
            grid.Columns = Math.Clamp((int)(width / minWidth), 1, maxColumns);
        }
    }

    // ---------------------------------------------------------------- Rendering

    private void Render()
    {
        var report = _report;
        if (report is null) return;

        var fetched = report.FetchedAt is { } at ? $"updated {_clock.Clock(at)}" : "";
        UpdatedText.Text = report.Stale ? $"{fetched} · showing the last answer weather.gov gave" : fetched;

        ReportHost.Children.Clear();
        _flowGrids.Clear();
        _bannerSlot = null;
        _stormsSlot = null;

        switch (report.State)
        {
            case WeatherStates.Unconfigured:
                ReportHost.Children.Add(SetupNotice(report));
                break;
            case WeatherStates.Unsupported:
                if (report.Location is { } place) ReportHost.Children.Add(PlaceHeading(place));
                ReportHost.Children.Add(Notice("NO FORECAST FOR THIS LOCATION", report.Message));
                break;
            case WeatherStates.Unreachable:
                ReportHost.Children.Add(Notice("WEATHER.GOV UNREACHABLE", report.Message,
                    Caption("The section tries again on its own every ten minutes, or press Refresh.")));
                break;
            default:
                RenderReport(report);
                break;
        }

        ApplyLayout();
        FlowGrids();
    }

    private void RenderReport(WeatherReport report)
    {
        if (report.Location is { } location) ReportHost.Children.Add(PlaceHeading(location));

        var problems = report.Problems.Concat(_details?.Problems ?? new List<string>()).ToList();
        foreach (var problem in problems)
        {
            ReportHost.Children.Add(Caption("⚠ " + problem, Palette.Amber, new Thickness(0, 0, 0, 4)));
        }

        _bannerSlot = new StackPanel();
        ReportHost.Children.Add(_bannerSlot);

        foreach (var alert in report.Alerts) ReportHost.Children.Add(AlertCard(alert));

        ReportHost.Children.Add(NowPanel(report));

        var cards = new UniformGrid { Columns = 1, Margin = new Thickness(0, 0, 0, 16) };
        _stormsSlot = new StackPanel();
        cards.Children.Add(Card(_stormsSlot));
        cards.Children.Add(OutlookCard());
        cards.Children.Add(TotalsCard());
        _flowGrids.Add((cards, 250, 3));
        ReportHost.Children.Add(cards);

        if (report.Hourly.Count > 0) ReportHost.Children.Add(HourlySection(report.Hourly));
        if (report.Daily.Count > 0) ReportHost.Children.Add(DailySection(report.Daily));
        if (_details?.Discussion is { Text.Length: > 0 } discussion) ReportHost.Children.Add(DiscussionSection(discussion));
        ReportHost.Children.Add(Footer(report));

        RenderStormParts();
    }

    private void RenderStormParts()
    {
        if (_bannerSlot is not null)
        {
            _bannerSlot.Children.Clear();
            if (WeatherText.StormBanner(_storms) is { } banner) _bannerSlot.Children.Add(StormBanner(banner));
        }
        if (_stormsSlot is not null)
        {
            _stormsSlot.Children.Clear();
            BuildStormsCard(_stormsSlot);
        }
    }

    // ---- Place, notices ----

    private UIElement PlaceHeading(WeatherLocation location)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock
        {
            Text = location.DisplayName.ToUpperInvariant(),
            Foreground = Palette.Green,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
        });
        panel.Children.Add(Caption(WeatherText.PlaceMeta(location)));
        return panel;
    }

    private Border Notice(string title, string? message, params UIElement[] extra)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, Foreground = Palette.Amber, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(Prose(message ?? ""));
        foreach (var element in extra)
        {
            if (element is FrameworkElement framework && framework.Margin == default) framework.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(element);
        }
        var card = Card(body);
        card.MaxWidth = 640;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        return card;
    }

    private Border SetupNotice(WeatherReport report)
    {
        UIElement action;
        if (_model.IsAdmin)
        {
            var button = new Button { Content = "Open location settings…", HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += async (_, _) =>
            {
                new ServerSettingsWindow(_model, "location") { Owner = this.OwnerWindow() }.ShowDialog();
                await LoadAsync();
            };
            action = button;
        }
        else
        {
            action = Caption("Ask an admin to set it under the server's Settings → LOCATION.");
        }
        return Notice("NO LOCATION SET", report.Message,
            Prose("The forecast comes from the US National Weather Service, so this works for places in the United States and its territories. The coordinates are sent to weather.gov only while the weather is being looked at."),
            action);
    }

    // ---- Storm banner ----

    private UIElement StormBanner(WeatherStormBanner banner)
    {
        var color = banner.Level == "severe" ? Palette.Red : Palette.Amber;
        var lines = new StackPanel();
        foreach (var warning in banner.Covering)
        {
            lines.Children.Add(LeadLine(WeatherText.WarningLine(warning, _clock), color));
        }
        foreach (var cell in banner.Incoming)
        {
            var line = LeadLine(WeatherText.IncomingLine(cell), color);
            MakeClickable(line, () => RadarMap.FocusCell(cell), "Show this storm on the map.");
            lines.Children.Add(line);
        }

        var row = new DockPanel();
        var icon = new TextBlock { Text = "⛈", FontFamily = EmojiFont, FontSize = 22, Foreground = color, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(lines);

        return new Border
        {
            Child = row,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = color,
            Background = Tint(color, 0x22),
        };
    }

    // "Lead · the rest": the part before the first separator in the colour, the rest plain.
    private static TextBlock LeadLine(string text, Brush color)
    {
        var split = text.IndexOf(" · ", StringComparison.Ordinal);
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.PrimaryText, Margin = new Thickness(0, 1, 0, 1) };
        if (split < 0)
        {
            block.Inlines.Add(new Run(text) { Foreground = color, FontWeight = FontWeights.SemiBold });
        }
        else
        {
            block.Inlines.Add(new Run(text[..split]) { Foreground = color, FontWeight = FontWeights.SemiBold });
            block.Inlines.Add(new Run(text[split..]));
        }
        return block;
    }

    // ---- Alerts ----

    private UIElement AlertCard(WeatherAlert alert)
    {
        var color = alert.Level switch { "severe" => Palette.Red, "moderate" => Palette.Amber, _ => Palette.Cyan };
        var open = _openAlerts.Contains(alert.Id);

        var header = new DockPanel { Background = Brushes.Transparent };
        var caret = new TextBlock { Text = open ? "▾" : "▸", Foreground = color, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(caret, Dock.Right);
        header.Children.Add(caret);
        if (alert.Ends is { } ends)
        {
            var until = Caption($"until {_clock.WeekdayClock(ends)}");
            until.VerticalAlignment = VerticalAlignment.Center;
            until.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(until, Dock.Right);
            header.Children.Add(until);
        }
        header.Children.Add(new TextBlock
        {
            Text = alert.Event.ToUpperInvariant(),
            Foreground = color,
            FontWeight = FontWeights.Bold,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
        });

        var body = new StackPanel { Visibility = open ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(Mono(alert.Description));
        if (!string.IsNullOrWhiteSpace(alert.Instruction))
        {
            var instruction = Mono(alert.Instruction);
            instruction.Foreground = Palette.PrimaryText;
            instruction.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(instruction);
        }
        var meta = string.Join(" · ", new[] { alert.AreaDescription, alert.Sender }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (meta.Length > 0) body.Children.Add(Caption(meta, null, new Thickness(0, 10, 0, 0)));

        var stack = new StackPanel();
        stack.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(alert.Headline)) stack.Children.Add(Caption(alert.Headline, Palette.PrimaryText, new Thickness(0, 4, 0, 0)));
        stack.Children.Add(body);

        var card = new Border
        {
            Child = stack,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(alert.IsSevere ? 2 : 1),
            BorderBrush = color,
            Background = Tint(color, (byte)(alert.IsSevere ? 0x2A : 0x14)),
        };
        MakeClickable(header, () =>
        {
            var nowOpen = body.Visibility != Visibility.Visible;
            body.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
            caret.Text = nowOpen ? "▾" : "▸";
            if (nowOpen) _openAlerts.Add(alert.Id);
            else _openAlerts.Remove(alert.Id);
        }, $"Click for the full text of this {alert.Severity.ToLowerInvariant()}, {alert.Urgency.ToLowerInvariant()}-urgency alert.");
        return card;
    }

    // ---- Now ----

    private UIElement NowPanel(WeatherReport report)
    {
        var panel = new StackPanel();
        panel.Children.Add(PanelTitle("NOW"));
        var current = report.Current;
        var next = WeatherText.NextPrecipitation(report.Hourly, _clock);

        if (current is null)
        {
            panel.Children.Add(Caption("No current conditions."));
            if (next is not null) panel.Children.Add(NextLine(next));
            return Section(Card(panel));
        }

        var head = new DockPanel { Margin = new Thickness(0, 2, 0, 10) };
        var glyph = new TextBlock
        {
            Text = WeatherText.Glyph(current.Icon, current.IsDaytime),
            FontFamily = EmojiFont,
            FontSize = 50,
            Foreground = GlyphBrush(current.Icon, current.IsDaytime),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
        };
        DockPanel.SetDock(glyph, Dock.Left);
        head.Children.Add(glyph);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = $"{current.TemperatureF}°F", FontSize = 38, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText });
        words.Children.Add(Prose(string.IsNullOrWhiteSpace(current.Description) ? "—" : current.Description));
        if (current.FeelsLikeF is { } feels && feels != current.TemperatureF) words.Children.Add(Caption($"feels like {feels}°"));
        var hiLo = WeatherText.HighLow(report.Daily);
        if (hiLo.Length > 0) words.Children.Add(Caption(hiLo));
        head.Children.Add(words);
        panel.Children.Add(head);

        if (next is not null) panel.Children.Add(NextLine(next));

        var sun = report.Sun;
        var moon = MoonPhase.At(DateTimeOffset.Now);
        var facts = new List<(string Label, string? Value)>
        {
            ("humidity", current.HumidityPercent is { } humidity ? $"{humidity}%" : null),
            ("dew point", current.DewpointF is { } dew ? $"{dew}°{WeatherText.DewComfort(dew)}" : null),
            ("wind", WeatherText.WindText(current)),
            ("gusts", current.WindGustMph is { } gust ? $"{gust} mph" : null),
            ("pressure", WeatherText.PressureText(current, _details?.Pressure)),
            ("visibility", current.VisibilityMiles is { } miles ? $"{miles.ToString("0.##", CultureInfo.InvariantCulture)} mi" : null),
            ("sunrise", sun?.Sunrise is { } rise ? _clock.Clock(rise) : null),
            ("sunset", sun?.Sunset is { } set ? _clock.Clock(set) : null),
            ("daylight", WeatherText.DaylightText(sun)),
            ("moon", $"{moon.Name}, {moon.IlluminationPercent}% lit"),
        };
        var grid = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, value) in facts)
        {
            if (value is null) continue;
            var tile = new StackPanel { Width = 150, Margin = new Thickness(0, 0, 8, 10) };
            tile.Children.Add(Caption(label.ToUpperInvariant(), null, null, 10.5));
            tile.Children.Add(Prose(value));
            grid.Children.Add(tile);
        }
        panel.Children.Add(grid);

        var observed = current.ObservedAt is { } time ? $" · observed {_clock.WeekdayClock(time)}" : "";
        var source = Caption($"{current.Station} · {current.StationName}{observed}");
        source.ToolTip = "Current conditions come from the nearest National Weather Service observation station, which is usually an airport a few miles away - so it can differ a degree or two from the porch.";
        panel.Children.Add(source);

        return Section(Card(panel));
    }

    private static Border NextLine(string text) => new()
    {
        BorderBrush = Palette.Cyan,
        BorderThickness = new Thickness(2, 0, 0, 0),
        Background = Tint(Palette.Cyan, 0x12),
        Padding = new Thickness(10, 6, 10, 6),
        Margin = new Thickness(0, 0, 0, 12),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.PrimaryText },
    };

    // ---- Storms near home ----

    private void BuildStormsCard(StackPanel slot)
    {
        var title = PanelTitle("STORMS NEAR HOME");
        title.ToolTip = "Storm cells the NEXRAD radars are tracking right now, nearest and most dangerous first. A time means the storm's current track passes within 10 miles of home. Click one to find it on the map.";
        slot.Children.Add(title);

        if (_storms is null)
        {
            slot.Children.Add(Caption(_stormsFailed ? "Storm tracking is unavailable right now." : "loading…"));
            return;
        }
        var cells = WeatherText.StormsNearHome(_storms);
        if (cells.Count == 0)
        {
            slot.Children.Add(Caption("No storms tracked within 100 miles."));
            return;
        }
        foreach (var cell in cells)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = ThreatBrush(cell.Threat), Margin = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            var text = new StackPanel();
            var name = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.PrimaryText };
            name.Inlines.Add(new Run(WeatherText.CellTitle(cell)));
            if (cell.TowardHome && cell.EtaMinutes is { } eta)
            {
                name.Inlines.Add(new Run($"  ~{eta} min") { Foreground = Palette.Amber, FontWeight = FontWeights.SemiBold });
            }
            text.Children.Add(name);
            text.Children.Add(Caption(WeatherText.CellMeta(cell)));
            row.Children.Add(text);
            MakeClickable(row, () => RadarMap.FocusCell(cell), "Show this storm on the map.");
            slot.Children.Add(row);
        }
    }

    // ---- Severe outlook ----

    private Border OutlookCard()
    {
        var panel = new StackPanel();
        var title = PanelTitle("SEVERE OUTLOOK");
        title.ToolTip = "The NOAA Storm Prediction Center's convective outlook for home: the chance of severe thunderstorms today and the next two days, on its 1-to-5 scale, and today's odds of each hazard within 25 miles.";
        panel.Children.Add(title);

        var severe = _details?.Severe;
        if (severe is null)
        {
            panel.Children.Add(Caption(_details is null ? "loading…" : "The Storm Prediction Center's outlook is unavailable right now."));
            return Card(panel);
        }

        foreach (var day in severe.Days)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var chip = OutlookChip(day.Category);
            DockPanel.SetDock(chip, Dock.Left);
            row.Children.Add(chip);
            var name = new TextBlock { Text = WeatherText.OutlookDayName(day.Day, DateTimeOffset.Now, _clock), Width = 80, Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            var scale = Caption(WeatherText.OutlookScale(day.Category));
            scale.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(scale);
            panel.Children.Add(row);
        }

        var hazards = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        hazards.Children.Add(Hazard("tornado", severe.TornadoPercent, severe.SignificantTornado));
        hazards.Children.Add(Hazard("hail", severe.HailPercent, severe.SignificantHail));
        hazards.Children.Add(Hazard("wind", severe.WindPercent, severe.SignificantWind));
        panel.Children.Add(hazards);

        if (severe.IssuedAt is { } issued) panel.Children.Add(Caption($"SPC day 1 issued {_clock.WeekdayClock(issued)}", null, new Thickness(0, 6, 0, 0)));
        return Card(panel);
    }

    private static Border OutlookChip(string? category)
    {
        // The SPC's own colours for its categories.
        (string? background, string? foreground) = category switch
        {
            "TSTM" => ("#C1E9C1", "#000000"),
            "MRGL" => ("#66A366", "#000000"),
            "SLGT" => ("#FFE066", "#000000"),
            "ENH" => ("#FFA366", "#000000"),
            "MDT" => ("#E06666", "#FFFFFF"),
            "HIGH" => ("#EE99EE", "#000000"),
            _ => (null, null),
        };
        return new Border
        {
            Width = 48,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(4),
            Background = background is null ? Brushes.Transparent : BrushFrom(background),
            BorderBrush = background is null ? Palette.SecondaryText : Brushes.Transparent,
            BorderThickness = new Thickness(background is null ? 1 : 0),
            Child = new TextBlock
            {
                Text = category ?? "NONE",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = foreground is null ? Palette.SecondaryText : BrushFrom(foreground),
            },
        };
    }

    private static UIElement Hazard(string label, int? percent, bool significant)
    {
        var block = new TextBlock { Margin = new Thickness(0, 0, 16, 4), Foreground = Palette.SecondaryText };
        block.Inlines.Add(new Run(label + " "));
        block.Inlines.Add(new Run($"{percent ?? 0}%") { Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold });
        if (significant)
        {
            block.Inlines.Add(new Run(" sig") { Foreground = Palette.Red, FontWeight = FontWeights.Bold });
            block.ToolTip = "Hatched area: the SPC expects the significant kind here (EF2+ tornadoes, 2-inch hail or 75 mph gusts).";
        }
        return block;
    }

    // ---- Rain and snow ahead ----

    private Border TotalsCard()
    {
        var panel = new StackPanel();
        var title = PanelTitle("RAIN & SNOW AHEAD");
        title.ToolTip = "From the National Weather Service's forecast grid for this spot: expected liquid rain, snowfall and ice, and the highest chance of thunder.";
        panel.Children.Add(title);

        var totals = _details?.Totals;
        if (totals is null)
        {
            panel.Children.Add(Caption(_details is null ? "loading…" : "Precipitation totals are unavailable right now."));
            return Card(panel);
        }

        var rows = new List<(string, string)>
        {
            ("rain, next 24 h", WeatherText.Inches(totals.RainNext24HoursInches)),
            ("rain, next 7 days", WeatherText.Inches(totals.RainNext7DaysInches)),
        };
        if (totals.SnowNext7DaysInches > 0)
        {
            rows.Add(("snow, next 24 h", WeatherText.Inches(totals.SnowNext24HoursInches, 1)));
            rows.Add(("snow, next 7 days", WeatherText.Inches(totals.SnowNext7DaysInches, 1)));
        }
        if (totals.IceNext7DaysInches > 0) rows.Add(("ice, next 7 days", WeatherText.Inches(totals.IceNext7DaysInches)));
        rows.Add(("thunder, next 24 h", $"{totals.ThunderChanceNext24Hours}%"));
        rows.Add(("thunder, next 7 days", $"{totals.ThunderChanceNext7Days}%"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Caption(rows[i].Item1, null, new Thickness(0, 0, 14, 4));
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            var value = Prose(rows[i].Item2);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        panel.Children.Add(grid);
        return Card(panel);
    }

    // ---- Hourly ----

    private UIElement HourlySection(IReadOnlyList<WeatherHour> hours)
    {
        var section = new StackPanel();

        var title = new TextBlock { Foreground = Palette.Cyan, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(new Run($"NEXT {hours.Count} HOURS   "));
        title.Inlines.Add(new Run("━ temperature  ") { Foreground = Palette.Amber, FontWeight = FontWeights.Normal, FontSize = 11 });
        title.Inlines.Add(new Run("┅ feels like  ") { Foreground = FeelsBrush, FontWeight = FontWeights.Normal, FontSize = 11 });
        title.Inlines.Add(new Run("▮ chance of rain") { Foreground = Palette.Cyan, FontWeight = FontWeights.Normal, FontSize = 11 });
        section.Children.Add(title);

        var extras = WeatherText.HourDetailsByTime(_details);
        var content = new StackPanel();
        if (HourlyChart(hours, extras) is { } chart) content.Children.Add(chart);

        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        string? lastDay = null;
        foreach (var hour in hours)
        {
            var day = _clock.DateKey(hour.StartTime);
            var newDay = day != lastDay;
            lastDay = day;
            extras.TryGetValue(hour.StartTime.UtcTicks, out var extra);

            var column = new StackPanel { Width = HourStep };
            column.Children.Add(new TextBlock { Text = newDay ? _clock.Weekday(hour.StartTime).ToUpperInvariant() : " ", Foreground = Palette.Cyan, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center });
            column.Children.Add(new TextBlock { Text = _clock.Hour(hour.StartTime), Foreground = Palette.SecondaryText, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            column.Children.Add(new TextBlock
            {
                Text = WeatherText.Glyph(hour.Icon, hour.IsDaytime),
                FontFamily = EmojiFont,
                FontSize = 20,
                Foreground = GlyphBrush(hour.Icon, hour.IsDaytime),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            });
            column.Children.Add(new TextBlock { Text = hour.TemperatureF is { } temp ? $"{temp}°" : "—", Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            column.Children.Add(new TextBlock
            {
                Text = hour.PrecipitationChance is { } chance and > 0 ? $"{chance}%" : " ",
                Foreground = Palette.Cyan,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            column.Children.Add(new TextBlock { Text = WeatherText.HourWind(hour), Foreground = Palette.SecondaryText, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center });

            strip.Children.Add(new Border
            {
                Child = column,
                Background = Brushes.Transparent,
                BorderBrush = (Brush)FindResource("CardBorder"),
                BorderThickness = new Thickness(newDay && strip.Children.Count > 0 ? 1 : 0, 0, 0, 0),
                Padding = new Thickness(0, 2, 0, 6),
                ToolTip = WeatherText.HourTip(hour, extra),
            });
        }
        content.Children.Add(strip);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
            Padding = new Thickness(0, 0, 0, 4),
        };
        // A horizontal strip inside the vertical report: the wheel scrolls the report (Shift+wheel
        // scrolls the hours), rather than the strip swallowing it.
        scroller.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
            else ReportScroller.ScrollToVerticalOffset(ReportScroller.VerticalOffset - e.Delta);
        };

        section.Children.Add(Card(scroller));
        return Section(section);
    }

    private static readonly Brush FeelsBrush = BrushFrom("#FF8A65");

    private UIElement? HourlyChart(IReadOnlyList<WeatherHour> hours, Dictionary<long, WeatherHourDetail> extras)
    {
        var temps = hours.Select(hour => hour.TemperatureF).OfType<int>().ToList();
        if (temps.Count < 2) return null;
        var feels = hours.Select(hour => extras.TryGetValue(hour.StartTime.UtcTicks, out var extra) ? extra.FeelsLikeF : null).ToList();

        const double height = 132, top = 22, tempBottom = 88, barBottom = 128;
        var width = hours.Count * HourStep;
        var all = temps.Concat(feels.OfType<int>()).ToList();
        double min = all.Min(), max = all.Max();
        var span = Math.Max(max - min, 1);
        double Y(double t) => tempBottom - (t - min) / span * (tempBottom - top);
        double X(int i) => i * HourStep + HourStep / 2;

        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };

        canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = barBottom, Y2 = barBottom, Stroke = (Brush)FindResource("CardBorder"), StrokeThickness = 1 });

        var barBrush = Tint(Palette.Cyan, 0x5A);
        for (var i = 0; i < hours.Count; i++)
        {
            var chance = hours[i].PrecipitationChance ?? 0;
            if (chance <= 0) continue;
            var barHeight = chance / 100.0 * 34;
            var bar = new Rectangle { Width = HourStep * 0.7, Height = barHeight, Fill = barBrush, ToolTip = $"{chance}% chance of precipitation" };
            Canvas.SetLeft(bar, X(i) - HourStep * 0.35);
            Canvas.SetTop(bar, barBottom - barHeight);
            canvas.Children.Add(bar);
            if (chance >= 20) canvas.Children.Add(Centered($"{chance}", X(i), barBottom - barHeight - 14, Palette.Cyan, 9.5));
        }

        string? lastDay = null;
        for (var i = 0; i < hours.Count; i++)
        {
            var day = _clock.DateKey(hours[i].StartTime);
            if (day != lastDay && i > 0)
            {
                canvas.Children.Add(new Line { X1 = i * HourStep, X2 = i * HourStep, Y1 = 4, Y2 = barBottom, Stroke = (Brush)FindResource("CardBorder"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
                var name = new TextBlock { Text = _clock.Weekday(hours[i].StartTime).ToUpperInvariant(), Foreground = Palette.Cyan, FontSize = 10 };
                Canvas.SetLeft(name, i * HourStep + 4);
                Canvas.SetTop(name, 0);
                canvas.Children.Add(name);
            }
            lastDay = day;
        }

        if (feels.Select((f, i) => f is not null && f != hours[i].TemperatureF).Any(differs => differs))
        {
            canvas.Children.Add(new Polyline
            {
                Points = Points(feels.Select(f => (double?)f).ToList(), X, Y),
                Stroke = FeelsBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                ToolTip = "feels like",
            });
        }
        canvas.Children.Add(new Polyline
        {
            Points = Points(hours.Select(hour => (double?)hour.TemperatureF).ToList(), X, Y),
            Stroke = Palette.Amber,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
        });

        for (var i = 0; i < hours.Count; i += 3)
        {
            if (hours[i].TemperatureF is not { } temp) continue;
            canvas.Children.Add(Centered($"{temp}°", X(i), Y(temp) - 19, Palette.Amber, 11));
        }
        return canvas;
    }

    private static PointCollection Points(List<double?> values, Func<int, double> x, Func<double, double> y)
    {
        var points = new PointCollection();
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is { } value) points.Add(new Point(x(i), y(value)));
        }
        return points;
    }

    private static TextBlock Centered(string text, double x, double y, Brush color, double size)
    {
        var block = new TextBlock { Text = text, Foreground = color, FontSize = size, Width = 40, TextAlignment = TextAlignment.Center };
        Canvas.SetLeft(block, x - 20);
        Canvas.SetTop(block, y);
        return block;
    }

    // ---- Seven days ----

    private UIElement DailySection(IReadOnlyList<WeatherPeriod> periods)
    {
        var days = WeatherDay.Group(periods, _clock);
        var range = WeatherDay.WeekRange(periods);

        var list = new StackPanel();
        list.Children.Add(PanelTitle($"{days.Count}-DAY FORECAST"));
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var lead = day.Lead;

            var row = new Grid { Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            var name = new TextBlock { Text = lead.Name, Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(name);

            var glyph = new TextBlock { Text = WeatherText.Glyph(lead.Icon, lead.IsDaytime), FontFamily = EmojiFont, FontSize = 20, Foreground = GlyphBrush(lead.Icon, lead.IsDaytime), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(glyph, 1);
            row.Children.Add(glyph);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            words.Children.Add(new TextBlock { Text = lead.ShortForecast, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap });
            if (day.Meta.Length > 0) words.Children.Add(Caption(day.Meta));
            Grid.SetColumn(words, 2);
            row.Children.Add(words);

            var low = new TextBlock { Text = day.Low is { } l ? $"{l}°" : "", Foreground = Palette.Cyan, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(low, 3);
            row.Children.Add(low);

            if (range is { } week && day.RangeWithin(week.Min, week.Max) is { } bar)
            {
                var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { Background = (Brush)FindResource("InputBackground"), CornerRadius = new CornerRadius(3) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bar.Left, GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bar.Width, GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - bar.Left - bar.Width), GridUnitType.Star) });
                Grid.SetColumnSpan(track.Children[0], 3);
                var fill = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Background = new LinearGradientBrush(((SolidColorBrush)Palette.Cyan).Color, ((SolidColorBrush)Palette.Amber).Color, 0),
                };
                Grid.SetColumn(fill, 1);
                track.Children.Add(fill);
                Grid.SetColumn(track, 4);
                row.Children.Add(track);
            }

            var high = new TextBlock { Text = day.High is { } h ? $"{h}°" : "", Foreground = Palette.Amber, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(high, 5);
            row.Children.Add(high);

            var detail = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = _openDays.Contains(day.Key) ? Visibility.Visible : Visibility.Collapsed };
            foreach (var period in day.Periods)
            {
                var paragraph = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.PrimaryText, Margin = new Thickness(0, 0, 0, 6) };
                paragraph.Inlines.Add(new Run(period.Name + ". ") { FontWeight = FontWeights.SemiBold });
                paragraph.Inlines.Add(new Run(period.DetailedForecast));
                detail.Children.Add(paragraph);
            }

            var item = new StackPanel();
            item.Children.Add(row);
            item.Children.Add(detail);
            var border = new Border
            {
                Child = item,
                Padding = new Thickness(0, 8, 0, 8),
                BorderBrush = (Brush)FindResource("CardBorder"),
                BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0),
                Background = Brushes.Transparent,
            };
            var key = day.Key;
            MakeClickable(border, () =>
            {
                var open = detail.Visibility != Visibility.Visible;
                detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                if (open) _openDays.Add(key);
                else _openDays.Remove(key);
            }, "Click for the full forecast for this day and night.");
            list.Children.Add(border);
        }
        return Section(Card(list));
    }

    // ---- Discussion and footer ----

    private UIElement DiscussionSection(WeatherDiscussion discussion)
    {
        var issued = discussion.IssuedAt is { } at ? $" · issued {_clock.WeekdayClock(at)}" : "";
        var header = new TextBlock { Foreground = Palette.Cyan, FontWeight = FontWeights.SemiBold, Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap };
        var caret = new Run(_discussionOpen ? "▾ " : "▸ ");
        header.Inlines.Add(caret);
        header.Inlines.Add(new Run($"FORECASTER DISCUSSION · {discussion.Office}{issued}"));

        var text = Mono(discussion.Text.Trim());
        text.Margin = new Thickness(0, 10, 0, 0);
        text.Visibility = _discussionOpen ? Visibility.Visible : Visibility.Collapsed;

        MakeClickable(header, () =>
        {
            _discussionOpen = !_discussionOpen;
            text.Visibility = _discussionOpen ? Visibility.Visible : Visibility.Collapsed;
            caret.Text = _discussionOpen ? "▾ " : "▸ ";
        }, "The forecasters' own notes on why the forecast says what it does - how sure they are, what could change, and what they're watching. Written by the local National Weather Service office a few times a day.");

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(text);
        return Section(Card(panel));
    }

    private UIElement Footer(WeatherReport report)
    {
        var issued = report.ForecastUpdatedAt is { } at ? $", forecast issued {_clock.WeekdayClock(at)}" : "";
        var zone = report.Location?.TimeZone is { Length: > 0 } timeZone ? $" Times are shown in {timeZone.Replace('_', ' ')}." : "";
        var radar = RadarMap.IsInteractive
            ? " Storm tracks and warning outlines via the Iowa Environmental Mesonet; basemap © Esri, HERE, Garmin and OpenStreetMap contributors."
            : "";
        return Caption(
            $"Forecast, observations, radar and discussion from the US National Weather Service (weather.gov){issued}. Severe outlook from the NOAA Storm Prediction Center.{radar}{zone}",
            null, new Thickness(0, 4, 0, 8), 10.5);
    }

    // ---------------------------------------------------------------- Small parts

    private Border Card(UIElement child) => new()
    {
        Style = (Style)FindResource("Card"),
        Child = child,
        Margin = new Thickness(0, 0, 8, 8),
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static UIElement Section(UIElement element)
    {
        if (element is FrameworkElement framework) framework.Margin = new Thickness(0, 0, 0, 16);
        return element;
    }

    private static TextBlock PanelTitle(string text) => new()
    {
        Text = text,
        Foreground = Palette.Cyan,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12,
        Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock Caption(string text, Brush? color = null, Thickness? margin = null, double? size = null) => new()
    {
        Text = text,
        Style = (Style)FindResource("Caption"),
        Foreground = color ?? Palette.SecondaryText,
        Margin = margin ?? new Thickness(0),
        FontSize = size ?? (double)FindResource("CaptionFontSize"),
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Prose(string text) => new()
    {
        Text = text,
        Foreground = Palette.PrimaryText,
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Mono(string text) => new()
    {
        Text = text,
        FontFamily = MonoFont,
        FontSize = 12,
        Foreground = Palette.SecondaryText,
        TextWrapping = TextWrapping.Wrap,
    };

    private static void MakeClickable(FrameworkElement element, Action action, string? tip)
    {
        element.Cursor = Cursors.Hand;
        if (tip is not null) element.ToolTip = tip;
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            action();
        };
    }

    private static Brush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush Tint(Brush color, byte alpha)
    {
        var c = ((SolidColorBrush)color).Color;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static Brush ThreatBrush(int threat) => threat switch
    {
        1 => BrushFrom("#FFD54F"),
        2 => BrushFrom("#FF9800"),
        3 => BrushFrom("#FF4D5E"),
        4 => BrushFrom("#FF00E5"),
        _ => BrushFrom("#CFD8DC"),
    };

    // Monochrome glyphs (WPF draws emoji in one colour), tinted by what they show.
    private static Brush GlyphBrush(string? code, bool? isDaytime)
    {
        if (code is null) return Palette.PrimaryText;
        if (code.StartsWith("tsra", StringComparison.Ordinal) || code is "tornado" or "hurricane" or "tropical_storm") return Palette.Amber;
        if (code.Contains("rain", StringComparison.Ordinal) || code.Contains("snow", StringComparison.Ordinal)
            || code.Contains("sleet", StringComparison.Ordinal) || code.Contains("fzra", StringComparison.Ordinal) || code is "blizzard" or "cold") return Palette.Cyan;
        if (isDaytime != false && code is "skc" or "few" or "hot") return Palette.Amber;
        return Palette.PrimaryText;
    }
}
