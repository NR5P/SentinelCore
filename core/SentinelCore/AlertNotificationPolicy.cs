namespace SentinelCore;

public static class AlertNotificationPolicy
{
    public static bool ShouldNotify(AlertResult result)
    {
        var analysis = result.Analysis;
        if (analysis?.NotificationRecommended != true)
            return false;

        // Nothing in the frame means nothing to alert about, whatever the model recommended.
        //
        // This is deliberately the first check and deliberately profile-independent. The
        // gate-specific suppression further down only runs when gate.state is "closed", and an
        // unreadable frame - a spiderweb across the lens catching the IR illuminator, say -
        // makes the model answer "unknown", which skipped straight past it to notify. Two real
        // alerts arrived that way, both with person/vehicle/package/animal all absent and the
        // model asserting a person at 0.3 confidence in the summary alone.
        //
        // Trusting the model's own boolean flags rather than its prose is the point: the flags
        // were right in every observed case, and it was the free-text summary that invented a
        // person.
        if (!HasVisibleSubject(analysis))
            return false;

        if (!string.Equals(result.Profile, "gate", StringComparison.OrdinalIgnoreCase) ||
            !EqualsValue(analysis.Gate.State, "closed"))
        {
            return true;
        }

        // With the gate closed, a person clearly on the camera/inside side is expected and
        // not alertable by itself. A person outside, an active delivery, a new package, or
        // an animal remains independently alertable.
        //
        // Vehicles are judged more leniently: a single still frame captured mid-arrival
        // almost always looks the same as "already inside" (the prompt tells the model to
        // default activity to "unknown" rather than guess "entering" from one frame), so
        // requiring a confident "entering" label here caused real arrivals to be silently
        // dropped. Only a vehicle in its clearly-settled resting spot (parked_inside_property)
        // is suppressed; anything else - including plain inside_driveway, which is what a
        // fresh arrival looks like in a single frame - still notifies.
        var personOnlyInside = !analysis.Person.Visible || IsInside(analysis.Person.Location);
        var vehicleSettled = analysis.Vehicle.Visible &&
            EqualsValue(analysis.Vehicle.Location, "parked_inside_property");
        var vehicleOnlyInside = !analysis.Vehicle.Visible || vehicleSettled;

        if (!personOnlyInside || !vehicleOnlyInside)
            return true;

        var otherAlertSubject =
            EqualsValue(analysis.Package.Status, "being_delivered") ||
            EqualsValue(analysis.Package.Status, "delivered") ||
            analysis.Animal.Visible;

        return otherAlertSubject;
    }

    // A package can be the whole event without anything else in frame - a parcel left at the gate
    // and the courier already gone - so its status counts as a subject in its own right.
    private static bool HasVisibleSubject(VisionAnalysis analysis) =>
        analysis.Person.Visible ||
        analysis.Vehicle.Visible ||
        analysis.Package.Visible ||
        analysis.Animal.Visible ||
        EqualsValue(analysis.Package.Status, "being_delivered") ||
        EqualsValue(analysis.Package.Status, "delivered") ||
        EqualsValue(analysis.Package.Status, "already_present");

    private static bool IsInside(string location) =>
        EqualsValue(location, "inside_driveway") ||
        EqualsValue(location, "inside_property");

    private static bool EqualsValue(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
