using System.Windows;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

/// <summary>
/// Signs the server in to another computer's shared folders (SMB), for a Movies &amp; TV library
/// on that computer. New computers are POSTed; a saved one is PUT, where a blank password keeps the
/// password already saved. The server tests the sign-in before it saves anything.
/// </summary>
public partial class MediaNetworkSignInWindow : Window
{
    private readonly AppModel _model;
    private readonly MediaNetworkComputer? _existing;

    /// <param name="existing">A saved computer to sign in to again, or null for a new one.</param>
    /// <param name="host">Prefills the computer box for a new one (a folder that needed a sign-in).</param>
    // /api/media for Movies, /api/audio for Music & Podcasts - one saved list of sign-ins either way.
    private readonly string _basePath;

    public MediaNetworkSignInWindow(AppModel model, MediaNetworkComputer? existing, string? host = null, string basePath = SentinelClient.MediaBase)
    {
        _model = model;
        _existing = existing;
        _basePath = basePath;
        InitializeComponent();

        HeadingText.Text = existing is null ? "Sign in to a computer" : $"Sign in to {existing.DisplayName}";
        Title = HeadingText.Text;
        ForgetButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        PasswordHint.Visibility = existing is { HasPassword: true } ? Visibility.Visible : Visibility.Collapsed;

        HostBox.Text = existing?.Host ?? host ?? "";
        NameBox.Text = existing is not null && existing.Name != existing.Host ? existing.Name : "";
        UsernameBox.Text = existing is null
            ? ""
            : existing.Domain.Length > 0 ? $"{existing.Domain}\\{existing.Username}" : existing.Username;

        Loaded += (_, _) =>
        {
            if (HostBox.Text.Length == 0) HostBox.Focus();
            else if (UsernameBox.Text.Length == 0) UsernameBox.Focus();
            else PasswordBox.Focus();
        };
    }

    /// <summary>The saved computer and the shared folders it listed, after a sign-in that worked.</summary>
    public MediaNetworkSignInResult? Result { get; private set; }

    /// <summary>True when the dialog closed because the computer was forgotten.</summary>
    public bool Forgotten { get; private set; }

    private async void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            Message("Type the computer's name or IP address, like DESKTOP-PC or 192.168.1.20.");
            HostBox.Focus();
            return;
        }

        // "DOMAIN\name" is split here rather than left to the server: the server decides whether a
        // blank password keeps the saved one by comparing the username alone, before its own split.
        var username = UsernameBox.Text.Trim();
        var domain = "";
        var slash = username.IndexOf('\\');
        if (slash > 0)
        {
            domain = username[..slash];
            username = username[(slash + 1)..];
        }

        var input = new MediaNetworkComputerInput
        {
            Host = host,
            Name = NameBox.Text.Trim() is { Length: > 0 } name ? name : null,
            Username = username,
            Password = PasswordBox.Password,
            Domain = domain,
        };

        SignInButton.IsEnabled = false;
        Message($"Signing in to {host}…");
        try
        {
            Result = await _model.Client.SaveMediaNetworkComputerAsync(_existing?.Id, input, basePath: _basePath);
            DialogResult = true;
            Close();
        }
        catch (SentinelServerException error) when (error.NeedsLogin)
        {
            Message(error.Message);
            PasswordBox.Clear();
            (UsernameBox.Text.Length == 0 ? UsernameBox : (UIElement)PasswordBox).Focus();
            SignInButton.IsEnabled = true;
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            SignInButton.IsEnabled = true;
        }
    }

    private async void OnForgetClicked(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;

        var confirmed = MessageBox.Show(
            this,
            $"Forget the sign-in for {_existing.DisplayName}?\n\nLibraries on that computer stop scanning until it " +
            "is signed in to again. Nothing on the computer itself is touched.",
            "Forget computer",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.DeleteMediaNetworkComputerAsync(_existing.Id, basePath: _basePath);
            Forgotten = true;
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
