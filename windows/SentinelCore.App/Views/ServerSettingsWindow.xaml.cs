using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// Every server setting the web dashboard has, rendered from the schema the server publishes.
/// </summary>
/// <remarks>
/// Nothing here knows what any particular setting means. The server describes its own sections and
/// fields (GET /api/settings/schema) and this draws whatever it is told to - which is the whole
/// point: a section added on the server appears here with no change to this app, and the web
/// dashboard, the Android app and the Apple apps all stay in step for free.
///
/// Two rules matter on save. Secrets arrive masked and are sent back unchanged to mean "keep the
/// stored value" - so an untouched password box must send the mask, not an empty string. And an
/// unrecognised field type is rendered read-only rather than guessed at, because writing back
/// something malformed is worse than not offering to edit it.
/// </remarks>
public partial class ServerSettingsWindow : Window
{
    private readonly AppModel _model;

    private List<SettingsSectionSchema> _schema = new();
    private Dictionary<string, SettingsValue> _snapshot = new();
    private SettingsSectionSchema? _section;
    private readonly Dictionary<string, Func<SettingsValue?>> _readers = new();
    // How to put a value back into a field's control, for a lookup that fills several at once.
    private readonly Dictionary<string, Action<SettingsValue>> _writers = new();

    public ServerSettingsWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    public sealed record SectionRow(SettingsSectionSchema Section, string Title, string GroupHeading, Visibility GroupHeadingVisibility);

    private async Task LoadAsync()
    {
        try
        {
            _schema = await _model.Client.SettingsSchemaAsync();
            _snapshot = await _model.Client.SettingsSnapshotAsync();
            BuildSectionList();
            if (_schema.Count > 0) ShowSection(VisibleSections().FirstOrDefault() ?? _schema[0]);
            Message("");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private IEnumerable<SettingsSectionSchema> VisibleSections() =>
        _schema.Where(section => ShowAdvancedToggle.IsChecked == true || section.Basic);

    private void BuildSectionList()
    {
        var rows = new List<SectionRow>();
        string? lastGroup = null;

        // Grouping comes from the server so this app and the dashboard divide the settings the same
        // way. A server that predates grouping sends none, and everything lands in one flat list -
        // which is exactly what it had before.
        foreach (var section in VisibleSections()
                     .OrderBy(section => section.GroupOrder ?? int.MaxValue)
                     .ThenBy(section => section.Title, StringComparer.OrdinalIgnoreCase))
        {
            var group = section.Group;
            var isNewGroup = group is { Length: > 0 } && group != lastGroup;
            rows.Add(new SectionRow(
                section,
                section.Title,
                group ?? "",
                isNewGroup ? Visibility.Visible : Visibility.Collapsed));
            if (isNewGroup) lastGroup = group;
        }

        SectionList.ItemsSource = rows;
    }

    private void OnShowAdvancedToggled(object sender, RoutedEventArgs e)
    {
        BuildSectionList();
        if (_section is not null && !VisibleSections().Contains(_section))
        {
            var first = VisibleSections().FirstOrDefault();
            if (first is not null) ShowSection(first);
        }
    }

    private void OnSectionClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SectionRow row) return;
        ShowSection(row.Section);
    }

    private async void ShowSection(SettingsSectionSchema section)
    {
        _section = section;
        _readers.Clear();
        _writers.Clear();
        FieldHost.Children.Clear();

        FieldHost.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Green,
            Margin = new Thickness(0, 0, 0, 4),
        });

        if (section.Description is { Length: > 0 })
        {
            FieldHost.Children.Add(new TextBlock
            {
                Text = section.Description,
                Foreground = Palette.SecondaryText,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });
        }

        // Most sections read their values out of the bulk snapshot, which is keyed by section - a
        // field name is relative to its own section, not to the top of the snapshot. A section that
        // says where its values live instead (only INTERNET EXPOSED MODE today, which configures
        // the dashboard's own listener and so lives in a different settings file) is fetched from
        // there, and that response is already the section's own values.
        var values = SettingsSnapshot.SectionValues(_snapshot, section.Key);
        if (section.DataEndpoint is { Length: > 0 } endpoint)
        {
            try
            {
                values = await _model.Client.SettingsSectionDataAsync(endpoint);
            }
            catch (Exception error)
            {
                Message(AppModel.UserFacingMessage(error));
                values = new Dictionary<string, SettingsValue>();
            }
        }

        foreach (var field in section.Fields) FieldHost.Children.Add(BuildField(field, values));

        if (section.NeedsRestart)
        {
            FieldHost.Children.Add(new TextBlock
            {
                Text = $"Some settings here only take effect after the {section.RestartTarget} restarts. " +
                       "Saving does not restart it for you.",
                Foreground = Palette.Amber,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 18, 0, 0),
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        var save = new Button { Content = "Save", MinWidth = 90, Style = (Style)FindResource("PrimaryButton") };
        save.Click += async (_, _) => await SaveAsync(section, save);
        buttons.Children.Add(save);

        // An extra button a section offers - pure data, an endpoint to POST to, so no client needs
        // bespoke code per action.
        if (section.ExtraAction is { } action)
        {
            var extra = new Button
            {
                Content = action.Label,
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = action.Tip,
            };
            extra.Click += async (_, _) => await RunActionAsync(action, extra);
            buttons.Children.Add(extra);
        }

        FieldHost.Children.Add(buttons);
    }

    private UIElement BuildField(SettingsFieldSchema field, Dictionary<string, SettingsValue> values)
    {
        var current = SettingsValue.Read(values, field.Name);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        // A checkbox carries its own label; everything else gets one above the control.
        if (field.Type != "checkbox")
        {
            panel.Children.Add(new TextBlock
            {
                Text = field.Label,
                Foreground = Palette.SecondaryText,
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        switch (field.Type)
        {
            case "checkbox":
            {
                var box = new CheckBox { Content = field.Label, IsChecked = current?.AsBool ?? false };
                _readers[field.Name] = () => SettingsValue.FromBool(box.IsChecked == true);
                _writers[field.Name] = value => box.IsChecked = value.AsBool;
                panel.Children.Add(box);
                break;
            }

            case "select":
            {
                var combo = new ComboBox
                {
                    ItemsSource = field.Options ?? new List<SettingsFieldOption>(),
                    DisplayMemberPath = nameof(SettingsFieldOption.Label),
                    SelectedValuePath = nameof(SettingsFieldOption.Value),
                    SelectedValue = current?.DisplayString,
                };
                _readers[field.Name] = () => combo.SelectedValue is string value
                    ? SettingsValue.FromString(value)
                    : current;
                _writers[field.Name] = value => combo.SelectedValue = value.DisplayString;
                panel.Children.Add(combo);
                break;
            }

            case "password":
            {
                // Secrets arrive masked. A plain TextBox holding the mask is deliberate: the mask
                // is not a password to hide, it is a marker meaning "unchanged", and showing it as
                // dots would make it impossible to tell apart from a real value being typed.
                var box = new TextBox { Text = current?.DisplayString ?? "" };
                _readers[field.Name] = () => SettingsValue.FromString(box.Text);
                _writers[field.Name] = value => box.Text = value.DisplayString;
                panel.Children.Add(box);
                if (current?.DisplayString == SentinelSecrets.Mask)
                {
                    panel.Children.Add(Hint("The dots mean the server is keeping the value it already has. " +
                                            "Leave them alone unless you are changing it."));
                }
                break;
            }

            case "number":
            {
                var box = new TextBox { Text = current?.DisplayString ?? "" };
                _readers[field.Name] = () =>
                {
                    var text = box.Text.Trim();
                    if (text.Length == 0) return null;
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? SettingsValue.FromNumber(number)
                        : null;
                };
                _writers[field.Name] = value => box.Text = value.DisplayString;
                panel.Children.Add(box);

                var bounds = new List<string>();
                if (field.Min is { } min) bounds.Add($"at least {min}");
                if (field.Max is { } max) bounds.Add($"at most {max}");
                if (field.Integer == true) bounds.Add("whole numbers only");
                if (field.Recommended is { } recommended) bounds.Add($"recommended {recommended}");
                if (bounds.Count > 0) panel.Children.Add(Hint(string.Join(" · ", bounds)));
                break;
            }

            case "csv":
            {
                var box = new TextBox { Text = current?.DisplayString ?? "" };
                _readers[field.Name] = () => SettingsValue.FromArray(
                    box.Text
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(SettingsValue.FromString)
                        .ToList());
                _writers[field.Name] = value => box.Text = value.DisplayString;
                panel.Children.Add(box);
                panel.Children.Add(Hint("Separate entries with commas."));
                break;
            }

            case "text":
            {
                var box = new TextBox { Text = current?.DisplayString ?? "" };
                _readers[field.Name] = () => SettingsValue.FromString(box.Text.Trim());
                _writers[field.Name] = value => box.Text = value.DisplayString;
                panel.Children.Add(box);

                // A field that can look its own values up gets a button for it. The endpoint and the
                // meaning of the results both come from the schema, so nothing here is
                // location-specific or Ollama-specific.
                if (field.Lookup is { } lookup) panel.Children.Add(BuildLookupButton(field, lookup, box));
                if (field.OptionsSource is { } source) panel.Children.Add(BuildOptionsButton(source, box));
                break;
            }

            default:
            {
                // Anything unrecognised is shown read-only rather than guessed at - better than
                // writing back something malformed.
                var box = new TextBox { Text = current?.DisplayString ?? "", IsReadOnly = true };
                panel.Children.Add(box);
                panel.Children.Add(Hint($"This app doesn't know how to edit a \"{field.Type}\" setting, " +
                                        "so it is shown as the server sent it. Edit it in the web dashboard."));
                break;
            }
        }

        if (field.Tip is { Length: > 0 }) panel.Children.Add(Hint(field.Tip));

        if (field.RequiresRestart)
        {
            var restart = Hint("Takes effect after a restart.");
            restart.Foreground = Palette.Amber;
            panel.Children.Add(restart);
        }

        return panel;
    }

    private UIElement BuildLookupButton(SettingsFieldSchema field, SettingsLookupSchema lookup, TextBox box)
    {
        var button = new Button
        {
            Content = lookup.Label,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = lookup.Tip,
        };
        button.Click += async (_, _) =>
        {
            var query = box.Text.Trim();
            if (query.Length == 0)
            {
                Message($"Type something into {field.Label} first, then press {lookup.Label}.");
                return;
            }

            button.IsEnabled = false;
            try
            {
                var results = await _model.Client.SettingsLookupAsync(lookup, query);
                if (results.Count == 0)
                {
                    Message("Nothing was found for that.");
                    return;
                }

                var picker = new ListPickerWindow(lookup.Label, results.Select(r => r.Label).ToList()) { Owner = this };
                if (picker.ShowDialog() != true) return;

                // The keys are field names in the same section; the ones this app recognises are
                // applied and the rest ignored, so a newer server can't break an older app.
                foreach (var (name, value) in results[picker.SelectedIndex].Values)
                {
                    if (_readers.ContainsKey(name)) ApplyValue(name, value);
                }
                Message($"Filled in from \"{results[picker.SelectedIndex].Label}\".");
            }
            catch (Exception error)
            {
                Message(AppModel.UserFacingMessage(error));
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private UIElement BuildOptionsButton(SettingsOptionsSource source, TextBox box)
    {
        var button = new Button
        {
            Content = source.Label,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = source.Tip,
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                var options = await _model.Client.SettingsOptionsAsync(source);
                if (options.Count == 0)
                {
                    // The server's own wording, because it is the one message that names what to go
                    // and do, and this app shouldn't know that.
                    Message(source.EmptyMessage);
                    return;
                }

                var picker = new ListPickerWindow(source.Label, options.Select(option => option.Label).ToList())
                {
                    Owner = this,
                };
                if (picker.ShowDialog() != true) return;
                box.Text = options[picker.SelectedIndex].Value;
            }
            catch (Exception error)
            {
                Message(AppModel.UserFacingMessage(error));
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    /// <summary>
    /// Writes a value into the control that owns a field, for a lookup that fills several fields at
    /// once. Rebuilding the whole section instead would throw away everything else half-typed.
    /// </summary>
    private void ApplyValue(string name, SettingsValue value)
    {
        if (_writers.TryGetValue(name, out var write)) write(value);
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Palette.SecondaryText,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 4, 0, 0),
    };

    private async Task SaveAsync(SettingsSectionSchema section, Button save)
    {
        var values = new Dictionary<string, SettingsValue>();
        foreach (var field in section.Fields)
        {
            if (!_readers.TryGetValue(field.Name, out var read)) continue;
            var value = read();
            if (value is null)
            {
                Message($"\"{field.Label}\" isn't a number this server will accept.");
                return;
            }

            if (field.Type == "number" && value.Kind == SettingsValue.ValueKind.Number)
            {
                var number = value.NumberValue;
                if (field.Integer == true && Math.Abs(number - Math.Round(number)) > double.Epsilon)
                {
                    Message($"\"{field.Label}\" has to be a whole number.");
                    return;
                }
                if (field.Min is { } min && number < min)
                {
                    Message($"\"{field.Label}\" has to be at least {min}.");
                    return;
                }
                if (field.Max is { } max && number > max)
                {
                    Message($"\"{field.Label}\" has to be at most {max}.");
                    return;
                }
            }

            // Dotted names write into the nested object they describe ("apple.teamId"), the same
            // convention the dashboard uses.
            SettingsValue.Write(values, field.Name, value);
        }

        save.IsEnabled = false;
        try
        {
            var result = await _model.Client.SaveSettingsSectionAsync(section.Endpoint, values);
            Message(result.RestartRequired == true
                ? $"Saved. The {section.RestartTarget} has to restart before this takes effect."
                : "Saved.");

            // Re-read, so masked secrets and anything the server normalised come back as it now
            // holds them rather than as they were typed.
            _snapshot = await _model.Client.SettingsSnapshotAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            save.IsEnabled = true;
        }
    }

    private async Task RunActionAsync(SettingsActionSchema action, Button button)
    {
        var confirmed = MessageBox.Show(
            this,
            $"{action.Label}?\n\n{action.Tip}",
            action.Label,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;
        if (!confirmed) return;

        button.IsEnabled = false;
        try
        {
            var detail = await _model.Client.RunSettingsActionAsync(action);
            Message(detail ?? $"{action.Label}: done.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
