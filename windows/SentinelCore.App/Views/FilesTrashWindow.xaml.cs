using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// What was taken out of the library and can still be put back: one batch per deletion or
/// duplicate sweep, each file inside it restorable on its own. Emptying it for good is admin-only.
/// </summary>
public partial class FilesTrashWindow : Window
{
    private readonly AppModel _model;
    private readonly bool _admin;

    public FilesTrashWindow(AppModel model, bool isAdmin)
    {
        _model = model;
        _admin = isAdmin;
        InitializeComponent();

        EmptyButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var batches = await _model.Client.FamilyFilesTrashAsync();
            BatchHost.Children.Clear();
            foreach (var batch in batches.OrderByDescending(batch => batch.DeletedUtc))
            {
                BatchHost.Children.Add(BatchCard(batch));
            }

            var files = batches.Sum(batch => batch.Files.Count);
            var bytes = batches.Sum(batch => batch.Bytes);
            SummaryText.Text = files == 0
                ? "Files that were removed from the library, or found to be duplicates. Anything here can be put back."
                : $"{files:N0} file{(files == 1 ? "" : "s")} · {ByteSizeConverter.FormatBytes(bytes)} · anything here can be put back.";
            EmptyState.Visibility = batches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyButton.IsEnabled = _admin && batches.Count > 0;
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private Border BatchCard(FamilyTrashBatch batch)
    {
        var body = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = BatchTitle(batch),
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
        });
        titles.Children.Add(new TextBlock
        {
            Text = BatchDetail(batch),
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(titles, 0);
        header.Children.Add(titles);

        var restoreAll = new Button
        {
            Content = batch.Files.Count == 1 ? "Put back" : "Put all back",
            Padding = new Thickness(9, 4, 9, 4),
            VerticalAlignment = VerticalAlignment.Top,
        };
        restoreAll.Click += async (_, _) => await RestoreAsync(batch.Id, null);
        Grid.SetColumn(restoreAll, 1);
        header.Children.Add(restoreAll);
        body.Children.Add(header);

        var files = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var file in batch.Files) files.Children.Add(FileRow(batch, file));
        body.Children.Add(files);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("CardBackground"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = body,
        };
    }

    private Grid FileRow(FamilyTrashBatch batch, FamilyTrashFile file)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var names = new StackPanel();
        names.Children.Add(new TextBlock
        {
            Text = file.Path,
            Foreground = Palette.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = file.Path,
        });
        if (file.KeptCopy is { Length: > 0 } kept)
        {
            names.Children.Add(new TextBlock
            {
                Text = $"Same as {kept}, which stayed",
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        Grid.SetColumn(names, 0);
        row.Children.Add(names);

        var size = new TextBlock
        {
            Text = ByteSizeConverter.FormatBytes(file.Size),
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(size, 1);
        row.Children.Add(size);

        if (batch.Files.Count > 1)
        {
            var restore = new Button
            {
                Content = "Put back",
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            restore.Click += async (_, _) => await RestoreAsync(batch.Id, file.Path);
            Grid.SetColumn(restore, 2);
            row.Children.Add(restore);
        }

        return row;
    }

    internal static string BatchTitle(FamilyTrashBatch batch)
    {
        var count = $"{batch.Files.Count:N0} file{(batch.Files.Count == 1 ? "" : "s")}";
        return batch.Reason == "duplicate" ? $"{count} removed as duplicates" : $"{count} deleted";
    }

    private static string BatchDetail(FamilyTrashBatch batch)
    {
        var when = RelativeTime.Describe(batch.DeletedUtc);
        var by = string.IsNullOrWhiteSpace(batch.By) ? "" : $" by {batch.By}";
        return $"{when}{by} · {ByteSizeConverter.FormatBytes(batch.Bytes)}";
    }

    private async Task RestoreAsync(string batchId, string? path)
    {
        try
        {
            var result = await _model.Client.FamilyFilesRestoreTrashAsync(batchId, path);
            Message(result.Detail);
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnEmptyClicked(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            this,
            "Empty the trash?\n\nEverything in it is deleted for good. Nothing here can be put back afterwards.",
            "Empty trash",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        EmptyButton.IsEnabled = false;
        try
        {
            var result = await _model.Client.FamilyFilesEmptyTrashAsync();
            Message(result.Detail);
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
            EmptyButton.IsEnabled = true;
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
