using System.Windows;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

public partial class DashboardLoginWindow : Window
{
    private readonly AppModel _model;

    public DashboardLoginWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Apply();
    }

    private void Apply()
    {
        var enabled = _model.AuthStatus?.Enabled == true;
        StatusText.Text = enabled
            ? "A login is required. Everyone opening the dashboard, and every app, has to sign in."
            : "No login is required. Anyone who can reach the dashboard on your network has full access.";

        EnablePanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DisablePanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnEnableClicked(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        if (username.Length == 0 || PasswordBox.Password.Length == 0)
        {
            Message("Enter a username and a password for the first Admin account.");
            return;
        }
        if (PasswordBox.Password != ConfirmBox.Password)
        {
            Message("The two passwords don't match.");
            return;
        }

        EnableButton.IsEnabled = false;
        try
        {
            await _model.Client.EnableDashboardLoginAsync(username, PasswordBox.Password);
            PasswordBox.Clear();
            ConfirmBox.Clear();
            await _model.RefreshAuthStatusAsync();
            Apply();
            Message($"A login is now required. Sign in as {username}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            EnableButton.IsEnabled = true;
        }
    }

    private async void OnDisableClicked(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            this,
            "Stop requiring a login?\n\nAnyone who can reach this server on your network will have full access " +
            "with no password - the cameras, the recordings, and every setting. Existing accounts are kept, " +
            "but nothing will ask for them.",
            "Stop requiring a login",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        DisableButton.IsEnabled = false;
        try
        {
            await _model.Client.DisableDashboardLoginAsync();
            await _model.RefreshAuthStatusAsync();
            Apply();
            Message("A login is no longer required.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            DisableButton.IsEnabled = true;
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
