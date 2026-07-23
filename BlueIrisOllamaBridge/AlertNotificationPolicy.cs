namespace BlueIrisOllamaBridge;

public static class AlertNotificationPolicy
{
    public static bool ShouldNotify(AlertResult result)
    {
        var analysis = result.Analysis;
        if (analysis?.NotificationRecommended != true)
            return false;

        if (!string.Equals(result.Profile, "gate", StringComparison.OrdinalIgnoreCase) ||
            !EqualsValue(analysis.Gate.State, "closed"))
        {
            return true;
        }

        // With the gate closed, a person or vehicle already on the camera/inside side is
        // expected and not alertable by itself. A person or vehicle arriving from outside,
        // an active delivery, a new package, or an animal remains independently alertable.
        var personOnlyInside = !analysis.Person.Visible || IsInside(analysis.Person.Location);

        var vehicleArriving =
            EqualsValue(analysis.Vehicle.Activity, "entering") ||
            EqualsValue(analysis.EventType, "vehicle_entering_property");
        var vehicleOnlyInside = !analysis.Vehicle.Visible ||
            (!vehicleArriving && IsInside(analysis.Vehicle.Location));

        if (!personOnlyInside || !vehicleOnlyInside)
            return true;

        var otherAlertSubject =
            EqualsValue(analysis.Package.Status, "being_delivered") ||
            EqualsValue(analysis.Package.Status, "delivered") ||
            analysis.Animal.Visible;

        return otherAlertSubject;
    }

    private static bool IsInside(string location) =>
        EqualsValue(location, "inside_driveway") ||
        EqualsValue(location, "inside_property") ||
        EqualsValue(location, "parked_inside_property");

    private static bool EqualsValue(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
