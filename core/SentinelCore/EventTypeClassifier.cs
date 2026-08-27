namespace SentinelCore;

// Keeps event_type honest, because as returned by the model it is very nearly useless.
//
// Measured on this install's events.db across the 566 alerts the model recommended notifying on:
// 393 were "none", 170 "unknown", 2 "vehicle_at_gate", 1 "gate_open". So 99.4% of the events worth
// waking someone for carried no usable classification, and not one was ever "person_at_gate"
// despite many real person events. "gate_open" is not even in the allowed list - the model invented
// it and nothing rejected it, because ValidateAnalysis only ever checked event_type was non-empty.
//
// Three things consume this field and were all quietly degraded: the Feed's event-type filter
// (its dropdown is built from the distinct values actually in the database, so picking "Vehicle At
// Gate" returned 2 rows out of 566), automation rules via the event_type template value, and the
// Telegram chat assistant, which reasons over an event history that said "none" for nearly
// everything.
//
// The fix is not to reject a bad value: a JsonException from the parse path triggers a full retry,
// and one analysis costs ~157s on this hardware, so failing an alert over a taxonomy slip would
// trade a wrong label for no notification at all. Instead the label is derived from the structured
// fields - person.location, vehicle.location, package.status, animal.visible - which are the same
// fields AlertNotificationPolicy already trusts to decide whether to notify at all. If they are
// good enough to wake someone up, they are good enough to name what happened.
public static class EventTypeClassifier
{
    // The values the prompt actually offers the model (see OllamaVisionClient's schema block).
    public static readonly string[] Allowed =
    [
        "none", "vehicle_on_road", "vehicle_at_gate", "vehicle_entering_property",
        "vehicle_inside_property", "person_at_gate", "person_outside_vehicle",
        "person_on_property", "delivery_in_progress", "package_delivered", "animal", "unknown"
    ];

    public static bool IsAllowed(string? eventType) =>
        eventType is not null && Allowed.Contains(eventType, StringComparer.OrdinalIgnoreCase);

    // What the model said, corrected where the rest of its own answer contradicts it.
    //
    // Only two cases are overridden, so a model that classified well keeps its answer: a value
    // outside the allowed list, and a "none"/"unknown" placeholder sitting on top of structured
    // fields that plainly describe a subject. Anything else is left exactly as returned.
    public static string Normalize(VisionAnalysis analysis)
    {
        var reported = analysis.EventType;
        var derived = Derive(analysis);

        if (!IsAllowed(reported)) return derived ?? "unknown";

        var isPlaceholder = Equals(reported, "none") || Equals(reported, "unknown");
        return isPlaceholder && derived is not null ? derived : reported;
    }

    // The event type the structured fields support, or null when they support none.
    //
    // Null rather than a guess is the point: FastLocalVisionClient deliberately reports
    // location "unknown" because a class label cannot know where a subject is relative to the
    // gate, and inventing "person_at_gate" from that would be worse than the placeholder it
    // replaced. A subject with no location stays unclassified.
    public static string? Derive(VisionAnalysis analysis)
    {
        // A delivery is the whole event when it is happening, regardless of who else is in frame.
        if (Equals(analysis.Package.Status, "being_delivered")) return "delivery_in_progress";
        if (Equals(analysis.Package.Status, "delivered")) return "package_delivered";

        // People outrank vehicles: on a gate camera the person is the higher-stakes subject, and
        // the notification policy's suppressions are written about them.
        if (analysis.Person.Visible)
        {
            // More specific than either location - someone who has got out of a vehicle is a
            // different event from someone merely standing in the same place.
            if (analysis.Person.OutsideVehicle && analysis.Vehicle.Visible) return "person_outside_vehicle";

            var person = PersonEventFor(analysis.Person.Location);
            if (person is not null) return person;
        }

        if (analysis.Vehicle.Visible)
        {
            var vehicle = VehicleEventFor(analysis.Vehicle.Location);
            if (vehicle is not null) return vehicle;
        }

        if (analysis.Animal.Visible) return "animal";

        // Nothing in frame at all is a real classification, not an absent one - but only when the
        // model itself found no subject, so an empty frame keeps saying "none".
        return AlertNotificationPolicy.HasVisibleSubject(analysis) ? null : "none";
    }

    private static string? PersonEventFor(string location) => location switch
    {
        _ when Equals(location, "at_gate") || Equals(location, "outside_gate") => "person_at_gate",
        _ when Equals(location, "inside_driveway") || Equals(location, "inside_property") => "person_on_property",
        // public_road, near_vehicle and unknown have no honest counterpart in the allowed list -
        // there is no "person_on_road" - so they stay unclassified rather than being rounded to
        // the nearest gate-relative label.
        _ => null,
    };

    private static string? VehicleEventFor(string location) => location switch
    {
        _ when Equals(location, "public_road") || Equals(location, "passing_gate") => "vehicle_on_road",
        _ when Equals(location, "outside_gate_entrance") => "vehicle_at_gate",
        // Deliberately "entering" rather than "inside": AlertNotificationPolicy notes that a single
        // still frame of a fresh arrival is indistinguishable from one already parked, which is why
        // plain inside_driveway still notifies. Only the settled resting spot reads as inside.
        _ when Equals(location, "inside_driveway") => "vehicle_entering_property",
        _ when Equals(location, "parked_inside_property") => "vehicle_inside_property",
        _ => null,
    };

    private static bool Equals(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
