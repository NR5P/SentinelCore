using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

public partial class LoginView : UserControl
{
    private AppModel? _model;
    private bool _signingIn;

    public LoginView() => InitializeComponent();

    public void Attach(AppModel model) => _model = model;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SignInAsync();
    }

    private void OnSignInClicked(object sender, RoutedEventArgs e) => _ = SignInAsync();

    private async Task SignInAsync()
    {
        if (_model is null || _signingIn) return;

        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (username.Length == 0 || password.Length == 0) return;

        _signingIn = true;
        SignInButton.IsEnabled = false;
        SignInButton.Content = "Connecting…";
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            await _model.SignInAsync(username, password);
            // Not kept a moment longer than the sign-in needs it.
            PasswordBox.Clear();
        }
        catch (Exception error)
        {
            ErrorText.Text = AppModel.UserFacingMessage(error);
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _signingIn = false;
            SignInButton.IsEnabled = true;
            SignInButton.Content = "Sign In";
        }
    }

    /// <summary>
    /// Clearing the address is what sends the shell back to the setup screen. Worth having here:
    /// an address typed wrong, or a server that has moved, otherwise leaves the app on a sign-in
    /// screen for a machine that will never answer, with no way back.
    /// </summary>
    private void OnChangeServerClicked(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        _model.ServerUrl = "";
    }
}
