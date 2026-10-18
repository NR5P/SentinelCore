using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// Where the library's files live: a folder on the server, or a shared folder on another computer
/// (Windows, or Linux with Samba). Admin only - the drives window hides its button from a Viewer.
/// </summary>
/// <remarks>
/// Test signs in and writes a test file without changing anything; "Use this location" runs the
/// same check and then moves the library there. Files already in the old place are never moved.
/// </remarks>
public partial class FilesLocationWindow : Window
{
    private readonly AppModel _model;
    private readonly string _current;

    public FilesLocationWindow(AppModel model, FamilyFilesStatus status)
    {
        _model = model;
        _current = status.LibraryFolder ?? "";
        InitializeComponent();

        KindBox.SelectedIndex = status.LibraryOnNetwork ? 1 : 0;
        if (status.LibraryOnNetwork) NetworkPathBox.Text = _current;
        else LocalPathBox.Text = _current;
        ApplyKind();
    }

    private bool Network => (KindBox.SelectedItem as ComboBoxItem)?.Tag as string == "network";

    private void OnKindChanged(object sender, SelectionChangedEventArgs e) => ApplyKind();

    private void ApplyKind()
    {
        // The ComboBox raises its first change during InitializeComponent, before the panels exist.
        if (LocalPanel is null || NetworkPanel is null) return;
        LocalPanel.Visibility = Network ? Visibility.Collapsed : Visibility.Visible;
        NetworkPanel.Visibility = Network ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The form as typed. Null, with the reason shown, when it cannot be sent at all.</summary>
    private FamilyLibraryLocationInput? Read()
    {
        var input = FamilyLibraryLocationInput.FromForm(
            Network, Network ? NetworkPathBox.Text : LocalPathBox.Text, UsernameBox.Text, PasswordBox.Password);
        if (Network && input.Location.Length == 0)
        {
            Message("Type the shared folder, e.g. smb://192.168.1.20/Family/Files.");
            return null;
        }
        return input;
    }

    private void Show(FamilyLibraryLocationResult result) =>
        Message(!result.Ok && result.NeedsLogin && Network
            ? $"{result.Message}\nEnter a username and password for an account on that computer that can change files in the folder."
            : result.Message);

    private async void OnTestClicked(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } input) return;

        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        Message("Testing…");
        try
        {
            Show(await _model.Client.FamilyFilesTestLibraryLocationAsync(input));
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } input) return;
        if (input.Location == _current)
        {
            Close();
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"Move the library to {(input.Location.Length == 0 ? "the server's data folder" : input.Location)}?\n\n" +
            (_current.Length == 0
                ? "Files already in the old place stay there and stop showing in the library."
                : $"Files already in {_current} stay there and stop showing in the library."),
            "Library location",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;
        if (!confirmed) return;

        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        Message("Checking…");
        try
        {
            var result = await _model.Client.FamilyFilesSetLibraryLocationAsync(input);
            if (result.Ok)
            {
                Detail = result.Message;
                DialogResult = true;
                Close();
                return;
            }
            Show(result);
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        TestButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
    }

    /// <summary>What the server said after a move, for the drives window to show.</summary>
    public string? Detail { get; private set; }

    private void Message(string message) => MessageText.Text = message;

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
