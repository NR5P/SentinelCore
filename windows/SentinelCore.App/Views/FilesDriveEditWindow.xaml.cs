using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// One backup drive's settings: a folder the server can reach, or an SFTP account on another
/// computer. Admin only - the drives list hides its Edit and Add buttons from a Viewer.
/// </summary>
/// <remarks>
/// The password never comes back from the server; the box is blank on an existing drive and a
/// blank box on save means "keep what is saved". Test connection sends the form as typed, with
/// the drive's id so that the same rule applies there.
/// </remarks>
public partial class FilesDriveEditWindow : Window
{
    private readonly AppModel _model;
    private readonly FamilyDrive? _existing;

    public FilesDriveEditWindow(AppModel model, FamilyDrive? existing)
    {
        _model = model;
        _existing = existing;
        InitializeComponent();

        HeadingText.Text = existing is null ? "Add a backup drive" : "Edit backup drive";
        Title = HeadingText.Text;
        DeleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        PasswordHint.Visibility = existing is { HasPassword: true } ? Visibility.Visible : Visibility.Collapsed;

        Populate(existing is null ? new FamilyDriveInput() : FamilyDriveInput.FromDrive(existing));
    }

    private void Populate(FamilyDriveInput input)
    {
        NameBox.Text = input.Name;
        SelectKind(input.Kind);
        FolderPathBox.Text = input.FolderPath;
        HostBox.Text = input.SftpHost;
        PortBox.Text = input.SftpPort.ToString(CultureInfo.InvariantCulture);
        UsernameBox.Text = input.SftpUsername;
        SftpFolderBox.Text = input.SftpFolder;
        FingerprintBox.Text = input.SftpHostKeyFingerprint;
        EnabledToggle.IsChecked = input.Enabled;
        ApplyKind();
    }

    private void SelectKind(string kind) =>
        KindBox.SelectedItem = KindBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == kind)
                               ?? KindBox.Items.OfType<ComboBoxItem>().First();

    private string Kind => (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "folder";

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => ApplyKind();

    private void ApplyKind()
    {
        // Both panels exist before the ComboBox raises its first change, but not before the
        // constructor's InitializeComponent finishes - hence the null checks.
        if (FolderPanel is null || SftpPanel is null) return;
        var sftp = Kind == "sftp";
        FolderPanel.Visibility = sftp ? Visibility.Collapsed : Visibility.Visible;
        SftpPanel.Visibility = sftp ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The form as typed. Null, with the reason shown, when it cannot be sent at all.</summary>
    private FamilyDriveInput? Read()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Message("Give the drive a name.");
            return null;
        }

        var port = 22;
        if (Kind == "sftp" && (!int.TryParse(PortBox.Text.Trim(), out port) || port is < 1 or > 65535))
        {
            Message("The SFTP port must be a number between 1 and 65535.");
            return null;
        }

        return new FamilyDriveInput
        {
            Id = _existing?.Id ?? "",
            Name = name,
            Kind = Kind,
            Enabled = EnabledToggle.IsChecked == true,
            FolderPath = FolderPathBox.Text.Trim(),
            SftpHost = HostBox.Text.Trim(),
            SftpPort = port,
            SftpUsername = UsernameBox.Text.Trim(),
            SftpPassword = PasswordBox.Password,
            SftpFolder = SftpFolderBox.Text.Trim(),
            SftpHostKeyFingerprint = FingerprintBox.Text.Trim(),
        };
    }

    private async void OnTestClicked(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } input) return;

        TestButton.IsEnabled = false;
        Message("Testing…");
        try
        {
            var result = await _model.Client.FamilyFilesTestDriveAsync(input);
            Message(result.Message);

            // The server saw a host key. Whether the test passed (no fingerprint pinned yet) or
            // failed (a different one pinned), offering to pin what was seen is the one useful
            // next step - nobody types 43 characters of base64 by hand.
            if (result.HostKeyFingerprint is { Length: > 0 } seen && seen != FingerprintBox.Text.Trim())
            {
                var pin = MessageBox.Show(
                    this,
                    $"The server saw this host key fingerprint:\n\n{seen}\n\nUse it for this drive? Only say yes if you " +
                    "trust the connection you tested over - once set, the backup refuses any other key.",
                    "Host key fingerprint",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (pin) FingerprintBox.Text = seen;
            }
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } input) return;

        SaveButton.IsEnabled = false;
        try
        {
            if (_existing is null)
            {
                await _model.Client.FamilyFilesCreateDriveAsync(input);
            }
            else
            {
                await _model.Client.FamilyFilesUpdateDriveAsync(_existing.Id, input);
            }
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;

        var confirmed = MessageBox.Show(
            this,
            $"Remove \"{_existing.Name}\" from the backup drives?\n\nNothing on the drive itself is touched - the " +
            "copy already there stays as it is. It just stops being kept up to date.",
            "Remove drive",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.FamilyFilesDeleteDriveAsync(_existing.Id);
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
