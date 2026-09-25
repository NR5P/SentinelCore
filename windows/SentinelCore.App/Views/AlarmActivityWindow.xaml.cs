using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

/// <summary>Every arm, disarm, trigger, skipped sensor, low battery and outage, newest first.</summary>
public partial class AlarmActivityWindow : Window
{
    private readonly AppModel _model;

    public AlarmActivityWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        EventHost.Children.Clear();
        try
        {
            var events = await _model.Client.AlarmEventsAsync();
            if (events.Count == 0)
                EventHost.Children.Add(new TextBlock { Text = "Nothing has happened yet.", Style = (Style)FindResource("Caption") });
            foreach (var entry in events)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                row.Children.Add(new TextBlock { Text = entry.Message, Foreground = Color(entry.Kind), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = entry.At.LocalDateTime.ToString("g"), Style = (Style)FindResource("Caption") });
                EventHost.Children.Add(row);
            }
        }
        catch (Exception error)
        {
            EventHost.Children.Add(new TextBlock { Text = AppModel.UserFacingMessage(error), Foreground = Palette.Amber, TextWrapping = TextWrapping.Wrap });
        }
    }

    private Brush Color(string kind) => kind switch
    {
        "triggered" or "panic" => Palette.Red,
        "pending" or "bypassed" or "batteryLow" or "offline" or "pinRejected" => Palette.Amber,
        "armed" or "arming" => Palette.Green,
        _ => Palette.PrimaryText
    };

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
