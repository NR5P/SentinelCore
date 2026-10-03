using System.Net;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The Music &amp; Podcasts routes against a real loopback server, checked against the contract in
/// core/docs/MUSIC_AND_PODCASTS.md: paths, bodies, and that the documented shapes decode.
/// </summary>
public class AudioClientTests
{
    private static SentinelClient ClientFor(FakeServer server) => new(() => server.BaseUrl, NullSentinelLog.Instance);

    private const string TrackJson = """
        {"id":"t1","title":"Dreams","artist":"Fleetwood Mac","artists":["Fleetwood Mac"],"artistId":"a1",
         "albumArtist":"Fleetwood Mac","album":"Rumours","albumId":"al1","trackNumber":2,"discNumber":1,"year":1977,
         "genres":["Rock"],"durationSeconds":257.8,"codec":"flac","bitrate":900000,"sampleRate":44100,"bitDepth":16,
         "channels":2,"sizeBytes":29000000,"composer":null,"bpm":null,
         "replayGain":{"trackGain":-6.2,"trackPeak":0.98,"albumGain":-7.1,"albumPeak":1.0},
         "hasLyrics":true,"artUrl":"/api/music/albums/al1/art","streamUrl":"/api/music/tracks/t1/stream",
         "addedAt":"2026-01-01T00:00:00Z","loved":false,"rating":0,"playCount":12,"lastPlayedAt":"2026-09-01T00:00:00Z","path":null}
        """;

    private const string EpisodeJson = """
        {"id":"e1","podcastId":"p1","podcastTitle":"The Daily","title":"Ep 512","summary":"s","publishedAt":"2026-10-01T09:00:00Z",
         "durationSeconds":1780,"season":null,"episode":512,"episodeType":"full","explicit":false,"link":"https://x",
         "artUrl":"/api/podcasts/episodes/e1/art","audioUrl":"/api/podcasts/episodes/e1/audio","mimeType":"audio/mpeg",
         "sizeBytes":28000000,"download":{"state":"done","progress":1.0,"error":null,"enhanced":true,"timeSavedSeconds":40},
         "archived":false,"hasChapters":true,"hasTranscript":true,"transcriptSource":"feed",
         "progress":{"positionSeconds":300,"durationSeconds":1780,"percent":16.9,"played":false,"updatedAt":"2026-10-01T10:00:00Z"},
         "inQueue":true,"bookmarkCount":1}
        """;

    [Fact]
    public async Task Enabled_flags_decode_and_an_older_server_404s()
    {
        using var server = new FakeServer().OnGet("/api/audio/enabled", """{"music":true,"podcasts":false}""");
        using var client = ClientFor(server);
        var enabled = await client.AudioEnabledAsync();
        Assert.True(enabled.Music);
        Assert.False(enabled.Podcasts);

        using var old = new FakeServer();
        using var oldClient = ClientFor(old);
        await Assert.ThrowsAsync<SentinelServerException>(() => oldClient.AudioEnabledAsync());
    }

    [Fact]
    public async Task Track_decodes_with_replay_gain_and_stream_path_carries_the_chromium_codecs()
    {
        using var server = new FakeServer().OnGet("/api/music/tracks", $$"""{"items":[{{TrackJson}}],"total":1}""");
        using var client = ClientFor(server);

        var page = await client.MusicTracksAsync(q: "dre", loved: true, sort: "plays", order: "desc", limit: 50);

        var track = Assert.Single(page.Items);
        Assert.Equal(1, page.Total);
        Assert.Equal(-7.1, track.ReplayGain!.AlbumGain);
        Assert.Equal("Fleetwood Mac · Rumours", track.Subtitle);
        Assert.Equal("/api/music/tracks/t1/stream?codecs=mp3,aac,flac,opus,vorbis,wav", SentinelClient.TrackStreamPath(track));
        Assert.Equal("/api/music/tracks/t1/stream?codecs=mp3,aac,flac,opus,vorbis,wav&maxBitrate=128", SentinelClient.TrackStreamPath(track, 128));
        Assert.Equal("GET /api/music/tracks?q=dre&loved=true&sort=plays&order=desc&limit=50", server.Requests.Single());
    }

    [Fact]
    public async Task Home_decodes_every_row_and_missing_rows_are_empty()
    {
        var json = $$"""
            {"pinned":[],"recentlyPlayed":[{"kind":"mix","mix":{"id":"daily-1","name":"Daily Mix 1","artUrls":[],"seedType":"genre","seedId":"rock"} }],
             "recentlyAdded":[{"id":"al1","title":"Rumours","artist":"Fleetwood Mac","trackCount":11}],
             "mixes":[{"id":"loved","name":"Loved","artUrls":["/a"]}],
             "forgottenFavorites":[{{TrackJson}}],"onThisDay":{"label":"October 2, 2025","tracks":[{{TrackJson}}]},
             "decades":[{"decade":1990,"label":"90s","trackCount":812}],"trackCount":8123,"albumCount":702,"artistCount":410}
            """;
        using var server = new FakeServer().OnGet("/api/music/home", json);
        using var client = ClientFor(server);

        var home = await client.MusicHomeAsync();
        Assert.Equal("daily-1", home.RecentlyPlayed[0].Mix!.Id);
        Assert.Single(home.OnThisDay!.Tracks);
        Assert.Empty(home.HeavyRotation);
        Assert.Empty(home.LovedTracks);
        Assert.Equal(90, home.Decades[0].Decade - 1900);
        Assert.False(home.IsEmpty);
    }

    [Fact]
    public async Task Love_rating_played_and_now_playing_track_post_the_contract_bodies()
    {
        using var server = new FakeServer()
            .On("POST", "/api/music/tracks/t1/love", """{"loved":true}""")
            .On("POST", "/api/music/albums/al1/rating", """{"rating":4}""")
            .On("POST", "/api/music/tracks/t1/played", "", HttpStatusCode.NoContent)
            .On("POST", "/api/music/now-playing-track", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        Assert.True(await client.SetLovedAsync("tracks", "t1", true));
        Assert.Equal(4, await client.SetRatingAsync("albums", "al1", 9));
        await client.ReportTrackPlayedAsync("t1", ListenMeter.Report(181, true, false, DateTimeOffset.Parse("2026-10-02T10:00:00Z"), "Den PC"));
        await client.ReportNowPlayingTrackAsync("t1");

        Assert.Equal("""{"loved":true}""", server.Bodies[0]);
        Assert.Equal("""{"rating":5}""", server.Bodies[1]);
        Assert.Contains("\"secondsListened\":181", server.Bodies[2]);
        Assert.Contains("\"completed\":true", server.Bodies[2]);
        Assert.Contains("\"skipped\":false", server.Bodies[2]);
        Assert.Contains("\"deviceName\":\"Den PC\"", server.Bodies[2]);
        Assert.Equal("""{"trackId":"t1"}""", server.Bodies[3]);
    }

    [Fact]
    public async Task Playlist_routes_hit_the_contract_paths()
    {
        const string playlist = """{"id":"p1","name":"Road Trip","mine":true,"smart":false,"trackCount":2,"artUrls":["/a","/b"],"pinned":false}""";
        using var server = new FakeServer()
            .On("POST", "/api/music/playlists", playlist, HttpStatusCode.Created)
            .On("PUT", "/api/music/playlists/p1", playlist)
            .On("POST", "/api/music/playlists/p1/tracks", playlist)
            .On("DELETE", "/api/music/playlists/p1/tracks/e9", "", HttpStatusCode.NoContent)
            .On("PUT", "/api/music/playlists/p1/order", "", HttpStatusCode.NoContent)
            .On("POST", "/api/music/playlists/p1/pin", "{}")
            .On("POST", "/api/music/playlists/p1/duplicate", playlist, HttpStatusCode.Created)
            .On("DELETE", "/api/music/playlists/p1", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        var input = new MusicPlaylistInput { Name = "Road Trip", Shared = true, TrackIds = new() { "t1" } };
        var created = await client.CreateMusicPlaylistAsync(input);
        await client.UpdateMusicPlaylistAsync("p1", input);
        await client.AddToPlaylistAsync("p1", new[] { "t1", "t2" });
        await client.RemoveFromPlaylistAsync("p1", "e9");
        await client.ReorderPlaylistAsync("p1", new[] { "e2", "e1" });
        await client.PinPlaylistAsync("p1", true);
        await client.DuplicatePlaylistAsync("p1");
        await client.DeleteMusicPlaylistAsync("p1");

        Assert.True(created.CanEditTracks);
        Assert.Equal(
            new[]
            {
                "POST /api/music/playlists", "PUT /api/music/playlists/p1", "POST /api/music/playlists/p1/tracks",
                "DELETE /api/music/playlists/p1/tracks/e9", "PUT /api/music/playlists/p1/order", "POST /api/music/playlists/p1/pin",
                "POST /api/music/playlists/p1/duplicate", "DELETE /api/music/playlists/p1",
            },
            server.Requests);
        Assert.Contains("\"trackIds\":[\"t1\"]", server.Bodies[0]);
        Assert.DoesNotContain("trackIds", server.Bodies[1]); // PUT never sends tracks
        Assert.Equal("""{"trackIds":["t1","t2"]}""", server.Bodies[2]);
        Assert.Equal("""{"entryIds":["e2","e1"]}""", server.Bodies[4]);
        Assert.Equal("""{"pinned":true}""", server.Bodies[5]);
    }

    [Fact]
    public async Task Smart_playlist_rules_are_sent_with_typed_values()
    {
        using var server = new FakeServer().On("POST", "/api/music/playlists", """{"id":"s1","name":"Top","smart":true,"mine":true}""", HttpStatusCode.Created);
        using var client = ClientFor(server);

        var rules = new SmartRules { Match = "all", Rules = new() { SmartRuleCatalog.NewRule("rating") }, Sort = "rating", Order = "desc", Limit = 25 };
        var created = await client.CreateMusicPlaylistAsync(new MusicPlaylistInput { Name = "Top", Rules = rules });

        Assert.False(created.CanEditTracks);
        Assert.Contains("""{"match":"all","rules":[{"field":"rating","op":"is","value":4}],"sort":"rating","order":"desc","limit":25}""", server.Bodies[0]);
    }

    [Fact]
    public async Task Album_downloads_search_in_the_background_and_grab_by_search_id()
    {
        using var server = new FakeServer()
            .OnGet("/api/music/downloads/status", """{"enabled":true,"canDownload":true,"musicLibrary":true,"status":{"services":"running","vpn":{"state":"connected"}}}""")
            .On("POST", "/api/music/downloads/search", """{"id":"s1","phase":"connecting","message":"Connecting to the VPN..."}""", HttpStatusCode.Accepted)
            .OnGet("/api/music/downloads/search/s1", """
                {"id":"s1","phase":"done","artist":"Fleetwood Mac","album":"Rumours","year":1977,
                 "results":[{"id":"r1","title":"Rumours [FLAC]","site":"The Pirate Bay","sizeBytes":300000000,"seeders":40,"leechers":2,"format":"FLAC","lossless":true,"matches":true,"warning":null}],
                 "others":[]}
                """)
            .On("POST", "/api/music/downloads", """{"id":"d1","kind":"album","title":"Rumours","subtitle":"Fleetwood Mac","state":"queued"}""", HttpStatusCode.Created)
            .OnGet("/api/music/downloads", """{"status":null,"active":[{"id":"d1","kind":"album","state":"downloading","progress":0.5}],"recent":[]}""")
            .On("POST", "/api/music/downloads/d1/retry", """{"id":"d1","state":"queued"}""");
        using var client = ClientFor(server);

        var status = await client.MusicDownloadStatusAsync();
        var started = await client.StartMusicDownloadSearchAsync(new MusicReleaseSearchInput { Artist = "Fleetwood Mac", Album = "Rumours", Year = 1977 });
        var finished = await client.MusicDownloadSearchStateAsync(started.Id);
        var download = await client.MusicDownloadGrabAsync(new MusicGrabInput { SearchId = "s1", ReleaseId = "r1" });
        var list = await client.MusicDownloadsAsync();
        await client.RetryMusicDownloadAsync("d1");

        Assert.True(status.CanDownload && status.MusicLibrary);
        Assert.Equal("connected", status.Status!.Vpn!.State);
        Assert.False(started.IsFinished);
        Assert.True(finished.IsFinished);
        Assert.Equal("The Pirate Bay", finished.Results.Single().Site);
        Assert.Equal("album", download.Kind);
        Assert.Equal(0.5, list.Active.Single().Progress);
        Assert.Contains("\"searchId\":\"s1\"", server.Bodies[3]);
        Assert.DoesNotContain("libraryId", server.Bodies[3]);
    }

    [Fact]
    public async Task Episode_decodes_progress_and_download_state()
    {
        using var server = new FakeServer().OnGet("/api/podcasts/home", $$"""{"upNext":[{{EpisodeJson}}],"timeSavedSeconds":12000}""");
        using var client = ClientFor(server);

        var home = await client.PodcastHomeAsync();
        var episode = Assert.Single(home.UpNext);
        Assert.True(episode.IsInProgress);
        Assert.Equal(300, episode.ResumeSeconds);
        Assert.True(episode.Download!.IsDone);
        Assert.Equal("/api/podcasts/episodes/e1/audio", episode.AudioPath);
        Assert.Empty(home.Subscriptions);
        Assert.Equal(12000, home.TimeSavedSeconds);
    }

    [Fact]
    public async Task Podcast_routes_send_the_contract_bodies()
    {
        using var server = new FakeServer()
            .On("POST", "/api/podcasts/subscriptions", """{"id":"p1","feedUrl":"https://f","title":"Show","subscribed":true}""", HttpStatusCode.Created)
            .On("POST", "/api/podcasts/queue", "[]")
            .On("PUT", "/api/podcasts/queue", "[]")
            .On("POST", "/api/podcasts/episodes/e1/progress", """{"positionSeconds":300,"durationSeconds":1780,"percent":16.9,"played":false}""")
            .On("POST", "/api/podcasts/episodes/e1/bookmarks", """{"id":"b1","episodeId":"e1","positionSeconds":1234,"note":"n"}""", HttpStatusCode.Created)
            .On("POST", "/api/podcasts/bookmarks/b1/to-note", """{"noteId":"n1"}""")
            .On("POST", "/api/podcasts/episodes/e1/clips", """{"id":"c1","url":"/api/podcasts/clips/c1.m4a","durationSeconds":45}""", HttpStatusCode.Created)
            .On("PUT", "/api/podcasts/p1/my-settings", """{"id":"p1","feedUrl":"f","title":"Show"}""")
            .On("POST", "/api/podcasts/opml", """{"added":12,"existing":3,"failed":[{"feedUrl":"https://bad","error":"404"}]}""");
        using var client = ClientFor(server);

        await client.SubscribeAsync(" https://f ");
        await client.AddToPodcastQueueAsync("e1", "next");
        await client.SetPodcastQueueAsync(new[] { "e2", "e1" });
        await client.ReportEpisodeProgressAsync("e1", new EpisodeProgressInput { PositionSeconds = 300, DurationSeconds = 1780, Speed = 1.5, DeviceName = "PC" });
        await client.AddBookmarkAsync("e1", 1234, "n");
        var note = await client.BookmarkToNoteAsync("b1");
        var clip = await client.CreateClipAsync("e1", 1200, 400, "Best bit");
        await client.SavePodcastMySettingsAsync("p1", new PodcastMySettings { PlaybackSpeed = 1.5, SkipIntroSeconds = 30 });
        var opml = await client.ImportOpmlAsync("<opml/>");

        Assert.Equal("""{"feedUrl":"https://f"}""", server.Bodies[0]);
        Assert.Equal("""{"episodeId":"e1","position":"next"}""", server.Bodies[1]);
        Assert.Equal("""{"episodeIds":["e2","e1"]}""", server.Bodies[2]);
        Assert.Equal("""{"positionSeconds":300,"durationSeconds":1780,"speed":1.5,"deviceName":"PC"}""", server.Bodies[3]);
        Assert.Equal("""{"positionSeconds":1234,"note":"n"}""", server.Bodies[4]);
        Assert.Equal("n1", note.NoteId);
        Assert.Contains("\"durationSeconds\":180", server.Bodies[6]); // clamped to the 5-180 s the server allows
        Assert.Equal("/api/podcasts/clips/c1.m4a", clip.Url);
        Assert.Contains("\"playbackSpeed\":1.5", server.Bodies[7]);
        Assert.StartsWith("text/xml", server.ContentTypes[8]);
        Assert.Equal("<opml/>", server.Bodies[8]);
        Assert.Equal(12, opml.Added);
        Assert.Equal("https://bad", opml.Failed.Single().FeedUrl);
    }

    [Fact]
    public async Task Transcript_and_chapters_decode()
    {
        using var server = new FakeServer()
            .OnGet("/api/podcasts/episodes/e1/transcript", """{"source":"whisper","language":"en","segments":[{"start":0.0,"end":4.2,"speaker":null,"text":"Hi"}]}""")
            .OnGet("/api/podcasts/episodes/e1/chapters", """[{"startSeconds":0,"endSeconds":95,"title":"Intro","imageUrl":null,"url":null}]""");
        using var client = ClientFor(server);

        var transcript = await client.EpisodeTranscriptAsync("e1");
        var chapters = await client.EpisodeChaptersAsync("e1");
        Assert.Equal(4.2, transcript.Segments.Single().End);
        Assert.Equal(95, chapters.Single().EndSeconds);
    }

    [Fact]
    public async Task Now_playing_is_put_without_updated_at_and_resolves()
    {
        using var server = new FakeServer()
            .On("PUT", "/api/audio/now-playing", "", HttpStatusCode.NoContent)
            .OnGet("/api/audio/now-playing/ios-1/resolve", $$"""
                {"nowPlaying":{"deviceId":"ios-1","deviceName":"iPhone","kind":"music","queue":[{"type":"track","id":"t1"}],"index":0,
                  "positionSeconds":61.2,"playing":true,"shuffle":false,"repeat":"all","speed":1.0,"updatedAt":"2026-10-02T10:00:00Z"},
                 "tracks":[{{TrackJson}}],"episodes":[]}
                """);
        using var client = ClientFor(server);

        await client.PutNowPlayingAsync(new NowPlaying
        {
            DeviceId = "windows-1", DeviceName = "Den PC", Kind = "music",
            Queue = new() { new AudioQueueRef { Type = "track", Id = "t1" } }, Index = 0, PositionSeconds = 3, Playing = true,
            Repeat = "one", Speed = 1, Context = new AudioContext { Type = "album", Id = "al1", Name = "Rumours" },
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var resolved = await client.ResolveNowPlayingAsync("ios-1");

        Assert.DoesNotContain("updatedAt", server.Bodies[0]);
        Assert.Contains("\"repeat\":\"one\"", server.Bodies[0]);
        Assert.Contains("\"context\":{\"type\":\"album\",\"id\":\"al1\",\"name\":\"Rumours\"}", server.Bodies[0]);
        Assert.Equal(RepeatMode.All, PlayQueue.ParseRepeat(resolved.NowPlaying.Repeat));
        Assert.Equal("Dreams", resolved.Tracks.Single().Title);
    }

    [Fact]
    public async Task Speaker_registration_state_and_commands_use_the_contract()
    {
        using var server = new FakeServer()
            .On("PUT", "/api/audio/speakers/w1", "", HttpStatusCode.NoContent)
            .On("PUT", "/api/audio/speakers/w1/state", "", HttpStatusCode.NoContent)
            .On("POST", "/api/audio/speakers/k1/commands", """{"seq":42}""", HttpStatusCode.Accepted)
            .OnGet("/api/audio/speakers", """[{"deviceId":"k1","name":"Kitchen","kind":"kiosk","online":true,"allowAnnouncements":true,"volume":0.6,"state":{"playing":true,"title":"Dreams"}}]""");
        using var client = ClientFor(server);

        await client.RegisterSpeakerAsync("w1", new SpeakerRegistration { Name = "Den PC", AllowAnnouncements = true, Volume = 0.5 });
        await client.PutSpeakerStateAsync("w1", new SpeakerState { Playing = true, Title = "Dreams", Volume = 0.5, Kind = "music", ItemId = "t1" });
        var accepted = await client.SendSpeakerCommandAsync("k1", new SpeakerCommand { Action = "play", Queue = new() { new AudioQueueRef { Id = "t1" } }, Index = 0 });
        var speakers = await client.SpeakersAsync();

        Assert.Equal("""{"name":"Den PC","kind":"windows","allowAnnouncements":true,"volume":0.5}""", server.Bodies[0]);
        Assert.Equal("""{"action":"play","queue":[{"type":"track","id":"t1"}],"index":0}""", server.Bodies[2]);
        Assert.Equal(42, accepted.Seq);
        Assert.True(speakers.Single().State!.Playing);
    }

    [Fact]
    public async Task Speaker_session_applies_commands_in_seq_order_one_finished_before_the_next()
    {
        using var server = new FakeServer();
        using var client = ClientFor(server);
        var log = new List<string>();
        var session = new SpeakerSession(client, "w1", () => new SpeakerRegistration(), async command =>
        {
            log.Add($"start {command.Action}");
            if (command.Action == "announce") await Task.Delay(50);
            log.Add($"end {command.Action}");
        }, NullSentinelLog.Instance);

        await session.ApplyAsync(new SpeakerCommandsReply
        {
            Commands = new()
            {
                new DeliveredSpeakerCommand { Seq = 8, Action = "play" },
                new DeliveredSpeakerCommand { Seq = 7, Action = "announce", Text = "Good morning" },
            },
            LatestSeq = 8,
        });
        Assert.Equal(new[] { "start announce", "end announce", "start play", "end play" }, log);
        Assert.Equal(8, session.After);

        // A repeat of seq 8 (a reply resent after a dropped connection) is not applied twice.
        await session.ApplyAsync(new SpeakerCommandsReply { Commands = new() { new DeliveredSpeakerCommand { Seq = 8, Action = "play" } } });
        Assert.Equal(4, log.Count);

        // latestSeq moves the cursor even with nothing to do.
        await session.ApplyAsync(new SpeakerCommandsReply { LatestSeq = 12 });
        Assert.Equal(12, session.After);
    }

    [Fact]
    public async Task Speaker_session_first_polls_for_only_new_commands_and_heartbeats()
    {
        using var server = new FakeServer()
            .On("PUT", "/api/audio/speakers/w1", "", HttpStatusCode.NoContent)
            .On("DELETE", "/api/audio/speakers/w1", "", HttpStatusCode.NoContent)
            .OnGet("/api/audio/speakers/w1/commands", """{"commands":[{"seq":5,"at":"2026-10-02T10:00:00Z","from":"nathan","action":"pause"}],"latestSeq":5}""");
        using var client = ClientFor(server);
        var received = new List<string>();
        var session = new SpeakerSession(client, "w1", () => new SpeakerRegistration { Name = "PC" }, command =>
        {
            lock (received) received.Add(command.Action);
            return Task.CompletedTask;
        }, NullSentinelLog.Instance, waitSeconds: 1, retryDelay: TimeSpan.FromMilliseconds(50));

        session.Start();
        for (var i = 0; i < 100 && session.After < 5; i++) await Task.Delay(20);
        await session.StopAsync();

        List<string> requests;
        lock (server.Requests) requests = server.Requests.ToList();
        Assert.Contains("PUT /api/audio/speakers/w1", requests);
        Assert.Contains("GET /api/audio/speakers/w1/commands?after=-1&wait=1", requests);
        Assert.Equal("DELETE /api/audio/speakers/w1", requests.Last());
        Assert.Contains("pause", received);
        Assert.Equal(5, session.After);
    }

    [Fact]
    public void Speaker_poll_backs_off_to_a_minute_and_waits_a_minute_when_switched_off()
    {
        var retry = TimeSpan.FromSeconds(5);
        Assert.Equal(5, SpeakerSession.Backoff(retry, 1, off: false).TotalSeconds);
        Assert.Equal(10, SpeakerSession.Backoff(retry, 2, off: false).TotalSeconds);
        Assert.Equal(40, SpeakerSession.Backoff(retry, 4, off: false).TotalSeconds);
        Assert.Equal(60, SpeakerSession.Backoff(retry, 9, off: false).TotalSeconds);
        Assert.Equal(60, SpeakerSession.Backoff(retry, 1, off: true).TotalSeconds);
    }

    [Fact]
    public async Task Alarms_briefing_stats_and_listenbrainz_decode()
    {
        using var server = new FakeServer()
            .On("POST", "/api/audio/alarms", """{"id":"w1","name":"Weekdays","time":"06:45","days":["mon"],"speakerId":"k1","source":{"type":"briefing"},"volume":0.5,"rampSeconds":120,"enabled":true,"nextFireAt":"2026-10-03T11:45:00Z"}""", HttpStatusCode.Created)
            .OnGet("/api/audio/briefing", $$"""{"greeting":"Good morning, Nathan.","speech":["It's Thursday.",""],"episodes":[{{EpisodeJson}}]}""")
            .OnGet("/api/audio/stats", """{"period":"year","music":{"minutes":12034,"plays":3400,"topArtists":[{"artist":{"id":"a","name":"Fleetwood Mac"},"plays":300,"minutes":1100}],"byHour":[1,2],"longestStreakDays":41,"firstPlay":null},"podcasts":{"minutes":4000,"topShows":[]}}""")
            .On("PUT", "/api/audio/me/listenbrainz", """{"connected":true,"username":"nathan"}""");
        using var client = ClientFor(server);

        var alarm = await client.SaveWakeAlarmAsync(new WakeAlarm { Name = "Weekdays", Time = "06:45", Days = new() { "mon" }, SpeakerId = "k1", Source = new WakeAlarmSource { Type = "briefing" } });
        var briefing = await client.MorningBriefingAsync();
        var stats = await client.ListeningStatsAsync("year", 2026);
        var lb = await client.SetListenBrainzTokenAsync(" tok ");

        Assert.Equal("w1", alarm.Id);
        Assert.NotNull(alarm.NextFireAt);
        Assert.DoesNotContain("nextFireAt", server.Bodies[0]);
        Assert.Equal(new[] { "Good morning, Nathan.", "It's Thursday." }, briefing.AllLines);
        Assert.Equal("Fleetwood Mac", stats.Music!.TopArtists.Single().Artist.Name);
        Assert.Contains("GET /api/audio/stats?period=year&year=2026", server.Requests);
        Assert.Equal("""{"token":"tok"}""", server.Bodies[3]);
        Assert.True(lb.Connected);
    }

    [Fact]
    public async Task Audio_picker_and_network_routes_live_under_api_audio()
    {
        using var server = new FakeServer()
            .OnGet("/api/audio/browse", """{"path":"/mnt","parent":"","kind":"local","folders":[{"name":"Music","path":"/mnt/Music","videoCount":12}],"videoCount":0}""")
            .OnGet("/api/audio/network", "[]")
            .On("POST", "/api/music/libraries", """{"id":"m1","name":"Music","path":"/mnt/Music","network":false,"trackCount":0}""", HttpStatusCode.Created)
            .On("POST", "/api/music/libraries/m1/scan", "", HttpStatusCode.Accepted);
        using var client = ClientFor(server);

        var listing = await client.MediaBrowseAsync("/mnt", basePath: SentinelClient.AudioBase);
        await client.MediaNetworkComputersAsync(basePath: SentinelClient.AudioBase);
        var library = await client.CreateMusicLibraryAsync(new MusicLibraryInput { Name = "Music", Path = "/mnt/Music" });
        await client.ScanMusicLibraryAsync("m1");

        Assert.Equal(12, listing.Folders.Single().VideoCount);
        Assert.Equal("m1", library.Id);
        Assert.Equal(new[] { "GET /api/audio/browse?path=%2Fmnt", "GET /api/audio/network", "POST /api/music/libraries", "POST /api/music/libraries/m1/scan" }, server.Requests);
    }

    [Fact]
    public void Art_paths_ask_for_a_size_within_the_contract_range()
    {
        Assert.Equal("/api/music/albums/a/art?size=1200", SentinelClient.ArtPath("/api/music/albums/a/art", 5000));
        Assert.Equal("/x?y=1&size=64", SentinelClient.ArtPath("/x?y=1", 10));
        Assert.Equal("https://cdn/x.jpg", SentinelClient.ArtPath("https://cdn/x.jpg", 300));
        Assert.Equal("", SentinelClient.ArtPath(null, 300));
    }
}
