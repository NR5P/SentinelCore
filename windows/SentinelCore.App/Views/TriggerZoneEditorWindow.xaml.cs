using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using Point = System.Windows.Point;

namespace SentinelCore.App.Views;

/// <summary>
/// Where motion counts on one camera, how big it has to be, and what happens when it does.
/// </summary>
/// <remarks>
/// Polygons are stored as fractions of the frame (0-1 on each axis), not pixels, so a zone drawn
/// here means the same thing on the server whatever resolution the camera is running at and
/// whatever size this window happens to be.
///
/// Two rules from core/docs/MULTI_ZONE_CLIENT_HANDOFF.md are load-bearing on save, and both are
/// handled in <see cref="BuildConfigForSave"/>: the legacy zonePolygon/minTriggerArea pair must
/// keep being sent, and an empty zone list means something different from no zone list at all.
/// </remarks>
public partial class TriggerZoneEditorWindow : Window
{
    private static readonly Brush[] ZoneColours =
    {
        new SolidColorBrush(Color.FromRgb(0x38, 0xFF, 0x9C)),
        new SolidColorBrush(Color.FromRgb(0x4A, 0xE8, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x45)),
        new SolidColorBrush(Color.FromRgb(0xC9, 0x8B, 0xFF)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0x8B, 0xB5)),
    };

    private readonly AppModel _model;
    private readonly Camera _camera;

    private TriggerConfig _config = new();
    private List<TriggerZone> _zones = new();
    private int _selectedZone;
    private int _draggingPoint = -1;

    // Where the frame actually sits inside the canvas. Stretch="Uniform" letterboxes the image, so
    // a click has to be mapped against the drawn rectangle rather than the whole control - without
    // this every zone drifts by the size of the letterbox bars.
    private Rect _frameRect = Rect.Empty;

    public TriggerZoneEditorWindow(AppModel model, Camera camera)
    {
        _model = model;
        _camera = camera;
        InitializeComponent();

        Title = $"Trigger zones - {camera.Name}";
        SizeChanged += (_, _) => Redraw();
        ZoneCanvas.SizeChanged += (_, _) => Redraw();

        Loaded += async (_, _) =>
        {
            await LoadProfilesAsync();
            await LoadConfigAsync();
            await LoadFrameAsync();
        };
    }

    // ------------------------------------------------------------------ Loading

    private async Task LoadConfigAsync()
    {
        try
        {
            _config = await _model.Client.TriggerConfigAsync(_camera.Id);
            // ResolvedZones always yields at least one zone: a config written before multi-zone
            // reads as a single zone built from the legacy pair, so nothing here special-cases it.
            _zones = _config.ResolvedZones
                .Select(zone => new TriggerZone
                {
                    Name = zone.Name,
                    MinTriggerArea = zone.MinTriggerArea,
                    Polygon = zone.Polygon.Select(point => new Point2D(point.X, point.Y)).ToList(),
                })
                .ToList();

            EnabledToggle.IsChecked = _config.Enabled;
            CooldownBox.Text = _config.CooldownSeconds.ToString(CultureInfo.InvariantCulture);
            MaxPerMinuteBox.Text = _config.MaxTriggersPerMinute.ToString(CultureInfo.InvariantCulture);
            ShadowSlider.Value = _config.ShadowSensitivity;
            RecordClipsToggle.IsChecked = _config.RecordClips;
            PreSecondsBox.Text = _config.ClipPreSeconds.ToString(CultureInfo.InvariantCulture);
            PostSecondsBox.Text = _config.ClipPostSeconds.ToString(CultureInfo.InvariantCulture);
            SendToAlertsToggle.IsChecked = _config.SendToAlerts;
            ProfileBox.SelectedValue = _config.AlertProfile;

            NightToggle.IsChecked = _config.ResolvedNightEnabled;
            NightStartBox.Text = _config.ResolvedNightStartHour.ToString(CultureInfo.InvariantCulture);
            NightEndBox.Text = _config.ResolvedNightEndHour.ToString(CultureInfo.InvariantCulture);

            _selectedZone = 0;
            RefreshZoneList();
            Message("");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _model.Client.PromptProfileNamesAsync();
            ProfileBox.ItemsSource = profiles;
        }
        catch (Exception)
        {
            // Not fatal: the profile stays whatever the server already has for this camera, and the
            // rest of the screen works.
            ProfileBox.IsEnabled = false;
        }
    }

    private async Task LoadFrameAsync()
    {
        FrameNotice.Text = "Grabbing a frame from the camera…";
        RefreshFrameButton.IsEnabled = false;
        try
        {
            // Live ffmpeg snapshot, never cached - fetched fresh so the zones are drawn over what
            // the camera is looking at now, not over last week's furniture.
            var bytes = await _model.Client.ReferenceFrameAsync(_camera.Id);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            FrameImage.Source = bitmap;
            FrameNotice.Text = "";
            Redraw();
        }
        catch (Exception error)
        {
            // Zones can still be drawn against a black rectangle - awkward, but better than a dead
            // screen when a camera is briefly unreachable.
            FrameNotice.Text = "Couldn't grab a frame from this camera. You can still edit the zones, " +
                               "but you'll be drawing blind.\n\n" + AppModel.UserFacingMessage(error);
        }
        finally
        {
            RefreshFrameButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ Zone list

    public sealed record ZoneRow(string Name, string Detail, Brush Colour);

    private void RefreshZoneList()
    {
        ZoneList.SelectionChanged -= OnZoneSelected;
        ZoneList.ItemsSource = _zones
            .Select((zone, index) => new ZoneRow(
                zone.Name,
                $"{zone.Polygon.Count} point{(zone.Polygon.Count == 1 ? "" : "s")} · " +
                $"min {zone.MinTriggerArea * 100:0.0}% of frame",
                ZoneColours[index % ZoneColours.Length]))
            .ToList();
        if (_selectedZone >= 0 && _selectedZone < _zones.Count) ZoneList.SelectedIndex = _selectedZone;
        ZoneList.SelectionChanged += OnZoneSelected;

        // The last zone can't be deleted: a camera with no zones at all is a different setting
        // (triggering off) and has its own switch.
        DeleteZoneButton.IsEnabled = _zones.Count > 1;

        if (_selectedZone >= 0 && _selectedZone < _zones.Count)
        {
            MinAreaSlider.ValueChanged -= OnMinAreaChanged;
            MinAreaSlider.Value = Math.Clamp(_zones[_selectedZone].MinTriggerArea, 0, 0.2);
            MinAreaSlider.ValueChanged += OnMinAreaChanged;
            UpdateMinAreaText();
        }

        UpdateShadowText();
        Redraw();
    }

    private void OnZoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ZoneList.SelectedIndex < 0) return;
        _selectedZone = ZoneList.SelectedIndex;
        RefreshZoneList();
    }

    private void OnAddZoneClicked(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Add zone", "Name for the new zone:", initialValue: $"Zone {_zones.Count + 1}")
        {
            Owner = this,
        };
        if (prompt.ShowDialog() != true) return;

        _zones.Add(new TriggerZone
        {
            Name = prompt.Value,
            Polygon = new List<Point2D>(),
            // A new zone starts at the same sensitivity as the one being looked at, which is a far
            // better guess than zero (which would trigger on a moth).
            MinTriggerArea = _selectedZone < _zones.Count ? _zones[_selectedZone].MinTriggerArea : 0.02,
        });
        _selectedZone = _zones.Count - 1;
        RefreshZoneList();
        Message("Click on the frame to draw the new zone.");
    }

    private void OnRenameZoneClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedZone < 0 || _selectedZone >= _zones.Count) return;
        var prompt = new TextPromptWindow("Rename zone", "New name:", initialValue: _zones[_selectedZone].Name)
        {
            Owner = this,
        };
        if (prompt.ShowDialog() != true) return;
        _zones[_selectedZone].Name = prompt.Value;
        RefreshZoneList();
    }

    private void OnDeleteZoneClicked(object sender, RoutedEventArgs e)
    {
        if (_zones.Count <= 1 || _selectedZone < 0 || _selectedZone >= _zones.Count) return;
        _zones.RemoveAt(_selectedZone);
        _selectedZone = Math.Max(0, _selectedZone - 1);
        RefreshZoneList();
    }

    private void OnMinAreaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedZone < 0 || _selectedZone >= _zones.Count) return;
        _zones[_selectedZone].MinTriggerArea = MinAreaSlider.Value;
        UpdateMinAreaText();
    }

    private void UpdateMinAreaText() =>
        MinAreaText.Text = MinAreaSlider.Value <= 0
            ? "Any movement at all counts"
            : $"{MinAreaSlider.Value * 100:0.0}% of the frame";

    private void OnShadowChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateShadowText();

    private void UpdateShadowText() =>
        ShadowText.Text = ShadowSlider.Value switch
        {
            <= 20 => $"{(int)ShadowSlider.Value} - almost nothing is dismissed as a shadow",
            >= 80 => $"{(int)ShadowSlider.Value} - dismisses a lot as shadow; real movement can be missed",
            _ => $"{(int)ShadowSlider.Value}",
        };

    // ------------------------------------------------------------------ Drawing

    private void Redraw()
    {
        ZoneCanvas.Children.Clear();
        _frameRect = FrameRectangle();
        if (_frameRect.IsEmpty) return;

        for (var index = 0; index < _zones.Count; index++)
        {
            var zone = _zones[index];
            var colour = ZoneColours[index % ZoneColours.Length];
            var isSelected = index == _selectedZone;
            if (zone.Polygon.Count == 0) continue;

            var polygon = new Polygon
            {
                Stroke = colour,
                StrokeThickness = isSelected ? 2.5 : 1.5,
                Fill = new SolidColorBrush(((SolidColorBrush)colour).Color) { Opacity = isSelected ? 0.22 : 0.10 },
                Opacity = isSelected ? 1 : 0.75,
                IsHitTestVisible = false,
                Points = new PointCollection(zone.Polygon.Select(ToCanvas)),
            };
            ZoneCanvas.Children.Add(polygon);

            if (!isSelected) continue;

            // Handles only on the zone being edited: a frame full of draggable dots from five zones
            // is unusable, and a click meant for one zone lands on another's handle.
            for (var pointIndex = 0; pointIndex < zone.Polygon.Count; pointIndex++)
            {
                var position = ToCanvas(zone.Polygon[pointIndex]);
                var handle = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = colour,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Tag = pointIndex,
                    Cursor = Cursors.SizeAll,
                };
                Canvas.SetLeft(handle, position.X - 6);
                Canvas.SetTop(handle, position.Y - 6);
                ZoneCanvas.Children.Add(handle);
            }
        }
    }

    /// <summary>
    /// Where the letterboxed frame actually sits inside the canvas. Everything on screen is
    /// measured against this, never against the canvas itself.
    /// </summary>
    private Rect FrameRectangle()
    {
        var width = ZoneCanvas.ActualWidth;
        var height = ZoneCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return Rect.Empty;

        if (FrameImage.Source is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            // No frame yet: the whole canvas stands in for it, so zones drawn blind still map to
            // sensible fractions.
            return new Rect(0, 0, width, height);
        }

        var scale = Math.Min(width / source.PixelWidth, height / source.PixelHeight);
        var drawnWidth = source.PixelWidth * scale;
        var drawnHeight = source.PixelHeight * scale;
        return new Rect((width - drawnWidth) / 2, (height - drawnHeight) / 2, drawnWidth, drawnHeight);
    }

    private Point ToCanvas(Point2D point) => new(
        _frameRect.X + point.X * _frameRect.Width,
        _frameRect.Y + point.Y * _frameRect.Height);

    private Point2D ToFraction(Point point) => new(
        Math.Clamp((point.X - _frameRect.X) / _frameRect.Width, 0, 1),
        Math.Clamp((point.Y - _frameRect.Y) / _frameRect.Height, 0, 1));

    // ------------------------------------------------------------------ Mouse

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedZone < 0 || _selectedZone >= _zones.Count) return;
        if (_frameRect.IsEmpty) return;

        var position = e.GetPosition(ZoneCanvas);

        // Grabbing an existing handle takes priority over adding a point, or a polygon could never
        // be adjusted once its points were dense.
        var hit = HitTestHandle(position);
        if (hit >= 0)
        {
            _draggingPoint = hit;
            ZoneCanvas.CaptureMouse();
            return;
        }

        _zones[_selectedZone].Polygon.Add(ToFraction(position));
        RefreshZoneList();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPoint < 0 || _selectedZone < 0 || _selectedZone >= _zones.Count) return;
        var zone = _zones[_selectedZone];
        if (_draggingPoint >= zone.Polygon.Count) return;

        zone.Polygon[_draggingPoint] = ToFraction(e.GetPosition(ZoneCanvas));
        Redraw();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingPoint < 0) return;
        _draggingPoint = -1;
        ZoneCanvas.ReleaseMouseCapture();
        RefreshZoneList();
    }

    private void OnCanvasRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedZone < 0 || _selectedZone >= _zones.Count) return;
        var hit = HitTestHandle(e.GetPosition(ZoneCanvas));
        if (hit < 0) return;
        _zones[_selectedZone].Polygon.RemoveAt(hit);
        RefreshZoneList();
    }

    private int HitTestHandle(Point position)
    {
        if (_selectedZone < 0 || _selectedZone >= _zones.Count) return -1;
        var polygon = _zones[_selectedZone].Polygon;
        for (var index = 0; index < polygon.Count; index++)
        {
            var handle = ToCanvas(polygon[index]);
            if ((handle - position).Length <= 10) return index;
        }
        return -1;
    }

    private void OnClearZoneClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedZone < 0 || _selectedZone >= _zones.Count) return;
        _zones[_selectedZone].Polygon.Clear();
        RefreshZoneList();
    }

    private void OnFillZoneClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedZone < 0 || _selectedZone >= _zones.Count) return;
        _zones[_selectedZone].Polygon = new List<Point2D>
        {
            new(0, 0), new(1, 0), new(1, 1), new(0, 1),
        };
        RefreshZoneList();
    }

    private void OnRefreshFrameClicked(object sender, RoutedEventArgs e) => _ = LoadFrameAsync();

    // ------------------------------------------------------------------ Saving

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        // A polygon of fewer than three points encloses nothing, and the server would store a zone
        // that can never trigger. Saying so beats saving something that silently does nothing.
        var degenerate = _zones.FirstOrDefault(zone => zone.Polygon.Count is > 0 and < 3);
        if (degenerate is not null)
        {
            Message($"\"{degenerate.Name}\" has fewer than three points, so it doesn't enclose anything. " +
                    "Add points, or clear it.");
            return;
        }
        if (_zones.All(zone => zone.Polygon.Count == 0) && EnabledToggle.IsChecked == true)
        {
            Message("Triggering is on but no zone has a shape, so nothing would ever trigger. " +
                    "Draw a zone, or use \"Cover everything\".");
            return;
        }

        if (!TryReadInt(CooldownBox, "cooldown", out var cooldown) ||
            !TryReadInt(MaxPerMinuteBox, "most triggers per minute", out var maxPerMinute) ||
            !TryReadInt(PreSecondsBox, "seconds before", out var preSeconds) ||
            !TryReadInt(PostSecondsBox, "seconds after", out var postSeconds) ||
            !TryReadHour(NightStartBox, "start hour", out var nightStart) ||
            !TryReadHour(NightEndBox, "end hour", out var nightEnd))
        {
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var config = BuildConfigForSave(cooldown, maxPerMinute, preSeconds, postSeconds, nightStart, nightEnd);
            _config = await _model.Client.SaveTriggerConfigAsync(_camera.Id, config);
            Message("Saved.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private TriggerConfig BuildConfigForSave(
        int cooldown, int maxPerMinute, int preSeconds, int postSeconds, int nightStart, int nightEnd)
    {
        var zones = _zones.Where(zone => zone.Polygon.Count >= 3).ToList();
        var primary = zones.FirstOrDefault();

        return new TriggerConfig
        {
            Enabled = EnabledToggle.IsChecked == true,

            // The legacy pair mirrors zones[0] and must keep being sent whatever else changes: a
            // client that stopped sending them would break every installed build that still reads
            // them.
            ZonePolygon = primary?.Polygon ?? new List<Point2D>(),
            MinTriggerArea = primary?.MinTriggerArea ?? 0,

            // This editor knows about zones, so it always sends the list - including an empty one,
            // which means "the editor really did delete them". Null would mean "this client doesn't
            // know about zones", and would leave the stored ones untouched.
            Zones = zones,

            CooldownSeconds = cooldown,
            MaxTriggersPerMinute = maxPerMinute,
            ShadowSensitivity = (int)ShadowSlider.Value,
            RecordClips = RecordClipsToggle.IsChecked == true,
            ClipPreSeconds = preSeconds,
            ClipPostSeconds = postSeconds,
            SendToAlerts = SendToAlertsToggle.IsChecked == true,
            AlertProfile = ProfileBox.SelectedValue as string ?? _config.AlertProfile,

            // Not shown anywhere on this screen, and carried through untouched precisely so editing
            // a polygon here doesn't unbind a Device Hub sensor set up on the web dashboard.
            SensorDeviceId = _config.SensorDeviceId,
            SensorTriggerOn = _config.SensorTriggerOn,
            SensorCooldownSeconds = _config.SensorCooldownSeconds,

            // All three sent together, always. This screen knows about the night window, so it must
            // state it rather than omitting it - omitting means "I don't know about this feature"
            // and merges against the stored row.
            NightWindowEnabled = NightToggle.IsChecked == true,
            NightStartHour = nightStart,
            NightEndHour = nightEnd,
        };
    }

    private bool TryReadInt(TextBox box, string label, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0)
        {
            return true;
        }
        Message($"The {label} has to be a whole number of zero or more.");
        return false;
    }

    private bool TryReadHour(TextBox box, string label, out int value)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value is >= 0 and <= 23)
        {
            return true;
        }
        Message($"The {label} has to be a whole hour between 0 and 23.");
        return false;
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
