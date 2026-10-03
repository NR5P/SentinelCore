using System.Text;
using System.Text.Json;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Subscribing, refreshing, downloading and listening, against a publisher on loopback and real
// ffmpeg (skipped without it), with the services wired the way Program.cs wires them.
public sealed class PodcastServiceTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly PodcastTestServer _server = new();
    private readonly List<PodcastTestKit> _kits = [];

    public void Dispose()
    {
        _server.Dispose();
        foreach (var kit in _kits) kit.Dispose();
    }

    private PodcastTestKit Kit(string settings = """{ "AutoDownloadNew": false }""")
    {
        var kit = new PodcastTestKit(settings);
        _kits.Add(kit);
        return kit;
    }

    private string Items(params int[] numbers) =>
        string.Join("\n", numbers.Select(n => PodcastFeeds.Item($"ep-{n}", $"Episode {n}", Day.AddDays(n), _server.Url($"/audio/{n}.mp3"))));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EpisodesThatLeaveTheFeed_AreArchived_NeverDeleted_AndComeBackIfTheyReturn()
    {
        var kit = Kit();
        _server.Feed("/feed.xml", PodcastFeeds.Rss("Archive Show", Items(1, 2, 3)));
        var result = await kit.Service.SubscribeAsync("nathan", _server.Url("/feed.xml"), null, Ct);
        Assert.Null(result.Error);
        var show = result.Show!;
        Assert.Equal("Archive Show", show.Title);
        Assert.Equal(3, (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Count);

        _server.Feed("/feed.xml", PodcastFeeds.Rss("Archive Show", Items(2, 3, 4)));
        await kit.Service.RefreshAsync(show.Id, Ct);
        var episodes = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).ToDictionary(e => e.FeedGuid);
        Assert.Equal(4, episodes.Count);
        Assert.True(episodes["ep-1"].Archived);
        Assert.False(episodes["ep-4"].Archived);

        // A feed that comes back empty (broken for the moment) archives nothing.
        _server.Feed("/feed.xml", PodcastFeeds.Rss("Archive Show", ""));
        await kit.Service.RefreshAsync(show.Id, Ct);
        Assert.Single(await kit.Store.GetEpisodesAsync(show.Id, Ct), e => e.Archived);

        _server.Feed("/feed.xml", PodcastFeeds.Rss("Archive Show", Items(1, 2, 3, 4)));
        await kit.Service.RefreshAsync(show.Id, Ct);
        Assert.DoesNotContain(await kit.Store.GetEpisodesAsync(show.Id, Ct), e => e.Archived);
    }

    [Fact]
    public async Task EpisodesWithoutAGuid_KeepTheirIdsAcrossRefreshes()
    {
        var kit = Kit();
        var items = """
            <item><title>No guid one</title><enclosure url="https://cdn.example.com/a.mp3" type="audio/mpeg"/></item>
            <item><title>No guid two</title><enclosure url="https://cdn.example.com/b.mp3" type="audio/mpeg"/></item>
            """;
        _server.Feed("/noguid.xml", PodcastFeeds.Rss("No Guids", items));
        var show = (await kit.Service.SubscribeAsync("", _server.Url("/noguid.xml"), null, Ct)).Show!;
        var before = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Select(e => e.Id).Order().ToList();
        await kit.Service.RefreshAsync(show.Id, Ct);
        var after = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Select(e => e.Id).Order().ToList();
        Assert.Equal(before, after);
        Assert.Equal(PodcastService.EpisodeId(show.Id, "https://cdn.example.com/a.mp3"), (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Single(e => e.Title == "No guid one").Id);
    }

    [Fact]
    public async Task ARefresh_SendsTheETag_AndA304ChangesNothing()
    {
        var kit = Kit();
        _server.Feed("/etag.xml", PodcastFeeds.Rss("ETag Show", Items(1)), etag: "\"v1\"", gzip: true);
        var show = (await kit.Service.SubscribeAsync("", _server.Url("/etag.xml"), null, Ct)).Show!;
        Assert.Equal("\"v1\"", (await kit.Store.GetShowAsync(show.Id, Ct))!.ETag);

        var firstRefresh = (await kit.Store.GetShowAsync(show.Id, Ct))!.LastRefreshedAt;
        await Task.Delay(20, Ct);
        var refreshed = await kit.Service.RefreshAsync(show.Id, Ct);
        Assert.NotNull(refreshed);
        Assert.Null(refreshed.RefreshError);
        Assert.True(refreshed.LastRefreshedAt > firstRefresh);
        Assert.Single(await kit.Store.GetEpisodesAsync(show.Id, Ct));
        Assert.Equal(2, _server.Hits["/etag.xml"]);
    }

    [Fact]
    public async Task AMovedFeed_IsFollowed_ByNewFeedUrlAndByPermanentRedirect()
    {
        var kit = Kit();
        _server.Feed("/new.xml", PodcastFeeds.Rss("Moved Show", Items(1, 2)));
        _server.Feed("/old.xml", PodcastFeeds.Rss("Moved Show", Items(1), $"<itunes:new-feed-url>{_server.Url("/new.xml")}</itunes:new-feed-url>"));
        var show = (await kit.Service.SubscribeAsync("", _server.Url("/old.xml"), null, Ct)).Show!;
        Assert.Equal(_server.Url("/new.xml"), show.FeedUrl);
        await kit.Service.RefreshAsync(show.Id, Ct);
        Assert.Equal(2, (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Count);
        Assert.True(_server.Hits.GetValueOrDefault("/new.xml") >= 1);

        _server.Redirect("/redirected.xml", _server.Url("/target.xml"), 301);
        _server.Feed("/target.xml", PodcastFeeds.Rss("Redirected Show", Items(5)));
        var redirected = (await kit.Service.SubscribeAsync("", _server.Url("/redirected.xml"), null, Ct)).Show!;
        Assert.Equal(_server.Url("/target.xml"), redirected.FeedUrl);

        // A temporary redirect is followed but not remembered.
        _server.Redirect("/temporary.xml", _server.Url("/target2.xml"), 302);
        _server.Feed("/target2.xml", PodcastFeeds.Rss("Temp Show", Items(6)));
        var temporary = (await kit.Service.SubscribeAsync("", _server.Url("/temporary.xml"), null, Ct)).Show!;
        Assert.Equal(_server.Url("/temporary.xml"), temporary.FeedUrl);
    }

    [Fact]
    public async Task ABrokenFeed_IsRefusedWithAReason()
    {
        var kit = Kit();
        _server.Route("/page.html", _ => new PodcastTestServer.Reply(200, Encoding.UTF8.GetBytes("<html><body>Hi</body></html>"), "text/html", []));
        var page = await kit.Service.SubscribeAsync("", _server.Url("/page.html"), null, Ct);
        Assert.Null(page.Show);
        Assert.Contains("web page", page.Error);
        var missing = await kit.Service.SubscribeAsync("", _server.Url("/nothing.xml"), null, Ct);
        Assert.Contains("404", missing.Error);
    }

    [Fact]
    public async Task NewEpisodes_PushOncePerShow_OnlyToFollowersWithNotifyOn_AndNeverOnTheFirstFetch()
    {
        var kit = Kit();
        _server.Feed("/push.xml", PodcastFeeds.Rss("Push Show", Items(1, 2)));
        var show = (await kit.Service.SubscribeAsync("nathan", _server.Url("/push.xml"), null, Ct)).Show!;
        await kit.Service.SubscribeAsync("anna", _server.Url("/push.xml"), null, Ct);
        await kit.Service.SubscribeAsync("quiet", _server.Url("/push.xml"), null, Ct);
        var quiet = (await kit.Store.GetSubscriptionsAsync(Ct)).Single(s => s.UserKey == "quiet");
        await kit.Store.SaveSubscriptionAsync(quiet with { Settings = new PodcastMySettings { Notify = false } }, Ct);
        var anna = (await kit.Store.GetSubscriptionsAsync(Ct)).Single(s => s.UserKey == "anna");
        await kit.Store.SaveSubscriptionAsync(anna with { Settings = new PodcastMySettings { AutoQueue = true } }, Ct);
        Assert.Empty(kit.Notifier.Sent);

        await kit.Service.RefreshAsync(show.Id, Ct);
        Assert.Empty(kit.Notifier.Sent);

        _server.Feed("/push.xml", PodcastFeeds.Rss("Push Show", Items(1, 2, 3, 4)));
        await kit.Service.RefreshAsync(show.Id, Ct);
        var push = Assert.Single(kit.Notifier.Sent);
        Assert.Equal(["anna", "nathan"], push.Users.Order());
        Assert.Equal(["Episode 4", "Episode 3"], push.Episodes.Select(e => e.Title));

        // Anna's Up Next gets them oldest first.
        var queue = await kit.Store.GetQueueAsync("anna", Ct);
        var titles = (await kit.Store.GetEpisodesAsync(queue.Select(q => q.EpisodeId), Ct)).ToDictionary(e => e.Id, e => e.Title);
        Assert.Equal(["Episode 3", "Episode 4"], queue.Select(q => titles[q.EpisodeId]));
        Assert.Empty(await kit.Store.GetQueueAsync("nathan", Ct));

        await kit.Service.RefreshAsync(show.Id, Ct);
        Assert.Single(kit.Notifier.Sent);
    }

    [Fact]
    public async Task Progress_FollowsThePlayedRules_AndCountsListeningTime()
    {
        var kit = Kit();
        _server.Feed("/listen.xml", PodcastFeeds.Rss("Listen Show", Items(1)));
        var show = (await kit.Service.SubscribeAsync("nathan", _server.Url("/listen.xml"), null, Ct)).Show!;
        var episode = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Single();
        await kit.Store.SetQueueAsync("nathan", [episode.Id], Ct);

        Assert.Null(await kit.Service.ReportProgressAsync("nathan", episode, new PodcastProgressInput { PositionSeconds = 9 }, Ct));
        var halfway = await kit.Service.ReportProgressAsync("nathan", episode, new PodcastProgressInput { PositionSeconds = 300, DurationSeconds = 600, Speed = 1.5 }, Ct);
        Assert.False(halfway!.Played);
        var end = await kit.Service.ReportProgressAsync("nathan", episode, new PodcastProgressInput { PositionSeconds = 571, DurationSeconds = 600 }, Ct);
        Assert.True(end!.Played);
        Assert.NotNull(end.PlayedAt);
        Assert.Empty(await kit.Store.GetQueueAsync("nathan", Ct));

        var listening = await kit.Store.GetListeningAsync("nathan", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct);
        // The first report counts at most 15 s of listening (22.5 s of audio at 1.5x, saving 7.5 s);
        // the jump to the end a moment later is a seek, not 271 s of listening.
        var row = Assert.Single(listening);
        Assert.Equal(15, row.ListenedSeconds, 3);
        Assert.Equal(7.5, row.SavedSeconds, 3);

        var source = new PodcastAudioSource(kit.Store, kit.Downloads, kit.Settings);
        var stats = JsonSerializer.SerializeToElement(await source.StatsAsync("nathan", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), Ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(1, stats.GetProperty("episodesFinished").GetInt32());
        Assert.Equal(24, stats.GetProperty("byHour").GetArrayLength());
        Assert.Equal(7, stats.GetProperty("byWeekday").GetArrayLength());
        Assert.Equal("Listen Show", stats.GetProperty("topShows")[0].GetProperty("podcast").GetProperty("title").GetString());
    }

    [Fact]
    public void StorageCap_RemovesPlayedFirst_ThenUnqueued_OldestFirst_NeverTheNewDownload()
    {
        static PodcastDownloadManager.StorageCandidate C(int n, bool played = false, bool queued = false) =>
            new(new Guid(n, 0, 0, new byte[8]), 100, Day.AddDays(n), played, queued);
        var downloads = new[] { C(1, queued: true), C(2), C(3, played: true), C(4), C(5) };
        Assert.Empty(PodcastDownloadManager.ChooseEvictions(downloads, 500, null));
        Assert.Equal([C(3).EpisodeId], PodcastDownloadManager.ChooseEvictions(downloads, 400, null));
        Assert.Equal([C(3).EpisodeId, C(2).EpisodeId, C(4).EpisodeId], PodcastDownloadManager.ChooseEvictions(downloads, 200, keep: null));
        Assert.Equal([C(3).EpisodeId, C(2).EpisodeId, C(4).EpisodeId, C(1).EpisodeId], PodcastDownloadManager.ChooseEvictions(downloads, 50, keep: C(5).EpisodeId));
    }

    [Fact]
    public async Task Downloads_KeepTheNewestN_AndResumeFromAPartFile()
    {
        var kit = Kit();
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
        foreach (var n in new[] { 1, 2, 3 }) _server.Bytes($"/audio/{n}.mp3", content, "audio/mpeg");
        _server.Feed("/dl.xml", PodcastFeeds.Rss("Download Show", Items(1, 2, 3)));
        var show = (await kit.Service.SubscribeAsync("nathan", _server.Url("/dl.xml"), null, Ct)).Show!;
        show.Settings.KeepEpisodes = 2;
        await kit.Store.SaveShowAsync(show, Ct);
        var episodes = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).OrderBy(e => e.PublishedAt).ToList();

        // Half of episode 1 is already on disk from a download that was interrupted.
        var folder = Path.Combine(kit.Locations.DefaultDownloadFolder, show.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{episodes[0].Id:N}.mp3.part"), content[..100_000], Ct);

        await kit.Downloads.RequestAsync(episodes[0].Id, false, Ct);
        await PodcastTestKit.WaitForAsync(async () => (await kit.Store.GetDownloadAsync(episodes[0].Id, Ct))?.IsDone == true, "the first download");
        Assert.Contains("/audio/1.mp3 bytes=100000-", _server.RangeRequests);
        var first = (await kit.Store.GetDownloadAsync(episodes[0].Id, Ct))!;
        Assert.Equal(content, await File.ReadAllBytesAsync(first.FilePath!, Ct));
        Assert.False(File.Exists(first.FilePath + ".part"));

        await kit.Downloads.RequestAsync(episodes[1].Id, false, Ct);
        await kit.Downloads.RequestAsync(episodes[2].Id, false, Ct);
        await PodcastTestKit.WaitForAsync(async () =>
            (await kit.Store.GetDownloadAsync(episodes[2].Id, Ct))?.IsDone == true && (await kit.Store.GetDownloadAsync(episodes[1].Id, Ct))?.IsDone == true
            && (await kit.Store.GetDownloadAsync(episodes[0].Id, Ct))?.State == PodcastDownloadStates.None, "keep-2 to clear the oldest");
        Assert.False(File.Exists(first.FilePath));
    }

    [Fact]
    public async Task TrimSilence_CutsTheSilences_AndMovesChaptersProgressBookmarksAndWhisperTimes()
    {
        if (!await PodcastTestKit.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        var kit = Kit("""{ "AutoDownloadNew": false, "TranscriptsEnabled": true }""");
        _server.Bytes("/audio/1.mp3", await kit.MakeEpisodeAudioAsync(chapters: true), "audio/mpeg");
        _server.Feed("/trim.xml", PodcastFeeds.Rss("Trim Show", PodcastFeeds.Item("ep-1", "Episode 1", Day, _server.Url("/audio/1.mp3"), duration: "49")));
        var show = (await kit.Service.SubscribeAsync("nathan", _server.Url("/trim.xml"), null, Ct)).Show!;
        show.Settings.TrimSilence = true;
        show.Settings.VoiceBoost = true;
        await kit.Store.SaveShowAsync(show, Ct);
        var episode = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Single();

        // Listened to (and bookmarked) from the publisher's file before the download existed.
        Assert.NotNull(await kit.Service.ReportProgressAsync("nathan", episode, new PodcastProgressInput { PositionSeconds = 12, DurationSeconds = 49 }, Ct));
        await kit.Store.SaveBookmarkAsync(new PodcastBookmark(Guid.NewGuid(), "nathan", episode.Id, 7.5, "the high tone", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Ct);

        Assert.Null(await kit.Service.TranscribeAsync(episode.Id, Ct));
        await PodcastTestKit.WaitForAsync(async () => await kit.Store.GetTranscriptAsync(episode.Id, Ct) is not null, "download, trim and transcribe", 120);

        episode = (await kit.Store.GetEpisodeAsync(episode.Id, Ct))!;
        var download = episode.Download!;
        Assert.True(download.Enhanced);
        Assert.Equal(2, download.Cuts.Count);
        Assert.Equal(2.15, download.Cuts[0].Start, 1);
        Assert.Equal(4.85, download.Cuts[0].End, 1);
        Assert.Equal(7.15, download.Cuts[1].Start, 1);
        Assert.Equal(8.85, download.Cuts[1].End, 1);
        Assert.Equal(4.4, download.TimeSavedSeconds, 1);
        Assert.InRange(download.FileDurationSeconds!.Value, 44.45, 44.75);
        Assert.Equal("audio/mp4", download.FileMimeType);

        // The file's own chapters were kept in the original timing and come out in the file's.
        var chapters = await kit.Service.ChaptersAsync(episode, Ct);
        Assert.Equal([0.0, 5.0], chapters.Select(c => Math.Round(c.StartSeconds, 1)));
        Assert.Equal(2.3, episode.Timeline.ToFile(chapters[1].StartSeconds), 1);

        var view = await kit.ViewAsync("nathan");
        var progress = JsonSerializer.SerializeToElement(PodcastView.Progress(view.ProgressOf(episode.Id), episode));
        Assert.Equal(7.6, progress.GetProperty("positionSeconds").GetDouble(), 1);
        Assert.InRange(progress.GetProperty("durationSeconds").GetDouble(), 44.45, 44.75);
        var bookmark = JsonSerializer.SerializeToElement(view.Bookmark((await kit.Store.GetBookmarksAsync("nathan", Ct)).Single(), episode));
        // 7.5 s was inside the second silence: it lands on the join, 7.15 - 2.7 = 4.45 s.
        Assert.InRange(bookmark.GetProperty("positionSeconds").GetDouble(), 4.4, 4.5);

        // whisper heard the trimmed file; its 2.3 s is the original's 5.0 s.
        var transcript = (await kit.Store.GetTranscriptAsync(episode.Id, Ct))!;
        Assert.Equal("whisper", transcript.Source);
        Assert.Equal(5.0, transcript.Segments[1].Start, 1);
        Assert.Equal(1, kit.Whisper.Runs);
    }

    [Fact]
    public async Task VoiceBoostAlone_KeepsTheLength()
    {
        if (!await PodcastTestKit.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        var kit = Kit();
        _server.Bytes("/audio/2.mp3", await kit.MakeEpisodeAudioAsync(), "audio/mpeg");
        _server.Feed("/boost.xml", PodcastFeeds.Rss("Boost Show", PodcastFeeds.Item("ep-2", "Episode 2", Day, _server.Url("/audio/2.mp3"), duration: "49")));
        var show = (await kit.Service.SubscribeAsync("", _server.Url("/boost.xml"), null, Ct)).Show!;
        show.Settings.VoiceBoost = true;
        await kit.Store.SaveShowAsync(show, Ct);
        var episode = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).Single();
        await kit.Downloads.RequestAsync(episode.Id, false, Ct);
        await PodcastTestKit.WaitForAsync(async () => (await kit.Store.GetDownloadAsync(episode.Id, Ct))?.IsDone == true, "the voice-boosted download");
        var download = (await kit.Store.GetDownloadAsync(episode.Id, Ct))!;
        Assert.True(download.Enhanced);
        Assert.Empty(download.Cuts);
        Assert.InRange(download.FileDurationSeconds!.Value, 48.9, 49.1);
        Assert.Equal(0, download.TimeSavedSeconds);
    }

    [Fact]
    public async Task Transcripts_AreSearchable()
    {
        var kit = Kit();
        _server.Feed("/search.xml", PodcastFeeds.Rss("Bread Talk", Items(1, 2)));
        var show = (await kit.Service.SubscribeAsync("", _server.Url("/search.xml"), null, Ct)).Show!;
        var episodes = (await kit.Store.GetEpisodesAsync(show.Id, Ct)).OrderBy(e => e.PublishedAt).ToList();
        await kit.Store.SaveTranscriptAsync(episodes[0].Id, new PodcastTranscript("feed", "en",
        [
            new(0, 5, "Host", "Welcome back to the show."),
            new(600, 610, "Host", "Today it is all about sourdough starters and how to feed them."),
            new(610, 620, "Guest", "Rye flour makes a lively starter.")
        ]), Ct);
        await kit.Store.SaveTranscriptAsync(episodes[1].Id, new PodcastTranscript("whisper", "en", [new(0, 4, null, "Nothing about bread here, just crème brûlée.")]), Ct);

        var hits = await kit.Store.SearchTranscriptsAsync("sourdough", 50, Ct);
        var hit = Assert.Single(hits);
        Assert.Equal(episodes[0].Id, hit.EpisodeId);
        Assert.Contains("sourdough", hit.Snippet);

        Assert.Single(await kit.Store.SearchTranscriptsAsync("sourd", 50, Ct));          // prefix, as typed
        Assert.Single(await kit.Store.SearchTranscriptsAsync("creme brulee", 50, Ct));   // accents ignored
        Assert.Empty(await kit.Store.SearchTranscriptsAsync("sourdough brulee", 50, Ct)); // every word must match one block

        // Saving again replaces the index rather than doubling it.
        await kit.Store.SaveTranscriptAsync(episodes[0].Id, new PodcastTranscript("whisper", "en", [new(1, 2, null, "Only rye now.")]), Ct);
        Assert.Empty(await kit.Store.SearchTranscriptsAsync("sourdough", 50, Ct));
        Assert.Equal("whisper", (await kit.Store.TranscriptSourcesAsync(Ct))[episodes[0].Id]);
    }

    [Fact]
    public async Task Backup_RoundTrips_AndARestoreNeverRollsAnythingBack()
    {
        var source = Kit();
        _server.Feed("/backup.xml", PodcastFeeds.Rss("Backup Show", Items(1, 2)));
        var show = (await source.Service.SubscribeAsync("nathan", _server.Url("/backup.xml"), null, Ct)).Show!;
        show.Settings.TrimSilence = true;
        await source.Store.SaveShowAsync(show, Ct);
        var episodes = (await source.Store.GetEpisodesAsync(show.Id, Ct)).OrderBy(e => e.PublishedAt).ToList();
        _server.Feed("/backup.xml", PodcastFeeds.Rss("Backup Show", Items(2)));
        await source.Service.RefreshAsync(show.Id, Ct);
        await source.Service.ReportProgressAsync("nathan", episodes[1], new PodcastProgressInput { PositionSeconds = 120, DurationSeconds = 600 }, Ct);
        await source.Store.SetQueueAsync("nathan", [episodes[1].Id, episodes[0].Id], Ct);
        await source.Store.SaveBookmarkAsync(new PodcastBookmark(Guid.NewGuid(), "nathan", episodes[0].Id, 42, "archived but kept", "snip", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Ct);

        var backup = await new PodcastAudioSource(source.Store, source.Downloads, source.Settings).ExportAsync(Ct);
        Assert.Single(backup.ArchivedEpisodes!);
        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = JsonSerializer.Deserialize<PodcastBackup>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var target = Kit();
        // Something newer already on the target must survive the restore.
        await target.Store.SetProgressAsync(new PodcastProgress("nathan", episodes[1].Id, 500, 600, false, DateTimeOffset.UtcNow.AddHours(1)), Ct);
        var restore = new PodcastAudioSource(target.Store, target.Downloads, target.Settings);
        Assert.True(await restore.RestoreAsync(parsed, Ct) > 0);
        var again = await restore.RestoreAsync(parsed, Ct);

        var restoredShow = (await target.Store.GetShowAsync(show.Id, Ct))!;
        Assert.True(restoredShow.Settings.TrimSilence);
        Assert.Null(restoredShow.LastRefreshedAt);   // the next refresh is a first fetch: no pushes
        Assert.True((await target.Store.GetSubscriptionsAsync(Ct)).Single().Subscribed);
        Assert.Equal(500, (await target.Store.GetProgressAsync("nathan", Ct)).Single().PositionSeconds);
        Assert.Equal([episodes[1].Id, episodes[0].Id], (await target.Store.GetQueueAsync("nathan", Ct)).Select(q => q.EpisodeId));
        Assert.Single(await target.Store.GetBookmarksAsync("nathan", Ct));
        Assert.True((await target.Store.GetEpisodeAsync(episodes[0].Id, Ct))!.Archived);
        Assert.Equal(0, again);

        // The first refresh lands on the same episode ids and pushes nobody.
        await target.Service.RefreshAsync(show.Id, Ct);
        Assert.Equal(2, (await target.Store.GetEpisodesAsync(show.Id, Ct)).Count);
        Assert.Empty(target.Notifier.Sent);
    }
}
