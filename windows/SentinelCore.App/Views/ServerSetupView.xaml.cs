using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SentinelCore.Client;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

public partial class ServerSetupView : UserControl
{
    private AppModel? _model;

    public ServerSetupView() => InitializeComponent();

    public void Attach(AppModel model)
    {
        _model = model;
        AddressBox.Text = model.ServerUrl;

        model.DiscoveredServers.CollectionChanged += OnDiscoveredChanged;
        model.PropertyChanged += OnModelPropertyChanged;
        DiscoveredList.ItemsSource = model.DiscoveredServers;

        // Browsing runs only while this screen is up. It is the one place the results are shown,
        // and it is also where someone is likely to be powering the server on as they look at it -
        // but a browse multicasts a query on every interface every five seconds, which is not
        // something to leave running for the life of the process once the address is set.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) model.StartServerDiscovery();
            else model.StopServerDiscovery();
        };
        if (IsVisible) model.StartServerDiscovery();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppModel.LastError)) return;
        Dispatcher.Invoke(() =>
        {
            ErrorText.Text = _model?.LastError ?? "";
            ErrorText.Visibility = string.IsNullOrEmpty(ErrorText.Text) ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void OnDiscoveredChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            var any = _model?.DiscoveredServers.Count > 0;
            DiscoveredPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            SearchingText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        });

    private void OnDiscoveredServerClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DiscoveredServer server) return;
        AddressBox.Text = server.AddressString;
        Connect();
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Connect();
    }

    private void OnConnectClicked(object sender, RoutedEventArgs e) => Connect();

    private void OnWebsiteLinkClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Connect()
    {
        if (_model is null) return;

        // A bare LAN address shouldn't be forced into a scheme nobody configured, so http:// is
        // filled in for anything typed without one.
        var address = ServerAddress.AddSchemeIfMissing(AddressBox.Text);
        if (ServerAddress.Normalize(address) is null)
        {
            ErrorText.Text = "Enter a valid server address, such as http://192.168.1.50:3320.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        // Setting the address is what triggers the connect - the shell watches it.
        _model.ServerIdentity.Forget(address);
        _model.ServerUrl = address;
        _ = _model.ConnectAsync(resetNetworkSession: true);
    }
}
