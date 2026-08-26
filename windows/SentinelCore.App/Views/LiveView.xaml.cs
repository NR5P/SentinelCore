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

    private void Rebuild()
    {
        foreach (var tile in _tiles) tile.Player.Stop();
        _tiles.Clear();
        TileHost.Items.Clear();

        var cameras = _model.LiveCameras;
        EmptyState.Visibility = cameras.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var camera in cameras)
        {
            var tile = BuildTile(camera);
            _tiles.Add(tile);
            TileHost.Items.Add(tile.Root);
            if (_isActive) _ = tile.Player.PlayAsync(_model, camera.Id);
        }
    }

    private sealed record Tile(Guid CameraId, Border Root, PlayerHost Player);

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
        player.RecordRequested += async () =>
        {
            player.SetBusy("record", true);
            try
            {
                await _model.Client.RecordNowAsync(camera.Id);
                player.Toast("Recording saved. It will appear in Events shortly.");
            }
            catch (Exception error)
            {
                player.Toast(AppModel.UserFacingMessage(error));
            }
            finally
            {
                player.SetBusy("record", false);
            }
        };
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

        return new Tile(camera.Id, root, player);
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
