extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// What the Music and Podcasts tabs share (core/docs/MUSIC_AND_PODCASTS.md §1, §7-§9), through
// the real web host with the two tabs replaced by fakes: play where you left off, speakers,
// wake-up alarms, the briefing, ListenBrainz, the announcer and the audio folder picker.
public sealed class AudioSharedTests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _dataDirectory;
    private readonly FakeMusic _music = new();
    private readonly FakePodcasts _podcasts = new();
    private readonly FakeNotifier _notifier = new();
    private readonly FakeListenBrainz _listenBrainz = new();

    public AudioSharedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"audio-shared-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(_settingsPath, """{ "Auth": { "Enabled": false }, "Music": { "AnnounceOnSpeakers": true } }""");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseTestSettings(_settingsPath, _dataDirectory)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IMusicAudioSource>(_music);
                services.AddSingleton<IPodcastAudioSource>(_podcasts);
                services.AddSingleton<IAudioNotifier>(_notifier);
                services.AddHttpClient(AudioListenBrainz.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _listenBrainz);
            }));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task RegisterSpeakerAsync(HttpClient client, string id, string name = "Kitchen", bool announcements = true) =>
        await JsonAsync(await client.PutAsJsonAsync($"/api/audio/speakers/{id}", new { name, kind = "kiosk", allowAnnouncements = announcements, volume = 0.4 }, Ct),
            HttpStatusCode.NoContent);

    private static async Task<List<JsonElement>> CommandsAsync(HttpClient client, string id, long after, int wait = 0)
    {
        var reply = await JsonAsync(await client.GetAsync($"/api/audio/speakers/{id}/commands?after={after}&wait={wait}", Ct), HttpStatusCode.OK);
        return reply.GetProperty("commands").EnumerateArray().ToList();
    }

    // MARK: - Feature flags

    [Fact]
    public async Task Enabled_AnswersWhenBothTabsAreOff_AndEverythingElseIs404()
    {
        _music.Enabled = false;
        _podcasts.Enabled = false;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var flags = await JsonAsync(await client.GetAsync("/api/audio/enabled", Ct), HttpStatusCode.OK);
        Assert.False(flags.GetProperty("music").GetBoolean());
        Assert.False(flags.GetProperty("podcasts").GetBoolean());
        var off = await JsonAsync(await client.GetAsync("/api/audio/speakers", Ct), HttpStatusCode.NotFound);
        Assert.Contains("turned off", off.GetProperty("error").GetString());

        _podcasts.Enabled = true;
        await JsonAsync(await client.GetAsync("/api/audio/speakers", Ct), HttpStatusCode.OK);
    }

    // MARK: - Play where you left off

    [Fact]
    public async Task NowPlaying_IsPerDevice_NewestFirst_LongQueuesKeepThe500AroundThePlayer_AndResolveReturnsTheItems()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var tracks = Enumerable.Range(0, 800).Select(_ => Guid.NewGuid()).ToList();
        await JsonAsync(await client.PutAsJsonAsync("/api/audio/now-playing", new
        {
            deviceId = "phone", deviceName = "Nathan's iPhone", kind = "music",
            queue = tracks.Select(id => new { type = "track", id }), index = 700, positionSeconds = 61.5, playing = true,
            repeat = "all", speed = 1, context = new { type = "playlist", id = "p1", name = "Road Trip" }
        }, Ct), HttpStatusCode.NoContent);
        await Task.Delay(20, Ct);
        await JsonAsync(await client.PutAsJsonAsync("/api/audio/now-playing", new
        {
            deviceId = "kitchen", deviceName = "Kitchen", kind = "podcast",
            queue = new[] { new { type = "episode", id = Guid.NewGuid() } }, index = 0, positionSeconds = 10, playing = false
        }, Ct), HttpStatusCode.NoContent);

        var all = (await JsonAsync(await client.GetAsync("/api/audio/now-playing", Ct), HttpStatusCode.OK)).EnumerateArray().ToList();
        Assert.Equal(["kitchen", "phone"], all.Select(n => n.GetProperty("deviceId").GetString()));
        var phone = all[1];
        var queue = phone.GetProperty("queue").EnumerateArray().Select(e => Guid.Parse(e.GetProperty("id").GetString()!)).ToList();
        Assert.Equal(500, queue.Count);
        // The track being played is still the one at `index`.
        Assert.Equal(tracks[700], queue[phone.GetProperty("index").GetInt32()]);
        Assert.Equal(tracks[^1], queue[^1]);
        Assert.Equal("all", phone.GetProperty("repeat").GetString());
        Assert.Equal("Road Trip", phone.GetProperty("context").GetProperty("name").GetString());

        _music.Known.Add(tracks[700]);
        var resolved = await JsonAsync(await client.GetAsync("/api/audio/now-playing/phone/resolve", Ct), HttpStatusCode.OK);
        Assert.Equal(tracks[700].ToString(), Assert.Single(resolved.GetProperty("tracks").EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty(resolved.GetProperty("episodes").EnumerateArray());

        await JsonAsync(await client.DeleteAsync("/api/audio/now-playing/phone", Ct), HttpStatusCode.NoContent);
        await JsonAsync(await client.GetAsync("/api/audio/now-playing/phone/resolve", Ct), HttpStatusCode.NotFound);
        await JsonAsync(await client.PutAsJsonAsync("/api/audio/now-playing", new { deviceId = "", kind = "video" }, Ct), HttpStatusCode.BadRequest);
    }

    // MARK: - Speakers

    [Fact]
    public async Task Speakers_GetCommandsInOrder_ByLongPoll_AndOnlyNewOnesOnFirstConnect()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await RegisterSpeakerAsync(client, "kiosk-1", "Living Room");

        // Something sent before the speaker first connects is not replayed to it.
        await JsonAsync(await client.PostAsJsonAsync("/api/audio/speakers/kiosk-1/commands", new { action = "pause" }, Ct), HttpStatusCode.Accepted);
        Assert.Empty(await CommandsAsync(client, "kiosk-1", -1));

        // A long poll that is waiting wakes up as soon as a command is sent.
        var latest = (await JsonAsync(await client.GetAsync("/api/audio/speakers/kiosk-1/commands?after=-1&wait=0", Ct), HttpStatusCode.OK)).GetProperty("latestSeq").GetInt64();
        var waiting = CommandsAsync(client, "kiosk-1", latest, wait: 20);
        await Task.Delay(200, Ct);
        Assert.False(waiting.IsCompleted);
        var track = Guid.NewGuid();
        await JsonAsync(await client.PostAsJsonAsync("/api/audio/speakers/kiosk-1/commands",
            new { action = "play", queue = new[] { new { type = "track", id = track } }, index = 0, volume = 0.5 }, Ct), HttpStatusCode.Accepted);
        await JsonAsync(await client.PostAsJsonAsync("/api/audio/speakers/kiosk-1/commands", new { action = "announce", text = "Dinner is ready" }, Ct), HttpStatusCode.Accepted);
        var first = await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("play", first[0].GetProperty("action").GetString());
        Assert.Equal(track.ToString(), first[0].GetProperty("queue")[0].GetProperty("id").GetString());

        var all = await CommandsAsync(client, "kiosk-1", latest);
        Assert.Equal(["play", "announce"], all.Select(c => c.GetProperty("action").GetString()));
        Assert.True(all[1].GetProperty("seq").GetInt64() > all[0].GetProperty("seq").GetInt64());
        Assert.Equal("Dinner is ready", all[1].GetProperty("text").GetString());
        Assert.False(all[1].TryGetProperty("queue", out _), "unset fields are left out");

        await JsonAsync(await client.PutAsJsonAsync("/api/audio/speakers/kiosk-1/state",
            new { playing = true, title = "Dreams", subtitle = "Fleetwood Mac", positionSeconds = 12, durationSeconds = 257, volume = 0.7, kind = "music" }, Ct),
            HttpStatusCode.NoContent);
        var speaker = Assert.Single((await JsonAsync(await client.GetAsync("/api/audio/speakers", Ct), HttpStatusCode.OK)).EnumerateArray());
        Assert.True(speaker.GetProperty("online").GetBoolean());
        Assert.Equal("Dreams", speaker.GetProperty("state").GetProperty("title").GetString());
        Assert.Equal(0.7, speaker.GetProperty("volume").GetDouble());
    }

    [Fact]
    public async Task SpeakerCommands_AreChecked_AndNeedARegisteredSpeaker()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await JsonAsync(await client.PostAsJsonAsync("/api/audio/speakers/nobody/commands", new { action = "pause" }, Ct), HttpStatusCode.NotFound);
        await RegisterSpeakerAsync(client, "tv");
        foreach (var bad in new object[] { new { action = "explode" }, new { action = "play" }, new { action = "announce" }, new { action = "seek" }, new { action = "volume" } })
            await JsonAsync(await client.PostAsJsonAsync("/api/audio/speakers/tv/commands", bad, Ct), HttpStatusCode.BadRequest);
        await JsonAsync(await client.PutAsJsonAsync("/api/audio/speakers/tv", new { name = " " }, Ct), HttpStatusCode.BadRequest);
        await JsonAsync(await client.DeleteAsync("/api/audio/speakers/tv", Ct), HttpStatusCode.NoContent);
        Assert.Empty((await JsonAsync(await client.GetAsync("/api/audio/speakers", Ct), HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task SpeakerHub_DropsCommandsNobodyCollectedWithinTwoMinutes()
    {
        var clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var hub = new AudioSpeakerHub(clock);
        hub.Send("tv", new AudioSpeakerCommand { Action = "pause" });
        clock.Now += TimeSpan.FromMinutes(3);
        hub.Send("tv", new AudioSpeakerCommand { Action = "resume" });
        var (commands, _) = await hub.WaitAsync("tv", 0, TimeSpan.Zero, Ct);
        Assert.Equal(["resume"], commands.Select(c => c.Action));
    }

    // MARK: - Wake-up alarms

    [Fact]
    public void AlarmSchedule_FindsTheNextChosenDay_HandlesOnceAndTheSpringForwardGap()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var alarm = new AudioWakeAlarm { Time = "06:45", Days = ["mon", "tue", "wed", "thu", "fri"] };
        // Friday 2026-10-02 07:00 Central: next is Monday 06:45.
        var friday = new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 6, 45, 0, TimeSpan.FromHours(-5)), AudioAlarmSchedule.Next(alarm, friday, chicago));
        // Before 06:45 on a weekday it is today.
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 6, 45, 0, TimeSpan.FromHours(-5)),
            AudioAlarmSchedule.Next(alarm, friday.AddHours(-1), chicago));

        var once = new AudioWakeAlarm { Time = "06:45" };
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 6, 45, 0, TimeSpan.FromHours(-5)), AudioAlarmSchedule.Next(once, friday, chicago));

        // 2:30 does not exist on 2026-03-08 in Chicago: it rings at 3:30 CDT.
        var early = new AudioWakeAlarm { Time = "02:30", Days = ["sun"] };
        var saturday = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-6));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-5)), AudioAlarmSchedule.Next(early, saturday, chicago));

        Assert.Null(AudioAlarmSchedule.Next(new AudioWakeAlarm { Time = "25:00" }, friday, chicago));
        Assert.Null(AudioAlarmSchedule.Next(new AudioWakeAlarm { Time = "06:00", Enabled = false }, friday, chicago));
    }

    [Fact]
    public async Task Alarms_AreValidated_AndTestPlaysTheSourceOnTheSpeaker()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var bad = await JsonAsync(await client.PostAsJsonAsync("/api/audio/alarms",
            new { time = "7 o'clock", days = new[] { "funday" }, speakerId = "ghost", source = new { type = "movie" } }, Ct), HttpStatusCode.BadRequest);
        Assert.Equal(4, bad.GetProperty("errors").GetArrayLength());

        await RegisterSpeakerAsync(client, "kiosk");
        var playlist = Guid.NewGuid().ToString();
        _music.Queues[(AudioSourceTypes.Playlist, playlist)] = [Guid.NewGuid(), Guid.NewGuid()];
        var created = await JsonAsync(await client.PostAsJsonAsync("/api/audio/alarms", new
        {
            name = "Weekdays", time = "6:45", days = new[] { "fri", "mon" }, speakerId = "kiosk",
            source = new { type = "playlist", id = playlist, name = "Morning" }, volume = 0.5, rampSeconds = 120
        }, Ct), HttpStatusCode.Created);
        Assert.Equal("06:45", created.GetProperty("time").GetString());
        Assert.Equal(["mon", "fri"], created.GetProperty("days").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(JsonValueKind.String, created.GetProperty("nextFireAt").ValueKind);

        var latest = (await JsonAsync(await client.GetAsync("/api/audio/speakers/kiosk/commands?after=-1&wait=0", Ct), HttpStatusCode.OK)).GetProperty("latestSeq").GetInt64();
        await JsonAsync(await client.PostAsync($"/api/audio/alarms/{created.GetProperty("id").GetString()}/test", null, Ct), HttpStatusCode.Accepted);
        var play = Assert.Single(await CommandsAsync(client, "kiosk", latest));
        Assert.Equal("play", play.GetProperty("action").GetString());
        Assert.Equal(2, play.GetProperty("queue").GetArrayLength());
        Assert.Equal(5, play.GetProperty("rampSeconds").GetDouble());
        Assert.Equal("Morning", play.GetProperty("context").GetProperty("name").GetString());

        // Briefing: the spoken part first, then the episodes.
        var episode = Guid.NewGuid();
        _podcasts.Briefing = [new AudioEntry(episode, new { id = episode, title = "Today's news" })];
        var briefing = await JsonAsync(await client.PutAsJsonAsync($"/api/audio/alarms/{created.GetProperty("id").GetString()}", new
        {
            name = "Weekdays", time = "06:45", days = new[] { "mon" }, speakerId = "kiosk", source = new { type = "briefing" }
        }, Ct), HttpStatusCode.OK);
        latest = (await JsonAsync(await client.GetAsync("/api/audio/speakers/kiosk/commands?after=-1&wait=0", Ct), HttpStatusCode.OK)).GetProperty("latestSeq").GetInt64();
        await JsonAsync(await client.PostAsync($"/api/audio/alarms/{briefing.GetProperty("id").GetString()}/test", null, Ct), HttpStatusCode.Accepted);
        var commands = await CommandsAsync(client, "kiosk", latest);
        Assert.Equal(["announce", "play"], commands.Select(c => c.GetProperty("action").GetString()));
        Assert.Contains("Here's your news.", commands[0].GetProperty("text").GetString());
        Assert.Equal(episode.ToString(), commands[1].GetProperty("queue")[0].GetProperty("id").GetString());
        Assert.Equal("episode", commands[1].GetProperty("queue")[0].GetProperty("type").GetString());

        Assert.Single((await JsonAsync(await client.GetAsync("/api/audio/alarms", Ct), HttpStatusCode.OK)).EnumerateArray());
        await JsonAsync(await client.DeleteAsync($"/api/audio/alarms/{created.GetProperty("id").GetString()}", Ct), HttpStatusCode.NoContent);
        Assert.Empty((await JsonAsync(await client.GetAsync("/api/audio/alarms", Ct), HttpStatusCode.OK)).EnumerateArray());
    }

    [Fact]
    public async Task AlarmWorker_RingsDueAlarms_SkipsVeryLateOnes_TurnsOffOnceAlarms_AndTellsThePersonWhenTheSpeakerIsOffline()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await RegisterSpeakerAsync(client, "kiosk");
        var store = factory.Services.GetRequiredService<SqliteAudioStore>();
        var hub = factory.Services.GetRequiredService<AudioSpeakerHub>();
        var shuffle = Guid.NewGuid();
        _music.Queues[(AudioSourceTypes.Shuffle, null)] = [shuffle];

        // Far in the future, so the server's own worker (on the real clock) leaves these alone.
        var due = new DateTimeOffset(2031, 6, 2, 11, 45, 0, TimeSpan.Zero);
        var clock = new ManualClock(due.AddSeconds(5));
        var worker = new AudioAlarmWorker(store, factory.Services.GetRequiredService<AudioDirector>(), _notifier, clock, NullLogger<AudioAlarmWorker>.Instance)
        {
            TimeZone = TimeZoneInfo.Utc
        };
        var onTime = new AudioWakeAlarm { Id = Guid.NewGuid(), UserKey = "", Name = "Once", Time = "11:45", SpeakerId = "kiosk", NextFireAt = due,
            Source = new AudioAlarmSource(AudioSourceTypes.Shuffle, null, "Shuffle"), Volume = 0.3, RampSeconds = 90 };
        var late = new AudioWakeAlarm { Id = Guid.NewGuid(), UserKey = "", Name = "Late", Time = "11:00", Days = ["mon"], SpeakerId = "kiosk",
            NextFireAt = due.AddMinutes(-45), Source = new AudioAlarmSource(AudioSourceTypes.Shuffle, null, null) };
        var offline = new AudioWakeAlarm { Id = Guid.NewGuid(), UserKey = "", Name = "Bedroom", Time = "11:45", Days = ["mon"], SpeakerId = "gone",
            NextFireAt = due, Source = new AudioAlarmSource(AudioSourceTypes.Shuffle, null, null) };
        foreach (var alarm in new[] { onTime, late, offline }) await store.SaveAlarmAsync(alarm, Ct);

        var latest = hub.LatestSeq;
        await worker.RunOnceAsync(Ct);

        var (commands, _) = await hub.WaitAsync("kiosk", latest, TimeSpan.Zero, Ct);
        var play = Assert.Single(commands);
        Assert.Equal(90, play.RampSeconds);
        Assert.Equal(0.3, play.Volume);
        Assert.Equal(shuffle, Assert.Single(play.Queue!).Id);

        var saved = (await store.GetAlarmsAsync(null, Ct)).ToDictionary(a => a.Name);
        Assert.False(saved["Once"].Enabled);
        Assert.Null(saved["Once"].NextFireAt);
        Assert.Null(saved["Late"].LastFiredAt);
        Assert.Equal(new DateTimeOffset(2031, 6, 9, 11, 0, 0, TimeSpan.Zero), saved["Late"].NextFireAt);
        var push = Assert.Single(_notifier.Sent);
        Assert.Contains("11:45 AM alarm couldn't play", push.Body);
        Assert.Contains("isn't set up", push.Body);
    }

    // MARK: - Briefing

    private static OrganizerItem Item(string kind, string json, string? parent = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = kind,
        SpaceId = "family",
        ParentId = parent,
        Fields = JsonNode.Parse(json)!.AsObject()
    };

    private static AudioBriefingBuilder Briefing() =>
        new(null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<AudioBriefingBuilder>.Instance) { TimeZone = TimeZoneInfo.Utc };

    [Fact]
    public void Briefing_ReadsTodaysEventsInOrder_IncludingRepeatingAndAllDayOnes()
    {
        var now = new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero); // a Friday
        var lines = Briefing().EventLines(
        [
            Item("event", """{ "title": "Soccer practice", "start": "2026-10-02T17:00:00Z", "end": "2026-10-02T18:00:00Z" }"""),
            Item("event", """{ "title": "Dentist", "start": "2026-10-02T09:30:00Z" }"""),
            Item("event", """{ "title": "Grandma's birthday", "start": "2020-10-02", "allDay": true, "recurrence": "FREQ=YEARLY" }"""),
            Item("event", """{ "title": "Standup", "start": "2026-09-07T08:15:00Z", "recurrence": "FREQ=WEEKLY;BYDAY=MO,WE,FR" }"""),
            Item("event", """{ "title": "Skipped standup", "start": "2026-09-07T08:15:00Z", "recurrence": "FREQ=WEEKLY;BYDAY=FR", "exdates": ["2026-10-02T08:15:00Z"] }"""),
            Item("event", """{ "title": "Tuesday thing", "start": "2026-09-01T08:00:00Z", "recurrence": "FREQ=WEEKLY;BYDAY=TU" }"""),
            Item("event", """{ "title": "Tomorrow", "start": "2026-10-03T08:00:00Z" }"""),
        ], now).ToList();
        Assert.Equal(["You have 4 events today: Grandma's birthday, all day, Standup at 8:15 AM, Dentist at 9:30 AM, and Soccer practice at 5 PM."], lines);
    }

    [Fact]
    public void Briefing_SaysTheCalendarIsClear_AndCountsRemindersDueAndOverdue()
    {
        var now = new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero);
        var builder = Briefing();
        Assert.Equal(["Your calendar is clear today."], builder.EventLines([], now));
        var parent = Item("reminder", """{ "title": "Trip prep", "due": "2026-10-02" }""");
        var lines = builder.ReminderLines(
        [
            Item("reminder", """{ "title": "Take out the bins", "due": "2026-10-02T18:00:00Z" }"""),
            parent,
            Item("reminder", """{ "title": "Pack socks", "due": "2026-10-02" }""", parent.Id),
            Item("reminder", """{ "title": "Done already", "due": "2026-10-02", "completed": true }"""),
            Item("reminder", """{ "title": "Renew licence", "due": "2026-09-20" }"""),
            Item("reminder", """{ "title": "Someday" }"""),
        ], now).ToList();
        Assert.Equal(["Two reminders are due today: Take out the bins and Trip prep.", "One reminder is overdue."], lines);
    }

    [Theory]
    [InlineData("FREQ=DAILY;INTERVAL=2", "2026-10-01", "2026-10-03", true)]
    [InlineData("FREQ=DAILY;INTERVAL=2", "2026-10-01", "2026-10-02", false)]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "2026-09-18", "2026-10-02", true)]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=FR", "2026-09-25", "2026-10-02", false)]
    [InlineData("FREQ=WEEKLY;BYDAY=FR;COUNT=2", "2026-09-18", "2026-10-02", false)]
    [InlineData("FREQ=WEEKLY;BYDAY=FR;UNTIL=20260930T000000Z", "2026-09-18", "2026-10-02", false)]
    [InlineData("FREQ=MONTHLY", "2026-01-02", "2026-10-02", true)]
    [InlineData("FREQ=MONTHLY;BYDAY=1FR", "2026-01-02", "2026-10-02", true)]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "2026-01-30", "2026-10-30", true)]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "2026-01-30", "2026-10-23", false)]
    [InlineData("FREQ=MONTHLY", "2026-01-31", "2026-02-28", false)]
    [InlineData("FREQ=YEARLY", "2024-02-29", "2028-02-29", true)]
    [InlineData("FREQ=YEARLY", "2024-02-29", "2026-02-28", false)]
    public void Recurrence_KnowsWhichDaysARuleLandsOn(string rule, string start, string day, bool expected) =>
        Assert.Equal(expected, AudioRecurrence.OccursOn(rule, DateOnly.Parse(start), DateOnly.Parse(day)));

    // MARK: - Stats windows

    [Fact]
    public void StatsWindow_IsThisWeekFromMonday_ThisMonth_AWholeYear_OrEverything()
    {
        var now = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal((new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)),
            AudioEndpoints.StatsWindow("week", null, now, TimeZoneInfo.Utc));
        Assert.Equal((new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)),
            AudioEndpoints.StatsWindow("month", null, now, TimeZoneInfo.Utc));
        Assert.Equal((new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            AudioEndpoints.StatsWindow("year", 2025, now, TimeZoneInfo.Utc));
        Assert.Equal(DateTimeOffset.UnixEpoch, AudioEndpoints.StatsWindow("all", null, now, TimeZoneInfo.Utc).From);
    }

    [Fact]
    public async Task Stats_CombineBothTabs_AndRejectUnknownPeriods()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var stats = await JsonAsync(await client.GetAsync("/api/audio/stats?period=year&year=2025", Ct), HttpStatusCode.OK);
        Assert.Equal("year", stats.GetProperty("period").GetString());
        Assert.Equal(12, stats.GetProperty("music").GetProperty("plays").GetInt32());
        Assert.Equal(3, stats.GetProperty("podcasts").GetProperty("episodesFinished").GetInt32());
        // A calendar year in the household's time zone (the server's).
        Assert.Equal(new DateTime(2025, 1, 1), TimeZoneInfo.ConvertTime(_music.StatsFrom!.Value, TimeZoneInfo.Local).DateTime);
        await JsonAsync(await client.GetAsync("/api/audio/stats?period=decade", Ct), HttpStatusCode.BadRequest);
    }

    // MARK: - ListenBrainz

    [Fact]
    public async Task ListenBrainz_ChecksTheToken_SubmitsListens_RetriesOutages_AndDropsRefusals()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var bad = await JsonAsync(await client.PutAsJsonAsync("/api/audio/me/listenbrainz", new { token = "nope" }, Ct), HttpStatusCode.BadRequest);
        Assert.Contains("didn't accept", bad.GetProperty("error").GetString());
        var ok = await JsonAsync(await client.PutAsJsonAsync("/api/audio/me/listenbrainz", new { token = FakeListenBrainz.Token }, Ct), HttpStatusCode.OK);
        Assert.Equal("nathan", ok.GetProperty("username").GetString());
        Assert.True((await JsonAsync(await client.GetAsync("/api/audio/me/listenbrainz", Ct), HttpStatusCode.OK)).GetProperty("connected").GetBoolean());

        var store = factory.Services.GetRequiredService<SqliteAudioStore>();
        var listenBrainz = factory.Services.GetRequiredService<AudioListenBrainz>();
        var played = new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero);
        _listenBrainz.SubmitStatus = HttpStatusCode.ServiceUnavailable;
        await store.EnqueueListenAsync("", new AudioListen("Fleetwood Mac", "Dreams", "Rumours", 257.8, played, 2), DateTimeOffset.UtcNow, Ct);
        await listenBrainz.SendDueAsync(Ct);
        Assert.Equal(1, await store.PendingListenCountAsync(Ct));

        _listenBrainz.SubmitStatus = HttpStatusCode.OK;
        // The retry waits a minute; make it due now.
        foreach (var due in await store.DueListensAsync(DateTimeOffset.UtcNow.AddHours(2), 10, Ct)) await store.RetryListenAsync(due.Id, DateTimeOffset.UtcNow.AddSeconds(-1), Ct);
        await listenBrainz.SendDueAsync(Ct);
        Assert.Equal(0, await store.PendingListenCountAsync(Ct));
        var body = JsonNode.Parse(_listenBrainz.Submitted.Last())!;
        Assert.Equal("single", body["listen_type"]!.GetValue<string>());
        var listen = body["payload"]![0]!;
        Assert.Equal(played.ToUnixTimeSeconds(), listen["listened_at"]!.GetValue<long>());
        Assert.Equal("Dreams", listen["track_metadata"]!["track_name"]!.GetValue<string>());
        Assert.Equal("Rumours", listen["track_metadata"]!["release_name"]!.GetValue<string>());
        Assert.Equal(257800, listen["track_metadata"]!["additional_info"]!["duration_ms"]!.GetValue<long>());

        _listenBrainz.SubmitStatus = HttpStatusCode.BadRequest;
        await store.EnqueueListenAsync("", new AudioListen("A", "B", null, null, played), DateTimeOffset.UtcNow, Ct);
        await listenBrainz.SendDueAsync(Ct);
        Assert.Equal(0, await store.PendingListenCountAsync(Ct));

        await JsonAsync(await client.PutAsJsonAsync("/api/audio/me/listenbrainz", new { token = "" }, Ct), HttpStatusCode.OK);
        Assert.False((await JsonAsync(await client.GetAsync("/api/audio/me/listenbrainz", Ct), HttpStatusCode.OK)).GetProperty("connected").GetBoolean());
    }

    // MARK: - Backup

    [Fact]
    public async Task Backup_CarriesAlarmsAndListenBrainzConnections_AndRestoreNeverOverwritesNewerOnes()
    {
        BackupDocument document;
        var alarmId = Guid.NewGuid();
        using (var factory = CreateFactory())
        {
            var store = factory.Services.GetRequiredService<SqliteAudioStore>();
            await store.SaveAlarmAsync(new AudioWakeAlarm { Id = alarmId, UserKey = "sam", Name = "School days", Time = "06:30", Days = ["mon"], SpeakerId = "kiosk" }, Ct);
            await store.SetListenBrainzAsync("sam", new AudioListenBrainzAccount("sam", "tok", "sam_lb"), Ct);
            document = await factory.Services.GetRequiredService<BackupService>().BuildDocumentAsync(Ct);
        }
        Assert.Equal("School days", Assert.Single(document.Audio!.Alarms!).Name);

        // Restored onto a fresh install whose own copy of that alarm was renamed since.
        var otherData = Path.Combine(_root, "other");
        Directory.CreateDirectory(otherData);
        using var restored = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, otherData));
        var target = restored.Services.GetRequiredService<SqliteAudioStore>();
        await target.SaveAlarmAsync(new AudioWakeAlarm { Id = alarmId, UserKey = "sam", Name = "Renamed", Time = "07:00", SpeakerId = "kiosk" }, Ct);
        var admin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")], "test"));
        var result = await restored.Services.GetRequiredService<BackupService>().RestoreAsync(document, null, false, admin, Ct);
        Assert.True(result.Errors is null or { Count: 0 }, string.Join("; ", result.Errors ?? []));
        Assert.Equal("Renamed", Assert.Single(await target.GetAlarmsAsync(null, Ct)).Name);
        Assert.Equal("sam_lb", (await target.GetListenBrainzAsync("sam", Ct))!.Username);
    }

    // MARK: - The announcer

    [Fact]
    public async Task HomeAlarm_PausesEverySpeaker_AndSaysWhyWhereAnnouncementsAreAllowed()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await RegisterSpeakerAsync(client, "kitchen", announcements: true);
        await RegisterSpeakerAsync(client, "bedroom", announcements: false);
        var hub = factory.Services.GetRequiredService<AudioSpeakerHub>();
        var latest = hub.LatestSeq;
        var announcer = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<AudioAnnouncer>().Single();
        await announcer.AlarmTriggeredAsync("Front door opened", Ct);

        var (kitchen, _) = await hub.WaitAsync("kitchen", latest, TimeSpan.Zero, Ct);
        Assert.Equal(["pause", "announce"], kitchen.Select(c => c.Action));
        Assert.Equal("The alarm is going off. Front door opened.", kitchen[1].Text);
        var (bedroom, _) = await hub.WaitAsync("bedroom", latest, TimeSpan.Zero, Ct);
        Assert.Equal(["pause"], bedroom.Select(c => c.Action));
    }

    [Fact]
    public void SpokenAlert_NamesTheCamera_AndKeepsItShort()
    {
        Assert.Equal("Front Door: A person is at the door.", AudioAnnouncer.SpokenAlert("Front Door", "A person is at the door"));
        Assert.Equal("Drive: Someone is there.", AudioAnnouncer.SpokenAlert("Drive", null));
        var long_ = AudioAnnouncer.SpokenAlert("Yard", string.Join(" ", Enumerable.Repeat("word", 80)));
        Assert.True(long_.Length < 160, long_);
        Assert.EndsWith("word.", long_);
    }

    // MARK: - Folder picker

    [Fact]
    public async Task Browse_CountsAudioFilesNotVideos()
    {
        var folder = Path.Combine(_root, "Music");
        Directory.CreateDirectory(Path.Combine(folder, "Album"));
        File.WriteAllText(Path.Combine(folder, "a.mp3"), "x");
        File.WriteAllText(Path.Combine(folder, "b.flac"), "x");
        File.WriteAllText(Path.Combine(folder, "film.mkv"), "x");
        File.WriteAllText(Path.Combine(folder, "Album", "c.m4a"), "x");
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var page = await JsonAsync(await client.GetAsync($"/api/audio/browse?path={Uri.EscapeDataString(folder)}", Ct), HttpStatusCode.OK);
        Assert.Equal(2, page.GetProperty("audioCount").GetInt32());
        Assert.Equal(2, page.GetProperty("videoCount").GetInt32());
        var album = Assert.Single(page.GetProperty("folders").EnumerateArray());
        Assert.Equal(1, album.GetProperty("audioCount").GetInt32());
        // The Movies picker still counts videos.
        File.WriteAllText(_settingsPath, """{ "Auth": { "Enabled": false }, "Media": { "Enabled": true } }""");
        var movies = await JsonAsync(await client.GetAsync($"/api/media/browse?path={Uri.EscapeDataString(folder)}", Ct), HttpStatusCode.OK);
        Assert.Equal(1, movies.GetProperty("videoCount").GetInt32());
    }

    [Fact]
    public async Task NewFolder_MakesTheFolderAndOpensIt()
    {
        var folder = Path.Combine(_root, "Library");
        Directory.CreateDirectory(folder);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var made = await JsonAsync(await client.PostAsJsonAsync("/api/audio/browse/folder", new { parent = folder, name = " Podcasts " }, Ct), HttpStatusCode.OK);
        Assert.Equal(Path.Combine(folder, "Podcasts"), made.GetProperty("path").GetString());
        Assert.Equal(folder, made.GetProperty("parent").GetString());
        Assert.Equal(0, made.GetProperty("audioCount").GetInt32());
        Assert.True(Directory.Exists(Path.Combine(folder, "Podcasts")));

        // Already there is fine: the picker just opens it.
        await JsonAsync(await client.PostAsJsonAsync("/api/audio/browse/folder", new { parent = folder, name = "Podcasts" }, Ct), HttpStatusCode.OK);

        // The Movies route does the same, for a TV folder.
        File.WriteAllText(_settingsPath, """{ "Auth": { "Enabled": false }, "Media": { "Enabled": true } }""");
        var tv = await JsonAsync(await client.PostAsJsonAsync("/api/media/browse/folder", new { parent = folder, name = "TV Shows" }, Ct), HttpStatusCode.OK);
        Assert.Equal("local", tv.GetProperty("kind").GetString());
        Assert.True(Directory.Exists(Path.Combine(folder, "TV Shows")));

        foreach (var bad in new[] { "", "a/b", "..", "what?" })
            await JsonAsync(await client.PostAsJsonAsync("/api/media/browse/folder", new { parent = folder, name = bad }, Ct), HttpStatusCode.BadRequest);
        await JsonAsync(await client.PostAsJsonAsync("/api/media/browse/folder", new { parent = Path.Combine(folder, "Gone"), name = "X" }, Ct), HttpStatusCode.BadRequest);
        await JsonAsync(await client.PostAsJsonAsync("/api/media/browse/folder", new { parent = "", name = "X" }, Ct), HttpStatusCode.BadRequest);
    }

    // MARK: - Fakes

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeMusic : IMusicAudioSource
    {
        public bool Enabled = true;
        public readonly HashSet<Guid> Known = [];
        public readonly Dictionary<(string, string?), List<Guid>> Queues = [];
        public DateTimeOffset? StatsFrom;

        public Task<bool> EnabledAsync(CancellationToken cancellationToken) => Task.FromResult(Enabled);
        public Task<IReadOnlyList<AudioEntry>> ResolveTracksAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioEntry>>(ids.Where(Known.Contains).Select(id => new AudioEntry(id, new { id, title = "A track" })).ToList());
        public Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(Queues.TryGetValue((type, id), out var queue) ? queue : []);
        public Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            StatsFrom = from;
            return Task.FromResult<object>(new { plays = 12 });
        }
        public Task<IReadOnlyList<AudioLibraryInfo>> LibrariesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AudioLibraryInfo>>([]);
        public void RequestScan(Guid libraryId) { }
        public Task<bool> HasAlbumAsync(string artist, string album, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<double> LosslessShareAsync(CancellationToken cancellationToken) => Task.FromResult(0.0);
        public Task<MusicBackup> ExportAsync(CancellationToken cancellationToken) => Task.FromResult(new MusicBackup());
        public Task<int> RestoreAsync(MusicBackup backup, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class FakePodcasts : IPodcastAudioSource
    {
        public bool Enabled = true;
        public IReadOnlyList<AudioEntry> Briefing = [];

        public Task<bool> EnabledAsync(CancellationToken cancellationToken) => Task.FromResult(Enabled);
        public Task<IReadOnlyList<AudioEntry>> ResolveEpisodesAsync(string userKey, bool isAdmin, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioEntry>>([]);
        public Task<IReadOnlyList<AudioEntry>> BriefingEpisodesAsync(string userKey, int max, CancellationToken cancellationToken) => Task.FromResult(Briefing);
        public Task<IReadOnlyList<Guid>> QueueForSourceAsync(string userKey, string type, string? id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<object> StatsAsync(string userKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { episodesFinished = 3 });
        public Task<PodcastBackup> ExportAsync(CancellationToken cancellationToken) => Task.FromResult(new PodcastBackup());
        public Task<int> RestoreAsync(PodcastBackup backup, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class FakeNotifier : IAudioNotifier
    {
        public readonly List<(string UserKey, string Title, string Body)> Sent = [];

        public Task PushAsync(string userKey, string title, string body, CancellationToken cancellationToken)
        {
            Sent.Add((userKey, title, body));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeListenBrainz : HttpMessageHandler
    {
        public const string Token = "good-token";
        public HttpStatusCode SubmitStatus = HttpStatusCode.OK;
        public readonly List<string> Submitted = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter;
            if (request.RequestUri!.AbsolutePath == "/1/validate-token")
                return Json(token == Token ? """{ "valid": true, "user_name": "nathan" }""" : """{ "valid": false, "message": "Invalid token" }""");
            if (request.RequestUri.AbsolutePath == "/1/submit-listens")
            {
                Submitted.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(SubmitStatus) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
