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
    public async Task ResolveAsync(Guid triggerId, AlertForwardResult forwarded, CancellationToken cancellationToken)
    {
        try
        {
            if (forwarded.AlertId is not { } alertId)
            {
                // Accepted but no id to follow, or refused outright. Either way no verdict is
                // coming, so don't leave it hidden.
                await eventStore.SetConfirmationAsync(triggerId, TriggerConfirmation.Confirmed, cancellationToken);
                return;
            }

            var settings = await GetSettingsAsync(cancellationToken);
            var verdict = await AwaitVerdictAsync(alertId, TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken);
            await eventStore.SetConfirmationAsync(triggerId, verdict, cancellationToken);

            logger.LogInformation(
                "Trigger {TriggerId} was {Verdict} by analysis of alert {AlertId}", triggerId, verdict, alertId);
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

    private async Task<TriggerConfirmation> AwaitVerdictAsync(Guid alertId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", cancellationToken);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
        {
            logger.LogWarning("Cannot confirm a trigger - the main app's API key is not configured.");
            return TriggerConfirmation.Confirmed;
        }

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(TriggerConfirmationService));
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await TryFetchResultAsync(client, baseUrl, bridge.ApiKey, alertId, cancellationToken);

            // Null covers "not found yet" and any transport failure - keep waiting rather than
            // giving a verdict on no information, right up to the deadline.
            if (result is not null && !string.Equals(result.Status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "Alert {AlertId} ended as '{Status}' rather than completing, so its trigger stays visible unjudged",
                        alertId, result.Status);
                    return TriggerConfirmation.Confirmed;
                }

                if (result.Analysis is null)
                    return TriggerConfirmation.Confirmed;

                return AlertNotificationPolicy.HasVisibleSubject(result.Analysis)
                    ? TriggerConfirmation.Confirmed
                    : TriggerConfirmation.Rejected;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        logger.LogInformation(
            "Alert {AlertId} hadn't been analyzed within {Timeout}s, so its trigger stays visible unjudged",
            alertId, (int)timeout.TotalSeconds);
        return TriggerConfirmation.Confirmed;
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
            await eventStore.SetConfirmationAsync(triggerId, TriggerConfirmation.Confirmed, CancellationToken.None);
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
                await eventStore.SetConfirmationAsync(id, TriggerConfirmation.Confirmed, stoppingToken);

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
