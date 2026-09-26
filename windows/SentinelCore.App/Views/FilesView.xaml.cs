using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SentinelCore.App.Design;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views;

/// <summary>
/// The household's shared file library: one folder at a time, with the backup drives and the trash
/// a button away. A port of the Files tab on the other five surfaces.
/// </summary>
/// <remarks>
/// Files open by being fetched into this PC's temp folder and handed to the shell, the way a
/// browser download does - WPF has no viewer for a PDF or a video and should not pretend to. A
/// double-click on a file that was opened a moment ago reuses the copy already here.
///
/// The server does its heavy lifting (backups, restores, duplicate checks) in the background and
/// reports one job at a time; while one is running the banner polls every two seconds, which is
/// what the API contract asks of every client.
/// </remarks>
public partial class FilesView : UserControl
{
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromSeconds(2);

    private readonly AppModel _model;
    private readonly DispatcherTimer _jobPoll;

    private FamilyFilesStatus? _status;
    private string _path = "";

    // Non-null while the list shows search results rather than a folder.
    private string? _search;

    private bool _isActive;
    private bool _loading;
    private CancellationTokenSource? _transfer;

    public FilesView(AppModel model)
    {
        _model = model;
        InitializeComponent();

        _jobPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = JobPollInterval };
        _jobPoll.Tick += async (_, _) => await PollJobAsync();

        IsVisibleChanged += (_, _) => SetActive(IsVisible);
        ShowCrumbs();
    }

    public void SetActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;
        if (active)
        {
            _ = ReloadAsync();
        }
        else
        {
            _jobPoll.Stop();
        }
    }

    // ---------------------------------------------------------------- Loading

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            await LoadStatusAsync();
            await LoadListingAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            _status = await _model.Client.FamilyFilesStatusAsync();
            ApplyStatus();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    private void ApplyStatus()
    {
        if (_status is not { Enabled: true } status)
        {
            SummaryText.Text = "Family Files is turned off in Settings.";
            return;
        }

        var parts = new List<string>();
        if (status.Library is { } library)
        {
            parts.Add($"{library.Files:N0} file{(library.Files == 1 ? "" : "s")} · {ByteSizeConverter.FormatBytes(library.Bytes)}");
        }
        parts.Add(status.Drives.Count switch
        {
            0 => "no backup drives yet",
            1 => "1 backup drive",
            var count => $"{count} backup drives",
        });
        if (status.Trash is { Files: > 0 } trash) parts.Add($"{trash.Files} in the trash");
        SummaryText.Text = string.Join(" · ", parts);

        ApplyJob(status.Job);
    }

    /// <summary>
    /// Shows or hides the banner for the server's background job, and keeps the poll running for
    /// exactly as long as there is one.
    /// </summary>
    private void ApplyJob(FamilyFilesJob? job)
    {
        if (job is null)
        {
            JobBanner.Visibility = Visibility.Collapsed;
            _jobPoll.Stop();
            return;
        }

        JobText.Text = string.IsNullOrWhiteSpace(job.Message) ? $"{job.Title}…" : $"{job.Title} · {job.Message}";
        if (job.Total > 0)
        {
            JobProgress.IsIndeterminate = false;
            JobProgress.Value = Math.Clamp((double)job.Done / job.Total, 0, 1);
        }
        else
        {
            JobProgress.IsIndeterminate = true;
        }
        JobBanner.Visibility = Visibility.Visible;
        if (_isActive && !_jobPoll.IsEnabled) _jobPoll.Start();
    }

    private async Task PollJobAsync()
    {
        if (!_isActive) return;
        try
        {
            var job = await _model.Client.FamilyFilesJobAsync();
            if (job is not null)
            {
                ApplyJob(job);
                return;
            }

            // The job finished: whatever it did to the library, the list and the totals show it.
            ApplyJob(null);
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
            _jobPoll.Stop();
        }
    }

    private async Task LoadListingAsync()
    {
        try
        {
            var rows = new List<FilesRow>();
            if (_search is { } query)
            {
                var files = await _model.Client.FamilyFilesSearchAsync(query, 200);
                rows.AddRange(files.Select(FilesRow.ForFile));
                EmptyTitle.Text = "No files match";
                EmptyDetail.Text = "Search looks at file names across the whole library.";
            }
            else
            {
                var listing = await _model.Client.FamilyFilesListAsync(_path);
                rows.AddRange(listing.Folders.Select(FilesRow.ForFolder));
                rows.AddRange(listing.Files.Select(FilesRow.ForFile));
                EmptyTitle.Text = _path.Length == 0 ? "Nothing here yet" : "This folder is empty";
                EmptyDetail.Text = "Drop files onto this list, or press Add files.";
            }

            FileList.ItemsSource = rows;
            EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowCrumbs();
        }
        catch (SentinelServerException error) when (error.Status == 404 && _search is null && _path.Length > 0)
        {
            // The folder went away under us - renamed from another device, or trashed. Up we go.
            Notice(error.Message);
            Navigate(ParentOf(_path));
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    // ---------------------------------------------------------------- Navigation

    private void Navigate(string path)
    {
        _search = null;
        _path = path;
        SearchBox.Text = "";
        _ = LoadListingAsync();
    }

    private void ShowCrumbs()
    {
        Crumbs.Children.Clear();

        if (_search is { } query)
        {
            Crumbs.Children.Add(Crumb("Library", () => Navigate(_path)));
            Crumbs.Children.Add(Slash());
            Crumbs.Children.Add(new TextBlock
            {
                Text = $"Search: {query}",
                Foreground = Palette.PrimaryText,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            BackButton.IsEnabled = true;
            BackButton.ToolTip = "Back to the folder";
            return;
        }

        var segments = _path.Length == 0 ? Array.Empty<string>() : _path.Split('/');
        if (segments.Length == 0)
        {
            Crumbs.Children.Add(new TextBlock
            {
                Text = "Library",
                Foreground = Palette.PrimaryText,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            Crumbs.Children.Add(Crumb("Library", () => Navigate("")));
            for (var index = 0; index < segments.Length; index++)
            {
                Crumbs.Children.Add(Slash());
                var target = string.Join("/", segments.Take(index + 1));
                if (index == segments.Length - 1)
                {
                    Crumbs.Children.Add(new TextBlock
                    {
                        Text = segments[index],
                        Foreground = Palette.PrimaryText,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                else
                {
                    Crumbs.Children.Add(Crumb(segments[index], () => Navigate(target)));
                }
            }
        }

        BackButton.IsEnabled = _path.Length > 0;
        BackButton.ToolTip = "Up one folder";
    }

    private Button Crumb(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("LinkButton"),
            Padding = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock Slash() => new()
    {
        Text = "/",
        Foreground = Palette.SecondaryText,
        Margin = new Thickness(6, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_search is not null)
        {
            Navigate(_path);
            return;
        }
        if (_path.Length > 0) Navigate(ParentOf(_path));
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            if (_search is not null) Navigate(_path);
            return;
        }
        _search = query;
        _ = LoadListingAsync();
    }

    // ---------------------------------------------------------------- Rows

    private FilesRow? SelectedRow => FileList.SelectedItem as FilesRow;

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e.OriginalSource) is not { } row) return;
        if (row.Folder is { } folder)
        {
            Navigate(folder.Path);
        }
        else if (row.File is { } file)
        {
            _ = OpenAsync(file);
        }
    }

    /// <summary>
    /// A right-click selects the row under the pointer before the menu opens, so the menu acts on
    /// what was clicked rather than on whatever happened to be selected before.
    /// </summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { } item) item.IsSelected = true;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            e.Handled = true;
            return;
        }
        var isFile = row.File is not null;
        OpenItem.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        SaveCopyItem.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        OpenItem.Header = "Open";
        RenameItem.Header = "Rename…";
        TrashItem.Header = row.Folder is null ? "Move to trash" : "Move folder to trash";
    }

    private static FilesRow? RowUnder(object source) => ItemUnder(source)?.DataContext as FilesRow;

    private static ListBoxItem? ItemUnder(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (node is ListBoxItem item) return item;
            if (node is ListBox) return null;
        }
        return null;

        // A click can land on a Run inside a TextBlock, which is not a Visual and would make the
        // visual-tree walk throw; the logical tree gets it back up to the TextBlock.
        static DependencyObject? ParentOf(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { File: { } file }) _ = OpenAsync(file);
        else if (SelectedRow is { Folder: { } folder }) Navigate(folder.Path);
    }

    private void OnSaveCopyClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { File: { } file }) return;
        var dialog = new SaveFileDialog { FileName = file.Name, Title = "Save a copy" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = DownloadAsync(file, dialog.FileName, $"Saved a copy of {file.Name}.");
    }

    private async void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        var currentName = row.Name;
        var currentPath = row.Path;

        var prompt = new TextPromptWindow("Rename", "New name", initialValue: currentName) { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true || prompt.Value == currentName) return;

        try
        {
            await _model.Client.FamilyFilesMoveAsync(currentPath, Join(ParentOf(currentPath), prompt.Value));
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnTrashClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;

        var what = row.Folder is null ? $"\"{row.Name}\"" : $"the folder \"{row.Name}\" and everything in it";
        var confirmed = MessageBox.Show(
            Window.GetWindow(this),
            $"Move {what} to the trash?\n\nIt can be put back from the Trash until the trash is emptied.",
            "Move to trash",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            var result = await _model.Client.FamilyFilesDeleteAsync(row.Path);
            Notice($"Moved {result.Trashed} file{(result.Trashed == 1 ? "" : "s")} to the trash.");
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    // ---------------------------------------------------------------- Opening and saving

    /// <summary>
    /// Where a library file lands on this PC when opened: the same relative path under
    /// %TEMP%\SentinelCore\Files, so two files with the same name in different folders never
    /// overwrite each other.
    /// </summary>
    internal static string TempPathFor(string remotePath) =>
        Path.Combine(
            Path.GetTempPath(),
            "SentinelCore",
            "Files",
            remotePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task OpenAsync(FamilyFileEntry file)
    {
        var local = TempPathFor(file.Path);
        var cached = File.Exists(local) && new FileInfo(local).Length == file.Size;
        if (!cached && !await DownloadAsync(file, local, null)) return;

        try
        {
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Notice($"Windows could not open {file.Name}: {error.Message}");
        }
    }

    private async Task<bool> DownloadAsync(FamilyFileEntry file, string localPath, string? doneMessage)
    {
        if (_transfer is not null)
        {
            Notice("Wait for the current transfer to finish.");
            return false;
        }

        using var cancel = new CancellationTokenSource();
        _transfer = cancel;
        ShowTransfer($"Downloading {file.Name}…", 0);
        try
        {
            var progress = new Progress<long>(done =>
            {
                var fraction = file.Size > 0 ? Math.Clamp((double)done / file.Size, 0, 1) : 0;
                ShowTransfer($"Downloading {file.Name} · {ByteSizeConverter.FormatBytes(done)} of {ByteSizeConverter.FormatBytes(file.Size)}", fraction);
            });
            await _model.Client.FamilyFilesDownloadAsync(file.Path, localPath, progress, cancel.Token);
            if (doneMessage is not null) Notice(doneMessage);
            return true;
        }
        catch (OperationCanceledException)
        {
            Notice("Download cancelled.");
            return false;
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
            return false;
        }
        finally
        {
            _transfer = null;
            HideTransfer();
        }
    }

    // ---------------------------------------------------------------- Adding

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Add files to the library" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _ = UploadAsync(Expand(dialog.FileNames));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        e.Handled = true;
        _ = UploadAsync(Expand(dropped));
    }

    /// <summary>
    /// Files as (local path, library path). A dropped folder brings its contents along, keeping its
    /// own structure under the folder we are looking at - dropping "Holiday 2026" makes a
    /// "Holiday 2026" folder here, not a pile of loose photos.
    /// </summary>
    private List<(string Local, string Remote)> Expand(IEnumerable<string> paths)
    {
        var destination = _search is null ? _path : "";
        var files = new List<(string, string)>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var root = Path.GetDirectoryName(path) ?? path;
                foreach (var inside in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, inside).Replace(Path.DirectorySeparatorChar, '/');
                    files.Add((inside, Join(destination, relative)));
                }
            }
            else if (File.Exists(path))
            {
                files.Add((path, Join(destination, Path.GetFileName(path))));
            }
        }
        return files;
    }

    private async Task UploadAsync(List<(string Local, string Remote)> files)
    {
        if (files.Count == 0) return;
        if (_transfer is not null)
        {
            Notice("Wait for the current transfer to finish.");
            return;
        }

        var limit = _status?.MaxUploadBytes;
        using var cancel = new CancellationTokenSource();
        _transfer = cancel;

        var added = 0;
        var alreadyThere = 0;
        var skipped = new List<string>();
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var (local, remote) = files[index];
                var name = Path.GetFileName(local);
                var size = new FileInfo(local).Length;
                if (limit is { } max && size > max)
                {
                    skipped.Add($"{name} is larger than the server's limit of {ByteSizeConverter.FormatBytes(max)}");
                    continue;
                }

                var label = files.Count == 1 ? $"Uploading {name}" : $"Uploading {index + 1} of {files.Count}: {name}";
                ShowTransfer($"{label}…", 0);
                var progress = new Progress<long>(sent =>
                {
                    var fraction = size > 0 ? Math.Clamp((double)sent / size, 0, 1) : 1;
                    ShowTransfer($"{label} · {ByteSizeConverter.FormatBytes(sent)} of {ByteSizeConverter.FormatBytes(size)}", fraction);
                });

                try
                {
                    var result = await _model.Client.FamilyFilesUploadAsync(
                        local, remote, File.GetLastWriteTimeUtc(local), progress, cancel.Token);
                    if (result.AlreadyThere) alreadyThere++;
                    else added++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    skipped.Add($"{name}: {AppModel.UserFacingMessage(error)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            skipped.Add("cancelled");
        }
        finally
        {
            _transfer = null;
            HideTransfer();
        }

        var summary = new List<string>();
        if (added > 0) summary.Add($"Added {added} file{(added == 1 ? "" : "s")}");
        if (alreadyThere > 0) summary.Add($"{alreadyThere} already in the library");
        if (skipped.Count > 0) summary.Add($"{skipped.Count} not added: {string.Join("; ", skipped.Take(3))}");
        Notice(summary.Count == 0 ? "Nothing was added." : string.Join(" · ", summary) + ".");

        await ReloadAsync();
    }

    private void OnCancelTransferClicked(object sender, RoutedEventArgs e) => _transfer?.Cancel();

    private async void OnNewFolderClicked(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("New folder", "Folder name") { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;

        try
        {
            var result = await _model.Client.FamilyFilesCreateFolderAsync(Join(_search is null ? _path : "", prompt.Value));
            Navigate(result.Path);
        }
        catch (Exception error)
        {
            Notice(AppModel.UserFacingMessage(error));
        }
    }

    // ---------------------------------------------------------------- Windows

    private async void OnDrivesClicked(object sender, RoutedEventArgs e)
    {
        var window = new FilesDrivesWindow(_model, _status) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        // Whatever was started in there shows in the banner here, and whatever a restore brought
        // back shows in the list.
        await ReloadAsync();
    }

    private async void OnTrashWindowClicked(object sender, RoutedEventArgs e)
    {
        var window = new FilesTrashWindow(_model, _status?.IsAdmin ?? _model.IsAdmin) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        await ReloadAsync();
    }

    // ---------------------------------------------------------------- Bits

    private void ShowTransfer(string text, double fraction)
    {
        TransferText.Text = text;
        TransferProgress.Value = fraction;
        TransferPanel.Visibility = Visibility.Visible;
    }

    private void HideTransfer() => TransferPanel.Visibility = Visibility.Collapsed;

    private void Notice(string message)
    {
        NoticeText.Text = message;
        NoticeText.Visibility = Visibility.Visible;
    }

    internal static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    internal static string Join(string folder, string name) =>
        folder.Length == 0 ? name : $"{folder}/{name}";
}

/// <summary>One line of the list: a folder or a file, with the columns already worded.</summary>
public sealed class FilesRow
{
    public FamilyFolderEntry? Folder { get; init; }
    public FamilyFileEntry? File { get; init; }

    public string Path => Folder?.Path ?? File?.Path ?? "";
    public string Name => Folder?.Name ?? File?.Name ?? "";
    public string Glyph => Folder is not null ? "" : File is { IsImage: true } ? "" : "";
    public Brush GlyphBrush => Folder is not null ? Palette.Amber : Palette.SecondaryText;
    public string SizeText => File is { } file ? ByteSizeConverter.FormatBytes(file.Size) : "";

    public string DateText => File is { } file
        ? file.ModifiedUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)
        : "";

    public static FilesRow ForFolder(FamilyFolderEntry folder) => new() { Folder = folder };

    public static FilesRow ForFile(FamilyFileEntry file) => new() { File = file };
}
