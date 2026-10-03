using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// Makes or edits a playlist: name, description, shared, collaborative - and for a smart playlist
/// its rules (§3), edited from <see cref="SmartRuleCatalog"/> so only combinations the server
/// accepts can be built.
/// </summary>
public sealed class PlaylistEditorWindow : AudioDialog
{
    private readonly MusicPlaylist? _existing;
    private readonly bool _smart;
    private readonly TextBox _name = new();
    private readonly TextBox _description = new() { AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _shared = new() { Content = "Shared - everyone in the household can see and play it" };
    private readonly CheckBox _collaborative = new() { Content = "Collaborative - everyone can also add, remove and reorder songs" };
    private readonly SmartRules _rules;
    private readonly StackPanel _ruleHost = new();
    private readonly ComboBox _match = new() { Width = 90, ItemsSource = new[] { "all", "any" } };
    private readonly ComboBox _sort = new() { Width = 150, ItemsSource = SmartRuleCatalog.Sorts };
    private readonly ComboBox _order = new() { Width = 120, ItemsSource = new[] { "ascending", "descending" } };
    private readonly TextBox _limit = new() { Width = 80 };
    private List<MusicLibrary> _libraries = new();

    public PlaylistEditorWindow(AppModel model, MusicPlaylist? existing, bool smart)
        : base(model, existing is null ? smart ? "New smart playlist" : "New playlist" : $"Edit {existing.Name}",
            smart ? "A smart playlist fills itself with every song that matches its rules, worked out fresh each time it is opened." : null,
            smart ? 720 : 560, smart ? 640 : 420)
    {
        _existing = existing;
        _smart = smart;
        _rules = Clone(existing?.Rules) ?? new SmartRules { Rules = new() { SmartRuleCatalog.NewRule("genre") } };

        _name.Text = existing?.Name ?? "";
        _description.Text = existing?.Description ?? "";
        _shared.IsChecked = existing?.Shared ?? false;
        _collaborative.IsChecked = existing?.Collaborative ?? false;
        _collaborative.Margin = new Thickness(0, 6, 0, 0);
        _shared.Margin = new Thickness(0, 12, 0, 0);
        _collaborative.Checked += (_, _) => _shared.IsChecked = true;

        Body.Children.Add(Label("Name"));
        Body.Children.Add(_name);
        Body.Children.Add(Label("Description"));
        Body.Children.Add(_description);
        Body.Children.Add(_shared);
        if (!smart) Body.Children.Add(_collaborative);

        if (smart)
        {
            Body.Children.Add(AudioUi.Heading("Rules"));
            _match.SelectedItem = _rules.Match;
            _match.SelectionChanged += (_, _) => _rules.Match = _match.SelectedItem as string ?? "all";
            Body.Children.Add(AudioUi.Row(new TextBlock { Text = "Match", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                _match, new TextBlock { Text = "of these rules", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }));
            Body.Children.Add(_ruleHost);
            var add = AudioUi.TextButton("+ Add rule", () =>
            {
                _rules.Rules.Add(SmartRuleCatalog.NewRule("genre"));
                DrawRules();
            });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            Body.Children.Add(add);

            _sort.SelectedItem = SmartRuleCatalog.Sorts.FirstOrDefault(sort => sort.Value == _rules.Sort) ?? SmartRuleCatalog.Sorts[0];
            _order.SelectedIndex = _rules.Order == "desc" ? 1 : 0;
            _limit.Text = _rules.Limit.ToString();
            Body.Children.Add(AudioUi.Heading("Order and size"));
            Body.Children.Add(AudioUi.Row(new TextBlock { Text = "Sort by", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                _sort, new Border { Width = 8 }, _order, new TextBlock { Text = "Limit", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) }, _limit,
                new TextBlock { Text = "songs (0 = no limit)", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }));
            Loaded += async (_, _) =>
            {
                try
                {
                    _libraries = await Model.Client.MusicLibrariesAsync();
                }
                catch (Exception)
                {
                    // The Library field still takes an id typed by hand.
                }
                DrawRules();
            };
            DrawRules();
        }

        AddFooterButton("Cancel", Close, isCancel: true);
        AddFooterButton(existing is null ? "Create" : "Save", () => _ = SaveAsync(), primary: true);
        Loaded += (_, _) => _name.Focus();
    }

    public MusicPlaylist? Saved { get; private set; }

    private static SmartRules? Clone(SmartRules? rules) =>
        rules is null ? null : JsonSerializer.Deserialize<SmartRules>(JsonSerializer.Serialize(rules));

    private void DrawRules()
    {
        _ruleHost.Children.Clear();
        for (var i = 0; i < _rules.Rules.Count; i++) _ruleHost.Children.Add(RuleRow(_rules.Rules[i]));
        if (_rules.Rules.Count == 0) _ruleHost.Children.Add(AudioUi.Caption("No rules yet - add one."));
    }

    private FrameworkElement RuleRow(SmartRule rule)
    {
        var field = SmartRuleCatalog.Field(rule.Field) ?? SmartRuleCatalog.Fields[3];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        var fieldPicker = new ComboBox { Width = 150, ItemsSource = SmartRuleCatalog.Fields.Select(f => f.Label).ToList(), SelectedItem = field.Label };
        fieldPicker.SelectionChanged += (_, _) =>
        {
            var chosen = SmartRuleCatalog.Fields[Math.Max(0, fieldPicker.SelectedIndex)];
            if (chosen.Name == rule.Field) return;
            var fresh = SmartRuleCatalog.NewRule(chosen.Name);
            rule.Field = fresh.Field;
            rule.Op = fresh.Op;
            rule.Value = fresh.Value;
            Dispatcher.BeginInvoke(DrawRules);
        };
        row.Children.Add(fieldPicker);

        var ops = field.Ops.Select(op => new SmartOption(op, SmartRuleCatalog.OpLabel(op, field.Kind))).ToList();
        var opPicker = new ComboBox { Width = 170, ItemsSource = ops, Margin = new Thickness(8, 0, 8, 0) };
        opPicker.SelectedItem = ops.FirstOrDefault(op => op.Value == rule.Op) ?? ops[0];
        opPicker.SelectionChanged += (_, _) => rule.Op = (opPicker.SelectedItem as SmartOption)?.Value ?? field.Ops[0];
        if (field.Kind == SmartValueKind.Bool) opPicker.Visibility = Visibility.Collapsed;
        row.Children.Add(opPicker);

        switch (field.Kind)
        {
            case SmartValueKind.Bool:
            {
                var picker = new ComboBox { Width = 140, ItemsSource = new[] { "Loved", "Not loved" }, Margin = new Thickness(8, 0, 0, 0) };
                picker.SelectedIndex = rule.Value.ValueKind == JsonValueKind.False ? 1 : 0;
                picker.SelectionChanged += (_, _) => rule.Value = JsonSerializer.SerializeToElement(picker.SelectedIndex == 0);
                row.Children.Add(picker);
                break;
            }
            case SmartValueKind.Codec:
            {
                var picker = new ComboBox { Width = 140, ItemsSource = SmartRuleCatalog.Codecs, IsEditable = true, Text = SmartRuleCatalog.ValueText(rule.Value) };
                picker.SelectionChanged += (_, _) => rule.Value = JsonSerializer.SerializeToElement(picker.SelectedItem as string ?? "");
                picker.LostFocus += (_, _) => rule.Value = JsonSerializer.SerializeToElement(picker.Text.Trim());
                row.Children.Add(picker);
                break;
            }
            case SmartValueKind.Library:
            {
                var options = _libraries.Select(library => new SmartOption(library.Id, library.Name)).ToList();
                var picker = new ComboBox { Width = 180, ItemsSource = options };
                picker.SelectedItem = options.FirstOrDefault(option => option.Value == SmartRuleCatalog.ValueText(rule.Value));
                picker.SelectionChanged += (_, _) => rule.Value = JsonSerializer.SerializeToElement((picker.SelectedItem as SmartOption)?.Value ?? "");
                row.Children.Add(picker);
                break;
            }
            default:
            {
                var box = new TextBox { Width = 180, Text = SmartRuleCatalog.ValueText(rule.Value), ToolTip = $"Needs {SmartRuleCatalog.ValueHint(field.Kind)}" };
                box.TextChanged += (_, _) =>
                {
                    var parsed = SmartRuleCatalog.ParseValue(field.Kind, box.Text);
                    rule.Value = parsed ?? JsonSerializer.SerializeToElement(box.Text);
                };
                row.Children.Add(box);
                if (field.Kind == SmartValueKind.Days) row.Children.Add(new TextBlock { Text = "days", Foreground = Palette.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
                break;
            }
        }

        var remove = AudioUi.GlyphButton("", "Remove this rule", () =>
        {
            _rules.Rules.Remove(rule);
            DrawRules();
        }, 11);
        remove.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(remove);
        return row;
    }

    private async Task SaveAsync()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            Message("Give the playlist a name.", error: true);
            _name.Focus();
            return;
        }

        SmartRules? rules = null;
        if (_smart)
        {
            _rules.Sort = (_sort.SelectedItem as SmartOption)?.Value ?? "random";
            _rules.Order = _order.SelectedIndex == 1 ? "desc" : "asc";
            _rules.Match = _match.SelectedItem as string ?? "all";
            if (!int.TryParse(_limit.Text.Trim(), out var limit))
            {
                Message("The limit has to be a whole number (0 for no limit).", error: true);
                return;
            }
            _rules.Limit = limit;
            var problems = SmartRuleCatalog.Validate(_rules);
            if (problems.Count > 0)
            {
                Message(string.Join("\n", problems), error: true);
                return;
            }
            rules = _rules;
        }

        var input = new MusicPlaylistInput
        {
            Name = name,
            Description = _description.Text.Trim(),
            Shared = _shared.IsChecked == true || _collaborative.IsChecked == true,
            Collaborative = !_smart && _collaborative.IsChecked == true,
            Rules = rules,
        };
        try
        {
            Saved = _existing is null
                ? await Model.Client.CreateMusicPlaylistAsync(input)
                : await Model.Client.UpdateMusicPlaylistAsync(_existing.Id, input);
            Done();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}
