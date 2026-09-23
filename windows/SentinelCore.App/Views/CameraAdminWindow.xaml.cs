using System.Windows;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

public partial class CameraAdminWindow : Window
{
    private readonly AppModel _model;

    public CameraAdminWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    public sealed record CameraRow(Client.Models.Camera Camera)
    {
        public string Name => Camera.Name;
        public string StreamUrl => Camera.IsDevicePublished
            ? "Streams from a device - a phone, a Mac, a PC running this app, or a browser's publish page"
            : Camera.StreamUrl ?? "No stream address";
        public Visibility PtzVisibility => Camera.PtzEnabled == true ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AudioVisibility => Camera.IncludeAudio == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReloadAsync()
    {
        try
        {
            var cameras = await _model.Client.CamerasAsync();
            CameraList.ItemsSource = cameras.Select(camera => new CameraRow(camera)).ToList();

            // The app's own list is refreshed too, so the Live grid and the feed agree with what
            // was just changed here rather than waiting for the next poll.
            _model.Cameras.Clear();
            foreach (var camera in cameras) _model.Cameras.Add(camera);

            Message("");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var window = new CameraEditWindow(_model, null) { Owner = this };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CameraRow row) return;
        var window = new CameraEditWindow(_model, row.Camera) { Owner = this };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }

    private void OnZonesClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CameraRow row) return;
        new TriggerZoneEditorWindow(_model, row.Camera) { Owner = this }.ShowDialog();
    }

    private void OnDiscoverClicked(object sender, RoutedEventArgs e)
    {
        var window = new CameraDiscoveryWindow(_model) { Owner = this };
        if (window.ShowDialog() == true) _ = ReloadAsync();
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CameraRow row) return;

        var confirmed = MessageBox.Show(
            this,
            $"Delete \"{row.Name}\"?\n\nIts trigger zones and settings go with it. Recordings and events already " +
            "captured are kept.",
            "Delete camera",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.DeleteCameraAsync(row.Camera.Id);
            await ReloadAsync();
            Message($"Deleted {row.Name}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
