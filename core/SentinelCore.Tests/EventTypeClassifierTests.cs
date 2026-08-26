using SentinelCore;
using Xunit;

namespace SentinelCore.Tests;

// event_type was effectively dead data: of the 566 alerts on this install that the model
// recommended notifying about, 393 came back "none" and 170 "unknown". These tests pin the two
// real alerts observed on the hardware on 2026-08-25 - a truck at the gate and a person inside it -
// plus the one out-of-vocabulary value the old non-empty check let through.
public sealed class EventTypeClassifierTests
{
    private static VisionAnalysis Analysis(
        string eventType,
        PersonAnalysis? person = null,
        VehicleAnalysis? vehicle = null,
        PackageAnalysis? package = null,
        AnimalAnalysis? animal = null,
        bool notify = false) => new()
        {
            EventType = eventType,
            Summary = "irrelevant to classification",
            Person = person ?? new PersonAnalysis(),
            Vehicle = vehicle ?? new VehicleAnalysis(),
            Package = package ?? new PackageAnalysis(),
            Animal = animal ?? new AnimalAnalysis(),
            NotificationRecommended = notify,
        };

    // The real one: a gray pickup in the gate entrance that emailed "Vehicle is here at your gate",
    // had notification_recommended true - and stored event_type "none".
    [Fact]
    public void TheTruckAtTheGate_IsNoLongerFiledAsNothing()
    {
        var analysis = Analysis(
            "none",
            vehicle: new VehicleAnalysis { Visible = true, Location = "outside_gate_entrance", Activity = "parked" },
            notify: true);

        Assert.Equal("vehicle_at_gate", EventTypeClassifier.Normalize(analysis));
    }

    // The other real one: a person on the camera side of a closed gate. Correctly not notified,
    // but it is still a person on the property and the Feed should be able to find it.
    [Fact]
    public void ThePersonInsideTheGate_IsClassifiedEvenThoughItDoesNotNotify()
    {
        var analysis = Analysis(
            "none",
            person: new PersonAnalysis { Visible = true, Location = "inside_driveway", Activity = "standing" });

        Assert.Equal("person_on_property", EventTypeClassifier.Normalize(analysis));
    }

    // "gate_open" is not in the allowed list. The old check only required non-empty, so it was
    // stored as-is and then offered as a filter option in the dashboard.
    [Fact]
    public void AnInventedValue_IsReplacedRatherThanStored()
    {
        var analysis = Analysis(
            "gate_open",
            person: new PersonAnalysis { Visible = true, Location = "outside_gate" });

        var normalized = EventTypeClassifier.Normalize(analysis);

        Assert.Equal("person_at_gate", normalized);
        Assert.True(EventTypeClassifier.IsAllowed(normalized));
    }

    [Fact]
    public void AnInventedValueWithNothingToDeriveFrom_FallsBackToUnknown() =>
        Assert.Equal("unknown", EventTypeClassifier.Normalize(Analysis("gate_open",
            person: new PersonAnalysis { Visible = true, Location = "unknown" })));

    // A model that classified well is left alone - this corrects, it does not overwrite.
    [Theory]
    [InlineData("person_at_gate")]
    [InlineData("delivery_in_progress")]
    [InlineData("vehicle_inside_property")]
    public void AConfidentAnswer_IsKept(string reported) =>
        Assert.Equal(reported, EventTypeClassifier.Normalize(Analysis(reported,
            person: new PersonAnalysis { Visible = true, Location = "inside_driveway" })));

    // FastLocal cannot know where a subject is - it has a class label and nothing else - and says
    // so with location "unknown". Guessing a gate-relative label from that would be worse than the
    // placeholder, so the placeholder stands.
    [Fact]
    public void AFastLocalDetectionWithNoLocation_StaysUnknown()
    {
        var analysis = Analysis(
            "unknown",
            person: new PersonAnalysis { Visible = true, Location = "unknown", Activity = "unknown" });

        Assert.Equal("unknown", EventTypeClassifier.Normalize(analysis));
        Assert.Null(EventTypeClassifier.Derive(analysis));
    }

    // There is no "person_on_road" in the allowed list, so a person on the public road must not be
    // rounded to the nearest gate-relative label.
    [Fact]
    public void APersonOnTheRoad_IsNotRoundedToAtTheGate() =>
        Assert.Null(EventTypeClassifier.Derive(Analysis("none",
            person: new PersonAnalysis { Visible = true, Location = "public_road" })));

    [Fact]
    public void AnEmptyFrame_StaysNone() =>
        Assert.Equal("none", EventTypeClassifier.Normalize(Analysis("none")));

    [Fact]
    public void ADeliveryOutranksWhoeverElseIsInFrame() =>
        Assert.Equal("delivery_in_progress", EventTypeClassifier.Normalize(Analysis("none",
            person: new PersonAnalysis { Visible = true, Location = "at_gate" },
            package: new PackageAnalysis { Visible = true, Status = "being_delivered" })));

    [Fact]
    public void SomeoneWhoGotOutOfAVehicle_IsItsOwnEvent() =>
        Assert.Equal("person_outside_vehicle", EventTypeClassifier.Normalize(Analysis("none",
            person: new PersonAnalysis { Visible = true, Location = "at_gate", OutsideVehicle = true },
            vehicle: new VehicleAnalysis { Visible = true, Location = "outside_gate_entrance" })));

    // A fresh arrival and an already-parked car look identical in one still frame, which is why
    // AlertNotificationPolicy still notifies on plain inside_driveway. The label has to agree.
    [Theory]
    [InlineData("inside_driveway", "vehicle_entering_property")]
    [InlineData("parked_inside_property", "vehicle_inside_property")]
    [InlineData("public_road", "vehicle_on_road")]
    [InlineData("passing_gate", "vehicle_on_road")]
    public void VehicleLocationsMapToTheirEvent(string location, string expected) =>
        Assert.Equal(expected, EventTypeClassifier.Normalize(Analysis("none",
            vehicle: new VehicleAnalysis { Visible = true, Location = location })));

    [Fact]
    public void AnAnimalIsClassifiedWhenNothingElseIs() =>
        Assert.Equal("animal", EventTypeClassifier.Normalize(Analysis("none",
            animal: new AnimalAnalysis { Visible = true })));

    // Whatever comes out is always something the dashboard filter and automation rules can rely on.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GATE_OPEN")]
    [InlineData("person at gate")]
    public void NormalizeAlwaysReturnsAnAllowedValue(string reported) =>
        Assert.True(EventTypeClassifier.IsAllowed(EventTypeClassifier.Normalize(Analysis(reported))));
}
