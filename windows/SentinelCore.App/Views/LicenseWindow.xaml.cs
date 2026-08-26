using System.IO;
using System.Windows;
using Microsoft.Win32;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

public partial class LicenseWindow : Window
{
    private readonly AppModel _model;

    public LicenseWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var state = await _model.Client.LicenseStateAsync();
            _model.ApplyLicenseState(state);
            Apply(state.Valid, state.CustomerName);
            Message("");
        }
        catch (Exception error)
        {
            // A check that could not be made says nothing about the licence, so this reports the
            // failure rather than declaring the server unlicensed.
            StatusText.Text = "Could not read the licence status from this server.";
            StatusDot.Fill = Palette.Amber;
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void Apply(bool valid, string? customer)
    {
        StatusText.Text = valid ? "Licensed" : "Not licensed";
        StatusDot.Fill = valid ? Palette.Green : Palette.Amber;
        CustomerText.Text = customer is { Length: > 0 } ? $"Licensed to {customer}" : "";
        CustomerText.Visibility = customer is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Licence file|*.lic|All files|*.*",
            Title = "Choose a licence file",
        };
        if (dialog.ShowDialog(this) != true) return;

        InstallButton.IsEnabled = false;
        try
        {
            var contents = await File.ReadAllTextAsync(dialog.FileName);
            // Verified server-side against the embedded public key before anything is written, so a
            // truncated or wrong pick can't clobber a working licence.
            var state = await _model.Client.InstallLicenseAsync(contents);
            _model.ApplyLicenseState(state);
            Apply(state.Valid, state.CustomerName);
            Message(state.Valid ? "Licence installed." : "The server did not accept that file as a valid licence.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
