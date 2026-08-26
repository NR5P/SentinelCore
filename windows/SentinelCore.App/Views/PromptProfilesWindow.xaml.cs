using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// The prompt profiles, edited a field at a time.
/// </summary>
/// <remarks>
/// The profiles are held here as the raw JSON the server sent, not as a typed object, and that is
/// load-bearing rather than laziness. The save endpoint replaces the whole PromptProfiles
/// dictionary, so anything this app failed to model would be erased by the act of saving - a
/// profile's crop region is a rectangle no form here should be editing, but it must survive being
/// edited here. Keeping the original object and writing back only the handful of fields this screen
/// shows is what makes the rest round-trip untouched.
/// </remarks>
public partial class PromptProfilesWindow : Window
{
    // The fields this screen actually edits. Everything else in a profile is carried through.
    private static readonly string[] EditedFields = { "displayName", "prompt", "notifyWhen" };

    private readonly AppModel _model;
    private Dictionary<string, SettingsValue> _profiles = new();
    private string? _selectedKey;

    public PromptProfilesWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    public sealed record ProfileRow(string Key, string DisplayName);

    private async Task ReloadAsync()
    {
        try
        {
            _profiles = await _model.Client.PromptProfilesAsync();
            ProfileList.ItemsSource = _profiles
                .Select(pair => new ProfileRow(pair.Key, DisplayNameOf(pair.Key, pair.Value)))
                .OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Message(_profiles.Count == 0 ? "This server has no prompt profiles configured." : "");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private static string DisplayNameOf(string key, SettingsValue profile) =>
        profile.Kind == SettingsValue.ValueKind.Object &&
        profile.ObjectValue?.TryGetValue("displayName", out var name) == true &&
        name.DisplayString.Length > 0
            ? name.DisplayString
            : key;

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        CommitEdits();

        if (ProfileList.SelectedItem is not ProfileRow row)
        {
            EditorPanel.Visibility = Visibility.Collapsed;
            _selectedKey = null;
            return;
        }

        _selectedKey = row.Key;
        EditorPanel.Visibility = Visibility.Visible;

        var fields = FieldsOf(row.Key);
        DisplayNameBox.Text = Read(fields, "displayName");
        PromptBox.Text = Read(fields, "prompt");
        NotifyBox.Text = Read(fields, "notifyWhen");

        // Named explicitly rather than left implicit: someone editing a prompt has a right to know
        // that the crop region and everything else they can't see here is being preserved.
        var untouched = fields.Keys
            .Where(key => !EditedFields.Contains(key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UntouchedText.Text = untouched.Count == 0
            ? "This profile has nothing else in it."
            : "Carried through untouched when you save: " + string.Join(", ", untouched) +
              ". This screen doesn't edit those, and saving from here doesn't lose them.";
    }

    private Dictionary<string, SettingsValue> FieldsOf(string key) =>
        _profiles.TryGetValue(key, out var profile) &&
        profile.Kind == SettingsValue.ValueKind.Object &&
        profile.ObjectValue is { } fields
            ? fields
            : new Dictionary<string, SettingsValue>();

    private static string Read(Dictionary<string, SettingsValue> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value.DisplayString : "";

    /// <summary>
    /// Folds the three edited fields back into the profile's own object, leaving everything else in
    /// it exactly as the server sent it.
    /// </summary>
    private void CommitEdits()
    {
        if (_selectedKey is not { } key) return;
        if (EditorPanel.Visibility != Visibility.Visible) return;

        var fields = new Dictionary<string, SettingsValue>(FieldsOf(key));
        fields["displayName"] = SettingsValue.FromString(DisplayNameBox.Text.Trim());
        fields["prompt"] = SettingsValue.FromString(PromptBox.Text);

        var notify = NotifyBox.Text.Trim();
        if (notify.Length > 0)
        {
            fields["notifyWhen"] = SettingsValue.FromString(notify);
        }
        else
        {
            // Removing the key rather than writing an empty string: an empty condition and no
            // condition are different instructions to the server.
            fields.Remove("notifyWhen");
        }

        _profiles[key] = SettingsValue.FromObject(fields);
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        CommitEdits();

        SaveButton.IsEnabled = false;
        try
        {
            await _model.Client.SavePromptProfilesAsync(_profiles);
            Message("Saved.");
            var selected = _selectedKey;
            await ReloadAsync();
            if (selected is not null && ProfileList.ItemsSource is IEnumerable<ProfileRow> rows)
            {
                ProfileList.SelectedItem = rows.FirstOrDefault(row => row.Key == selected);
            }
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
