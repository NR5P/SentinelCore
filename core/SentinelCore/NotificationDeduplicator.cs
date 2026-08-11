using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Decides whether a notification would be telling the user something they were told seconds ago.
//
// Every channel already has its own DuplicateCooldownMinutes, keyed on the camera and the category
// AlertCategorizer assigns (see EmailAlertAction/TelegramAlertAction/PushNotifications). That keyed
// check is too fine-grained for a burst: a truck pulling up, then pulling in, then someone getting
// out of it produces vehicle-at-gate, vehicle-inside and person-at-gate - three different
// categories, so all three got through, and the user's phone buzzed three times for one arrival.
//
// The rule here is coarser on purpose. Within the window, a camera notifies again only when the
// set of things actually in frame has changed - a person appearing next to the truck is worth
// interrupting someone for, the truck having moved twenty feet is not. It gates notifications
// only. The trigger, its clip and its row in the Feed are untouched, the same split
// AlertNotificationPolicy already draws between "worth interrupting you" and "worth keeping".
public sealed class NotificationDeduplicator(IOptionsMonitor<RepeatAlertOptions> options, ILogger<NotificationDeduplicator> logger)
{
    private readonly ConcurrentDictionary<string, (string Signature, Guid AlertId, DateTimeOffset At)> _lastNotified =
        new(StringComparer.OrdinalIgnoreCase);

    // The id of the alert this one repeats, or null when it says something new. Records this alert
    // as the camera's latest notification whenever it answers null, so the comparison walks forward
    // through a burst rather than always measuring against the first frame.
    public Guid? FindRecentDuplicate(AlertResult result, DateTimeOffset now)
    {
        var window = options.CurrentValue.DuplicateNotificationWindowSeconds;
        if (window <= 0 || result.Analysis is not { } analysis) return null;

        // Both of these are decisions taken before the alert ever got here, by logic that has
        // already established this specific moment is worth reporting: a gate-open check only
        // recommends on a state transition (GateCheckDecision), and Package Theft Watch fires once
        // when a watched package stops being visible (AlertProcessor). Neither repeats on its own,
        // and both would read as "no subject changed" to the signature below - the package is gone
        // and nobody is necessarily in frame - so they must not be measured by it.
        if (string.Equals(result.Profile, GateCheckDecision.ProfileName, StringComparison.OrdinalIgnoreCase)) return null;
        if (analysis.PackageTheftSuspected) return null;

        var signature = SubjectSignature(analysis);

        if (_lastNotified.TryGetValue(result.Camera, out var last) &&
            now - last.At < TimeSpan.FromSeconds(window) &&
            string.Equals(last.Signature, signature, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Alert {AlertId} from {Camera} shows the same subjects as alert {PreviousId} {Seconds:F0}s ago, " +
                "so it is recorded but not notified on.",
                result.Id, result.Camera, last.AlertId, (now - last.At).TotalSeconds);
            return last.AlertId;
        }

        _lastNotified[result.Camera] = (signature, result.Id, now);
        return null;
    }

    // What is in frame, not where it is or what it is doing. Location and activity are exactly the
    // fields that churn across a burst while the event stays the same one, and including them
    // would make this no coarser than the per-category cooldown it sits above. Package status is
    // the exception: "delivered" versus "being_delivered" versus gone is a change of subject, not
    // a change of position, and it is what AlertNotificationPolicy already treats as a subject in
    // its own right.
    private static string SubjectSignature(VisionAnalysis analysis) =>
        $"p{(analysis.Person.Visible ? 1 : 0)}" +
        $"v{(analysis.Vehicle.Visible ? 1 : 0)}" +
        $"a{(analysis.Animal.Visible ? 1 : 0)}" +
        $"k{(analysis.Package.Visible ? 1 : 0)}:{analysis.Package.Status.ToLowerInvariant()}";
}
