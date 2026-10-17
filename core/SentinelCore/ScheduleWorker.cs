using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Where a schedule's own settings are turned into the two facts everything else needs: which
// time zone its wall-clock times are in, and where on earth "sunset" refers to. Both are read
// live, so changing either in Settings takes effect on the next pass without a restart.
public sealed class ScheduleContext(
    IOptionsMonitor<ScheduleOptions> scheduleOptions,
    IOptionsMonitor<LocationOptions> locationOptions,
    ILogger<ScheduleContext> logger)
{
    private string? _lastRejectedTimeZone;

    public TimeZoneInfo TimeZone
    {
        get
        {
            var configured = scheduleOptions.CurrentValue.TimeZone;
            if (string.IsNullOrWhiteSpace(configured)) return TimeZoneInfo.Local;

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(configured.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Logged once per distinct bad value, not once per pass - this runs every few
                // seconds, and a typo would otherwise fill the log by morning.
                if (_lastRejectedTimeZone != configured)
                {
                    _lastRejectedTimeZone = configured;
                    logger.LogWarning(
                        "'{TimeZone}' is not a time zone this machine knows; schedules will use the machine's own time zone instead.",
                        configured);
                }

                return TimeZoneInfo.Local;
            }
        }
    }

    // Null means no location has been set, which is only a problem for schedules that need one.
    public GeoCoordinate? Location
    {
        get
        {
            var current = locationOptions.CurrentValue;
            var coordinate = new GeoCoordinate(current.Latitude, current.Longitude);
            return coordinate.IsValid ? coordinate : null;
        }
    }

    public TimeSpan MaximumCatchUp => TimeSpan.FromMinutes(scheduleOptions.CurrentValue.MaximumCatchUpMinutes);

    public bool Enabled => scheduleOptions.CurrentValue.Enabled;

    public DateTimeOffset? NextOccurrence(Schedule schedule, DateTimeOffset after) =>
        ScheduleTiming.NextOccurrence(schedule, after, TimeZone, Location);
}

// Carries out one firing and records it. Separate from ScheduleWorker because two callers need
// it - the worker, when a schedule comes due, and the "run now" endpoint - and both must obey
// the same rule that a schedule never runs twice at once.
public sealed class ScheduleRunner(
    IScheduleStore store,
    AutomationActionDispatcher dispatcher,
    ILogger<ScheduleRunner> logger)
{
    // Which schedules are mid-run. A schedule whose action takes longer than its own interval
    // would otherwise pile up runs until something gave.
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    // `weather` is what the weather check measured, when one let this run go ahead - added to the
    // run's detail so the history shows why it ran as well as what it did.
    public async Task<ScheduleRun> ExecuteAsync(
        Schedule schedule, DateTimeOffset dueAt, CancellationToken cancellationToken, string? weather = null)
    {
        var firedAt = DateTimeOffset.UtcNow;

        if (!_inFlight.TryAdd(schedule.Id, 0))
        {
            logger.LogWarning(
                "Schedule {ScheduleName} came due again while the previous run was still going; skipping this one",
                schedule.Name);

            return await RecordAsync(
                schedule, dueAt, firedAt, ScheduleRun.StatusSkipped,
                "the previous run was still going", 0, cancellationToken);
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var tokens = AutomationTemplate.BuildScheduleValues(schedule, Guid.NewGuid(), dueAt, firedAt);

            try
            {
                var detail = await dispatcher.DispatchAsync(schedule.Action, tokens, cancellationToken);
                if (!string.IsNullOrEmpty(weather)) detail = $"{detail} (weather: {weather})";
                logger.LogInformation("Schedule {ScheduleName} fired: {Detail}", schedule.Name, detail);

                return await RecordAsync(
                    schedule, dueAt, firedAt, ScheduleRun.StatusOk, detail,
                    stopwatch.ElapsedMilliseconds, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failure is recorded, not thrown: the history is the only place someone can
                // find out that the coop door did not in fact close last night.
                logger.LogWarning(ex, "Schedule {ScheduleName} failed", schedule.Name);

                return await RecordAsync(
                    schedule, dueAt, firedAt, ScheduleRun.StatusFailed, ex.Message,
                    stopwatch.ElapsedMilliseconds, cancellationToken);
            }
        }
        finally
        {
            _inFlight.TryRemove(schedule.Id, out _);
        }
    }

    // Used by the worker for a firing that was missed or suppressed - it never ran, but it is
    // still something the owner should be able to see.
    public Task<ScheduleRun> RecordWithoutRunningAsync(
        Schedule schedule, DateTimeOffset dueAt, string status, string detail, CancellationToken cancellationToken) =>
        RecordAsync(schedule, dueAt, DateTimeOffset.UtcNow, status, detail, 0, cancellationToken);

    private async Task<ScheduleRun> RecordAsync(
        Schedule schedule, DateTimeOffset dueAt, DateTimeOffset firedAt,
        string status, string detail, long durationMs, CancellationToken cancellationToken)
    {
        var run = new ScheduleRun(Guid.NewGuid(), schedule.Id, dueAt, firedAt, status, detail, durationMs);

        // Deliberately not the caller's token. By the time this runs the action has already
        // happened - the gate has opened, the message has gone out - and cancelling the write
        // that records it would lose the only evidence of it, which is precisely the question
        // the history exists to answer. It is one local SQLite insert, so it cannot hang.
        //
        // The next due time is not this class's business either: the worker set it before
        // dispatching, so that a slow action here cannot leave a stale one behind to fire twice.
        await store.RecordRunAsync(run, CancellationToken.None);
        return run;
    }
}

// Decides when schedules come due and hands them to ScheduleRunner. Follows DailySummaryWorker's
// shape - work out the next firing, sleep until then - with the sleep capped so that a schedule
// edited from the dashboard or an app is picked up promptly rather than at the next firing.
public sealed class ScheduleWorker(
    IScheduleStore store,
    ScheduleRunner runner,
    ScheduleContext context,
    IScheduleWeather weather,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
    // Also how long an edit can take to be noticed, which is why it is seconds rather than
    // minutes: the loop re-reads every enabled schedule each pass, and that is one cheap query.
    private static readonly TimeSpan MaximumSleep = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MinimumSleep = TimeSpan.FromMilliseconds(500);

    // Firings in progress. They are deliberately not awaited by the loop: a webhook that takes
    // half a minute must not delay every other schedule behind it, because "late" is exactly
    // what the catch-up rules would then record them as. Tracked rather than abandoned so
    // shutdown can give them a moment to finish.
    private readonly ConcurrentDictionary<Guid, Task> _dispatching = new();

    // Weather schedules whose last check couldn't get the weather, so an outage is recorded in
    // the history once when it starts rather than every half hour until it ends.
    private readonly ConcurrentDictionary<Guid, byte> _weatherUnavailable = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!context.Enabled)
            {
                if (!await WaitAsync(TimeSpan.FromSeconds(60), stoppingToken)) break;
                continue;
            }

            TimeSpan sleep;
            try
            {
                sleep = await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad schedule, or a database hiccup, must not take the whole scheduler down
                // - every other schedule is still someone's expectation about tonight.
                logger.LogError(ex, "A scheduling pass failed unexpectedly");
                sleep = MaximumSleep;
            }

            if (!await WaitAsync(sleep, stoppingToken)) break;
        }

        await DrainAsync();
    }

    // Gives whatever was mid-flight when the stop signal arrived a few seconds to land. A gate
    // that has been told to open should finish being told, and the run should reach the history.
    private async Task DrainAsync()
    {
        var outstanding = _dispatching.Values.ToArray();
        if (outstanding.Length == 0) return;

        try
        {
            await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Some schedule runs did not finish before shutdown");
        }
    }

    // Returns how long to sleep before the next pass.
    private async Task<TimeSpan> RunPassAsync(CancellationToken cancellationToken)
    {
        var schedules = await store.GetEnabledAsync(cancellationToken);
        if (schedules.Count == 0) return MaximumSleep;

        var now = DateTimeOffset.UtcNow;
        var earliestNextDue = (DateTimeOffset?)null;

        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nextDue = schedule.NextDueUtc;

            // A schedule that has never been evaluated - just created, or created before a
            // location was set - gets its first due time worked out now, and fires no earlier
            // than that. Nothing is fired retroactively on first sight.
            if (nextDue is null)
            {
                nextDue = context.NextOccurrence(schedule, now);
                await store.SetNextDueAsync(schedule.Id, nextDue, cancellationToken);

                if (nextDue is null)
                {
                    LogUnschedulable(schedule);
                    continue;
                }
            }

            if (nextDue > now)
            {
                earliestNextDue = Earliest(earliestNextDue, nextDue.Value);
                continue;
            }

            // Advanced before anything is dispatched, so this firing cannot be picked up again
            // by the next pass while the action for it is still running.
            var following = context.NextOccurrence(schedule, now);
            await store.SetNextDueAsync(schedule.Id, following, cancellationToken);

            // A one-shot has no next occurrence by definition. Disabled rather than deleted, so
            // it stays visible in the list with its history afterwards.
            if (following is null && string.Equals(schedule.Kind, Schedule.KindOnce, StringComparison.OrdinalIgnoreCase))
                await store.SetEnabledAsync(schedule.Id, false, cancellationToken);

            await HandleDueAsync(schedule, nextDue.Value, now, cancellationToken);

            if (following is { } upcoming) earliestNextDue = Earliest(earliestNextDue, upcoming);
        }

        if (earliestNextDue is null) return MaximumSleep;

        var untilNext = earliestNextDue.Value - DateTimeOffset.UtcNow;
        return untilNext > MaximumSleep ? MaximumSleep : untilNext < MinimumSleep ? MinimumSleep : untilNext;
    }

    private async Task HandleDueAsync(
        Schedule schedule, DateTimeOffset dueAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A check, not a firing: one missed while the server was down is simply made now, so the
        // catch-up rules don't apply.
        if (schedule.IsWeatherKind)
        {
            Dispatch(schedule, dueAt, ct => CheckWeatherTriggerAsync(schedule, dueAt, ct), cancellationToken);
            return;
        }

        var decision = ScheduleCatchUp.Decide(schedule, dueAt, now, context.MaximumCatchUp);

        if (!decision.Run)
        {
            logger.LogWarning(
                "Schedule {ScheduleName} was due at {DueAt} but did not run: {Reason}",
                schedule.Name, dueAt.ToLocalTime(), decision.Reason);

            await runner.RecordWithoutRunningAsync(
                schedule, dueAt, decision.Status, decision.Reason, cancellationToken);
            return;
        }

        Dispatch(schedule, dueAt, ct => RunIfWeatherAllowsAsync(schedule, dueAt, ct), cancellationToken);
    }

    // A timed firing with "only if" weather tests. Checked here, at firing time, rather than
    // when it was scheduled - "no rain in the last 3 days" means as of 06:00, not as of last
    // night. Unknown weather skips the run: the safe side of "sprinklers on" is off.
    private async Task RunIfWeatherAllowsAsync(Schedule schedule, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        if (schedule.WeatherTests.Count == 0)
        {
            await runner.ExecuteAsync(schedule, dueAt, cancellationToken);
            return;
        }

        var verdict = await weather.CheckAsync(schedule.WeatherTests, context.TimeZone, cancellationToken);
        if (verdict.Holds != true)
        {
            logger.LogInformation("Schedule {ScheduleName} skipped for the weather: {Detail}", schedule.Name, verdict.Detail);
            await runner.RecordWithoutRunningAsync(
                schedule, dueAt, ScheduleRun.StatusSkipped, $"skipped: {verdict.Detail}", cancellationToken);
            return;
        }

        await runner.ExecuteAsync(schedule, dueAt, cancellationToken, verdict.Detail);
    }

    // A "weather" schedule's check: fire when the tests go from not holding to holding, and not
    // again within the quiet period - the same rule OrganizerAutomationEngine.RunWeatherRulesAsync
    // applies, so "a freeze is forecast" fires once when it appears rather than every half hour
    // while it stays in the forecast.
    private async Task CheckWeatherTriggerAsync(Schedule schedule, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        var verdict = await weather.CheckAsync(schedule.WeatherTests, context.TimeZone, cancellationToken);
        if (verdict.Holds is not { } holds)
        {
            // No change either way; try again at the next check. Recorded once per outage.
            if (_weatherUnavailable.TryAdd(schedule.Id, 0))
                await runner.RecordWithoutRunningAsync(
                    schedule, dueAt, ScheduleRun.StatusSkipped, $"skipped: {verdict.Detail}", cancellationToken);
            return;
        }

        _weatherUnavailable.TryRemove(schedule.Id, out _);
        var now = DateTimeOffset.UtcNow;
        var firedAt = schedule.WeatherFiredAtUtc;
        var quiet = firedAt is { } last && now - last < Schedule.WeatherQuietPeriod;

        if (holds && schedule.WeatherHeld != true && !quiet)
        {
            firedAt = now;
            // Stored before running, so a slow action can't let the next check fire it again. Not
            // the caller's token, for ScheduleRunner.RecordAsync's reason: once the weather has
            // been read, a shutdown mid-write mustn't lose what it said.
            await store.SetWeatherStateAsync(schedule.Id, true, firedAt, CancellationToken.None);
            await runner.ExecuteAsync(schedule, dueAt, cancellationToken, verdict.Detail);
            return;
        }

        await store.SetWeatherStateAsync(schedule.Id, holds, firedAt, CancellationToken.None);
    }

    // Started, not awaited - see _dispatching. The task removes itself when it finishes, and
    // ScheduleRunner already records both success and failure; anything else that throws is
    // logged here so it can't fault silently.
    private void Dispatch(
        Schedule schedule, DateTimeOffset dueAt, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid();
        var task = Task.Run(async () =>
        {
            try
            {
                await work(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Schedule {ScheduleName} due at {DueAt} hit an error", schedule.Name, dueAt);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _dispatching.TryRemove(key, out _);
            }
        }, CancellationToken.None);

        // A run that finished before this line does not need tracking, and adding it here would
        // leave an entry nothing will ever remove.
        if (!task.IsCompleted) _dispatching.TryAdd(key, task);
    }

    private void LogUnschedulable(Schedule schedule)
    {
        // The overwhelmingly likely cause is a "when it gets dark" schedule on a server whose
        // location has not been set, so that is named explicitly rather than left to be guessed.
        if (schedule.NeedsLocation && context.Location is null)
        {
            logger.LogWarning(
                "Schedule {ScheduleName} follows the sun but no location is set - add your latitude and longitude, or your town, in Settings.",
                schedule.Name);
            return;
        }

        logger.LogWarning(
            "Schedule {ScheduleName} has no upcoming run - check its timing settings.", schedule.Name);
    }

    private static DateTimeOffset Earliest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is null || candidate < current ? candidate : current.Value;

    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
