namespace BlueIrisOllamaBridge;

public static class AlertNotificationPolicy
{
    public static bool ShouldNotify(AlertResult result)
    {
        var analysis = result.Analysis;
        if (analysis?.NotificationRecommended != true)
            return false;

        if (!string.Equals(result.Profile, "gate", StringComparison.OrdinalIgnoreCase) ||
            !EqualsValue(analysis.Gate.State, "closed") ||
            !analysis.Person.Visible ||
            !IsInside(analysis.Person.Location))
        {
            return true;
        }

        // Suppress only a person-only event. An outside vehicle, active delivery,
        // new package, or animal in the same frame remains independently alertable.
        var otherAlertSubject =
            (analysis.Vehicle.Visible &&
             (EqualsValue(analysis.Vehicle.Location, "outside_gate_entrance") ||
              EqualsValue(analysis.Vehicle.Activity, "entering"))) ||
            EqualsValue(analysis.Package.Status, "being_delivered") ||
            EqualsValue(analysis.Package.Status, "delivered") ||
            analysis.Animal.Visible;

        return otherAlertSubject;
    }

    private static bool IsInside(string location) =>
        EqualsValue(location, "inside_driveway") ||
        EqualsValue(location, "inside_property");

    private static bool EqualsValue(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
