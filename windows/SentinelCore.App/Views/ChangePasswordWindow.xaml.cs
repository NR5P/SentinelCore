using System.Windows;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

public partial class ChangePasswordWindow : Window
{
    private readonly AppModel _model;

    public ChangePasswordWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var mismatch = ConfirmBox.Password.Length > 0 && NewBox.Password != ConfirmBox.Password;
        MessageText.Text = mismatch ? "New passwords don't match." : "";
        MessageText.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (CurrentBox.Password.Length == 0 || NewBox.Password.Length < 8 || NewBox.Password != ConfirmBox.Password)
        {
            Message("Fill in your current password, and a new one of at least 8 characters, twice.");
            return;
        }

        SaveButton.IsEnabled = false;
        SaveButton.Content = "Saving…";
        try
        {
            await _model.Client.ChangeMyPasswordAsync(CurrentBox.Password, NewBox.Password);
            DialogResult = true;
            Close();

            // The server ends every session for the account when the password changes, this one
            // included. Reconnecting immediately is what turns that into a sign-in prompt: without
            // it the app looks connected until whatever happens next fails, which reads as a bug
            // rather than as the sign-out it is.
            await _model.ConnectAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            SaveButton.IsEnabled = true;
            SaveButton.Content = "Save";
        }
    }

    private void Message(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
