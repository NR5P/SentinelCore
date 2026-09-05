using System.Net;
using System.Text.Json;
using SentinelCore;

namespace SentinelCore.Web;

// Decides whether a motion trigger was real, by asking the main app what its AI made of the frame.
//
// Motion detection here is a frame-diff (see MotionDetector) with no idea what it's looking at, so
// on a windy night the Triggers tab fills with branches, rain, moths on the IR illuminator and
// headlight sweep. With TriggerConfirmation enabled, a trigger from a camera that forwards to the
// alert pipeline is recorded Pending and stays out of the tab until the pipeline's analysis has
// actually looked at it - confirmed if there was a person/vehicle/package/animal in frame,
// rejected if there wasn't.
//
// Polls rather than being called back: the dashboard already reaches into the main app this way
// (see AlertForwarder, GateCheckForwarder) with an API key it already holds, and a callback would
// mean teaching the main app the dashboard's address and credentials for no other reason. The wait
// is one HTTP GET every few seconds against loopback for as long as the analysis takes.
//
// Every failure path here confirms rather than rejects. A trigger the AI never got to judge - main
// app down, queue full, model unreachable, analysis errored, verdict too slow - is footage of
// something that did move, and losing it to an infrastructure problem is a far worse outcome than
// showing one more false positive. Rejection requires the AI to have actually looked and said no.
public sealed class TriggerConfirmationService(
    IHttpClientFactory httpClientFactory,
    AppSettingsFile mainAppSettings,
    ITriggerEventStore eventStore,
    IConfiguration configuration,
    ILogger<TriggerConfirmationService> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web);

    public async Task<TriggerConfirmationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A missing section means an install that predates this feature - on by default, which
            // is the whole point of the setting's default. An unreadable settings file is not a
            // reason to start hiding footage either, so that path lands on the default too.
            return await mainAppSettings.GetSectionAsync<TriggerConfirmationSettingsDto>("TriggerConfirmation", cancellationToken)
                ?? new TriggerConfirmationSettingsDto();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not read the TriggerConfirmation settings; using defaults");
            return new TriggerConfirmationSettingsDto();
        }
    }

    // Whether a trigger from this camera should be held back pending an AI verdict. A camera that
    // doesn't forward to the alert pipeline is never analyzed at all, so gating it would hide its
    // triggers forever - those stay visible exactly as they were before this feature existed.
    public async Task<bool> ShouldGateAsync(bool cameraForwardsToAlerts, CancellationToken cancellationToken)
    {
        if (!cameraForwardsToAlerts) return false;
        return (await GetSettingsAsync(cancellationToken)).Enabled;
    }

    // Fire-and-forget from the trigger path: waits out the analysis, then writes the verdict.
    // Never throws - the caller has already recorded the trigger and moved on.
    //
    // keepOnlyAlertable is trailing and defaulted rather than required: it comes off the camera's
    // trigger config, it is off for all but a camera or two, and every other caller (and the
    // fail-open paths above) means "judge this the normal way".
    public async Task ResolveAsync(
        Guid triggerId, AlertForwardResult forwarded, CancellationToken cancellationToken,
        bool keepOnlyAlertable = false)
    {
        try
        {
            if (forwarded.AlertId is not { } alertId)
            {
                // Accepted but no id to follow, or refused outright. Either way no verdict is
                // coming, so don't leave it hidden.
                await eventStore.SetConfirmationAsync(triggerId, TriggerConfirmation.Confirmed, judged: false, cancellationToken);
                // Logged because this is the one outcome that used to leave no trace on this side
                // at all. AlertForwarder records why the forward failed, but nothing said what
                // became of the trigger - so a queue-full burst showed up as a run of triggers
                // that simply appeared, indistinguishable from ones the AI had approved.
                logger.LogInformation(
                    "Trigger {TriggerId} is left visible unjudged - it never reached the alert pipeline", triggerId);
                return;
            }

            var settings = await GetSettingsAsync(cancellationToken);
            var (verdict, judged) = await AwaitVerdictAsync(
                alertId, TimeSpan.FromSeconds(settings.TimeoutSeconds), keepOnlyAlertable, cancellationToken);
            await eventStore.SetConfirmationAsync(triggerId, verdict, judged, cancellationToken);

            // Said plainly, because Confirmed means two different things and only one of them is a
            // verdict. The AI looking at the frame and finding a person is Confirmed; so is every
            // fail-open path - a supersede chain longer than the hop cap, an analysis that ended as
            // anything but completed, the timeout. Logging both as "confirmed by analysis" made a
            // camera misfiring for two hours read as two hours of confirmed subjects, when in fact
            // 282 of those 287 triggers were never judged at all.
            if (judged)
                logger.LogInformation(
                    "Trigger {TriggerId} was {Verdict} by analysis of alert {AlertId}", triggerId, verdict, alertId);
            else
                logger.LogInformation(
                    "Trigger {TriggerId} is left visible unjudged - alert {AlertId} produced no verdict for it", triggerId, alertId);

            // Usually a no-op right here, because the analysis nearly always outlasts the clip's
            // post-roll and the clip therefore isn't cut yet. The recorder asks again the moment it
            // has one - see TriggerZoneMonitor.BuildClipAsync - and that is the call that normally
            // does the work. This one covers the other order: an analysis that came back before the
            // post-roll elapsed, or a clip that was already patched in by a neighbouring trigger.
            if (verdict == TriggerConfirmation.Rejected)
                await DiscardClipIfRejectedAsync(triggerId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down - the startup sweep will release this one on the way back up.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Confirming trigger {TriggerId} failed; leaving it visible", triggerId);
            await TryConfirmAsync(triggerId);
        }
    }

    // Deletes the video behind a trigger the AI looked at and found nothing in.
    //
    // A rejected trigger is branches, rain, a moth on the illuminator or a headlight sweep, and
    // storing 19 MB of video of it is the single largest thing on this disk that is worth nothing.
    // The snapshot stays: it is what "show hidden" renders, and seeing what a zone actually fired
    // ON is the only way to tell a badly tuned threshold from a well tuned one.
    //
    // Every uncertain path here keeps the video, which is the opposite of the rest of this class'
    // fail-open bias and the same instinct applied to a different question. Confirmation fails open
    // to SHOWING footage; deletion fails closed to KEEPING it. Both times the recoverable mistake
    // is the one that leaves the file alone:
    //   - the setting couldn't be read - never delete on a value we failed to read
    //   - the verdict wasn't a rejection, or has since changed
    //   - the clip is shared with a trigger that wasn't rejected (the store enforces this)
    //   - the file won't unlink - left for the orphan sweep, which is what collects these
    //
    // Called on the rejection itself and again once the clip has actually been cut, since those
    // two arrive in either order. It is safe to call as often as either wants: the store clears
    // ClipPath as it hands the file over, so the second call finds nothing to do.
    public async Task DiscardClipIfRejectedAsync(Guid triggerId, CancellationToken cancellationToken)
    {
        TriggerConfirmationSettingsDto settings;
        try
        {
            settings = await mainAppSettings.GetSectionAsync<TriggerConfirmationSettingsDto>("TriggerConfirmation", cancellationToken)
                ?? new TriggerConfirmationSettingsDto();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // Deliberately not GetSettingsAsync: that one falls back to the defaults, which is
            // right for "should this trigger be held back" and wrong for "may I delete a file".
            logger.LogWarning(ex, "Could not read the TriggerConfirmation settings; keeping the rejected trigger's clip");
            return;
        }

        if (!settings.DiscardRejectedClips) return;

        string? clipPath;
        try
        {
            clipPath = await eventStore.ReleaseRejectedClipAsync(triggerId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not release the clip for rejected trigger {TriggerId}", triggerId);
            return;
        }

        if (clipPath is null) return;

        try
        {
            var info = new FileInfo(clipPath);
            if (!info.Exists) return;

            var megabytes = info.Length / 1024d / 1024d;
            info.Delete();

            logger.LogInformation(
                "Deleted the clip for trigger {TriggerId} - the AI found nothing in it - reclaiming {Megabytes:0.0} MB. Its snapshot is kept",
                triggerId, megabytes);
        }
        // The row no longer points at the file, so nothing will come back for it here. That is not
        // a leak: a file no row references is exactly what MotionRetentionWorker's orphan sweep
        // exists to collect, and it runs twice a day.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex, "Could not delete the clip for rejected trigger {TriggerId}; the orphan sweep will collect it", triggerId);
        }
    }

    // Judged is false on every path that reached Confirmed by giving up rather than by the AI
    // saying so, which is most of them: no API key, a supersede chain past the hop cap, an alert
    // that ended as anything but completed, a completed alert carrying no analysis, the timeout.
    // The verdict written to the row is the same either way - failing open to visible is the whole
    // design - but the two are not the same event and the log must not call them the same thing.
    // Rejected is only ever reachable with Judged true.
    private readonly record struct Verdict(TriggerConfirmation Confirmation, bool Judged);

    private static Verdict Unjudged => new(TriggerConfirmation.Confirmed, false);

    // The bar a judged frame has to clear. Normally "was anything there" - a person, vehicle,
    // package or animal - which is the right question for a driveway, where anything in shot is
    // worth a look.
    //
    // KeepOnlyAlertable asks the narrower one instead: would the AI have told you about this? At a
    // chicken coop the two come apart completely. Chickens are animals and are always in frame, so
    // the default bar confirms every trigger and keeps every clip; the model meanwhile answers
    // notification_recommended=false for them and true for a predator, which is exactly the
    // distinction being asked for. Measured on 2026-09-03: 30 of 40 analyses had a visible subject
    // and none of the 40 warranted a notification.
    //
    // Read off the model's own flag rather than AlertNotificationPolicy.ShouldNotify, deliberately.
    // ShouldNotify also suppresses duplicates, and a repeat sighting of a real fox is still footage
    // worth keeping - the same reasoning the dedup check there already spells out for itself.
    //
    // Not combined with HasVisibleSubject: a model that recommends a notification while reporting
    // nothing in frame is believed, because that direction errs towards keeping footage and this
    // whole file errs that way on purpose.
    private static bool IsConfirmed(VisionAnalysis analysis, bool keepOnlyAlertable) =>
        keepOnlyAlertable
            ? analysis.NotificationRecommended
            : AlertNotificationPolicy.HasVisibleSubject(analysis);

    private async Task<Verdict> AwaitVerdictAsync(
        Guid alertId, TimeSpan timeout, bool keepOnlyAlertable, CancellationToken cancellationToken)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
        {
            logger.LogWarning("Cannot confirm a trigger - the main app's API key is not configured.");
            return Unjudged;
        }

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(TriggerConfirmationService));
        var deadline = DateTimeOffset.UtcNow + timeout;

        // How many times a poll may follow a supersede before giving up. A chain can only grow
        // while the queue is backed up on one camera, so it is short in practice; the cap is
        // against a cycle in the data, not against ordinary depth.
        const int MaxSupersedeHops = 10;
        var hops = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await TryFetchResultAsync(client, baseUrl, bridge.ApiKey, alertId, cancellationToken);

            // The alert this trigger produced never got analyzed, because a newer frame from the
            // same camera took its place in the queue (see AlertQueue). Judge this trigger by that
            // frame instead: it is the same camera moments later, which is a far better answer than
            // the "couldn't tell, leave it visible" one every other unfinished status gets here.
            if (result is { SupersededByAlertId: { } replacement } && hops++ < MaxSupersedeHops)
            {
                logger.LogInformation(
                    "Alert {AlertId} was superseded by {ReplacementId}, so its trigger is judged by that analysis instead",
                    alertId, replacement);
                alertId = replacement;
                continue;
            }

            // Null covers "not found yet" and any transport failure - keep waiting rather than
            // giving a verdict on no information, right up to the deadline.
            if (result is not null && !string.Equals(result.Status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "Alert {AlertId} ended as '{Status}' rather than completing, so its trigger stays visible unjudged",
                        alertId, result.Status);
                    return Unjudged;
                }

                if (result.Analysis is null)
                    return Unjudged;

                return new Verdict(
                    IsConfirmed(result.Analysis, keepOnlyAlertable)
                        ? TriggerConfirmation.Confirmed
                        : TriggerConfirmation.Rejected,
                    Judged: true);
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        logger.LogInformation(
            "Alert {AlertId} hadn't been analyzed within {Timeout}s, so its trigger stays visible unjudged",
            alertId, (int)timeout.TotalSeconds);
        return Unjudged;
    }

    private async Task<AlertResult?> TryFetchResultAsync(
        HttpClient client, string baseUrl, string apiKey, Guid alertId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/results/{alertId}");
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Polling alert {AlertId} answered {StatusCode}", alertId, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<AlertResult>(stream, ResultOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            logger.LogDebug(ex, "Polling alert {AlertId} failed; will retry", alertId);
            return null;
        }
    }

    private async Task TryConfirmAsync(Guid triggerId)
    {
        try
        {
            await eventStore.SetConfirmationAsync(triggerId, TriggerConfirmation.Confirmed, judged: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not release trigger {TriggerId} from pending; it will be swept on next startup", triggerId);
        }
    }
}

// Releases triggers whose verdict poll died with the process. A Pending row is invisible, and the
// task that was waiting on it doesn't survive a restart, so without this a crash mid-analysis
// would hide that footage permanently. Runs once at startup and confirms anything that has been
// pending for longer than any verdict could legitimately take.
public sealed class TriggerConfirmationSweeper(
    ITriggerEventStore eventStore,
    TriggerConfirmationService confirmation,
    ILogger<TriggerConfirmationSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await confirmation.GetSettingsAsync(stoppingToken);

            // Twice the configured timeout: a poll still running against a slow analysis when the
            // process went down had at most one timeout left to go, and the margin keeps this from
            // racing a live poll that's about to answer properly.
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-2 * settings.TimeoutSeconds);
            var stranded = await eventStore.GetPendingBeforeAsync(cutoff, stoppingToken);
            if (stranded.Count == 0) return;

            foreach (var id in stranded)
                await eventStore.SetConfirmationAsync(id, TriggerConfirmation.Confirmed, judged: false, stoppingToken);

            logger.LogInformation(
                "Released {Count} trigger(s) that were left waiting for an AI verdict by a restart", stranded.Count);
        }
        catch (OperationCanceledException)
        {
            // Shutting down before the sweep finished - it runs again next start.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sweeping stranded pending triggers failed");
        }
    }
}
