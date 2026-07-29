namespace SentinelCore;

// The gate/door open check's own notify decision - deliberately not AlertNotificationPolicy,
// which requires a visible person/vehicle/package/animal before it will ever notify (see its own
// comments on HasVisibleSubject). An empty gate left open is exactly the case that policy would
// silently swallow, so this is a separate, much simpler decision based on state transitions
// rather than subjects in frame.
public static class GateCheckDecision
{
    // The AlertResult.Profile value GateCheckAnalyzer sets on every synthetic result it builds.
    // Reserved: no Blue Iris alert or trigger zone ever sets this, since it's not a real prompt
    // profile key - AlertNotificationPolicy and EmailAlertAction both recognize it to skip the
    // person/vehicle/package/animal subject requirement that doesn't apply to a state check.
    public const string ProfileName = "gate-check";

    // "unknown" readings never notify and are never treated as a state change - a single bad
    // frame (glare, something briefly blocking the view, a dark frame) shouldn't erase the
    // memory of a real open/closed reading or cause a notification either way. Only a confident
    // open/closed reading counts as a transition.
    public static bool IsDefiniteState(string state) =>
        !string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase);

    // previousState/newState are "open"/"closed"/"unknown". Closing (or staying closed) never
    // notifies - the safety concern this feature exists for is a gate left open unnoticed, not
    // being told it closed. Staying open only re-notifies if a reminder interval is configured
    // and has actually elapsed since the last notification.
    public static bool ShouldNotifyOpen(
        string previousState,
        string newState,
        DateTimeOffset? lastNotifiedAt,
        int? remindIfStillOpenAfterHours,
        DateTimeOffset now)
    {
        if (!IsDefiniteState(newState)) return false;

        var isOpen = string.Equals(newState, "open", StringComparison.OrdinalIgnoreCase);
        if (!isOpen) return false;

        var wasOpen = string.Equals(previousState, "open", StringComparison.OrdinalIgnoreCase);
        if (!wasOpen) return true;

        if (remindIfStillOpenAfterHours is not { } hours) return false;
        if (lastNotifiedAt is null) return true;
        return now - lastNotifiedAt.Value >= TimeSpan.FromHours(hours);
    }

    // Only the bad transition (unblocked -> blocked) notifies, same asymmetry as the door state:
    // being told the camera is now covered matters; being told it's clear again doesn't need to
    // interrupt anyone. No repeat reminder while it stays blocked - one notification is enough to
    // prompt someone to go check it.
    public static bool ShouldNotifyBlocked(bool previousBlocked, bool newBlocked) =>
        !previousBlocked && newBlocked;
}
