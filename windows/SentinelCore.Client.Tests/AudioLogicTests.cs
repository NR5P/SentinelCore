using System.Text.Json;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The player's pure logic for Music &amp; Podcasts (core/docs/MUSIC_AND_PODCASTS.md §11): the
/// queue, LRC and transcript following, Sound Check, the sleep timer, podcast skipping, listening
/// reports, Continue-from-device and the smart-playlist rules.
/// </summary>
public class AudioLogicTests
{
    private static QueueEntry Track(string id, double duration = 200) =>
        new(new MusicTrack { Id = id, Title = $"Song {id}", Artist = "Band", Album = "Record", AlbumId = "al", DurationSeconds = duration });

    private static PlayQueue QueueOf(int count, Random? random = null)
    {
        var queue = new PlayQueue(random ?? new Random(1));
        queue.Load(Enumerable.Range(1, count).Select(i => Track(i.ToString())));
        return queue;
    }

    private static string[] Ids(PlayQueue queue) => queue.Entries.Select(entry => entry.Id).ToArray();

    // ---------------------------------------------------------------- Queue

    [Fact]
    public void Next_walks_the_queue_and_stops_at_the_end_with_repeat_off()
    {
        var queue = QueueOf(3);
        Assert.Equal("1", queue.Current!.Id);
        Assert.Equal("2", queue.Next(automatic: true)!.Id);
        Assert.Equal("3", queue.Next(automatic: true)!.Id);
        Assert.Null(queue.Next(automatic: true));
        Assert.Equal("3", queue.Current!.Id);
    }

    [Fact]
    public void Repeat_all_wraps_and_repeat_one_repeats_only_when_the_song_ends_by_itself()
    {
        var queue = QueueOf(2);
        queue.Repeat = RepeatMode.All;
        queue.Next(true);
        Assert.Equal("1", queue.Next(true)!.Id);

        queue.Repeat = RepeatMode.One;
        Assert.Equal("1", queue.Next(automatic: true)!.Id);
        Assert.Equal("2", queue.Next(automatic: false)!.Id);
    }

    [Fact]
    public void Cycle_repeat_goes_off_all_one_off()
    {
        var queue = QueueOf(1);
        Assert.Equal(RepeatMode.All, queue.CycleRepeat());
        Assert.Equal(RepeatMode.One, queue.CycleRepeat());
        Assert.Equal(RepeatMode.Off, queue.CycleRepeat());
    }

    [Fact]
    public void Previous_restarts_after_three_seconds_and_goes_back_before()
    {
        var queue = QueueOf(3);
        queue.Next(false);
        var (restart, isRestart) = queue.Previous(10);
        Assert.True(isRestart);
        Assert.Equal("2", restart!.Id);

        var (back, wentBack) = queue.Previous(1);
        Assert.False(wentBack);
        Assert.Equal("1", back!.Id);

        // At the very start with repeat off there is nowhere to go: restart.
        Assert.True(queue.Previous(0).Restart);
    }

    [Fact]
    public void Shuffle_keeps_the_current_song_first_and_unshuffle_restores_the_order_from_it()
    {
        var queue = QueueOf(10, new Random(42));
        queue.JumpTo(4);
        queue.SetShuffle(true);

        Assert.Equal("5", queue.Current!.Id);
        Assert.Equal(0, queue.Index);
        Assert.Equal(10, queue.Entries.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i.ToString()).OrderBy(x => x), Ids(queue).OrderBy(x => x));
        Assert.NotEqual(Enumerable.Range(1, 10).Select(i => i.ToString()).ToArray(), Ids(queue));

        queue.Next(false);
        var playing = queue.Current!.Id;
        queue.SetShuffle(false);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i.ToString()).ToArray(), Ids(queue));
        Assert.Equal(playing, queue.Current!.Id);
    }

    [Fact]
    public void Loading_shuffled_with_no_start_picks_any_song_and_with_a_start_plays_it_first()
    {
        var queue = new PlayQueue(new Random(3));
        queue.Load(Enumerable.Range(1, 5).Select(i => Track(i.ToString())), startIndex: 2, shuffle: true);
        Assert.Equal("3", queue.Current!.Id);
        Assert.True(queue.Shuffle);

        queue.Load(Enumerable.Range(1, 5).Select(i => Track(i.ToString())), startIndex: -1, shuffle: true);
        Assert.NotNull(queue.Current);
        Assert.Equal(0, queue.Index);
    }

    [Fact]
    public void Play_next_inserts_after_the_current_and_add_last_appends()
    {
        var queue = QueueOf(3);
        queue.PlayNext(new[] { Track("x") });
        queue.AddLast(new[] { Track("y") });
        Assert.Equal(new[] { "1", "x", "2", "3", "y" }, Ids(queue));
        Assert.Equal("x", queue.Next(false)!.Id);
    }

    [Fact]
    public void Move_keeps_the_current_entry_current()
    {
        var queue = QueueOf(4);
        queue.JumpTo(1); // "2"
        queue.Move(3, 0);
        Assert.Equal(new[] { "4", "1", "2", "3" }, Ids(queue));
        Assert.Equal("2", queue.Current!.Id);
        Assert.Equal(2, queue.Index);
    }

    [Fact]
    public void Removing_the_current_entry_hands_back_the_next_and_removing_earlier_shifts_the_index()
    {
        var queue = QueueOf(4);
        queue.JumpTo(2); // "3"
        Assert.Null(queue.RemoveAt(0));
        Assert.Equal("3", queue.Current!.Id);
        Assert.Equal(1, queue.Index);

        var next = queue.RemoveAt(1);
        Assert.Equal("4", next!.Id);
        Assert.Equal(new[] { "2", "4" }, Ids(queue));
    }

    [Fact]
    public void Duplicate_songs_are_distinct_entries()
    {
        var a = Track("same");
        var b = Track("same");
        Assert.NotEqual(a.Uid, b.Uid);
    }

    [Fact]
    public void Wire_form_is_cut_to_500_around_the_current_entry()
    {
        var queue = QueueOf(1200);
        queue.JumpTo(1000);
        var (wire, index) = queue.ToWire();
        Assert.Equal(500, wire.Count);
        Assert.Equal("1001", wire[index].Id);
        Assert.All(wire, entry => Assert.Equal("track", entry.Type));
    }

    [Fact]
    public void Album_gain_applies_only_to_an_album_playing_in_order()
    {
        var queue = new PlayQueue(new Random(1));
        queue.Load(new[] { Track("1"), Track("2") }, context: new AudioContext { Type = "album", Id = "al" });
        Assert.True(queue.IsAlbumInOrder);
        queue.SetShuffle(true);
        Assert.False(queue.IsAlbumInOrder);
    }

    [Fact]
    public void Episodes_carry_their_show_and_art()
    {
        var entry = new QueueEntry(new PodcastEpisode { Id = "e1", Title = "Ep", PodcastTitle = "Show", DurationSeconds = 100 });
        Assert.True(entry.IsEpisode);
        Assert.Equal("episode", entry.ToRef().Type);
        Assert.Equal("Show", entry.Subtitle);
        Assert.Equal("/api/podcasts/episodes/e1/art", entry.ArtUrl);
    }

    // ---------------------------------------------------------------- Lyrics

    [Fact]
    public void Lrc_parses_stamps_offsets_and_repeated_lines()
    {
        const string lrc = "[ar:Fleetwood Mac]\n[ti:Dreams]\n[offset:+500]\n[00:12.30]Now here you go again\n[00:20.00][01:05.5]You say you want your freedom\n[00:30]Well, who am I\n";
        var lines = LyricsTimeline.ParseLrc(lrc);

        Assert.Equal(4, lines.Count);
        Assert.Equal(11.8, lines[0].Time!.Value, 3);
        Assert.Equal("Now here you go again", lines[0].Text);
        Assert.Equal(19.5, lines[1].Time!.Value, 3);
        Assert.Equal(29.5, lines[2].Time!.Value, 3);
        Assert.Equal(65.0, lines[3].Time!.Value, 3);
        Assert.Equal("You say you want your freedom", lines[3].Text);
    }

    [Fact]
    public void Plain_text_lyrics_come_back_unsynced()
    {
        var lines = LyricsTimeline.ParseLrc("Line one\n\nLine two");
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Null(line.Time));
        Assert.False(LyricsTimeline.IsSynced(lines));
        Assert.Equal(-1, LyricsTimeline.ActiveIndex(lines, 10));
    }

    [Fact]
    public void Active_lyric_line_is_the_last_one_started()
    {
        var lines = LyricsTimeline.ParseLrc("[00:01]a\n[00:05]b\n[00:09]c");
        Assert.Equal(-1, LyricsTimeline.ActiveIndex(lines, 0.5));
        Assert.Equal(0, LyricsTimeline.ActiveIndex(lines, 1));
        Assert.Equal(1, LyricsTimeline.ActiveIndex(lines, 8.9));
        Assert.Equal(2, LyricsTimeline.ActiveIndex(lines, 300));
    }

    [Fact]
    public void Server_lyrics_use_their_own_lines_or_parse_plain()
    {
        var synced = new MusicLyrics
        {
            Synced = true,
            Lines = new() { new LyricsLine { Time = 5, Text = "b" }, new LyricsLine { Time = 1, Text = "a" } },
        };
        Assert.Equal(new[] { "a", "b" }, LyricsTimeline.LinesFor(synced).Select(line => line.Text));

        var plainLrc = new MusicLyrics { Synced = false, Plain = "[00:02]x" };
        Assert.True(LyricsTimeline.IsSynced(LyricsTimeline.LinesFor(plainLrc)));
    }

    // ---------------------------------------------------------------- Transcripts and chapters

    private static readonly List<TranscriptSegment> Segments = new()
    {
        new() { Start = 0, End = 4, Text = "Welcome to the show" },
        new() { Start = 4, End = 9, Text = "Today: the Café economy" },
        new() { Start = 12, End = 15, Text = "And the weather" },
    };

    [Fact]
    public void Transcript_follows_playback_and_holds_through_gaps()
    {
        Assert.Equal(0, TranscriptTimeline.ActiveIndex(Segments, 1));
        Assert.Equal(1, TranscriptTimeline.ActiveIndex(Segments, 4));
        Assert.Equal(1, TranscriptTimeline.ActiveIndex(Segments, 10.5));
        Assert.Equal(2, TranscriptTimeline.ActiveIndex(Segments, 13));
        Assert.Equal(-1, TranscriptTimeline.ActiveIndex(new List<TranscriptSegment>(), 3));
    }

    [Fact]
    public void Transcript_search_ignores_case_and_accents_and_needs_every_word()
    {
        Assert.Equal(new[] { 1 }, TranscriptTimeline.Search(Segments, "cafe"));
        Assert.Equal(new[] { 1 }, TranscriptTimeline.Search(Segments, "CAFÉ economy"));
        Assert.Empty(TranscriptTimeline.Search(Segments, "cafe weather"));
        Assert.Empty(TranscriptTimeline.Search(Segments, "  "));
    }

    [Fact]
    public void Chapters_know_the_current_next_and_previous()
    {
        var chapters = new List<PodcastChapter>
        {
            new() { StartSeconds = 0, Title = "Intro" },
            new() { StartSeconds = 95, Title = "News" },
            new() { StartSeconds = 600, Title = "Sport" },
        };
        Assert.Equal(1, ChapterTimeline.ActiveIndex(chapters, 100));
        Assert.Equal(600, ChapterTimeline.NextStart(chapters, 100));
        Assert.Null(ChapterTimeline.NextStart(chapters, 700));
        Assert.Equal(95, ChapterTimeline.PreviousStart(chapters, 200));
        Assert.Equal(0, ChapterTimeline.PreviousStart(chapters, 96));
    }

    // ---------------------------------------------------------------- Sound Check and volume

    [Fact]
    public void Sound_check_is_ten_to_the_gain_over_twenty_clamped_to_one()
    {
        var gain = new ReplayGainInfo { TrackGain = -6.0, AlbumGain = -9.0 };
        Assert.Equal(Math.Pow(10, -6.0 / 20), SoundCheck.Factor(gain, albumMode: false), 6);
        Assert.Equal(Math.Pow(10, -9.0 / 20), SoundCheck.Factor(gain, albumMode: true), 6);
        Assert.Equal(1.0, SoundCheck.Factor(new ReplayGainInfo { TrackGain = 4 }, false));
        Assert.Equal(1.0, SoundCheck.Factor(null, false));
        // Each falls back to the other.
        Assert.Equal(Math.Pow(10, -3.0 / 20), SoundCheck.Factor(new ReplayGainInfo { AlbumGain = -3 }, false), 6);
        Assert.Equal(Math.Pow(10, -2.0 / 20), SoundCheck.Factor(new ReplayGainInfo { TrackGain = -2 }, true), 6);
    }

    [Fact]
    public void Effective_volume_multiplies_user_gain_duck_and_fade()
    {
        Assert.Equal(0.5 * 0.8 * 0.2 * 0.5, SoundCheck.Effective(0.5, 0.8, ducked: true, fade: 0.5), 6);
        Assert.Equal(1.0, SoundCheck.Effective(2, 1, false, 1));
        Assert.Equal(0.25, SoundCheck.Ramp(30, 120), 6);
        Assert.Equal(1.0, SoundCheck.Ramp(500, 120));
        Assert.Equal(1.0, SoundCheck.Ramp(0, 0));
    }

    // ---------------------------------------------------------------- Sleep timer

    [Fact]
    public void Sleep_timer_in_minutes_fades_over_the_last_ten_seconds_then_is_due()
    {
        var start = new DateTimeOffset(2026, 10, 2, 22, 0, 0, TimeSpan.Zero);
        var timer = new SleepTimer();
        timer.StartMinutes(15, start);

        Assert.Equal(900, timer.RemainingSeconds(start)!.Value, 3);
        Assert.Equal(1.0, timer.Fade(start.AddSeconds(889), 0, 0));
        Assert.Equal(0.5, timer.Fade(start.AddSeconds(895), 0, 0), 3);
        Assert.False(timer.IsDue(start.AddSeconds(899)));
        Assert.True(timer.IsDue(start.AddSeconds(900)));
        Assert.Equal("Sleep in 15:00", timer.Label(start, episode: false));

        timer.Cancel();
        Assert.False(timer.IsActive);
        Assert.Equal(1.0, timer.Fade(start.AddSeconds(899), 0, 0));
    }

    [Fact]
    public void Sleep_timer_at_end_of_item_fades_on_the_items_own_clock()
    {
        var timer = new SleepTimer();
        timer.StartEndOfItem();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1.0, timer.Fade(now, 100, 200));
        Assert.Equal(0.3, timer.Fade(now, 197, 200), 3);
        Assert.False(timer.IsDue(now));
        Assert.Equal("Sleep at end of episode", timer.Label(now, episode: true));
    }

    // ---------------------------------------------------------------- Podcasts

    [Fact]
    public void Podcast_start_resumes_or_skips_the_intro_and_the_outro_ends_early()
    {
        var settings = new PodcastMySettings { SkipIntroSeconds = 30, SkipOutroSeconds = 60 };
        var fresh = new PodcastEpisode { Id = "e", DurationSeconds = 1800 };
        var started = new PodcastEpisode
        {
            Id = "e",
            DurationSeconds = 1800,
            Progress = new EpisodeProgress { PositionSeconds = 300, DurationSeconds = 1800 },
        };
        Assert.Equal(30, PodcastPlayback.StartPosition(fresh, settings));
        Assert.Equal(300, PodcastPlayback.StartPosition(started, settings));
        Assert.Equal(0, PodcastPlayback.StartPosition(fresh, null));
        Assert.Equal(1740, PodcastPlayback.EarlyEnd(1800, settings));
        Assert.Null(PodcastPlayback.EarlyEnd(1800, null));
        Assert.Equal(1500, started.RemainingSeconds);
    }

    [Fact]
    public void Speeds_clamp_label_and_cycle()
    {
        Assert.Equal(3.0, PodcastPlayback.ClampSpeed(5));
        Assert.Equal("1.5×", PodcastPlayback.SpeedLabel(1.5));
        Assert.Equal(1.1, PodcastPlayback.NextSpeed(1.0));
        Assert.Equal(0.5, PodcastPlayback.NextSpeed(3.0));
        Assert.Equal(1.25, PodcastPlayback.SpeedFor(new PodcastMySettings { PlaybackSpeed = 1.25 }, 1.0));
        Assert.Equal(1.5, PodcastPlayback.SpeedFor(new PodcastMySettings(), 1.5));
        Assert.Equal(0, PodcastPlayback.Skip(10, -15, 100));
        Assert.Equal(99.5, PodcastPlayback.Skip(90, 30, 100));
    }

    // ---------------------------------------------------------------- Reporting

    [Fact]
    public void Listen_meter_counts_only_time_spent_playing()
    {
        var t = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var meter = new ListenMeter();
        meter.Reset(t, playing: true);
        meter.Pause(t.AddSeconds(40));
        Assert.Equal(40, meter.Seconds(t.AddSeconds(100)), 3);
        meter.Resume(t.AddSeconds(100));
        Assert.Equal(50, meter.Seconds(t.AddSeconds(110)), 3);
        meter.Reset(t.AddSeconds(110), playing: false);
        Assert.Equal(0, meter.Seconds(t.AddSeconds(200)));
    }

    [Fact]
    public void Played_report_follows_the_contract_rules()
    {
        Assert.True(ListenMeter.CountsAsPlay(130, 257));
        Assert.False(ListenMeter.CountsAsPlay(100, 257));
        Assert.True(ListenMeter.CountsAsPlay(240, 900));
        Assert.False(ListenMeter.CountsAsPlay(29, 40));

        var report = ListenMeter.Report(12.345, completed: true, skipped: true, DateTimeOffset.UnixEpoch, "Den PC");
        Assert.Equal(12.3, report.SecondsListened);
        Assert.False(report.Skipped); // completed wins
        Assert.Equal("Den PC", report.DeviceName);
    }

    [Fact]
    public void Interval_gate_passes_once_per_interval()
    {
        var gate = new IntervalGate(TimeSpan.FromSeconds(15));
        var t = DateTimeOffset.UnixEpoch;
        Assert.True(gate.TryPass(t));
        Assert.False(gate.TryPass(t.AddSeconds(14)));
        Assert.True(gate.TryPass(t.AddSeconds(15)));
        gate.Mark(t.AddSeconds(20));
        Assert.False(gate.TryPass(t.AddSeconds(30)));
    }

    // ---------------------------------------------------------------- Continue from device

    [Fact]
    public void Continue_banner_offers_the_freshest_other_device_preferring_one_playing()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var queue = new List<AudioQueueRef> { new() { Type = "track", Id = "t" } };
        var devices = new[]
        {
            new NowPlaying { DeviceId = "me", Queue = queue, Playing = true, UpdatedAt = now },
            new NowPlaying { DeviceId = "phone", DeviceName = "Phone", Queue = queue, Playing = false, UpdatedAt = now.AddMinutes(-1) },
            new NowPlaying { DeviceId = "kitchen", DeviceName = "Kitchen iPad", Queue = queue, Playing = true, UpdatedAt = now.AddMinutes(-5),
                PositionSeconds = 60, Speed = 1.5, Context = new AudioContext { Type = "album", Name = "Rumours" } },
            new NowPlaying { DeviceId = "old", Queue = queue, Playing = true, UpdatedAt = now.AddDays(-2) },
            new NowPlaying { DeviceId = "empty", Playing = true, UpdatedAt = now },
        };

        var pick = ContinueListening.Pick(devices, "me", now);
        Assert.Equal("kitchen", pick!.DeviceId);
        Assert.Equal("Playing on Kitchen iPad · Rumours", ContinueListening.Banner(pick));
        Assert.Equal(60 + 300 * 1.5, ContinueListening.EstimatedPosition(pick, now), 3);
        Assert.Null(ContinueListening.Pick(devices.Take(1), "me", now));
    }

    [Fact]
    public void Device_id_is_made_once_and_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc-audio-{Guid.NewGuid():N}.json");
        try
        {
            var first = AudioDevice.Id(new PreferenceStore(path));
            Assert.StartsWith("windows-", first);
            Assert.Equal(first, AudioDevice.Id(new PreferenceStore(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- Time formatting

    [Fact]
    public void Times_read_like_a_player()
    {
        Assert.Equal("3:05", AudioTime.Clock(185.9));
        Assert.Equal("1:02:03", AudioTime.Clock(3723));
        Assert.Equal("0:00", AudioTime.Clock(-4));
        Assert.Equal("-2:10", AudioTime.Remaining(50, 180));
        Assert.Equal("45 min", AudioTime.Length(2700));
        Assert.Equal("1 hr 5 min", AudioTime.Length(3900));
        Assert.Equal("1 hr 5 min left", AudioTime.Left(3900));
        Assert.Equal("200 hours", AudioTime.Minutes(12000));
    }

    // ---------------------------------------------------------------- Smart playlists

    [Fact]
    public void Smart_rules_validate_against_the_field_table()
    {
        var rules = new SmartRules
        {
            Match = "all",
            Rules = new()
            {
                new SmartRule { Field = "genre", Op = "is", Value = JsonSerializer.SerializeToElement("Rock") },
                new SmartRule { Field = "rating", Op = "gte", Value = JsonSerializer.SerializeToElement(4) },
                new SmartRule { Field = "lastPlayed", Op = "notInLast", Value = JsonSerializer.SerializeToElement(90) },
            },
            Sort = "random",
            Limit = 100,
        };
        Assert.Empty(SmartRuleCatalog.Validate(rules));

        rules.Rules.Add(new SmartRule { Field = "year", Op = "contains", Value = JsonSerializer.SerializeToElement(1990) });
        rules.Rules.Add(new SmartRule { Field = "loved", Op = "is", Value = JsonSerializer.SerializeToElement("yes") });
        rules.Rules.Add(new SmartRule { Field = "mood", Op = "is", Value = JsonSerializer.SerializeToElement("x") });
        rules.Limit = 9000;
        var problems = SmartRuleCatalog.Validate(rules);
        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public void Smart_values_are_typed_for_the_wire()
    {
        Assert.Equal(JsonValueKind.Number, SmartRuleCatalog.ParseValue(SmartValueKind.Number, "4")!.Value.ValueKind);
        Assert.Equal("4", SmartRuleCatalog.ParseValue(SmartValueKind.Number, "4")!.Value.GetRawText());
        Assert.Equal(JsonValueKind.True, SmartRuleCatalog.ParseValue(SmartValueKind.Bool, "yes")!.Value.ValueKind);
        Assert.Equal("Rock", SmartRuleCatalog.ParseValue(SmartValueKind.Text, " Rock ")!.Value.GetString());
        Assert.Null(SmartRuleCatalog.ParseValue(SmartValueKind.Days, "-3"));
        Assert.Null(SmartRuleCatalog.ParseValue(SmartValueKind.Number, "lots"));
        Assert.Null(SmartRuleCatalog.ParseValue(SmartValueKind.Text, "  "));
    }

    [Fact]
    public void Smart_rules_round_trip_through_json_and_read_as_a_sentence()
    {
        var json = """
            {"match":"any","rules":[{"field":"genre","op":"is","value":"Rock"},{"field":"rating","op":"gte","value":4},
             {"field":"loved","op":"is","value":true},{"field":"added","op":"inLast","value":30}],
             "sort":"plays","order":"desc","limit":50}
            """;
        var rules = JsonSerializer.Deserialize<SmartRules>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(SmartRuleCatalog.Validate(rules));
        Assert.Equal(
            "Genre is \"Rock\" or Rating is at least 4 or Loved or Added in the last 30 days · sorted by play count, up to 50 tracks",
            SmartRuleCatalog.Summary(rules));

        var back = JsonSerializer.Serialize(rules, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"value\":4", back);
        Assert.Contains("\"value\":true", back);
    }

    [Fact]
    public void New_rules_start_valid_for_non_text_fields()
    {
        foreach (var field in SmartRuleCatalog.Fields.Where(field => field.Kind is not (SmartValueKind.Text or SmartValueKind.Library)))
        {
            var rules = new SmartRules { Rules = new() { SmartRuleCatalog.NewRule(field.Name) } };
            Assert.Empty(SmartRuleCatalog.Validate(rules));
        }
    }
}
