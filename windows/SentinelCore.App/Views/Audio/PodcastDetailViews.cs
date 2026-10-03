using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SentinelCore.App.Audio;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// One show: art, description, follow/unfollow, settings, and its episodes with sort, filter,
/// season and search.
/// </summary>
public sealed class PodcastShowView : AudioPage
{
    private static readonly (string Value, string Label)[] Filters =
    {
        ("all", "All"), ("unplayed", "Unplayed"), ("inProgress", "In progress"), ("downloaded", "Downloaded"), ("bookmarked", "Bookmarked"),
    };

    private readonly string _id;
    private Podcast? _show;
    private string _filter = "all";
    private string? _sort;
    private int? _season;
    private string _query = "";
    private readonly StackPanel _episodes = new();
    private readonly List<PodcastEpisode> _loaded = new();

    public PodcastShowView(AppModel model, INavigator navigator, string id) : base(model, navigator)
    {
        _id = id;
        Loaded += async (_, _) =>
        {
            if (_show is null) await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            _show = await Model.Client.PodcastAsync(_id);
            _sort ??= _show.MySettings?.Sort ?? "newest";
            RenderHeader(_show);
            await LoadEpisodesAsync(0);
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }

    private void RenderHeader(Podcast show)
    {
        Host.Children.Clear();
        var facts = new List<string>();
        if (!string.IsNullOrEmpty(show.Author)) facts.Add(show.Author!);
        if (show.Categories.Count > 0) facts.Add(string.Join(", ", show.Categories.Take(2)));
        facts.Add(AudioUi.Count(show.EpisodeCount, "episode", "episodes"));
        if (show.UnplayedCount > 0) facts.Add($"{show.UnplayedCount} unplayed");
        if (show.SubscriberCount > 1) facts.Add($"{show.SubscriberCount} in the household follow it");

        var actions = new List<UIElement>();
        actions.Add(AudioUi.TextButton(show.Subscribed ? "Following ✓" : "Follow", () => _ = ToggleFollowAsync(show), show.Subscribed ? "Click to unfollow" : null, primary: !show.Subscribed));
        actions.Add(AudioUi.TextButton("▶  Play newest unplayed", () => _ = PlayNewestAsync()));
        actions.Add(AudioUi.TextButton("Settings…", () =>
        {
            var window = new PodcastSettingsWindow(Model, show) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true) _ = LoadAsync();
        }));
        var menu = new ContextMenu();
        menu.Items.Add(AudioUi.Item("Check for new episodes", () => _ = Try(async () =>
        {
            await Model.Client.RefreshPodcastAsync(_id);
            await LoadAsync();
        })));
        menu.Items.Add(AudioUi.Item("Mark every episode played", () => _ = Try(async () =>
        {
            if (!AudioUi.Confirm("Mark played", $"Mark every episode of {show.Title} played?")) return;
            await Model.Client.MarkPodcastPlayedAsync(_id, true);
            await LoadAsync();
        })));
        menu.Items.Add(AudioUi.Item("Mark every episode unplayed", () => _ = Try(async () =>
        {
            await Model.Client.MarkPodcastPlayedAsync(_id, false);
            await LoadAsync();
        })));
        if (!string.IsNullOrEmpty(show.Link)) menu.Items.Add(AudioUi.Item("Open the show's web page", () => PodcastUi.OpenLink(show.Link!)));
        foreach (var funding in show.Funding) menu.Items.Add(AudioUi.Item(string.IsNullOrWhiteSpace(funding.Title) ? "Support the show" : funding.Title!, () => PodcastUi.OpenLink(funding.Url)));
        if (Model.IsAdmin)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(AudioUi.Item("Delete the show for everyone…", () => _ = Try(async () =>
            {
                if (!AudioUi.Confirm("Delete show", $"Forget {show.Title}, every episode and every download, for the whole household?")) return;
                await Model.Client.DeletePodcastAsync(_id);
                Navigator.Pop();
            })));
        }
        var more = AudioUi.TextButton("More ▾", () => { });
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        actions.Add(more);

        Host.Children.Add(DetailHeader.Build(AudioUi.Art(show.ArtUrl, 200, AudioUi.PodcastGlyph), "Podcast", show.Title, null, string.Join(" · ", facts), actions.ToArray()));
        if (!string.IsNullOrWhiteSpace(show.Description))
        {
            var description = AudioUi.Body(show.Description!);
            description.MaxHeight = 90;
            description.TextTrimming = TextTrimming.WordEllipsis;
            description.ToolTip = show.Description;
            description.Margin = new Thickness(0, 0, 0, 10);
            Host.Children.Add(description);
        }
        if (!string.IsNullOrEmpty(show.RefreshError))
        {
            Host.Children.Add(new TextBlock { Text = $"The feed couldn't be read last time: {show.RefreshError}", Foreground = Palette.Amber, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        }

        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        var filter = new ComboBox { Width = 140, ItemsSource = Filters.Select(f => f.Label).ToList(), SelectedIndex = Array.FindIndex(Filters, f => f.Value == _filter), Margin = new Thickness(0, 0, 8, 0) };
        filter.SelectionChanged += (_, _) =>
        {
            _filter = Filters[Math.Max(0, filter.SelectedIndex)].Value;
            _ = LoadEpisodesAsync(0);
        };
        var sort = new ComboBox { Width = 130, ItemsSource = new[] { "Newest first", "Oldest first" }, SelectedIndex = _sort == "oldest" ? 1 : 0, Margin = new Thickness(0, 0, 8, 0) };
        sort.SelectionChanged += (_, _) =>
        {
            _sort = sort.SelectedIndex == 1 ? "oldest" : "newest";
            _ = LoadEpisodesAsync(0);
        };
        var search = new TextBox { Width = 200, ToolTip = "Search this show's episodes", Text = _query };
        search.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            _query = search.Text.Trim();
            _ = LoadEpisodesAsync(0);
        };
        bar.Children.Add(filter);
        bar.Children.Add(sort);
        bar.Children.Add(_seasonPicker);
        bar.Children.Add(search);
        Host.Children.Add(bar);
        Host.Children.Add(_episodes);
    }

    private readonly ComboBox _seasonPicker = new() { Width = 120, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private bool _seasonsWired;

    private async Task LoadEpisodesAsync(int offset)
    {
        if (offset == 0)
        {
            _episodes.Children.Clear();
            _episodes.Children.Add(AudioUi.Caption("Loading episodes…"));
            _loaded.Clear();
        }
        try
        {
            var page = await Model.Client.PodcastEpisodesAsync(_id, _sort, _filter, _query, _season, offset, 100);
            if (offset == 0) _episodes.Children.Clear();
            if (page.Seasons.Count > 1 && !_seasonsWired)
            {
                _seasonsWired = true;
                var labels = new List<string> { "All seasons" };
                labels.AddRange(page.Seasons.Select(s => $"Season {s}"));
                _seasonPicker.ItemsSource = labels;
                _seasonPicker.SelectedIndex = 0;
                _seasonPicker.Visibility = Visibility.Visible;
                _seasonPicker.SelectionChanged += (_, _) =>
                {
                    _season = _seasonPicker.SelectedIndex <= 0 ? null : page.Seasons[_seasonPicker.SelectedIndex - 1];
                    _ = LoadEpisodesAsync(0);
                };
            }
            var start = _loaded.Count;
            _loaded.AddRange(page.Items);
            for (var i = 0; i < page.Items.Count; i++)
            {
                var index = start + i;
                _episodes.Children.Add(PodcastUi.EpisodeRow(Model, Navigator, page.Items[i],
                    () => _ = Player.PlayEpisodesAsync(_loaded.Skip(index).Take(1).ToList(), 0, new AudioContext { Type = "podcast", Id = _id, Name = _show?.Title }),
                    () => _ = LoadEpisodesAsync(0), showShow: false));
            }
            if (page.Total == 0) _episodes.Children.Add(AudioUi.Caption("No episodes match."));
            if (_episodes.Children.Count > 0 && _episodes.Children[^1] is Button { Tag: "more" } old) _episodes.Children.Remove(old);
            if (_loaded.Count < page.Total)
            {
                var more = AudioUi.TextButton($"Load more ({page.Total - _loaded.Count} left)", () => _ = LoadEpisodesAsync(_loaded.Count), margin: new Thickness(0, 8, 0, 0));
                more.Tag = "more";
                more.HorizontalAlignment = HorizontalAlignment.Left;
                _episodes.Children.Add(more);
            }
        }
        catch (Exception error)
        {
            _episodes.Children.Clear();
            Fail(error);
        }
    }

    private async Task ToggleFollowAsync(Podcast show) => await Try(async () =>
    {
        if (show.Subscribed)
        {
            if (!AudioUi.Confirm("Unfollow", $"Stop following {show.Title}?")) return;
            await Model.Client.UnsubscribeAsync(_id);
        }
        else
        {
            await Model.Client.SubscribeAsync(show.FeedUrl);
        }
        await LoadAsync();
    });

    private async Task PlayNewestAsync() => await Try(async () =>
    {
        var page = await Model.Client.PodcastEpisodesAsync(_id, "newest", "unplayed", limit: 1);
        if (page.Items.Count == 0)
        {
            Notice("You've heard every episode.");
            return;
        }
        await Player.PlayEpisodesAsync(page.Items, 0, new AudioContext { Type = "podcast", Id = _id, Name = _show?.Title });
    });
}

/// <summary>
/// "Look before you follow" (§6 preview): a directory show's description and latest episodes,
/// with Follow. Episodes can be played straight from the feed without following.
/// </summary>
public sealed class PodcastPreviewView : AudioPage
{
    private readonly string? _feedUrl;
    private readonly long? _itunesId;

    public PodcastPreviewView(AppModel model, INavigator navigator, string? feedUrl, long? itunesId) : base(model, navigator)
    {
        _feedUrl = feedUrl;
        _itunesId = itunesId;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading("Reading the feed…");
        try
        {
            var preview = await Model.Client.PodcastPreviewAsync(_feedUrl, _feedUrl is null ? _itunesId : null);
            var show = preview.Podcast;
            Host.Children.Clear();
            var follow = AudioUi.TextButton("Follow", () => _ = Try(async () =>
            {
                var followed = await Model.Client.SubscribeAsync(show.FeedUrl.Length > 0 ? show.FeedUrl : _feedUrl, _itunesId);
                Navigator.Pop();
                if (followed.Id is { } id) AudioNav.OpenShow(Model, Navigator, id, followed.Title);
            }), primary: true);
            Host.Children.Add(DetailHeader.Build(AudioUi.Art(show.ArtUrl, 180, AudioUi.PodcastGlyph), "Podcast", show.Title, show.Author,
                string.Join(" · ", show.Categories.Take(2).Append(AudioUi.Count(preview.Episodes.Count, "recent episode", "recent episodes"))), follow));
            if (!string.IsNullOrWhiteSpace(show.Description))
            {
                var description = AudioUi.Body(show.Description!);
                description.Margin = new Thickness(0, 0, 0, 12);
                Host.Children.Add(description);
            }
            Host.Children.Add(AudioUi.Heading("Latest episodes"));
            foreach (var episode in preview.Episodes.Take(30))
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(new TextBlock { Text = episode.Title, Foreground = Palette.PrimaryText, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = PodcastUi.Facts(episode, false), Foreground = Palette.SecondaryText, FontSize = 11.5 });
                if (!string.IsNullOrWhiteSpace(episode.Summary)) row.Children.Add(new TextBlock { Text = episode.Summary, Foreground = Palette.SecondaryText, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                Host.Children.Add(row);
            }
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }
}

/// <summary>
/// One episode: its show notes, chapters, transcript (with Transcribe when there is none),
/// bookmarks, and download and queue actions.
/// </summary>
public sealed class PodcastEpisodeView : AudioPage
{
    private readonly string _id;

    public PodcastEpisodeView(AppModel model, INavigator navigator, string id) : base(model, navigator)
    {
        _id = id;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading();
        try
        {
            var episode = await Model.Client.PodcastEpisodeAsync(_id);
            Host.Children.Clear();
            var context = new AudioContext { Type = "podcast", Id = episode.PodcastId, Name = episode.PodcastTitle };
            var show = new Button { Content = episode.PodcastTitle ?? "Show", Style = AudioUi.StyleOf("LinkButton"), FontSize = 14 };
            show.Click += (_, _) =>
            {
                if (episode.PodcastId is { } podcastId) AudioNav.OpenShow(Model, Navigator, podcastId, episode.PodcastTitle ?? "Show");
            };
            var menu = PodcastUi.EpisodeMenu(Model, Navigator, episode, () => _ = LoadAsync());
            var more = AudioUi.TextButton("More ▾", () => { });
            more.Click += (_, _) =>
            {
                menu.PlacementTarget = more;
                menu.IsOpen = true;
            };
            Host.Children.Add(DetailHeader.Build(AudioUi.Art(episode.ArtUrl, 180, AudioUi.PodcastGlyph), "Episode", episode.Title, null,
                PodcastUi.Facts(episode, false), show,
                AudioUi.TextButton(episode.IsInProgress ? $"▶  Resume ({AudioTime.Left(episode.RemainingSeconds)})" : "▶  Play",
                    () => _ = Player.PlayEpisodesAsync(new[] { episode }, 0, context), primary: true),
                AudioUi.TextButton("From the start", () => _ = Player.PlayEpisodesAsync(new[] { episode }, 0, context, 0)),
                AudioUi.TextButton(episode.InQueue ? "In Up Next ✓" : "Add to Up Next", () => _ = Try(async () =>
                {
                    if (episode.InQueue) await Model.Client.RemoveFromPodcastQueueAsync(episode.Id);
                    else await Model.Client.AddToPodcastQueueAsync(episode.Id, "last");
                    await LoadAsync();
                })),
                more));

            if (episode.Download is { TimeSavedSeconds: > 1 } saved)
            {
                Host.Children.Add(AudioUi.Caption($"Trimming silence saved {AudioTime.Length(saved.TimeSavedSeconds)} on this episode.", 0));
            }

            Host.Children.Add(AudioUi.Heading("Show notes", 4));
            Host.Children.Add(ShowNotes(episode));

            if (episode.HasChapters)
            {
                var chapters = await Model.Client.EpisodeChaptersAsync(episode.Id);
                if (chapters.Count > 0)
                {
                    Host.Children.Add(AudioUi.Heading("Chapters"));
                    foreach (var chapter in chapters)
                    {
                        var target = chapter;
                        var row = new TextBlock { Text = $"{AudioTime.Clock(chapter.StartSeconds)}   {chapter.Title}", Foreground = Palette.PrimaryText };
                        Host.Children.Add(AudioUi.ClickableRow(row, () => _ = Player.PlayEpisodesAsync(new[] { episode }, 0, context, target.StartSeconds), null, chapter.Title));
                    }
                }
            }

            Host.Children.Add(AudioUi.Heading("Transcript"));
            if (episode.HasTranscript)
            {
                Host.Children.Add(AudioUi.TextButton("Open the transcript", () => _ = ShowTranscriptAsync(episode, context), margin: new Thickness(0, 0, 0, 6)));
            }
            else
            {
                Host.Children.Add(AudioUi.Caption("No transcript yet."));
                var transcribe = AudioUi.TextButton("Transcribe", () => _ = Try(async () =>
                {
                    await Model.Client.TranscribeEpisodeAsync(episode.Id);
                    Notice("Queued for transcription on the server. It takes a while for a long episode.");
                }), "Write a transcript on the server with whisper (downloads the episode first)");
                transcribe.HorizontalAlignment = HorizontalAlignment.Left;
                Host.Children.Add(transcribe);
            }

            var bookmarks = await Model.Client.BookmarksAsync(episode.Id);
            Host.Children.Add(AudioUi.Heading("Bookmarks"));
            if (bookmarks.Count == 0) Host.Children.Add(AudioUi.Caption("None yet - press the bookmark button while it plays."));
            foreach (var bookmark in bookmarks.OrderBy(b => b.PositionSeconds))
            {
                var target = bookmark;
                Host.Children.Add(BookmarkUi.Row(Model, bookmark, () => _ = Player.PlayEpisodesAsync(new[] { episode }, 0, context, target.PositionSeconds), () => _ = LoadAsync(), showEpisode: false));
            }
        }
        catch (Exception error)
        {
            Host.Children.Clear();
            Fail(error);
        }
    }

    private async Task ShowTranscriptAsync(PodcastEpisode episode, AudioContext context) => await Try(async () =>
    {
        var transcript = await Model.Client.EpisodeTranscriptAsync(episode.Id);
        Navigator.Push(new TranscriptReaderView(Model, Navigator, episode, transcript, context), $"Transcript · {episode.Title}");
    });

    /// <summary>
    /// The sanitized show-notes HTML (§5: p, br, a, b, i, em, strong, ul, ol, li, blockquote) as
    /// WPF text with working links, falling back to the plain text.
    /// </summary>
    private static FrameworkElement ShowNotes(PodcastEpisodeDetail episode)
    {
        var block = new TextBlock { Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        if (string.IsNullOrWhiteSpace(episode.Description))
        {
            block.Text = episode.DescriptionText ?? episode.Summary ?? "";
            return block;
        }
        ShowNotesHtml.Fill(block.Inlines, episode.Description!);
        return block;
    }
}

/// <summary>Turns the server's small, sanitized show-notes HTML into WPF inlines.</summary>
internal static partial class ShowNotesHtml
{
    [GeneratedRegex(@"<(/?)(\w+)([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    public static void Fill(InlineCollection inlines, string html)
    {
        var bold = 0;
        var italic = 0;
        string? link = null;
        var position = 0;
        foreach (Match match in Tag().Matches(html))
        {
            AddText(html[position..match.Index]);
            position = match.Index + match.Length;
            var closing = match.Groups[1].Value == "/";
            var name = match.Groups[2].Value.ToLowerInvariant();
            switch (name)
            {
                case "b" or "strong":
                    bold += closing ? -1 : 1;
                    break;
                case "i" or "em":
                    italic += closing ? -1 : 1;
                    break;
                case "a":
                    link = closing ? null : Href().Match(match.Groups[3].Value) is { Success: true } href ? WebUtility.HtmlDecode(href.Groups[1].Value) : null;
                    break;
                case "br":
                    inlines.Add(new LineBreak());
                    break;
                case "p" or "ul" or "ol" or "blockquote":
                    if (closing || inlines.Count > 0)
                    {
                        inlines.Add(new LineBreak());
                        if (name == "p" && closing) inlines.Add(new LineBreak());
                    }
                    break;
                case "li":
                    if (!closing) inlines.Add(new Run("  •  "));
                    else inlines.Add(new LineBreak());
                    break;
            }
        }
        AddText(html[position..]);

        void AddText(string raw)
        {
            if (raw.Length == 0) return;
            var text = WebUtility.HtmlDecode(raw).Replace("\r", "").Replace("\n", " ");
            if (text.Length == 0) return;
            var run = new Run(text);
            if (bold > 0) run.FontWeight = FontWeights.SemiBold;
            if (italic > 0) run.FontStyle = FontStyles.Italic;
            if (link is not null && (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                var target = link;
                var hyperlink = new Hyperlink(run) { Foreground = Palette.Cyan, ToolTip = target };
                hyperlink.Click += (_, _) => PodcastUi.OpenLink(target);
                inlines.Add(hyperlink);
            }
            else
            {
                inlines.Add(run);
            }
        }
    }
}

/// <summary>A whole transcript to read and search, outside the player; double-click a line to play from it.</summary>
public sealed class TranscriptReaderView : AudioPage
{
    public TranscriptReaderView(AppModel model, INavigator navigator, PodcastEpisode episode, PodcastTranscript transcript, AudioContext context)
        : base(model, navigator)
    {
        var search = new TextBox { Width = 280, ToolTip = "Find in this transcript" };
        var list = new StackPanel();
        void Draw()
        {
            list.Children.Clear();
            var hits = search.Text.Trim().Length > 0 ? TranscriptTimeline.Search(transcript.Segments, search.Text).ToHashSet() : null;
            for (var i = 0; i < transcript.Segments.Count; i++)
            {
                if (hits is not null && !hits.Contains(i)) continue;
                var segment = transcript.Segments[i];
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.PrimaryText };
                text.Inlines.Add(new Run($"{AudioTime.Clock(segment.Start)}  ") { Foreground = Palette.Cyan });
                if (!string.IsNullOrEmpty(segment.Speaker)) text.Inlines.Add(new Run($"{segment.Speaker}: ") { FontWeight = FontWeights.SemiBold });
                text.Inlines.Add(new Run(segment.Text));
                var start = segment.Start;
                list.Children.Add(AudioUi.ClickableRow(text, () => _ = PlayerController.Shared!.PlayEpisodesAsync(new[] { episode }, 0, context, start), null, segment.Text));
            }
            if (hits is { Count: 0 }) list.Children.Add(AudioUi.Caption("Not found in this transcript."));
        }
        search.TextChanged += (_, _) => Draw();
        Host.Children.Add(AudioUi.Caption($"{(transcript.Source == "whisper" ? "Transcribed on the server" : "From the show's feed")}{(string.IsNullOrEmpty(transcript.Language) ? "" : $" · {transcript.Language}")} · double-click a line to play from it"));
        Host.Children.Add(AudioUi.Row(search));
        Host.Children.Add(list);
        Draw();
    }
}

/// <summary>A show's settings: the household's (admin) and this person's own.</summary>
public sealed class PodcastSettingsWindow : AudioDialog
{
    private readonly Podcast _show;
    private readonly CheckBox _autoDownload = new() { Content = "Download new episodes onto the server" };
    private readonly TextBox _keep = new() { Width = 80 };
    private readonly CheckBox _voiceBoost = new() { Content = "Voice boost - even out loudness and make voices clearer" };
    private readonly CheckBox _trimSilence = new() { Content = "Trim silence - cut pauses longer than a second" };
    private readonly CheckBox _transcribe = new() { Content = "Write a transcript of each new episode" };
    private readonly ComboBox _speed = new() { Width = 120 };
    private readonly TextBox _intro = new() { Width = 80 };
    private readonly TextBox _outro = new() { Width = 80 };
    private readonly CheckBox _notify = new() { Content = "Tell me when a new episode arrives" };
    private readonly CheckBox _autoQueue = new() { Content = "Add new episodes to Up Next" };
    private readonly CheckBox _briefing = new() { Content = "Include the newest episode in the Morning Briefing" };
    private readonly ComboBox _sort = new() { Width = 140, ItemsSource = new[] { "Newest first", "Oldest first" } };

    public PodcastSettingsWindow(AppModel model, Podcast show) : base(model, $"{show.Title} settings", null, 600, 640)
    {
        _show = show;
        var mine = show.MySettings ?? new PodcastMySettings();
        Body.Children.Add(AudioUi.Heading("For you", 0));
        var speeds = new List<string> { $"App default ({PodcastPlayback.SpeedLabel(PlayerController.Shared?.DefaultPodcastSpeed ?? 1)})" };
        speeds.AddRange(PodcastPlayback.Speeds.Select(PodcastPlayback.SpeedLabel));
        _speed.ItemsSource = speeds;
        _speed.SelectedIndex = mine.PlaybackSpeed is { } speed ? Math.Max(0, PodcastPlayback.Speeds.ToList().FindIndex(s => Math.Abs(s - speed) < 0.01) + 1) : 0;
        Body.Children.Add(Label("Playback speed"));
        Body.Children.Add(_speed);
        _intro.Text = mine.SkipIntroSeconds.ToString();
        _outro.Text = mine.SkipOutroSeconds.ToString();
        Body.Children.Add(Label("Skip the first … seconds of each episode"));
        Body.Children.Add(_intro);
        Body.Children.Add(Label("Skip the last … seconds"));
        Body.Children.Add(_outro);
        _notify.IsChecked = mine.Notify;
        _autoQueue.IsChecked = mine.AutoQueue;
        _briefing.IsChecked = mine.InBriefing;
        foreach (var box in new[] { _notify, _autoQueue, _briefing })
        {
            box.Margin = new Thickness(0, 10, 0, 0);
            Body.Children.Add(box);
        }
        _sort.SelectedIndex = mine.Sort == "oldest" ? 1 : 0;
        Body.Children.Add(Label("Episode order"));
        Body.Children.Add(_sort);

        var household = show.Settings ?? new PodcastSettings();
        Body.Children.Add(AudioUi.Heading("For the household"));
        if (!model.IsAdmin) Body.Children.Add(AudioUi.Caption("Only an admin can change these."));
        _autoDownload.IsChecked = household.AutoDownload;
        _keep.Text = household.KeepEpisodes.ToString();
        _voiceBoost.IsChecked = household.VoiceBoost;
        _trimSilence.IsChecked = household.TrimSilence;
        _transcribe.IsChecked = household.Transcribe;
        Body.Children.Add(_autoDownload);
        Body.Children.Add(Label("Keep the newest … downloaded episodes (0 = all)"));
        Body.Children.Add(_keep);
        foreach (var box in new[] { _voiceBoost, _trimSilence, _transcribe })
        {
            box.Margin = new Thickness(0, 10, 0, 0);
            Body.Children.Add(box);
        }
        foreach (var control in new Control[] { _autoDownload, _keep, _voiceBoost, _trimSilence, _transcribe }) control.IsEnabled = model.IsAdmin;

        AddFooterButton("Cancel", Close, isCancel: true);
        AddFooterButton("Save", () => _ = SaveAsync(), primary: true);
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(_intro.Text.Trim(), out var intro) || !int.TryParse(_outro.Text.Trim(), out var outro) || intro < 0 || outro < 0)
        {
            Message("Skip times are whole numbers of seconds.", error: true);
            return;
        }
        var mine = new PodcastMySettings
        {
            PlaybackSpeed = _speed.SelectedIndex <= 0 ? null : PodcastPlayback.Speeds[_speed.SelectedIndex - 1],
            SkipIntroSeconds = intro,
            SkipOutroSeconds = outro,
            Notify = _notify.IsChecked == true,
            AutoQueue = _autoQueue.IsChecked == true,
            InBriefing = _briefing.IsChecked == true,
            Sort = _sort.SelectedIndex == 1 ? "oldest" : "newest",
        };
        try
        {
            var id = _show.Id!;
            await Model.Client.SavePodcastMySettingsAsync(id, mine);
            PlayerController.Shared?.ShowSettingsChanged(id, mine);
            if (Model.IsAdmin)
            {
                if (!int.TryParse(_keep.Text.Trim(), out var keep) || keep < 0)
                {
                    Message("Keep has to be a whole number (0 keeps every episode).", error: true);
                    return;
                }
                await Model.Client.SavePodcastSettingsAsync(id, new PodcastSettings
                {
                    AutoDownload = _autoDownload.IsChecked == true,
                    KeepEpisodes = keep,
                    VoiceBoost = _voiceBoost.IsChecked == true,
                    TrimSilence = _trimSilence.IsChecked == true,
                    Transcribe = _transcribe.IsChecked == true,
                });
            }
            Done();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}

/// <summary>Makes a clip of the episode playing (5-180 s), then offers to save the file.</summary>
public sealed class PodcastClipWindow : AudioDialog
{
    private readonly PodcastEpisode _episode;
    private readonly TextBox _start = new() { Width = 100 };
    private readonly ComboBox _length = new() { Width = 140, ItemsSource = new[] { "15 seconds", "30 seconds", "45 seconds", "1 minute", "90 seconds", "2 minutes", "3 minutes" } };
    private static readonly double[] Lengths = { 15, 30, 45, 60, 90, 120, 180 };
    private readonly TextBox _title = new();

    public PodcastClipWindow(AppModel model, PodcastEpisode episode, double position)
        : base(model, "Make a clip", $"A short piece of {episode.Title} as an audio file, to keep or send to someone.", 520, 420)
    {
        _episode = episode;
        // Default: the 30 seconds leading up to now - the bit that was just worth clipping.
        _start.Text = AudioTime.Clock(Math.Max(0, position - 30));
        _length.SelectedIndex = 1;
        Body.Children.Add(Label("Start (m:ss or h:mm:ss)"));
        Body.Children.Add(_start);
        Body.Children.Add(Label("Length"));
        Body.Children.Add(_length);
        Body.Children.Add(Label("Title"));
        Body.Children.Add(_title);
        AddFooterButton("Cancel", Close, isCancel: true);
        AddFooterButton("Make clip", () => _ = MakeAsync(), primary: true);
    }

    internal static double? ParseClock(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0) return null;
            total = total * 60 + value;
        }
        return total;
    }

    private async Task MakeAsync()
    {
        if (ParseClock(_start.Text) is not { } start)
        {
            Message("Type the start as minutes and seconds, like 12:30.", error: true);
            return;
        }
        Message("Cutting the clip on the server…");
        try
        {
            var clip = await Model.Client.CreateClipAsync(_episode.Id, start, Lengths[Math.Max(0, _length.SelectedIndex)], _title.Text.Trim());
            Done();
            await AudioUi.SaveFileAsync(clip.Url, string.IsNullOrWhiteSpace(_title.Text) ? $"{_episode.PodcastTitle} clip" : _title.Text.Trim(), ".m4a");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}
