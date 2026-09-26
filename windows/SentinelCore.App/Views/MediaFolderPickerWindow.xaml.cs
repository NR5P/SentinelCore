using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

/// <summary>
/// Picks a folder for a Movies &amp; TV library by walking the server's own disks and the shared
/// folders of other computers on the network, through GET /api/media/browse.
/// </summary>
/// <remarks>
/// The picker only ever shows what the server can see - a path on this PC means nothing to a
/// server on another machine. The starting page lists places on the server and the network
/// computers it has sign-ins for; a computer (smb://host) lists its shared folders but is not a
/// folder itself, so "Use this folder" waits until one of its shares is open.
///
/// A folder that cannot be opened comes back as data (error, needsLogin, host), never as a failed
/// request, so the page shows the message and - when a sign-in would fix it - a button for that.
/// </remarks>
public partial class MediaFolderPickerWindow : Window
{
    private const string DriveGlyph = "";
    private const string FolderGlyph = "";
    private const string ComputerGlyph = "";

    private readonly AppModel _model;
    private readonly string? _startPath;

    private MediaBrowseListing? _listing;
    private List<MediaNetworkComputer> _computers = new();
    private bool _computersLoaded;
    private List<MediaDiscoveredComputer>? _discovered;
    private bool _discovering;

    // Bumped by every browse; an answer for an older one is dropped, so clicking quickly through
    // folders on a slow share never paints a stale page over the one asked for last.
    private int _generation;

    public MediaFolderPickerWindow(AppModel model, string? startPath = null)
    {
        _model = model;
        _startPath = string.IsNullOrWhiteSpace(startPath) ? null : startPath.Trim();
        InitializeComponent();
        UpButton.IsEnabled = false;
        Loaded += async (_, _) => await BrowseAsync(_startPath);
    }

    /// <summary>The folder chosen, once the dialog closed with "Use this folder".</summary>
    public string? ChosenPath { get; private set; }

    // ---------------------------------------------------------------- Browsing

    private async Task BrowseAsync(string? path)
    {
        var generation = ++_generation;
        UseButton.IsEnabled = false;
        Message(string.IsNullOrWhiteSpace(path) ? "Loading…" : $"Opening {path}…");
        try
        {
            var listing = await _model.Client.MediaBrowseAsync(path);
            if (generation != _generation) return;

            _listing = listing;
            if (listing.IsRoots)
            {
                _computers = listing.Computers;
                _computersLoaded = true;
            }
            PathBox.Text = listing.Path;
            UpButton.IsEnabled = listing.CanGoUp;
            UseButton.IsEnabled = listing.CanPick;
            Message("");
            Render();
            Scroller.ScrollToTop();
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            // 404 here is "Movies & TV is turned off in Settings." - the server's own wording.
            Message(AppModel.UserFacingMessage(error));
            UseButton.IsEnabled = _listing?.CanPick == true;
        }
    }

    private void Render()
    {
        ContentHost.Children.Clear();
        if (_listing is null) return;
        if (_listing.IsRoots) RenderRoots(_listing);
        else RenderFolder(_listing);
    }

    private void RenderRoots(MediaBrowseListing listing)
    {
        ContentHost.Children.Add(Heading("ON THIS SERVER", top: 0));
        if (listing.Folders.Count == 0) ContentHost.Children.Add(Caption("The server listed no folders to start from. Type a path above."));
        foreach (var folder in listing.Folders)
        {
            ContentHost.Children.Add(Row(DriveGlyph, folder.Name, folder.Name == folder.Path ? null : folder.Path,
                CountWording(folder.VideoCount), () => _ = BrowseAsync(folder.Path)));
        }

        ContentHost.Children.Add(Heading("NETWORK COMPUTERS", top: 18));
        ContentHost.Children.Add(Caption(
            "Shared folders on other computers: a Windows PC, a Mac with File Sharing on, or a NAS. " +
            "Open a computer to see its shared folders."));
        if (_computers.Count == 0) ContentHost.Children.Add(Caption("No computers signed in to yet.", top: 8));
        foreach (var computer in _computers)
        {
            var signIn = new Button
            {
                Content = "Sign-in…",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                ToolTip = "Change the username or password the server uses for this computer, or forget it",
            };
            signIn.Click += (_, _) => OpenSignIn(computer, null, null);
            ContentHost.Children.Add(Row(ComputerGlyph, computer.DisplayName,
                $"{computer.Host} · {(computer.Username.Length == 0 ? "guest" : $"signed in as {computer.SignInSummary}")}",
                null, () => _ = BrowseAsync(computer.Path), signIn));
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = "Add a computer…" };
        add.Click += (_, _) => OpenSignIn(null, null, null);
        actions.Children.Add(add);
        var find = new Button { Content = _discovering ? "Looking…" : "Find computers", Margin = new Thickness(8, 0, 0, 0), IsEnabled = !_discovering };
        find.Click += async (_, _) => await DiscoverAsync();
        actions.Children.Add(find);
        ContentHost.Children.Add(actions);

        if (_discovering)
        {
            ContentHost.Children.Add(Caption("Looking for computers sharing files on the network. This takes a few seconds…", top: 10));
        }
        else if (_discovered is { } found)
        {
            ContentHost.Children.Add(Heading("FOUND ON THE NETWORK", top: 16));
            if (found.Count == 0) ContentHost.Children.Add(Caption("No computers answered."));
            foreach (var computer in found)
            {
                var where = computer.Address is { Length: > 0 } address && address != computer.Host
                    ? $"{computer.Host} ({address})"
                    : computer.Host;
                if (computer.SavedId is not null) where += " · signed in";
                ContentHost.Children.Add(Row(ComputerGlyph, computer.Name.Length > 0 ? computer.Name : computer.Host, where,
                    null, () => _ = BrowseAsync(computer.Path)));
            }
        }

        if (_discovering || _discovered is not null)
        {
            ContentHost.Children.Add(Caption(
                "Windows PCs don't show up here - add a Windows PC by its name or IP address with Add a computer.", top: 8));
        }
    }

    private void RenderFolder(MediaBrowseListing listing)
    {
        if (!string.IsNullOrEmpty(listing.Error))
        {
            ContentHost.Children.Add(new TextBlock
            {
                Text = listing.Error,
                Foreground = Palette.Red,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
            if (listing.NeedsLogin)
            {
                var host = listing.Host ?? HostOf(listing.Path);
                var signIn = new Button
                {
                    Content = host is null ? "Sign in…" : $"Sign in to {host}…",
                    Style = (Style)FindResource("PrimaryButton"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                signIn.Click += async (_, _) => await OpenSignInForHostAsync(host, listing.Path);
                ContentHost.Children.Add(signIn);
            }
            return;
        }

        if (listing.IsComputer)
        {
            var saved = FindSaved(listing.Host);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            line.Children.Add(new TextBlock
            {
                Text = saved is null
                    ? $"Shared folders on {listing.Host}. Open one to use it, or a folder inside it."
                    : $"Shared folders on {saved.DisplayName} ({(saved.Username.Length == 0 ? "guest" : saved.SignInSummary)}). Open one to use it, or a folder inside it.",
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 440,
            });
            var change = new Button
            {
                Content = saved is null ? "Sign in…" : "Change sign-in…",
                Style = (Style)FindResource("LinkButton"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            change.Click += async (_, _) => await OpenSignInForHostAsync(listing.Host, listing.Path);
            line.Children.Add(change);
            ContentHost.Children.Add(line);
        }
        else if (listing.VideoCount is { } videos)
        {
            ContentHost.Children.Add(Caption(videos switch
            {
                0 => "No videos directly in this folder. That's fine for a folder of movie folders.",
                1 => "1 video in this folder",
                _ => $"{videos:N0} videos in this folder",
            }, top: 0, bottom: 10));
        }

        if (!string.IsNullOrWhiteSpace(listing.Hint))
        {
            ContentHost.Children.Add(new TextBlock
            {
                Text = listing.Hint,
                Foreground = Palette.Amber,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        if (listing.Folders.Count == 0)
        {
            ContentHost.Children.Add(Caption(listing.IsComputer ? "No shared folders listed." : "No folders inside this one."));
            return;
        }
        foreach (var folder in listing.Folders)
        {
            ContentHost.Children.Add(Row(FolderGlyph, folder.Name, null, CountWording(folder.VideoCount),
                () => _ = BrowseAsync(folder.Path)));
        }
    }

    // ---------------------------------------------------------------- Network computers

    private async Task DiscoverAsync()
    {
        _discovering = true;
        if (_listing?.IsRoots == true) Render();
        try
        {
            _discovered = await _model.Client.DiscoverMediaComputersAsync();
            Message(_discovered.Count == 0 ? "" : $"Found {_discovered.Count} computer{(_discovered.Count == 1 ? "" : "s")}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            _discovering = false;
            if (_listing?.IsRoots == true) Render();
        }
    }

    private MediaNetworkComputer? FindSaved(string? host) =>
        host is null
            ? null
            : _computers.FirstOrDefault(computer => string.Equals(computer.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The sign-in form for a host a folder needed a sign-in for: the saved computer's own form
    /// when there is one (so it is edited rather than duplicated), a new one prefilled otherwise.
    /// </summary>
    private async Task OpenSignInForHostAsync(string? host, string? retryPath)
    {
        if (!_computersLoaded)
        {
            try
            {
                _computers = await _model.Client.MediaNetworkComputersAsync();
                _computersLoaded = true;
            }
            catch (Exception)
            {
                // Not knowing the saved ones only means a fresh form; the server replaces a saved
                // sign-in for the same host anyway.
            }
        }
        OpenSignIn(FindSaved(host), host, retryPath);
    }

    private void OpenSignIn(MediaNetworkComputer? existing, string? host, string? retryPath)
    {
        var window = new MediaNetworkSignInWindow(_model, existing, host) { Owner = this };
        if (window.ShowDialog() != true) return;

        if (window.Forgotten)
        {
            _computers.RemoveAll(computer => computer.Id == existing?.Id);
            Message($"Forgot {existing?.DisplayName}.");
            _ = BrowseAsync(null);
            return;
        }

        if (window.Result is { } result)
        {
            _computers.RemoveAll(computer => computer.Id == result.Computer.Id);
            _computers.Add(result.Computer);
            // Back to the folder that asked for the sign-in when it is on this computer; otherwise
            // the computer's list of shared folders.
            var target = retryPath is { Length: > 0 } && string.Equals(HostOf(retryPath), result.Computer.Host, StringComparison.OrdinalIgnoreCase)
                ? retryPath
                : result.Computer.Path;
            _ = BrowseAsync(target);
        }
    }

    /// <summary>The computer part of smb://host/Share/…, or null for anything else.</summary>
    internal static string? HostOf(string? path)
    {
        const string scheme = "smb://";
        if (path is null || !path.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = path[scheme.Length..];
        var slash = rest.IndexOf('/');
        var host = slash < 0 ? rest : rest[..slash];
        return host.Length == 0 ? null : host;
    }

    // ---------------------------------------------------------------- Building blocks

    private static string? CountWording(int? videos) => videos switch
    {
        null or <= 0 => null,
        1 => "1 video",
        var count => $"{count:N0} videos",
    };

    private TextBlock Heading(string text, double top) => new()
    {
        Text = text,
        Style = (Style)FindResource("GroupHeading"),
        Margin = new Thickness(0, top, 0, 6),
    };

    private TextBlock Caption(string text, double top = 0, double bottom = 6) => new()
    {
        Text = text,
        Style = (Style)FindResource("Caption"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, bottom),
    };

    /// <summary>
    /// One clickable line: a folder, a drive or a computer. A button rather than a list item, so
    /// it is reachable by Tab and opens with Enter or Space as well as a click.
    /// </summary>
    private FrameworkElement Row(string glyph, string title, string? subtitle, string? trailing, Action open, Button? extra = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = Palette.Cyan,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Palette.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (trailing is not null)
        {
            var count = new TextBlock
            {
                Text = trailing,
                Style = (Style)FindResource("Caption"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(count, 2);
            grid.Children.Add(count);
        }

        var button = new Button
        {
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
            ToolTip = subtitle is null ? null : $"{title}\n{subtitle}",
        };
        button.Click += (_, _) => open();

        if (extra is null)
        {
            button.Margin = new Thickness(0, 0, 0, 6);
            return button;
        }

        var line = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(button);
        Grid.SetColumn(extra, 1);
        line.Children.Add(extra);
        return line;
    }

    // ---------------------------------------------------------------- Header and footer

    private async void OnUpClicked(object sender, RoutedEventArgs e)
    {
        if (_listing?.Parent is not { } parent) return;
        await BrowseAsync(parent.Length == 0 ? null : parent);
    }

    private async void OnGoClicked(object sender, RoutedEventArgs e) => await BrowseAsync(PathBox.Text.Trim());

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await BrowseAsync(PathBox.Text.Trim());
    }

    private void OnUseClicked(object sender, RoutedEventArgs e)
    {
        if (_listing is not { CanPick: true } listing) return;
        ChosenPath = listing.Path;
        DialogResult = true;
        Close();
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
