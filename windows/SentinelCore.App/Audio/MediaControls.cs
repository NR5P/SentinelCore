using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SentinelCore.Client;
using Windows.Media;
using Windows.Storage.Streams;

namespace SentinelCore.App.Audio;

/// <summary>
/// Windows' own media controls for the player: the keyboard's media keys and the media flyout
/// (volume pop-up, lock screen, Bluetooth headset buttons) with title, artist, artwork and a seek
/// bar. System Media Transport Controls, reached from a desktop app through the per-window
/// interop - which is why the app targets the Windows 10 SDK.
/// </summary>
/// <remarks>Every failure is logged and swallowed: the player must work on a PC where this doesn't.</remarks>
public sealed class MediaControls : IDisposable
{
    private readonly AppModel _model;
    private readonly Dispatcher _dispatcher;
    private SystemMediaTransportControls? _smtc;
    private string? _artPath;
    private int _artGeneration;

    public MediaControls(AppModel model, Dispatcher dispatcher)
    {
        _model = model;
        _dispatcher = dispatcher;
    }

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action? StopPressed;
    public event Action<double>? SeekRequested;

    private bool Ensure()
    {
        if (_smtc is not null) return true;
        try
        {
            var window = Application.Current.MainWindow;
            if (window is null) return false;
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.ButtonPressed += OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
            return true;
        }
        catch (Exception error)
        {
            _model.Log($"Windows media controls are unavailable: {error.Message}", DiagnosticLevel.Notice);
            _smtc = null;
            return false;
        }
    }

    // Raised on a background thread; everything the player does happens on the UI thread.
    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) =>
        _dispatcher.BeginInvoke(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play: PlayPressed?.Invoke(); break;
                case SystemMediaTransportControlsButton.Pause: PausePressed?.Invoke(); break;
                case SystemMediaTransportControlsButton.Next: NextPressed?.Invoke(); break;
                case SystemMediaTransportControlsButton.Previous: PreviousPressed?.Invoke(); break;
                case SystemMediaTransportControlsButton.Stop: StopPressed?.Invoke(); break;
            }
        });

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        _dispatcher.BeginInvoke(() => SeekRequested?.Invoke(args.RequestedPlaybackPosition.TotalSeconds));

    /// <summary>What is playing. Artwork is fetched over the app's session and handed over as bytes.</summary>
    public void SetItem(string title, string? artist, string? album, string? artPath, bool podcast)
    {
        if (!Ensure()) return;
        try
        {
            var updater = _smtc!.DisplayUpdater;
            updater.ClearAll();
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title ?? "";
            updater.MusicProperties.Artist = artist ?? "";
            updater.MusicProperties.AlbumTitle = album ?? "";
            if (podcast) updater.MusicProperties.Genres.Add("Podcast");
            updater.Update();
        }
        catch (Exception error)
        {
            _model.Log($"Could not update the media flyout: {error.Message}", DiagnosticLevel.Notice);
        }

        if (artPath == _artPath) return;
        _artPath = artPath;
        _ = LoadArtAsync(artPath, ++_artGeneration);
    }

    private async Task LoadArtAsync(string? artPath, int generation)
    {
        if (string.IsNullOrEmpty(artPath) || _smtc is null) return;
        try
        {
            var bytes = await _model.Client.DownloadAsync(Client.Networking.SentinelClient.ArtPath(artPath, 600));
            if (generation != _artGeneration || _smtc is null) return;
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            var updater = _smtc.DisplayUpdater;
            updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            updater.Update();
        }
        catch (Exception)
        {
            // No artwork is a blank square in the flyout, never a problem worth a line in the log.
        }
    }

    public void SetPlaying(bool playing, bool hasItem)
    {
        if (!Ensure()) return;
        try
        {
            _smtc!.PlaybackStatus = !hasItem
                ? MediaPlaybackStatus.Closed
                : playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch (Exception)
        {
            // See class remarks.
        }
    }

    private DateTime _lastTimeline = DateTime.MinValue;

    /// <summary>The flyout's seek bar. Sent at most every couple of seconds unless forced.</summary>
    public void SetTimeline(double position, double duration, bool force = false)
    {
        if (_smtc is null || duration <= 0) return;
        if (!force && (DateTime.UtcNow - _lastTimeline).TotalSeconds < 2) return;
        _lastTimeline = DateTime.UtcNow;
        try
        {
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)),
                MaxSeekTime = TimeSpan.FromSeconds(duration),
                EndTime = TimeSpan.FromSeconds(duration),
            });
        }
        catch (Exception)
        {
            // See class remarks.
        }
    }

    public void Clear()
    {
        if (_smtc is null) return;
        try
        {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            _artPath = null;
        }
        catch (Exception)
        {
            // See class remarks.
        }
    }

    public void Dispose()
    {
        if (_smtc is null) return;
        try
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
            _smtc.IsEnabled = false;
        }
        catch (Exception)
        {
            // Shutting down.
        }
        _smtc = null;
    }
}
