using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>A date, and a time unless it is all day. Null when no date is picked.</summary>
internal sealed class DateTimeInput : StackPanel
{
    private readonly DatePicker _date = new() { Width = 150 };
    private readonly TextBox _time = new() { Width = 80, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Time, for example 14:30 or 2:30 PM" };
    private bool _allDay;

    public DateTimeInput(DateTimeOffset? value, bool allDay)
    {
        Orientation = Orientation.Horizontal;
        Children.Add(_date);
        Children.Add(_time);
        Value = value;
        AllDay = allDay;
        _date.SelectedDateChanged += (_, _) => Changed?.Invoke();
        // A typed time counts once the field is left, not at each keystroke on the way to "2:30".
        _time.LostFocus += (_, _) => Changed?.Invoke();
    }

    /// <summary>The date was picked or the time typed and left.</summary>
    public event Action? Changed;

    public bool AllDay
    {
        get => _allDay;
        set
        {
            _allDay = value;
            _time.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public DateTimeOffset? Value
    {
        get
        {
            if (_date.SelectedDate is not { } date) return null;
            var time = TimeSpan.Zero;
            if (!_allDay && DateTime.TryParse(_time.Text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed))
            {
                time = parsed.TimeOfDay;
            }
            return OrganizerDates.At(date.Date + time, Ui.Zone);
        }
        set
        {
            if (value is not { } moment)
            {
                _date.SelectedDate = null;
                _time.Text = "";
                return;
            }
            var local = OrganizerDates.InZone(moment, Ui.Zone);
            _date.SelectedDate = local.Date;
            _time.Text = local.ToString("t", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Whether what was typed as a time is one. An all-day value has no time to get wrong;
    /// a timed one with the time left empty is wrong, not quietly midnight.</summary>
    public bool TimeIsValid => _allDay || _date.SelectedDate is null
                               || DateTime.TryParse(_time.Text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out _);

    public DatePicker DatePicker => _date;
}

/// <summary>An item's <c>links</c>: web pages, documents, anything with an address.</summary>
internal sealed class LinksEditor : StackPanel
{
    private readonly List<Dictionary<string, object?>> _links;
    private readonly StackPanel _rows = new();
    private readonly TextBox _url = new() { MinWidth = 240 };
    private readonly TextBox _title = new() { MinWidth = 140, Margin = new Thickness(8, 0, 0, 0) };

    public LinksEditor(IReadOnlyList<object?> links)
    {
        _links = links.OfType<IReadOnlyDictionary<string, object?>>()
            .Select(link => new Dictionary<string, object?>(link)).ToList();
        Children.Add(_rows);

        var add = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var button = Ui.Button("Add link", Add);
        button.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(button, Dock.Right);
        add.Children.Add(button);
        var fields = new StackPanel { Orientation = Orientation.Horizontal };
        fields.Children.Add(Labeled("Address", _url));
        fields.Children.Add(Labeled("Title (optional)", _title));
        add.Children.Add(fields);
        Children.Add(add);
        _url.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        Rebuild();
    }

    private static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel();
        var caption = Ui.Caption(label);
        caption.Margin = new Thickness(control.Margin.Left, 0, 0, 2);
        panel.Children.Add(caption);
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>The links as the field stores them - and anything the add row still holds, since
    /// someone who typed an address and pressed Save meant to keep it.</summary>
    public List<object?> Value
    {
        get
        {
            Add();
            return _links.Cast<object?>().ToList();
        }
    }

    private void Add()
    {
        var url = _url.Text.Trim();
        if (url.Length == 0) return;
        if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        var link = new Dictionary<string, object?> { ["url"] = url };
        if (_title.Text.Trim() is { Length: > 0 } title) link["title"] = title;
        _links.Add(link);
        _url.Text = "";
        _title.Text = "";
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        foreach (var link in _links.ToList())
        {
            var url = link.GetValueOrDefault("url") as string ?? "";
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var remove = Ui.Icon("", "Remove this link", () =>
            {
                _links.Remove(link);
                Rebuild();
            });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new TextBlock
            {
                Text = link.GetValueOrDefault("title") is string { Length: > 0 } title ? $"{title} — {url}" : url,
                Foreground = Palette.Cyan,
                Cursor = Cursors.Hand,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = url,
            };
            text.MouseLeftButtonUp += (_, _) => Ui.OpenUrl(url);
            row.Children.Add(text);
            _rows.Children.Add(row);
        }
    }
}

/// <summary>
/// Files and pictures on an item. For an item that exists they are attached straight away - the
/// file is kept on this PC and goes up whenever the server is reachable. For one being created they
/// wait until it's saved.
/// </summary>
internal sealed class AttachmentsEditor : StackPanel
{
    private readonly OrganizerEngine _engine;
    private readonly WrapPanel _tiles = new();
    private readonly List<(byte[] Bytes, string Name, string Type, int? Width, int? Height)> _waiting = new();
    private string? _parentId;

    public AttachmentsEditor(OrganizerEngine engine, string? parentId)
    {
        _engine = engine;
        _parentId = parentId;
        Children.Add(_tiles);
        var add = Ui.Button("Add pictures or files…", Pick);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(add);
        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        };
        Rebuild();
    }

    /// <summary>The item now exists; anything picked while it was being created is attached to it.</summary>
    public void Attach(string parentId)
    {
        _parentId = parentId;
        foreach (var file in _waiting)
        {
            try
            {
                _engine.Attach(file.Bytes, file.Name, file.Type, parentId, file.Width, file.Height);
            }
            catch (Exception error)
            {
                Ui.ShowError(this, error);
            }
        }
        _waiting.Clear();
    }

    private string? _shown;

    /// <summary>Redraws the tiles if what they show has changed. Called on every engine change,
    /// which while a note is being typed into is every autosave - redrawing then reloaded every
    /// picture and made them flicker.</summary>
    public void Refresh()
    {
        var signature = Signature();
        if (signature == _shown) return;
        Rebuild();
    }

    private string Signature() =>
        string.Join("|", (_parentId is null ? new List<OrganizerItem>() : _engine.Children(_parentId, OrganizerKinds.Attachment))
            .Select(item => $"{item.Id}:{item.String("name")}:{_engine.HasOriginal(item.String("hash") ?? "")}:{_engine.KeptOffline.Contains(item.Id)}"))
        + "#" + _waiting.Count;

    public void Rebuild()
    {
        _shown = Signature();
        _tiles.Children.Clear();
        if (_parentId is not null)
        {
            foreach (var attachment in _engine.Children(_parentId, OrganizerKinds.Attachment).OrderBy(item => item.SortPosition))
            {
                _tiles.Children.Add(Tile(attachment));
            }
        }
        foreach (var file in _waiting)
        {
            _tiles.Children.Add(Frame(Ui.Text($"{file.Name}\n{Ui.Size(file.Bytes.Length)} · added when saved", "Caption"), null));
        }
    }

    private void Pick()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Attach pictures or files" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AddFiles(dialog.FileNames);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var name = Path.GetFileName(path);
                var type = Ui.ContentType(name);
                var (width, height) = type.StartsWith("image/", StringComparison.Ordinal) ? Dimensions(bytes) : (null, null);
                if (_parentId is { } parentId) _engine.Attach(bytes, name, type, parentId, width, height);
                else _waiting.Add((bytes, name, type, width, height));
            }
            catch (Exception error)
            {
                Ui.ShowError(this, error);
            }
        }
        Rebuild();
    }

    private static (int?, int?) Dimensions(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private Border Frame(UIElement content, ContextMenu? menu) => new()
    {
        Width = 150,
        MinHeight = 60,
        Margin = new Thickness(0, 0, 8, 8),
        Padding = new Thickness(6),
        CornerRadius = new CornerRadius(6),
        Background = Ui.Brush("InputBackground"),
        Child = content,
        ContextMenu = menu,
    };

    private Border Tile(OrganizerItem attachment)
    {
        var hash = attachment.String("hash") ?? "";
        var name = attachment.String("name") ?? "attachment";
        var isImage = attachment.String("contentType")?.StartsWith("image/", StringComparison.Ordinal) == true;
        var panel = new StackPanel();
        var image = new Image { Height = 96, Stretch = Stretch.Uniform, Visibility = isImage ? Visibility.Visible : Visibility.Collapsed };
        panel.Children.Add(image);
        if (!isImage) panel.Children.Add(Ui.Glyph("", Palette.Cyan, 26));
        panel.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Palette.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = name,
        });
        var detail = attachment.Number("size") is { } size ? Ui.Size(size) : "";
        if (!_engine.HasOriginal(hash)) detail += (detail.Length > 0 ? " · " : "") + "on the server";
        panel.Children.Add(Ui.Caption(detail));

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open", () => _ = OpenAsync(hash, name)));
        menu.Items.Add(MenuItem("Save a copy…", () => _ = SaveAsync(hash, name)));
        menu.Items.Add(MenuItem(_engine.KeptOffline.Contains(attachment.Id) ? "Stop keeping on this PC" : "Always keep on this PC",
            () => _engine.SetKeptOffline(attachment.Id, !_engine.KeptOffline.Contains(attachment.Id))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Remove", () =>
        {
            if (!Ui.Confirm(this, $"Remove {name}?")) return;
            _engine.Delete(attachment.Id);
            Rebuild();
        }));

        var tile = Frame(panel, menu);
        tile.Cursor = Cursors.Hand;
        tile.ToolTip = "Double-click to open · right-click for more";
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) _ = OpenAsync(hash, name);
        };
        if (isImage) _ = LoadAsync(image, hash);
        return tile;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task LoadAsync(Image target, string hash)
    {
        var bytes = await _engine.ImageBytesAsync(hash, thumbnail: true);
        if (bytes is null) return;
        target.Source = Bitmap(bytes, 300);
    }

    public static BitmapImage? Bitmap(byte[] bytes, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the file in whatever Windows opens that kind of file with. The engine's own copy is
    /// named by its hash and has no extension, so a copy with the real name goes in a temporary
    /// folder first.
    /// </summary>
    private async Task OpenAsync(string hash, string name)
    {
        try
        {
            var original = await _engine.EnsureOriginalAsync(hash);
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SentinelCore", "Organizer", hash[..12])).FullName;
            var copy = Path.Combine(folder, SafeName(name));
            if (!File.Exists(copy)) File.Copy(original, copy);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(copy) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Ui.ShowError(this, new Exception($"Couldn't open {name}: {error.Message}"));
        }
    }

    private async Task SaveAsync(string hash, string name)
    {
        var dialog = new SaveFileDialog { FileName = SafeName(name), Title = "Save a copy" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.Copy(await _engine.EnsureOriginalAsync(hash), dialog.FileName, overwrite: true);
        }
        catch (Exception error)
        {
            Ui.ShowError(this, new Exception($"Couldn't save {name}: {error.Message}"));
        }
    }

    private static string SafeName(string name)
    {
        var cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return cleaned.Length == 0 ? "attachment" : cleaned;
    }
}
