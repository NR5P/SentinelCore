using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

/// <summary>
/// The torrent sites the server searches when anyone adds a movie or show: turn them on and off,
/// remove them, and add more from Prowlarr's catalog - with one press for a public site, or
/// through a small form for one that needs a login or choices. Admin only (status.canManageSites).
/// </summary>
/// <remarks>
/// The contract is core/docs/MOVIE_DOWNLOADS.md ("Sites"). Prowlarr lives inside the VPN, so these
/// routes start it; when it is not up within ~25 s the server answers 503 with starting: true, and
/// this window shows that sentence and asks again every three seconds until it answers or is closed.
/// </remarks>
public partial class MediaDownloadSitesWindow : Window
{
    private static readonly TimeSpan StartingRetryInterval = TimeSpan.FromSeconds(3);

    private sealed record FieldInput(MediaDownloadSiteField Field, Func<object?> Read);

    private readonly AppModel _model;
    private readonly CancellationTokenSource _closing = new();
    private List<MediaDownloadSiteDefinition>? _catalog;
    private MediaDownloadSiteDefinition? _adding;
    private readonly List<FieldInput> _fieldInputs = new();

    // Each request that is waiting out a 503 bumps this; the busy bar belongs to whichever is newest.
    private int _busyOwner;

    public MediaDownloadSitesWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        // Cancelled, never disposed: a request still in flight reads its token after the close.
        Closed += (_, _) => _closing.Cancel();
        Loaded += async (_, _) => await LoadSitesAsync();
    }

    private bool IsClosing => _closing.IsCancellationRequested;

    // ---------------------------------------------------------------- Waiting for the VPN

    /// <summary>
    /// Runs a sites request, and while the server says the VPN is still starting, shows its
    /// sentence and asks again every three seconds. Throws OperationCanceledException once the
    /// window closes.
    /// </summary>
    private async Task<T> WhileStartingAsync<T>(Func<CancellationToken, Task<T>> request)
    {
        var owner = ++_busyOwner;
        try
        {
            while (true)
            {
                var token = _closing.Token;
                try
                {
                    return await request(token);
                }
                catch (SentinelServerException error) when (error.Status == 503 && error.Starting)
                {
                    if (owner == _busyOwner) ShowBusy(error.Message);
                    await Task.Delay(StartingRetryInterval, token);
                }
            }
        }
        finally
        {
            if (owner == _busyOwner && !IsClosing) ShowBusy(null);
        }
    }

    private Task WhileStartingAsync(Func<CancellationToken, Task> request) =>
        WhileStartingAsync<bool>(async token =>
        {
            await request(token);
            return true;
        });

    private void ShowBusy(string? message)
    {
        BusyText.Text = message ?? "";
        BusyBar.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- Views

    private void ShowView(FrameworkElement panel)
    {
        ListPanel.Visibility = panel == ListPanel ? Visibility.Visible : Visibility.Collapsed;
        CatalogPanel.Visibility = panel == CatalogPanel ? Visibility.Visible : Visibility.Collapsed;
        FormPanel.Visibility = panel == FormPanel ? Visibility.Visible : Visibility.Collapsed;
        AddSiteButton.Visibility = panel == ListPanel ? Visibility.Visible : Visibility.Collapsed;
        HeadingText.Text = panel == ListPanel ? "Torrent sites" : panel == CatalogPanel ? "Add a site" : _adding?.Name ?? "Set up a site";
    }

    private void OnShowListClicked(object sender, RoutedEventArgs e)
    {
        ShowView(ListPanel);
        _ = LoadSitesAsync();
    }

    private void OnShowCatalogClicked(object sender, RoutedEventArgs e) => ShowView(CatalogPanel);

    // ---------------------------------------------------------------- Your sites

    private async Task LoadSitesAsync()
    {
        ShowView(ListPanel);
        AddSiteButton.IsEnabled = false;
        ListEmpty.Visibility = Visibility.Collapsed;
        ShowBusy("Loading the sites…");
        try
        {
            var sites = await WhileStartingAsync(token => _model.Client.MediaDownloadSitesAsync(token));
            SitesHost.Children.Clear();
            foreach (var site in sites) SitesHost.Children.Add(SiteRow(site));
            ListEmpty.Visibility = sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AddSiteButton.IsEnabled = true;
        }
        catch (Exception) when (IsClosing)
        {
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error), isError: true);
        }
    }

    private FrameworkElement SiteRow(MediaDownloadSite site)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(TitleLine(site.Name, site.Privacy, site.Language));
        if (!string.IsNullOrWhiteSpace(site.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = site.Description,
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        var toggle = new CheckBox
        {
            Content = "On",
            IsChecked = site.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = site.Id is not null,
            ToolTip = "Whether this site is searched",
        };
        toggle.Click += async (_, _) => await ToggleAsync(site, toggle);

        var remove = new Button
        {
            Content = "Remove",
            Style = (Style)FindResource("DangerButton"),
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = site.Id is not null,
        };
        remove.Click += async (_, _) => await RemoveAsync(site, remove);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        return Card(grid);
    }

    private async Task ToggleAsync(MediaDownloadSite site, CheckBox toggle)
    {
        if (site.Id is not { } id) return;
        var enabled = toggle.IsChecked == true;
        toggle.IsEnabled = false;
        try
        {
            await WhileStartingAsync(token => _model.Client.SetMediaDownloadSiteEnabledAsync(id, enabled, token));
            Message(enabled ? $"{site.Name} is searched again." : $"{site.Name} is no longer searched.");
        }
        catch (Exception) when (IsClosing)
        {
            return;
        }
        catch (Exception error)
        {
            toggle.IsChecked = !enabled;
            Message(AppModel.UserFacingMessage(error), isError: true);
        }
        toggle.IsEnabled = true;
    }

    private async Task RemoveAsync(MediaDownloadSite site, Button button)
    {
        if (site.Id is not { } id) return;
        var confirmed = MessageBox.Show(
            this,
            $"Remove {site.Name}? It is no longer searched.",
            "Remove site",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        button.IsEnabled = false;
        try
        {
            await WhileStartingAsync(token => _model.Client.DeleteMediaDownloadSiteAsync(id, token));
            Message($"Removed {site.Name}.");
        }
        catch (Exception) when (IsClosing)
        {
            return;
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error), isError: true);
        }
        await LoadSitesAsync();
    }

    // ---------------------------------------------------------------- The catalog

    private void OnAddSiteClicked(object sender, RoutedEventArgs e)
    {
        ShowView(CatalogPanel);
        CatalogQueryBox.Focus();
        if (_catalog is null) _ = LoadCatalogAsync();
    }

    private void OnCatalogQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = LoadCatalogAsync();
    }

    private void OnCatalogFindClicked(object sender, RoutedEventArgs e) => _ = LoadCatalogAsync();

    private async Task LoadCatalogAsync()
    {
        var query = CatalogQueryBox.Text.Trim();
        CatalogHost.Children.Clear();
        CatalogStatusText(Palette.SecondaryText, "Loading the site list…");
        CatalogFindButton.IsEnabled = false;
        try
        {
            var catalog = await WhileStartingAsync(token => _model.Client.MediaDownloadSiteCatalogAsync(query, token));
            if (CatalogQueryBox.Text.Trim() != query) return; // a newer search is on its way
            _catalog = catalog;
            CatalogScroller.ScrollToTop();
            if (catalog.Count == 0)
            {
                CatalogStatusText(Palette.SecondaryText, "No site by that name.");
                return;
            }
            CatalogStatusText(Palette.SecondaryText, "");
            foreach (var definition in catalog) CatalogHost.Children.Add(CatalogRow(definition));
        }
        catch (Exception) when (IsClosing)
        {
        }
        catch (Exception error)
        {
            CatalogStatusText(Palette.Red, AppModel.UserFacingMessage(error));
        }
        finally
        {
            if (!IsClosing) CatalogFindButton.IsEnabled = true;
        }
    }

    private void CatalogStatusText(Brush brush, string text)
    {
        CatalogStatus.Foreground = brush;
        CatalogStatus.Text = text;
        CatalogStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement CatalogRow(MediaDownloadSiteDefinition definition)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(TitleLine(definition.Name, definition.Privacy, definition.Language));
        if (!string.IsNullOrWhiteSpace(definition.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = definition.Description,
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        var pick = new Button
        {
            Content = definition.RequiresSetup ? "Set up…" : "Add",
            MinWidth = 80,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !string.IsNullOrEmpty(definition.Definition),
        };
        pick.Click += async (_, _) =>
        {
            if (definition.RequiresSetup) OpenForm(definition);
            else await AddSiteAsync(definition, new Dictionary<string, object?>(), pick);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(pick, 1);
        grid.Children.Add(pick);
        return Card(grid);
    }

    // ---------------------------------------------------------------- Setting a site up

    private void OpenForm(MediaDownloadSiteDefinition definition)
    {
        _adding = definition;
        _fieldInputs.Clear();
        FieldsHost.Children.Clear();
        FormError.Text = "";
        FormName.Text = definition.Name ?? "";
        FormDescription.Text = definition.Description ?? "";
        FormDescription.Visibility = string.IsNullOrWhiteSpace(definition.Description) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var field in definition.Fields)
        {
            if (string.IsNullOrEmpty(field.Name)) continue;
            FieldsHost.Children.Add(FieldControl(field));
            if (!string.IsNullOrWhiteSpace(field.Help))
            {
                FieldsHost.Children.Add(new TextBlock
                {
                    Text = field.Help,
                    Style = (Style)FindResource("Caption"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 4, 0, 0),
                });
            }
        }
        ShowView(FormPanel);
    }

    // One control per field, by type; each registers how to read its value back.
    private FrameworkElement FieldControl(MediaDownloadSiteField field)
    {
        var panel = new StackPanel();
        switch (field.Type)
        {
            case "checkbox":
            {
                var box = new CheckBox { Content = field.DisplayLabel, IsChecked = field.ValueIsTrue, Margin = new Thickness(0, 14, 0, 0) };
                panel.Children.Add(box);
                _fieldInputs.Add(new FieldInput(field, () => MediaDownloadText.SiteFieldValue("checkbox", null, box.IsChecked == true)));
                return panel;
            }
            case "password":
            {
                panel.Children.Add(Label(field));
                var box = new PasswordBox();
                if (field.ValueText.Length > 0) box.Password = field.ValueText;
                panel.Children.Add(box);
                _fieldInputs.Add(new FieldInput(field, () => MediaDownloadText.SiteFieldValue("password", box.Password)));
                return panel;
            }
            case "select":
            {
                panel.Children.Add(Label(field));
                var combo = new ComboBox { ItemsSource = field.Options, HorizontalAlignment = HorizontalAlignment.Stretch };
                var current = field.ValueText;
                combo.SelectedItem = field.Options.FirstOrDefault(option => option.ValueText == current) ?? field.Options.FirstOrDefault();
                panel.Children.Add(combo);
                _fieldInputs.Add(new FieldInput(field, () =>
                    MediaDownloadText.SiteFieldValue("select", (combo.SelectedItem as MediaDownloadSiteOption)?.ValueText ?? current)));
                return panel;
            }
            case "number":
            {
                panel.Children.Add(Label(field));
                var box = new TextBox { Text = field.ValueText, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
                panel.Children.Add(box);
                _fieldInputs.Add(new FieldInput(field, () => MediaDownloadText.SiteFieldValue("number", box.Text)));
                return panel;
            }
            default:
            {
                panel.Children.Add(Label(field));
                var box = new TextBox { Text = field.ValueText };
                panel.Children.Add(box);
                _fieldInputs.Add(new FieldInput(field, () => MediaDownloadText.SiteFieldValue("text", box.Text)));
                return panel;
            }
        }
    }

    private TextBlock Label(MediaDownloadSiteField field) => new()
    {
        Text = field.DisplayLabel,
        Style = (Style)FindResource("FieldLabel"),
    };

    private async void OnSaveSiteClicked(object sender, RoutedEventArgs e)
    {
        if (_adding is not { } definition) return;
        var fields = new Dictionary<string, object?>();
        foreach (var input in _fieldInputs) fields[input.Field.Name!] = input.Read();
        await AddSiteAsync(definition, fields, SaveSiteButton);
    }

    private async Task AddSiteAsync(MediaDownloadSiteDefinition definition, Dictionary<string, object?> fields, Button button)
    {
        if (string.IsNullOrEmpty(definition.Definition)) return;
        var inForm = FormPanel.Visibility == Visibility.Visible;
        button.IsEnabled = false;
        FormError.Text = "";
        Message(inForm ? "" : $"Adding {definition.Name}… Prowlarr tests the site first.");
        try
        {
            await WhileStartingAsync(token => _model.Client.AddMediaDownloadSiteAsync(
                new MediaDownloadSiteInput { Definition = definition.Definition!, Fields = fields }, token));
        }
        catch (Exception) when (IsClosing)
        {
            return;
        }
        catch (Exception error)
        {
            button.IsEnabled = true;
            var message = AppModel.UserFacingMessage(error);
            if (inForm) FormError.Text = message;
            else Message(message, isError: true);
            return;
        }

        button.IsEnabled = true;
        _adding = null;
        Message($"{definition.Name} added.");
        await LoadSitesAsync();
    }

    // ---------------------------------------------------------------- Pieces

    private static FrameworkElement TitleLine(string? name, string? privacy, string? language)
    {
        var line = new WrapPanel();
        line.Children.Add(new TextBlock
        {
            Text = name ?? "",
            Foreground = Palette.PrimaryText,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(privacy)) line.Children.Add(Chip(privacy!, privacy == "public" ? Palette.Green : Palette.Cyan));
        if (!string.IsNullOrWhiteSpace(language)) line.Children.Add(Chip(language!, Palette.SecondaryText));
        return line;
    }

    private static Border Chip(string text, Brush foreground) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x26, 0x2C)),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(0, 2, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = foreground, FontSize = 11 },
    };

    private Border Card(UIElement child) => new()
    {
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 0, 0, 6),
        CornerRadius = new CornerRadius(6),
        Background = (Brush)FindResource("CardBackground"),
        BorderBrush = (Brush)FindResource("CardBorder"),
        BorderThickness = new Thickness(1),
        Child = child,
    };

    private void Message(string message, bool isError = false)
    {
        MessageText.Text = message;
        MessageText.Foreground = isError ? Palette.Red : Palette.SecondaryText;
        MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
