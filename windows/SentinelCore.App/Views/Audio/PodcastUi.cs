using System.Windows;
using System.Windows.Controls;
using SentinelCore.App.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Views.Audio;

/// <summary>Episode rows and menus shared by every Podcasts screen.</summary>
public static class PodcastUi
{
    /// <summary>"2 Oct · 45 min" / "2 Oct · 12 min left" / "Played".</summary>
    public static string Facts(PodcastEpisode episode, bool showShow)
    {
        var parts = new List<string>();
        if (showShow && !string.IsNullOrEmpty(episode.PodcastTitle)) parts.Add(episode.PodcastTitle!);
        if (episode.PublishedAt is { } published)
        {
            var local = published.ToLocalTime();
            parts.Add(local.Year == DateTime.Now.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy"));
        }
        if (episode.Season is { } season && episode.Episode is { } number) parts.Add($"S{season} E{number}");
        else if (episode.Episode is { } only) parts.Add($"Episode {only}");
        if (episode.EpisodeType is "trailer" or "bonus") parts.Add(episode.EpisodeType == "trailer" ? "Trailer" : "Bonus");
        if (episode.IsPlayed) parts.Add("Played");
        else if (episode.IsInProgress) parts.Add(AudioTime.Left(episode.RemainingSeconds));
        else parts.Add(AudioTime.Length(episode.DurationSeconds));
        if (episode.Download is { IsDone: true }) parts.Add(episode.Download.Enhanced ? "Downloaded · enhanced" : "Downloaded");
        else if (episode.Download is { IsActive: true } active) parts.Add($"Downloading {Math.Round(active.Progress * 100)}%");
        else if (episode.Download is { State: "failed" }) parts.Add("Download failed");
        if (episode.Archived) parts.Add("No longer in the feed");
        if (episode.HasTranscript) parts.Add("Transcript");
        if (episode.BookmarkCount > 0) parts.Add(AudioUi.Count(episode.BookmarkCount, "bookmark", "bookmarks"));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// One episode in a list: artwork, title, facts, a progress strip, and a play button.
    /// Double-click opens its page; the play button plays it (with <paramref name="playList"/> as
    /// the queue when given).
    /// </summary>
    public static FrameworkElement EpisodeRow(
        AppModel model, INavigator navigator, PodcastEpisode episode, Action play, Action? changed, bool showShow = true)
    {
        var grid = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(AudioUi.Art(episode.ArtUrl, 56, AudioUi.PodcastGlyph));

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = episode.Title,
            Foreground = episode.IsPlayed ? Palette.SecondaryText : Palette.PrimaryText,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock { Text = Facts(episode, showShow), Foreground = Palette.SecondaryText, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(episode.Summary))
        {
            text.Children.Add(new TextBlock { Text = episode.Summary, Foreground = Palette.SecondaryText, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, TextWrapping = TextWrapping.Wrap });
        }
        if (episode.IsInProgress && episode.Progress is { } progress)
        {
            text.Children.Add(new ProgressBar { Maximum = 100, Value = progress.Percent, Height = 3, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Left });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var menu = EpisodeMenu(model, navigator, episode, changed);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(AudioUi.GlyphButton(AudioUi.PlayGlyph, episode.IsInProgress ? "Resume" : "Play", play, 13, Palette.Green));
        var more = AudioUi.GlyphButton(AudioUi.MoreGlyph, "More", () => { }, 12);
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        buttons.Children.Add(more);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        var row = AudioUi.ClickableRow(grid, () => AudioNav.OpenEpisode(model, navigator, episode.Id, episode.Title), menu, episode.Title);
        row.Margin = new Thickness(0, 0, 0, 4);
        return row;
    }

    public static ContextMenu EpisodeMenu(AppModel model, INavigator navigator, PodcastEpisode episode, Action? changed)
    {
        var player = PlayerController.Shared!;
        var menu = new ContextMenu();

        async Task Do(Func<Task> action)
        {
            try
            {
                await action();
                changed?.Invoke();
            }
            catch (Exception error)
            {
                MessageBox.Show(Application.Current.MainWindow, AppModel.UserFacingMessage(error), episode.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        menu.Items.Add(AudioUi.Item(episode.IsInProgress ? "Resume" : "Play", () => _ = player.PlayEpisodesAsync(new[] { episode }, 0, new AudioContext { Type = "podcast", Id = episode.PodcastId, Name = episode.PodcastTitle })));
        menu.Items.Add(AudioUi.Item("Episode details", () => AudioNav.OpenEpisode(model, navigator, episode.Id, episode.Title)));
        if (!string.IsNullOrEmpty(episode.PodcastId))
        {
            menu.Items.Add(AudioUi.Item("Go to show", () => AudioNav.OpenShow(model, navigator, episode.PodcastId!, episode.PodcastTitle ?? "Show")));
        }
        menu.Items.Add(new Separator());
        if (episode.InQueue)
        {
            menu.Items.Add(AudioUi.Item("Remove from Up Next", () => _ = Do(() => model.Client.RemoveFromPodcastQueueAsync(episode.Id))));
        }
        else
        {
            menu.Items.Add(AudioUi.Item("Play next (Up Next)", () => _ = Do(() => model.Client.AddToPodcastQueueAsync(episode.Id, "next"))));
            menu.Items.Add(AudioUi.Item("Add to Up Next", () => _ = Do(() => model.Client.AddToPodcastQueueAsync(episode.Id, "last"))));
        }
        menu.Items.Add(AudioUi.Item("Add to this PC's queue", () => player.AddToQueue(new[] { new QueueEntry(episode) })));
        menu.Items.Add(AudioUi.Item(episode.IsPlayed ? "Mark unplayed" : "Mark played", () => _ = Do(() => model.Client.SetEpisodePlayedAsync(episode.Id, !episode.IsPlayed))));
        menu.Items.Add(new Separator());
        if (episode.Download is not { IsDone: true } && episode.Download is not { IsActive: true })
        {
            menu.Items.Add(AudioUi.Item("Download to the server", () => _ = Do(() => model.Client.DownloadEpisodeOnServerAsync(episode.Id))));
        }
        if (episode.Download is { IsDone: true })
        {
            menu.Items.Add(AudioUi.Item("Remove the server's download", () => _ = Do(() => model.Client.DeleteEpisodeDownloadAsync(episode.Id))));
        }
        menu.Items.Add(AudioUi.Item("Save file…", () => _ = AudioUi.SaveFileAsync(SentinelClient.EpisodeDownloadPath(episode.Id),
            $"{episode.PodcastTitle} - {episode.Title}", episode.MimeType switch
            {
                "audio/mp4" or "audio/x-m4a" or "audio/aac" => ".m4a",
                "audio/ogg" => ".ogg",
                _ => ".mp3",
            })));
        if (!string.IsNullOrEmpty(episode.Link))
        {
            menu.Items.Add(AudioUi.Item("Open the episode's web page", () => OpenLink(episode.Link!)));
        }
        return menu;
    }

    public static void OpenLink(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser to open it in; nothing else to do.
        }
    }

    /// <summary>A show's square card.</summary>
    public static Button ShowCard(AppModel model, INavigator navigator, Podcast show, double size = 150)
    {
        var subtitle = show.UnplayedCount > 0 ? $"{show.UnplayedCount} new" : show.Author;
        return AudioUi.Card(AudioUi.Art(show.ArtUrl, size, AudioUi.PodcastGlyph), show.Title, subtitle,
            () =>
            {
                if (show.Id is { } id) AudioNav.OpenShow(model, navigator, id, show.Title);
            }, size);
    }

    /// <summary>A directory result's card: opens the "look before you follow" preview.</summary>
    public static Button DirectoryCard(AppModel model, INavigator navigator, DirectoryShow show, double size = 140) =>
        AudioUi.Card(AudioUi.Art(show.ArtUrl, size, AudioUi.PodcastGlyph), show.Title, show.Subscribed ? "Following" : show.Author,
            () =>
            {
                if (show.Subscribed && show.PodcastId is { } id) AudioNav.OpenShow(model, navigator, id, show.Title);
                else navigator.Push(new PodcastPreviewView(model, navigator, show.FeedUrl, show.ItunesId), show.Title);
            }, size);
}
