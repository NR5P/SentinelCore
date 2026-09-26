using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// One Movies &amp; TV library: its name, whether it holds movies or TV shows, and its folder.
/// Saving a new library (or a new folder) makes the server scan it straight away.
/// </summary>
public partial class MediaLibraryEditWindow : Window
{
    private readonly AppModel _model;
    private readonly MediaLibrary? _existing;

    public MediaLibraryEditWindow(AppModel model, MediaLibrary? existing)
    {
        _model = model;
        _existing = existing;
        InitializeComponent();

        HeadingText.Text = existing is null ? "Add a library" : "Edit library";
        Title = HeadingText.Text;

        SelectKind(existing?.Kind ?? "movies");
        NameBox.Text = existing?.Name ?? DefaultName(Kind);
        PathBox.Text = existing?.Path ?? "";
        Loaded += (_, _) => (existing is null ? PathBox : NameBox).Focus();
    }

    /// <summary>The library as the server saved it, once Save worked.</summary>
    public MediaLibrary? Saved { get; private set; }

    private static string DefaultName(string kind) => kind == "shows" ? "TV Shows" : "Movies";

    private void SelectKind(string kind) =>
        KindBox.SelectedItem = KindBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == kind)
                               ?? KindBox.Items.OfType<ComboBoxItem>().First();

    private string Kind => (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "movies";

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        // Raised during InitializeComponent, before the name box exists.
        if (NameBox is null) return;

        // A name nobody typed - blank, or the default for the kind it was - follows the kind, so
        // "Movies" does not end up as the name of the TV library.
        var name = NameBox.Text.Trim();
        var wasDefault = name.Length == 0 || e.RemovedItems.OfType<ComboBoxItem>()
            .Any(item => string.Equals(name, DefaultName((string)item.Tag), StringComparison.Ordinal));
        if (wasDefault) NameBox.Text = DefaultName(Kind);
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new MediaFolderPickerWindow(_model, PathBox.Text.Trim()) { Owner = this };
        if (picker.ShowDialog() == true && picker.ChosenPath is { Length: > 0 } chosen)
        {
            PathBox.Text = chosen;
            Message("");
        }
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var path = PathBox.Text.Trim();
        if (name.Length == 0)
        {
            Message("Give the library a name.");
            return;
        }
        if (path.Length == 0)
        {
            Message("Choose the folder the videos are in.");
            return;
        }

        var input = new MediaLibraryInput { Name = name, Kind = Kind, Path = path };
        SaveButton.IsEnabled = false;
        Message(_existing is null ? "Adding…" : "Saving…");
        try
        {
            Saved = _existing is null
                ? await _model.Client.CreateMediaLibraryAsync(input)
                : await _model.Client.UpdateMediaLibraryAsync(_existing.Id, input);
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            SaveButton.IsEnabled = true;
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
