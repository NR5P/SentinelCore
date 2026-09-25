using System.Windows;
using System.Windows.Input;

namespace SentinelCore.App.Views;

/// <summary>The alarm's PIN, asked for when arming or disarming needs it.</summary>
public partial class AlarmPinWindow : Window
{
    public string Pin { get; private set; } = "";

    public AlarmPinWindow(string heading, string? error)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        if (!string.IsNullOrEmpty(error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => PinBox.Focus();
    }

    private void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOkClicked(sender, e);
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        Pin = PinBox.Password.Trim();
        if (Pin.Length == 0) return;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
}
