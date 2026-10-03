using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

public static class AudioFileTypes
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".alac", ".ogg", ".oga", ".opus", ".wav", ".aif", ".aiff",
        ".wma", ".ape", ".wv", ".dsf", ".dff"
    };

    public static bool IsAudioFile(string path) => Extensions.Contains(Path.GetExtension(path));
}

// What the Music and Podcasts tabs share over HTTP: the folder picker, play where you left off,
// speakers, wake-up alarms, the morning briefing, stats and ListenBrainz. Contract:
// core/docs/MUSIC_AND_PODCASTS.md §1 and §7-§9.
public static class AudioEndpoints
{
    private const string OffMessage = "Music and Podcasts are both turned off in Settings.";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    public static void MapAudioEndpoints(this WebApplication app)
    {
        var audio = app.MapGroup("/api/audio").RequireAuthorization();

        audio.MapGet("/enabled", async (IMusicAudioSource music, IPodcastAudioSource podcasts, CancellationToken ct) =>
            Results.Ok(new { music = await music.EnabledAsync(ct), podcasts = await podcasts.EnabledAsync(ct) }));

        var enabled = audio.MapGroup("").AddEndpointFilter(async (invocation, next) =>
        {
            var services = invocation.HttpContext.RequestServices;
            var ct = invocation.HttpContext.RequestAborted;
            var on = await services.GetRequiredService<IMusicAudioSource>().EnabledAsync(ct)
                     || await services.GetRequiredService<IPodcastAudioSource>().EnabledAsync(ct);
            return on ? await next(invocation) : Results.NotFound(new { error = OffMessage });
        });

        MapFolders(enabled);
        MapNowPlaying(enabled);
        MapSpeakers(enabled);
        MapAlarms(enabled);
        MapExtras(enabled);
    }

    // MARK: - Folder picker (admin)

    private static void MapFolders(RouteGroupBuilder enabled)
    {
        // The Movies picker, counting audio files instead of videos: `videoCount` keeps its name
        // so every app can reuse its Movies picker, and `audioCount` says the same thing plainly.
        enabled.MapGet("/browse", async (string? path, MediaFiles files, CancellationToken ct) =>
        {
            var result = await Task.Run(() => MediaEndpoints.Browse(path, files, AudioFileTypes.IsAudioFile), ct);
            var node = JsonSerializer.SerializeToNode(result, WebJson)!.AsObject();
            node["audioCount"] = node["videoCount"]?.DeepClone();
            foreach (var folder in node["folders"] as JsonArray ?? [])
                if (folder is JsonObject entry) entry["audioCount"] = entry["videoCount"]?.DeepClone();
            return Results.Ok(node);
        }).RequireAuthorization("AdminOnly");

        // The same saved sign-ins as Movies: a computer signed in to there already works here.
        MediaEndpoints.MapNetwork(enabled);
    }

    // MARK: - Play where you left off

    private static void MapNowPlaying(RouteGroupBuilder enabled)
    {
        enabled.MapPut("/now-playing", async (AudioNowPlayingInput input, HttpContext context, SqliteAudioStore store, TimeProvider clock, CancellationToken ct) =>
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(input.DeviceId) || input.DeviceId.Length > 200) errors.Add("deviceId is required.");
            if (input.Kind is not ("music" or "podcast")) errors.Add("kind must be music or podcast.");
            var queue = input.Queue ?? [];
            if (queue.Any(e => e.Type is not (AudioItemTypes.Track or AudioItemTypes.Episode))) errors.Add("Queue entries are tracks or episodes.");
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var index = queue.Count == 0 ? 0 : Math.Clamp(input.Index, 0, queue.Count - 1);
            // A long queue keeps the 500 around where the person is.
            if (queue.Count > AudioNowPlaying.MaxQueue)
            {
                var start = Math.Clamp(index - AudioNowPlaying.MaxQueue / 2, 0, queue.Count - AudioNowPlaying.MaxQueue);
                queue = queue.GetRange(start, AudioNowPlaying.MaxQueue);
                index -= start;
            }
            await store.SaveNowPlayingAsync(UserKey(context), new AudioNowPlaying
            {
                DeviceId = input.DeviceId.Trim(),
                DeviceName = string.IsNullOrWhiteSpace(input.DeviceName) ? "A device" : input.DeviceName.Trim(),
                Kind = input.Kind,
                Queue = queue,
                Index = index,
                PositionSeconds = Math.Max(0, input.PositionSeconds),
                Playing = input.Playing,
                Shuffle = input.Shuffle,
                Repeat = input.Repeat is "all" or "one" ? input.Repeat : "off",
                Speed = input.Speed is >= 0.25 and <= 4 ? input.Speed.Value : 1,
                Context = input.Context,
                UpdatedAt = clock.GetUtcNow()
            }, ct);
            return Results.NoContent();
        });

        enabled.MapGet("/now-playing", async (HttpContext context, SqliteAudioStore store, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await store.GetNowPlayingAsync(UserKey(context), clock.GetUtcNow(), ct)));

        enabled.MapDelete("/now-playing/{deviceId}", async (string deviceId, HttpContext context, SqliteAudioStore store, CancellationToken ct) =>
        {
            await store.DeleteNowPlayingAsync(UserKey(context), deviceId, ct);
            return Results.NoContent();
        });

        // Everything a device needs to take over in one request.
        enabled.MapGet("/now-playing/{deviceId}/resolve", async (
            string deviceId, HttpContext context, SqliteAudioStore store, IMusicAudioSource music, IPodcastAudioSource podcasts,
            TimeProvider clock, CancellationToken ct) =>
        {
            var userKey = UserKey(context);
            var nowPlaying = (await store.GetNowPlayingAsync(userKey, clock.GetUtcNow(), ct)).FirstOrDefault(n => n.DeviceId == deviceId);
            if (nowPlaying is null) return Results.NotFound(new { error = "That device isn't playing anything any more." });
            var (tracks, episodes) = await ResolveAsync(nowPlaying.Queue, userKey, IsAdmin(context), music, podcasts, ct);
            return Results.Ok(new { nowPlaying, tracks = tracks.Select(t => t.Dto), episodes = episodes.Select(e => e.Dto) });
        });
    }

    private static async Task<(IReadOnlyList<AudioEntry> Tracks, IReadOnlyList<AudioEntry> Episodes)> ResolveAsync(
        IReadOnlyList<AudioQueueEntry> queue, string userKey, bool isAdmin, IMusicAudioSource music, IPodcastAudioSource podcasts, CancellationToken ct)
    {
        var trackIds = queue.Where(e => e.Type == AudioItemTypes.Track).Select(e => e.Id).Distinct().ToList();
        var episodeIds = queue.Where(e => e.Type == AudioItemTypes.Episode).Select(e => e.Id).Distinct().ToList();
        IReadOnlyList<AudioEntry> tracks = trackIds.Count > 0 && await music.EnabledAsync(ct)
            ? await music.ResolveTracksAsync(userKey, isAdmin, trackIds, ct) : [];
        IReadOnlyList<AudioEntry> episodes = episodeIds.Count > 0 && await podcasts.EnabledAsync(ct)
            ? await podcasts.ResolveEpisodesAsync(userKey, isAdmin, episodeIds, ct) : [];
        return (tracks, episodes);
    }

    // MARK: - Speakers

    private static void MapSpeakers(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/speakers", async (SqliteAudioStore store, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            return Results.Ok((await store.GetSpeakersAsync(ct))
                .OrderByDescending(s => s.IsOnline(now))
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => SpeakerDto(s, now)));
        });

        // The heartbeat: on start and every minute while "Let others play here" is on.
        enabled.MapPut("/speakers/{deviceId}", async (string deviceId, AudioSpeakerInput input, SqliteAudioStore store, TimeProvider clock, CancellationToken ct) =>
        {
            if (deviceId.Length is 0 or > 200) return Results.BadRequest(new { errors = new[] { "deviceId is required." } });
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { errors = new[] { "Give the speaker a name, like Kitchen or Living Room TV." } });
            var existing = (await store.GetSpeakersAsync(ct)).FirstOrDefault(s => s.DeviceId == deviceId);
            await store.SaveSpeakerAsync(new AudioSpeaker
            {
                DeviceId = deviceId,
                Name = input.Name.Trim()[..Math.Min(input.Name.Trim().Length, 80)],
                Kind = AudioSpeaker.Kinds.Contains(input.Kind) ? input.Kind : "web",
                AllowAnnouncements = input.AllowAnnouncements,
                Volume = Math.Clamp(input.Volume ?? existing?.Volume ?? 0.6, 0, 1),
                LastSeenAt = clock.GetUtcNow(),
                State = existing?.State
            }, ct);
            return Results.NoContent();
        });

        enabled.MapDelete("/speakers/{deviceId}", async (string deviceId, SqliteAudioStore store, CancellationToken ct) =>
        {
            await store.DeleteSpeakerAsync(deviceId, ct);
            return Results.NoContent();
        });

        enabled.MapPut("/speakers/{deviceId}/state", async (string deviceId, AudioSpeakerState state, SqliteAudioStore store, TimeProvider clock, CancellationToken ct) =>
        {
            var speaker = (await store.GetSpeakersAsync(ct)).FirstOrDefault(s => s.DeviceId == deviceId);
            if (speaker is null) return Results.NotFound(new { error = "Register the speaker first (PUT /api/audio/speakers/{deviceId})." });
            speaker.State = state;
            if (state.Volume is { } volume) speaker.Volume = Math.Clamp(volume, 0, 1);
            speaker.LastSeenAt = clock.GetUtcNow();
            await store.SaveSpeakerAsync(speaker, ct);
            return Results.NoContent();
        });

        enabled.MapPost("/speakers/{deviceId}/commands", async (
            string deviceId, AudioSpeakerCommandInput input, HttpContext context, SqliteAudioStore store, AudioSpeakerHub hub, CancellationToken ct) =>
        {
            var errors = new List<string>();
            if (!AudioSpeakerActions.All.Contains(input.Action)) errors.Add($"action must be one of {string.Join(", ", AudioSpeakerActions.All)}.");
            if (input.Action == AudioSpeakerActions.Play && (input.Queue is null || input.Queue.Count == 0)) errors.Add("play needs a queue.");
            if (input.Queue?.Any(e => e.Type is not (AudioItemTypes.Track or AudioItemTypes.Episode)) == true) errors.Add("Queue entries are tracks or episodes.");
            if (input.Action == AudioSpeakerActions.Announce && string.IsNullOrWhiteSpace(input.Text)) errors.Add("announce needs text.");
            if (input.Action == AudioSpeakerActions.Seek && input.PositionSeconds is null) errors.Add("seek needs positionSeconds.");
            if (input.Action == AudioSpeakerActions.Volume && input.Volume is null) errors.Add("volume needs volume.");
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            if ((await store.GetSpeakersAsync(ct)).All(s => s.DeviceId != deviceId))
                return Results.NotFound(new { error = "That speaker isn't set up any more." });

            var seq = hub.Send(deviceId, new AudioSpeakerCommand
            {
                Action = input.Action,
                Queue = input.Queue?.Take(AudioNowPlaying.MaxQueue).ToList(),
                Index = input.Index,
                PositionSeconds = input.PositionSeconds,
                Shuffle = input.Shuffle,
                Volume = input.Volume is { } v ? Math.Clamp(v, 0, 1) : null,
                RampSeconds = input.RampSeconds is { } r ? Math.Clamp(r, 0, 1800) : null,
                Text = input.Text?.Trim(),
                Context = input.Context,
                From = DisplayName(context)
            });
            return Results.Accepted(value: new { seq });
        });

        enabled.MapGet("/speakers/{deviceId}/commands", async (
            string deviceId, long? after, int? wait, SqliteAudioStore store, AudioSpeakerHub hub, TimeProvider clock, CancellationToken ct) =>
        {
            // Asking for commands is a heartbeat too, so a speaker that is busy long-polling never
            // looks offline between its minute heartbeats.
            var speaker = (await store.GetSpeakersAsync(ct)).FirstOrDefault(s => s.DeviceId == deviceId);
            if (speaker is not null)
            {
                speaker.LastSeenAt = clock.GetUtcNow();
                await store.SaveSpeakerAsync(speaker, ct);
            }
            var timeout = TimeSpan.FromSeconds(Math.Clamp(wait ?? 25, 0, (int)MaxWait.TotalSeconds));
            try
            {
                var (commands, latest) = await hub.WaitAsync(deviceId, after ?? -1, timeout, ct);
                return Results.Ok(new { commands, latestSeq = latest });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });
    }

    private static object SpeakerDto(AudioSpeaker speaker, DateTimeOffset now) => new
    {
        deviceId = speaker.DeviceId,
        name = speaker.Name,
        kind = speaker.Kind,
        online = speaker.IsOnline(now),
        allowAnnouncements = speaker.AllowAnnouncements,
        volume = speaker.Volume,
        lastSeenAt = speaker.LastSeenAt,
        state = speaker.IsOnline(now) ? speaker.State : null
    };

    // MARK: - Wake-up alarms

    private static void MapAlarms(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/alarms", async (HttpContext context, SqliteAudioStore store, CancellationToken ct) =>
            Results.Ok((await store.GetAlarmsAsync(UserKey(context), ct)).Select(AlarmDto)));

        enabled.MapPost("/alarms", async (AudioWakeAlarmInput input, HttpContext context, SqliteAudioStore store, AudioAlarmWorker worker, TimeProvider clock, CancellationToken ct) =>
        {
            var alarm = new AudioWakeAlarm { Id = Guid.NewGuid(), UserKey = UserKey(context), CreatedAt = clock.GetUtcNow() };
            var errors = await ApplyAlarmAsync(alarm, input, store, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            alarm.NextFireAt = AudioAlarmSchedule.Next(alarm, clock.GetUtcNow(), worker.TimeZone);
            await store.SaveAlarmAsync(alarm, ct);
            return Results.Created($"/api/audio/alarms/{alarm.Id}", AlarmDto(alarm));
        });

        enabled.MapPut("/alarms/{id:guid}", async (Guid id, AudioWakeAlarmInput input, HttpContext context, SqliteAudioStore store, AudioAlarmWorker worker, TimeProvider clock, CancellationToken ct) =>
        {
            var alarm = (await store.GetAlarmsAsync(UserKey(context), ct)).FirstOrDefault(a => a.Id == id);
            if (alarm is null) return Results.NotFound(new { error = "That alarm no longer exists." });
            var errors = await ApplyAlarmAsync(alarm, input, store, ct);
            if (errors.Count > 0) return Results.BadRequest(new { errors });
            alarm.NextFireAt = AudioAlarmSchedule.Next(alarm, clock.GetUtcNow(), worker.TimeZone);
            await store.SaveAlarmAsync(alarm, ct);
            return Results.Ok(AlarmDto(alarm));
        });

        enabled.MapDelete("/alarms/{id:guid}", async (Guid id, HttpContext context, SqliteAudioStore store, CancellationToken ct) =>
        {
            if ((await store.GetAlarmsAsync(UserKey(context), ct)).Any(a => a.Id == id)) await store.DeleteAlarmAsync(id, ct);
            return Results.NoContent();
        });

        enabled.MapPost("/alarms/{id:guid}/test", async (Guid id, HttpContext context, SqliteAudioStore store, AudioDirector director, CancellationToken ct) =>
        {
            var alarm = (await store.GetAlarmsAsync(UserKey(context), ct)).FirstOrDefault(a => a.Id == id);
            if (alarm is null) return Results.NotFound(new { error = "That alarm no longer exists." });
            // A test fades up over a few seconds, not the alarm's two minutes: whoever pressed it
            // wants to hear that it works.
            var result = await director.PlaySourceAsync(alarm.UserKey, alarm.SpeakerId, alarm.Source, alarm.Volume,
                Math.Min(alarm.RampSeconds, 5), $"Alarm test: {alarm.Name}", ct);
            return result.Ok ? Results.Accepted(value: new { seq = result.Seq }) : Results.BadRequest(new { error = result.Error });
        });
    }

    private static async Task<List<string>> ApplyAlarmAsync(AudioWakeAlarm alarm, AudioWakeAlarmInput input, SqliteAudioStore store, CancellationToken ct)
    {
        var errors = new List<string>();
        if (!AudioAlarmSchedule.TryParseTime(input.Time, out var time)) errors.Add("Set a time like 06:45.");
        var days = (input.Days ?? []).Select(d => d.Trim().ToLowerInvariant()).Distinct().ToList();
        if (days.Any(d => !AudioWakeAlarm.DayNames.Contains(d))) errors.Add("Days are mon, tue, wed, thu, fri, sat and sun.");
        if (string.IsNullOrWhiteSpace(input.SpeakerId)) errors.Add("Choose the speaker it plays on.");
        else if ((await store.GetSpeakersAsync(ct)).All(s => s.DeviceId != input.SpeakerId))
            errors.Add("That speaker isn't set up. Turn on \"Let others play here\" on the device first.");
        var source = input.Source;
        if (source is null || !(AudioSourceTypes.IsMusic(source.Type) || source.Type is AudioSourceTypes.Podcast or AudioSourceTypes.Briefing))
            errors.Add("Choose what it plays: a playlist, album, artist, mix, shuffle, a podcast or the morning briefing.");
        else if (source.Type is not (AudioSourceTypes.Shuffle or AudioSourceTypes.Briefing) && string.IsNullOrWhiteSpace(source.Id))
            errors.Add($"Choose which {source.Type} it plays.");
        if (errors.Count > 0) return errors;

        alarm.Name = string.IsNullOrWhiteSpace(input.Name) ? time.ToString("h:mm tt", CultureInfo.InvariantCulture) : input.Name.Trim();
        alarm.Time = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        alarm.Days = AudioWakeAlarm.DayNames.Where(days.Contains).ToList();
        alarm.SpeakerId = input.SpeakerId;
        alarm.Source = source!;
        alarm.Volume = Math.Clamp(input.Volume ?? alarm.Volume, 0.05, 1);
        alarm.RampSeconds = Math.Clamp(input.RampSeconds ?? alarm.RampSeconds, 0, 1800);
        alarm.Enabled = input.Enabled ?? true;
        return errors;
    }

    private static object AlarmDto(AudioWakeAlarm alarm) => new
    {
        id = alarm.Id,
        name = alarm.Name,
        time = alarm.Time,
        days = alarm.Days,
        speakerId = alarm.SpeakerId,
        source = alarm.Source,
        volume = alarm.Volume,
        rampSeconds = alarm.RampSeconds,
        enabled = alarm.Enabled,
        nextFireAt = alarm.NextFireAt,
        lastFiredAt = alarm.LastFiredAt
    };

    // MARK: - Briefing, stats, ListenBrainz

    private static void MapExtras(RouteGroupBuilder enabled)
    {
        enabled.MapGet("/briefing", async (HttpContext context, AudioBriefingBuilder briefing, CancellationToken ct) =>
        {
            var built = await briefing.BuildAsync(UserKey(context), ct);
            return Results.Ok(new { greeting = built.Greeting, speech = built.Speech, episodes = built.Episodes.Select(e => e.Dto) });
        });

        enabled.MapGet("/stats", async (string? period, int? year, HttpContext context, IMusicAudioSource music, IPodcastAudioSource podcasts,
            AudioAlarmWorker worker, TimeProvider clock, CancellationToken ct) =>
        {
            period = (period ?? "month").ToLowerInvariant();
            if (period is not ("week" or "month" or "year" or "all"))
                return Results.BadRequest(new { error = "period is week, month, year or all." });
            var (from, to) = StatsWindow(period, year, clock.GetUtcNow(), worker.TimeZone);
            var userKey = UserKey(context);
            return Results.Ok(new
            {
                period,
                from,
                to,
                music = await music.EnabledAsync(ct) ? await music.StatsAsync(userKey, from, to, ct) : null,
                podcasts = await podcasts.EnabledAsync(ct) ? await podcasts.StatsAsync(userKey, from, to, ct) : null
            });
        });

        enabled.MapGet("/me/listenbrainz", async (HttpContext context, SqliteAudioStore store, CancellationToken ct) =>
        {
            var account = await store.GetListenBrainzAsync(UserKey(context), ct);
            return Results.Ok(new { connected = account is not null, username = account?.Username });
        });

        enabled.MapPut("/me/listenbrainz", async (AudioListenBrainzInput input, HttpContext context, SqliteAudioStore store, AudioListenBrainz listenBrainz, CancellationToken ct) =>
        {
            var userKey = UserKey(context);
            var token = input.Token?.Trim() ?? "";
            if (token.Length == 0)
            {
                await store.SetListenBrainzAsync(userKey, null, ct);
                await listenBrainz.ConnectedChangedAsync(ct);
                return Results.Ok(new { connected = false, username = (string?)null });
            }
            var (username, error) = await listenBrainz.ValidateAsync(token, ct);
            if (username is null) return Results.BadRequest(new { error });
            await store.SetListenBrainzAsync(userKey, new AudioListenBrainzAccount(userKey, token, username), ct);
            await listenBrainz.ConnectedChangedAsync(ct);
            return Results.Ok(new { connected = true, username });
        });
    }

    // [from, to) for a stats period, in the household's time zone: this week from Monday, this
    // month, a whole calendar year ("Wrapped"), or everything.
    public static (DateTimeOffset From, DateTimeOffset To) StatsWindow(string period, int? year, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        DateTimeOffset At(DateTime date) => new(date, zone.GetUtcOffset(date));
        switch (period)
        {
            case "week":
            {
                var monday = local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7));
                return (At(monday).ToUniversalTime(), At(monday.AddDays(7)).ToUniversalTime());
            }
            case "month":
            {
                var first = new DateTime(local.Year, local.Month, 1);
                return (At(first).ToUniversalTime(), At(first.AddMonths(1)).ToUniversalTime());
            }
            case "year":
            {
                var y = year is >= 2000 and <= 2100 ? year.Value : local.Year;
                var first = new DateTime(y, 1, 1);
                return (At(first).ToUniversalTime(), At(first.AddYears(1)).ToUniversalTime());
            }
            default:
                return (DateTimeOffset.UnixEpoch, now.AddMinutes(1));
        }
    }

    public static string UserKey(HttpContext context) => MediaEndpoints.UserKey(context);

    private static bool IsAdmin(HttpContext context) => context.User.IsInRole(nameof(AccountRole.Admin));

    private static string? DisplayName(HttpContext context) =>
        context.User.Identity?.Name is { Length: > 0 } name ? name : null;
}
