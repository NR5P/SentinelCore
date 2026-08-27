using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Decides whether a newer frame really is the better one to spend an analysis on, for the case
// AlertQueue's supersede rule gets wrong.
//
// The rule it moderates keeps the newest frame, always, on the reasoning in AlertQueue's own
// comment: "the truck arrives, then someone gets out of it". That is right, and it stays right -
// but the decision was made at ingest, keyed on nothing but the camera name, before any pixel had
// been looked at. So it could not tell that case apart from its mirror image.
//
// On this install the mirror image is the one that matters. The gate profile says a person on the
// road side must notify and a person on the camera side must not, so somebody walking in is
// outside-then-inside: blind supersession discards the frames that would have notified and keeps
// the one that is suppressed by design. Measured on the live database, 852 alerts had been
// superseded, all from the gate camera, and in 23% of bursts the surviving frame was settled by the
// sub-second detector alone - the frames thrown away to save a 157s analysis that was never spent.
// Observed directly on 2026-08-25: three frames containing a person were dropped in favour of a
// later empty one, which was then analyzed in 0.26s as "No person, vehicle, or animal detected."
//
// The fix is deliberately narrow: a frame with nothing in it may not stand down a frame that has
// something in it. Everything else behaves exactly as before, including newest-wins when both
// frames have a subject, which is the truck case. The extra frame this keeps is nearly free -
// having no subject is precisely what makes it cost one detector pass and no escalation.
public sealed class BurstFramePicker(
    IFastLocalDetector detector,
    IPromptProfileProvider profiles,
    IOptionsMonitor<FastLocalOptions> fastLocalOptions,
    ILogger<BurstFramePicker> logger)
{
    // True when the arriving frame should stand down the one already waiting.
    //
    // Fails safe in every direction: no waiting frame, no detector, a profile that cannot be
    // resolved, or any error at all leaves the answer true, which is exactly what the queue did
    // before this existed. The only outcome that changes is the one backed by two real verdicts.
    public async Task<bool> ShouldSupersedeAsync(
        IncomingAlert? waiting, IncomingAlert arriving, CancellationToken cancellationToken)
    {
        if (waiting is null) return true;

        try
        {
            // Nothing to compare against until the model is on disk and loaded. Checked rather
            // than assumed, so an install that never routes to "fastlocal" is not made to download
            // ~100MB just to answer this.
            if (!await detector.IsReadyAsync(cancellationToken)) return true;

            // The waiting frame's own profile, not the arriving one's - they are the same camera,
            // but the crop belongs to the frame being judged. A mismatched crop would have the
            // probe disagree with the analysis that follows it.
            var waitingHasSubject = await HasSubjectAsync(waiting, cancellationToken);
            if (!waitingHasSubject) return true;

            var arrivingHasSubject = await HasSubjectAsync(arriving, cancellationToken);
            if (arrivingHasSubject) return true;

            logger.LogInformation(
                "Alert {ArrivingId} from {Camera} did not stand down {WaitingId}: the waiting frame has a subject and this one does not, so the waiting frame is kept and both are analyzed",
                arriving.Id, arriving.Camera, waiting.Id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A burst is the worst possible moment to start failing ingest over an optional
            // refinement, so this degrades to the old behaviour rather than propagating.
            logger.LogWarning(ex,
                "Could not compare frames for {Camera}; falling back to keeping the newer one", arriving.Camera);
            return true;
        }
    }

    // Deliberately routed through the same pure mapping the real FastLocal pass uses, so the probe
    // and the analysis can never disagree about what counts as a subject.
    private async Task<bool> HasSubjectAsync(IncomingAlert alert, CancellationToken cancellationToken)
    {
        var crop = profiles.Current.TryGetValue(alert.Profile, out var profile) ? profile.Crop : null;
        var detections = await detector.DetectAsync(alert.ImageBytes, crop, cancellationToken);
        return AlertNotificationPolicy.HasVisibleSubject(
            FastLocalVisionClient.MapToAnalysis(detections, fastLocalOptions.CurrentValue));
    }
}
