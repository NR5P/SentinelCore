using System.IO;
using System.Windows;
using Microsoft.Win32;
using SentinelCore.Client;

namespace SentinelCore.App.Views;

public partial class BackupWindow : Window
{
    private readonly AppModel _model;

    public BackupWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Sentinel Core backup|*.json",
            FileName = $"sentinel-core-backup-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportButton.IsEnabled = false;
        try
        {
            // Raw bytes in both directions: a client that decoded a backup into a fixed shape would
            // quietly drop any section added after this app was built, which is the one thing a
            // backup must never do.
            var bytes = await _model.Client.ExportBackupAsync();
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            Message($"Saved {Design.ByteSizeConverter.FormatBytes(bytes.Length)} to {dialog.FileName}");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private async void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Sentinel Core backup|*.json|All files|*.*",
            Title = "Choose a backup to restore",
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirmed = MessageBox.Show(
            this,
            $"Restore from {Path.GetFileName(dialog.FileName)}?\n\n" +
            "This replaces the server's cameras, zones, accounts and settings with what is in the file. " +
            "Anything added since that backup was taken is lost.",
            "Restore backup",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        ImportButton.IsEnabled = false;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            var result = await _model.Client.ImportBackupAsync(bytes);
            Message($"Restored {result.Summary}. Reconnecting…");
            await _model.ConnectAsync(resetNetworkSession: true);
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
