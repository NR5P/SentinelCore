using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class AccountAdminWindow : Window
{
    private readonly AppModel _model;

    public AccountAdminWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>One account, dressed for the list.</summary>
    public sealed record AccountRow(Account Account)
    {
        public string Username => Account.Username;

        public string Detail =>
            (string.IsNullOrWhiteSpace(Account.DisplayName) ? "" : $"{Account.DisplayName} · ") +
            $"{Account.Role} · created {Account.CreatedAt.LocalDateTime:d MMM yyyy}";

        public string RoleButtonLabel => Account.Role == "Admin" ? "Make Viewer" : "Make Admin";
    }

    private async Task ReloadAsync()
    {
        try
        {
            var accounts = await _model.Client.AccountsAsync();
            AccountList.ItemsSource = accounts.Select(account => new AccountRow(account)).ToList();
            Message("");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var username = NewUsername.Text.Trim();
        var password = NewPassword.Password;
        var role = (NewRole.SelectedItem as ComboBoxItem)?.Content as string ?? "Viewer";
        var displayName = NewDisplayName.Text.Trim();

        if (username.Length == 0 || password.Length == 0)
        {
            Message("A new account needs a username and a password.");
            return;
        }

        AddButton.IsEnabled = false;
        try
        {
            await _model.Client.CreateAccountAsync(
                username, password, role, displayName.Length == 0 ? null : displayName);
            NewUsername.Clear();
            NewDisplayName.Clear();
            NewPassword.Clear();
            await ReloadAsync();
            Message($"Added {username}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private async void OnToggleRoleClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AccountRow row) return;
        var target = row.Account.Role == "Admin" ? "Viewer" : "Admin";

        // Demoting yourself is allowed by the server but locks you out of every admin screen in the
        // same instant, so it is worth a question first.
        if (target == "Viewer" && row.Username == _model.AuthStatus?.Username)
        {
            var confirmed = MessageBox.Show(
                this,
                "That's the account you're signed in with. Making it a Viewer takes away your own access to " +
                "cameras, settings and accounts - and only another Admin could give it back. Continue?",
                "Change your own role",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;
            if (!confirmed) return;
        }

        try
        {
            await _model.Client.UpdateAccountRoleAsync(row.Account.Id, target);
            await ReloadAsync();
            await _model.RefreshAuthStatusAsync();
            Message($"{row.Username} is now a {target}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnResetPasswordClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AccountRow row) return;

        var prompt = new TextPromptWindow(
            "Reset password",
            $"New password for {row.Username}:",
            isPassword: true)
        {
            Owner = this,
        };
        if (prompt.ShowDialog() != true) return;

        try
        {
            await _model.Client.ResetAccountPasswordAsync(row.Account.Id, prompt.Value);
            Message($"Password reset for {row.Username}. Every session signed in as that account has ended.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AccountRow row) return;

        var confirmed = MessageBox.Show(
            this,
            $"Delete the account \"{row.Username}\"? Anyone signed in as it is signed out immediately.",
            "Delete account",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.DeleteAccountAsync(row.Account.Id);
            await ReloadAsync();
            Message($"Deleted {row.Username}.");
        }
        catch (Exception error)
        {
            // The server refuses to delete the last Admin, which is exactly the message worth
            // showing rather than a generic failure.
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
