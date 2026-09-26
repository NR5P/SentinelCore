using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// One film or episode, playing across the whole window. The picture and every control are the
/// embedded Live/movie.html page; this side asks the server for a playback, turns the page's
/// player clock into movie time, and saves progress.
/// </summary>
/// <remarks>
/// Disposed by the navigator on Back (or on the page's own close button, which pops). Disposing
/// saves the last known position, stops the page, and gives the window its chrome back. The
/// <c>finished</c> callback runs once that last save has landed, so the detail page underneath can
/// reload and show the new resume point rather than the one from before this session.
/// </remarks>
public partial class MoviePlayerView : UserControl, IDisposable
{
    private readonly AppModel _model;
    private readonly INavigator _navigator;
    private readonly Action? _finished;
    private readonly double? _initialStart;

    private string _itemId;
    private MediaPlayback? _playback;
    private double _playerSeconds;
    private double _lastReported = -1;
    private bool _started;
    private bool _disposed;

    public MoviePlayerView(AppModel model, INavigator navigator, string itemId, double? start, Action? finished = null)
    {
        _model = model;
        _navigator = navigator;
        _itemId = itemId;
        _initialStart = start;
        _finished = finished;
        InitializeComponent();

        Player.MovieTime += OnTime;
        Player.MovieProgress += OnProgress;
        Player.MovieClosed += OnClosed;
        Player.MovieNextRequested += OnNextRequested;
        Player.MovieRestartRequested += OnRestartRequested;
        Player.MovieFailed += OnFailed;
        Player.FullScreenChanged += OnFullScreenChanged;

        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>The item on screen now - it changes when Up next moves on to the next episode.</summary>
    public string ItemId => _itemId;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _started) return;
        _started = true;
        await StartAsync(_itemId, _initialStart);
    }

    // Closing the window only hides it to the tray; a film has no business playing on behind it.
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_disposed && !IsVisible) Player.SetPlaying(false);
    }

    private async Task StartAsync(string itemId, double? start)
    {
        MediaPlayback playback;
        try
        {
            playback = await _model.Client.MediaPlaybackAsync(itemId, start);
        }
        catch (Exception error)
        {
            if (_disposed) return;
            _model.Log($"Movie playback could not start: {error.Message}", DiagnosticLevel.Notice);
            Player.ShowMessage(AppModel.UserFacingMessage(error));
            return;
        }
        if (_disposed) return;

        _itemId = itemId;
        _playback = playback;
        _playerSeconds = MediaPlaybackRules.InitialSeek(playback);
        _lastReported = -1;

        // The window goes to the film only once the page - and its close button - is actually up.
        // Until then, and whenever WebView2 is missing, the shell's Back bar stays the way out.
        if (await Player.PlayMovieAsync(_model, playback) && !_disposed) _navigator.SetImmersive(true);
    }

    private void OnTime(double playerSeconds) => _playerSeconds = playerSeconds;

    private void OnProgress(double playerSeconds, string reason)
    {
        _playerSeconds = playerSeconds;
        _ = ReportAsync(playerSeconds);
    }

    /// <summary>
    /// Saves where the viewer is, in movie time. Nothing under 30 seconds is sent (the server
    /// would ignore it), and a position already saved is not sent twice - the page's own "close"
    /// report and this view's final one on Dispose are usually the same number.
    /// </summary>
    private Task ReportAsync(double playerSeconds)
    {
        if (_playback is not { } playback) return Task.CompletedTask;
        var absolute = MediaPlaybackRules.AbsolutePosition(playback, playerSeconds);
        if (!MediaPlaybackRules.IsWorthReporting(absolute)) return Task.CompletedTask;
        if (Math.Abs(absolute - _lastReported) < 0.5) return Task.CompletedTask;
        _lastReported = absolute;

        var client = _model.Client;
        var id = _itemId;
        var duration = playback.DurationSeconds;
        return Task.Run(async () =>
        {
            try
            {
                await client.ReportMediaProgressAsync(id, absolute, duration).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A missed save costs a few seconds of resume point; it must never stop the film.
                _model.Log($"Could not save movie progress: {error.Message}", DiagnosticLevel.Notice);
            }
        });
    }

    private void OnClosed() => _navigator.Pop();

    private async void OnNextRequested()
    {
        if (_disposed || _playback?.NextEpisodeId is not { Length: > 0 } next) return;
        await ReportAsync(_playerSeconds);
        // No start: the server resumes the next episode if it was already begun, else plays it
        // from the top.
        await StartAsync(next, null);
    }

    private async void OnRestartRequested(double absoluteSeconds)
    {
        if (_disposed) return;
        await StartAsync(_itemId, Math.Max(0, absoluteSeconds));
    }

    private void OnFailed(string message) =>
        _model.Log($"Movie playback failed: {message}", DiagnosticLevel.Notice);

    private void OnFullScreenChanged(bool fullScreen)
    {
        if (!_disposed) _navigator.SetImmersive(true, fullScreen);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var saved = ReportAsync(_playerSeconds);

        Player.MovieTime -= OnTime;
        Player.MovieProgress -= OnProgress;
        Player.MovieClosed -= OnClosed;
        Player.MovieNextRequested -= OnNextRequested;
        Player.MovieRestartRequested -= OnRestartRequested;
        Player.MovieFailed -= OnFailed;
        Player.FullScreenChanged -= OnFullScreenChanged;
        Loaded -= OnLoaded;
        IsVisibleChanged -= OnVisibleChanged;

        Player.Stop();
        _navigator.SetImmersive(false);

        if (_finished is { } finished)
        {
            var dispatcher = Dispatcher;
            saved.ContinueWith(_ => dispatcher.InvokeAsync(finished), TaskScheduler.Default);
        }
    }
}
