using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SentinelCore.App.Design;
using SentinelCore.App.Live;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

/// <summary>
/// The camera grid: one live tile per camera, arranged the way this account arranged it.
/// </summary>
public partial class LiveView : UserControl
{
    private const double TileWidth = 360;
    private const double TileHeight = 202;   // 16:9 for the video, with the caption below it

    private readonly AppModel _model;
    private readonly INavigator _navigator;
    private readonly List<Tile> _tiles = new();
    private bool _isActive;

    public LiveView(AppModel model, INavigator? navigator = null)
    {
        _model = model;
        _navigator = navigator ?? NullNavigator.Instance;
        InitializeComponent();

        _model.Cameras.CollectionChanged += OnCamerasChanged;
        _model.PropertyChanged += OnModelPropertyChanged;

        // Visibility is the honest signal for "is anyone looking at this": it covers switching
        // section, pushing a camera detail on top, and coming back from one, without each of those
        // having to remember to say so.
        IsVisibleChanged += (_, _) => SetActive(IsVisible);

        Rebuild();
    }

    /// <summary>
    /// Whether the tiles should be streaming. A grid of WebView2 players decoding video nobody is
    /// looking at is real CPU, and over a tunnel real bandwidth.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        foreach (var tile in _tiles)
        {
            if (active)
            {
                _ = tile.Player.PlayAsync(_model, tile.CameraId);
            }
            else
            {
                tile.Player.Stop();
            }
        }
    }

    private void OnCamerasChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.Invoke(Rebuild);

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.LiveCameras) or nameof(AppModel.IsLicensed))
        {
            Dispatcher.Invoke(Rebuild);
        }
    }

    /// <summary>
    /// Brings the grid in line with the model, keeping the tiles that are still wanted.
    /// </summary>
    /// <remarks>
    /// A tile owns a WebView2 player with a live socket and a decoder behind it, so recreating one
    /// costs a reconnect and several seconds of black. This used to clear the lot and build every
    /// tile again on any change, which - because a refresh replaced the camera list an item at a
    /// time - meant the whole grid was torn down and rebuilt several times over on each 30-second
    /// refresh, and live video could never survive one. Existing tiles are now reused and only
    /// their order is applied, so a refresh that returns the same cameras touches nothing.
    /// </remarks>
    private void Rebuild()
    {
        var cameras = _model.LiveCameras;
        EmptyState.Visibility = cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var existing = _tiles.ToDictionary(tile => tile.CameraId);
        var wanted = cameras.Select(camera => camera.Id).ToHashSet();

        // Cameras that have gone away: stop them before dropping the control, or the player keeps
        // its socket and decoder alive with nothing on screen to show for it.
        foreach (var tile in _tiles.Where(tile => !wanted.Contains(tile.CameraId)))
        {
            tile.Player.Stop();
        }

        var ordered = new List<Tile>(cameras.Count);
        foreach (var camera in cameras)
        {
            if (existing.TryGetValue(camera.Id, out var tile))
            {
                ordered.Add(tile);
                continue;
            }
            tile = BuildTile(camera);
            ordered.Add(tile);
            if (_isActive) _ = tile.Player.PlayAsync(_model, camera.Id);
        }

        // Applied to reused tiles as well as new ones: both are properties of the grid as a whole
        // rather than of a camera, and a tile that outlives the change that altered them would
        // otherwise keep showing the state it was built in - a stale UNLICENSED badge over video
        // that has just been licensed, or a tile that cannot be dragged because it was built when
        // it was the only camera.
        var draggable = cameras.Count > 1;
        foreach (var tile in ordered)
        {
            tile.Watermark.IsLicensed = _model.IsLicensed;
            tile.Root.AllowDrop = draggable;
        }

        _tiles.Clear();
        _tiles.AddRange(ordered);

        // Only the host's ordering is rewritten. The controls themselves are the same instances, so
        // re-adding them re-parents rather than rebuilds.
        TileHost.Items.Clear();
        foreach (var tile in ordered) TileHost.Items.Add(tile.Root);
    }

    private sealed record Tile(Guid CameraId, Border Root, PlayerHost Player, UnlicensedWatermark Watermark);

    private Tile BuildTile(Client.Models.Camera camera)
    {
        var player = new PlayerHost
        {
            // The low rendition, not the main one. A tile is a few hundred pixels wide, and a grid
            // of them pulling full-resolution streams is what saturates the network. The full-size
            // view keeps the main stream, which is where the detail is wanted and where it is the
            // only stream being pulled.
            Quality = "sub",
            // A tile is muted and has no way not to be, so it never asks for audio.
            WantsAudio = false,
            PosterPath = $"/api/cameras/{camera.Id}/thumbnail.jpg",
            Mode = "tile",
            Height = TileHeight,
        };

        var watermark = new UnlicensedWatermark
        {
            IsLicensed = _model.IsLicensed,
            Content = player,
            Height = TileHeight,
        };

        // Record and open are drawn by the player page itself: WebView2 is hosted as a child
        // window, so a WPF button placed over the video would be both invisible and unclickable.
        //
        // A press starts recording and a second press stops it, so this tile's button has to know
        // which one it is showing. The answer comes from the server rather than from a local flag:
        // the same recording can be stopped from the opened camera, from a phone, or by the limit
        // in Settings running out, and none of those go through here.
        var recording = false;
        var busy = false;

        void Apply(Client.Models.ManualRecordingState state)
        {
            recording = state.IsRecording;
            player.SetRecording(state.IsRecording);
        }

        async Task RefreshAsync()
        {
            try { Apply(await _model.Client.RecordingStateAsync(camera.Id)); }
            catch { Apply(Client.Models.ManualRecordingState.Idle); }
        }

        player.RecordRequested += async () =>
        {
            if (busy) return;
            busy = true;
            player.SetBusy("record", true);

            var wasRecording = recording;
            try
            {
                if (wasRecording)
                {
                    var stopped = await _model.Client.StopRecordingAsync(camera.Id);
                    Apply(stopped.State);
                    player.Toast(Client.Models.RecordingText.StopMessage(stopped));
                }
                else
                {
                    Apply(await _model.Client.RecordNowAsync(camera.Id));
                    player.Toast("Recording. Press again to stop.");
                }
            }
            catch (Exception error)
            {
                player.Toast(AppModel.UserFacingMessage(error));
                await RefreshAsync();
            }
            finally
            {
                busy = false;
                player.SetBusy("record", false);
            }
        };

        // A tile built while another device is already recording this camera should come up showing
        // a stop button, not a record one. Safe this early: PlayerHost remembers the state and
        // applies it once its page has loaded, so this does not have to wait for WebView2. Fires
        // again when tiles are re-parented by a drag-reorder, which is a wasted request per tile and
        // a fresh answer for the price - worth it over leaving a tile showing a stale button.
        player.Loaded += async (_, _) => await RefreshAsync();
        player.OpenRequested += () => Dispatcher.Invoke(() => OpenCamera(camera));
        var videoArea = new Grid { Height = TileHeight };
        videoArea.Children.Add(watermark);

        var caption = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 10, 2, 0),
        };
        caption.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Palette.Green,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        caption.Children.Add(new TextBlock
        {
            Text = camera.Name,
            Foreground = Palette.Green,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = TileWidth - 130,
        });
        if (camera.IncludeAudio == true)
        {
            caption.Children.Add(Badge("", "This camera has audio"));
        }
        if (camera.PtzEnabled == true)
        {
            caption.Children.Add(Badge("", "This camera can pan, tilt and zoom"));
        }

        var body = new StackPanel();
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = Brushes.Black,
            Child = videoArea,
        };
        body.Children.Add(frame);
        body.Children.Add(caption);

        var root = new Border
        {
            Width = TileWidth,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackground"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = body,
            Cursor = Cursors.Hand,
            Tag = camera.Id,
            // A single camera has nowhere to go, and starting a drag would only get in the way of
            // opening it.
            AllowDrop = _model.Cameras.Count > 1,
        };

        root.MouseMove += (_, args) =>
        {
            if (args.LeftButton != MouseButtonState.Pressed) return;
            if (_model.Cameras.Count < 2) return;
            DragDrop.DoDragDrop(root, camera.Id.ToString(), DragDropEffects.Move);
        };
        root.DragOver += (_, args) =>
        {
            args.Effects = DragDropEffects.Move;
            root.BorderBrush = Palette.Green;
            args.Handled = true;
        };
        root.DragLeave += (_, _) => root.BorderBrush = (Brush)FindResource("CardBorder");
        root.Drop += (_, args) =>
        {
            root.BorderBrush = (Brush)FindResource("CardBorder");
            if (args.Data.GetData(typeof(string)) is not string dropped) return;
            if (!Guid.TryParse(dropped, out var movingId)) return;
            // The dragged camera takes this tile's place and the rest close up behind it. The move
            // is shown immediately and saved in the background - a round trip to the server is no
            // reason to make anyone watch a tile snap back before it moves.
            _model.MoveLiveCamera(movingId, camera.Id);
        };

        return new Tile(camera.Id, root, player, watermark);
    }

    private static TextBlock Badge(string glyph, string tip) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        Foreground = Palette.Cyan,
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip,
    };

    private void OpenCamera(Client.Models.Camera camera) =>
        // Pushed into the same window. The tiles stop streaming on their own when this view stops
        // being visible, and start again when it comes back.
        _navigator.Push(new LiveCameraView(_model, camera), camera.Name);

    private void OnRefreshClicked(object sender, RoutedEventArgs e) =>
        _ = _model.ConnectAsync(resetNetworkSession: true);
}
