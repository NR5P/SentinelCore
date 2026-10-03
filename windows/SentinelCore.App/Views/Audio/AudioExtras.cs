using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SentinelCore.App.Audio;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// Settings → Music → Libraries (§1-§2): the music folders, with Add (folder picker), Rename,
/// Change folder, Remove, Scan, and the scan's progress. Admin only.
/// </summary>
public sealed class MusicLibrariesWindow : AudioDialog
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _scan = new() { Foreground = Palette.Cyan, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _grace;

    public MusicLibrariesWindow(AppModel model)
        : base(model, "Music libraries", "The folders the server scans for music: on the server itself, or a shared folder on another computer on the network.", 760, 560)
    {
        Body.Children.Add(_scan);
        Body.Children.Add(_list);
        AddFooterButton("Add library…", () => _ = AddAsync(), primary: true);
        AddFooterButton("Scan all", () => _ = ScanAsync(null));
        AddFooterButton("Close", Close, isCancel: true);
        _poll.Tick += async (_, _) => await LoadAsync();
        Closed += (_, _) => _poll.Stop();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var status = await Model.Client.MusicStatusAsync();
            if (!status.Enabled)
            {
                _list.Children.Clear();
                _list.Children.Add(AudioUi.Caption("Music is turned off. Turn it on under Server Settings → Music."));
                _poll.Stop();
                return;
            }
            _scan.Visibility = status.Scanning ? Visibility.Visible : Visibility.Collapsed;
            if (status.Scanning)
            {
                var scan = status.Scan;
                var what = scan?.Phase switch
                {
                    "files" => "looking for music files",
                    "tags" => "reading tags",
                    "artwork" => "finding album art",
                    "lyrics" => "finding lyrics",
                    _ => "working",
                };
                _scan.Text = $"Scanning{(scan?.LibraryName is { Length: > 0 } n ? $" {n}" : "")}: {what}{(scan is { Total: > 0 } ? $" · {scan.Processed:N0} of {scan.Total:N0}" : "")}";
                _grace = 0;
                if (!_poll.IsEnabled) _poll.Start();
            }
            else if (_grace > 0)
            {
                _grace--;
            }
            else
            {
                _poll.Stop();
            }
            if (!status.FfmpegAvailable) Message("ffmpeg isn't installed on the server, so files a device can't play natively won't be converted.");

            _list.Children.Clear();
            if (status.Libraries.Count == 0) _list.Children.Add(AudioUi.Caption("No music libraries yet. Add the folder your music is in."));
            foreach (var library in status.Libraries.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)) _list.Children.Add(Row(library, status.Scanning));
            if (status.Libraries.Count > 0)
            {
                _list.Children.Add(AudioUi.Caption(
                    $"{AudioUi.Count(status.ArtistCount, "artist", "artists")} · {AudioUi.Count(status.AlbumCount, "album", "albums")} · {AudioUi.Count(status.TrackCount, "song", "songs")} · {ByteSizeConverter.FormatBytes(status.TotalBytes)} · {AudioTime.Minutes(status.TotalDurationSeconds / 60)}", 10));
            }
        }
        catch (Exception error)
        {
            _poll.Stop();
            Fail(error);
        }
    }

    private FrameworkElement Row(MusicLibrary library, bool scanning)
    {
        var card = new Border { Style = AudioUi.StyleOf("Card"), Margin = new Thickness(0, 0, 0, 10) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = library.Name + (library.Network ? "  (network)" : ""), Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = library.Path, Foreground = Palette.SecondaryText, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        var scanLine = !string.IsNullOrWhiteSpace(library.LastScanError)
            ? $"Last scan failed: {library.LastScanError}"
            : library.LastScanFinishedAt is { } finished ? $"Scanned {RelativeTime.Describe(finished)}" : "Not scanned yet";
        text.Children.Add(new TextBlock
        {
            Text = $"{AudioUi.Count(library.TrackCount, "song", "songs")} · {scanLine}",
            Foreground = string.IsNullOrWhiteSpace(library.LastScanError) ? Palette.SecondaryText : Palette.Red,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        });
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var scan = AudioUi.TextButton("Scan", () => _ = ScanAsync(library));
        scan.IsEnabled = !scanning;
        buttons.Children.Add(scan);
        buttons.Children.Add(AudioUi.TextButton("Rename…", () => _ = RenameAsync(library)));
        buttons.Children.Add(AudioUi.TextButton("Folder…", () => _ = ChangeFolderAsync(library), "Point this library at a different folder (it is rescanned)"));
        var remove = AudioUi.TextButton("Remove", () => _ = RemoveAsync(library));
        remove.Style = AudioUi.StyleOf("DangerButton");
        buttons.Children.Add(remove);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        card.Child = grid;
        return card;
    }

    private string? PickFolder(string? start)
    {
        var picker = new MediaFolderPickerWindow(Model, start, SentinelClient.AudioBase) { Owner = this };
        return picker.ShowDialog() == true ? picker.ChosenPath : null;
    }

    private async Task AddAsync()
    {
        var path = PickFolder(null);
        if (path is null) return;
        var suggested = path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? "Music";
        var name = AudioUi.Prompt("Name the library", "Name", suggested.Length == 0 ? "Music" : suggested);
        if (name is null) return;
        try
        {
            await Model.Client.CreateMusicLibraryAsync(new MusicLibraryInput { Name = name, Path = path });
            Message($"Added {name}. The server is scanning it now.");
            _grace = 2;
            _poll.Start();
            await LoadAsync();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task RenameAsync(MusicLibrary library)
    {
        var name = AudioUi.Prompt("Rename library", "Name", library.Name);
        if (name is null || name == library.Name) return;
        await SaveAsync(library, name, library.Path);
    }

    private async Task ChangeFolderAsync(MusicLibrary library)
    {
        var path = PickFolder(library.Path);
        if (path is null || path == library.Path) return;
        await SaveAsync(library, library.Name, path);
        _grace = 2;
        _poll.Start();
    }

    private async Task SaveAsync(MusicLibrary library, string name, string path)
    {
        try
        {
            await Model.Client.UpdateMusicLibraryAsync(library.Id, new MusicLibraryInput { Name = name, Path = path });
            Message($"Saved {name}.");
            await LoadAsync();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task ScanAsync(MusicLibrary? library)
    {
        try
        {
            if (library is null) await Model.Client.ScanAllMusicAsync();
            else await Model.Client.ScanMusicLibraryAsync(library.Id);
            Message(library is null ? "Scanning every library…" : $"Scanning {library.Name}…");
            _grace = 2;
            _poll.Start();
            await LoadAsync();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task RemoveAsync(MusicLibrary library)
    {
        if (!AudioUi.Confirm("Remove library", $"Remove the library \"{library.Name}\"?\n\nIts songs leave Sentinel Core. Play history, playlists and loves are kept in case the folder comes back. The files are not touched.")) return;
        try
        {
            await Model.Client.DeleteMusicLibraryAsync(library.Id);
            Message($"Removed {library.Name}.");
            await LoadAsync();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}

/// <summary>Connects a ListenBrainz account (§9) so every counted play is scrobbled.</summary>
public sealed class ListenBrainzWindow : AudioDialog
{
    private readonly TextBlock _state = new() { Foreground = Palette.PrimaryText, Margin = new Thickness(0, 0, 0, 10) };
    private readonly PasswordBox _token = new();

    public ListenBrainzWindow(AppModel model)
        : base(model, "ListenBrainz", "Send everything you play to your ListenBrainz profile. Paste your user token from listenbrainz.org → Settings.", 520, 340)
    {
        Body.Children.Add(_state);
        Body.Children.Add(Label("User token"));
        Body.Children.Add(_token);
        AddFooterButton("Disconnect", () => _ = SaveAsync(""));
        AddFooterButton("Close", Close, isCancel: true);
        AddFooterButton("Connect", () => _ = SaveAsync(_token.Password), primary: true);
        Loaded += async (_, _) =>
        {
            try
            {
                Show(await Model.Client.ListenBrainzAsync());
            }
            catch (Exception error)
            {
                Fail(error);
            }
        };
    }

    private void Show(ListenBrainzStatus status) =>
        _state.Text = status.Connected ? $"Connected as {status.Username}." : "Not connected.";

    private async Task SaveAsync(string token)
    {
        try
        {
            var status = await Model.Client.SetListenBrainzTokenAsync(token);
            Show(status);
            _token.Clear();
            Message(status.Connected ? "Connected. Plays from now on are sent to ListenBrainz." : "Disconnected.");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}

/// <summary>
/// Play on… (§8): the household's speakers, with what each is playing. Picking one moves playback
/// there; "This PC" brings it back.
/// </summary>
public sealed class SpeakerPickerWindow : AudioDialog
{
    private readonly StackPanel _list = new();

    public SpeakerPickerWindow(AppModel model)
        : base(model, "Play on…", "Speakers are devices that said \"Let others play here\": the kiosk, the Apple TV, an app left open.", 520, 480)
    {
        Body.Children.Add(_list);
        AddFooterButton("Close", Close, isCancel: true);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var player = PlayerController.Shared!;
        _list.Children.Clear();
        _list.Children.Add(Choice("This PC", player.DeviceName, player.RemoteTarget is null, true, () => _ = ChooseAsync(null)));
        try
        {
            var speakers = await Model.Client.SpeakersAsync();
            foreach (var speaker in speakers.Where(s => s.DeviceId != player.DeviceId).OrderByDescending(s => s.Online).ThenBy(s => s.Name))
            {
                var state = speaker.State is { Title: { Length: > 0 } title }
                    ? $"{(speaker.State.Playing ? "Playing" : "Paused")}: {title}{(string.IsNullOrEmpty(speaker.State.Subtitle) ? "" : $" · {speaker.State.Subtitle}")}"
                    : speaker.Online ? "Idle" : $"Offline{(speaker.LastSeenAt is { } seen ? $" · seen {RelativeTime.Describe(seen)}" : "")}";
                var pick = speaker;
                _list.Children.Add(Choice(speaker.Name, $"{KindLabel(speaker.Kind)} · {state}", player.RemoteTarget?.DeviceId == speaker.DeviceId, speaker.Online, () => _ = ChooseAsync(pick)));
            }
            if (speakers.All(s => s.DeviceId == player.DeviceId))
            {
                _list.Children.Add(AudioUi.Caption("No other speakers yet. On the kiosk, the Apple TV or another app, switch on \"Let others play here\".", 8));
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private static string KindLabel(string? kind) => kind switch
    {
        "tv" => "TV",
        "kiosk" => "Kiosk",
        "phone" => "Phone",
        "tablet" => "Tablet",
        "mac" => "Mac",
        "windows" => "Windows PC",
        "web" => "Browser",
        _ => "Speaker",
    };

    private Button Choice(string title, string subtitle, bool selected, bool enabled, Action choose)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = (selected ? "✓  " : "") + title, Foreground = selected ? Palette.Green : Palette.PrimaryText, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = subtitle, Foreground = Palette.SecondaryText, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), IsEnabled = enabled };
        button.Click += (_, _) => choose();
        return button;
    }

    private async Task ChooseAsync(Speaker? speaker)
    {
        await PlayerController.Shared!.SetTargetAsync(speaker);
        Done();
    }
}

/// <summary>Wake-up alarms (§9): music or a podcast on a speaker at a set time, fading up.</summary>
public sealed class WakeAlarmsWindow : AudioDialog
{
    private readonly StackPanel _list = new();

    public WakeAlarmsWindow(AppModel model)
        : base(model, "Wake-up alarms", "Start music, a podcast or the Morning Briefing on a speaker at a set time. Times are the server's time zone.", 640, 520)
    {
        Body.Children.Add(_list);
        AddFooterButton("New alarm…", () => _ = EditAsync(null), primary: true);
        AddFooterButton("Close", Close, isCancel: true);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var alarms = await Model.Client.WakeAlarmsAsync();
            var speakers = await Model.Client.SpeakersAsync();
            _list.Children.Clear();
            if (alarms.Count == 0) _list.Children.Add(AudioUi.Caption("No alarms yet."));
            foreach (var alarm in alarms.OrderBy(a => a.Time)) _list.Children.Add(Row(alarm, speakers));
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private FrameworkElement Row(WakeAlarm alarm, List<Speaker> speakers)
    {
        var card = new Border { Style = AudioUi.StyleOf("Card"), Margin = new Thickness(0, 0, 0, 10) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"{alarm.Time}  {alarm.Name}", FontSize = 16, Foreground = alarm.Enabled ? Palette.PrimaryText : Palette.SecondaryText, FontWeight = FontWeights.SemiBold });
        var speaker = speakers.FirstOrDefault(s => s.DeviceId == alarm.SpeakerId)?.Name ?? "a speaker that isn't registered now";
        text.Children.Add(new TextBlock
        {
            Text = $"{WakeAlarmText.Days(alarm.Days)} · {WakeAlarmText.Source(alarm.Source)} on {speaker}" +
                   (alarm.Enabled && alarm.NextFireAt is { } next ? $" · next {next.ToLocalTime():ddd d MMM HH:mm}" : alarm.Enabled ? "" : " · off"),
            Foreground = Palette.SecondaryText,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        });
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new CheckBox { IsChecked = alarm.Enabled, ToolTip = "On or off", Margin = new Thickness(0, 0, 10, 0) };
        toggle.Click += async (_, _) =>
        {
            alarm.Enabled = toggle.IsChecked == true;
            try
            {
                await Model.Client.SaveWakeAlarmAsync(alarm);
                await LoadAsync();
            }
            catch (Exception error)
            {
                Fail(error);
            }
        };
        buttons.Children.Add(toggle);
        buttons.Children.Add(AudioUi.TextButton("Test", () => _ = TestAsync(alarm), "Play it on the speaker now"));
        buttons.Children.Add(AudioUi.TextButton("Edit…", () => _ = EditAsync(alarm)));
        var delete = AudioUi.TextButton("Delete", () => _ = DeleteAsync(alarm));
        delete.Style = AudioUi.StyleOf("DangerButton");
        buttons.Children.Add(delete);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        card.Child = grid;
        return card;
    }

    private async Task EditAsync(WakeAlarm? alarm)
    {
        var editor = new WakeAlarmEditorWindow(Model, alarm) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync();
    }

    private async Task TestAsync(WakeAlarm alarm)
    {
        try
        {
            await Model.Client.TestWakeAlarmAsync(alarm.Id!);
            Message($"Sent {alarm.Name} to the speaker.");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task DeleteAsync(WakeAlarm alarm)
    {
        if (!AudioUi.Confirm("Delete alarm", $"Delete the {alarm.Time} alarm \"{alarm.Name}\"?")) return;
        try
        {
            await Model.Client.DeleteWakeAlarmAsync(alarm.Id!);
            await LoadAsync();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}

internal static class WakeAlarmText
{
    public static readonly string[] DayKeys = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
    public static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    public static string Days(IReadOnlyCollection<string> days)
    {
        if (days.Count == 0) return "Once";
        if (days.Count == 7) return "Every day";
        var set = days.ToHashSet();
        if (set.SetEquals(DayKeys.Take(5))) return "Weekdays";
        if (set.SetEquals(DayKeys.Skip(5))) return "Weekends";
        return string.Join(" ", DayKeys.Select((key, i) => set.Contains(key) ? DayNames[i] : null).Where(name => name is not null));
    }

    public static string Source(WakeAlarmSource source) => source.Type switch
    {
        "briefing" => "Morning Briefing",
        "shuffle" => "Shuffle all",
        "podcast" => $"Newest episode of {source.Name ?? "a show"}",
        _ => source.Name ?? source.Type,
    };
}

/// <summary>One wake-up alarm's time, days, speaker, what plays, volume and fade.</summary>
public sealed class WakeAlarmEditorWindow : AudioDialog
{
    private static readonly (string Type, string Label)[] SourceTypes =
    {
        ("shuffle", "Shuffle all"), ("briefing", "Morning Briefing"), ("playlist", "A playlist"), ("album", "An album"),
        ("artist", "An artist"), ("mix", "A mix"), ("podcast", "A podcast's newest episode"),
    };

    private readonly WakeAlarm _alarm;
    private readonly TextBox _name = new();
    private readonly TextBox _time = new() { Width = 90 };
    private readonly List<CheckBox> _days = new();
    private readonly ComboBox _speaker = new() { Width = 260 };
    private readonly ComboBox _sourceType = new() { Width = 220, ItemsSource = SourceTypes.Select(s => s.Label).ToList() };
    private readonly ComboBox _sourceItem = new() { Width = 300, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Width = 240, TickFrequency = 0.05 };
    private readonly ComboBox _ramp = new() { Width = 160, ItemsSource = new[] { "No fade", "30 seconds", "1 minute", "2 minutes", "5 minutes", "10 minutes" } };
    private static readonly double[] RampValues = { 0, 30, 60, 120, 300, 600 };
    private List<(string Id, string Name)> _items = new();

    public WakeAlarmEditorWindow(AppModel model, WakeAlarm? existing)
        : base(model, existing is null ? "New alarm" : $"Edit {existing.Name}", "No days ticked means once, at the next time it comes round.", 620, 620)
    {
        _alarm = existing is null
            ? new WakeAlarm { Name = "Wake up", Time = "07:00", Days = WakeAlarmText.DayKeys.Take(5).ToList() }
            : new WakeAlarm
            {
                Id = existing.Id, Name = existing.Name, Time = existing.Time, Days = existing.Days.ToList(), SpeakerId = existing.SpeakerId,
                Source = new WakeAlarmSource { Type = existing.Source.Type, Id = existing.Source.Id, Name = existing.Source.Name },
                Volume = existing.Volume, RampSeconds = existing.RampSeconds, Enabled = existing.Enabled,
            };

        _name.Text = _alarm.Name;
        _time.Text = _alarm.Time;
        Body.Children.Add(Label("Name"));
        Body.Children.Add(_name);
        Body.Children.Add(Label("Time (24-hour, server time)"));
        Body.Children.Add(_time);
        Body.Children.Add(Label("Days"));
        var days = new WrapPanel();
        for (var i = 0; i < 7; i++)
        {
            var box = new CheckBox { Content = WakeAlarmText.DayNames[i], IsChecked = _alarm.Days.Contains(WakeAlarmText.DayKeys[i]), Margin = new Thickness(0, 0, 14, 0) };
            _days.Add(box);
            days.Children.Add(box);
        }
        Body.Children.Add(days);
        Body.Children.Add(Label("Speaker"));
        Body.Children.Add(_speaker);
        Body.Children.Add(Label("Play"));
        Body.Children.Add(_sourceType);
        Body.Children.Add(_sourceItem);
        Body.Children.Add(Label("Volume"));
        _volume.Value = _alarm.Volume;
        Body.Children.Add(_volume);
        Body.Children.Add(Label("Fade up over"));
        _ramp.SelectedIndex = Array.FindIndex(RampValues, value => value >= _alarm.RampSeconds) is var index and >= 0 ? index : 3;
        Body.Children.Add(_ramp);

        _sourceType.SelectedIndex = Math.Max(0, Array.FindIndex(SourceTypes, s => s.Type == _alarm.Source.Type));
        _sourceType.SelectionChanged += async (_, _) => await LoadItemsAsync();

        AddFooterButton("Cancel", Close, isCancel: true);
        AddFooterButton("Save", () => _ = SaveAsync(), primary: true);
        Loaded += async (_, _) =>
        {
            try
            {
                var speakers = await Model.Client.SpeakersAsync();
                var options = speakers.Select(s => new SmartOption(s.DeviceId, s.Online ? s.Name : $"{s.Name} (offline)")).ToList();
                if (!string.IsNullOrEmpty(_alarm.SpeakerId) && options.All(o => o.Value != _alarm.SpeakerId)) options.Add(new SmartOption(_alarm.SpeakerId, "The speaker this alarm used"));
                _speaker.ItemsSource = options;
                _speaker.SelectedItem = options.FirstOrDefault(o => o.Value == _alarm.SpeakerId) ?? options.FirstOrDefault();
                if (options.Count == 0) Message("No speakers yet: switch on \"Let others play here\" on the device the alarm should play on.", error: true);
            }
            catch (Exception error)
            {
                Fail(error);
            }
            await LoadItemsAsync();
        };
    }

    private string SourceType => SourceTypes[Math.Max(0, _sourceType.SelectedIndex)].Type;

    private async Task LoadItemsAsync()
    {
        _items = new();
        _sourceItem.Visibility = SourceType is "shuffle" or "briefing" ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            switch (SourceType)
            {
                case "playlist":
                    _items = (await Model.Client.MusicPlaylistsAsync()).Select(p => (p.Id, p.Name)).ToList();
                    break;
                case "album":
                    _items = (await Model.Client.MusicAlbumsAsync(sort: "plays", order: "desc", limit: 500)).Items.Select(a => (a.Id, $"{a.Title} · {a.Artist}")).ToList();
                    break;
                case "artist":
                    _items = (await Model.Client.MusicArtistsAsync(sort: "plays", order: "desc", limit: 500)).Items.Select(a => (a.Id, a.Name)).ToList();
                    break;
                case "mix":
                    _items = (await Model.Client.MusicHomeAsync()).Mixes.Select(m => (m.Id, m.Name)).ToList();
                    break;
                case "podcast":
                    _items = (await Model.Client.PodcastsAsync()).Where(p => p.Id is not null).Select(p => (p.Id!, p.Title)).ToList();
                    break;
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
        _sourceItem.ItemsSource = _items.Select(i => i.Name).ToList();
        var current = _items.FindIndex(i => i.Id == _alarm.Source.Id);
        _sourceItem.SelectedIndex = current >= 0 ? current : _items.Count > 0 ? 0 : -1;
    }

    private async Task SaveAsync()
    {
        if (!TimeOnly.TryParseExact(_time.Text.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            Message("Type the time as hours and minutes, like 06:45.", error: true);
            return;
        }
        if (_speaker.SelectedItem is not SmartOption speaker)
        {
            Message("Pick the speaker it plays on.", error: true);
            return;
        }
        var type = SourceType;
        string? id = null, name = null;
        if (type is not ("shuffle" or "briefing"))
        {
            if (_sourceItem.SelectedIndex < 0)
            {
                Message("Pick what it plays.", error: true);
                return;
            }
            (id, name) = _items[_sourceItem.SelectedIndex];
        }
        _alarm.Name = _name.Text.Trim().Length == 0 ? "Alarm" : _name.Text.Trim();
        _alarm.Time = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        _alarm.Days = WakeAlarmText.DayKeys.Where((_, i) => _days[i].IsChecked == true).ToList();
        _alarm.SpeakerId = speaker.Value;
        _alarm.Source = new WakeAlarmSource { Type = type, Id = id, Name = name ?? SourceTypes.First(s => s.Type == type).Label };
        _alarm.Volume = Math.Round(_volume.Value, 2);
        _alarm.RampSeconds = RampValues[Math.Max(0, _ramp.SelectedIndex)];
        _alarm.Enabled = true;
        try
        {
            await Model.Client.SaveWakeAlarmAsync(_alarm);
            Done();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}

/// <summary>
/// Listening stats and Wrapped (§9): minutes, top artists, albums, songs, genres and shows, and
/// when you listen - for a week, a month, a year or all time.
/// </summary>
public sealed class AudioStatsView : AudioPage
{
    private string _period = "month";
    private int _year = DateTime.Now.Year;

    public AudioStatsView(AppModel model, INavigator navigator) : base(model, navigator)
    {
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            var stats = await Model.Client.ListeningStatsAsync(_period, _year);
            Render(stats);
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }

    private void Render(ListeningStats stats)
    {
        Host.Children.Clear();
        var bar = AudioUi.Tabs(new[] { ("week", "Week"), ("month", "Month"), ("year", "Wrapped"), ("all", "All time") }, _period, period =>
        {
            _period = period;
            _ = LoadAsync();
        });
        if (_period == "year")
        {
            bar.Children.Add(AudioUi.TextButton("◀", () =>
            {
                _year--;
                _ = LoadAsync();
            }, margin: new Thickness(16, 0, 4, 0)));
            bar.Children.Add(new TextBlock { Text = _year.ToString(), Foreground = Palette.PrimaryText, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
            var next = AudioUi.TextButton("▶", () =>
            {
                _year++;
                _ = LoadAsync();
            });
            next.IsEnabled = _year < DateTime.Now.Year;
            bar.Children.Add(next);
        }
        Host.Children.Add(bar);

        if (_period == "year") Host.Children.Add(AudioUi.Title($"Your {_year}, Wrapped", 28));

        if (stats.Music is { } music)
        {
            Host.Children.Add(AudioUi.Heading("Music", 18));
            Host.Children.Add(Tiles(
                (AudioTime.Minutes(music.Minutes), "listened"),
                ($"{music.Plays:N0}", "plays"),
                ($"{music.DistinctArtists:N0}", $"artists · {music.NewArtists} new"),
                ($"{music.LongestStreakDays}", "day streak")));
            if (music.TopArtists.Count > 0)
            {
                Host.Children.Add(AudioUi.Heading("Top artists"));
                var row = new WrapPanel();
                foreach (var (entry, i) in music.TopArtists.Select((e, i) => (e, i)))
                {
                    row.Children.Add(AudioUi.Card(AudioUi.Art(entry.Artist.ArtUrl, 120, AudioUi.MusicGlyph, true), $"{i + 1}. {entry.Artist.Name}",
                        $"{entry.Plays:N0} plays · {AudioTime.Minutes(entry.Minutes)}", () => AudioNav.OpenArtist(Model, Navigator, entry.Artist.Id, entry.Artist.Name), 120));
                }
                Host.Children.Add(row);
            }
            if (music.TopAlbums.Count > 0)
            {
                var shelf = AudioUi.Shelf("Top albums", music.TopAlbums.Select(e => (UIElement)AudioUi.Card(AudioUi.Art(e.Album.ArtUrl, 130), e.Album.Title,
                    $"{e.Plays:N0} plays", () => AudioNav.OpenAlbum(Model, Navigator, e.Album.Id, e.Album.Title), 130)), Scroller);
                if (shelf is not null) Host.Children.Add(shelf);
            }
            if (music.TopTracks.Count > 0)
            {
                Host.Children.Add(AudioUi.Heading("Top songs"));
                var tracks = music.TopTracks.Select(e => e.Track).ToList();
                for (var i = 0; i < music.TopTracks.Count; i++)
                {
                    var index = i;
                    Host.Children.Add(AudioUi.TrackRow(tracks[i], $"{i + 1}", () => _ = Player.PlayTracksAsync(tracks, index, false, new AudioContext { Type = "queue", Name = "Top songs" }),
                        AudioUi.TrackMenu(tracks[i], Navigator)));
                }
                Host.Children.Add(AudioUi.TextButton("Make a playlist of these", () => _ = Try(async () =>
                {
                    var playlist = await Model.Client.CreateMusicPlaylistAsync(new MusicPlaylistInput
                    {
                        Name = _period == "year" ? $"Top songs {_year}" : $"Top songs · {DateTime.Now:MMMM yyyy}",
                        TrackIds = tracks.Select(t => t.Id).ToList(),
                    });
                    AudioNav.OpenPlaylist(Model, Navigator, playlist.Id, playlist.Name);
                }), margin: new Thickness(0, 8, 0, 0)));
            }
            if (music.TopGenres.Count > 0)
            {
                Host.Children.Add(AudioUi.Heading("Top genres"));
                var most = music.TopGenres.Max(g => g.Minutes);
                foreach (var genre in music.TopGenres) Host.Children.Add(BarRow(genre.Genre, genre.Minutes, most, AudioTime.Minutes(genre.Minutes)));
            }
            Host.Children.Add(Rhythm("When you listen to music", music.ByHour, music.ByWeekday));
            if (music.FirstPlay is { } first)
            {
                Host.Children.Add(AudioUi.Caption($"First song of the period: {first.Track.Title} by {first.Track.ArtistLine}{(first.PlayedAt is { } at ? $", {at.ToLocalTime():d MMM}" : "")}.", 10));
            }
        }

        if (stats.Podcasts is { } podcasts)
        {
            Host.Children.Add(AudioUi.Heading("Podcasts", 22));
            Host.Children.Add(Tiles(
                (AudioTime.Minutes(podcasts.Minutes), "listened"),
                ($"{podcasts.EpisodesFinished:N0}", "episodes finished"),
                (AudioTime.Minutes(podcasts.TimeSavedMinutes), "saved by speed and skips")));
            if (podcasts.TopShows.Count > 0)
            {
                var most = podcasts.TopShows.Max(s => s.Minutes);
                foreach (var show in podcasts.TopShows) Host.Children.Add(BarRow(show.Podcast.Title, show.Minutes, most, $"{AudioTime.Minutes(show.Minutes)} · {AudioUi.Count(show.Episodes, "episode", "episodes")}"));
            }
            Host.Children.Add(Rhythm("When you listen to podcasts", podcasts.ByHour, podcasts.ByWeekday));
        }
    }

    private static FrameworkElement Tiles(params (string Value, string Label)[] tiles)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (value, label) in tiles)
        {
            var card = new Border { Style = AudioUi.StyleOf("Card"), Margin = new Thickness(0, 0, 12, 12), MinWidth = 160 };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Palette.Green });
            text.Children.Add(new TextBlock { Text = label, Foreground = Palette.SecondaryText });
            card.Child = text;
            row.Children.Add(card);
        }
        return row;
    }

    private static FrameworkElement BarRow(string label, double value, double most, string caption)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Palette.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var bar = new Border { Background = Palette.Cyan, Height = 10, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var track = new Grid();
        track.Children.Add(bar);
        track.SizeChanged += (_, args) => bar.Width = Math.Max(2, (args.NewSize.Width - 16) * (most <= 0 ? 0 : value / most));
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        var text = new TextBlock { Text = caption, Foreground = Palette.SecondaryText, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Two small bar charts: minutes by hour of day, and by weekday.</summary>
    private static FrameworkElement Rhythm(string title, IReadOnlyList<double> byHour, IReadOnlyList<double> byWeekday)
    {
        var panel = new StackPanel();
        panel.Children.Add(AudioUi.Heading(title));
        var row = new WrapPanel();
        row.Children.Add(Bars(byHour, Enumerable.Range(0, 24).Select(h => h % 6 == 0 ? $"{h}" : "").ToList(), 10, "minutes by hour of day"));
        row.Children.Add(Bars(byWeekday, new List<string> { "M", "T", "W", "T", "F", "S", "S" }, 22, "minutes by weekday"));
        panel.Children.Add(row);
        return panel;
    }

    private static FrameworkElement Bars(IReadOnlyList<double> values, IReadOnlyList<string> labels, double width, string caption)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 32, 10) };
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 80 };
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
        var most = values.Count == 0 ? 0 : values.Max();
        for (var i = 0; i < values.Count; i++)
        {
            var height = most <= 0 ? 1 : Math.Max(1, 78 * values[i] / most);
            bars.Children.Add(new Border
            {
                Width = width - 2,
                Height = height,
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Palette.Green,
                CornerRadius = new CornerRadius(2),
                ToolTip = $"{labels.ElementAtOrDefault(i)} {Math.Round(values[i]):N0} min",
            });
            labelRow.Children.Add(new TextBlock { Text = labels.ElementAtOrDefault(i) ?? "", Width = width, FontSize = 10, Foreground = Palette.SecondaryText, TextAlignment = TextAlignment.Center });
        }
        box.Children.Add(bars);
        box.Children.Add(labelRow);
        box.Children.Add(AudioUi.Caption(caption, 2));
        return box;
    }
}
