namespace SentinelCore;

// One check's worth of everything: a snapshot comes in from GateCheckMonitor (SentinelCore.Web),
// gets analyzed with an ephemeral prompt profile built just for this camera's check, and the
// resulting state is compared against what the caller last knew. Config and the durable
// last-known-state live in the Web project's GateChecks table (a different process/database), so
// this returns the new state back to the caller rather than storing it itself - see
// GateCheckOutcome and docs/USER_GUIDE.md for the split.
public sealed record GateCheckRequest(
    string Camera,
    string CheckLabel,
    bool CheckBlocked,
    byte[] ImageBytes,
    string ContentType,
    string FileName,
    string PreviousState,
    bool PreviousBlocked,
    DateTimeOffset? LastStateChangedAt,
    DateTimeOffset? LastNotifiedAt,
    int? RemindIfStillOpenAfterHours);

public sealed record GateCheckOutcome(
    string State,
    bool Blocked,
    DateTimeOffset? StateChangedAt,
    DateTimeOffset? NotifiedAt,
    bool NotifiedOpen,
    bool NotifiedBlocked);

public sealed class GateCheckAnalyzer(
    IVisionAnalysisClient vision,
    IResultStore resultStore,
    IEnumerable<IAlertAction> actions,
    ILogger<GateCheckAnalyzer> logger)
{
    public async Task<GateCheckOutcome> AnalyzeAsync(GateCheckRequest request, CancellationToken cancellationToken)
    {
        var profile = BuildProfile(request.CheckLabel, request.CheckBlocked);
        var alert = new IncomingAlert(
            Guid.NewGuid(), request.Camera, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            GateCheckDecision.ProfileName, request.ContentType, request.FileName, request.ImageBytes);

        var (analysis, _, model) = await vision.AnalyzeAsync(alert, profile, cancellationToken);

        var newState = NormalizeState(analysis.Gate.State);
        var newBlocked = request.CheckBlocked && analysis.CameraBlocked;
        var now = DateTimeOffset.UtcNow;

        var notifyOpen = GateCheckDecision.ShouldNotifyOpen(
            request.PreviousState, newState, request.LastNotifiedAt, request.RemindIfStillOpenAfterHours, now);
        var notifyBlocked = request.CheckBlocked &&
            GateCheckDecision.ShouldNotifyBlocked(request.PreviousBlocked, newBlocked);

        // "unknown" readings never overwrite the last known state - see GateCheckDecision.
        var isDefinite = GateCheckDecision.IsDefiniteState(newState);
        var effectiveState = isDefinite ? newState : request.PreviousState;
        var stateChanged = isDefinite && !string.Equals(newState, request.PreviousState, StringComparison.OrdinalIgnoreCase);
        var stateChangedAt = stateChanged ? now : request.LastStateChangedAt;

        var notifiedAt = request.LastNotifiedAt;

        if (notifyOpen || notifyBlocked)
        {
            notifiedAt = now;
            await DeliverAsync(alert, model, analysis, request, effectiveState, notifyOpen, notifyBlocked, now, cancellationToken);
        }

        return new GateCheckOutcome(effectiveState, newBlocked, stateChangedAt, notifiedAt, notifyOpen, notifyBlocked);
    }

    private async Task DeliverAsync(
        IncomingAlert alert,
        string model,
        VisionAnalysis analysis,
        GateCheckRequest request,
        string effectiveState,
        bool notifyOpen,
        bool notifyBlocked,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var summary = BuildSummary(request.CheckLabel, notifyOpen, notifyBlocked, request.LastStateChangedAt, now);

        // A fresh VisionAnalysis rather than mutating the model's own response - Gate/Confidence/
        // CameraBlocked carry over as evidence, but Summary/NotificationRecommended reflect this
        // feature's own transition-based decision (see GateCheckDecision), not whatever the model
        // itself thought was notification-worthy about one frame in isolation.
        var notifyAnalysis = new VisionAnalysis
        {
            EventType = notifyBlocked ? "camera_blocked" : "gate_open",
            Summary = summary,
            Gate = analysis.Gate,
            Confidence = analysis.Confidence,
            NotificationRecommended = true,
            NotificationReason = summary,
            CameraBlocked = analysis.CameraBlocked
        };

        var imagePath = await resultStore.SaveImageAsync(alert, cancellationToken);
        var result = new AlertResult
        {
            Id = alert.Id,
            Camera = alert.Camera,
            OccurredAt = alert.OccurredAt,
            ReceivedAt = alert.ReceivedAt,
            CompletedAt = now,
            Profile = GateCheckDecision.ProfileName,
            Model = model,
            Status = "completed",
            ImagePath = imagePath,
            Analysis = notifyAnalysis
        };
        await resultStore.SaveAsync(result, cancellationToken);

        // Same shape as AlertProcessor's own dispatch loop - one try/catch per action so a
        // failing channel (SMTP down, Telegram unreachable) never stops the others from sending.
        foreach (var action in actions)
        {
            try { await action.ExecuteAsync(result, cancellationToken); }
            catch (Exception ex) { logger.LogError(ex, "Gate-check notification action failed for {AlertId}", alert.Id); }
        }

        logger.LogInformation(
            "Gate check notified for {Camera} ({Label}): {Summary}", request.Camera, request.CheckLabel, summary);
    }

    private static string BuildSummary(string checkLabel, bool notifyOpen, bool notifyBlocked, DateTimeOffset? lastChangedAt, DateTimeOffset now)
    {
        if (notifyBlocked)
            return $"The camera watching {checkLabel} appears to be blocked, covered, or obstructed.";

        if (lastChangedAt is { } changedAt && now - changedAt > TimeSpan.FromMinutes(1))
        {
            var duration = now - changedAt;
            return $"{Capitalize(checkLabel)} has been open for over {FormatDuration(duration)}.";
        }

        return $"{Capitalize(checkLabel)} is open.";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} hour(s)" : $"{(int)duration.TotalMinutes} minute(s)";

    private static string Capitalize(string label) =>
        string.IsNullOrEmpty(label) ? label : char.ToUpperInvariant(label[0]) + label[1..];

    private static string NormalizeState(string state) => state.ToLowerInvariant() switch
    {
        "open" => "open",
        "closed" => "closed",
        _ => "unknown"
    };

    private static PromptProfile BuildProfile(string checkLabel, bool checkBlockedEnabled) => new()
    {
        SystemPrompt =
            $"You are watching a fixed security camera pointed at {checkLabel}. Your only job is to " +
            "report whether it is open, closed, or unclear, plus whether the camera's own view is " +
            "usable. Do not describe people, vehicles, or other activity.",
        Questions =
        [
            $"Is {checkLabel} open, closed, or unclear from this image?",
            "Is the camera's own view blocked, covered, or obstructed, as opposed to a normal dark nighttime frame?"
        ],
        CameraBlockedCheckEnabled = checkBlockedEnabled
    };
}
