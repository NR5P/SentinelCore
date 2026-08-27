using System.Collections.Concurrent;
using SentinelCore;

namespace SentinelCore.Web;

// Starts recording windows when their time comes, and picks up the ones a restart interrupted.
//
// The timing itself is not computed here: RecordingSchedule.ToSchedule hands the row to
// ScheduleTiming.NextOccurrence, the same pure function the main app's ScheduleWorker uses, so
// daylight saving, day-of-week sets, cron expressions and solar events behave identically in both
// places and are tested in one.
public sealed class RecordingScheduleWorker(
    IRecordingScheduleStore schedules,
    IRecordingStore recordings,
    ICameraStore cameras,
    ScheduledRecorder recorder,
    AppSettingsFile mainAppSettings,
    ILogger<RecordingScheduleWorker> logger) : BackgroundService
{
    // Also how long an edit takes to be noticed, and the worst case for how late a window can
    // start. Matches ScheduleWorker's own cadence; a recording that begins up to half a minute late
    // is a recording, while polling every second to avoid that would be a busy loop against a
    // clock.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // How far back a schedule with no run history will look for a window it should already be
    // inside. Bounds the catch-up walk on a schedule that has never fired without letting a
    // freshly-created one decide it owes a recording from last week.
    private static readonly TimeSpan ColdStartLookback = TimeSpan.FromHours(25);

    // Guards the occurrence walk below. A cron expression that fires every minute would otherwise
    // take 40,000 steps to walk a month of missed windows.
    private const int OccurrenceWalkLimit = 500;

    // Windows being recorded right now, keyed by schedule, so a pass that runs while a two-hour
    // recording is in flight doesn't start a second one on top of it. Recordings with no schedule
    // (a resumed row whose schedule was deleted) are tracked under their own id.
    private readonly ConcurrentDictionary<Guid, Task> _active = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResumeInterruptedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One malformed schedule or a database hiccup must not take the whole thing down -
                // every other window is still somebody's expectation about tonight.
                logger.LogError(ex, "A recording schedule pass failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Give anything still recording a moment to finalize its file rather than abandoning the
        // segments - RunAsync writes a partial mp4 on cancellation, but only if it gets to run.
        try { await Task.WhenAll(_active.Values).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
        catch (Exception ex) { logger.LogDebug(ex, "Recordings did not all finalize during shutdown"); }
    }

    // Rows still marked as recording when this process starts belong to a run that died with the
    // previous one. A window that has not yet ended is resumed for its remainder - the segments
    // already on disk stay, and the concatenation at the end joins both halves with the outage
    // between them. One whose window has passed is finalized from whatever was captured.
    private async Task ResumeInterruptedAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<Recording> interrupted;
        try
        {
            interrupted = await recordings.GetInProgressAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not look for interrupted recordings");
            return;
        }

        foreach (var recording in interrupted)
        {
            var camera = await cameras.GetAsync(recording.CameraId, stoppingToken);
            if (camera is null)
            {
                await recordings.FinishAsync(
                    recording.Id, DateTimeOffset.UtcNow, null, 0, Recording.StatusFailed,
                    "The camera was removed while this recording was in progress.", stoppingToken);
                continue;
            }

            // A hand-started recording is not resumed, only closed out. Its window was never an
            // intention - it is the cap on a recording somebody meant to end themselves - and the
            // press that would have ended it belongs to a session that died with the last process.
            // Resuming would leave the camera recording for the rest of the cap with no button
            // anywhere able to stop it, which is the exact runaway the cap exists to prevent.
            // Whatever was captured before the restart is finalized and kept.
            if (recording.IsManual)
            {
                logger.LogInformation(
                    "Finalizing hand-started recording on camera {CameraId} - the server restarted while it was running",
                    camera.Id);
                await recorder.FinalizeInterruptedAsync(recording, stoppingToken);
                continue;
            }

            var remaining = recording.RequestedEndUtc - DateTimeOffset.UtcNow;
            logger.LogInformation(
                remaining > TimeSpan.Zero
                    ? "Resuming recording {Name} on camera {CameraId} - {Minutes:0.#} minute(s) of its window left"
                    : "Finalizing recording {Name} on camera {CameraId} - its window ended while the server was down ({Minutes:0.#})",
                recording.Name, camera.Id, Math.Abs(remaining.TotalMinutes));

            Track(recording.ScheduleId ?? recording.Id, camera, recording, stoppingToken);
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        var all = await schedules.GetAllAsync(stoppingToken);
        if (all.Count == 0) return;

        var zone = await ResolveTimeZoneAsync(stoppingToken);
        var location = await ResolveLocationAsync(stoppingToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var schedule in all)
        {
            if (!schedule.Enabled) continue;
            if (_active.ContainsKey(schedule.Id)) continue;

            var due = FindDueWindow(schedule, now, zone, location);
            if (due is not { } window) continue;

            var camera = await cameras.GetAsync(schedule.CameraId, stoppingToken);
            if (camera is null)
            {
                await schedules.SetLastRunAsync(
                    schedule.Id, now, Recording.StatusFailed, "That camera no longer exists.", stoppingToken);
                continue;
            }

            var recording = await recordings.AddAsync(
                new Recording(
                    Guid.NewGuid(),
                    schedule.CameraId,
                    schedule.Id,
                    schedule.Name,
                    window.Start,
                    window.End,
                    ActualStartUtc: null,
                    ActualEndUtc: null,
                    FilePath: null,
                    ByteSize: 0,
                    Recording.StatusRecording,
                    string.Empty),
                stoppingToken);

            // Written before the recording starts, not after it finishes: this is what stops the
            // next pass, thirty seconds from now, from deciding the same window is still due. The
            // outcome is recorded on the Recording row, which is where it belongs anyway.
            await schedules.SetLastRunAsync(
                schedule.Id, window.Start, Recording.StatusRecording, "Recording now.", stoppingToken);

            logger.LogInformation(
                "Starting scheduled recording {Name} on camera {CameraName} - {Start:HH:mm} to {End:HH:mm} local",
                schedule.Name, camera.Name, window.Start.ToLocalTime(), window.End.ToLocalTime());

            Track(schedule.Id, camera, recording, stoppingToken);
        }
    }

    // Runs one recording without blocking the pass. A two-hour window must not hold up every other
    // schedule behind it - that is exactly how the others would end up late.
    private void Track(Guid key, Camera camera, Recording recording, CancellationToken stoppingToken)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await recorder.RunAsync(camera, recording, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown; RunAsync has already finalized what it captured.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Recording {RecordingId} ended badly", recording.Id);
            }
            finally
            {
                _active.TryRemove(key, out _);
            }
        }, CancellationToken.None);

        _active[key] = task;
    }

    // The window this schedule should be recording right now, or null.
    //
    // A window whose end has already passed is skipped rather than caught up, which is the one real
    // difference from how the main app treats a missed schedule. Catch-up there means "fire late" -
    // meaningful for "arm the alarm", useless here: the footage of 8pm to 8:30pm cannot be captured
    // at 9pm, and there is nothing to be gained by recording the wrong half hour. A window that is
    // merely *underway* is a different matter and does start - a reboot two minutes into a half
    // hour should still get the other twenty-eight.
    public static (DateTimeOffset Start, DateTimeOffset End)? FindDueWindow(
        RecordingSchedule schedule, DateTimeOffset now, TimeZoneInfo zone, GeoCoordinate? location)
    {
        var duration = TimeSpan.FromSeconds(schedule.DurationSeconds);
        var after = schedule.LastStartedUtc ?? now - ColdStartLookback;
        var timing = schedule.ToSchedule();

        for (var step = 0; step < OccurrenceWalkLimit; step++)
        {
            var next = ScheduleTiming.NextOccurrence(timing, after, zone, location);
            if (next is not { } start) return null;

            // Still in the future - nothing to do yet.
            if (start > now) return null;

            // Underway: started already, hasn't ended.
            if (start + duration > now) return (start, start + duration);

            // Entirely in the past. Step over it and look at the next one.
            after = start;
        }

        return null;
    }

    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(CancellationToken stoppingToken) =>
        ResolveTimeZone(await mainAppSettings.GetSectionAsync<ScheduleOptions>(ScheduleOptions.SectionName, stoppingToken));

    private async Task<GeoCoordinate?> ResolveLocationAsync(CancellationToken stoppingToken) =>
        ResolveLocation(await mainAppSettings.GetSectionAsync<LocationOptions>(LocationOptions.SectionName, stoppingToken));

    // Static and settings-in-hand so the editor's "next few runs" preview resolves the clock the
    // same way this worker does. A preview computed against a different time zone than the one that
    // will actually fire is worse than no preview - it is a confident wrong answer.
    public static TimeZoneInfo ResolveTimeZone(ScheduleOptions? options)
    {
        var configured = options?.TimeZone;
        if (string.IsNullOrWhiteSpace(configured)) return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configured.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Same fallback as ScheduleWorker. Not logged: that project already warns about a bad
            // value, and this would repeat it every thirty seconds forever.
            return TimeZoneInfo.Local;
        }
    }

    // Only needed by "solar" windows ("record from dusk for an hour"); everything else ignores it.
    // Null when the location has never been set, which is what makes ScheduleTiming return null for
    // a solar schedule rather than inventing a sunset in the Gulf of Guinea.
    public static GeoCoordinate? ResolveLocation(LocationOptions? options)
    {
        if (options is null) return null;

        var coordinate = new GeoCoordinate(options.Latitude, options.Longitude);
        return coordinate.IsValid ? coordinate : null;
    }
}
