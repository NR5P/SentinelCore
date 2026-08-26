using System.Windows.Controls;
using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

/// <summary>
/// One camera, full size: the main rendition, audio if it carries it, digital zoom, the manual
/// record button, and PTZ if it has it.
/// </summary>
/// <remarks>
/// The controls are drawn by the player page; this class is what they mean. It owns the command
/// queue that keeps a PTZ stop from overtaking its own move, and the hold that turns Home into
/// "save this position".
/// </remarks>
public partial class LiveCameraView : UserControl
{
    private const double Speed = 0.5;

    /// <summary>
    /// How long Home has to be held before it means "save this position" rather than "go to the
    /// saved one". Long enough not to fire on a normal press, short enough to discover by accident -
    /// which matters, because saving home is the only way to make the button work at all on a camera
    /// with no home position of its own, and nothing else in the UI mentions it.
    /// </summary>
    private static readonly TimeSpan SaveHomeHold = TimeSpan.FromMilliseconds(1200);

    private readonly AppModel _model;
    private readonly Camera _camera;
    private readonly PtzCommandQueue _commands = new();
    private readonly DispatcherTimer _homeHold;

    private bool _recording;
    private bool _homeHandled;

    public LiveCameraView(AppModel model, Camera camera)
    {
        _model = model;
        _camera = camera;
        InitializeComponent();

        Watermark.IsLicensed = model.IsLicensed;

        // Audio is asked for only when the camera actually carries it: asking go2rtc for a track it
        // then has to carry costs bandwidth on every camera, and most have none.
        Player.WantsAudio = camera.IncludeAudio == true;
        Player.PtzEnabled = camera.PtzEnabled == true;
        Player.PosterPath = $"/api/cameras/{camera.Id}/thumbnail.jpg";

        Player.RecordRequested += OnRecordRequested;
        Player.PtzGesture += OnPtzGesture;

        _homeHold = new DispatcherTimer { Interval = SaveHomeHold };
        _homeHold.Tick += (_, _) => SaveHome();

        Loaded += async (_, _) => await Player.PlayAsync(_model, _camera.Id);
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Releases the stream and leaves the camera still. Called when navigating away.</summary>
    public void Stop()
    {
        _homeHold.Stop();
        Player.RecordRequested -= OnRecordRequested;
        Player.PtzGesture -= OnPtzGesture;
        Player.Stop();

        // Whatever the camera was doing when this closed, it should not keep doing it. Stop on an
        // idle camera is a no-op, so sending it unconditionally is safe - and a press whose release
        // was swallowed by the screen going away is exactly the case that needs it.
        if (_camera.PtzEnabled == true) _commands.Enqueue(() => _model.Client.PtzStopAsync(_camera.Id));
    }

    /// <summary>
    /// Every camera gets this, not just a PTZ one: the moment worth keeping is just as likely on a
    /// fixed camera you happened to be looking at. The server turns it into an ordinary trigger, so
    /// what comes out the other end is a row in Events exactly like a motion event.
    /// </summary>
    private async void OnRecordRequested()
    {
        if (_recording) return;
        _recording = true;
        Player.SetBusy("record", true);

        try
        {
            await _model.Client.RecordNowAsync(_camera.Id);
            Player.Toast("Recording saved. It will appear in Events shortly.");
        }
        catch (Exception error)
        {
            Player.Toast(AppModel.UserFacingMessage(error));
        }
        finally
        {
            _recording = false;
            Player.SetBusy("record", false);
        }
    }

    private void OnPtzGesture(string direction, bool pressed)
    {
        if (direction == "home")
        {
            if (pressed) BeginHomePress();
            else EndHomePress();
            return;
        }

        if (!pressed)
        {
            _commands.Enqueue(() => _model.Client.PtzStopAsync(_camera.Id));
            return;
        }

        var (pan, tilt, zoom) = direction switch
        {
            "up" => (0d, Speed, 0d),
            "down" => (0d, -Speed, 0d),
            "left" => (-Speed, 0d, 0d),
            "right" => (Speed, 0d, 0d),
            "upLeft" => (-Speed, Speed, 0d),
            "upRight" => (Speed, Speed, 0d),
            "downLeft" => (-Speed, -Speed, 0d),
            "downRight" => (Speed, -Speed, 0d),
            "zoomIn" => (0d, 0d, Speed),
            "zoomOut" => (0d, 0d, -Speed),
            _ => (0d, 0d, 0d),
        };
        if (pan == 0 && tilt == 0 && zoom == 0) return;

        _commands.Enqueue(() => _model.Client.PtzMoveAsync(_camera.Id, pan, tilt, zoom));
    }

    /// <summary>
    /// Home is a one-shot goto, not a continuous move, so it has no stop to pair with - but the
    /// press still has to be held open, because holding it past the threshold means the opposite
    /// thing. The hold fires on its own timer rather than on release, so the camera saves the moment
    /// the threshold passes and the button coming up afterwards is not another command.
    /// </summary>
    private void BeginHomePress()
    {
        _homeHandled = false;
        Player.SetHomeSaved(false);
        _homeHold.Start();
    }

    private void EndHomePress()
    {
        _homeHold.Stop();
        if (_homeHandled) return;
        _homeHandled = true;

        _commands.Enqueue(async () =>
        {
            try
            {
                await _model.Client.PtzHomeAsync(_camera.Id);
                Report("Returning to the home position.");
            }
            catch (Exception error)
            {
                // PTZ is otherwise fire-and-forget on purpose - a move that didn't land is obvious
                // from the picture. Home is the one command whose failure looks exactly like
                // success: the picture doesn't change either way.
                Report(AppModel.UserFacingMessage(error));
            }
        });
    }

    private void SaveHome()
    {
        _homeHold.Stop();
        if (_homeHandled) return;
        _homeHandled = true;
        Dispatcher.Invoke(() => Player.SetHomeSaved(true));

        _commands.Enqueue(async () =>
        {
            try
            {
                await _model.Client.PtzSaveHomeAsync(_camera.Id);
                Report("Saved this position as home.");
            }
            catch (Exception error)
            {
                Report(AppModel.UserFacingMessage(error));
            }
        });
    }

    private void Report(string message) => Dispatcher.Invoke(() => Player.Toast(message));
}
