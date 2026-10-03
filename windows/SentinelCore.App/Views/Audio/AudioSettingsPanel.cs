using System.Windows;
using System.Windows.Controls;
using SentinelCore.App.Audio;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// Settings → MUSIC &amp; PODCASTS: this PC's own player options ("Let others play here", its
/// speaker name, Sound Check, data saver, the default podcast speed), ListenBrainz and wake-up
/// alarms - and for an admin, the hand-built server parts of §1: Music → Libraries, and Podcasts →
/// Download folder (Choose…), storage and OPML. The rest of the server's music and podcasts
/// settings render automatically in Server Settings from the schema.
/// </summary>
public sealed class AudioSettingsPanel : StackPanel
{
    private static readonly (int Kbps, string Label)[] DataSaver =
    {
        (0, "Original quality"), (320, "320 kbps"), (256, "256 kbps"), (192, "192 kbps"), (128, "128 kbps (data saver)"), (96, "96 kbps"),
    };

    private readonly AppModel _model;
    private readonly StackPanel _admin = new();
    private readonly TextBlock _storage = new() { Style = AudioUi.StyleOf("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _libraries = new() { Style = AudioUi.StyleOf("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public AudioSettingsPanel(AppModel model)
    {
        _model = model;
        var player = PlayerController.Shared;
        Children.Add(new TextBlock { Text = "MUSIC & PODCASTS", Style = AudioUi.StyleOf("GroupHeading") });
        if (player is null) return;

        var speaker = new CheckBox { Content = "Let others play here", IsChecked = player.SpeakerEnabled, ToolTip = "Make this PC a speaker other devices can play music and podcasts to" };
        speaker.Click += (_, _) => player.SpeakerEnabled = speaker.IsChecked == true;
        Children.Add(speaker);
        Children.Add(new TextBlock
        {
            Text = "While this is on, the phone, the kiosk or another PC can pick this PC from Play on…, and wake-up alarms can play here. It works with the window closed to the tray.",
            Style = AudioUi.StyleOf("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(26, 2, 0, 6),
        });
        var announce = new CheckBox { Content = "Allow spoken announcements (doorbell, alarm, camera alerts)", IsChecked = player.AllowAnnouncements, Margin = new Thickness(26, 0, 0, 6) };
        announce.Click += (_, _) => player.AllowAnnouncements = announce.IsChecked == true;
        Children.Add(announce);

        var name = new TextBox { Text = player.DeviceName, Width = 260 };
        var rename = AudioUi.TextButton("Save name", () => player.RenameDevice(name.Text), margin: new Thickness(8, 0, 0, 0));
        Children.Add(new TextBlock { Text = "This PC's name as a speaker and in \"Continue from…\"", Style = AudioUi.StyleOf("FieldLabel") });
        Children.Add(AudioUi.Row(name, rename));

        var soundCheck = new CheckBox { Content = "Sound Check - play songs at the same loudness", IsChecked = player.SoundCheckEnabled };
        soundCheck.Click += (_, _) => player.SoundCheckEnabled = soundCheck.IsChecked == true;
        Children.Add(soundCheck);

        Children.Add(new TextBlock { Text = "Music quality", Style = AudioUi.StyleOf("FieldLabel") });
        var quality = new ComboBox { Width = 220, ItemsSource = DataSaver.Select(d => d.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Left };
        quality.SelectedIndex = Math.Max(0, Array.FindIndex(DataSaver, d => d.Kbps == player.DataSaverKbps));
        quality.SelectionChanged += (_, _) => player.DataSaverKbps = DataSaver[Math.Max(0, quality.SelectedIndex)].Kbps;
        Children.Add(quality);

        Children.Add(new TextBlock { Text = "Podcast speed (a show you set a speed for keeps its own)", Style = AudioUi.StyleOf("FieldLabel") });
        var speed = new ComboBox { Width = 120, ItemsSource = PodcastPlayback.Speeds.Select(PodcastPlayback.SpeedLabel).ToList(), HorizontalAlignment = HorizontalAlignment.Left };
        speed.SelectedIndex = Math.Max(0, PodcastPlayback.Speeds.ToList().FindIndex(s => Math.Abs(s - player.DefaultPodcastSpeed) < 0.01));
        speed.SelectionChanged += (_, _) => player.DefaultPodcastSpeed = PodcastPlayback.Speeds[Math.Max(0, speed.SelectedIndex)];
        Children.Add(speed);

        Children.Add(AudioUi.Wrap(
            AudioUi.TextButton("Wake-up alarms…", () => new WakeAlarmsWindow(model) { Owner = this.OwnerWindow() }.ShowDialog()),
            AudioUi.TextButton("ListenBrainz…", () => new ListenBrainzWindow(model) { Owner = this.OwnerWindow() }.ShowDialog()),
            AudioUi.TextButton("Import OPML…", () => _ = PodcastsView.ImportOpmlAsync(model, this.OwnerWindow(), null)),
            AudioUi.TextButton("Export OPML…", () => _ = PodcastsView.ExportOpmlAsync(model, this.OwnerWindow()))));
        ((FrameworkElement)Children[^1]).Margin = new Thickness(0, 14, 0, 0);

        // The admin half: the server's own Music and Podcasts settings.
        _admin.Children.Add(new TextBlock { Text = "Music → Libraries", Style = AudioUi.StyleOf("FieldLabel") });
        _admin.Children.Add(AudioUi.Wrap(
            AudioUi.TextButton("Music libraries…", () =>
            {
                new MusicLibrariesWindow(model) { Owner = this.OwnerWindow() }.ShowDialog();
                Refresh();
            }),
            AudioUi.TextButton("Music settings…", () => new ServerSettingsWindow(model, "music") { Owner = this.OwnerWindow() }.ShowDialog())));
        _admin.Children.Add(_libraries);
        _admin.Children.Add(new TextBlock { Text = "Podcasts → Download folder", Style = AudioUi.StyleOf("FieldLabel") });
        _admin.Children.Add(AudioUi.Wrap(
            AudioUi.TextButton("Choose…", () => _ = ChooseDownloadFolderAsync(), "Pick the folder on the server where episodes are downloaded"),
            AudioUi.TextButton("Podcast settings…", () =>
            {
                new ServerSettingsWindow(model, "podcasts") { Owner = this.OwnerWindow() }.ShowDialog();
                Refresh();
            })));
        _admin.Children.Add(_storage);
        Children.Add(_admin);
        Refresh();
    }

    public void Refresh()
    {
        _admin.Visibility = _model.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        if (!_model.IsAdmin) return;
        _ = LoadStatusAsync();
    }

    private async Task LoadStatusAsync()
    {
        if (_model.MusicEnabled)
        {
            try
            {
                var status = await _model.Client.MusicStatusAsync();
                _libraries.Text = status.Libraries.Count == 0
                    ? "No music libraries yet."
                    : string.Join("\n", status.Libraries.Select(l => $"{l.Name} - {l.Path} ({AudioUi.Count(l.TrackCount, "song", "songs")})")) +
                      (status.Scanning ? "\nScanning now…" : "");
            }
            catch (Exception error)
            {
                _libraries.Text = AppModel.UserFacingMessage(error);
            }
        }
        else
        {
            _libraries.Text = "Music is turned off.";
        }

        if (_model.PodcastsEnabled)
        {
            try
            {
                _storage.Text = StorageLine(await _model.Client.PodcastStatusAsync());
            }
            catch (Exception error)
            {
                _storage.Text = AppModel.UserFacingMessage(error);
            }
        }
        else
        {
            _storage.Text = "Podcasts are turned off.";
        }
    }

    /// <summary>"/home/sentinel/podcasts · 11.2 GB of 50 GB · 1 downloading, 3 waiting · transcripts: …".</summary>
    public static string StorageLine(PodcastStatus status)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(status.DownloadFolder)) parts.Add(status.DownloadFolder!);
        parts.Add(status.MaxStorageBytes > 0
            ? $"{ByteSizeConverter.FormatBytes(status.StorageBytes)} of {ByteSizeConverter.FormatBytes(status.MaxStorageBytes)} used"
            : $"{ByteSizeConverter.FormatBytes(status.StorageBytes)} used");
        if (status.DownloadsActive + status.DownloadsQueued > 0) parts.Add($"{status.DownloadsActive} downloading, {status.DownloadsQueued} waiting");
        parts.Add($"{AudioUi.Count(status.PodcastCount, "show", "shows")}, {AudioUi.Count(status.EpisodeCount, "episode", "episodes")}");
        if (status.Transcripts is { Enabled: true } transcripts)
        {
            parts.Add(transcripts.Available ? $"transcripts on{(transcripts.Queued > 0 ? $" ({transcripts.Queued} queued)" : "")}" : $"transcripts can't run: {transcripts.Error}");
        }
        if (!status.FfmpegAvailable) parts.Add("ffmpeg is missing, so voice boost, trim silence and clips won't work");
        return string.Join(" · ", parts);
    }

    /// <summary>Picks a folder on the server (the §1 picker) and saves it as podcasts.downloadFolder.</summary>
    private async Task ChooseDownloadFolderAsync()
    {
        string? current = null;
        try
        {
            current = (await _model.Client.PodcastStatusAsync()).DownloadFolder;
        }
        catch (Exception)
        {
            // Start from the top instead.
        }
        var picker = new MediaFolderPickerWindow(_model, current, SentinelClient.AudioBase) { Owner = this.OwnerWindow() };
        if (picker.ShowDialog() != true || picker.ChosenPath is not { } path) return;
        try
        {
            await PodcastSettingsSaver.SaveDownloadFolderAsync(_model, path);
            Refresh();
        }
        catch (Exception error)
        {
            MessageBox.Show(this.OwnerWindow(), AppModel.UserFacingMessage(error), "Download folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>
/// Saving one podcasts setting without disturbing the others: the section is PUT whole, so the
/// current values are read from the snapshot and only downloadFolder is replaced.
/// </summary>
public static class PodcastSettingsSaver
{
    public static async Task SaveDownloadFolderAsync(AppModel model, string path)
    {
        var schema = await model.Client.SettingsSchemaAsync();
        var section = schema.FirstOrDefault(s => string.Equals(s.Key, "podcasts", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("This server has no Podcasts settings.");
        var snapshot = await model.Client.SettingsSnapshotAsync();
        var current = SettingsSnapshot.SectionValues(snapshot, section.Key);
        var values = new Dictionary<string, SettingsValue>();
        foreach (var field in section.Fields)
        {
            if (SettingsValue.Read(current, field.Name) is { } value) SettingsValue.Write(values, field.Name, value);
        }
        SettingsValue.Write(values, "downloadFolder", SettingsValue.FromString(path));
        await model.Client.SaveSettingsSectionAsync(section.Endpoint, values);
    }
}
